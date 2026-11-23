using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.UI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media.Imaging;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>功能区唯一的按钮（ID_EXCMD_TOGGLE_REVIT_MCP）：单击开关桥接，Shift+单击打开 Revit Bridge 面板。
    /// 开关成功不弹框，状态看按钮本身（文字、图标、提示）；启动失败打开一次非模态面板说明原因。</para>
    /// <para>The one ribbon button (ID_EXCMD_TOGGLE_REVIT_MCP): a click turns the bridge on or
    /// off, Shift+click opens the Revit Bridge panel. Switching shows no dialog; the button's
    /// text, icon and tooltip show the state. A failed start opens the modeless panel once,
    /// with the reason. Automated sessions invoke this button and must never be stopped by a
    /// dialog on success.</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (BridgeController.IsShiftDown())
                {
                    BridgePanel.ShowPanel(commandData.Application.MainWindowHandle);
                    return Result.Succeeded;
                }

                // Cancelled when nothing changed (a failed start, reported once already):
                // Revit then shows nothing more.
                return BridgeController.Toggle(commandData.Application, BridgeOrigin.Ribbon)
                    ? Result.Succeeded
                    : Result.Cancelled;
            }
            catch (Exception ex)
            {
                // Revit shows this one message; nothing else was shown for it.
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// <para>按钮在没有打开文档时也可用：开关桥接与打开面板都不需要文档。</para>
    /// <para>The button stays available with no document open: switching the bridge and
    /// opening the panel need no document.</para>
    /// </summary>
    public class BridgeButtonAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }

    /// <summary>Who asked for a start: decides how a failure is reported.</summary>
    public enum BridgeOrigin
    {
        /// <summary>The ribbon button. Pairing prompts are TaskDialogs as before; a failed start opens the panel.</summary>
        Ribbon,
        /// <summary>The panel's switch. Everything is reported in the panel's status line.</summary>
        Panel,
        /// <summary>"Turn the bridge on when Revit starts". No modal dialog; a failure opens the panel.</summary>
        AutoStart,
    }

    /// <summary>Work the panel and the WebSocket thread hand to the Revit UI thread.</summary>
    public enum BridgeRequest
    {
        TurnOn,
        TurnOff,
        ReloadCommands,
        RefreshRibbon,
    }

    /// <summary>
    /// <para>桥接的开关、状态与功能区按钮。所有 Revit API 调用（服务初始化、命令加载、按钮更新）都在
    /// Revit UI 线程的 API 上下文中：按钮命令、启动事件，或本类的外部事件。面板与 WebSocket 线程只经
    /// <see cref="Post"/> 排队，不直接调 Revit API。</para>
    /// <para>Switching, state and the ribbon button. Every Revit API call (service
    /// initialisation, command loading, ribbon updates) runs on the Revit UI thread in an API
    /// context: the button's command, the start-up event, or this class's external event. The
    /// panel and the WebSocket thread only queue work through <see cref="Post"/>.</para>
    /// </summary>
    public static class BridgeController
    {
        // Names resolved at run time by automated tests and journals; do not rename.
        public const string RibbonPanelName = "Revit MCP Plugin";
        public const string ButtonName = "ID_EXCMD_TOGGLE_REVIT_MCP";

        public const string OnText = "Revit Bridge\r\nOn";
        public const string OffText = "Revit Bridge\r\nOff";

        private const string IconOff16 = "/RevitMCPPlugin;component/Core/Ressources/icon-16.png";
        private const string IconOff32 = "/RevitMCPPlugin;component/Core/Ressources/icon-32.png";
        private const string IconOn16 = "/RevitMCPPlugin;component/Core/Ressources/icon-on-16.png";
        private const string IconOn32 = "/RevitMCPPlugin;component/Core/Ressources/icon-on-32.png";

        private static PushButton _button;
        private static ExternalEvent _event;
        private static BitmapImage _off16, _off32, _on16, _on32;

        private static readonly object QueueLock = new object();
        private static readonly Queue<BridgeRequest> Pending = new Queue<BridgeRequest>();

        private static volatile string _lastError;
        private static volatile ServiceSettings _settings = new ServiceSettings();
        private static volatile string _settingsError;

        /// <summary>Why the last start did not happen, or null.</summary>
        public static string LastError => _lastError;

        public static bool IsOn => SocketService.Instance.IsRunning || WebSocketService.Instance.IsRunning;

        /// <summary>Whether the panel can hand work to the UI thread.</summary>
        public static bool CanPost => _event != null;

        /// <summary>A switch or reload handed over that Revit has not run yet (it runs when Revit is idle).</summary>
        public static bool HasPendingWork
        {
            get
            {
                lock (QueueLock)
                {
                    foreach (BridgeRequest request in Pending)
                    {
                        if (request != BridgeRequest.RefreshRibbon)
                            return true;
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// <para>OnStartup（API 上下文）调用：记下按钮、建外部事件、订阅 WebSocket 状态。</para>
        /// <para>Called from OnStartup (API context): keep the button, create the external
        /// event, follow the WebSocket state.</para>
        /// </summary>
        internal static void Initialize(PushButton button)
        {
            _button = button;
            _off16 = LoadImage(IconOff16);
            _off32 = LoadImage(IconOff32);
            _on16 = LoadImage(IconOn16);
            _on32 = LoadImage(IconOn32);
            _event = ExternalEvent.Create(new BridgeEventHandler());
            WebSocketService.Instance.StateChanged += () => Post(BridgeRequest.RefreshRibbon);
            ReloadSettingsSnapshot();
            RefreshRibbon();
        }

        public static bool IsShiftDown()
        {
            return (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift)
                   == System.Windows.Forms.Keys.Shift;
        }

        // ── Switching (Revit UI thread, API context) ────────────────────

        /// <summary>Turn the bridge off when on, on when off. True when the state changed.</summary>
        public static bool Toggle(UIApplication uiApp, BridgeOrigin origin)
        {
            if (IsOn)
            {
                TurnOff();
                return true;
            }
            return TurnOn(uiApp, origin);
        }

        /// <summary>
        /// <para>按配置的模式启动。成功不弹框；失败记入 <see cref="LastError"/> 并按来源报告一次。</para>
        /// <para>Start in the configured mode. Success shows nothing; a failure goes to
        /// <see cref="LastError"/> and is reported once, depending on the origin.</para>
        /// </summary>
        public static bool TurnOn(UIApplication uiApp, BridgeOrigin origin)
        {
            _lastError = null;
            ServiceSettings settings = ReloadSettingsSnapshot();
            bool started;
            try
            {
                started = IsRemote(settings)
                    ? StartRemote(uiApp, settings, origin)
                    : StartLocal(uiApp, origin);
            }
            catch (Exception ex)
            {
                // The caller reports it (the command's message, the panel's status line).
                _lastError = "Could not turn the bridge on: " + ex.Message;
                throw;
            }
            finally
            {
                RefreshRibbon();
            }
            return started;
        }

        public static void TurnOff()
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                    SocketService.Instance.Stop();
                if (WebSocketService.Instance.IsRunning)
                    WebSocketService.Instance.Stop();
                _lastError = null;
            }
            finally
            {
                RefreshRibbon();
            }
        }

        private static bool StartLocal(UIApplication uiApp, BridgeOrigin origin)
        {
            // Mode switch: stop the WebSocket client if it was running.
            if (WebSocketService.Instance.IsRunning)
                WebSocketService.Instance.Stop();

            SocketService service = SocketService.Instance;
            service.Initialize(uiApp);
            service.Start();
            if (service.IsRunning)
                return true;

            _lastError = "Could not start the local bridge: " + (service.LastStartError ?? "unknown reason.");
            ReportFailure(uiApp, origin, _lastError);
            return false;
        }

        private static bool StartRemote(UIApplication uiApp, ServiceSettings settings, BridgeOrigin origin)
        {
            // Mode switch: stop the TCP server if it was running.
            if (SocketService.Instance.IsRunning)
                SocketService.Instance.Stop();

            WebSocketService service = WebSocketService.Instance;

            // 远程模式的配对提示照旧（从功能区开时是 TaskDialog）。
            // The remote-mode pairing prompts stay as they were (TaskDialogs from the ribbon).
            string problem = null;
            if (string.IsNullOrWhiteSpace(settings.WsUrl))
            {
                problem = "No WebSocket server URL configured.\n" +
                          "Set 'wsUrl' in Commands\\commandRegistry.json (installer -Server) " +
                          "or pair this Revit in the Revit Bridge panel (Shift+click the Revit Bridge button), " +
                          "then click the button again.";
            }
            else if (!settings.IsPaired)
            {
                problem = "This Revit is not paired with a bridge server.\n" +
                          "Open the Revit Bridge panel (Shift+click the Revit Bridge button) and enter a pairing code, " +
                          "or re-run the installer with -Mode remote -Pair <code>.";
            }
            // 服务器已吊销/不认识这个 deviceId（close 4003）。配置里还是同一个设备时
            // 直接提示，不再连；用户重新配对后 deviceId 变化，即可再连。
            // The server refused this deviceId with close 4003. While the config
            // still holds that same device, say so instead of reconnecting; after
            // pairing again the deviceId differs and the switch works normally.
            else if (service.IsUnpaired &&
                     string.Equals(service.UnpairedDeviceId, settings.DeviceId, StringComparison.Ordinal))
            {
                problem = $"Unpaired or revoked: the server refused device {settings.DeviceId}.\n" +
                          "Pair this Revit again in the Revit Bridge panel (Shift+click the Revit Bridge button) " +
                          "with a new pairing code.";
            }

            if (problem != null)
            {
                _lastError = problem.Replace("\n", " ");
                if (origin == BridgeOrigin.Ribbon)
                    TaskDialog.Show("revitMCP", problem);
                else if (origin == BridgeOrigin.AutoStart)
                    ShowPanel(uiApp, fallbackDialog: false, _lastError);
                return false;
            }

            service.Initialize(uiApp);
            service.Start(settings.WsUrl, settings.DeviceId);
            return true;
        }

        // A failed start is reported once: the panel says why (non-modal). From the panel
        // itself, its status line already does.
        private static void ReportFailure(UIApplication uiApp, BridgeOrigin origin, string reason)
        {
            if (origin == BridgeOrigin.Panel)
                return;
            ShowPanel(uiApp, fallbackDialog: origin == BridgeOrigin.Ribbon, reason);
        }

        private static void ShowPanel(UIApplication uiApp, bool fallbackDialog, string reason)
        {
            try
            {
                BridgePanel.ShowPanel(uiApp.MainWindowHandle);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge: could not open the panel: {ex}");
                if (fallbackDialog)
                    TaskDialog.Show("revitMCP", reason);
            }
        }

        /// <summary>"Turn the bridge on when Revit starts": called once from ApplicationInitialized.</summary>
        public static void AutoStart(UIApplication uiApp)
        {
            ServiceSettings settings = ReloadSettingsSnapshot();
            if (!settings.AutoStart || IsOn)
                return;
            try
            {
                TurnOn(uiApp, BridgeOrigin.AutoStart);
            }
            catch (Exception ex)
            {
                _lastError = "Could not turn the bridge on at start-up: " + ex.Message;
                System.Diagnostics.Trace.WriteLine($"revit-bridge: auto start failed: {ex}");
                RefreshRibbon();
                ShowPanel(uiApp, fallbackDialog: false, _lastError);
            }
        }

        /// <summary>Rebuild the running service's command registry (API context).</summary>
        public static void ReloadCommands()
        {
            if (SocketService.Instance.IsRunning)
                SocketService.Instance.ReloadCommands();
            if (WebSocketService.Instance.IsRunning)
                WebSocketService.Instance.ReloadCommands();
        }

        // ── From the panel or other threads ─────────────────────────────

        /// <summary>
        /// <para>把工作交给 Revit UI 线程（外部事件）。任何线程都可调用。</para>
        /// <para>Hand work to the Revit UI thread through the external event. Any thread.</para>
        /// </summary>
        /// <returns>false when the external event is not available.</returns>
        public static bool Post(BridgeRequest request)
        {
            ExternalEvent externalEvent = _event;
            if (externalEvent == null)
                return false;
            lock (QueueLock)
            {
                // Every run ends with a ribbon refresh, so a refresh behind other work is not queued twice.
                if (request != BridgeRequest.RefreshRibbon || Pending.Count == 0)
                    Pending.Enqueue(request);
            }
            try
            {
                ExternalEventRequest accepted = externalEvent.Raise();
                if (accepted == ExternalEventRequest.Denied || accepted == ExternalEventRequest.TimedOut)
                    System.Diagnostics.Trace.WriteLine($"revit-bridge: external event {accepted}; request {request} waits for the next one.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge: could not raise the external event: {ex.Message}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// <para>面板改了"允许接入我的工具"（已写入配置）：立刻生效于服务端判定；运行中则重建命令注册表。</para>
        /// <para>The panel changed "Allow my tools" (already saved): the service-side check
        /// follows at once; a running bridge also rebuilds its command registry.</para>
        /// </summary>
        public static void SetUserToolsAllowed(bool allowed)
        {
            UserToolAccess.Allowed = allowed;
            ReloadSettingsSnapshot();
            Post(IsOn ? BridgeRequest.ReloadCommands : BridgeRequest.RefreshRibbon);
        }

        /// <summary>The panel saved settings: refresh what the button shows.</summary>
        public static void SettingsChanged()
        {
            ReloadSettingsSnapshot();
            Post(BridgeRequest.RefreshRibbon);
        }

        // Runs in the external event (Revit UI thread, API context).
        internal static void RunPending(UIApplication app)
        {
            List<BridgeRequest> work;
            lock (QueueLock)
            {
                work = new List<BridgeRequest>(Pending);
                Pending.Clear();
            }

            foreach (BridgeRequest request in work)
            {
                try
                {
                    switch (request)
                    {
                        case BridgeRequest.TurnOn:
                            if (!IsOn)
                                TurnOn(app, BridgeOrigin.Panel);
                            break;
                        case BridgeRequest.TurnOff:
                            if (IsOn)
                                TurnOff();
                            break;
                        case BridgeRequest.ReloadCommands:
                            ReloadCommands();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // TurnOn has already put its own reason there.
                    if (request != BridgeRequest.TurnOn || _lastError == null)
                        _lastError = $"{request} failed: {ex.Message}";
                    System.Diagnostics.Trace.WriteLine($"revit-bridge: {request} failed: {ex}");
                }
            }
            RefreshRibbon();
        }

        // ── State ───────────────────────────────────────────────────────

        private static bool IsRemote(ServiceSettings settings)
        {
            return string.Equals(settings?.Mode, "websocket", StringComparison.OrdinalIgnoreCase);
        }

        private static ServiceSettings ReloadSettingsSnapshot()
        {
            string error;
            FrameworkConfig config = SettingsStore.LoadOrDefaults(out error);
            _settings = config.Settings;
            _settingsError = error;
            return config.Settings;
        }

        /// <summary>What the button and the panel show, read without any Revit API.</summary>
        public static BridgeStatus CurrentStatus()
        {
            SocketService tcp = SocketService.Instance;
            WebSocketService ws = WebSocketService.Instance;
            ServiceSettings settings = _settings ?? new ServiceSettings();
            bool localRunning = tcp.IsRunning;
            bool remoteRunning = ws.IsRunning;
            return new BridgeStatus
            {
                LocalRunning = localRunning,
                LocalPort = localRunning ? tcp.Port : settings.Port,
                RemoteRunning = remoteRunning,
                RemoteConnected = remoteRunning && ws.IsConnected,
                RemoteUrl = remoteRunning ? ws.ServerUrl : settings.WsUrl,
                DeviceId = remoteRunning ? ws.DeviceId : settings.DeviceId,
                RemoteError = ws.LastConnectionError,
                RemoteUnpaired = ws.IsUnpaired,
                UnpairedDeviceId = ws.UnpairedDeviceId,
                Remote = remoteRunning || (!localRunning && IsRemote(settings)),
                // Running: what the services check now. Off: what the next start will use.
                UserToolsAllowed = localRunning || remoteRunning ? UserToolAccess.Allowed : settings.LocalToolsEnabled,
                MethodCount = localRunning ? tcp.RegisteredMethodCount : remoteRunning ? ws.RegisteredMethodCount : 0,
                ConfigLoadError = localRunning ? tcp.ConfigLoadError : remoteRunning ? ws.ConfigLoadError : null,
                SettingsError = _settingsError,
                LastError = _lastError,
            };
        }

        /// <summary>Update the button's text, icon and tooltip. Revit UI thread, API context.</summary>
        internal static void RefreshRibbon()
        {
            PushButton button = _button;
            if (button == null)
                return;
            try
            {
                BridgeStatus status = CurrentStatus();
                button.ItemText = status.IsOn ? OnText : OffText;
                if (_on16 != null && _off16 != null)
                    button.Image = status.IsOn ? _on16 : _off16;
                if (_on32 != null && _off32 != null)
                    button.LargeImage = status.IsOn ? _on32 : _off32;
                button.ToolTip = BridgeStatusText.Tooltip(status);
            }
            catch (Exception ex)
            {
                // A ribbon that cannot be updated must not take the request with it.
                System.Diagnostics.Trace.WriteLine($"revit-bridge: could not update the ribbon button: {ex.Message}");
            }
        }

        internal static BitmapImage LoadImage(string packUri)
        {
            try
            {
                var image = new BitmapImage(new Uri(packUri, UriKind.RelativeOrAbsolute));
                if (image.CanFreeze)
                    image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge: could not load {packUri}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>The external event behind <see cref="BridgeController.Post"/>.</summary>
    internal sealed class BridgeEventHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try
            {
                BridgeController.RunPending(app);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge: control event failed: {ex}");
            }
        }

        public string GetName()
        {
            return "revit-bridge control";
        }
    }

    /// <summary>A snapshot of the bridge's state; no Revit types, so it can be checked offline.</summary>
    public sealed class BridgeStatus
    {
        public bool LocalRunning { get; set; }
        public int LocalPort { get; set; }
        public bool RemoteRunning { get; set; }
        public bool RemoteConnected { get; set; }
        public string RemoteUrl { get; set; }
        public string DeviceId { get; set; }
        public string RemoteError { get; set; }
        public bool RemoteUnpaired { get; set; }
        public string UnpairedDeviceId { get; set; }
        /// <summary>Remote mode: running remote, or off with remote configured.</summary>
        public bool Remote { get; set; }
        public bool UserToolsAllowed { get; set; }
        public int MethodCount { get; set; }
        public string ConfigLoadError { get; set; }
        public string SettingsError { get; set; }
        public string LastError { get; set; }

        public bool IsOn => LocalRunning || RemoteRunning;
    }

    /// <summary>The texts of the status line and the tooltip, from a <see cref="BridgeStatus"/>.</summary>
    public static class BridgeStatusText
    {
        public static string StatusLine(BridgeStatus s)
        {
            var text = new StringBuilder();
            if (s.LocalRunning)
            {
                text.Append($"On - local (this computer), port {s.LocalPort}, {s.MethodCount} methods.");
            }
            else if (s.RemoteRunning)
            {
                if (s.RemoteConnected)
                    text.Append($"On - remote, connected to {s.RemoteUrl} as {s.DeviceId}.");
                else if (!string.IsNullOrWhiteSpace(s.RemoteError))
                    text.Append($"On - remote, reconnecting to {s.RemoteUrl} as {s.DeviceId}. Last error: {s.RemoteError}");
                else
                    text.Append($"On - remote, connecting to {s.RemoteUrl} as {s.DeviceId}...");
            }
            else if (!string.IsNullOrWhiteSpace(s.LastError))
            {
                text.Append("Off - ").Append(s.LastError);
            }
            else if (s.RemoteUnpaired)
            {
                text.Append($"Off - unpaired or revoked: the server refused device {s.UnpairedDeviceId}. Enter a new pairing code below.");
            }
            else
            {
                text.Append(s.Remote ? "Off - remote mode." : $"Off - local mode, port {s.LocalPort}.");
            }

            if (s.IsOn && !string.IsNullOrWhiteSpace(s.ConfigLoadError))
                text.Append($" Warning: {s.ConfigLoadError}, so no commands were loaded.");
            return text.ToString();
        }

        public static string Tooltip(BridgeStatus s)
        {
            var text = new StringBuilder();
            text.AppendLine(s.IsOn ? "Revit Bridge: On" : "Revit Bridge: Off");
            if (s.Remote)
            {
                string server = string.IsNullOrWhiteSpace(s.RemoteUrl) ? "(no server set)" : s.RemoteUrl;
                text.AppendLine($"Mode: remote, server {server}");
                if (s.RemoteRunning)
                    text.AppendLine(s.RemoteConnected
                        ? $"Connected as {s.DeviceId}"
                        : string.IsNullOrWhiteSpace(s.RemoteError) ? "Connecting..." : $"Reconnecting. Last error: {s.RemoteError}");
            }
            else
            {
                text.AppendLine($"Mode: local (this computer), port {s.LocalPort}");
            }
            text.AppendLine(s.UserToolsAllowed ? "Allow my tools: on" : "Allow my tools: off");
            if (!s.IsOn && !string.IsNullOrWhiteSpace(s.LastError))
                text.AppendLine(s.LastError);
            else if (!s.IsOn && s.RemoteUnpaired)
                text.AppendLine($"Unpaired or revoked: the server refused device {s.UnpairedDeviceId}.");
            if (s.IsOn && !string.IsNullOrWhiteSpace(s.ConfigLoadError))
                text.AppendLine($"Warning: {s.ConfigLoadError}, so no commands were loaded.");
            if (!string.IsNullOrWhiteSpace(s.SettingsError))
                text.AppendLine($"Settings: {s.SettingsError}; the defaults are shown.");
            text.Append(s.IsOn
                ? "Click to turn the bridge off. Shift+click opens the Revit Bridge panel."
                : "Click to turn the bridge on. Shift+click opens the Revit Bridge panel.");
            return text.ToString();
        }
    }
}
