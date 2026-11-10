using System;
using System.IO;
using System.Text;
using System.Threading;
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
    /// <para>同一进程内的读改写串行执行；写入先写临时文件再整体替换，中途崩溃不会留下空文件。</para>
    /// <para>Load-modify-save is serialized within the process, and a write goes to a
    /// temporary file that then replaces the target, so a crash mid-write cannot leave
    /// an empty or partial file.</para>
    /// </summary>
    public static class SettingsStore
    {
        // 设置窗口（UI 线程）与 WebSocket 工作线程都会写这个文件。
        // Both the settings window (UI thread) and the WebSocket worker thread write the file.
        private static readonly object FileLock = new object();

        public static FrameworkConfig Load()
        {
            lock (FileLock)
            {
                string configPath = PathManager.GetCommandRegistryFilePath();
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    return JsonConvert.DeserializeObject<FrameworkConfig>(json) ?? new FrameworkConfig();
                }
                return new FrameworkConfig();
            }
        }

        public static void Save(FrameworkConfig config)
        {
            lock (FileLock)
            {
                string configPath = PathManager.GetCommandRegistryFilePath();
                string output = JsonConvert.SerializeObject(config, Formatting.Indented);
                WriteAllTextAtomically(configPath, output, overwrite: true);
            }
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
                lock (FileLock)
                {
                    var config = Load();
                    if (config.Settings == null)
                        config.Settings = new ServiceSettings();

                    change(config.Settings);
                    Save(config);
                }
                return true;
            }
            catch (Exception ex)
            {
                logger?.Warning("写入设置失败 / Failed to write settings: {0}", ex.Message);
                System.Diagnostics.Trace.WriteLine($"Failed to write settings: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// <para>先写同目录下的临时文件并刷到磁盘，再改名替换目标（目标不存在时直接改名）。
        /// 失败时删除临时文件并抛出。编码与原来相同：UTF-8，无 BOM。</para>
        /// <para>Write a temporary file in the same directory, flush it to disk, then rename
        /// it over the target (or to it, when there is none). On failure the temporary file
        /// is removed and the exception is rethrown. Same encoding as before: UTF-8, no BOM.</para>
        /// </summary>
        /// <param name="overwrite">false: keep a target that already exists (IOException).</param>
        internal static void WriteAllTextAtomically(string path, string contents, bool overwrite)
        {
            string tempPath = Path.Combine(Path.GetDirectoryName(path),
                Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(true);
                }
                MoveWithRetry(tempPath, path, overwrite);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    System.Diagnostics.Trace.WriteLine($"Could not delete the temporary file {tempPath}: {cleanupEx.Message}");
                }
                throw;
            }
        }

        // 另一线程或进程正短暂打开目标文件时，改名会被拒绝：稍等后重试几次（共约 0.5 秒）。
        // While another thread or process has the target open for a moment the rename is
        // refused: wait briefly and retry a few times (about 0.5 s in all).
        private const int MoveAttempts = 5;
        private const int MoveRetryDelayMs = 50;

        private static void MoveWithRetry(string tempPath, string path, bool overwrite)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
                                           attempt < MoveAttempts &&
                                           (overwrite || !File.Exists(path)))
                {
                    Thread.Sleep(MoveRetryDelayMs * attempt);
                }
            }
        }
    }
}
