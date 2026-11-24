using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Utils;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>命令管理器，负责加载和管理命令</para>
    /// <para>Command Manager</para>
    /// </summary>
    public class CommandManager
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;
        private readonly ConfigurationManager _configManager;
        private readonly UIApplication _uiApplication;
        private readonly RevitVersionAdapter _versionAdapter;
        private string _currentVersion;
        private bool _userToolsAllowed;

        // The registry being filled, when it can record which methods are user tools, and
        // the registry a reload replaces (its official instances are kept).
        private readonly BridgeCommandRegistry _bridgeRegistry;
        private readonly BridgeCommandRegistry _reuseFrom;

        /// <summary>
        /// Manager in charge of loading and managing commands.
        /// </summary>
        /// <param name="commandRegistry"></param>
        /// <param name="logger"></param>
        /// <param name="configManager"></param>
        /// <param name="uiApplication"></param>
        /// <param name="reuseFrom">
        /// <para>重载时被替换的注册表：官方命令沿用其中的实例（各自的忙标志与外部事件），不再新建。</para>
        /// <para>On a reload, the registry being replaced: official commands keep their
        /// instances from it (their busy guards and external events) instead of new ones.</para>
        /// </param>
        public CommandManager(
            ICommandRegistry commandRegistry,
            ILogger logger,
            ConfigurationManager configManager,
            UIApplication uiApplication,
            BridgeCommandRegistry reuseFrom = null)
        {
            _commandRegistry = commandRegistry;
            _bridgeRegistry = commandRegistry as BridgeCommandRegistry;
            _reuseFrom = reuseFrom;
            _logger = logger;
            _configManager = configManager;
            _uiApplication = uiApplication;
            _versionAdapter = new RevitVersionAdapter(_uiApplication.Application);
        }

        /// <summary>
        /// <para>加载配置文件中指定的所有命令.</para>
        /// <para>Load all commands specified in the configuration file.</para>
        /// </summary>
        public void LoadCommands()
        {
            _logger.Info("开始加载命令\nStart loading command.");
            string currentVersion = _versionAdapter.GetRevitVersion();
            _currentVersion = currentVersion;
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            // "Allow my tools" as the configuration says now; the services check it per request.
            ServiceSettings settings = _configManager.Config.Settings ?? new ServiceSettings();
            _userToolsAllowed = settings.LocalToolsEnabled;
            UserToolAccess.Allowed = _userToolsAllowed;
            _logger.Info("Allow my tools: {0}{1}", _userToolsAllowed ? "on" : "off",
                settings.LocalToolsIsDefault ? " (not set: the default for this mode)" : "");

            // 从配置加载外部命令
            // Load external commands from the configuration file.
            foreach (var commandConfig in _configManager.Config.Commands)
            {
                // 损坏的文件里可能有 null 项；catch 里也要读 CommandName，所以先跳过。
                // A damaged file can hold a null entry; the catch below reads CommandName too.
                if (commandConfig == null)
                {
                    _logger.Warning("命令列表中有一项为空，已跳过\nSkipped an empty entry in the command list.");
                    continue;
                }

                try
                {
                    if (!commandConfig.Enabled)
                    {
                        _logger.Info("跳过禁用的命令: {0}\nSkipping disabled command: {0}", commandConfig.CommandName);
                        continue;
                    }

                    // 检查版本兼容性
                    // Check Revit version compatibility.
                    if (commandConfig.SupportedRevitVersions != null &&
                        commandConfig.SupportedRevitVersions.Length > 0 &&
                        !_versionAdapter.IsVersionSupported(commandConfig.SupportedRevitVersions))
                    {
                        _logger.Warning("命令 {0} 不支持当前 Revit 版本 {1}，已跳过\nThe command {0} is not supported by the current Revit version ({1}) and it has been skipped.",
                            commandConfig.CommandName, currentVersion);
                        continue;
                    }

                    // 替换路径中的版本占位符
                    // Replace version placeholder strings in paths.
                    commandConfig.AssemblyPath = commandConfig.AssemblyPath.Contains("{VERSION}")
                        ? commandConfig.AssemblyPath.Replace("{VERSION}", currentVersion)
                        : commandConfig.AssemblyPath;

                    // 加载外部命令程序集
                    // Load external command assembly.
                    LoadCommandFromAssembly(commandConfig);
                }
                catch (Exception ex)
                {
                    _logger.Error("加载命令 {0} 失败: {1}\nFailed to load command {0}: {1}", commandConfig.CommandName, ex.Message);
                }
            }

            _logger.Info("命令加载完成\nCommand loading complete.");
        }

        /// <summary>
        /// 加载特定程序集中的特定命令
        /// Loads specific commands in specific assemblies.
        /// </summary>
        /// <param name="config">Configuration class describing the command.</param>
        private void LoadCommandFromAssembly(CommandConfig config)
        {
            try
            {
                // 确定程序集路径
                // Determine the assembly path.
                string assemblyPath = config.AssemblyPath;
                if (!Path.IsPathRooted(assemblyPath))
                {
                    // 如果不是绝对路径，则相对于Commands目录
                    // If it is not an absolute path, then it is relative to the Command's directory.
                    string baseDir = PathManager.GetCommandsDirectoryPath();
                    assemblyPath = Path.Combine(baseDir, assemblyPath);
                }

                assemblyPath = Path.GetFullPath(assemblyPath);
                // 官方命令集按安装目录里的程序集路径认，不按名字认。
                // The official command set is recognised by the assembly path in the install
                // folder, never by a name.
                bool official = IsOfficialAssembly(assemblyPath, _currentVersion);
                bool shadowsOfficial = !official && _configManager.Config.Commands.Any(candidate =>
                    IsOfficialCommand(candidate, config.CommandName));
                switch (DecideLoad(official, shadowsOfficial, File.Exists(assemblyPath), _userToolsAllowed))
                {
                    case LoadDecision.SkipShadowing:
                        _logger.Warning("User command {0} cannot replace an installed official command.", config.CommandName);
                        return;
                    case LoadDecision.SkipMissing:
                        // Not loaded and not recorded: a request for it is "method not found", whatever the switch.
                        _logger.Error("命令程序集不存在: {0}\nCommand assembly does not exist: {0}", assemblyPath);
                        return;
                    case LoadDecision.Block:
                        // Not loaded at all; a request for it is answered with UserToolAccess.ErrorCode.
                        _bridgeRegistry?.AddBlocked(config.CommandName);
                        _logger.Info("Allow my tools is off; skipped user command {0} from {1}.",
                            config.CommandName, Path.GetFileName(assemblyPath));
                        return;
                }

                // 重载时沿用已有的官方命令实例；用户命令在本次 Revit 会话中只创建一次。
                // A reload keeps the official instances it already has; a user command is created
                // (and its Initialize called) once per Revit session and reused afterwards.
                IRevitCommand existing = official
                    ? (_reuseFrom != null && _reuseFrom.TryGetOfficial(config.CommandName, out var kept) ? kept : null)
                    : UserCommandCache.Find(assemblyPath, config.CommandName);
                if (existing != null)
                {
                    Register(existing, official);
                    _logger.Info("沿用已加载的命令 [{0}]\nKept the loaded command [{0}].", config.CommandName);
                    return;
                }

                // 加载程序集
                // Load assembly.
                Assembly assembly = Assembly.LoadFrom(assemblyPath);

                // 查找实现 IRevitCommand 接口的类型
                // Find types that implement the IRevitCommand interface.
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(RevitMCPSDK.API.Interfaces.IRevitCommand).IsAssignableFrom(type) &&
                        !type.IsInterface &&
                        !type.IsAbstract)
                    {
                        try
                        {
                            // 创建命令实例
                            // Create a command instance.
                            RevitMCPSDK.API.Interfaces.IRevitCommand command;

                            // 检查命令是否实现了可初始化接口
                            // Check whether the command implements the initializable interface.
                            if (typeof(IRevitCommandInitializable).IsAssignableFrom(type))
                            {
                                // 创建实例并初始化
                                // Create instance and initialize.
                                command = (IRevitCommand)Activator.CreateInstance(type);
                                ((IRevitCommandInitializable)command).Initialize(_uiApplication);
                            }
                            else
                            {
                                // 优先查找同时接受 UIApplication 与命令注册表的构造函数
                                // （get_bridge_capabilities 用注册表列出已注册的方法），其次只接受 UIApplication 的。
                                // Prefer a constructor that also takes the command registry
                                // (get_bridge_capabilities lists the registered methods from it),
                                // then one that accepts UIApplication only.
                                var registryConstructor = type.GetConstructor(new[] { typeof(UIApplication), typeof(ICommandRegistry) });
                                var constructor = type.GetConstructor(new[] { typeof(UIApplication) });
                                if (registryConstructor != null)
                                {
                                    command = (IRevitCommand)registryConstructor.Invoke(new object[] { _uiApplication, _commandRegistry });
                                }
                                else if (constructor != null)
                                {
                                    command = (IRevitCommand)constructor.Invoke(new object[] { _uiApplication });
                                }
                                else
                                {
                                    // 使用无参构造函数
                                    // Use a parameterless constructor.
                                    command = (IRevitCommand)Activator.CreateInstance(type);
                                }
                            }

                            // 检查命令名称是否与配置匹配
                            // Check whether the command name matches the configuration.
                            if (command.CommandName == config.CommandName)
                            {
                                // get_bridge_capabilities also reports "Allow my tools" and the
                                // user commands loaded (an added "user_tools" field).
                                if (official && command.CommandName == UserToolAccess.CapabilitiesMethod)
                                    command = new CapabilitiesWithUserTools(command);
                                if (!official)
                                    UserCommandCache.Remember(assemblyPath, command);
                                Register(command, official);
                                _logger.Info("成功注册命令 [{0}] 来自 {1}\nRegistered command [{0}] from {1}",
                                    command.CommandName, Path.GetFileName(assemblyPath));
                                break; // 找到匹配的命令后退出循环 - Exit the loop after finding a matching command.
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("创建命令实例失败 [{0}]: {1}\nFailed to create command instance [{0}]: {1}", type.FullName, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载命令程序集失败: {0}\nFailed to load command assembly: {0}", ex.Message);
            }
        }

        private void Register(IRevitCommand command, bool official)
        {
            if (_bridgeRegistry == null)
                _commandRegistry.RegisterCommand(command);
            else if (official)
                _bridgeRegistry.AddOfficial(command);
            else
                _bridgeRegistry.AddUser(command);
        }

        internal enum LoadDecision
        {
            Load,
            SkipShadowing,
            SkipMissing,
            Block,
        }

        /// <summary>
        /// <para>一条已启用命令的去向。用户命令：与官方同名则跳过；文件不存在则跳过（"找不到方法"）；
        /// "允许接入我的工具"关则记为被拦（-32002）；否则加载。</para>
        /// <para>What happens to one enabled entry. A user entry with an official method name
        /// is skipped; a missing DLL is skipped and not recorded ("method not found" whatever
        /// the switch); with "Allow my tools" off an existing user DLL is recorded as blocked
        /// (-32002); everything else loads.</para>
        /// </summary>
        internal static LoadDecision DecideLoad(bool official, bool shadowsOfficial, bool assemblyExists, bool userToolsAllowed)
        {
            if (official)
                return assemblyExists ? LoadDecision.Load : LoadDecision.SkipMissing;
            if (shadowsOfficial)
                return LoadDecision.SkipShadowing;
            if (!assemblyExists)
                return LoadDecision.SkipMissing;
            return userToolsAllowed ? LoadDecision.Load : LoadDecision.Block;
        }

        private bool IsOfficialCommand(CommandConfig candidate, string commandName)
        {
            if (candidate == null || candidate.CommandName != commandName || string.IsNullOrWhiteSpace(candidate.AssemblyPath))
                return false;
            try
            {
                string path = candidate.AssemblyPath.Replace("{VERSION}", _currentVersion);
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Path.GetDirectoryName(typeof(CommandManager).Assembly.Location), "Commands", path);
                return IsOfficialAssembly(path, _currentVersion);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        internal static bool IsOfficialAssembly(string assemblyPath, string version)
        {
            string installed = Path.Combine(Path.GetDirectoryName(typeof(CommandManager).Assembly.Location),
                "Commands", "RevitMCPCommandSet", version, "RevitMCPCommandSet.dll");
            return string.Equals(Path.GetFullPath(assemblyPath), Path.GetFullPath(installed),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <para>get_bridge_capabilities as the command set answers it, plus one added field:
        /// <c>"user_tools": {"allowed": bool, "methods": [user commands loaded, sorted]}</c>.
        /// Existing fields are not touched. Runs on the socket thread; no Revit API.</para>
        /// </summary>
        private sealed class CapabilitiesWithUserTools : IRevitCommand
        {
            private readonly IRevitCommand _inner;

            public CapabilitiesWithUserTools(IRevitCommand inner)
            {
                _inner = inner;
            }

            public string CommandName => _inner.CommandName;

            public object Execute(JObject parameters, string requestId)
            {
                object result = _inner.Execute(parameters, requestId);
                if (result is JObject reply && reply.Property(UserToolAccess.CapabilitiesField) == null)
                    reply[UserToolAccess.CapabilitiesField] = UserToolAccess.Describe();
                return result;
            }
        }
    }

    /// <summary>
    /// <para>"Allow my tools" on the service side. Both services (TCP and WebSocket) ask
    /// <see cref="RefusalFor"/> / <see cref="RefusalForMissing"/> before a request reaches a
    /// command; a refused request is answered with JSON-RPC error <see cref="ErrorCode"/>.
    /// When off, refused are: the user commands (any command whose assembly is not the
    /// installed RevitMCPCommandSet.dll; those are not even loaded) and
    /// manage_solidified_tools "register" and "run". Everything else - every official
    /// method, and manage_solidified_tools "list" and "delete" - is unchanged.
    /// When on, nothing is refused here.</para>
    /// <para>Which methods are user tools is read from the same registry snapshot the
    /// service looked the method up in (<see cref="BridgeCommandRegistry"/>), so a reload
    /// between the lookup and this check cannot let a user method through. Only the switch
    /// itself is shared state: CommandManager sets it on every (re)load from the
    /// configuration, the Revit Bridge panel sets <see cref="Allowed"/> when the checkbox
    /// changes. Read on socket threads, written on the Revit UI thread.</para>
    /// </summary>
    public static class UserToolAccess
    {
        /// <summary>JSON-RPC error for a request refused because "Allow my tools" is off.</summary>
        public const int ErrorCode = -32002;

        public const string SolidifiedToolsMethod = "manage_solidified_tools";
        public const string CapabilitiesMethod = "get_bridge_capabilities";
        public const string CapabilitiesField = "user_tools";

        private static volatile bool _allowed;
        private static volatile BridgeCommandRegistry _published;

        /// <summary>Whether user tools may run now.</summary>
        public static bool Allowed
        {
            get => _allowed;
            set => _allowed = value;
        }

        /// <summary>The registry the running service answers from (set when a service swaps one in).</summary>
        internal static void Publish(ICommandRegistry registry)
        {
            _published = registry as BridgeCommandRegistry;
        }

        /// <summary>User commands the running service's registry holds, sorted.</summary>
        public static IList<string> LoadedUserMethods =>
            _published?.UserMethods ?? new List<string>();

        /// <summary>The "user_tools" object of get_bridge_capabilities.</summary>
        public static JObject Describe()
        {
            return new JObject
            {
                ["allowed"] = _allowed,
                ["methods"] = new JArray(LoadedUserMethods),
            };
        }

        /// <summary>
        /// <para>For a method the registry has: null when the request may go on, else the
        /// error message. <paramref name="registry"/> is the snapshot the method was found in;
        /// <paramref name="parameters"/> is read only for manage_solidified_tools while user
        /// tools are off.</para>
        /// </summary>
        public static string RefusalFor(ICommandRegistry registry, string method, Func<JObject> parameters)
        {
            var snapshot = registry as BridgeCommandRegistry;
            return Decide(method, parameters, _allowed, snapshot?.UserMethodSet, snapshot?.BlockedMethodSet, registered: true);
        }

        /// <summary>
        /// <para>For a method the registry snapshot does not have: a user command that was not
        /// loaded because user tools are off gets the error message; anything else null (the
        /// caller answers "method not found" as before).</para>
        /// </summary>
        public static string RefusalForMissing(ICommandRegistry registry, string method)
        {
            var snapshot = registry as BridgeCommandRegistry;
            return Decide(method, null, _allowed, snapshot?.UserMethodSet, snapshot?.BlockedMethodSet, registered: false);
        }

        /// <summary>The decision itself, without shared state (offline checks call it).</summary>
        public static string Decide(string method, Func<JObject> parameters, bool allowed,
            ICollection<string> loaded, ICollection<string> blocked, bool registered)
        {
            if (allowed || method == null)
                return null;

            if ((loaded != null && loaded.Contains(method)) || (blocked != null && blocked.Contains(method)))
                return $"'{method}' is one of your own tools and 'Allow my tools' is off in this Revit. " +
                       "Turn on 'Allow my tools' in the Revit Bridge panel (Shift+click the Revit Bridge button).";

            if (registered && method == SolidifiedToolsMethod && IsRegisterOrRun(parameters))
                return "Registering and running solidified tools needs 'Allow my tools', which is off in this Revit. " +
                       "Turn it on in the Revit Bridge panel (Shift+click the Revit Bridge button). " +
                       "'list' and 'delete' still work.";

            return null;
        }

        // SolidifiedToolCommand dispatches on parameters.Value<string>("action") ?? "list",
        // lower-cased with the current culture. Read it the same way, so that no spelling
        // that the command would run as register or run (a dotted I under tr-TR, say) slips
        // past. An action it could not read either is left to the command, which fails on it
        // as before.
        private static bool IsRegisterOrRun(Func<JObject> parameters)
        {
            string action;
            try
            {
                action = parameters?.Invoke()?.Value<string>("action") ?? "list";
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException ||
                                       ex is ArgumentException || ex is InvalidOperationException)
            {
                return false;
            }

            string dispatched = action.ToLower(CultureInfo.CurrentCulture);
            return dispatched == "register" || dispatched == "run";
        }
    }

    /// <summary>
    /// <para>服务应答所用的注册表，并记下其中哪些方法是用户工具、哪些用户工具因开关关闭而未加载。
    /// 由 CommandManager 填好后才交给服务，之后不再改动，请求线程无需加锁即可读取。</para>
    /// <para>The registry a service answers from, plus which of its methods are user tools
    /// and which configured user tools were not loaded because "Allow my tools" is off.
    /// CommandManager fills it before the service publishes it; it never changes afterwards,
    /// so request threads read it without locks.</para>
    /// </summary>
    public sealed class BridgeCommandRegistry : RevitCommandRegistry
    {
        private readonly HashSet<string> _official = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _user = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _blocked = new HashSet<string>(StringComparer.Ordinal);

        internal void AddOfficial(IRevitCommand command)
        {
            RegisterCommand(command);
            _official.Add(command.CommandName);
            _user.Remove(command.CommandName);
        }

        internal void AddUser(IRevitCommand command)
        {
            RegisterCommand(command);
            _user.Add(command.CommandName);
        }

        internal void AddBlocked(string commandName)
        {
            if (commandName != null)
                _blocked.Add(commandName);
        }

        public bool IsUserMethod(string name) => name != null && _user.Contains(name);

        public bool IsBlockedUserMethod(string name) => name != null && _blocked.Contains(name);

        internal ICollection<string> UserMethodSet => _user;

        internal ICollection<string> BlockedMethodSet => _blocked;

        /// <summary>The user commands this registry answers, sorted.</summary>
        public IList<string> UserMethods => _user.OrderBy(name => name, StringComparer.Ordinal).ToList();

        /// <summary>An official command instance of this registry, for a reload to keep.</summary>
        public bool TryGetOfficial(string name, out IRevitCommand command)
        {
            command = null;
            return name != null && _official.Contains(name) && TryGetCommand(name, out command);
        }
    }

    /// <summary>
    /// <para>用户命令实例在一次 Revit 会话中只创建一次（构造函数与 Initialize 只调用一次），之后的加载
    /// 沿用同一实例：取消再勾选"允许接入我的工具"或关再开桥接，不会对同一程序集再次初始化。</para>
    /// <para>A user command instance is created once per Revit session (its constructor and
    /// Initialize run once); later loads reuse it, so unticking and ticking "Allow my tools"
    /// again, or switching the bridge off and on, never initialises the same assembly twice.
    /// Revit UI thread only; locked anyway.</para>
    /// </summary>
    internal static class UserCommandCache
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, IRevitCommand> Instances =
            new Dictionary<string, IRevitCommand>(StringComparer.Ordinal);

        private static string Key(string assemblyPath, string commandName) =>
            assemblyPath.ToUpperInvariant() + "|" + commandName;

        internal static IRevitCommand Find(string assemblyPath, string commandName)
        {
            lock (CacheLock)
            {
                return Instances.TryGetValue(Key(assemblyPath, commandName), out var command) ? command : null;
            }
        }

        internal static void Remember(string assemblyPath, IRevitCommand command)
        {
            lock (CacheLock)
            {
                Instances[Key(assemblyPath, command.CommandName)] = command;
            }
        }
    }
}
