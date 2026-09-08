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
    /// </summary>
    public static class LaneLayoutService
    {
        private sealed class Lane
        {
            public string Name;
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
        }

        /// <param name="spacingMin">Minimum pitch between tags (internal units); the text size may raise it.</param>
        /// <param name="shift">Leader shoulder length (internal units).</param>
        public static List<LaneResult> AutoLanes(Document doc, View view, IList<IndependentTag> tags, double spacingMin, double shift)
        {
            XYZ up = TagOrderingService.GetViewUp(view);
            XYZ right = TagOrderingService.GetViewRight(view, up);
            XYZ viewDir = TagOrderingService.GetViewDirection(view, up, right);
            shift = Math.Abs(shift);

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

                items.Add(new Item { Tag = tag, Anchor = anchor, S = anchor.DotProduct(right), A = anchor.DotProduct(up) });
            }

            var results = new List<LaneResult>();
            if (items.Count == 0)
            {
                return results;
            }

            // Text sizes (model units) drive the pitches and the margins.
            double scale = view.Scale > 0 ? view.Scale : 100;
            double textHeight = 0.0082 * scale;            // 2.5 mm text at this scale
            double maxWidth = 8 * 0.55 * textHeight / 0.716;
            double maxHeight = textHeight;
            foreach (Item item in items)
            {
                if (TagOrderingService.TryMeasureTextBlock(doc, view, item.Tag, out double w, out double h))
                {
                    maxWidth = Math.Max(maxWidth, w);
                    maxHeight = Math.Max(maxHeight, h);
                }
            }

            double gap = 0.6 * textHeight;
            double colPitch = Math.Max(spacingMin, maxHeight + gap);
            double rowPitch = Math.Max(spacingMin, maxWidth + gap);
            double clearance = shift + 2 * textHeight;    // between the elements' extents and the first lane line

            double minS = items.Min(i => i.S), maxS = items.Max(i => i.S);
            double minA = items.Min(i => i.A), maxA = items.Max(i => i.A);
            double depth = items.Average(i => i.Anchor.DotProduct(viewDir));
            Func<double, double, XYZ> P = (s, a) => right * s + up * a + viewDir * depth;

            // Perimeter rings until the capacity covers every tag (staggered rows/columns on outer rings).
            var lanes = new List<Lane>();
            int ring = 0;
            while (lanes.Sum(l => l.Capacity) < items.Count && ring < 6)
            {
                double colOffset = clearance + ring * (maxWidth + 2 * textHeight + shift);
                double rowOffset = clearance + ring * (maxHeight + 2 * textHeight + shift);
                double stagger = ring % 2 == 1 ? 0.5 : 0.0;
                double colSpan = (maxA - minA) + 2 * (rowOffset - clearance) + 2 * textHeight;
                double rowSpan = (maxS - minS) + 2 * (colOffset - clearance) + 2 * textHeight;
                double colTop = maxA + (rowOffset - clearance) + textHeight - stagger * colPitch;
                double rowLeft = minS - (colOffset - clearance) - textHeight + stagger * rowPitch;
                string suffix = ring == 0 ? "" : " " + (ring + 1);

                lanes.Add(MakeLane("Left column" + suffix, P(minS - colOffset, colTop), up.Negate(), null, colSpan, colPitch));
                lanes.Add(MakeLane("Right column" + suffix, P(maxS + colOffset, colTop), up.Negate(), null, colSpan, colPitch));
                lanes.Add(MakeLane("Top row" + suffix, P(rowLeft, maxA + rowOffset), right, right, rowSpan, rowPitch));
                lanes.Add(MakeLane("Bottom row" + suffix, P(rowLeft, minA - rowOffset), right, right, rowSpan, rowPitch));
                ring++;
            }

            // Nearest lane with capacity, closest elements first.
            foreach (Item item in items.OrderBy(i => lanes.Min(l => DistanceToLane(l, i.Anchor))))
            {
                Lane target = lanes.OrderBy(l => DistanceToLane(l, item.Anchor)).FirstOrDefault(l => l.Tags.Count < l.Capacity);
                if (target == null)
                {
                    target = lanes.OrderBy(l => DistanceToLane(l, item.Anchor)).First();
                }

                target.Tags.Add(item.Tag);
            }

            foreach (Lane lane in lanes.Where(l => l.Tags.Count > 0))
            {
                var result = new LaneResult
                {
                    Name = lane.Name,
                    Tags = lane.Tags,
                    Origin = lane.Start,
                    Direction = lane.ColumnDir,
                    Pitch = lane.Pitch,
                    Capacity = lane.Capacity,
                };
                result.Result = TagOrderingService.PlaceColumn(doc, view, lane.Tags, lane.Start, lane.ColumnDir, lane.Pitch, shift);
                results.Add(result);
            }

            return results;
        }

        private static Lane MakeLane(string name, XYZ start, XYZ dir, XYZ columnDir, double length, double pitch)
        {
            return new Lane
            {
                Name = name,
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
