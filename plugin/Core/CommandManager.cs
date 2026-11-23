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

        // User commands registered by this load, and configured user commands this load
        // skipped because "Allow my tools" is off. Published to UserToolAccess at the end.
        private readonly List<string> _loadedUserMethods = new List<string>();
        private readonly List<string> _blockedUserMethods = new List<string>();

        /// <summary>
        /// Manager in charge of loading and managing commands.
        /// </summary>
        /// <param name="commandRegistry"></param>
        /// <param name="logger"></param>
        /// <param name="configManager"></param>
        /// <param name="uiApplication"></param>
        public CommandManager(
            ICommandRegistry commandRegistry,
            ILogger logger,
            ConfigurationManager configManager,
            UIApplication uiApplication)
        {
            _commandRegistry = commandRegistry;
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
            _userToolsAllowed = (_configManager.Config.Settings ?? new ServiceSettings()).LocalToolsEnabled;
            UserToolAccess.Allowed = _userToolsAllowed;
            _loadedUserMethods.Clear();
            _blockedUserMethods.Clear();
            _logger.Info("Allow my tools: {0}", _userToolsAllowed ? "on" : "off");

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

            UserToolAccess.SetLoadedCommands(_loadedUserMethods, _blockedUserMethods);
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
                if (!official && _configManager.Config.Commands.Any(candidate =>
                    IsOfficialCommand(candidate, config.CommandName)))
                {
                    _logger.Warning("User command {0} cannot replace an installed official command.", config.CommandName);
                    return;
                }
                if (!official && !_userToolsAllowed)
                {
                    // Not loaded at all; a request for it is answered with UserToolAccess.ErrorCode.
                    _blockedUserMethods.Add(config.CommandName);
                    _logger.Info("Allow my tools is off; skipped user command {0} from {1}.",
                        config.CommandName, Path.GetFileName(assemblyPath));
                    return;
                }

                if (!File.Exists(assemblyPath))
                {
                    _logger.Error("命令程序集不存在: {0}\nCommand assembly does not exist: {0}", assemblyPath);
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
                                _commandRegistry.RegisterCommand(command);
                                if (!official)
                                    _loadedUserMethods.Add(command.CommandName);
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
    /// <para>State lives in memory: CommandManager sets it on every (re)load from the
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
        private static volatile HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);
        private static volatile HashSet<string> _blocked = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether user tools may run now.</summary>
        public static bool Allowed
        {
            get => _allowed;
            set => _allowed = value;
        }

        /// <summary>User commands the current registry holds, sorted.</summary>
        public static IList<string> LoadedUserMethods =>
            _loaded.OrderBy(name => name, StringComparer.Ordinal).ToList();

        internal static void SetLoadedCommands(IEnumerable<string> loaded, IEnumerable<string> blocked)
        {
            // New sets each time: a socket thread reading the old one is never disturbed.
            _loaded = new HashSet<string>(loaded.Where(name => name != null), StringComparer.Ordinal);
            _blocked = new HashSet<string>(blocked.Where(name => name != null), StringComparer.Ordinal);
        }

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
        /// error message. <paramref name="parameters"/> is read only for
        /// manage_solidified_tools while user tools are off.</para>
        /// </summary>
        public static string RefusalFor(string method, Func<JObject> parameters)
        {
            return Decide(method, parameters, _allowed, _loaded, _blocked, registered: true);
        }

        /// <summary>
        /// <para>For a method the registry does not have: a user command that was not loaded
        /// because user tools are off gets the error message; anything else null (the caller
        /// answers "method not found" as before).</para>
        /// </summary>
        public static string RefusalForMissing(string method)
        {
            return Decide(method, null, _allowed, _loaded, _blocked, registered: false);
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
}
