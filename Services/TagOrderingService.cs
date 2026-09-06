using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSetTags.Services
{
    /// <summary>
    /// Session-scoped store for the tags chosen with "Pick elements" and for the
    /// tag head positions captured before the first "Set tags" run, so "Reset"
    /// can restore them. Cleared when Revit restarts.
    /// </summary>
    public static class TagStore
    {
        private static readonly Dictionary<string, ElementId> TagIdsByDocument =
            new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private static readonly List<ElementId> TagIds = new List<ElementId>();

        private static readonly Dictionary<string, XYZ> OriginalPositions =
            new Dictionary<string, XYZ>(StringComparer.Ordinal);

        public static void AddTagIds(Document doc, IEnumerable<ElementId> ids)
        {
            foreach (ElementId id in ids)
            {
                if (!TagIds.Contains(id))
                {
                    TagIds.Add(id);
                }
            }
        }

        public static List<IndependentTag> GetTags(Document doc)
        {
            return TagIds
                .Select(id => doc.GetElement(id))
                .OfType<IndependentTag>()
                .ToList();
        }

        public static void RememberOriginalPosition(IndependentTag tag, XYZ position)
        {
            if (!OriginalPositions.ContainsKey(tag.UniqueId))
            {
                OriginalPositions[tag.UniqueId] = position;
            }
        }

        public static bool TryGetOriginalPosition(IndependentTag tag, out XYZ position)
        {
            return OriginalPositions.TryGetValue(tag.UniqueId, out position);
        }

        public static string Describe(Document doc)
        {
            int count = GetTags(doc).Count;
            return $"Tags selected: {count}.";
        }
    }

    /// <summary>
    /// Core behavior reverse-engineered from the "Revit API (C#) - Tags ordering" demo:
    /// the first tag head is placed at the start position, every following tag is
    /// placed one shift vector further, so the tags form an evenly spaced column.
    /// Tags are ordered by projecting their current head position onto the shift
    /// direction, which reproduces the "chosen tags are transferred and ordered"
    /// result of the demo.
    /// </summary>
    public static class TagOrderingService
    {
        public static string SetTags(Document doc, IList<IndependentTag> tags, XYZ startMeters, XYZ shiftMeters)
        {
            XYZ start = ToInternal(startMeters);
            XYZ shift = ToInternal(shiftMeters);
            XYZ direction = shift.GetLength() > 1e-9 ? shift.Normalize() : XYZ.BasisZ;

            List<IndependentTag> ordered = tags
                .Select(tag => new { Tag = tag, Head = TryGetHead(tag) })
                .Where(item => item.Head != null)
                .OrderBy(item => item.Head.DotProduct(direction))
                .ThenBy(item => item.Tag.Id.Value)
                .Select(item => item.Tag)
                .ToList();

            int moved = 0;
            int skipped = 0;
            int leaders = 0;

            foreach (IndependentTag tag in ordered)
            {
                XYZ target = start + shift * moved;
                try
                {
                    if (tag.Location is LocationPoint head)
                    {
                        TagStore.RememberOriginalPosition(tag, head.Point);
                        head.Point = target;
                        moved++;
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

            return $"{moved} tag(s) ordered, {skipped} skipped, {leaders} leader(s) enabled.";
        }

        public static string ResetTags(Document doc, IList<IndependentTag> tags)
        {
            int restored = 0;
            int missing = 0;

            foreach (IndependentTag tag in tags)
            {
                if (tag.Location is LocationPoint head && TagStore.TryGetOriginalPosition(tag, out XYZ original))
                {
                    head.Point = original;
                    restored++;
                }
                else
                {
                    missing++;
                }
            }

            return $"{restored} tag(s) restored, {missing} without a stored position.";
        }

        private static XYZ TryGetHead(IndependentTag tag)
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

        private static XYZ ToInternal(XYZ meters)
        {
            double factor = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Meters);
            return new XYZ(meters.X * factor, meters.Y * factor, meters.Z * factor);
        }
    }
}
