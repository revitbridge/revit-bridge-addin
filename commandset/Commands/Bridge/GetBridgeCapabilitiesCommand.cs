using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.Bridge
{
    /// <summary>
    /// <para>报告本 add-in 的协议版本、方法、功能开关与 Revit / 运行时版本。Revit 版本在构造时
    /// （命令加载，处于 API 上下文）读取并缓存；Execute 在 Socket 线程上运行，只返回缓存，
    /// 不碰任何 Revit 对象，不需要活动文档，不弹框。</para>
    /// <para>Reports this add-in's protocol version, methods, feature flags and Revit / runtime
    /// versions. The Revit versions are read and cached in the constructor (command loading,
    /// in API context); Execute runs on the socket thread, returns the cache only, touches no
    /// Revit object, needs no active document and shows no dialog.</para>
    /// </summary>
    public class GetBridgeCapabilitiesCommand : IRevitCommand
    {
        private readonly ICommandRegistry _registry;
        private readonly string _versionNumber;
        private readonly string _versionBuild;
        private readonly string _versionName;
        private readonly string _framework;
        private readonly string _clr;

        public string CommandName => "get_bridge_capabilities";

        public GetBridgeCapabilitiesCommand(UIApplication uiApp)
            : this(uiApp, null)
        {
        }

        /// <summary>
        /// <para>插件的 CommandManager 优先用这个构造函数，传入命令注册表，methods 据此只列出已注册的方法。</para>
        /// <para>The plugin's CommandManager prefers this constructor and passes its command
        /// registry, so that methods lists only what is registered.</para>
        /// </summary>
        public GetBridgeCapabilitiesCommand(UIApplication uiApp, ICommandRegistry registry)
        {
            _registry = registry;

            try
            {
                ReadRevitVersions(uiApp, out _versionNumber, out _versionBuild, out _versionName);
            }
            catch (Exception)
            {
                // 读不到就报 null，不影响加载。
                // Report null rather than fail command loading.
            }

            _framework = RuntimeInformation.FrameworkDescription;
            _clr = Environment.Version.ToString();
        }

        private static void ReadRevitVersions(UIApplication uiApp, out string number, out string build, out string name)
        {
            number = null;
            build = null;
            name = null;

            var app = uiApp?.Application;
            if (app == null)
                return;

            number = app.VersionNumber;
            build = app.VersionBuild;
            name = app.VersionName;
        }

        public object Execute(JObject parameters, string requestId)
        {
            var methods = BridgeInfo.ListMethods(_registry);

            return new JObject
            {
                ["protocol"] = BridgeInfo.ProtocolVersion,
                ["addin_version"] = BridgeInfo.AddinVersion,
                ["methods"] = new JArray(methods),
                ["features"] = new JObject
                {
                    ["check_code"] = methods.Contains("check_code_in_revit"),
                    ["structured_diagnostics"] = true,
                    ["kit"] = false,
                },
                ["revit"] = new JObject
                {
                    ["version_number"] = _versionNumber,
                    ["version_build"] = _versionBuild,
                    ["version_name"] = _versionName,
                },
                ["runtime"] = new JObject
                {
                    ["framework"] = _framework,
                    ["clr"] = _clr,
                },
                ["kit"] = JValue.CreateNull(),
            };
        }
    }
}
