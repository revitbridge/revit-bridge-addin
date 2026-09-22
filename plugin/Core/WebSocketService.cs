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
        private ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;
        private string _authToken;
        private bool _allowRemoteCodeExecution;
        private bool _confirmEachRun = true;

        // 代码执行类高危方法白名单（默认关闭，除非配置显式允许）
        // Code-execution class of high-risk methods (disabled by default unless config allows).
        private static readonly HashSet<string> CodeExecutionMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "send_code_to_revit",
            "manage_solidified_tools",
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
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;
        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;
        public string LastConnectionError => _lastConnectionError;
        public string DeviceId => _deviceId;
        public string ServerUrl => _serverUrl;

        /// <summary>
        /// <para>服务器已用 close 4003 拒绝本设备，重连已停止。</para>
        /// <para>The server refused this device with close 4003; reconnecting stopped.</para>
        /// </summary>
        public bool IsUnpaired => _isUnpaired;

        /// <summary>The deviceId the server refused, or null.</summary>
        public string UnpairedDeviceId => _unpairedDeviceId;

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

            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            // 读取鉴权 token 与代码执行开关（可选增强，缺失时向后兼容）
            // Read auth token and code-execution switch (optional; backward compatible).
            _authToken = configManager.Config?.Settings?.Token;
            _allowRemoteCodeExecution =
                configManager.Config?.Settings?.AllowRemoteCodeExecution ?? false;
            _confirmEachRun = configManager.Config?.Settings?.ConfirmEachRun ?? true;

            // 确认对话框的外部事件必须在 Revit UI 线程上创建（Initialize 由 Ribbon 点击触发）。
            // The confirmation dialog's external event has to be created on the Revit UI
            // thread; Initialize() runs from the ribbon click.
            ConfirmationPrompt.Initialize();

            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info("WebSocket service initialized, commands loaded");
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

            _serverUrl = serverUrl.TrimEnd('/');
            _deviceId = deviceId;
            _isRunning = true;
            _lastConnectionError = null;
            _isUnpaired = false;
            _unpairedDeviceId = null;
            _cts = new CancellationTokenSource();

            _workerThread = new Thread(WorkerLoop)
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

        private void WorkerLoop()
        {
            while (_isRunning)
            {
                try
                {
                    // Avoid AggregateException masking the actual HTTP/WebSocket error.
                    ConnectAndListen().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Exception rootCause = ex.GetBaseException();
                    _lastConnectionError = rootCause.Message;
                    _logger.Warning($"WebSocket connection lost: {rootCause.Message}");
                }

                // 被服务器判定为未配对/已吊销：不再重连，等用户重新配对。
                // Refused as unpaired/revoked: stop here until the user pairs again.
                if (_isUnpaired)
                {
                    _isRunning = false;
                    _logger.Warning("设备未配对或已吊销，已停止重连 / Device unpaired or revoked; stopped reconnecting.");
                    break;
                }

                // Wait before reconnecting
                if (_isRunning)
                {
                    _logger.Info($"Reconnecting in {_reconnectDelayMs / 1000}s...");
                    Thread.Sleep(_reconnectDelayMs);
                }
            }
        }

        private async Task ConnectAndListen()
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();
            // Cloudflare rejects the header-less .NET ClientWebSocket handshake
            // with HTTP 403. An explicit product User-Agent reaches the origin
            // and upgrades normally with HTTP 101.
            _ws.Options.SetRequestHeader("User-Agent", AddinInfo.UserAgent);

            var uri = new Uri($"{_serverUrl}/{_deviceId}");
            _logger.Info($"Connecting to {uri}...");

            await _ws.ConnectAsync(uri, _cts.Token);
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
            await _ws.SendAsync(
                new ArraySegment<byte>(authBytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: _cts.Token);
            _logger.Info("已发送鉴权握手 / Auth handshake sent");

            // Receive loop
            var buffer = new byte[65536];
            var messageBuffer = new StringBuilder();

            while (_ws.State == WebSocketState.Open && _isRunning)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleServerClose((int?)result.CloseStatus, result.CloseStatusDescription);

                    // 回一个关闭帧，让服务器干净地结束这条连接并立即释放本设备的登记，
                    // 否则下次重连可能被判为"已连接"（4002）。失败不影响后续流程。
                    // Answer with a close frame so the server finishes the handshake and
                    // releases this device's registration right away; otherwise the next
                    // reconnect can be refused as "already connected" (4002). Best effort.
                    try
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "",
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

                    await _ws.SendAsync(
                        new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: _cts.Token);
                }
            }
        }

        /// <summary>
        /// <para>处理服务器关闭帧：4003 = 未配对/已吊销（停止重连）；4002 = 该设备已有连接
        /// （保持原有重试）；其他按普通断线处理。</para>
        /// <para>Handle the server's close frame: 4003 = unpaired/revoked (stop
        /// reconnecting), 4002 = this device is already connected elsewhere (keep
        /// the existing retry), anything else is an ordinary disconnect.</para>
        /// </summary>
        private void HandleServerClose(int? closeCode, string description)
        {
            string detail = string.IsNullOrWhiteSpace(description) ? "" : ": " + description;

            if (closeCode == CloseUnpairedOrRevoked)
            {
                _isUnpaired = true;
                _unpairedDeviceId = _deviceId;
                _lastConnectionError =
                    $"Unpaired or revoked ({CloseUnpairedOrRevoked}){detail}. " +
                    "Pair this Revit again in Settings > Connection.";
                _logger.Warning(
                    "服务器拒绝本设备（未配对/已吊销）/ Server refused this device (unpaired or revoked){0}",
                    detail);
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

                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                {
                    return CreateErrorResponse(request.Id,
                        JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
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
