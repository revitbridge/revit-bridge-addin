using System;
using Autodesk.Revit.UI;
using System.Reflection;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // 面板名与按钮名是自动测试与日志里按字符串找的标识，不改。
            // The panel and button names are looked up by string in automated tests and
            // journals ("CustomCtrl_%CustomCtrl_%Add-Ins%Revit MCP Plugin%ID_EXCMD_TOGGLE_REVIT_MCP"); keep them.
            RibbonPanel mcpPanel = application.CreateRibbonPanel(BridgeController.RibbonPanelName);

            PushButtonData pushButtonData = new PushButtonData(BridgeController.ButtonName, BridgeController.OffText,
                Assembly.GetExecutingAssembly().Location, typeof(MCPServiceConnection).FullName);
            pushButtonData.AvailabilityClassName = typeof(BridgeButtonAvailability).FullName;
            pushButtonData.ToolTip = "Revit Bridge: Off\nClick to turn the bridge on. Shift+click opens the Revit Bridge panel.";
            pushButtonData.Image = BridgeController.LoadImage("/RevitMCPPlugin;component/Core/Ressources/icon-16.png");
            pushButtonData.LargeImage = BridgeController.LoadImage("/RevitMCPPlugin;component/Core/Ressources/icon-32.png");
            PushButton button = mcpPanel.AddItem(pushButtonData) as PushButton;

            try
            {
                BridgeController.Initialize(button);
            }
            catch (Exception ex)
            {
                // The button still works from its click; only the panel's switch and the live
                // state need the external event.
                System.Diagnostics.Trace.WriteLine(
                    $"初始化开关状态失败 / Failed to initialise the bridge switch: {ex}");
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                    SocketService.Instance.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"停止 SocketService 失败 / Failed to stop SocketService: {ex.Message}");
            }

            try
            {
                if (WebSocketService.Instance.IsRunning)
                    WebSocketService.Instance.Stop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"停止 WebSocketService 失败 / Failed to stop WebSocketService: {ex.Message}");
            }

            return Result.Succeeded;
        }
    }
}
