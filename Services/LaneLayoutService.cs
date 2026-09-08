using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSetTags.Services
{
    /// <summary>
    /// One perimeter lane filled by <see cref="LaneLayoutService.AutoLanes"/>.
    /// </summary>
    public sealed class LaneResult
    {
        public string Name;
        public string Family;      // tag family shown in this lane (lane-per-family option), else null
        public List<IndependentTag> Tags = new List<IndependentTag>();
        public XYZ Origin;
        public XYZ Direction;      // null = straight down the view (column); otherwise a row direction
        public double Pitch;
        public int Capacity;
        public ColumnResult Result;
    }

    /// <summary>
    /// "Auto lanes": lays a set of tags out in columns and rows around the
    /// footprint of their elements, so the plan itself stays clean and every
    /// tag points back with a leader. Lanes are derived from the elements'
    /// extents in the view; their pitch comes from the real text sizes of the
    /// tag families (via <see cref="TagOrderingService"/> metrics); each lane is
    /// then placed with <see cref="TagOrderingService.PlaceColumn"/>, so it gets
    /// the same level shoulders, kept leader ends and crossing-free order as a
    /// manually picked column.
    ///
    /// Elements are assigned to the side of their bearing from the centroid
    /// (right / top / left / bottom), so the leaders of different sides fan
    /// outwards and do not cross. With the lane-per-family option the families
    /// follow each other along the same line of every side, separated by a gap,
    /// so each run of text shows one kind of tag and no leader has to cross
    /// another family's texts.
    /// </summary>
    public static class LaneLayoutService
    {
        private static readonly string[] Sides = { "Right", "Top", "Left", "Bottom" };
        private const int MaxRingsPerSide = 8;

        private sealed class Lane
        {
            public string Name;
            public string Side;
            public int Ring;
            public string Family;
            public XYZ Start;          // world point on the lane line where the first tag goes
            public XYZ Dir;            // unit vector along the lane (world)
            public XYZ ColumnDir;      // passed to PlaceColumn (null = down)
            public double Length;
            public double Pitch;
            public int Capacity;
            public List<IndependentTag> Tags = new List<IndependentTag>();
        }

        private sealed class Item
        {
            public IndependentTag Tag;
            public XYZ Anchor;
            public double S;
            public double A;
            public string Family;
            public string Side;
        }

        private sealed class Frame
        {
            public XYZ Right, Up, ViewDir;
            public double MinS, MaxS, MinA, MaxA, Depth;
            public double TextHeight, MaxWidth, MaxHeight, Clearance, Shift;

            public XYZ P(double s, double a)
            {
                return Right * s + Up * a + ViewDir * Depth;
            }
        }

        /// <param name="spacingMin">Minimum pitch between tags (internal units); the text size may raise it.</param>
        /// <param name="shift">Leader shoulder length (internal units).</param>
        /// <param name="lanePerFamily">True: on every side, the families follow each other along the line, one run per family.</param>
        public static List<LaneResult> AutoLanes(Document doc, View view, IList<IndependentTag> tags, double spacingMin, double shift, bool lanePerFamily)
        {
            var frame = new Frame
            {
                Up = TagOrderingService.GetViewUp(view),
                Shift = Math.Abs(shift),
            };
            frame.Right = TagOrderingService.GetViewRight(view, frame.Up);
            frame.ViewDir = TagOrderingService.GetViewDirection(view, frame.Up, frame.Right);

            var items = new List<Item>();
            foreach (IndependentTag tag in tags)
            {
                if (tag == null || !tag.IsValidObject)
                {
                    continue;
                }

                XYZ anchor = TagOrderingService.GetAnchor(tag);
                if (anchor == null)
                {
                    continue;
                }

                string family = "?";
                try
                {
                    family = (doc.GetElement(tag.GetTypeId()) as FamilySymbol)?.Family?.Name ?? "?";
                }
                catch
                {
                    // Keep "?".
                }

                items.Add(new Item { Tag = tag, Anchor = anchor, S = anchor.DotProduct(frame.Right), A = anchor.DotProduct(frame.Up), Family = family });
            }

            var results = new List<LaneResult>();
            if (items.Count == 0)
            {
                return results;
            }

            // Text sizes (model units) drive the pitches and the margins.
            double scale = view.Scale > 0 ? view.Scale : 100;
            frame.TextHeight = 0.0082 * scale;            // 2.5 mm text at this scale
            frame.MaxWidth = 8 * 0.55 * frame.TextHeight / 0.716;
            frame.MaxHeight = frame.TextHeight;
            var familyWidth = new Dictionary<string, double>();
            var familyHeight = new Dictionary<string, double>();
            foreach (Item item in items)
            {
                if (TagOrderingService.TryMeasureTextBlock(doc, view, item.Tag, out double w, out double h))
                {
                    frame.MaxWidth = Math.Max(frame.MaxWidth, w);
                    frame.MaxHeight = Math.Max(frame.MaxHeight, h);
                    familyWidth[item.Family] = Math.Max(familyWidth.TryGetValue(item.Family, out double fw) ? fw : 0, w);
                    familyHeight[item.Family] = Math.Max(familyHeight.TryGetValue(item.Family, out double fh) ? fh : 0, h);
                }
            }

            frame.Clearance = frame.Shift + 2 * frame.TextHeight;    // between the elements' extents and the first lane line
            frame.MinS = items.Min(i => i.S);
            frame.MaxS = items.Max(i => i.S);
            frame.MinA = items.Min(i => i.A);
            frame.MaxA = items.Max(i => i.A);
            frame.Depth = items.Average(i => i.Anchor.DotProduct(frame.ViewDir));
            double gap = 0.6 * frame.TextHeight;

            // Side by bearing from the centroid: leaders of different sides fan outwards.
            double cS = items.Average(i => i.S), cA = items.Average(i => i.A);
            foreach (Item item in items)
            {
                double angle = Math.Atan2(item.A - cA, item.S - cS) * 180 / Math.PI;
                item.Side = angle > -45 && angle <= 45 ? "Right" : angle > 45 && angle <= 135 ? "Top" : angle > -135 && angle <= -45 ? "Bottom" : "Left";
            }

            var lanes = new List<Lane>();
            foreach (string side in Sides)
            {
                List<Item> sideItems = items.Where(i => i.Side == side).ToList();
                if (sideItems.Count == 0)
                {
                    continue;
                }

                double colPitch = Math.Max(spacingMin, frame.MaxHeight + gap);
                double rowPitch = Math.Max(spacingMin, frame.MaxWidth + gap);
                bool isColumn = side == "Left" || side == "Right";

                if (!lanePerFamily)
                {
                    // Rings on this side until the capacity covers its elements; inner ring first.
                    var sideLanes = new List<Lane>();
                    int ring = 0;
                    while (sideLanes.Sum(l => l.Capacity) < sideItems.Count && ring < MaxRingsPerSide)
                    {
                        sideLanes.Add(MakeSideLane(frame, side, ring++, colPitch, rowPitch));
                    }

                    foreach (Item item in sideItems.OrderBy(i => sideLanes.Min(l => DistanceToLane(l, i.Anchor))))
                    {
                        Lane target = sideLanes.OrderBy(l => l.Ring).FirstOrDefault(l => l.Tags.Count < l.Capacity) ?? sideLanes.Last();
                        target.Tags.Add(item.Tag);
                    }

                    lanes.AddRange(sideLanes);
                    continue;
                }

                // Lane per family: the families follow each other along the same line, one
                // pitch of gap between them, ordered like their elements along the side so the
                // segments do not cross each other. A new ring is opened only when the line overflows.
                var families = sideItems.GroupBy(i => i.Family)
                    .Select(g => new { Family = g.Key, Items = g.ToList(), Along = isColumn ? -g.Average(i => i.A) : g.Average(i => i.S) })
                    .OrderBy(g => g.Along)
                    .ToList();

                int segRing = 0;
                Lane line = MakeSideLane(frame, side, segRing, colPitch, rowPitch);
                double cursor = 0;
                double previousPitch = 0;
                foreach (var family in families)
                {
                    double fw = familyWidth.TryGetValue(family.Family, out double w0) ? w0 : frame.MaxWidth;
                    double fh = familyHeight.TryGetValue(family.Family, out double h0) ? h0 : frame.MaxHeight;
                    double pitch = isColumn ? Math.Max(spacingMin, fh + gap) : Math.Max(spacingMin, fw + gap);
                    double segmentLength = (family.Items.Count - 1) * pitch;

                    if (cursor > 0)
                    {
                        cursor += Math.Max(previousPitch, pitch); // visible break between families
                    }

                    if (cursor + segmentLength > line.Length * 1.5 && cursor > 0 && segRing < MaxRingsPerSide - 1)
                    {
                        line = MakeSideLane(frame, side, ++segRing, colPitch, rowPitch);
                        cursor = 0;
                    }

                    var segment = new Lane
                    {
                        Name = line.Name + " [" + family.Family + "]",
                        Side = side,
                        Ring = segRing,
                        Family = family.Family,
                        Start = line.Start + line.Dir * cursor,
                        Dir = line.Dir,
                        ColumnDir = line.ColumnDir,
                        Length = Math.Max(0, line.Length - cursor),
                        Pitch = pitch,
                        Capacity = family.Items.Count,
                    };
                    segment.Tags.AddRange(family.Items.Select(i => i.Tag));
                    lanes.Add(segment);

                    cursor += segmentLength;
                    previousPitch = pitch;
                }
            }

            foreach (Lane lane in lanes.Where(l => l.Tags.Count > 0))
            {
                var result = new LaneResult
                {
                    Name = lane.Name,
                    Family = lane.Family,
                    Tags = lane.Tags,
                    Origin = lane.Start,
                    Direction = lane.ColumnDir,
                    Pitch = lane.Pitch,
                    Capacity = lane.Capacity,
                };
                result.Result = TagOrderingService.PlaceColumn(doc, view, lane.Tags, lane.Start, lane.ColumnDir, lane.Pitch, frame.Shift);
                results.Add(result);
            }

            return results;
        }

        /// <summary>A column (Left/Right) or row (Top/Bottom) lane on the given ring outside the elements.</summary>
        private static Lane MakeSideLane(Frame f, string side, int ring, double colPitch, double rowPitch)
        {
            double colOffset = f.Clearance + ring * (f.MaxWidth + 2 * f.TextHeight + f.Shift);
            double rowOffset = f.Clearance + ring * (f.MaxHeight + 2 * f.TextHeight + f.Shift);
            double stagger = ring % 2 == 1 ? 0.5 : 0.0;
            double colSpan = (f.MaxA - f.MinA) + 2 * f.TextHeight;
            double rowSpan = (f.MaxS - f.MinS) + 2 * f.TextHeight;
            double colTop = f.MaxA + f.TextHeight - stagger * colPitch;
            double rowLeft = f.MinS - f.TextHeight + stagger * rowPitch;
            string suffix = ring == 0 ? "" : " " + (ring + 1);

            switch (side)
            {
                case "Left":
                    return MakeLane("Left column" + suffix, side, ring, f.P(f.MinS - colOffset, colTop), f.Up.Negate(), null, colSpan, colPitch);
                case "Right":
                    return MakeLane("Right column" + suffix, side, ring, f.P(f.MaxS + colOffset, colTop), f.Up.Negate(), null, colSpan, colPitch);
                case "Top":
                    return MakeLane("Top row" + suffix, side, ring, f.P(rowLeft, f.MaxA + rowOffset), f.Right, f.Right, rowSpan, rowPitch);
                default:
                    return MakeLane("Bottom row" + suffix, side, ring, f.P(rowLeft, f.MinA - rowOffset), f.Right, f.Right, rowSpan, rowPitch);
            }
        }

        private static Lane MakeLane(string name, string side, int ring, XYZ start, XYZ dir, XYZ columnDir, double length, double pitch)
        {
            return new Lane
            {
                Name = name,
                Side = side,
                Ring = ring,
                Start = start,
                Dir = dir.Normalize(),
                ColumnDir = columnDir,
                Length = Math.Max(0, length),
                Pitch = pitch,
                Capacity = Math.Max(1, (int)Math.Floor(Math.Max(0, length) / pitch) + 1),
            };
        }

        private static double DistanceToLane(Lane lane, XYZ point)
        {
            double t = Math.Max(0, Math.Min(lane.Length, (point - lane.Start).DotProduct(lane.Dir)));
            XYZ nearest = lane.Start + lane.Dir * t;
            return nearest.DistanceTo(point);
        }
    }
}
