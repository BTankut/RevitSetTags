using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSetTags.Services
{
    /// <summary>
    /// One lane filled by <see cref="LaneLayoutService.AutoLanes"/>.
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
    /// "Auto lanes": lays a set of tags out in columns and rows around their
    /// elements so the drawing stays clean and every tag points back with a leader.
    ///
    /// Plan and section views: the lanes follow the outline of what is drawn. The
    /// view region (crop box, or the model extents) is rasterised, model elements
    /// are marked, enclosed interiors are filled, and every straight outline edge
    /// (top, bottom, left, right faces, notches included) becomes a lane offset
    /// outwards into free space; outer rings are added when an edge overflows.
    /// Elements go to the nearest lane with room; each lane's run is centred on
    /// its elements. The crop box is enlarged when a lane would fall outside it.
    ///
    /// 3D views: a rectangle of perimeter rings around the elements' extents.
    ///
    /// Every lane is placed with <see cref="TagOrderingService.PlaceColumn"/>,
    /// so it gets the same level shoulders, kept leader ends and crossing-free
    /// order as a manually picked column. With the lane-per-family option the
    /// families of a lane follow each other along it, one run per family.
    /// </summary>
    public static class LaneLayoutService
    {
        private static readonly string[] Sides = { "Left", "Right", "Top", "Bottom" };

        /// <summary>Diagnostics of the last outline analysis (region, edges, rings, lanes); for support only.</summary>
        public static string LastLog = "";
        private const int MaxRings = 6;
        private const int OutlineRings = 4;

        private sealed class Lane
        {
            public string Name;
            public string Side;
            public int Ring;
            public string Family;
            public XYZ Start;          // world point on the lane line where the first slot is
            public XYZ Dir;            // unit vector along the lane (world)
            public XYZ ColumnDir;      // passed to PlaceColumn (null = down)
            public bool Horizontal;
            public bool OrderAlongAxis;   // rows hugging the outline: order by position along the lane
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
        }

        /// <summary>One tag position on a lane.</summary>
        private sealed class Slot
        {
            public Lane Lane;
            public int Index;
            public XYZ Point;
        }

        /// <summary>A stretch of an outline edge that could carry a lane (before conflicts between sides are resolved).</summary>
        private sealed class Candidate
        {
            public string Side;
            public bool Horizontal;
            public int Ring;
            public double Line, ZoneNear, ZoneFar, Pitch, RunLength;
            public int From, To;       // cells along the lane
        }

        private sealed class Frame
        {
            public XYZ Right, Up, ViewDir;
            public double MinS, MaxS, MinA, MaxA, Depth;
            public double TextHeight, MaxWidth, MaxHeight, Clearance, Shift;
            public Dictionary<string, double> FamilyWidth = new Dictionary<string, double>();
            public Dictionary<string, double> FamilyHeight = new Dictionary<string, double>();

            public XYZ P(double s, double a)
            {
                return Right * s + Up * a + ViewDir * Depth;
            }
        }

        /// <param name="spacingMin">Minimum pitch between tags (internal units); the text size may raise it.</param>
        /// <param name="shift">Leader shoulder length (internal units).</param>
        /// <param name="lanePerFamily">True: on every lane, the families follow each other, one run per family.</param>
        public static List<LaneResult> AutoLanes(Document doc, View view, IList<IndependentTag> tags, double spacingMin, double shift, bool lanePerFamily)
        {
            var frame = new Frame { Up = TagOrderingService.GetViewUp(view), Shift = Math.Abs(shift) };
            frame.Right = TagOrderingService.GetViewRight(view, frame.Up);
            frame.ViewDir = TagOrderingService.GetViewDirection(view, frame.Up, frame.Right);

            // Lanes assume horizontal text.
            foreach (IndependentTag tag in tags)
            {
                try
                {
                    if (tag != null && tag.IsValidObject && tag.TagOrientation != TagOrientation.Horizontal)
                    {
                        tag.TagOrientation = TagOrientation.Horizontal;
                    }
                }
                catch
                {
                    // Some tags cannot be rotated; they are measured as they are.
                }
            }

            var items = new List<Item>();
            foreach (IndependentTag tag in tags)
            {
                if (tag == null || !tag.IsValidObject)
                {
                    continue;
                }

                XYZ anchor = TagOrderingService.GetElementAnchor(tag);
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

            double scale = view.Scale > 0 ? view.Scale : 100;
            frame.TextHeight = 0.0082 * scale;            // 2.5 mm text at this scale
            frame.MaxWidth = 8 * 0.55 * frame.TextHeight / 0.716;
            frame.MaxHeight = frame.TextHeight;
            foreach (Item item in items)
            {
                if (TagOrderingService.TryMeasureTextBlock(doc, view, item.Tag, out double w, out double h))
                {
                    frame.MaxWidth = Math.Max(frame.MaxWidth, w);
                    frame.MaxHeight = Math.Max(frame.MaxHeight, h);
                    frame.FamilyWidth[item.Family] = Math.Max(frame.FamilyWidth.TryGetValue(item.Family, out double fw) ? fw : 0, w);
                    frame.FamilyHeight[item.Family] = Math.Max(frame.FamilyHeight.TryGetValue(item.Family, out double fh) ? fh : 0, h);
                }
            }

            frame.Clearance = frame.Shift + 2 * frame.TextHeight;
            frame.MinS = items.Min(i => i.S);
            frame.MaxS = items.Max(i => i.S);
            frame.MinA = items.Min(i => i.A);
            frame.MaxA = items.Max(i => i.A);
            frame.Depth = items.Average(i => i.Anchor.DotProduct(frame.ViewDir));

            List<Lane> lanes = view.ViewType == ViewType.ThreeD
                ? RectangleLanes(frame, items, spacingMin)
                : OutlineLanes(doc, view, frame, items, spacingMin, lanePerFamily);

            if (lanePerFamily && view.ViewType == ViewType.ThreeD)
            {
                lanes = SplitByFamily(lanes, items, frame, spacingMin);
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
                result.Result = TagOrderingService.PlaceColumn(doc, view, lane.Tags, lane.Start, lane.ColumnDir, lane.OrderAlongAxis ? lane.Pitch : spacingMin, frame.Shift, lane.OrderAlongAxis);
                result.Pitch = result.Result.PitchUsed;
                results.Add(result);
            }

            return results;
        }

        // ----- 3D views: rectangle of perimeter rings ----------------------------------

        private static List<Lane> RectangleLanes(Frame frame, List<Item> items, double spacingMin)
        {
            double gap = 0.6 * frame.TextHeight;          // between lines of a column
            double rowGap = 1.0 * frame.TextHeight;       // between texts side by side in a row
            double colPitch = Math.Max(spacingMin, frame.MaxHeight + gap);
            double rowPitch = Math.Max(spacingMin, frame.MaxWidth + rowGap);

            var lanes = new List<Lane>();
            int ring = 0;
            while (lanes.Sum(l => l.Capacity) < items.Count && ring < MaxRings)
            {
                foreach (string side in Sides)
                {
                    lanes.Add(MakeSideLane(frame, side, ring, colPitch, rowPitch));
                }

                ring++;
            }

            // Nearest lane with capacity, closest elements first (the original Auto lanes distribution).
            foreach (Item item in items.OrderBy(i => lanes.Min(l => DistanceToLane(l, i.Anchor))))
            {
                Lane target = lanes.OrderBy(l => DistanceToLane(l, item.Anchor)).FirstOrDefault(l => l.Tags.Count < l.Capacity)
                    ?? lanes.OrderBy(l => DistanceToLane(l, item.Anchor)).First();
                target.Tags.Add(item.Tag);
            }

            return lanes;
        }

        /// <summary>A column (Left/Right) or row (Top/Bottom) lane on the given ring outside the elements.</summary>
        private static Lane MakeSideLane(Frame f, string side, int ring, double colPitch, double rowPitch)
        {
            double colOffset = f.Clearance + ring * (f.MaxWidth + 2 * f.TextHeight + f.Shift);
            double rowOffset = f.Clearance + ring * (f.MaxHeight + 2 * f.TextHeight + f.Shift);
            double stagger = ring % 2 == 1 ? 0.5 : 0.0;
            double colSpan = (f.MaxA - f.MinA) + 2 * (rowOffset - f.Clearance) + 2 * f.TextHeight;
            double rowSpan = (f.MaxS - f.MinS) + 2 * (colOffset - f.Clearance) + 2 * f.TextHeight;
            double colTop = f.MaxA + (rowOffset - f.Clearance) + f.TextHeight - stagger * colPitch;
            double rowLeft = f.MinS - (colOffset - f.Clearance) - f.TextHeight + stagger * rowPitch;
            string suffix = ring == 0 ? "" : " " + (ring + 1);

            switch (side)
            {
                case "Left":
                    return MakeLane("Left column" + suffix, side, ring, f.P(f.MinS - colOffset, colTop), f.Up.Negate(), null, false, colSpan, colPitch);
                case "Right":
                    return MakeLane("Right column" + suffix, side, ring, f.P(f.MaxS + colOffset, colTop), f.Up.Negate(), null, false, colSpan, colPitch);
                case "Top":
                    return MakeLane("Top row" + suffix, side, ring, f.P(rowLeft, f.MaxA + rowOffset), f.Right, f.Right, true, rowSpan, rowPitch);
                default:
                    return MakeLane("Bottom row" + suffix, side, ring, f.P(rowLeft, f.MinA - rowOffset), f.Right, f.Right, true, rowSpan, rowPitch);
            }
        }

        // ----- 2D views: lanes along the outline of the drawing ------------------------

        private static readonly HashSet<long> NotObstacles = new HashSet<long>
        {
            (long)BuiltInCategory.OST_Rooms, (long)BuiltInCategory.OST_Areas, (long)BuiltInCategory.OST_MEPSpaces,
            (long)BuiltInCategory.OST_PipingSystem, (long)BuiltInCategory.OST_DuctSystem, (long)BuiltInCategory.OST_ElectricalCircuit,
            (long)BuiltInCategory.OST_Lines, (long)BuiltInCategory.OST_Levels, (long)BuiltInCategory.OST_Grids,
            (long)BuiltInCategory.OST_Cameras, (long)BuiltInCategory.OST_Viewers, (long)BuiltInCategory.OST_Topography,
            (long)BuiltInCategory.OST_Site, (long)BuiltInCategory.OST_Floors, (long)BuiltInCategory.OST_Roofs,
            (long)BuiltInCategory.OST_Ceilings, (long)BuiltInCategory.OST_Mass,
        };

        private static List<Lane> OutlineLanes(Document doc, View view, Frame f, List<Item> items, double spacingMin, bool lanePerFamily)
        {
            var log = new System.Text.StringBuilder();
            double th = f.TextHeight;
            double gap = 0.6 * th;
            double rowPitch = Math.Max(spacingMin, f.MaxWidth + th);
            double colPitch = Math.Max(spacingMin, f.MaxHeight + gap);

            // The drawn region: the crop box, else the extents of the model elements in the view.
            double cx0, cx1, cy0, cy1;
            bool cropped = view.CropBoxActive;
            if (cropped)
            {
                BoundingBoxXYZ cb = view.CropBox;
                XYZ a = cb.Transform.OfPoint(cb.Min), b = cb.Transform.OfPoint(cb.Max);
                cx0 = Math.Min(a.DotProduct(f.Right), b.DotProduct(f.Right)); cx1 = Math.Max(a.DotProduct(f.Right), b.DotProduct(f.Right));
                cy0 = Math.Min(a.DotProduct(f.Up), b.DotProduct(f.Up)); cy1 = Math.Max(a.DotProduct(f.Up), b.DotProduct(f.Up));
            }
            else
            {
                cx0 = double.MaxValue; cx1 = double.MinValue; cy0 = double.MaxValue; cy1 = double.MinValue;
                foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    if (!IsObstacle(e))
                    {
                        continue;
                    }

                    BoundingBoxXYZ bb = e.get_BoundingBox(view);
                    if (bb == null)
                    {
                        continue;
                    }

                    foreach (XYZ c in Corners(bb))
                    {
                        cx0 = Math.Min(cx0, c.DotProduct(f.Right)); cx1 = Math.Max(cx1, c.DotProduct(f.Right));
                        cy0 = Math.Min(cy0, c.DotProduct(f.Up)); cy1 = Math.Max(cy1, c.DotProduct(f.Up));
                    }
                }

                if (cx0 > cx1)
                {
                    cx0 = f.MinS; cx1 = f.MaxS; cy0 = f.MinA; cy1 = f.MaxA;
                }
            }

            double margin = 2 * (f.MaxWidth + f.Clearance + 2 * th + f.Shift);
            double rx0 = cx0 - margin, rx1 = cx1 + margin, ry0 = cy0 - margin, ry1 = cy1 + margin;
            double cell = Math.Max(1.0, th);
            int nx = (int)Math.Ceiling((rx1 - rx0) / cell) + 1, ny = (int)Math.Ceiling((ry1 - ry0) / cell) + 1;
            if ((long)nx * ny > 4000000)
            {
                cell = Math.Sqrt((rx1 - rx0) * (ry1 - ry0) / 4000000.0);
                nx = (int)Math.Ceiling((rx1 - rx0) / cell) + 1; ny = (int)Math.Ceiling((ry1 - ry0) / cell) + 1;
            }

            log.AppendLine($"crop {cropped} region X[{cx0:F1},{cx1:F1}] Y[{cy0:F1},{cy1:F1}] margin {margin:F1} cell {cell:F2} grid {nx}x{ny} th {th:F2} maxW {f.MaxWidth:F2} maxH {f.MaxHeight:F2} clearance {f.Clearance:F2} rowPitch {rowPitch:F2} colPitch {colPitch:F2}");
            var occ = new bool[nx, ny];
            Func<double, int> ix = x => (int)Math.Floor((x - rx0) / cell);
            Func<double, int> iy = y => (int)Math.Floor((y - ry0) / cell);

            // Obstacles: what the drawing shows inside the region (clipped to the crop), no systems/rooms/slabs.
            double regionArea = (cx1 - cx0) * (cy1 - cy0);
            foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (!IsObstacle(e))
                {
                    continue;
                }

                BoundingBoxXYZ bb = e.get_BoundingBox(view);
                if (bb == null)
                {
                    continue;
                }

                double bx0 = double.MaxValue, bx1 = double.MinValue, by0 = double.MaxValue, by1 = double.MinValue;
                foreach (XYZ c in Corners(bb))
                {
                    bx0 = Math.Min(bx0, c.DotProduct(f.Right)); bx1 = Math.Max(bx1, c.DotProduct(f.Right));
                    by0 = Math.Min(by0, c.DotProduct(f.Up)); by1 = Math.Max(by1, c.DotProduct(f.Up));
                }

                bx0 = Math.Max(bx0, cx0); bx1 = Math.Min(bx1, cx1); by0 = Math.Max(by0, cy0); by1 = Math.Min(by1, cy1);
                if (bx1 < bx0 || by1 < by0 || (bx1 - bx0) * (by1 - by0) > 0.6 * regionArea)
                {
                    continue;
                }

                for (int i = Math.Max(0, ix(bx0)); i <= Math.Min(nx - 1, ix(bx1)); i++)
                {
                    for (int j = Math.Max(0, iy(by0)); j <= Math.Min(ny - 1, iy(by1)); j++)
                    {
                        occ[i, j] = true;
                    }
                }
            }

            // Flood fill from the region border; enclosed free cells are building interior.
            var outside = new bool[nx, ny];
            var queue = new Queue<int>();
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    if ((i == 0 || j == 0 || i == nx - 1 || j == ny - 1) && !occ[i, j])
                    {
                        outside[i, j] = true;
                        queue.Enqueue(i * ny + j);
                    }
                }
            }

            int[] di = { 1, -1, 0, 0 }, dj = { 0, 0, 1, -1 };
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int ci = c / ny, cj = c % ny;
                for (int k = 0; k < 4; k++)
                {
                    int a = ci + di[k], b = cj + dj[k];
                    if (a < 0 || b < 0 || a >= nx || b >= ny || occ[a, b] || outside[a, b])
                    {
                        continue;
                    }

                    outside[a, b] = true;
                    queue.Enqueue(a * ny + b);
                }
            }

            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    if (!occ[i, j] && !outside[i, j])
                    {
                        occ[i, j] = true;
                    }
                }
            }

            Func<int, int, bool> free = (i, j) => i >= 0 && j >= 0 && i < nx && j < ny && !occ[i, j];
            Func<int, int, bool> full = (i, j) => i >= 0 && j >= 0 && i < nx && j < ny && occ[i, j];

            var lanes = new List<Lane>();
            var candidates = new List<Candidate>();
            foreach (string side in Sides)
            {
                bool horizontal = side == "Top" || side == "Bottom";
                double pitch = horizontal ? rowPitch : colPitch;
                double textExtent = horizontal ? f.MaxHeight : f.MaxWidth;

                // Boundary cells of this side, grouped by their line (row or column of cells).
                var cellsByLine = new Dictionary<int, List<int>>();
                for (int i = 0; i < nx; i++)
                {
                    for (int j = 0; j < ny; j++)
                    {
                        if (!free(i, j))
                        {
                            continue;
                        }

                        bool hit = side == "Top" ? full(i, j - 1) : side == "Bottom" ? full(i, j + 1) : side == "Left" ? full(i + 1, j) : full(i - 1, j);
                        if (!hit)
                        {
                            continue;
                        }

                        int key = horizontal ? j : i;
                        if (!cellsByLine.TryGetValue(key, out List<int> list))
                        {
                            list = new List<int>();
                            cellsByLine[key] = list;
                        }

                        list.Add(horizontal ? i : j);
                    }
                }

                foreach (KeyValuePair<int, List<int>> kv in cellsByLine.OrderBy(kv => kv.Key))
                {
                    // Edges cut by the crop are not outline edges: the drawing continues there.
                    double edgeCoord = horizontal ? kv.Key * cell + ry0 : kv.Key * cell + rx0;
                    if (cropped)
                    {
                        double limit = side == "Top" ? cy1 : side == "Bottom" ? cy0 : side == "Left" ? cx0 : cx1;
                        if (Math.Abs(edgeCoord - limit) <= 2 * cell)
                        {
                            continue;
                        }
                    }

                    List<int> alongs = kv.Value.OrderBy(v => v).ToList();
                    var runs = new List<int[]>();
                    int runStart = alongs[0], prev = alongs[0];
                    for (int k = 1; k <= alongs.Count; k++)
                    {
                        if (k < alongs.Count && alongs[k] - prev <= 3)
                        {
                            prev = alongs[k];
                            continue;
                        }

                        runs.Add(new[] { runStart, prev });
                        if (k < alongs.Count)
                        {
                            runStart = alongs[k];
                            prev = alongs[k];
                        }
                    }

                    foreach (int[] run in runs)
                    {
                        double runLen = (run[1] - run[0] + 1) * cell;
                        double runFrom = run[0] * cell + (horizontal ? rx0 : ry0), runTo = (run[1] + 1) * cell + (horizontal ? rx0 : ry0);
                        if (runLen < 3 * pitch)
                        {
                            log.AppendLine($"  {side} edge {edgeCoord:F1} run [{runFrom:F1},{runTo:F1}] too short");
                            continue;
                        }

                        // Rings outwards: sub-runs whose text zone is free, with their coverage of the run.
                        var ringSubs = new List<List<int[]>>();
                        var ringLine = new List<double>();
                        var ringZone = new List<double[]>();
                        var coverage = new List<double>();
                        for (int ring = 0; ring < OutlineRings; ring++)
                        {
                            double offset = f.Clearance + ring * (textExtent + 2 * th + f.Shift);
                            double edge = horizontal
                                ? (side == "Top" ? kv.Key * cell + ry0 : (kv.Key + 1) * cell + ry0)
                                : (side == "Left" ? (kv.Key + 1) * cell + rx0 : kv.Key * cell + rx0);
                            double line = side == "Top" ? edge + offset : side == "Bottom" ? edge - offset : side == "Left" ? edge - offset : edge + offset;
                            double zoneNear = side == "Top" || side == "Right" ? line : line - textExtent;
                            double zoneFar = side == "Top" || side == "Right" ? line + textExtent : line;
                            bool inRegion = horizontal ? (zoneNear >= ry0 && zoneFar <= ry1) : (zoneNear >= rx0 && zoneFar <= rx1);
                            if (!inRegion)
                            {
                                break;
                            }

                            int z0 = horizontal ? Math.Max(0, iy(zoneNear)) : Math.Max(0, ix(zoneNear));
                            int z1 = horizontal ? Math.Min(ny - 1, iy(zoneFar)) : Math.Min(nx - 1, ix(zoneFar));
                            var subs = new List<int[]>();
                            int s0 = -1;
                            double covered = 0;
                            for (int a = run[0]; a <= run[1] + 1; a++)
                            {
                                bool ok = a <= run[1];
                                for (int z = z0; z <= z1 && ok; z++)
                                {
                                    if (!(horizontal ? free(a, z) : free(z, a)))
                                    {
                                        ok = false;
                                    }
                                }

                                if (ok)
                                {
                                    if (s0 < 0)
                                    {
                                        s0 = a;
                                    }
                                }
                                else if (s0 >= 0)
                                {
                                    if ((a - s0) * cell >= 3 * pitch)
                                    {
                                        subs.Add(new[] { s0, a - 1 });
                                        covered += (a - s0) * cell;
                                    }

                                    s0 = -1;
                                }
                            }

                            ringSubs.Add(subs);
                            ringLine.Add(line);
                            ringZone.Add(new[] { zoneNear, zoneFar });
                            coverage.Add(covered / runLen);
                            log.AppendLine($"  {side} edge {edgeCoord:F1} run [{runFrom:F1},{runTo:F1}] ring {ring} line {line:F1} zone [{zoneNear:F1},{zoneFar:F1}] coverage {covered / runLen:P0} subs {string.Join(" ", subs.Select(sb => "[" + (sb[0] * cell + (horizontal ? rx0 : ry0)).ToString("F1") + "," + ((sb[1] + 1) * cell + (horizontal ? rx0 : ry0)).ToString("F1") + "]"))}");
                        }

                        if (ringSubs.Count == 0)
                        {
                            continue;
                        }

                        int baseRing = coverage.FindIndex(cv => cv >= 0.6);
                        if (baseRing < 0)
                        {
                            baseRing = coverage.IndexOf(coverage.Max());
                        }

                        for (int ring = baseRing; ring < ringSubs.Count; ring++)
                        {
                            if (ring > baseRing && runLen < 6 * pitch)
                            {
                                break; // outer rings only along long edges
                            }

                            foreach (int[] sub in ringSubs[ring])
                            {
                                candidates.Add(new Candidate
                                {
                                    Side = side,
                                    Horizontal = horizontal,
                                    Ring = ring - baseRing,
                                    Line = ringLine[ring],
                                    ZoneNear = ringZone[ring][0],
                                    ZoneFar = ringZone[ring][1],
                                    From = sub[0],
                                    To = sub[1],
                                    Pitch = pitch,
                                    RunLength = runLen,
                                });
                            }
                        }
                    }
                }
            }

            // Lanes from different edges may want the same pocket. Inner rings and long
            // stretches take their text zone first; later candidates keep only the parts
            // of their zone that are still free.
            var reserved = new bool[nx, ny];
            foreach (Candidate c in candidates.OrderBy(c => c.Ring).ThenByDescending(c => c.To - c.From))
            {
                int z0 = c.Horizontal ? Math.Max(0, iy(c.ZoneNear)) : Math.Max(0, ix(c.ZoneNear));
                int z1 = c.Horizontal ? Math.Min(ny - 1, iy(c.ZoneFar)) : Math.Min(nx - 1, ix(c.ZoneFar));
                int s0 = -1;
                for (int a = c.From; a <= c.To + 1; a++)
                {
                    bool ok = a <= c.To;
                    for (int z = z0; z <= z1 && ok; z++)
                    {
                        int i = c.Horizontal ? a : z, j = c.Horizontal ? z : a;
                        if (!free(i, j) || reserved[i, j])
                        {
                            ok = false;
                        }
                    }

                    if (ok)
                    {
                        if (s0 < 0)
                        {
                            s0 = a;
                        }
                    }
                    else if (s0 >= 0)
                    {
                        int e = a - 1;
                        double stagger = c.Ring % 2 == 1 ? 0.5 * c.Pitch : 0;
                        double along0 = c.Horizontal ? rx0 : ry0;
                        // Half a pitch of margin at both ends keeps neighbouring fragments apart.
                        double a0 = s0 * cell + along0 + 0.5 * c.Pitch + stagger;
                        double a1 = (e + 1) * cell + along0 - 0.5 * c.Pitch;
                        double len = a1 - a0;
                        if (len >= (c.Ring == 0 ? 2 * c.Pitch : 3 * c.Pitch))
                        {
                            XYZ start = c.Horizontal ? f.P(a0, c.Line) : f.P(c.Line, a1);
                            XYZ dir = c.Horizontal ? f.Right : f.Up.Negate();
                            string name = (c.Horizontal ? (c.Side == "Top" ? "Top row" : "Bottom row") : (c.Side == "Left" ? "Left column" : "Right column"))
                                + " @" + c.Line.ToString("F0") + (c.Ring > 0 ? " +" + c.Ring : "");
                            Lane outlineLane = MakeLane(name, c.Side, c.Ring, start, dir, c.Horizontal ? f.Right : null, c.Horizontal, len, c.Pitch);
                            outlineLane.OrderAlongAxis = true;
                            lanes.Add(outlineLane);
                            for (int q = s0; q <= e; q++)
                            {
                                for (int z = z0; z <= z1; z++)
                                {
                                    reserved[c.Horizontal ? q : z, c.Horizontal ? z : q] = true;
                                }
                            }
                        }

                        s0 = -1;
                    }
                }
            }

            foreach (Lane lane in lanes)
            {
                log.AppendLine($"lane {lane.Name} ring {lane.Ring} start ({lane.Start.DotProduct(f.Right):F1},{lane.Start.DotProduct(f.Up):F1}) len {lane.Length:F1} pitch {lane.Pitch:F2} cap {lane.Capacity}");
            }

            LastLog = log.ToString();
            if (lanes.Count == 0)
            {
                return RectangleLanes(f, items, spacingMin);
            }

            // Elements inside the drawn region only. Every lane offers slots at its pitch; each
            // element takes a slot so that the total squared leader length is minimal (a few
            // medium leaders beat one long one), outer rings costing a little extra. Used slots
            // that touch each other form one run, so a tag sits right next to its element.
            var inRegionItems = items.Where(i => i.S >= cx0 - cell && i.S <= cx1 + cell && i.A >= cy0 - cell && i.A <= cy1 + cell).ToList();
            // Lane per family: the empty slots between family runs need room, so lanes are
            // filled to 70 % only; the rest of the tags move to the next ring or lane.
            var slots = new List<Slot>();
            foreach (Lane lane in lanes)
            {
                int usable = lanePerFamily ? Math.Max(1, (int)Math.Floor(lane.Capacity * 0.7)) : lane.Capacity;
                for (int k = 0; k < usable; k++)
                {
                    slots.Add(new Slot { Lane = lane, Index = k, Point = lane.Start + lane.Dir * (k * lane.Pitch) });
                }
            }

            Dictionary<Item, Slot> chosen = AssignSlots(inRegionItems, slots, (item, slot) =>
            {
                double d = item.Anchor.DistanceTo(slot.Point) + slot.Lane.Ring * 2 * slot.Lane.Pitch;
                return d * d;
            }, 60);

            var usedRuns = new List<Lane>();
            foreach (IGrouping<Lane, KeyValuePair<Item, Slot>> byLane in chosen.GroupBy(kv => kv.Value.Lane).OrderBy(g => lanes.IndexOf(g.Key)))
            {
                Lane lane = byLane.Key;
                List<KeyValuePair<Item, Slot>> ordered = byLane.OrderBy(kv => kv.Value.Index).ToList();
                if (lanePerFamily)
                {
                    // Every contiguous group of used slots becomes one block of family runs
                    // (one run per family, an empty slot between runs); the blocks are packed
                    // along the lane as close to their slots as the lane allows.
                    var groups = new List<List<KeyValuePair<Item, Slot>>>();
                    var current = new List<KeyValuePair<Item, Slot>>();
                    for (int k = 0; k < ordered.Count; k++)
                    {
                        if (k > 0 && ordered[k].Value.Index != ordered[k - 1].Value.Index + 1)
                        {
                            groups.Add(current);
                            current = new List<KeyValuePair<Item, Slot>>();
                        }

                        current.Add(ordered[k]);
                    }

                    groups.Add(current);
                    usedRuns.AddRange(PackFamilyRuns(lane, groups, f, spacingMin));
                    continue;
                }

                int from = 0;
                int part = 0;
                for (int k = 1; k <= ordered.Count; k++)
                {
                    if (k < ordered.Count && ordered[k].Value.Index == ordered[k - 1].Value.Index + 1)
                    {
                        continue;
                    }

                    part++;
                    var run = new Lane
                    {
                        Name = lane.Name + (from > 0 || k < ordered.Count ? " /" + part : ""),
                        Side = lane.Side,
                        Ring = lane.Ring,
                        Start = ordered[from].Value.Point,
                        Dir = lane.Dir,
                        ColumnDir = lane.ColumnDir,
                        Horizontal = lane.Horizontal,
                        OrderAlongAxis = true,
                        Length = (k - from - 1) * lane.Pitch,
                        Pitch = lane.Pitch,
                        Capacity = k - from,
                    };
                    for (int q = from; q < k; q++)
                    {
                        run.Tags.Add(ordered[q].Key.Tag);
                    }

                    usedRuns.Add(run);
                    from = k;
                }
            }

            lanes = usedRuns;
            foreach (Lane lane in lanes)
            {
                log.AppendLine($"run {lane.Name}: {lane.Tags.Count} tags from ({lane.Start.DotProduct(f.Right):F1},{lane.Start.DotProduct(f.Up):F1})");
            }

            LastLog = log.ToString();

            // Enlarge the crop box when a lane would fall outside it.
            if (cropped)
            {
                double ex0 = cx0, ex1 = cx1, ey0 = cy0, ey1 = cy1;
                foreach (Lane lane in lanes.Where(l => l.Tags.Count > 0))
                {
                    double runLen = (lane.Tags.Count - 1) * lane.Pitch;
                    double s = lane.Start.DotProduct(f.Right), a = lane.Start.DotProduct(f.Up);
                    if (lane.Horizontal)
                    {
                        ex0 = Math.Min(ex0, s - f.MaxWidth); ex1 = Math.Max(ex1, s + runLen + f.MaxWidth);
                        ey0 = Math.Min(ey0, a - f.MaxHeight - th); ey1 = Math.Max(ey1, a + f.MaxHeight + th);
                    }
                    else
                    {
                        ey1 = Math.Max(ey1, a + f.MaxHeight); ey0 = Math.Min(ey0, a - runLen - f.MaxHeight);
                        ex0 = Math.Min(ex0, s - f.MaxWidth - th); ex1 = Math.Max(ex1, s + f.MaxWidth + th);
                    }
                }

                if (ex0 < cx0 || ex1 > cx1 || ey0 < cy0 || ey1 > cy1)
                {
                    try
                    {
                        BoundingBoxXYZ cb = view.CropBox;
                        Transform inv = cb.Transform.Inverse;
                        XYZ lmin = inv.OfPoint(f.P(ex0, ey0)), lmax = inv.OfPoint(f.P(ex1, ey1));
                        cb.Min = new XYZ(Math.Min(lmin.X, lmax.X), Math.Min(lmin.Y, lmax.Y), cb.Min.Z);
                        cb.Max = new XYZ(Math.Max(lmin.X, lmax.X), Math.Max(lmin.Y, lmax.Y), cb.Max.Z);
                        view.CropBox = cb;
                    }
                    catch
                    {
                        // The view keeps its crop; some tags may sit outside it.
                    }
                }
            }

            return lanes;
        }

        private static bool IsObstacle(Element e)
        {
            Category cat = e.Category;
            if (cat == null || cat.CategoryType != CategoryType.Model || e is IndependentTag)
            {
                return false;
            }

            return !NotObstacles.Contains(cat.Id.Value);
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            Transform t = box.Transform ?? Transform.Identity;
            for (int k = 0; k < 8; k++)
            {
                yield return t.OfPoint(new XYZ((k & 1) == 0 ? box.Min.X : box.Max.X, (k & 2) == 0 ? box.Min.Y : box.Max.Y, (k & 4) == 0 ? box.Min.Z : box.Max.Z));
            }
        }

        // ----- lane per family (outline lanes): family runs per slot group, packed ---------

        private sealed class FamilyBlock
        {
            public double Desired;                    // preferred start along the lane
            public double Length;                     // from the first to the last tag
            public List<Tuple<string, List<IndependentTag>, double>> Runs = new List<Tuple<string, List<IndependentTag>, double>>(); // family, tags, pitch
        }

        private static List<Lane> PackFamilyRuns(Lane lane, List<List<KeyValuePair<Item, Slot>>> groups, Frame f, double spacingMin)
        {
            double gap = 0.6 * f.TextHeight;
            Func<string, double> pitchOf = fam =>
            {
                double fw = f.FamilyWidth.TryGetValue(fam, out double w0) ? w0 : f.MaxWidth;
                double fh = f.FamilyHeight.TryGetValue(fam, out double h0) ? h0 : f.MaxHeight;
                return lane.Horizontal ? Math.Max(spacingMin, fw + f.TextHeight) : Math.Max(spacingMin, fh + gap);
            };

            // Long groups are cut into chunks so that a tag stays near its element even when
            // the families alternate along the lane.
            const int MaxChunk = 8;
            var blocks = new List<FamilyBlock>();
            foreach (List<KeyValuePair<Item, Slot>> group in groups)
            {
                for (int offset = 0; offset < group.Count; offset += MaxChunk)
                {
                    List<KeyValuePair<Item, Slot>> chunk = group.Skip(offset).Take(MaxChunk).ToList();
                    var block = new FamilyBlock { Desired = chunk[0].Value.Index * lane.Pitch };
                    var families = chunk.GroupBy(kv => kv.Key.Family)
                        .Select(g => new { Family = g.Key, Tags = g.Select(kv => kv.Key.Tag).ToList(), Along = g.Average(kv => (kv.Key.Anchor - lane.Start).DotProduct(lane.Dir)) })
                        .OrderBy(g => g.Along)
                        .ToList();
                    double cursor = 0, previousPitch = 0;
                    bool first = true;
                    foreach (var fam in families)
                    {
                        double pitch = pitchOf(fam.Family);
                        if (!first)
                        {
                            cursor += 2 * Math.Max(previousPitch, pitch); // one empty slot between families
                        }

                        first = false;
                        block.Runs.Add(Tuple.Create(fam.Family, fam.Tags, pitch));
                        cursor += (fam.Tags.Count - 1) * pitch;
                        previousPitch = pitch;
                    }

                    block.Length = cursor;
                    blocks.Add(block);
                }
            }

            // Pack: keep every block as close to its slots as the neighbours and the lane end allow.
            double space = 2 * lane.Pitch;
            var remaining = new double[blocks.Count + 1];
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                remaining[i] = blocks[i].Length + remaining[i + 1] + (i < blocks.Count - 1 ? space : 0);
            }

            bool fits = remaining[0] <= lane.Length + 1e-6;
            var result = new List<Lane>();
            double previousEnd = double.NegativeInfinity;
            int part = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                FamilyBlock block = blocks[i];
                double start = fits
                    ? Math.Max(Math.Max(0, previousEnd + space), Math.Min(block.Desired, lane.Length - remaining[i]))
                    : Math.Max(0, previousEnd + space);
                previousEnd = start + block.Length;
                part++;

                double cursor = start, previousPitch = 0;
                bool firstRun = true;
                foreach (Tuple<string, List<IndependentTag>, double> run in block.Runs)
                {
                    if (!firstRun)
                    {
                        cursor += 2 * Math.Max(previousPitch, run.Item3);
                    }

                    firstRun = false;

                    var runLane = new Lane
                    {
                        Name = lane.Name + (blocks.Count > 1 ? " /" + part : "") + " [" + run.Item1 + "]",
                        Side = lane.Side,
                        Ring = lane.Ring,
                        Family = run.Item1,
                        Start = lane.Start + lane.Dir * cursor,
                        Dir = lane.Dir,
                        ColumnDir = lane.ColumnDir,
                        Horizontal = lane.Horizontal,
                        OrderAlongAxis = true,
                        Length = (run.Item2.Count - 1) * run.Item3,
                        Pitch = run.Item3,
                        Capacity = run.Item2.Count,
                    };
                    runLane.Tags.AddRange(run.Item2);
                    result.Add(runLane);
                    cursor += (run.Item2.Count - 1) * run.Item3;
                    previousPitch = run.Item3;
                }
            }

            return result;
        }

        // ----- lane per family: consecutive runs along each lane -----------------------

        private static List<Lane> SplitByFamily(List<Lane> lanes, List<Item> items, Frame f, double spacingMin)
        {
            double gap = 0.6 * f.TextHeight;
            var family = items.ToDictionary(i => i.Tag.Id.Value, i => i.Family);
            var anchors = items.ToDictionary(i => i.Tag.Id.Value, i => i.Anchor);
            var result = new List<Lane>();
            foreach (Lane lane in lanes)
            {
                if (lane.Tags.Count == 0)
                {
                    continue;
                }

                var groups = lane.Tags.GroupBy(t => family[t.Id.Value])
                    .Select(g => new { Family = g.Key, Tags = g.ToList(), Along = g.Average(t => (anchors[t.Id.Value] - lane.Start).DotProduct(lane.Dir)) })
                    .OrderBy(g => g.Along)
                    .ToList();
                Func<string, double> pitchOf = fam =>
                {
                    double fw = f.FamilyWidth.TryGetValue(fam, out double w0) ? w0 : f.MaxWidth;
                    double fh = f.FamilyHeight.TryGetValue(fam, out double h0) ? h0 : f.MaxHeight;
                    return lane.Horizontal ? Math.Max(spacingMin, fw + f.TextHeight) : Math.Max(spacingMin, fh + gap);
                };

                // Outline lanes: the block of family runs is centred on the elements along the lane.
                double startAlong = 0;
                if (lane.OrderAlongAxis)
                {
                    double total = 0;
                    double prev = 0;
                    bool firstGroup = true;
                    foreach (var group in groups)
                    {
                        double pitch = pitchOf(group.Family);
                        if (!firstGroup)
                        {
                            total += 2 * Math.Max(prev, pitch);
                        }

                        firstGroup = false;

                        total += (group.Tags.Count - 1) * pitch;
                        prev = pitch;
                    }

                    double meanAlong = lane.Tags.Average(t => (anchors[t.Id.Value] - lane.Start).DotProduct(lane.Dir));
                    startAlong = Math.Max(0, Math.Min(Math.Max(0, lane.Length - total), meanAlong - total / 2));
                }

                if (groups.Count == 1)
                {
                    lane.Family = groups[0].Family;
                    lane.Start = lane.Start + lane.Dir * startAlong;
                    lane.Length = Math.Max(0, lane.Length - startAlong);
                    result.Add(lane);
                    continue;
                }

                double cursor = startAlong;
                double previousPitch = 0;
                bool firstRun = true;
                foreach (var group in groups)
                {
                    double pitch = pitchOf(group.Family);
                    if (!firstRun)
                    {
                        cursor += 2 * Math.Max(previousPitch, pitch); // one empty slot between families
                    }

                    firstRun = false;

                    var run = new Lane
                    {
                        Name = lane.Name + " [" + group.Family + "]",
                        Side = lane.Side,
                        Ring = lane.Ring,
                        Family = group.Family,
                        Start = lane.Start + lane.Dir * cursor,
                        Dir = lane.Dir,
                        ColumnDir = lane.ColumnDir,
                        Horizontal = lane.Horizontal,
                        Length = Math.Max(0, lane.Length - cursor),
                        Pitch = pitch,
                        Capacity = group.Tags.Count,
                    };
                    run.Tags.AddRange(group.Tags);
                    result.Add(run);
                    cursor += (group.Tags.Count - 1) * pitch;
                    previousPitch = pitch;
                }
            }

            return result;
        }

        // ----- assignment ---------------------------------------------------------------

        /// <summary>
        /// Matches items to slots with minimal total cost (min-cost flow, successive shortest
        /// paths). Every item only offers its <paramref name="candidates"/> cheapest slots to the
        /// flow; an item left unmatched takes the cheapest slot still free, if any.
        /// </summary>
        private static Dictionary<Item, Slot> AssignSlots(List<Item> items, List<Slot> slots, Func<Item, Slot, double> cost, int candidates)
        {
            var result = new Dictionary<Item, Slot>();
            int n = items.Count, m = slots.Count;
            if (n == 0 || m == 0)
            {
                return result;
            }

            // Nodes: 0 source, 1 sink, 2..2+n-1 items, 2+n..2+n+m-1 slots.
            int nodes = 2 + n + m;
            var to = new List<int>();
            var cap = new List<int>();
            var cst = new List<double>();
            var adj = new List<int>[nodes];
            for (int v = 0; v < nodes; v++)
            {
                adj[v] = new List<int>();
            }

            Action<int, int, int, double> addEdge = (u, v, c, w) =>
            {
                adj[u].Add(to.Count); to.Add(v); cap.Add(c); cst.Add(w);
                adj[v].Add(to.Count); to.Add(u); cap.Add(0); cst.Add(-w);
            };

            var costs = new double[n][];
            for (int i = 0; i < n; i++)
            {
                costs[i] = new double[m];
                for (int l = 0; l < m; l++)
                {
                    costs[i][l] = cost(items[i], slots[l]);
                }

                addEdge(0, 2 + i, 1, 0);
                foreach (int l in Enumerable.Range(0, m).OrderBy(l => costs[i][l]).Take(Math.Max(1, candidates)))
                {
                    addEdge(2 + i, 2 + n + l, 1, costs[i][l]);
                }
            }

            for (int l = 0; l < m; l++)
            {
                addEdge(2 + n + l, 1, 1, 0);
            }

            var dist = new double[nodes];
            var prevEdge = new int[nodes];
            var inQueue = new bool[nodes];
            for (int flow = 0; flow < n; flow++)
            {
                for (int v = 0; v < nodes; v++)
                {
                    dist[v] = double.PositiveInfinity;
                    prevEdge[v] = -1;
                }

                dist[0] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(0);
                inQueue[0] = true;
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    inQueue[u] = false;
                    foreach (int e in adj[u])
                    {
                        if (cap[e] <= 0)
                        {
                            continue;
                        }

                        int v = to[e];
                        if (dist[u] + cst[e] < dist[v] - 1e-9)
                        {
                            dist[v] = dist[u] + cst[e];
                            prevEdge[v] = e;
                            if (!inQueue[v])
                            {
                                queue.Enqueue(v);
                                inQueue[v] = true;
                            }
                        }
                    }
                }

                if (prevEdge[1] < 0)
                {
                    break; // no augmenting path left
                }

                for (int v = 1; v != 0; v = to[prevEdge[v] ^ 1])
                {
                    cap[prevEdge[v]] -= 1;
                    cap[prevEdge[v] ^ 1] += 1;
                }
            }

            var taken = new bool[m];
            var unmatched = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int slot = -1;
                foreach (int e in adj[2 + i])
                {
                    int v = to[e];
                    if (v >= 2 + n && cap[e] == 0 && cap[e ^ 1] == 1)
                    {
                        slot = v - 2 - n;
                        break;
                    }
                }

                if (slot >= 0)
                {
                    result[items[i]] = slots[slot];
                    taken[slot] = true;
                }
                else
                {
                    unmatched.Add(i);
                }
            }

            foreach (int i in unmatched)
            {
                int best = -1;
                for (int l = 0; l < m; l++)
                {
                    if (!taken[l] && (best < 0 || costs[i][l] < costs[i][best]))
                    {
                        best = l;
                    }
                }

                if (best >= 0)
                {
                    result[items[i]] = slots[best];
                    taken[best] = true;
                }
            }

            return result;
        }

        // ----- helpers ----------------------------------------------------------------

        private static Lane MakeLane(string name, string side, int ring, XYZ start, XYZ dir, XYZ columnDir, bool horizontal, double length, double pitch)
        {
            return new Lane
            {
                Name = name,
                Side = side,
                Ring = ring,
                Start = start,
                Dir = dir.Normalize(),
                ColumnDir = columnDir,
                Horizontal = horizontal,
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
