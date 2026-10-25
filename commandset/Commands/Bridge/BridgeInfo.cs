using System.IO;
using System.Reflection;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.Bridge
{
    /// <summary>
    /// <para>bridge 与 add-in 之间协议的元信息：协议版本、add-in 版本、本程序集实现的方法名。</para>
    /// <para>Metadata of the bridge/add-in protocol: protocol version, add-in version and the
    /// method names this assembly implements.</para>
    /// </summary>
    public static class BridgeInfo
    {
        /// <summary>
        /// <para>协议版本：2 = 有 get_bridge_capabilities 与 check_code_in_revit。</para>
        /// <para>Protocol version: 2 = get_bridge_capabilities and check_code_in_revit exist.</para>
        /// </summary>
        public const int ProtocolVersion = 2;

        // 插件程序集（RevitMCPPlugin.dll）带有 add-in 的正式版本号（AssemblyInfo.cs，与 CHANGELOG 同步）。
        // The plugin assembly (RevitMCPPlugin.dll) carries the add-in's release version
        // (AssemblyInfo.cs, kept in sync with CHANGELOG.md).
        private const string PluginAssemblyName = "RevitMCPPlugin";

        private static readonly Lazy<string> AddinVersionValue = new Lazy<string>(ResolveAddinVersion);
        private static readonly Lazy<string[]> OwnCommandNames = new Lazy<string[]>(DiscoverOwnCommandNames);

        /// <summary>"major.minor.build" of the loaded revit-bridge add-in, e.g. "0.2.0"; "0.0.0" when not found.</summary>
        public static string AddinVersion => AddinVersionValue.Value;

        /// <summary>
        /// <para>可调用的方法名（排序）：本程序集实现的命令名；有命令注册表时只留已注册的。</para>
        /// <para>Callable method names, sorted: the commands this assembly implements, narrowed to
        /// the registered ones when a command registry is available.</para>
        /// </summary>
        public static List<string> ListMethods(ICommandRegistry registry)
        {
            return OwnCommandNames.Value
                .Where(name => registry == null || IsRegistered(registry, name))
                .ToList();
        }

        private static bool IsRegistered(ICommandRegistry registry, string name)
        {
            try
            {
                return registry.TryGetCommand(name, out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string[] DiscoverOwnCommandNames()
        {
            Type[] types;
            try
            {
                types = typeof(BridgeInfo).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IRevitCommand).IsAssignableFrom(type))
                    continue;

                try
                {
                    // 不运行构造函数（有的会创建 ExternalEvent）；各命令的 CommandName 都是常量属性。
                    // Do not run constructors (some create ExternalEvents); every CommandName here
                    // is a constant property.
                    var command = (IRevitCommand)CreateUninitialized(type);
                    string name = command.CommandName;
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
                catch (Exception)
                {
                    // 读不出名字的类型跳过。
                    // Skip a type whose name cannot be read.
                }
            }
            return names.ToArray();
        }

        private static object CreateUninitialized(Type type)
        {
#if NET5_0_OR_GREATER
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
#else
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
#endif
        }

        private static string ResolveAddinVersion()
        {
            try
            {
                // 优先取与本程序集同属一个安装目录的插件程序集（与上游 mcp-servers-for-revit 并存时也不会取错）。
                // Prefer the plugin assembly whose folder contains this assembly, so a side-by-side
                // upstream mcp-servers-for-revit (same assembly name) is not picked by mistake.
                string ownPath = typeof(BridgeInfo).Assembly.Location ?? string.Empty;
                var candidates = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && string.Equals(a.GetName().Name, PluginAssemblyName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Assembly plugin = candidates.FirstOrDefault(a => ContainsPath(a.Location, ownPath))
                    ?? (candidates.Count == 1 ? candidates[0] : null);

                var version = plugin?.GetName().Version;
                return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch (Exception)
            {
                return "0.0.0";
            }
        }

        private static bool ContainsPath(string pluginLocation, string ownPath)
        {
            if (string.IsNullOrEmpty(pluginLocation) || string.IsNullOrEmpty(ownPath))
                return false;

            string folder = Path.GetDirectoryName(pluginLocation);
            if (string.IsNullOrEmpty(folder))
                return false;

            folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return ownPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
    }
}
