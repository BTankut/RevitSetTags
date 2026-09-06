using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSetTags.Services
{
    /// <summary>
    /// Core behavior reverse-engineered (agentic video analysis) from the
    /// "Revit API (C#) - Tags ordering" demo:
    ///
    /// - PlaceColumn: the first tag head lands on the picked origin point and
    ///   every following tag one Spacing further down the view's up direction,
    ///   Tag(i) = origin - i * spacing * up. Tags are ordered by the position of
    ///   their tagged ("host") elements along that same direction so the leader
    ///   lines do not cross.
    /// - Nudge: moves the tags currently selected in the view by the Shift
    ///   amount along the view up direction (post-correction).
    /// </summary>
    public static class TagOrderingService
    {
        public static string PlaceColumn(Document doc, IList<IndependentTag> tags, XYZ origin, double spacing, XYZ up)
        {
            List<IndependentTag> ordered = tags
                .Select(tag => new { Tag = tag, Anchor = TryGetAnchor(doc, tag) })
                .Where(item => item.Anchor != null)
                .OrderByDescending(item => item.Anchor.DotProduct(up))
                .ThenBy(item => item.Tag.Id.Value)
                .Select(item => item.Tag)
                .ToList();

            int placed = 0;
            int skipped = 0;
            int leaders = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                XYZ target = origin - up * (spacing * i);
                IndependentTag tag = ordered[i];
                try
                {
                    if (TrySetHeadPosition(tag, target))
                    {
                        placed++;
                        if (EnsureLeader(tag))
                        {
                            leaders++;
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch
                {
                    skipped++;
                }
            }

            return $"{placed} tag(s) placed in column, {skipped} skipped, {leaders} leader(s) enabled.";
        }

        public static string Nudge(Document doc, IList<IndependentTag> tags, XYZ delta)
        {
            int moved = 0;
            int skipped = 0;

            foreach (IndependentTag tag in tags)
            {
                try
                {
                    XYZ current = GetHeadPosition(tag);
                    if (current != null && TrySetHeadPosition(tag, current + delta))
                    {
                        moved++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch
                {
                    skipped++;
                }
            }

            return $"{moved} tag(s) shifted, {skipped} skipped.";
        }

        public static XYZ GetViewUp(View view)
        {
            try
            {
                XYZ up = view.UpDirection;
                if (up != null && up.GetLength() > 1e-9)
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

        private static XYZ TryGetAnchor(Document doc, IndependentTag tag)
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
                // Multi-reference or unpinnable tags fall back to the tag head.
            }

            return GetHeadPosition(tag);
        }

        private static XYZ GetElementCenter(Element element)
        {
            try
            {
                if (element.Location is LocationPoint point)
                {
                    return point.Point;
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

        private static bool EnsureLeader(IndependentTag tag)
        {
            try
            {
                if (!tag.HasLeader)
                {
                    tag.HasLeader = true;
                    return true;
                }
            }
            catch
            {
                // Some tag types/views do not support leaders; keep the tag placement anyway.
            }

            return false;
        }
    }
}
