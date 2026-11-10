using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>读写 <c>Commands\commandRegistry.json</c>。设置窗口与运行中的服务都用它，
    /// 保证只有一条写路径：每次只改自己那部分（settings 或 commands），文件其余内容原样保留。</para>
    /// <para>Reads and writes <c>Commands\commandRegistry.json</c>. Used by the settings
    /// window and by the running services so there is a single write path; each write
    /// changes only its part (settings or commands) and keeps the rest of the file.</para>
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

        /// <summary>
        /// <para>在同一把锁内读文件、改 settings、写回。只写 ServiceSettings 认识的字段；settings 里和
        /// 文件顶层本版本不认识的属性原样保留。失败时抛出且文件不变——文件为空、为 null、不是对象或
        /// 不是有效 JSON 时也一样（不会用默认值重写）。</para>
        /// <para>Read the file, change the settings and write it back under one lock. Only
        /// the fields ServiceSettings knows are written; properties this version does not
        /// know, in "settings" and at the top level, stay as they are. Throws on failure and
        /// leaves the file unchanged - also when it is empty, "null", not an object or not
        /// valid JSON (it is never rewritten with defaults).</para>
        /// </summary>
        /// <returns>The settings as written.</returns>
        public static ServiceSettings UpdateSettings(Action<ServiceSettings> change)
        {
            lock (FileLock)
            {
                string configPath = PathManager.GetCommandRegistryFilePath();
                string existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
                ServiceSettings written;
                string output = ApplySettingsChange(existing, change, out written);
                WriteAllTextAtomically(configPath, output, overwrite: true);
                return written;
            }
        }

        /// <summary>
        /// <para>UpdateSettings 的文本核心：输入原文件内容，返回改过 settings 的新内容。</para>
        /// <para>Text core of <see cref="UpdateSettings"/>: takes the file's text and returns
        /// it with the changed settings merged into its "settings" object.</para>
        /// </summary>
        public static string ApplySettingsChange(string registryJson, Action<ServiceSettings> change, out ServiceSettings written)
        {
            JObject root = ParseRegistryObject(registryJson);
            JObject settings = root["settings"] as JObject;
            if (settings == null)
            {
                settings = new JObject();
                root["settings"] = settings;
            }

            ServiceSettings current = settings.ToObject<ServiceSettings>() ?? new ServiceSettings();
            change(current);
            foreach (JProperty property in JObject.FromObject(current).Properties())
                settings[property.Name] = property.Value;

            written = current;
            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// <para>改动 settings 并落盘（见 UpdateSettings）。失败只记录，不抛给调用方：后台线程也会调用。</para>
        /// <para>Change settings and persist them (see <see cref="UpdateSettings"/>). Failures
        /// are logged, not thrown: this is also called from background threads.</para>
        /// </summary>
        /// <returns>true when the file was written.</returns>
        public static bool Update(Action<ServiceSettings> change, ILogger logger = null)
        {
            try
            {
                UpdateSettings(change);
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
        /// <para>只替换 commands 数组；文件里的其他内容（settings，以及本版本不认识的属性）原样保留。
        /// 命令集页面用它保存，避免把连接设置与配对一起抹掉。</para>
        /// <para>Replace the commands array and keep everything else in the file as it is
        /// (settings, and properties this version does not know). The Command sets page saves
        /// through this, so saving it no longer wipes the connection settings and the pairing.</para>
        /// </summary>
        /// <exception cref="InvalidDataException">The file holds JSON that is not an object; it is left unchanged.</exception>
        /// <exception cref="JsonException">The file is not valid JSON; it is left unchanged.</exception>
        public static void SaveCommands(IEnumerable<CommandConfig> commands)
        {
            lock (FileLock)
            {
                string configPath = PathManager.GetCommandRegistryFilePath();
                string existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
                WriteAllTextAtomically(configPath, ReplaceCommands(existing, commands), overwrite: true);
            }
        }

        /// <summary>
        /// <para>SaveCommands 的文本核心：输入原文件内容，返回只换了 "commands" 的新内容。</para>
        /// <para>Text core of <see cref="SaveCommands"/>: takes the file's text and returns it
        /// with only "commands" replaced. Other properties keep their values and order; date-like
        /// strings stay strings. Null (no file yet) starts a new object.</para>
        /// </summary>
        public static string ReplaceCommands(string registryJson, IEnumerable<CommandConfig> commands)
        {
            JObject root = ParseRegistryObject(registryJson);
            root["commands"] = JArray.FromObject(commands ?? new List<CommandConfig>());
            return root.ToString(Formatting.Indented);
        }

        // null = no file yet: a new object. An empty, "null", non-object or invalid file is
        // refused (the file stays as it is), never replaced by defaults.
        private static JObject ParseRegistryObject(string registryJson)
        {
            if (registryJson == null)
                return new JObject();
            if (string.IsNullOrWhiteSpace(registryJson))
                throw new InvalidDataException(
                    "commandRegistry.json is empty, so it was left unchanged. Repair it, or re-run the installer to restore it.");
            var token = JsonConvert.DeserializeObject<JToken>(registryJson,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            var root = token as JObject;
            if (root == null)
                throw new InvalidDataException(
                    "commandRegistry.json does not contain a JSON object, so it was left unchanged.");
            return root;
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
                    ReplaceOrMove(tempPath, path, overwrite);
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

        // 用 MoveFileEx 改名覆盖：.NET 8 的 File.Move(源, 目标, true) 内部就是这个调用，而该重载在
        // .NET Framework 4.8 上不存在；File.Replace 在读者占用目标时会留下 "~RF*.TMP"（旧内容，含 token）。
        // Rename over the target with MoveFileEx: .NET 8's File.Move(source, target, true) makes
        // this same call, but that overload does not exist on .NET Framework 4.8 (R20-R24), and
        // File.Replace leaves a "<name>~RF*.TMP" copy of the old contents (token included) behind
        // when a reader has the target open.
        private const int MOVEFILE_REPLACE_EXISTING = 0x1;
        private const int MOVEFILE_WRITE_THROUGH = 0x8;
        private const int ERROR_ACCESS_DENIED = 5;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        private static void ReplaceOrMove(string tempPath, string path, bool overwrite)
        {
            int flags = MOVEFILE_WRITE_THROUGH | (overwrite ? MOVEFILE_REPLACE_EXISTING : 0);
            if (MoveFileEx(tempPath, path, flags))
                return;

            int error = Marshal.GetLastWin32Error();
            string message = new Win32Exception(error).Message + " (" + path + ")";
            if (error == ERROR_ACCESS_DENIED)
                throw new UnauthorizedAccessException(message);
            throw new IOException(message, unchecked((int)0x80070000) | error);
        }
    }
}
