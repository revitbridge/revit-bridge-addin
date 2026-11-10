using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.IO;

namespace revit_mcp_plugin.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;

            // 配置文件路径
            // Configuration file path.
            _configPath = PathManager.GetCommandRegistryFilePath();
        }

        /// <summary>
        /// <para>加载配置</para>
        /// <para>Load configuration from a JSON file.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            FrameworkConfig loaded = null;
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    loaded = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    if (loaded == null)
                    {
                        // 空文件反序列化为 null。不自动覆盖，由用户修复或重装。
                        // An empty file deserializes to null. Not overwritten automatically.
                        _logger.Error("配置文件为空，未加载任何命令: {0}\n请修复该文件，或重新运行安装程序恢复它。\n" +
                                      "The configuration file is empty, so no commands were loaded: {0}\n" +
                                      "Repair the file, or re-run the installer to restore it.", _configPath);
                    }
                    else
                    {
                        _logger.Info("已加载配置文件: {0}\nConfiguration file loaded: {0}", _configPath);
                    }
                }
                else
                {
                    _logger.Error("未找到配置文件\nNo configuration file found.");
                }
            }
            catch (Exception ex)
            {
                // 损坏的文件保持原样（不自动覆盖），本次按空配置继续。
                // A damaged file is left as it is (not overwritten); this run goes on with an
                // empty configuration.
                _logger.Error("无法读取配置文件，未加载任何命令: {0}\n请修复该文件，或重新运行安装程序恢复它。\n" +
                              "Could not read the configuration file, so no commands were loaded: {0}\n" +
                              "Repair the file, or re-run the installer to restore it.\nDetails: {1}",
                              _configPath, ex.ToString());
            }

            // 绝不留 null：CommandManager 等直接遍历 Config.Commands。
            // Never null: CommandManager and others iterate Config.Commands directly.
            Config = loaded ?? new FrameworkConfig();
            if (Config.Commands == null)
                Config.Commands = new List<CommandConfig>();

            // 记录加载时间
            // Register load time.
            _lastConfigLoadTime = DateTime.Now;
        }

        ///// <summary>
        ///// <para>重新加载配置</para>
        ///  <para>Reload configuration.</para>
        ///// </summary>
        //public void RefreshConfiguration()
        //{
        //    LoadConfiguration();
        //    _logger.Info("配置已重新加载\nConfiguration has been reloaded.");
        //}

        //public bool HasConfigChanged()
        //{
        //    if (!File.Exists(_configPath))
        //        return false;

        //    DateTime lastWrite = File.GetLastWriteTime(_configPath);
        //    return lastWrite > _lastConfigLoadTime;
        //}

        private DateTime _lastConfigLoadTime;
    }
}
