using System;
using System.Collections.Generic;
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
        // 一个请求的后续分片最多等这么久（毫秒）。
        // How long to wait for the next segment of a request that is not complete yet.
        private const int FrameContinuationTimeoutMs = 5000;
        private const int MaxRequestBytes = 64 * 1024 * 1024;

        private enum FrameReadResult
        {
            Complete,
            TimedOut,
            Disconnected,
            TooLarge
        }

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
        // Why Start must refuse (a "token" that is present but unreadable, a settings file
        // that exists but cannot be read), or null.
        private string _startRefusal;
        // What the running service enforces, read at Initialize; a reload compares the file with it.
        private ServiceSettings _runningSettings = new ServiceSettings();
        private bool _startedWithLoadError;
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
            _commandRegistry = new BridgeCommandRegistry();
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

            // 读取结果、token、逐次确认、端口
            // Load result, token, per-run confirmation, port.
            ApplyConfiguration(configManager);

            // 确认对话框的外部事件必须在 Revit UI 线程上创建（Initialize 由 Ribbon 点击触发）。
            // The confirmation dialog's external event has to be created on the Revit UI
            // thread; Initialize() runs from the ribbon click.
            ConfirmationPrompt.Initialize();

            // 加载命令
            // Load command.
            LoadCommandRegistry(configManager, null);

            _logger.Info($"Socket service initialized on port {_port}");
        }

        /// <summary>
        /// <para>Initialize 中与 Revit 无关的一步：token、逐次确认、端口。token 存在却读不出（例如是
        /// 对象）时记下拒绝原因，Start 不启动：本机端口不能在没有密钥的情况下开放。</para>
        /// <para>The Revit-free step of Initialize: token, per-run confirmation, port. When a
        /// "token" is present but cannot be read (an object, say), Start refuses with the
        /// reason: the local port must not open without its secret.</para>
        /// </summary>
        internal void ApplyConfiguration(ConfigurationManager configManager)
        {
            _configLoadError = configManager.LoadError;
            _startedWithLoadError = configManager.LoadError != null;
            ApplySettings(configManager.Config?.Settings);
            // 文件存在却读不出：不开本机端口（读不到 token 就可能在没有密钥时开放）；文件不存在按缺省值启动。
            // The file exists but cannot be read: the local port does not open (its token, if
            // any, is unknown). A missing file is a fresh install and starts on the defaults.
            if (configManager.FileUnreadable)
                _startRefusal = configManager.LoadError +
                                ", so the local bridge does not start. Repair the file or re-run the installer.";
        }

        /// <summary>
        /// <para>运行中能否按磁盘上的设置重载命令：启动时配置读不出、现在读不出、或与安全有关的设置
        /// （token、模式、端口、allowRemoteCodeExecution）已变，都拒绝，须关再开。返回 null 表示可以。</para>
        /// <para>Whether a running service may reload its commands from the settings now on
        /// disk. Refused when the service started on a configuration it could not read, when
        /// the file cannot be read now, or when a security-relevant setting (token, mode,
        /// port, allowRemoteCodeExecution) differs from what the service enforces: the bridge
        /// must be turned off and on instead. null when the reload may go on.</para>
        /// </summary>
        internal string ReloadRefusal(ConfigurationManager now)
        {
            return ReloadRefusal(_startedWithLoadError, _runningSettings, now);
        }

        internal static string ReloadRefusal(bool startedWithLoadError, ServiceSettings running, ConfigurationManager now)
        {
            if (startedWithLoadError)
                return "the bridge was turned on while commandRegistry.json could not be read; turn the bridge off and on to apply the settings";
            if (now.LoadError != null)
                return now.LoadError + "; the loaded commands were kept. Repair the file, then turn the bridge off and on";
            IList<string> changed = ServiceSettings.SecurityDifferences(running, now.Config?.Settings);
            if (changed.Count > 0)
                return "settings changed on disk since the bridge was turned on (" + string.Join(", ", changed) +
                       "); turn the bridge off and on to apply them";
            return null;
        }

        internal void ApplySettings(ServiceSettings settings)
        {
            _runningSettings = settings ?? new ServiceSettings();

            // 读取预共享鉴权 token（可选增强，缺失时向后兼容放行）
            // Read the pre-shared auth token (optional; missing token stays backward compatible).
            _authToken = settings?.Token;
            _confirmEachRun = settings?.ConfirmEachRun ?? true;
            _startRefusal = settings != null && settings.TokenUnreadable
                ? "the 'token' in Commands\\commandRegistry.json is not text, so the local bridge would have no secret. " +
                  "Fix or remove 'token' in that file (or re-run the installer)."
                : null;

            // 从配置中读取服务端口（缺省仍为字段默认值 18080）
            // Read the service port from the configuration (defaults to 18080 when unset).
            if (settings?.Port > 0)
            {
                _port = settings.Port;
            }
        }

        /// <summary>
        /// <para>重新读配置、重建命令注册表（"允许接入我的工具"切换后）：官方命令沿用现有实例，只增减用户命令。
        /// 端口、token、逐次确认仍是启动时的值。配置读不出时保留现有注册表。须在 Revit API 上下文中调用。</para>
        /// <para>Read the configuration again and rebuild the command registry (after
        /// "Allow my tools" changed): official commands keep their instances, only user
        /// commands are added or dropped. Port, token and per-run confirmation keep their
        /// values from the start. When the configuration cannot be read the current registry
        /// stays. Call in a Revit API context: a new command's constructor creates external
        /// events.</para>
        /// </summary>
        /// <returns>null when reloaded, else why not.</returns>
        public string ReloadCommands()
        {
            if (_uiApp == null) return "the bridge has not been started in this Revit session";

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            string refusal = ReloadRefusal(configManager);
            if (refusal != null)
            {
                _logger.Warning("未重载命令，保留已加载的命令 / Commands not reloaded; kept the loaded ones: {0}", refusal);
                return refusal;
            }
            _configLoadError = null;
            LoadCommandRegistry(configManager, _commandRegistry as BridgeCommandRegistry);
            _logger.Info("Socket service commands reloaded");
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

            if (_startRefusal != null)
            {
                _lastStartError = _startRefusal;
                UserToolAccess.Unpublish(_commandRegistry);
                _logger.Error("Socket 服务拒绝启动 / Socket service refused to start: {0}", _startRefusal);
                return;
            }

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
                UserToolAccess.Unpublish(_commandRegistry);
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
                // Stopped: user_tools and the commands' registry view no longer show this registry.
                UserToolAccess.Unpublish(_commandRegistry);

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
            try
            {
                var tcpClient = (TcpClient)clientObj;
                try
                {
                    NetworkStream stream = tcpClient.GetStream();
                    byte[] buffer = new byte[8192];

                    while (_isRunning && tcpClient.Connected)
                    {
                        FrameReadResult result = ReadFrame(stream, buffer, out string message);
                        string response;
                        if (result == FrameReadResult.TooLarge)
                        {
                            response = CreateErrorResponse(null, JsonRPCErrorCodes.InvalidRequest,
                                "Request too large: the limit is 64 MB");
                        }
                        else if (result == FrameReadResult.TimedOut)
                        {
                            response = CreateErrorResponse(null, JsonRPCErrorCodes.ParseError, "Invalid JSON");
                        }
                        else
                        {
                            if (message == null) break;
                            System.Diagnostics.Trace.WriteLine($"收到消息: {message}\nReceived message: {message}");
                            response = ProcessJsonRPCRequest(message);
                        }

                        byte[] responseData = Encoding.UTF8.GetBytes(response);
                        stream.Write(responseData, 0, responseData.Length);
                        if (result != FrameReadResult.Complete) break;
                    }
                }
                finally
                {
                    tcpClient.Close();
                }
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.Error("Socket 客户端通信异常 / Socket client communication error: {0}", ex.Message);
                }
                catch (Exception)
                {
                    // 即使记录失败，也不能让后台线程的异常终止 Revit。
                    // Even a logging failure must stay on this client thread.
                }
            }
        }

        private static FrameReadResult ReadFrame(NetworkStream stream, byte[] buffer, out string message)
        {
            message = null;
            var frame = new JsonFrameScanner();
            int originalTimeout = stream.ReadTimeout;
            // 从读取缓冲区的大小开始倍增，容量最多恰好达到 64 MB。
            // Starting at the read buffer size lets capacity double up to exactly 64 MB.
            using (var request = new MemoryStream(buffer.Length))
            {
                FrameReadResult result;
                while (true)
                {
                    // 先读完已缓冲的数据。只有在等待未闭合请求的后续分片时才限时。
                    // Drain buffered data first. Only a gap in an open request gets a timeout.
                    bool waiting = request.Length > 0 && !stream.DataAvailable;
                    if (waiting && frame.Complete)
                    {
                        result = FrameReadResult.Complete;
                        break;
                    }

                    int bytesRead;
                    try
                    {
                        if (waiting) stream.ReadTimeout = FrameContinuationTimeoutMs;
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException ex) when (waiting && ex.InnerException is SocketException socketEx
                        && socketEx.SocketErrorCode == SocketError.TimedOut)
                    {
                        return FrameReadResult.TimedOut;
                    }
                    catch (IOException)
                    {
                        result = FrameReadResult.Disconnected;
                        break;
                    }
                    finally
                    {
                        if (waiting) stream.ReadTimeout = originalTimeout;
                    }

                    if (bytesRead == 0)
                    {
                        result = FrameReadResult.Disconnected;
                        break;
                    }
                    if (request.Length + bytesRead > MaxRequestBytes)
                        return FrameReadResult.TooLarge;

                    request.Write(buffer, 0, bytesRead);
                    frame.Feed(buffer, 0, bytesRead);
                }

                if (request.Length > 0)
                    message = Encoding.UTF8.GetString(request.GetBuffer(), 0, (int)request.Length);
                return result;
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
                    string notLoaded = UserToolAccess.RefusalForMissing(registry, request.Method);
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
