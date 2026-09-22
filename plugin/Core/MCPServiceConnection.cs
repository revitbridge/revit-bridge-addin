using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using Newtonsoft.Json;
using System;
using System.IO;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ServiceSettings settings = LoadSettings();
                bool useWebSocket = string.Equals(settings.Mode, "websocket",
                    StringComparison.OrdinalIgnoreCase);

                if (useWebSocket)
                {
                    return HandleWebSocket(commandData, settings);
                }
                else
                {
                    return HandleTcp(commandData);
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private Result HandleTcp(ExternalCommandData commandData)
        {
            // Stop WebSocket if it was running (mode switch)
            if (WebSocketService.Instance.IsRunning)
                WebSocketService.Instance.Stop();

            SocketService service = SocketService.Instance;

            if (service.IsRunning)
            {
                service.Stop();
                TaskDialog.Show("revitMCP", "TCP Server Closed");
            }
            else
            {
                service.Initialize(commandData.Application);
                service.Start();
                TaskDialog.Show("revitMCP", $"TCP Server Started (port {service.Port})");
            }

            return Result.Succeeded;
        }

        private Result HandleWebSocket(ExternalCommandData commandData, ServiceSettings settings)
        {
            // Stop TCP if it was running (mode switch)
            if (SocketService.Instance.IsRunning)
                SocketService.Instance.Stop();

            WebSocketService service = WebSocketService.Instance;

            if (service.IsRunning)
            {
                service.Stop();
                TaskDialog.Show("revitMCP", "WebSocket Disconnected");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(settings.WsUrl))
                {
                    TaskDialog.Show("revitMCP",
                        "No WebSocket server URL configured.\n" +
                        "Set 'wsUrl' in Commands\\commandRegistry.json (installer -Server) " +
                        "or in Settings > Connection, then click the switch again.");
                    return Result.Failed;
                }

                if (!settings.IsPaired)
                {
                    TaskDialog.Show("revitMCP",
                        "This Revit is not paired with a bridge server.\n" +
                        "Open Settings > Connection and enter a pairing code, " +
                        "or re-run the installer with -Mode remote -Pair <code>.");
                    return Result.Failed;
                }

                // 服务器已吊销/不认识这个 deviceId（close 4003）。配置里还是同一个设备时
                // 直接提示，不再连；用户重新配对后 deviceId 变化，即可再连。
                // The server refused this deviceId with close 4003. While the config
                // still holds that same device, say so instead of reconnecting; after
                // pairing again the deviceId differs and the switch works normally.
                if (service.IsUnpaired &&
                    string.Equals(service.UnpairedDeviceId, settings.DeviceId, StringComparison.Ordinal))
                {
                    TaskDialog.Show("revitMCP",
                        $"Unpaired or revoked: the server refused device {settings.DeviceId}.\n" +
                        "Pair this Revit again in Settings > Connection with a new pairing code.");
                    return Result.Failed;
                }

                service.Initialize(commandData.Application);
                service.Start(settings.WsUrl, settings.DeviceId);
                TaskDialog.Show("revitMCP",
                    $"WebSocket connection started\nServer: {settings.WsUrl}\n" +
                    $"Device: {settings.DeviceId}\nCheck Settings > Connection for live status.");
            }

            return Result.Succeeded;
        }

        private ServiceSettings LoadSettings()
        {
            try
            {
                string configPath = PathManager.GetCommandRegistryFilePath();
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    if (config?.Settings != null)
                        return config.Settings;
                }
            }
            catch
            {
                // Fall through to defaults
            }

            return new ServiceSettings();
        }
    }
}
