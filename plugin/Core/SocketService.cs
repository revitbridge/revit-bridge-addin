using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    public class SocketService
    {
        private static SocketService _instance;
        private TcpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning;
        private int _port = 18080;
        private UIApplication _uiApp;
        // 每次加载命令都换一个新的注册表，整体替换；请求线程每次只取一次引用。
        // Every command load fills a new registry and swaps it in whole; a request thread
        // reads the reference once, so it never sees a half-filled registry.
        private volatile ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private volatile string _lastStartError;
        private volatile string _configLoadError;
        private CommandExecutor _commandExecutor;
        private string _authToken;
        private bool _tokenWarningLogged;
        private bool _confirmEachRun = true;

        public static SocketService Instance
        {
            get
            {
                if(_instance == null)
                    _instance = new SocketService();
                return _instance;
            }
        }

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        /// <summary>
        /// <para>上一次 Start 失败的原因（含端口）；成功启动后为 null。</para>
        /// <para>Why the last Start failed, naming the port; null after a successful start.</para>
        /// </summary>
        public string LastStartError => _lastStartError;

        /// <summary>Why commandRegistry.json was not read at the last load, or null.</summary>
        public string ConfigLoadError => _configLoadError;

        /// <summary>Number of methods the current registry answers.</summary>
        public int RegisteredMethodCount => CountMethods(_commandRegistry);

        // 初始化
        // Initialization.
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // 初始化事件管理器
            // Initialize ExternalEventManager
            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            // 记录当前 Revit 版本
            // Get the current Revit version.
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            // 加载配置并注册命令
            // Load configuration and register commands.
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            _configLoadError = configManager.LoadError;

            // 读取预共享鉴权 token（可选增强，缺失时向后兼容放行）
            // Read the pre-shared auth token (optional; missing token stays backward compatible).
            _authToken = configManager.Config?.Settings?.Token;
            _confirmEachRun = configManager.Config?.Settings?.ConfirmEachRun ?? true;

            // 确认对话框的外部事件必须在 Revit UI 线程上创建（Initialize 由 Ribbon 点击触发）。
            // The confirmation dialog's external event has to be created on the Revit UI
            // thread; Initialize() runs from the ribbon click.
            ConfirmationPrompt.Initialize();


            // 从配置中读取服务端口（缺省仍为字段默认值 18080）
            // Read the service port from the configuration (defaults to 18080 when unset).
            if (configManager.Config?.Settings?.Port > 0)
            {
                _port = configManager.Config.Settings.Port;
            }

            // 加载命令
            // Load command.
            LoadCommandRegistry(configManager);

            _logger.Info($"Socket service initialized on port {_port}");
        }

        /// <summary>
        /// <para>重新读配置、重建命令注册表（"允许接入我的工具"切换后）。端口、token、
        /// 逐次确认仍是启动时的值。须在 Revit API 上下文中调用（命令的构造函数会创建外部事件）。</para>
        /// <para>Read the configuration again and rebuild the command registry (after
        /// "Allow my tools" changed). Port, token and per-run confirmation keep their values
        /// from the start. Call in a Revit API context: command constructors create external
        /// events.</para>
        /// </summary>
        public void ReloadCommands()
        {
            if (_uiApp == null) return;

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            _configLoadError = configManager.LoadError;
            LoadCommandRegistry(configManager);
            _logger.Info("Socket service commands reloaded");
        }

        private void LoadCommandRegistry(ConfigurationManager configManager)
        {
            var registry = new RevitCommandRegistry();
            CommandManager commandManager = new CommandManager(
                registry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _commandRegistry = registry;
            // 创建命令执行器
            // Create CommandExecutor
            _commandExecutor = new CommandExecutor(registry, _logger);
        }

        // A published registry is never changed again, so counting it from the UI thread is safe.
        internal static int CountMethods(ICommandRegistry registry)
        {
            return (registry as RevitCommandRegistry)?.GetRegisteredCommands().Count() ?? 0;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _lastStartError = null;
                _isRunning = true;
                // 仅监听本地回环地址，避免同网段主机直连未鉴权端口
                // Bind to loopback only so other hosts on the LAN cannot reach the port.
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();

                _listenerThread = new Thread(ListenForClients)
                {
                    IsBackground = true
                };
                _listenerThread.Start();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                try
                {
                    _listener?.Stop();
                }
                catch (SocketException stopEx)
                {
                    _logger.Debug("Socket 监听器清理失败 / Could not clean up the listener: {0}", stopEx.Message);
                }
                _listener = null;
                _lastStartError = DescribeStartFailure(ex, _port);
                _logger.Error("Socket 服务启动失败 / Failed to start socket service: {0}", ex.Message);
            }
        }

        /// <summary>
        /// <para>启动失败的说明：端口与原因。</para>
        /// <para>One line for a failed start: the port and why.</para>
        /// </summary>
        public static string DescribeStartFailure(Exception ex, int port)
        {
            if (ex is SocketException socketEx)
            {
                switch (socketEx.SocketErrorCode)
                {
                    case SocketError.AddressAlreadyInUse:
                        return $"port {port} is already in use (another Revit or another program is listening on it). " +
                               "Turn the bridge off there, or choose another port in the Revit Bridge panel (Advanced).";
                    case SocketError.AccessDenied:
                        return $"port {port} cannot be used (access denied; Windows may have reserved it). " +
                               "Choose another port in the Revit Bridge panel (Advanced).";
                }
                return $"port {port}: {socketEx.Message} ({socketEx.SocketErrorCode})";
            }
            return $"port {port}: {ex.Message}";
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _isRunning = false;

                _listener?.Stop();
                _listener = null;

                if(_listenerThread!=null && _listenerThread.IsAlive)
                {
                    _listenerThread.Join(1000);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Socket 服务停止时异常 / Error stopping socket service: {0}", ex.Message);
            }
        }

        private void ListenForClients()
        {
            try
            {
                while (_isRunning)
                {
                    TcpClient client = _listener.AcceptTcpClient();

                    Thread clientThread = new Thread(HandleClientCommunication)
                    {
                        IsBackground = true
                    };
                    clientThread.Start(client);
                }
            }
            catch (SocketException)
            {
                // 监听器被 Stop() 关闭时的正常退出，仅调试记录
                // Normal exit when Stop() closes the listener; debug-level only.
                _logger.Debug("Socket 监听线程结束 / Socket listener thread ended");
            }
            catch(Exception ex)
            {
                _logger.Error("Socket 监听循环异常 / Socket listen loop error: {0}", ex.Message);
            }
        }

        private void HandleClientCommunication(object clientObj)
        {
            TcpClient tcpClient = (TcpClient)clientObj;

            try
            {
                // 在 try 内取流：连接刚接受就被重置时 GetStream 会抛出，而后台线程上
                // 未处理的异常会结束整个 Revit 进程。
                // Inside the try: GetStream throws when the connection was reset right after
                // accept, and an unhandled exception on a background thread ends Revit.
                NetworkStream stream = tcpClient.GetStream();
                byte[] buffer = new byte[8192];

                while (_isRunning && tcpClient.Connected)
                {
                    // 循环读取并累加到 MemoryStream，直到读完当前完整帧再处理，
                    // 避免单次 8192B 读取截断超长消息（对齐 WebSocket 端的分片累加）。
                    // Loop-read into a MemoryStream until the full frame is received before
                    // processing, so messages larger than the buffer are not truncated
                    // (mirrors the fragment accumulation on the WebSocket side).
                    int bytesRead;

                    try
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException)
                    {
                        // 客户端断开连接
                        // Client disconnected.
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // 客户端断开连接
                        // Client disconnected.
                        break;
                    }

                    string message;
                    using (var ms = new MemoryStream())
                    {
                        ms.Write(buffer, 0, bytesRead);

                        // 继续读取仍在缓冲区中的剩余分片，直到没有更多数据
                        // Drain the remaining fragments of the same frame still buffered.
                        while (stream.DataAvailable)
                        {
                            int more;
                            try
                            {
                                more = stream.Read(buffer, 0, buffer.Length);
                            }
                            catch (IOException)
                            {
                                break;
                            }
                            if (more <= 0)
                                break;
                            ms.Write(buffer, 0, more);
                        }

                        message = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    }

                    System.Diagnostics.Trace.WriteLine($"收到消息: {message}\nReceived message: {message}");

                    string response = ProcessJsonRPCRequest(message);

                    // 发送响应
                    // Send response.
                    byte[] responseData = Encoding.UTF8.GetBytes(response);
                    stream.Write(responseData, 0, responseData.Length);
                }
            }
            catch (Exception ex)
            {
                // 记录客户端通信异常，便于排障（原为静默吞异常）
                // Log client-communication errors for diagnostics (was silently swallowed).
                _logger.Error("Socket 客户端通信异常 / Socket client communication error: {0}", ex.Message);
            }
            finally
            {
                tcpClient.Close();
            }
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request;

            try
            {
                // 解析JSON-RPC请求
                // Parse JSON-RPC requests.
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                // 验证请求格式是否有效
                // Verify that the request format is valid.
                if (request == null || !request.IsValid())
                {
                    return CreateErrorResponse(
                        null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request"
                    );
                }

                // 校验鉴权 token（未配置时向后兼容放行，仅记录一次警告）
                // Validate the auth token (backward-compatible when unset; warns once).
                if (!IsAuthorized(requestJson))
                {
                    return CreateErrorResponse(request.Id,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Unauthorized: invalid or missing token");
                }

                // 查找命令
                // Search for the command in the registry.
                ICommandRegistry registry = _commandRegistry;
                if (!registry.TryGetCommand(request.Method, out var command))
                {
                    // 因"允许接入我的工具"关闭而没有加载的用户命令：明确拒绝，而不是"找不到"。
                    // A user command not loaded because "Allow my tools" is off: say so
                    // instead of "not found".
                    string notLoaded = UserToolAccess.RefusalForMissing(request.Method);
                    if (notLoaded != null)
                    {
                        _logger.Warning("已拒绝用户工具 {0}（允许接入我的工具：关）\nRefused user tool {0} (Allow my tools: off).", request.Method);
                        return CreateErrorResponse(request.Id, UserToolAccess.ErrorCode, notLoaded);
                    }
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
                }

                // "允许接入我的工具"关闭时，用户命令与固化工具的登记、运行在服务端拒绝。
                // With "Allow my tools" off, user commands and solidified-tool register/run
                // are refused here, before any prompt or command runs.
                string refusal = UserToolAccess.RefusalFor(request.Method, request.GetParamsObject);
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

                // 执行命令
                // Execute command.
                try
                {                
                    object result = command.Execute(request.GetParamsObject(), request.Id);

                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException)
            {
                // JSON解析错误
                // JSON parsing error.
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.ParseError,
                    "Invalid JSON"
                );
            }
            catch (Exception ex)
            {
                // 处理请求时的其他错误
                // Catch other errors produced when processing requests.
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.InternalError,
                    $"Internal error: {ex.Message}"
                );
            }
        }

        /// <summary>
        /// 校验请求中的 token 字段是否与用户配置密钥一致。
        /// 配置里若无 token 则记录一次 warning 并放行（向后兼容）。
        /// Validate the request's token field against the configured secret.
        /// If no token is configured, log a warning once and allow (backward compatible).
        /// </summary>
        private bool IsAuthorized(string requestJson)
        {
            if (string.IsNullOrEmpty(_authToken))
            {
                if (!_tokenWarningLogged)
                {
                    _logger.Warning("未配置鉴权 token，已放行 socket 请求（向后兼容）\nNo auth token configured; allowing socket requests (backward compatible).");
                    _tokenWarningLogged = true;
                }
                return true;
            }

            try
            {
                var obj = JObject.Parse(requestJson);
                string reqToken = obj.Value<string>("token")
                    ?? obj["params"]?.Value<string>("token");
                return string.Equals(reqToken, _authToken, StringComparison.Ordinal);
            }
            catch
            {
                return false;
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
