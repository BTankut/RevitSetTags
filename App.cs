using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitSetTags
{
    /// <summary>
    /// Creates the "Set Tags" ribbon button on startup.
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "Pro Tools";
            const string panelName = "Tag Tools";

            try { application.CreateRibbonTab(tabName); }
            catch (System.ArgumentException) { /* tab already exists */ }

            RibbonPanel panel = application.CreateRibbonPanel(tabName, panelName);

            string dllPath = Assembly.GetExecutingAssembly().Location;

            var setTagsButton = new PushButtonData(
                "RevitSetTags_SetTags",
                "Set\nTags",
                dllPath,
                "RevitSetTags.Commands.ShowSetTagsCommand")
            {
                ToolTip = "Pick tags and align them in an ordered column.",
                LongDescription =
                    "Reorders the selected IndependentTag elements: the first tag head is placed at the " +
                    "start position and every following tag is shifted by the shift vector. " +
                    "Leaders keep pointing to the tagged elements. Works in 3D, plan and section views. " +
                    "Values are entered in meters.",
            };

            panel.AddItem(setTagsButton);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
