using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSetTags.Services;
using RevitSetTags.UI;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace RevitSetTags.Handlers
{
    public enum HandlerMode
    {
        None,
        GetTags,
        PlaceColumn,
        AutoLanes,
        Relayout
    }

    /// <summary>
    /// One entry of the palette's filter: a tag category, optionally narrowed to
    /// one tag family, with the number of matching tags in the current selection.
    /// Null filter = every selected tag.
    /// </summary>
    public sealed class TagTypeFilter
    {
        public string Display;
        public ElementId CategoryId;
        public ElementId FamilyId;
        public int Count;

        public override string ToString()
        {
            return Display;
        }

        public bool Matches(IndependentTag tag)
        {
            try
            {
                if (tag.Category == null || tag.Category.Id != CategoryId)
                {
                    return false;
                }

                if (FamilyId == null)
                {
                    return true;
                }

                FamilySymbol symbol = tag.Document.GetElement(tag.GetTypeId()) as FamilySymbol;
                return symbol?.Family != null && symbol.Family.Id == FamilyId;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Runs the palette's actions on the Revit API context. The modeless window
    /// raises the external event; Revit calls Execute when the API is idle.
    ///
    /// Workflow: GetTags selects tags (nothing else); the palette then offers a
    /// filter built from that selection; PlaceColumn picks the column origin and
    /// an optional direction point, lays the (filtered) tags out and ends the
    /// command. Relayout applies new "Spacing x" / "Shift x" values to the tags
    /// selected in the view, or to the last placed group when nothing is selected.
    /// </summary>
    public class SetTagsHandler : IExternalEventHandler
    {
        private const string TransactionName = "Order Tags";
        private const int MaxSessions = 50;

        public HandlerMode Mode { get; set; } = HandlerMode.None;
        public double SpacingMeters { get; set; } = 0.6;
        public double ShiftMeters { get; set; } = 0.3;
        public TagTypeFilter Filter { get; set; }
        /// <summary>When true, Pick direction asks for a second (direction) point; otherwise the column goes straight down.</summary>
        public bool PickDirectionPoint { get; set; }
        /// <summary>Auto lanes: one side (with rings) per tag family instead of sides by bearing.</summary>
        public bool LanePerFamily { get; set; }
        public SetTagsWindow.WindowBridge Bridge { get; set; }

        private PendingSelection _pending;
        private readonly List<ColumnSession> _sessions = new List<ColumnSession>();
        private readonly List<ColumnSession> _lastOperation = new List<ColumnSession>();

        private sealed class PendingSelection
        {
            public string DocumentKey;
            public ElementId ViewId;
            public List<ElementId> TagIds;
        }

        private sealed class ColumnSession
        {
            public string DocumentKey;
            public ElementId ViewId;
            public HashSet<long> TagIds;
            public XYZ Origin;
            public XYZ ColumnDir;
        }

        public string GetName()
        {
            return "revAgent tag tool handler";
        }

        public void Execute(UIApplication app)
        {
            HandlerMode mode = Mode;
            Mode = HandlerMode.None;
            string status;

            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null)
                {
                    status = "No active document.";
                }
                else
                {
                    switch (mode)
                    {
                        case HandlerMode.GetTags:
                            status = RunGetTags(uidoc);
                            break;
                        case HandlerMode.PlaceColumn:
                            status = RunPlaceColumn(uidoc);
                            break;
                        case HandlerMode.AutoLanes:
                            status = RunAutoLanes(uidoc);
                            break;
                        case HandlerMode.Relayout:
                            status = RunRelayout(uidoc);
                            break;
                        default:
                            status = null;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                status = "Selection cancelled.";
            }
            catch (Exception ex)
            {
                status = "Error: " + ex.Message;
            }

            if (status != null && Bridge != null)
            {
                Bridge.ReportStatus(status);
            }
        }

        /// <summary>Step 1: select tags. The palette gets the filter entries of that selection.</summary>
        private string RunGetTags(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            // Tags already selected with Revit's own selection tool need no Finish click.
            List<IndependentTag> tags = SelectedTags(uidoc, view);
            if (tags.Count == 0)
            {
                IList<Reference> refs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new TagSelectionFilter(),
                    "Select the tags to order (window selection is fine), then click Finish.");

                tags = refs
                    .Select(r => doc.GetElement(r.ElementId))
                    .OfType<IndependentTag>()
                    .ToList();
            }

            if (tags.Count == 0)
            {
                _pending = null;
                Bridge?.ReportSelection(new List<TagTypeFilter>(), 0);
                return "No tags in selection.";
            }

            _pending = new PendingSelection
            {
                DocumentKey = DocumentKey(doc),
                ViewId = view.Id,
                TagIds = tags.Select(t => t.Id).ToList(),
            };

            Bridge?.ReportSelection(CollectTagTypes(tags), tags.Count);
            return $"{tags.Count} tag(s) selected. Filter if needed, then Pick direction.";
        }

        /// <summary>
        /// Step 2: pick the column origin and an optional direction point, lay out
        /// the pending (filtered) tags, or the tags selected in the view, or move
        /// the last group when nothing is pending or selected.
        /// </summary>
        private string RunPlaceColumn(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;
            TagTypeFilter filter = Filter;

            List<IndependentTag> tags = PendingTags(doc, view);
            string source = "selection";
            if (tags.Count == 0)
            {
                tags = SelectedTags(uidoc, view);
            }

            if (tags.Count == 0)
            {
                ColumnSession last = LastSession(doc, view);
                if (last == null)
                {
                    return "Nothing to place. Use Get tags first.";
                }

                tags = SessionTags(doc, last);
                source = "last group";
            }

            if (filter != null)
            {
                tags = tags.Where(filter.Matches).ToList();
                if (tags.Count == 0)
                {
                    return "No selected tag matches the filter " + filter.Display + ".";
                }
            }

            XYZ origin = PickPointInTagPlane(uidoc, view, tags,
                "Click the column origin (the first tag lands there).");

            XYZ columnDir = null;
            if (PickDirectionPoint)
            {
                try
                {
                    XYZ second = PickPointInTagPlane(uidoc, view, tags,
                        "Click a point for the column direction, or press Esc for straight down.");
                    XYZ dir = second - origin;
                    if (dir.GetLength() > 1e-6)
                    {
                        columnDir = dir.Normalize();
                    }
                }
                catch (OperationCanceledException)
                {
                    columnDir = null;
                }
            }

            ColumnResult result = Layout(doc, view, tags, origin, columnDir);
            _lastOperation.Clear();
            _lastOperation.Add(RememberSession(doc, view, tags, origin, columnDir));

            _pending = null;
            Filter = null;
            Bridge?.ReportPlaced();
            return $"Tags count: {tags.Count}. {result} ({source})";
        }

        /// <summary>
        /// Auto lanes: the pending selection (filtered), else the tags selected in the
        /// view, else every tag of the view, laid out in perimeter columns and rows
        /// around their elements. Each lane is remembered as a group.
        /// </summary>
        private string RunAutoLanes(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;
            TagTypeFilter filter = Filter;

            string source = "selection";
            List<IndependentTag> tags = PendingTags(doc, view);
            if (tags.Count == 0)
            {
                tags = SelectedTags(uidoc, view);
            }

            if (tags.Count == 0)
            {
                tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                    .Where(t => !t.IsOrphaned).ToList();
                source = "whole view";
            }

            if (filter != null)
            {
                tags = tags.Where(filter.Matches).ToList();
            }

            if (tags.Count == 0)
            {
                return "No tags to lay out.";
            }

            TagOrderingService.PrepareTextMetrics(doc, tags);

            List<LaneResult> lanes;
            using (Transaction t = new Transaction(doc, "Auto lanes"))
            {
                t.Start();
                lanes = LaneLayoutService.AutoLanes(doc, view, tags, SpacingInternal(), ShiftInternal(), LanePerFamily);
                t.Commit();
            }

            _lastOperation.Clear();
            foreach (LaneResult lane in lanes)
            {
                _lastOperation.Add(RememberSession(doc, view, lane.Tags, lane.Origin, lane.Direction));
            }

            _pending = null;
            Filter = null;
            Bridge?.ReportPlaced();
            int placed = lanes.Sum(l => l.Result.Placed);
            int skipped = lanes.Sum(l => l.Result.Skipped);
            string detail = string.Join(", ", lanes.Select(l => l.Name + " " + l.Tags.Count));
            if (LanePerFamily)
            {
                detail = string.Join(", ", lanes.Select(l => (l.Family ?? "?") + " -> " + l.Name.Replace(" [" + (l.Family ?? "?") + "]", "") + " (" + l.Tags.Count + ")"));
            }
            return $"Tags count: {tags.Count}. Auto lanes ({source}): {lanes.Count} lanes, {placed} placed, {skipped} skipped. {detail}";
        }

        /// <summary>
        /// Live "Spacing x" / "Shift x". With tags selected in the view: every group that
        /// contains a selected tag is re-laid out in full; selected tags that belong to no
        /// group form a new group starting at their first tag. With nothing selected: every
        /// group of the last operation (one column, or all Auto lanes).
        /// </summary>
        private string RunRelayout(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;
            string key = DocumentKey(doc);

            List<IndependentTag> selected = SelectedTags(uidoc, view);
            var groups = new List<ColumnSession>();
            List<IndependentTag> loose = new List<IndependentTag>();
            if (selected.Count > 0)
            {
                foreach (IndependentTag tag in selected)
                {
                    ColumnSession session = null;
                    for (int i = _sessions.Count - 1; i >= 0; i--)
                    {
                        ColumnSession s = _sessions[i];
                        if (s.DocumentKey == key && s.ViewId == view.Id && s.TagIds.Contains(tag.Id.Value))
                        {
                            session = s;
                            break;
                        }
                    }

                    if (session == null)
                    {
                        loose.Add(tag);
                    }
                    else if (!groups.Contains(session))
                    {
                        groups.Add(session);
                    }
                }
            }
            else
            {
                groups.AddRange(_lastOperation.Where(s => s.DocumentKey == key && s.ViewId == view.Id));
                if (groups.Count == 0)
                {
                    ColumnSession last = LastSession(doc, view);
                    if (last != null)
                    {
                        groups.Add(last);
                    }
                }
            }

            if (groups.Count == 0 && loose.Count == 0)
            {
                return "Nothing to adjust yet: select tags in the view or place a column first.";
            }

            int placed = 0, skipped = 0, count = 0;
            foreach (ColumnSession session in groups)
            {
                List<IndependentTag> tags = SessionTags(doc, session);
                if (tags.Count == 0)
                {
                    _sessions.Remove(session);
                    continue;
                }

                ColumnResult result = Layout(doc, view, tags, session.Origin, session.ColumnDir);
                placed += result.Placed;
                skipped += result.Skipped;
                count += tags.Count;
            }

            if (loose.Count > 0)
            {
                XYZ origin = FirstHead(view, loose, null);
                if (origin != null)
                {
                    ColumnResult result = Layout(doc, view, loose, origin, null);
                    _lastOperation.Clear();
                    _lastOperation.Add(RememberSession(doc, view, loose, origin, null));
                    placed += result.Placed;
                    skipped += result.Skipped;
                    count += loose.Count;
                }
            }

            return $"Tags count: {count}. Adjusted {groups.Count + (loose.Count > 0 ? 1 : 0)} group(s): {placed} placed, {skipped} skipped.";
        }

        private ColumnResult Layout(Document doc, View view, IList<IndependentTag> tags, XYZ origin, XYZ columnDir)
        {
            // Family label geometry (EditFamily) must be read outside the transaction.
            TagOrderingService.PrepareTextMetrics(doc, tags);

            using (Transaction t = new Transaction(doc, TransactionName))
            {
                t.Start();
                ColumnResult result = TagOrderingService.PlaceColumn(doc, view, tags, origin, columnDir, SpacingInternal(), ShiftInternal());
                t.Commit();
                return result;
            }
        }

        // ----- selections and sessions -------------------------------------------------

        private List<IndependentTag> PendingTags(Document doc, View view)
        {
            if (_pending == null || _pending.DocumentKey != DocumentKey(doc) || _pending.ViewId != view.Id)
            {
                return new List<IndependentTag>();
            }

            return _pending.TagIds
                .Select(id => doc.GetElement(id))
                .OfType<IndependentTag>()
                .Where(tag => tag.IsValidObject)
                .ToList();
        }

        private static List<IndependentTag> SelectedTags(UIDocument uidoc, View view)
        {
            Document doc = uidoc.Document;
            return uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<IndependentTag>()
                .Where(tag => tag.IsValidObject && tag.OwnerViewId == view.Id)
                .ToList();
        }

        private List<IndependentTag> SessionTags(Document doc, ColumnSession session)
        {
            return session.TagIds
                .Select(id => doc.GetElement(new ElementId(id)))
                .OfType<IndependentTag>()
                .Where(tag => tag.IsValidObject)
                .ToList();
        }

        private ColumnSession LastSession(Document doc, View view)
        {
            string key = DocumentKey(doc);
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                if (_sessions[i].DocumentKey == key && _sessions[i].ViewId == view.Id)
                {
                    return _sessions[i];
                }
            }

            return null;
        }

        /// <summary>The most recent group that contains any of the given tags.</summary>
        private ColumnSession FindSession(Document doc, View view, IList<IndependentTag> tags)
        {
            string key = DocumentKey(doc);
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                ColumnSession s = _sessions[i];
                if (s.DocumentKey == key && s.ViewId == view.Id && tags.Any(t => s.TagIds.Contains(t.Id.Value)))
                {
                    return s;
                }
            }

            return null;
        }

        private ColumnSession RememberSession(Document doc, View view, IList<IndependentTag> tags, XYZ origin, XYZ columnDir)
        {
            var ids = new HashSet<long>(tags.Select(t => t.Id.Value));
            _sessions.RemoveAll(s => s.DocumentKey == DocumentKey(doc) && s.ViewId == view.Id && s.TagIds.SetEquals(ids));
            var session = new ColumnSession
            {
                DocumentKey = DocumentKey(doc),
                ViewId = view.Id,
                TagIds = ids,
                Origin = origin,
                ColumnDir = columnDir,
            };
            _sessions.Add(session);

            while (_sessions.Count > MaxSessions)
            {
                _sessions.RemoveAt(0);
            }

            return session;
        }

        /// <summary>Head of the tag nearest the start of the column axis; used as origin for never-placed selections.</summary>
        private static XYZ FirstHead(View view, IList<IndependentTag> tags, XYZ columnDir)
        {
            XYZ up = TagOrderingService.GetViewUp(view);
            XYZ right = TagOrderingService.GetViewRight(view, up);
            XYZ viewDir = TagOrderingService.GetViewDirection(view, up, right);
            XYZ axis = TagOrderingService.ProjectToViewPlane(columnDir, viewDir);
            if (axis == null || axis.GetLength() < 1e-9)
            {
                axis = up.Negate();
            }

            XYZ best = null;
            double bestA = double.MaxValue;
            foreach (IndependentTag tag in tags)
            {
                XYZ head;
                try
                {
                    head = tag.TagHeadPosition;
                }
                catch
                {
                    continue;
                }

                double a = head.DotProduct(axis);
                if (a < bestA)
                {
                    bestA = a;
                    best = head;
                }
            }

            return best;
        }

        /// <summary>Filter entries (category, and family when a category has several) for a set of tags.</summary>
        public static List<TagTypeFilter> CollectTagTypes(IList<IndependentTag> tags)
        {
            var result = new List<TagTypeFilter>();
            var byCategory = new Dictionary<long, TagTypeFilter>();
            var byFamily = new Dictionary<string, TagTypeFilter>();

            foreach (IndependentTag tag in tags)
            {
                Category category = tag.Category;
                if (category == null)
                {
                    continue;
                }

                if (!byCategory.TryGetValue(category.Id.Value, out TagTypeFilter cat))
                {
                    cat = new TagTypeFilter { Display = category.Name, CategoryId = category.Id };
                    byCategory[category.Id.Value] = cat;
                }

                cat.Count++;

                FamilySymbol symbol = tag.Document.GetElement(tag.GetTypeId()) as FamilySymbol;
                Family family = symbol?.Family;
                if (family == null)
                {
                    continue;
                }

                string key = category.Id.Value + "/" + family.Id.Value;
                if (!byFamily.TryGetValue(key, out TagTypeFilter fam))
                {
                    fam = new TagTypeFilter { Display = category.Name + " › " + family.Name, CategoryId = category.Id, FamilyId = family.Id };
                    byFamily[key] = fam;
                }

                fam.Count++;
            }

            foreach (TagTypeFilter cat in byCategory.Values.OrderBy(c => c.Display))
            {
                List<TagTypeFilter> families = byFamily.Values.Where(f => f.CategoryId == cat.CategoryId).OrderBy(f => f.Display).ToList();
                cat.Display = cat.Display + " (" + cat.Count + ")";
                result.Add(cat);
                if (families.Count > 1)
                {
                    foreach (TagTypeFilter fam in families)
                    {
                        fam.Display = fam.Display + " (" + fam.Count + ")";
                        result.Add(fam);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Picks a point in the tags' annotation plane. Plan and section views
        /// already have a work plane; a 3D view gets a temporary work plane
        /// through the tags, parallel to the screen, for the duration of the pick.
        /// </summary>
        private static XYZ PickPointInTagPlane(UIDocument uidoc, View view, IList<IndependentTag> tags, string prompt)
        {
            Document doc = uidoc.Document;

            if (view.ViewType != ViewType.ThreeD)
            {
                return uidoc.Selection.PickPoint(prompt);
            }

            XYZ planeOrigin = TagOrderingService.GetMeanHead(tags) ?? XYZ.Zero;
            XYZ up = TagOrderingService.GetViewUp(view);
            XYZ normal = TagOrderingService.GetViewDirection(view, up, TagOrderingService.GetViewRight(view, up));

            SketchPlane previous = null;
            try
            {
                previous = view.SketchPlane;
            }
            catch
            {
                // No work plane set.
            }

            ElementId tempPlaneId;
            using (Transaction t = new Transaction(doc, "Temporary work plane"))
            {
                t.Start();
                SketchPlane temp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, planeOrigin));
                view.SketchPlane = temp;
                tempPlaneId = temp.Id;
                t.Commit();
            }

            try
            {
                return uidoc.Selection.PickPoint(prompt);
            }
            finally
            {
                using (Transaction t = new Transaction(doc, "Remove temporary work plane"))
                {
                    t.Start();
                    try
                    {
                        if (previous != null && previous.IsValidObject)
                        {
                            view.SketchPlane = previous;
                        }

                        doc.Delete(tempPlaneId);
                    }
                    catch
                    {
                        // Leaving the plane behind is harmless.
                    }

                    t.Commit();
                }
            }
        }

        private double SpacingInternal()
        {
            return UnitUtils.ConvertToInternalUnits(SpacingMeters, UnitTypeId.Meters);
        }

        private double ShiftInternal()
        {
            return UnitUtils.ConvertToInternalUnits(ShiftMeters, UnitTypeId.Meters);
        }

        private static string DocumentKey(Document doc)
        {
            return (doc.PathName ?? "") + "|" + (doc.Title ?? "");
        }
    }

    /// <summary>
    /// Accepts only IndependentTag elements during PickObjects.
    /// </summary>
    public class TagSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is IndependentTag;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }
}
