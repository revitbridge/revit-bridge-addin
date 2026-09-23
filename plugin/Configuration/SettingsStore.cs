using System;
using System.IO;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>读写 <c>Commands\commandRegistry.json</c>。设置窗口与运行中的服务都用它，
    /// 保证只有一条写路径（写整份文件，保留 commands 数组）。</para>
    /// <para>Reads and writes <c>Commands\commandRegistry.json</c>. Used by the settings
    /// window and by the running services so there is a single write path; the whole
    /// file is rewritten, keeping the commands array.</para>
    /// </summary>
    public static class SettingsStore
    {
        public static FrameworkConfig Load()
        {
            string configPath = PathManager.GetCommandRegistryFilePath();
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<FrameworkConfig>(json) ?? new FrameworkConfig();
            }
            return new FrameworkConfig();
        }

        public static void Save(FrameworkConfig config)
        {
            string configPath = PathManager.GetCommandRegistryFilePath();
            string output = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configPath, output);
        }

        /// <summary>
        /// <para>改动 settings 并落盘。失败只记录，不抛给调用方（后台线程也会调用）。</para>
        /// <para>Change settings and persist them. Failures are logged, not thrown: this
        /// is also called from background threads.</para>
        /// </summary>
        /// <returns>true when the file was written.</returns>
        public static bool Update(Action<ServiceSettings> change, ILogger logger = null)
        {
            try
            {
                var config = Load();
                if (config.Settings == null)
                    config.Settings = new ServiceSettings();

                change(config.Settings);
                Save(config);
                return true;
            }
            catch (Exception ex)
            {
                logger?.Warning("写入设置失败 / Failed to write settings: {0}", ex.Message);
                System.Diagnostics.Trace.WriteLine($"Failed to write settings: {ex.Message}");
                return false;
            }
        }
    }
}
