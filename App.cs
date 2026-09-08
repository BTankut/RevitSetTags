using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitSetTags
{
    /// <summary>
    /// Adds the "revAgent tag tool" panel and its button to the Add-Ins tab on startup.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string AddInName = "revAgent tag tool";

        public Result OnStartup(UIControlledApplication application)
        {
            RibbonPanel panel = application.CreateRibbonPanel(AddInName);

            string dllPath = Assembly.GetExecutingAssembly().Location;

            var button = new PushButtonData(
                "RevAgentTagTool_Open",
                "Tag\nTool",
                dllPath,
                "RevitSetTags.Commands.ShowSetTagsCommand")
            {
                ToolTip = "revAgent tag tool: order tags in columns and rows with clean leaders.",
                LongDescription =
                    "Get tags reads the tags you selected; an optional filter keeps one tag family; " +
                    "Pick direction places them in a column from the point you click (right-click or " +
                    "Ctrl+click adds a direction point); Auto lanes puts every tag around the elements " +
                    "in perimeter columns and rows, optionally one lane per tag family. Spacing x and " +
                    "Shift x (meters) adjust the last groups live. Works in locked 3D, plan and section views.",
                LargeImage = LoadIcon("tagtool-32.png"),
                Image = LoadIcon("tagtool-16.png"),
            };

            panel.AddItem(button);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static BitmapImage LoadIcon(string fileName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = null;
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = name;
                        break;
                    }
                }

                if (resourceName == null)
                {
                    return null;
                }

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null; // the button still works without an icon
            }
        }
    }
}
