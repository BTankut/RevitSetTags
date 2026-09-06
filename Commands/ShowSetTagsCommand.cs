using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitSetTags.Commands
{
    /// <summary>
    /// Shows (or activates) the modeless "Set Tags" window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ShowSetTagsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UI.SetTagsWindow.ShowOrActivate(commandData.Application);
            return Result.Succeeded;
        }
    }
}
