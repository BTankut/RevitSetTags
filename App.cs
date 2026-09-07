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
                ToolTip = "Pick tags, pick a column origin, and stack the tags with equal spacing.",
                LongDescription =
                    "Recreates the 'Tags ordering' workflow: 'Get tags' picks IndependentTag elements and a " +
                    "column origin point; the first tag head lands on that point and every following tag one " +
                    "'Spacing x' further down the view (or along a direction picked with 'Pick direction'), ordered by their tagged " +
                    "elements so leaders do not cross. Every leader gets a shoulder of length 'Shift x' pointing " +
                    "towards the elements. Typing or stepping (-/+) either value re-lays out the last group live. " +
                    "Works in locked 3D, plan and section views. Values are entered in meters.",
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
