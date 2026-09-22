using System.Reflection;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// <para>插件自身的版本信息（来自 AssemblyInfo.cs），用于配对请求的 addin_version 与 User-Agent。</para>
    /// <para>This add-in's own version (from AssemblyInfo.cs), used for the pairing
    /// request's addin_version and the WebSocket User-Agent.</para>
    /// </summary>
    public static class AddinInfo
    {
        public const string Product = "revit-bridge-addin";

        private static string _version;

        /// <summary>"major.minor.build", e.g. "0.2.0".</summary>
        public static string Version
        {
            get
            {
                if (_version == null)
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    _version = v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
                }
                return _version;
            }
        }

        /// <summary>e.g. "revit-bridge-addin/0.2.0".</summary>
        public static string UserAgent => $"{Product}/{Version}";
    }
}
