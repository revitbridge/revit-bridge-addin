using RevitMCPSDK.API.Interfaces;
using System;
using System.IO;
using System.Text;

namespace revit_mcp_plugin.Utils
{
    public class Logger : ILogger
    {
        private readonly string _logFilePath;
        private LogLevel _currentLogLevel = LogLevel.Info;

        public Logger()
        {
            _logFilePath = Path.Combine(PathManager.GetLogsDirectoryPath(), $"mcp_{DateTime.Now:yyyyMMdd}.log");

        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            if (level < _currentLogLevel)
                return;

            string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {formattedMessage}";

            // 输出到 Debug 窗口
            // Output to debug window.
            System.Diagnostics.Debug.WriteLine(logEntry);

            // 写入日志文件
            // Write to the logfile.
            try
            {
                // 显式 UTF-8：内容本来就是 UTF-8，但没有 BOM 时 PowerShell 5.1 等按本地
                // 代码页读取，中文显示为乱码。新文件带 BOM，已存在的文件只追加、不改写。
                // Explicit UTF-8: the text already was UTF-8, but without a BOM
                // PowerShell 5.1 and friends read it in the ANSI code page and show the
                // Chinese as mojibake. New files get the BOM; existing files are only
                // appended to, never rewritten.
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 如果写入日志文件失败，不抛出异常
                // If writing to the logfile fails, do not throw an exception.
            }
        }

        public void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }
    }
}
