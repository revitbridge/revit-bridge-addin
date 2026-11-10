using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.UI;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class Settings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SettingsWindow window = new SettingsWindow();
            // Revit's own main window as the owner. Process.MainWindowHandle is a guess (the first
            // visible unowned window); on Revit 2021 it is often a WPF popup of the ribbon, and an
            // owned window is destroyed with its owner, so Settings closed by itself seconds later.
            _ = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle
            };
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            window.Show();

            return Result.Succeeded;
        }
    }
}
