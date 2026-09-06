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
        NudgeUp,
        NudgeDown
    }

    /// <summary>
    /// Runs the window's actions on the Revit API context. The modeless window
    /// raises the external event; Revit calls Execute when the API is idle.
    /// Mirrors the demoed "ProEngineering Bim" palette: Get tags, Spacing (m),
    /// Shift (m) with Up/Down post-correction buttons.
    /// </summary>
    public class SetTagsHandler : IExternalEventHandler
    {
        public HandlerMode Mode { get; set; } = HandlerMode.None;
        public double SpacingMeters { get; set; } = 1.0;
        public double ShiftMeters { get; set; } = 1.0;
        public SetTagsWindow.WindowBridge Bridge { get; set; }

        public string GetName()
        {
            return "Set Tags Handler";
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
                        case HandlerMode.NudgeUp:
                            status = RunNudge(uidoc, +1);
                            break;
                        case HandlerMode.NudgeDown:
                            status = RunNudge(uidoc, -1);
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

        private string RunGetTags(UIDocument uidoc)
        {
            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new TagSelectionFilter(),
                "Select the tags to order, then click Finish (or press Esc to cancel).");

            List<IndependentTag> tags = refs
                .Select(r => uidoc.Document.GetElement(r.ElementId))
                .OfType<IndependentTag>()
                .ToList();

            if (tags.Count == 0)
            {
                return "No tags in selection.";
            }

            XYZ origin = uidoc.Selection.PickPoint(
                "Click the origin point of the tag column (top of the stack).");

            View view = uidoc.ActiveView;
            XYZ up = TagOrderingService.GetViewUp(view);
            double spacing = UnitUtils.ConvertToInternalUnits(SpacingMeters, UnitTypeId.Meters);

            using (Transaction t = new Transaction(uidoc.Document, "Order Tags"))
            {
                t.Start();
                string result = TagOrderingService.PlaceColumn(uidoc.Document, tags, origin, spacing, up);
                t.Commit();

                return $"Tags count: {tags.Count}. {result}";
            }
        }

        private string RunNudge(UIDocument uidoc, int sign)
        {
            List<IndependentTag> tags = uidoc.Selection.GetElementIds()
                .Select(id => uidoc.Document.GetElement(id))
                .OfType<IndependentTag>()
                .ToList();

            if (tags.Count == 0)
            {
                return "Select one or more tags in the view, then use Up/Down.";
            }

            View view = uidoc.ActiveView;
            XYZ up = TagOrderingService.GetViewUp(view);
            double shift = UnitUtils.ConvertToInternalUnits(ShiftMeters, UnitTypeId.Meters);
            XYZ delta = up * (shift * sign);

            using (Transaction t = new Transaction(uidoc.Document, "Shift Tags"))
            {
                t.Start();
                string result = TagOrderingService.Nudge(uidoc.Document, tags, delta);
                t.Commit();
                return result;
            }
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
