using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.ExecuteDynamicCode;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>在打开的 Revit 里测试一个编译好的插件命令，不安装、不留改动。顺序（任务书 §4.3，硬要求）：
    /// 校验并编译读回片段；DLL 读成字节、算哈希、写进新的影子目录（原文件不被占用）；写输入文件；缩放（此后不动视图）；
    /// 在 API 上下文里把全部点选换算成屏幕坐标、载入命令、投递加载器；等加载器报告已调用 Execute 后在本线程点选
    /// （丢了的最后一次点击最多重点到三次）；超时则截图、按 Esc；加载器里读回、统计、还原；读结果文件。</para>
    /// <para>Tests a built add-in command in the open Revit without installing it and without keeping
    /// anything. Order (work plan section 4.3, required): check and compile the read-back; read the DLL
    /// as bytes, hash them and write them into a fresh shadow folder (the original file is never held
    /// open); write the input file; zoom (the view is not touched after this); in API context map every
    /// pick to a screen pixel, load the command and post the loader; once the loader reports that it
    /// called Execute, click the picks from this thread (a lost last click is repeated, three attempts at
    /// most); on a timeout take a picture and press Escape; read back, count and restore in the loader;
    /// read the result file.</para>
    /// </summary>
    public class TestAddinCommand : ApiCallCommandBase
    {
        /// <summary>The loader's AddInId in revit-bridge.addin, in lower case (LookupCommandId needs lower case).</summary>
        public const string LoaderAddInId = "25a0ccca-d8aa-4ea4-9a87-1e4889a4b605";

        private const int StepTimeoutMs = 30000;
        private const int LoaderStartMs = 20000;
        private const int LoaderFinishMs = 30000;
        private const int ClickDelayMs = 1500;
        private const int PickPauseMs = 1000;
        private const int RetryAfterMs = 10000;
        private const int MaxAttempts = 3;
        private const int MaxUndoSteps = 25;
        private const double ZoomMarginMm = 2500;

        public override string CommandName => "test_addin_command_in_revit";

        public TestAddinCommand(UIApplication uiApp)
            : base(uiApp)
        {
            // 加载器（插件程序集里）经这个数据槽找到本命令集的入口。
            // The loader (in the plugin assembly) finds this command set's entry through this slot.
            AppDomain.CurrentDomain.SetData(AddinTestSession.LoaderSlot,
                (Func<ExternalCommandData, ElementSet, Result>)AddinTestSession.RunFromLoader);
        }

        public override object Execute(JObject parameters, string requestId)
        {
            AddinTestRequest request = AddinTestRequest.Parse(parameters);
            if (!AddinTestSession.TryBegin(out AddinTestSession session))
                throw new InvalidOperationException("Another add-in test is running in Revit; this request was refused.");

            var reply = new AddinTestResult { Restore = request.Restore };
            try
            {
                Run(request, session, reply);
            }
            catch (Exception ex)
            {
                reply.AddError("test: " + CodeExecutor.FormatRuntimeError(ex));
                if (!session.CancelIfNotStarted() && !session.Finished.WaitOne(0))
                    reply.RestoredClean = false;
            }
            finally
            {
                session.ReleaseByCaller();
            }
            return reply;
        }

        private void Run(AddinTestRequest request, AddinTestSession session, AddinTestResult reply)
        {
            // 0. 读回片段先编译：编不过就什么也不运行。
            // 0. The read-back compiles first: if it does not, nothing runs.
            if (request.ReadbackCode != null)
            {
                CompileOutcome readback = CodeExecutor.Compile(request.ReadbackCode);
                if (!readback.Compiled)
                {
                    reply.ReadbackError = "readback_code does not compile: " + readback.FormatErrorMessage();
                    reply.LoadError = reply.ReadbackError;
                    return;
                }
                session.Readback = readback;
            }

            // 1. DLL：读成字节、哈希、写进影子目录。被测命令看到的是影子目录里的副本，输入与结果文件也在那里。
            // 1. The DLL: bytes, hash, shadow folder. The command sees the copy there, and its input and
            //    result files live next to that copy.
            string dll = request.Dll;
            if (!Path.IsPathRooted(dll) || !File.Exists(dll))
            {
                reply.LoadError = "DLL not found: " + dll;
                reply.Screenshot = Screenshot(reply, "addin-test-load");
                return;
            }
            byte[] bytes = File.ReadAllBytes(dll);
            reply.DllSha256 = Sha256(bytes);
            string shadowDll = ShadowCopy(dll, bytes, reply, out string shadowFolder);
            session.ShadowDirectory = shadowFolder;
            string baseName = Path.GetFileNameWithoutExtension(dll);
            string resultPath = Path.Combine(shadowFolder, baseName + ".result.json");
            if (request.Input != null)
            {
                File.WriteAllText(Path.Combine(shadowFolder, baseName + ".input.json"),
                    request.Input.ToString(Formatting.Indented), new UTF8Encoding(false));
            }

            // 2. 缩放，然后让 Revit 重画。此后不再动视图。
            // 2. Zoom, then let Revit redraw. The view is not touched after this.
            if (request.ZoomTo.HasValue)
            {
                try
                {
                    CallApi(app => { ViewTools.Zoom(app, request.ZoomTo, ZoomMarginMm); return null; }, StepTimeoutMs);
                }
                catch (Exception ex)
                {
                    reply.LoadError = "zoom_to: " + Message(ex);
                    return;
                }
                Thread.Sleep(1000);
            }

            // 3. API 上下文：校验视图、换算全部点选、载入命令、投递加载器。
            // 3. API context: check the view, map every pick, load the command, post the loader.
            Prepared prepared;
            try
            {
                prepared = (Prepared)CallApi(app => Prepare(app, request, session, shadowDll), StepTimeoutMs);
            }
            catch (Exception ex)
            {
                reply.LoadError = Message(ex);
                return;
            }
            reply.Loaded = prepared.Loaded;
            if (!prepared.Posted)
            {
                reply.LoadError = prepared.Error;
                if (!prepared.Loaded)
                    reply.Screenshot = Screenshot(reply, "addin-test-load");
                return;
            }

            // 4. 等加载器调用被测命令。没开始就撤回：什么也没运行。
            // 4. Wait for the loader to call the command. If it does not start, withdraw: nothing ran.
            if (!session.Executing.WaitOne(LoaderStartMs) && session.CancelIfNotStarted())
            {
                reply.LoadError = $"The test loader did not start within {LoaderStartMs / 1000} s; nothing ran.";
                return;
            }
            session.Executing.WaitOne();
            reply.RestoredClean = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(request.TimeoutSeconds);

            // 5. 点选：先等命令真正进入等待，再置前、按序点击。
            // 5. Picks: give the command time to reach its prompt, bring Revit to the front, click in order.
            session.CommandReturned.WaitOne(ClickDelayMs);
            reply.ForegroundOk = Desktop.BringToFront(prepared.MainWindow);
            DateTime lastClick = DateTime.UtcNow;
            for (int i = 0; i < request.Picks.Count && !session.CommandReturned.WaitOne(0); i++)
            {
                PickRequest pick = request.Picks[i];
                var done = new PickDone { Requested = Requested(pick), Screen = prepared.Points[i], Attempts = 1 };
                if (pick.IsEscape)
                    Desktop.Key(Desktop.VkEscape);
                else
                    Desktop.Click(done.Screen[0], done.Screen[1]);
                reply.PicksDone.Add(done);
                lastClick = DateTime.UtcNow;
                if (i < request.Picks.Count - 1)
                    Thread.Sleep(PickPauseMs);
            }

            // 6. 等命令返回；最后一次点击丢了（命令还在等）就重点。
            // 6. Wait for the command; repeat a lost last click (the command still waits).
            PickDone last = reply.PicksDone.Count == request.Picks.Count ? reply.PicksDone.LastOrDefault() : null;
            while (!session.CommandReturned.WaitOne(1000) && DateTime.UtcNow < deadline)
            {
                if (last != null && last.Screen != null && last.Attempts < MaxAttempts &&
                    (DateTime.UtcNow - lastClick).TotalMilliseconds >= RetryAfterMs)
                {
                    reply.ForegroundOk = Desktop.BringToFront(prepared.MainWindow);
                    Desktop.Click(last.Screen[0], last.Screen[1]);
                    last.Attempts++;
                    lastClick = DateTime.UtcNow;
                }
            }

            // 7. 超时：截图存档，按 Esc 让命令退出。
            // 7. Timeout: keep a picture, press Escape until the command ends.
            if (!session.CommandReturned.WaitOne(0))
            {
                reply.TimedOut = true;
                reply.Screenshot = Screenshot(reply, "addin-test-timeout");
                for (int round = 0; round < 2 && !session.CommandReturned.WaitOne(0); round++)
                {
                    Desktop.BringToFront(prepared.MainWindow);
                    for (int k = 0; k < 4; k++)
                        Desktop.Key(Desktop.VkEscape);
                    session.CommandReturned.WaitOne(10000);
                }
                if (!session.CommandReturned.WaitOne(0))
                {
                    reply.AddError("The command did not end after Escape; the test is still running in Revit and restores the model when the command ends.");
                    return;
                }
            }

            // 8. 加载器读回、统计并（事务组方式）回滚。
            // 8. The loader reads back, counts and (group rollback) rolls back.
            if (!session.Finished.WaitOne(LoaderFinishMs))
            {
                reply.Returned = session.Returned;
                reply.AddError($"The loader did not finish within {LoaderFinishMs / 1000} s after the command returned; the model may not be restored yet.");
                return;
            }
            reply.Returned = session.Returned;
            reply.CommandMessage = session.CommandMessage;
            reply.Changes = session.Changes ?? reply.Changes;
            reply.Failures = session.Recorder?.Failures ?? reply.Failures;
            reply.Readback = session.ReadbackJson;
            reply.ReadbackError = session.ReadbackError;
            reply.RolledBack = session.RolledBack;
            reply.AddError(session.LoaderError);
            reply.AddError(session.Collector?.Problem);
            reply.ResultFile = ReadResultFile(resultPath, reply);

            // 9. 还原：事务组方式已在加载器里完成；撤销方式在这里逐次投递撤销。
            // 9. Restore: the group rollback happened in the loader; undo is posted here, one at a time.
            if (request.Restore == AddinTestRequest.RestoreUndo)
                RestoreByUndo(session, reply);
            reply.RestoredClean = session.RestoredClean;
            reply.AddError(session.RestoreError);
        }

        private sealed class Prepared
        {
            public bool Loaded;
            public bool Posted;
            public string Error = string.Empty;
            public IntPtr MainWindow;
            public List<int[]> Points = new List<int[]>();
        }

        private static Prepared Prepare(UIApplication app, AddinTestRequest request, AddinTestSession session, string shadowDll)
        {
            var prepared = new Prepared();

            // 视图与点选在载入之前检查：不合格就什么也不载入。
            // The view and the picks are checked before loading: when they fail nothing is loaded.
            try
            {
                UIView uiView = ViewTools.ActiveUIView(app, out View view);
                if (!(view is ViewPlan))
                    throw new InvalidOperationException("The active view is not a plan view: " + view.Name + " (" + view.ViewType + ").");
                Document document = app.ActiveUIDocument.Document;
                for (int i = 0; i < request.Picks.Count; i++)
                {
                    PickRequest pick = request.Picks[i];
                    if (pick.IsEscape)
                    {
                        prepared.Points.Add(null);
                        continue;
                    }
                    XYZ point = pick.ElementId.HasValue
                        ? ViewTools.PickPoint(document, view, pick.ElementId.Value, pick.Fraction)
                        : new XYZ(pick.PointMm[0] / ViewTools.MillimetresPerFoot, pick.PointMm[1] / ViewTools.MillimetresPerFoot, pick.PointMm[2] / ViewTools.MillimetresPerFoot);
                    prepared.Points.Add(ViewTools.ScreenPoint(uiView, view, point, "picks[" + i + "]"));
                }
            }
            catch (InvalidOperationException ex)
            {
                prepared.Error = ex.Message;
                return prepared;
            }

            // 载入：按文件载入影子副本（LoadFile），依赖从影子目录补。
            // Load: the shadow copy by file (LoadFile), dependencies from the shadow folder.
            IExternalCommand command;
            AppDomain.CurrentDomain.AssemblyResolve += session.ResolveFromShadow;
            try
            {
                Assembly assembly = Assembly.LoadFile(shadowDll);
                Type type = assembly.GetType(request.ClassName, false);
                if (type == null)
                {
                    prepared.Error = $"Class {request.ClassName} not found in {Path.GetFileName(shadowDll)}. IExternalCommand classes there: {string.Join(", ", CommandClasses(assembly))}";
                    return prepared;
                }
                if (!typeof(IExternalCommand).IsAssignableFrom(type) || type.IsAbstract)
                {
                    prepared.Error = $"{request.ClassName} is not a concrete IExternalCommand.";
                    return prepared;
                }
                command = (IExternalCommand)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                prepared.Error = "Loading failed: " + CodeExecutor.FormatRuntimeError(ex);
                return prepared;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= session.ResolveFromShadow;
            }
            prepared.Loaded = true;

            // 投递加载器（清单里 Type="Command" 的那一条，按小写 AddInId 查）。
            // Post the loader (the Type="Command" entry of the manifest, looked up by lower-case AddInId).
            RevitCommandId loader = RevitCommandId.LookupCommandId(LoaderAddInId);
            if (loader == null)
            {
                prepared.Error = "The revit-bridge test loader is not registered: revit-bridge.addin of this version lists it; reinstall and restart Revit.";
                return prepared;
            }
            if (!app.CanPostCommand(loader))
            {
                prepared.Error = "Revit cannot start the test loader now.";
                return prepared;
            }
            session.Command = command;
            session.Restore = request.Restore;
            app.PostCommand(loader);
            prepared.Posted = true;
            prepared.MainWindow = app.MainWindowHandle;
            return prepared;
        }

        private void RestoreByUndo(AddinTestSession session, AddinTestResult reply)
        {
            int undone = 0;
            for (int step = 0; step < MaxUndoSteps; step++)
            {
                bool posted;
                try
                {
                    int done = undone;
                    posted = (bool)CallApi(app => session.PostUndoIfNeeded(app, done), StepTimeoutMs);
                }
                catch (Exception ex)
                {
                    reply.AddError("undo: " + Message(ex));
                    break;
                }
                if (!posted)
                    break;
                undone++;
                Thread.Sleep(1500);
            }

            // 最后一次撤销可能还没执行完：再等几秒，直到没有未撤回的改动。
            // The last Undo may not have run yet: wait a few seconds for the outstanding changes to clear.
            for (int wait = 0; wait < 5; wait++)
            {
                int outstanding;
                try
                {
                    outstanding = (int)CallApi(app => session.Collector?.CountOutstanding() ?? 0, StepTimeoutMs);
                }
                catch (Exception ex)
                {
                    reply.AddError("undo check: " + Message(ex));
                    break;
                }
                if (outstanding == 0)
                    break;
                Thread.Sleep(1000);
            }
            try
            {
                CallApi(app => { session.FinishUndo(); return null; }, StepTimeoutMs);
            }
            catch (Exception ex)
            {
                reply.AddError("undo finish: " + Message(ex));
            }
            reply.Undone = undone;
        }

        private static JToken ReadResultFile(string path, AddinTestResult reply)
        {
            if (!File.Exists(path))
                return null;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                reply.AddError("reading the result file failed: " + ex.Message);
                return null;
            }
            try
            {
                return JToken.Parse(text);
            }
            catch (JsonException)
            {
                return new JObject { ["unparsed"] = text };
            }
        }

        private static string ShadowCopy(string dll, byte[] bytes, AddinTestResult reply, out string folder)
        {
            string root = Path.Combine(Path.GetTempPath(), "revit-bridge", "addin-test");
            RemoveOldShadowFolders(root);
            folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);

            string source = Path.GetDirectoryName(dll);
            string baseName = Path.GetFileNameWithoutExtension(dll);
            var skipped = new List<string>();
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.Equals(Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(baseName + ".input.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(baseName + ".result.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Copy(file, Path.Combine(folder, name));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    skipped.Add(name + " (" + ex.Message + ")");
                }
            }
            if (skipped.Count > 0)
                reply.AddError("files next to the DLL not copied: " + string.Join("; ", skipped));

            // 被测的就是算过哈希的这些字节。
            // What runs is exactly the bytes that were hashed.
            string target = Path.Combine(folder, Path.GetFileName(dll));
            File.WriteAllBytes(target, bytes);
            return target;
        }

        // 上次及更早的影子目录：本进程已载入的删不掉，留到下次。
        // Earlier shadow folders: those this process has loaded cannot be deleted and stay until next time.
        private static void RemoveOldShadowFolders(string root)
        {
            if (!Directory.Exists(root))
                return;
            foreach (string folder in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddHours(-1))
                        Directory.Delete(folder, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.WriteLine("revit-bridge add-in test: old shadow folder kept: " + folder + ": " + ex.Message);
                }
            }
        }

        private static string Screenshot(AddinTestResult reply, string prefix)
        {
            try
            {
                IntPtr window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                int[] rect = Desktop.WindowRectangle(window);
                if (rect == null)
                {
                    reply.AddError("screenshot: no Revit window");
                    return string.Empty;
                }
                string path = Desktop.DefaultCapturePath(prefix);
                Desktop.CapturePng(rect[0], rect[1], rect[2], rect[3], path);
                return path;
            }
            catch (Exception ex)
            {
                reply.AddError("screenshot: " + ex.Message);
                return string.Empty;
            }
        }

        private static IEnumerable<string> CommandClasses(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            return types.Where(t => !t.IsAbstract && typeof(IExternalCommand).IsAssignableFrom(t)).Select(t => t.FullName);
        }

        private static JToken Requested(PickRequest pick)
        {
            if (pick.IsEscape)
                return "Escape";
            if (pick.ElementId.HasValue)
                return pick.ElementId.Value;
            return new JArray(pick.PointMm);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Message(Exception ex)
        {
            Exception inner = ex is InvalidOperationException && ex.InnerException != null ? ex.InnerException : ex;
            return inner.Message;
        }
    }
}
