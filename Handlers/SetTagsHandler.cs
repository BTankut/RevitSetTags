using System;
using System.Collections.Generic;
using System.Globalization;
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
        PickElements,
        SetTags,
        ResetTags
    }

    /// <summary>
    /// Runs the window's actions on the Revit API context. The modeless window
    /// raises the external event; Revit calls Execute when the API is idle.
    /// </summary>
    public class SetTagsHandler : IExternalEventHandler
    {
        public HandlerMode Mode { get; set; } = HandlerMode.None;
        public XYZ Start { get; set; }
        public XYZ Shift { get; set; }
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
                        case HandlerMode.PickElements:
                            status = PickElements(uidoc);
                            break;
                        case HandlerMode.SetTags:
                            status = RunSetTags(uidoc.Document);
                            break;
                        case HandlerMode.ResetTags:
                            status = RunReset(uidoc.Document);
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

        private string PickElements(UIDocument uidoc)
        {
            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new TagSelectionFilter(),
                "Select the tags to order, then click Finish (or press Esc to cancel).");

            List<ElementId> picked = refs
                .Select(r => r.ElementId)
                .ToList();

            TagStore.AddTagIds(uidoc.Document, picked);
            return TagStore.Describe(uidoc.Document);
        }

        private string RunSetTags(Document doc)
        {
            if (Start == null || Shift == null)
            {
                return "Start position and shift are required.";
            }

            List<IndependentTag> tags = TagStore.GetTags(doc);
            if (tags.Count == 0)
            {
                return "No tags selected. Click 'Pick elements' first.";
            }

            using (Transaction t = new Transaction(doc, "Set Tags"))
            {
                t.Start();
                string result = TagOrderingService.SetTags(doc, tags, Start, Shift);
                t.Commit();
                return result;
            }
        }

        private string RunReset(Document doc)
        {
            List<IndependentTag> tags = TagStore.GetTags(doc);
            if (tags.Count == 0)
            {
                return "No tags selected. Click 'Pick elements' first.";
            }

            using (Transaction t = new Transaction(doc, "Reset Tags"))
            {
                t.Start();
                string result = TagOrderingService.ResetTags(doc, tags);
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
