using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// WebSocket client service — connects TO the cloud server as a named slot.
    /// Receives JSON-RPC 2.0 requests from the server, executes them in Revit,
    /// and sends responses back through the same WebSocket.
    ///
    /// This is the reverse of SocketService (TCP):
    ///   TCP:       Server connects TO plugin (plugin = server)
    ///   WebSocket: Plugin connects TO server (plugin = client)
    ///
    /// JSON-RPC protocol is identical — only the transport layer changes.
    /// </summary>
    public class WebSocketService
    {
        private static WebSocketService _instance;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private Thread _workerThread;
        private bool _isRunning;
        private volatile string _lastConnectionError;

        private string _serverUrl;
        private string _deviceId;
        private int _reconnectDelayMs = 5000;

        // 服务器以 close 4003 拒绝本设备（未配对 / 已吊销）：停止重连，记录当时的 deviceId。
        // Server refused this device with close 4003 (unpaired / revoked): stop
        // reconnecting and remember which deviceId was refused.
        private volatile bool _isUnpaired;
        private volatile string _unpairedDeviceId;

        private UIApplication _uiApp;
        // Every command load fills a new registry and swaps it in whole (see SocketService).
        private volatile ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;
        private volatile string _configLoadError;
        private string _authToken;
        private bool _allowRemoteCodeExecution;
        private bool _confirmEachRun = true;
        // What the running service enforces (read at Initialize), and why Start must refuse.
        private ServiceSettings _runningSettings = new ServiceSettings();
        private bool _startedWithLoadError;
        private string _startRefusal;
        private volatile string _lastStartError;

        // 代码执行类高危方法白名单（默认关闭，除非配置显式允许）
        // Code-execution class of high-risk methods (disabled by default unless config allows).
        // check_code_in_revit（只编译）与 get_bridge_capabilities（只读元数据）没有副作用，故意不在此列。
        // dry_run_code_in_revit 虽然回滚模型改动，但同样在 Revit 里运行远程下发的代码，所以在列。
        // check_code_in_revit (compile only) and get_bridge_capabilities (read-only metadata)
        // have no side effects and are deliberately not listed here. dry_run_code_in_revit
        // rolls its model changes back but still runs remotely supplied code in Revit, so it is.
        // test_addin_command_in_revit 载入并运行本机 DLL；capture_view_in_revit 往调用方给的路径写文件：同样在列。
        // test_addin_command_in_revit loads and runs a local DLL and capture_view_in_revit writes a file
        // to a caller-chosen path: listed too.
        private static readonly HashSet<string> CodeExecutionMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "send_code_to_revit",
            "manage_solidified_tools",
            "dry_run_code_in_revit",
            "test_addin_command_in_revit",
            "capture_view_in_revit",
        };

        // 中继的关闭码（阶段 7 规格 §3）
        // Relay close codes (phase 7 spec, section 3).
        private const int CloseAlreadyConnected = 4002;   // another connection holds this device
        private const int CloseUnpairedOrRevoked = 4003;  // auth failed, unknown or revoked device

        public static WebSocketService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new WebSocketService();
                return _instance;
            }
        }

        private WebSocketService()
        {
            _commandRegistry = new BridgeCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;
        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
        public string LastConnectionError => _lastConnectionError;
        public string DeviceId => _deviceId;
        public string ServerUrl => _serverUrl;

        /// <summary>Why the last Start refused (an unreadable settings file), or null.</summary>
        public string LastStartError => _lastStartError;

        /// <summary>
        /// <para>服务器已用 close 4003 拒绝本设备，重连已停止。</para>
        /// <para>The server refused this device with close 4003; reconnecting stopped.</para>
        /// </summary>
        public bool IsUnpaired => _isUnpaired;

        /// <summary>The deviceId the server refused, or null.</summary>
        public string UnpairedDeviceId => _unpairedDeviceId;

        /// <summary>Why commandRegistry.json was not read at the last load, or null.</summary>
        public string ConfigLoadError => _configLoadError;

        /// <summary>Number of methods the current registry answers.</summary>
        public int RegisteredMethodCount => SocketService.CountMethods(_commandRegistry);

        /// <summary>
        /// <para>连接状态变了（连上、断开、被拒、工作线程结束）。在工作线程上触发；订阅者自己切回 UI 线程。</para>
        /// <para>The connection state changed (connected, lost, refused, worker ended). Raised
        /// on the worker thread; subscribers move to the UI thread themselves.</para>
        /// </summary>
        public event Action StateChanged;

        private void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Debug("状态通知失败 / State notification failed: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Initialize with Revit context and load commands.
        /// Call this before Start().
        /// </summary>
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("WebSocket mode — Revit version: {0}", currentVersion);

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            ApplyConfiguration(configManager);

            // 确认对话框的外部事件必须在 Revit UI 线程上创建（Initialize 由 Ribbon 点击触发）。
            // The confirmation dialog's external event has to be created on the Revit UI
            // thread; Initialize() runs from the ribbon click.
            ConfirmationPrompt.Initialize();

            LoadCommandRegistry(configManager, null);

            _logger.Info("WebSocket service initialized, commands loaded");
        }

        /// <summary>
        /// <para>The Revit-free step of Initialize: load result, token, code-execution switch,
        /// per-run confirmation. A settings file that exists but cannot be read makes Start
        /// refuse (as the local service does).</para>
        /// </summary>
        internal void ApplyConfiguration(ConfigurationManager configManager)
        {
            ServiceSettings settings = configManager.Config?.Settings;
            _configLoadError = configManager.LoadError;
            _startedWithLoadError = configManager.LoadError != null;
            _runningSettings = settings ?? new ServiceSettings();

            // 读取鉴权 token 与代码执行开关（可选增强，缺失时向后兼容）
            // Read auth token and code-execution switch (optional; backward compatible).
            _authToken = settings?.Token;
            _allowRemoteCodeExecution = settings?.AllowRemoteCodeExecution ?? false;
            _confirmEachRun = settings?.ConfirmEachRun ?? true;
            _startRefusal = configManager.FileUnreadable
                ? configManager.LoadError + ", so the remote bridge does not start. Repair the file or re-run the installer."
                : null;
        }

        /// <summary>
        /// <para>Read the configuration again and rebuild the command registry (after
        /// "Allow my tools" changed): official commands keep their instances, only user
        /// commands are added or dropped. Token, allowRemoteCodeExecution and per-run
        /// confirmation keep their values from the start. When the configuration cannot be
        /// read the current registry stays. Call in a Revit API context.</para>
        /// </summary>
        /// <returns>null when reloaded, else why not.</returns>
        public string ReloadCommands()
        {
            if (_uiApp == null) return "the bridge has not been started in this Revit session";

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            // Same rule as the local service: no reload on an unreadable file, after a start on
            // one, or when a security-relevant setting changed on disk.
            string refusal = SocketService.ReloadRefusal(_startedWithLoadError, _runningSettings, configManager);
            if (refusal != null)
            {
                _logger.Warning("未重载命令，保留已加载的命令 / Commands not reloaded; kept the loaded ones: {0}", refusal);
                return refusal;
            }
            _configLoadError = null;
            LoadCommandRegistry(configManager, _commandRegistry as BridgeCommandRegistry);
            _logger.Info("WebSocket service commands reloaded");
            return null;
        }

        private void LoadCommandRegistry(ConfigurationManager configManager, BridgeCommandRegistry reuseFrom)
        {
            var registry = new BridgeCommandRegistry();
            CommandManager commandManager = new CommandManager(
                registry, _logger, configManager, _uiApp, reuseFrom);
            commandManager.LoadCommands();

            _commandRegistry = registry;
            UserToolAccess.Publish(registry);
            _commandExecutor = new CommandExecutor(registry, _logger);
        }

        /// <summary>
        /// Start the WebSocket client.
        /// </summary>
        /// <param name="serverUrl">
        /// Base WebSocket URL from commandRegistry.json, e.g. "wss://host/api/v1/bridge/ws"
        /// </param>
        /// <param name="deviceId">Device id assigned when the pairing code was redeemed</param>
        public void Start(string serverUrl, string deviceId)
        {
            if (_isRunning) return;

            _lastStartError = _startRefusal;
            if (_startRefusal != null)
            {
                UserToolAccess.Unpublish(_commandRegistry);
                _logger.Error("WebSocket 服务拒绝启动 / WebSocket service refused to start: {0}", _startRefusal);
                return;
            }

            _serverUrl = serverUrl.TrimEnd('/');
            _deviceId = deviceId;
            _isRunning = true;
            _lastConnectionError = null;
            _isUnpaired = false;
            _unpairedDeviceId = null;
            _cts = new CancellationTokenSource();

            // 每个工作线程只看自己启动时的 token：Stop 取消它，之后的 Start 换新的 token，
            // 旧线程不会因为 _isRunning 又变成 true 而继续运行。
            // Each worker only looks at the token it was started with: Stop cancels it and a
            // later Start creates a new one, so an old worker never carries on because
            // _isRunning became true again.
            CancellationToken workerToken = _cts.Token;
            _workerThread = new Thread(() => WorkerLoop(workerToken))
            {
                IsBackground = true,
                Name = "WebSocketService"
            };
            _workerThread.Start();

            _logger.Info($"WebSocket service starting → {_serverUrl}/{_deviceId}");
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            // Stopped: user_tools and the commands' registry view no longer show this registry.
            UserToolAccess.Unpublish(_commandRegistry);

            try
            {
                _cts?.Cancel();

                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    // Best-effort close
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin stopping",
                        CancellationToken.None).Wait(2000);
                }
                _ws?.Dispose();
                _ws = null;

                if (_workerThread != null && _workerThread.IsAlive)
                    _workerThread.Join(2000);
            }
            catch (Exception ex)
            {
                // 停止过程中的异常仅记录，不影响关闭流程
                // Log shutdown errors instead of swallowing them silently.
                _logger.Warning("WebSocket 停止时异常 / Error stopping WebSocket service: {0}", ex.Message);
            }

            _logger.Info("WebSocket service stopped");
        }

        // ── Worker loop with auto-reconnect ─────────────────────────────

        private void WorkerLoop(CancellationToken stopToken)
        {
            while (!stopToken.IsCancellationRequested)
            {
                try
                {
                    // Avoid AggregateException masking the actual HTTP/WebSocket error.
                    ConnectAndListen(stopToken).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (stopToken.IsCancellationRequested) break;
                    Exception rootCause = ex.GetBaseException();
                    _lastConnectionError = rootCause.Message;
                    _logger.Warning($"WebSocket connection lost: {rootCause.Message}");
                }

                if (stopToken.IsCancellationRequested) break;

                // 被服务器判定为未配对/已吊销：不再重连，等用户重新配对。
                // Refused as unpaired/revoked: stop here until the user pairs again.
                if (_isUnpaired)
                {
                    _isRunning = false;
                    UserToolAccess.Unpublish(_commandRegistry);
                    _logger.Warning("设备未配对或已吊销，已停止重连 / Device unpaired or revoked; stopped reconnecting.");
                    NotifyStateChanged();
                    break;
                }

                // 断开（或连接失败）：功能区按钮的提示随之更新。
                // Disconnected (or could not connect): the ribbon button's tooltip follows.
                NotifyStateChanged();

                // Wait before reconnecting; Stop ends the wait at once.
                _logger.Info($"Reconnecting in {_reconnectDelayMs / 1000}s...");
                stopToken.WaitHandle.WaitOne(_reconnectDelayMs);
            }
        }

        private async Task ConnectAndListen(CancellationToken stopToken)
        {
            // 本次连接只用局部的 ws 与 stopToken；_ws 字段只供 IsConnected 与 Stop 使用。
            // This connection only uses its local ws and stopToken; the _ws field is only
            // for IsConnected and Stop.
            _ws?.Dispose();
            var ws = new ClientWebSocket();
            _ws = ws;
            // Cloudflare rejects the header-less .NET ClientWebSocket handshake
            // with HTTP 403. An explicit product User-Agent reaches the origin
            // and upgrades normally with HTTP 101.
            ws.Options.SetRequestHeader("User-Agent", AddinInfo.UserAgent);

            var uri = new Uri($"{_serverUrl}/{_deviceId}");
            _logger.Info($"Connecting to {uri}...");

            await ws.ConnectAsync(uri, stopToken);
            _lastConnectionError = null;
            _logger.Info($"Connected as device {_deviceId}");

            // 首条消息必须是鉴权握手，总是发送；服务器 10 秒内收不到即关闭连接。
            // The first message must be the auth handshake and is always sent; the
            // server closes the connection when it does not arrive within 10 s.
            string authMsg = JsonConvert.SerializeObject(new
            {
                type = "auth",
                device_id = _deviceId,
                token = _authToken ?? ""
            });
            byte[] authBytes = Encoding.UTF8.GetBytes(authMsg);
            await ws.SendAsync(
                new ArraySegment<byte>(authBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: stopToken);
            _logger.Info("已发送鉴权握手 / Auth handshake sent");
            NotifyStateChanged();

            // Receive loop
            var buffer = new byte[65536];
            var messageBuffer = new StringBuilder();

            while (ws.State == WebSocketState.Open && !stopToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer), stopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleServerClose((int?)result.CloseStatus, result.CloseStatusDescription, stopToken);

                    // 回一个关闭帧，让服务器干净地结束这条连接并立即释放本设备的登记，
                    // 否则下次重连可能被判为"已连接"（4002）。失败不影响后续流程。
                    // Answer with a close frame so the server finishes the handshake and
                    // releases this device's registration right away; otherwise the next
                    // reconnect can be refused as "already connected" (4002). Best effort.
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("回送关闭帧失败 / Could not send the closing frame: {0}", ex.Message);
                    }
                    break;
                }

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    string request = messageBuffer.ToString();
                    messageBuffer.Clear();

                    System.Diagnostics.Trace.WriteLine($"[WS] Received: {request}");

                    // Process and respond
                    string response = ProcessJsonRPCRequest(request);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);

                    await ws.SendAsync(
                        new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: stopToken);
                }
            }
        }

        /// <summary>
        /// <para>处理服务器关闭帧：4003 = 未配对/已吊销（停止重连）；4002 = 该设备已有连接
        /// （保持原有重试）；其他按普通断线处理。</para>
        /// <para>Handle the server's close frame: 4003 = unpaired/revoked (stop
        /// reconnecting), 4002 = this device is already connected elsewhere (keep
        /// the existing retry), anything else is an ordinary disconnect.</para>
        /// <para>已停止的工作线程（Stop 之后、下一次 Start 之前收到的关闭帧）不再处理：
        /// 它不能改服务状态，也不能在磁盘上收回 allowRemoteCodeExecution。</para>
        /// <para>A worker that has been stopped (a close frame arriving between Stop and the
        /// next Start) does nothing: it must not change the service's state or withdraw
        /// allowRemoteCodeExecution on disk.</para>
        /// </summary>
        private void HandleServerClose(int? closeCode, string description, CancellationToken stopToken)
        {
            string detail = string.IsNullOrWhiteSpace(description) ? "" : ": " + description;

            if (stopToken.IsCancellationRequested)
            {
                _logger.Info("已停止的连接收到关闭帧 {0}，已忽略 / A stopped connection received close {0}; ignored.",
                    closeCode?.ToString() ?? "unknown");
                return;
            }

            if (closeCode == CloseUnpairedOrRevoked)
            {
                _isUnpaired = true;
                _unpairedDeviceId = _deviceId;
                _lastConnectionError =
                    $"Unpaired or revoked ({CloseUnpairedOrRevoked}){detail}. " +
                    "Pair this Revit again in the Revit Bridge panel (Shift+click the Revit Bridge button).";
                _logger.Warning(
                    "服务器拒绝本设备（未配对/已吊销）/ Server refused this device (unpaired or revoked){0}",
                    detail);

                // 配对即授权远程执行代码；服务器不再认这台设备时收回该授权，
                // 内存与配置一起改，重新配对才会再打开。
                // Pairing is what authorises remote code execution here, so withdraw it
                // when the server no longer accepts this device: in memory and on disk.
                // Pairing again switches it back on.
                _allowRemoteCodeExecution = false;
                SettingsStore.Update(s => s.AllowRemoteCodeExecution = false, _logger);
                return;
            }

            if (closeCode == CloseAlreadyConnected)
            {
                _lastConnectionError =
                    $"This device is already connected elsewhere ({CloseAlreadyConnected}){detail}. Retrying...";
                _logger.Warning(
                    "该设备已有活动连接，稍后重试 / Device already connected elsewhere; will retry{0}",
                    detail);
                return;
            }

            _lastConnectionError =
                $"Server closed WebSocket connection: {closeCode?.ToString() ?? "unknown"}{detail}";
            _logger.Warning(_lastConnectionError);
        }

        // ── JSON-RPC processing (identical to SocketService) ────────────

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request;

            try
            {
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                if (request == null || !request.IsValid())
                {
                    return CreateErrorResponse(null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request");
                }

                // 代码执行类高危方法白名单开关：默认关闭，防止远程直接下发代码执行。
                // Gate code-execution class methods: disabled by default so a remote peer
                // cannot trigger arbitrary code execution without explicit opt-in.
                if (!_allowRemoteCodeExecution && CodeExecutionMethods.Contains(request.Method))
                {
                    _logger.Warning("已拦截代码执行类方法 {0}（allowRemoteCodeExecution=false）\nBlocked code-execution method {0} (allowRemoteCodeExecution=false).", request.Method);
                    return CreateErrorResponse(request.Id,
                        JsonRPCErrorCodes.InvalidRequest,
                        $"Method '{request.Method}' is disabled. Set 'allowRemoteCodeExecution' to enable.");
                }

                ICommandRegistry registry = _commandRegistry;
                if (!registry.TryGetCommand(request.Method, out var command))
                {
                    // A user command not loaded because "Allow my tools" is off: say so.
                    string notLoaded = UserToolAccess.RefusalForMissing(registry, request.Method);
                    if (notLoaded != null)
                    {
                        _logger.Warning("已拒绝用户工具 {0}（允许接入我的工具：关）\nRefused user tool {0} (Allow my tools: off).", request.Method);
                        return CreateErrorResponse(request.Id, UserToolAccess.ErrorCode, notLoaded);
                    }
                    return CreateErrorResponse(request.Id,
                        JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
                }

                // "允许接入我的工具"关闭时，用户命令与固化工具的登记、运行在服务端拒绝。
                // With "Allow my tools" off, user commands and solidified-tool register/run
                // are refused here, before any prompt or command runs.
                // Decided from the same registry snapshot the method was looked up in.
                string refusal = UserToolAccess.RefusalFor(registry, request.Method, request.GetParamsObject);
                if (refusal != null)
                {
                    _logger.Warning("已拒绝用户工具 {0}（允许接入我的工具：关）\nRefused user tool {0} (Allow my tools: off).", request.Method);
                    return CreateErrorResponse(request.Id, UserToolAccess.ErrorCode, refusal);
                }

                // 逐次确认：服务器给即席代码的请求加 confirm 参数，设备侧弹窗询问设计师。
                // 能力包、探针、读取不带 confirm，不弹窗。
                // Per-run confirmation: the server marks ad-hoc code requests with a
                // confirm object and the device asks the designer. Capability packs,
                // probes and reads carry no confirm and never prompt.
                if (ConfirmationPrompt.Decide(request.GetParamsObject(), _confirmEachRun, _logger)
                    == ConfirmationDecision.Declined)
                {
                    return CreateErrorResponse(request.Id,
                        ConfirmationPrompt.DeclinedErrorCode,
                        ConfirmationPrompt.DeclinedMessage);
                }

                try
                {
                    object result = command.Execute(request.GetParamsObject(), request.Id);
                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse(request.Id,
                        JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException)
            {
                return CreateErrorResponse(null,
                    JsonRPCErrorCodes.ParseError, "Invalid JSON");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse(null,
                    JsonRPCErrorCodes.InternalError, $"Internal error: {ex.Message}");
            }
        }

        private string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result is JToken jToken ? jToken : JToken.FromObject(result)
            };
            return response.ToJson();
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };
            return response.ToJson();
        }
    }
}
