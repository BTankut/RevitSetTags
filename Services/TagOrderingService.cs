using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSetTags.Services
{
    /// <summary>
    /// Outcome of one column layout run.
    /// </summary>
    public sealed class ColumnResult
    {
        public int Placed;
        public int Skipped;
        public int ElbowsSet;
        public int EndsKept;
        public int Measured;

        public override string ToString()
        {
            return $"{Placed} placed, {Skipped} skipped, {ElbowsSet} elbows, {EndsKept} ends kept.";
        }
    }

    /// <summary>
    /// Core behavior reverse-engineered from the "Revit API (C#) - Tags ordering"
    /// demo (ProEngineering Tools palette):
    ///
    /// - Tags are stacked from the picked origin along the column axis (straight
    ///   down the view unless a direction was picked): Row(i) = origin + i * spacing * axis.
    /// - The text block is centred on its row and its edge facing the elements is
    ///   put on the column line: the tag family's labels are read once per family
    ///   (EditFamily) and the text width is computed from the font's advance widths,
    ///   so centred or offset labels line up like left-aligned ones and the leader
    ///   (which leaves the text at the edge midpoint) gets a level shoulder.
    /// - Every leader keeps the point it pointed at (the leader end is switched to
    ///   "free" and restored after the move), gets a shoulder of length "Shift x"
    ///   from the column line towards the elements (elbows aligned, shoulders
    ///   equal), and the rows are ordered so that the leaders fan out without crossing.
    /// - Re-running with new values on the same tags/origin gives the live
    ///   post-correction seen in the demo.
    /// </summary>
    public static class TagOrderingService
    {
        private const double Eps = 1e-9;
        private const double CapHeightRatio = 0.716; // Revit "text size" is the cap height; Arial cap height / em

        private sealed class TagItem
        {
            public IndependentTag Tag;
            public XYZ Head;
            public XYZ Anchor;                       // where the leaders point (mean of the leader ends)
            public List<KeyValuePair<Reference, XYZ>> Ends = new List<KeyValuePair<Reference, XYZ>>();
            public bool FreeEnd;                     // leader end condition could be made free
            public double EdgeOffset;                // text edge facing the elements, along the shoulder axis, relative to the head
            public XYZ CenterOffset = XYZ.Zero;      // text block centre relative to the head (model units, view plane)
        }

        private sealed class LabelInfo
        {
            public double X;
            public double Y;
            public double Size;
            public double WidthFactor;
            public string Font;
            public bool Bold;
            public bool Italic;
            public HorizontalTextAlignment HAlign;
            public VerticalTextAlignment VAlign;
        }

        private static readonly Dictionary<string, List<LabelInfo>> FamilyLabels = new Dictionary<string, List<LabelInfo>>();
        private static readonly Dictionary<string, double> AdvanceCache = new Dictionary<string, double>();

        /// <summary>
        /// Reads the label geometry of every tag family used by the tags (once per
        /// family, cached). Must be called outside a transaction.
        /// </summary>
        public static void PrepareTextMetrics(Document doc, IEnumerable<IndependentTag> tags)
        {
            foreach (IndependentTag tag in tags)
            {
                Family family = null;
                try
                {
                    family = (doc.GetElement(tag.GetTypeId()) as FamilySymbol)?.Family;
                }
                catch
                {
                    // Ignore.
                }

                if (family == null)
                {
                    continue;
                }

                string key = FamilyKey(doc, family);
                if (FamilyLabels.ContainsKey(key))
                {
                    continue;
                }

                List<LabelInfo> labels = null;
                Document familyDoc = null;
                try
                {
                    familyDoc = doc.EditFamily(family);
                    labels = new List<LabelInfo>();
                    foreach (TextElement text in new FilteredElementCollector(familyDoc).OfClass(typeof(TextElement)).Cast<TextElement>())
                    {
                        TextElementType type = familyDoc.GetElement(text.GetTypeId()) as TextElementType;
                        if (type == null)
                        {
                            continue;
                        }

                        labels.Add(new LabelInfo
                        {
                            X = text.Coord.X,
                            Y = text.Coord.Y,
                            Size = ParamDouble(type, BuiltInParameter.TEXT_SIZE, 0.0082),
                            WidthFactor = ParamDouble(type, BuiltInParameter.TEXT_WIDTH_SCALE, 1.0),
                            Font = ParamString(type, BuiltInParameter.TEXT_FONT, "Arial"),
                            Bold = ParamInt(type, BuiltInParameter.TEXT_STYLE_BOLD) == 1,
                            Italic = ParamInt(type, BuiltInParameter.TEXT_STYLE_ITALIC) == 1,
                            HAlign = text.HorizontalAlignment,
                            VAlign = text.VerticalAlignment,
                        });
                    }
                }
                catch
                {
                    labels = null;
                }
                finally
                {
                    if (familyDoc != null)
                    {
                        try
                        {
                            familyDoc.Close(false);
                        }
                        catch
                        {
                            // Nothing to do.
                        }
                    }
                }

                FamilyLabels[key] = labels;
            }
        }

        /// <param name="columnDir">Optional column direction (any vector; projected into the view plane). Null = down the view.</param>
        public static ColumnResult PlaceColumn(Document doc, View view, IList<IndependentTag> tags, XYZ origin, XYZ columnDir, double spacing, double shift)
        {
            shift = Math.Abs(shift);
            XYZ up = GetViewUp(view);
            XYZ right = GetViewRight(view, up);
            XYZ viewDir = GetViewDirection(view, up, right);

            XYZ axis = ProjectToViewPlane(columnDir, viewDir);
            if (axis == null || axis.GetLength() < Eps)
            {
                axis = up.Negate();
            }

            axis = axis.Normalize();

            XYZ shoulderAxis = viewDir.CrossProduct(axis);
            if (shoulderAxis.GetLength() < Eps)
            {
                shoulderAxis = right;
            }

            shoulderAxis = shoulderAxis.Normalize();

            var result = new ColumnResult();

            // 1. Capture where every leader points before anything moves.
            var items = new List<TagItem>();
            foreach (IndependentTag tag in tags)
            {
                if (tag == null || !tag.IsValidObject)
                {
                    continue;
                }

                XYZ head = GetHeadPosition(tag);
                if (head == null)
                {
                    result.Skipped++;
                    continue;
                }

                var item = new TagItem { Tag = tag, Head = head };
                CaptureLeaderEnds(item);
                item.Anchor = item.Ends.Count > 0
                    ? Mean(item.Ends.Select(e => e.Value))
                    : (TryGetElementAnchor(tag) ?? head);
                items.Add(item);
            }

            if (items.Count == 0)
            {
                return result;
            }

            // Keep the column in the annotation plane of the tags (depth along the view direction).
            origin = ProjectToTagPlane(origin, items.Select(item => item.Head).ToList(), viewDir);
            double s0 = origin.DotProduct(shoulderAxis);
            double a0 = origin.DotProduct(axis);

            // 2. Shoulders point from the column towards the elements.
            double meanAnchorSide = items.Average(item => item.Anchor.DotProduct(shoulderAxis));
            int sign = meanAnchorSide >= s0 ? 1 : -1;
            double elbowS = s0 + sign * shift;

            // 3. Text block per tag: edge facing the elements and block centre (0 when the family is unknown).
            foreach (TagItem item in items)
            {
                if (TextMetrics(doc, view, item.Tag, shoulderAxis, right, up, sign, out double edge, out XYZ center))
                {
                    item.EdgeOffset = edge;
                    item.CenterOffset = center;
                    result.Measured++;
                }
            }

            // 4. Order the rows: angular fan from the column centre, then remove crossings.
            List<TagItem> ordered = OrderRows(items, axis, shoulderAxis, s0, a0, spacing, elbowS, sign);

            // 5. Place heads (text centre on the row, text edge on the column line), restore leaders,
            //    set ends and elbows. The leader leaves the text at the edge midpoint, so an elbow on
            //    the row gives a shoulder with zero angle.
            for (int i = 0; i < ordered.Count; i++)
            {
                TagItem item = ordered[i];
                XYZ rowPoint = origin + axis * (spacing * i);
                XYZ newHead = rowPoint - axis * item.CenterOffset.DotProduct(axis) - shoulderAxis * item.EdgeOffset;
                try
                {
                    if (!TrySetHeadPosition(item.Tag, newHead))
                    {
                        result.Skipped++;
                        continue;
                    }

                    result.Placed++;
                    XYZ elbow = rowPoint + shoulderAxis * (sign * shift);
                    RestoreLeaders(item, elbow, result);
                }
                catch
                {
                    result.Skipped++;
                }
            }

            return result;
        }

        // ----- text metrics ------------------------------------------------------------

        private static string FamilyKey(Document doc, Family family)
        {
            return (doc.PathName ?? doc.Title) + "|" + family.Id.Value;
        }

        /// <summary>
        /// Text block of the tag relative to its head (model units): the signed distance
        /// along the shoulder axis to the edge facing the elements, and the block centre.
        /// False when the family geometry is unknown.
        /// </summary>
        private static bool TextMetrics(Document doc, View view, IndependentTag tag, XYZ shoulderAxis, XYZ right, XYZ up, int sign, out double edgeOffset, out XYZ centerOffset)
        {
            edgeOffset = 0;
            centerOffset = XYZ.Zero;
            try
            {
                if (tag.TagOrientation != TagOrientation.Horizontal)
                {
                    return false;
                }

                Family family = (doc.GetElement(tag.GetTypeId()) as FamilySymbol)?.Family;
                if (family == null || !FamilyLabels.TryGetValue(FamilyKey(doc, family), out List<LabelInfo> labels) || labels == null || labels.Count == 0)
                {
                    return false;
                }

                string text = tag.TagText ?? "";
                if (text.Length == 0)
                {
                    return false;
                }

                // Text block in family (paper) units relative to the origin; x = view right, y = view up.
                double left = double.MaxValue, rightEdge = double.MinValue, bottom = double.MaxValue, top = double.MinValue;
                foreach (LabelInfo label in labels)
                {
                    double width = AdvanceEm(text, label.Font, label.Bold, label.Italic) * (label.Size / CapHeightRatio) * label.WidthFactor;
                    double height = label.Size;
                    double l = label.X - (label.HAlign == HorizontalTextAlignment.Center ? width * 0.5 : label.HAlign == HorizontalTextAlignment.Right ? width : 0);
                    double b = label.Y - (label.VAlign == VerticalTextAlignment.Middle ? height * 0.5 : label.VAlign == VerticalTextAlignment.Top ? height : 0);
                    left = Math.Min(left, l);
                    rightEdge = Math.Max(rightEdge, l + width);
                    bottom = Math.Min(bottom, b);
                    top = Math.Max(top, b + height);
                }

                double scale = view.Scale > 0 ? view.Scale : 100;
                double cs = shoulderAxis.DotProduct(right);
                double cu = shoulderAxis.DotProduct(up);
                var corners = new[]
                {
                    left * cs + bottom * cu, left * cs + top * cu, rightEdge * cs + bottom * cu, rightEdge * cs + top * cu,
                };

                edgeOffset = (sign > 0 ? corners.Max() : corners.Min()) * scale;
                centerOffset = (right * ((left + rightEdge) * 0.5) + up * ((bottom + top) * 0.5)) * scale;
                return true;
            }
            catch
            {
                edgeOffset = 0;
                centerOffset = XYZ.Zero;
                return false;
            }
        }

        /// <summary>Sum of the glyph advance widths of the text, in em units, measured with GDI.</summary>
        private static double AdvanceEm(string text, string fontName, bool bold, bool italic)
        {
            string key = fontName + "|" + bold + "|" + italic + "|" + text;
            if (AdvanceCache.TryGetValue(key, out double cached))
            {
                return cached;
            }

            double em;
            try
            {
                var style = System.Drawing.FontStyle.Regular;
                if (bold)
                {
                    style |= System.Drawing.FontStyle.Bold;
                }

                if (italic)
                {
                    style |= System.Drawing.FontStyle.Italic;
                }

                const float emPixels = 200f;
                using (var font = new System.Drawing.Font(string.IsNullOrEmpty(fontName) ? "Arial" : fontName, emPixels, style, System.Drawing.GraphicsUnit.Pixel))
                {
                    System.Drawing.Size size = System.Windows.Forms.TextRenderer.MeasureText(
                        text, font, new System.Drawing.Size(int.MaxValue, int.MaxValue),
                        System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.SingleLine);
                    em = size.Width / emPixels;
                }
            }
            catch
            {
                em = text.Length * 0.55; // rough Arial average
            }

            AdvanceCache[key] = em;
            return em;
        }

        private static double ParamDouble(Element element, BuiltInParameter parameter, double fallback)
        {
            try
            {
                Parameter p = element.get_Parameter(parameter);
                return p != null && p.HasValue ? p.AsDouble() : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static int ParamInt(Element element, BuiltInParameter parameter)
        {
            try
            {
                Parameter p = element.get_Parameter(parameter);
                return p != null && p.HasValue ? p.AsInteger() : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string ParamString(Element element, BuiltInParameter parameter, string fallback)
        {
            try
            {
                Parameter p = element.get_Parameter(parameter);
                string s = p != null && p.HasValue ? p.AsString() : null;
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch
            {
                return fallback;
            }
        }

        // ----- leaders -----------------------------------------------------------------

        private static void CaptureLeaderEnds(TagItem item)
        {
            IndependentTag tag = item.Tag;
            try
            {
                if (!tag.HasLeader)
                {
                    tag.HasLeader = true;
                }
            }
            catch
            {
                return;
            }

            try
            {
                if (tag.LeaderEndCondition == LeaderEndCondition.Free)
                {
                    item.FreeEnd = true;
                }
                else if (tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                {
                    // Revit keeps the end where the attached leader ended; from now on it stays put.
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    item.FreeEnd = true;
                }
            }
            catch
            {
                item.FreeEnd = false;
            }

            if (!item.FreeEnd)
            {
                return;
            }

            IList<Reference> refs;
            try
            {
                refs = tag.GetTaggedReferences();
            }
            catch
            {
                return;
            }

            foreach (Reference reference in refs)
            {
                try
                {
                    if (!IsLeaderVisible(tag, reference))
                    {
                        continue;
                    }

                    item.Ends.Add(new KeyValuePair<Reference, XYZ>(reference, tag.GetLeaderEnd(reference)));
                }
                catch
                {
                    // Leader without a free end; ignore.
                }
            }
        }

        private static void RestoreLeaders(TagItem item, XYZ elbow, ColumnResult result)
        {
            IndependentTag tag = item.Tag;
            try
            {
                if (!tag.HasLeader)
                {
                    tag.HasLeader = true;
                }
            }
            catch
            {
                return;
            }

            if (item.FreeEnd)
            {
                try
                {
                    if (tag.LeaderEndCondition != LeaderEndCondition.Free && tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                    {
                        tag.LeaderEndCondition = LeaderEndCondition.Free;
                    }
                }
                catch
                {
                    // Keep whatever Revit gives us.
                }

                foreach (KeyValuePair<Reference, XYZ> end in item.Ends)
                {
                    try
                    {
                        tag.SetLeaderEnd(end.Key, end.Value);
                        result.EndsKept++;
                    }
                    catch
                    {
                        // The reference is no longer tagged or the leader is hidden.
                    }
                }
            }

            IList<Reference> refs;
            try
            {
                refs = tag.GetTaggedReferences();
            }
            catch
            {
                return;
            }

            foreach (Reference reference in refs)
            {
                try
                {
                    if (!IsLeaderVisible(tag, reference))
                    {
                        continue;
                    }

                    tag.SetLeaderElbow(reference, elbow);
                    result.ElbowsSet++;
                }
                catch
                {
                    // Straight-only leaders: keep the placement, skip the elbow.
                }
            }
        }

        private static bool IsLeaderVisible(IndependentTag tag, Reference reference)
        {
            try
            {
                return tag.IsLeaderVisible(reference);
            }
            catch
            {
                return true;
            }
        }

        // ----- ordering ----------------------------------------------------------------

        private static List<TagItem> OrderRows(List<TagItem> items, XYZ axis, XYZ shoulderAxis, double s0, double a0, double spacing, double elbowS, int sign)
        {
            double centreA = a0 + spacing * (items.Count - 1) * 0.5;

            // Angular fan: the anchor pointing most towards the start of the column gets the first row.
            List<TagItem> ordered = items
                .OrderByDescending(item =>
                {
                    double u = sign * (item.Anchor.DotProduct(shoulderAxis) - s0);
                    double v = -(item.Anchor.DotProduct(axis) - centreA);
                    return Math.Atan2(v, u);
                })
                .ThenBy(item => item.Tag.Id.Value)
                .ToList();

            // Uncross: swapping the targets of two crossing leaders always shortens the total length,
            // so this terminates with no crossings left.
            for (int pass = 0; pass < 25; pass++)
            {
                bool swapped = false;
                for (int i = 0; i < ordered.Count; i++)
                {
                    for (int j = i + 1; j < ordered.Count; j++)
                    {
                        double ai = a0 + spacing * i;
                        double aj = a0 + spacing * j;
                        if (SegmentsCross(elbowS, ai, P(ordered[i], shoulderAxis, axis), elbowS, aj, P(ordered[j], shoulderAxis, axis)))
                        {
                            TagItem tmp = ordered[i];
                            ordered[i] = ordered[j];
                            ordered[j] = tmp;
                            swapped = true;
                        }
                    }
                }

                if (!swapped)
                {
                    break;
                }
            }

            return ordered;
        }

        private static double[] P(TagItem item, XYZ shoulderAxis, XYZ axis)
        {
            return new[] { item.Anchor.DotProduct(shoulderAxis), item.Anchor.DotProduct(axis) };
        }

        /// <summary>Proper intersection of segments (s1,a1)-(p1) and (s2,a2)-(p2) in 2D.</summary>
        private static bool SegmentsCross(double s1, double a1, double[] p1, double s2, double a2, double[] p2)
        {
            double d1 = Cross(s1, a1, p1[0], p1[1], s2, a2);
            double d2 = Cross(s1, a1, p1[0], p1[1], p2[0], p2[1]);
            double d3 = Cross(s2, a2, p2[0], p2[1], s1, a1);
            double d4 = Cross(s2, a2, p2[0], p2[1], p1[0], p1[1]);
            return d1 * d2 < -Eps && d3 * d4 < -Eps;
        }

        private static double Cross(double ox, double oy, double ax, double ay, double bx, double by)
        {
            return (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
        }

        // ----- geometry helpers --------------------------------------------------------

        private static XYZ Mean(IEnumerable<XYZ> points)
        {
            XYZ sum = XYZ.Zero;
            int n = 0;
            foreach (XYZ p in points)
            {
                sum += p;
                n++;
            }

            return n == 0 ? null : sum / n;
        }

        public static XYZ GetViewUp(View view)
        {
            try
            {
                XYZ up = view.UpDirection;
                if (up != null && up.GetLength() > Eps)
                {
                    return up.Normalize();
                }
            }
            catch
            {
                // Some view types do not expose UpDirection.
            }

            return XYZ.BasisZ;
        }

        public static XYZ GetViewRight(View view, XYZ up)
        {
            try
            {
                XYZ right = view.RightDirection;
                if (right != null && right.GetLength() > Eps)
                {
                    return right.Normalize();
                }
            }
            catch
            {
                // Fall through.
            }

            XYZ fallback = up.CrossProduct(XYZ.BasisZ);
            return fallback.GetLength() > Eps ? fallback.Normalize() : XYZ.BasisX;
        }

        public static XYZ GetViewDirection(View view, XYZ up, XYZ right)
        {
            try
            {
                XYZ dir = view.ViewDirection;
                if (dir != null && dir.GetLength() > Eps)
                {
                    return dir.Normalize();
                }
            }
            catch
            {
                // Fall through.
            }

            XYZ fallback = right.CrossProduct(up);
            return fallback.GetLength() > Eps ? fallback.Normalize() : XYZ.BasisZ;
        }

        /// <summary>
        /// Mean head position of the tags; used as the origin of the temporary
        /// work plane for picking in 3D views.
        /// </summary>
        public static XYZ GetMeanHead(IEnumerable<IndependentTag> tags)
        {
            return Mean(tags.Select(GetHeadPosition).Where(h => h != null));
        }

        public static XYZ ProjectToViewPlane(XYZ vector, XYZ viewDir)
        {
            if (vector == null)
            {
                return null;
            }

            return vector - viewDir * vector.DotProduct(viewDir);
        }

        private static XYZ ProjectToTagPlane(XYZ origin, IList<XYZ> heads, XYZ viewDir)
        {
            if (heads.Count == 0)
            {
                return origin;
            }

            double meanDepth = heads.Average(h => h.DotProduct(viewDir));
            double originDepth = origin.DotProduct(viewDir);
            return origin + viewDir * (meanDepth - originDepth);
        }

        private static XYZ TryGetElementAnchor(IndependentTag tag)
        {
            try
            {
                foreach (Element host in tag.GetTaggedLocalElements())
                {
                    XYZ center = GetElementCenter(host);
                    if (center != null)
                    {
                        return center;
                    }
                }
            }
            catch
            {
                // Linked or unavailable hosts fall back to the tag head.
            }

            return null;
        }

        private static XYZ GetElementCenter(Element element)
        {
            try
            {
                if (element.Location is LocationPoint point)
                {
                    return point.Point;
                }

                if (element.Location is LocationCurve curve && curve.Curve != null)
                {
                    return curve.Curve.Evaluate(0.5, true);
                }

                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box != null)
                {
                    return (box.Min + box.Max) * 0.5;
                }
            }
            catch
            {
                // Fall through.
            }

            return null;
        }

        private static XYZ GetHeadPosition(IndependentTag tag)
        {
            try
            {
                return tag.TagHeadPosition;
            }
            catch
            {
                try
                {
                    return (tag.Location as LocationPoint)?.Point;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool TrySetHeadPosition(IndependentTag tag, XYZ position)
        {
            try
            {
                tag.TagHeadPosition = position;
                return true;
            }
            catch
            {
                try
                {
                    if (tag.Location is LocationPoint head)
                    {
                        head.Point = position;
                        return true;
                    }
                }
                catch
                {
                    // Tag cannot be moved in this view.
                }

                return false;
            }
        }
    }
}
