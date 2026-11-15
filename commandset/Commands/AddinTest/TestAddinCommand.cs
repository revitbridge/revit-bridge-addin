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
    /// 在 API 上下文里把全部点选换算成屏幕坐标、载入命令、投递加载器（Revit 丢掉投递时重投，最多投递四次）；
    /// 等加载器报告已调用 Execute 后在本线程点选
    /// （丢了的最后一次点击最多重点到三次）；超时通过后台窗口命令取消对话框；加载器里读回、统计、还原；读结果文件。</para>
    /// <para>Tests a built add-in command in the open Revit without installing it and without keeping
    /// anything. Order (work plan section 4.3, required): check and compile the read-back; read the DLL
    /// as bytes, hash them and write them into a fresh shadow folder (the original file is never held
    /// open); write the input file; zoom (the view is not touched after this); in API context map every
    /// pick to a screen pixel, load the command and post the loader (posted again when Revit drops it,
    /// four posts at most); once the loader reports that it called Execute, click the picks from this
    /// thread (a lost last click is repeated, three attempts at
    /// most); on a timeout cancel the identified dialog with a native window command; read back, count and restore in the loader;
    /// read the result file.</para>
    /// </summary>
    public class TestAddinCommand : ApiCallCommandBase
    {
        /// <summary>The loader's AddInId in revit-bridge.addin, in lower case (LookupCommandId needs lower case).</summary>
        public const string LoaderAddInId = "25a0ccca-d8aa-4ea4-9a87-1e4889a4b605";

        private const int StepTimeoutMs = 30000;
        private const int LoaderStartMs = 20000;
        private const int LoaderSliceMs = 4000;
        private const int MaxLoaderPosts = 4;
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

            // 4. 等加载器调用被测命令。Revit 在某些短暂的输入状态下会悄悄丢掉投递的命令：每等一段还没开始，
            //    就回到 API 上下文重新投递（总时限不变，最多投递四次）。到时仍没开始就撤回：什么也没运行。
            // 4. Wait for the loader to call the command. Revit silently drops a posted command in some
            //    transient input states: after each slice without a start, post it again from API context
            //    (same total budget, four posts at most). If it never starts, withdraw: nothing ran.
            int posts = 1;
            string lastRepost = null;
            var startClock = System.Diagnostics.Stopwatch.StartNew();
            while (!session.Executing.WaitOne(Math.Max(0, Math.Min(LoaderSliceMs, LoaderStartMs - (int)startClock.ElapsedMilliseconds))))
            {
                int left = LoaderStartMs - (int)startClock.ElapsedMilliseconds;
                if (left <= 0)
                    break;
                if (posts >= MaxLoaderPosts)
                    continue;

                // 重投这一步不超过剩余时限，也不超过一段（Revit 不空闲时外部事件会迟迟不执行；加载器此时开始的话，
                // 这一步要等它结束才轮到）。超时或出错只记下，接着等。
                // The re-post step waits no longer than the budget left, nor than one slice (the external
                // event stalls while Revit is not idle, and waits for the loader to end if the loader starts
                // now). A timeout or error is recorded and the wait goes on.
                try
                {
                    switch ((RepostOutcome)CallApi(app => Repost(app, session), Math.Min(left, LoaderSliceMs)))
                    {
                        case RepostOutcome.Posted:
                            posts++;
                            lastRepost = null;
                            break;
                        case RepostOutcome.NotRegistered:
                            lastRepost = "the last re-post was refused: the loader is no longer registered";
                            break;
                        case RepostOutcome.CannotPost:
                            lastRepost = "the last re-post was refused: CanPostCommand returned false";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    lastRepost = "the last re-post failed: " + Message(ex);
                }
            }
            if (!session.Executing.WaitOne(0) && session.CancelIfNotStarted())
            {
                string posted = posts == 1
                    ? "posted once; " + (lastRepost ?? "Revit accepted the post but never ran it")
                    : $"posted {posts} times; " + (lastRepost ?? "Revit accepted each post but never ran it");
                reply.LoadError = $"The test loader did not start within {LoaderStartMs / 1000} s ({posted}); nothing ran.";
                return;
            }
            session.Executing.WaitOne();
            reply.RestoredClean = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(request.TimeoutSeconds);

            // 5. 点选：先等命令真正进入等待，再置前、按序点击。
            // 5. Picks: give the command time to reach its prompt, bring Revit to the front, click in order.
            session.CommandReturned.WaitOne(ClickDelayMs);
            reply.ForegroundOk = request.Picks.Count == 0;
            DateTime lastClick = DateTime.UtcNow;
            for (int i = 0; i < request.Picks.Count && !session.CommandReturned.WaitOne(0); i++)
            {
                while (!session.CommandReturned.WaitOne(0) && DateTime.UtcNow < deadline &&
                    !prepared.Dialogs.FocusForPick())
                    session.CommandReturned.WaitOne(250);
                if (session.CommandReturned.WaitOne(0) || DateTime.UtcNow >= deadline)
                    break;
                if (!prepared.Dialogs.CanClickPick()) { i--; continue; }
                reply.ForegroundOk = true;
                PickRequest pick = request.Picks[i];
                var done = new PickDone { Requested = Requested(pick), Screen = prepared.Points[i], Attempts = 1 };
                if (pick.IsEscape && prepared.Dialogs.CanClickPick())
                    Desktop.Key(Desktop.VkEscape);
                else
                    if (pick.IsEscape || !Desktop.Click(done.Screen[0], done.Screen[1], prepared.Dialogs.CanClickPick))
                    { i--; continue; }
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
                    prepared.Dialogs.CanSendPick() &&
                    (DateTime.UtcNow - lastClick).TotalMilliseconds >= RetryAfterMs)
                {
                    reply.ForegroundOk = prepared.Dialogs.FocusForPick();
                    if (reply.ForegroundOk && Desktop.Click(last.Screen[0], last.Screen[1], prepared.Dialogs.CanClickPick))
                        last.Attempts++;
                    lastClick = DateTime.UtcNow;
                }
            }

            // 7. 超时：只关闭已确定归属的新对话框；不能确定时不发送任何输入。
            // 7. Timeout: close only the identified new dialog. Never infer a pick
            // prompt from absence of a matched dialog, or fall back to input.
            if (!session.CommandReturned.WaitOne(0))
            {
                reply.TimedOut = true;
                for (int round = 0; round < 2 && !session.CommandReturned.WaitOne(0); round++)
                {
                    if (!prepared.Dialogs.CloseOwnedModal(out string cancelError))
                    {
                        // A slow Close handler can finish after the native send's
                        // deadline. Still wait independently and restore if it ends.
                        if (!session.CommandReturned.WaitOne(5000))
                            reply.AddError(cancelError);
                        break;
                    }
                    session.CommandReturned.WaitOne(5000);
                }
                if (!session.CommandReturned.WaitOne(0))
                {
                    reply.AddError("The command did not end after cancellation; the test is still running in Revit. " +
                        (request.Restore == AddinTestRequest.RestoreUndo
                            ? "Undo restoration requires recovery after the command ends."
                            : "The loader will attempt group rollback when the command ends; restoration is not confirmed."));
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
            public DialogAutomation Dialogs;
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
            prepared.Dialogs = new DialogAutomation(app.MainWindowHandle, session.CommandReturned);

            // 从这里起加载器的调用才认领本次测试。第一次投递就失败时撤回本次测试，免得还排着的旧投递在无人点选时运行它。
            // From here on a loader invocation takes this test. If the first post fails the test is
            // withdrawn, so an older post still queued cannot run it with nobody clicking the picks.
            session.Arm();
            try
            {
                app.PostCommand(loader);
            }
            catch
            {
                session.CancelIfNotStarted();
                throw;
            }
            prepared.Posted = true;
            prepared.MainWindow = app.MainWindowHandle;
            return prepared;
        }

        private enum RepostOutcome { Posted, NotWaiting, NotRegistered, CannotPost }

        // 重新投递加载器（API 上下文，每次一个外部事件）：只在测试已就绪、加载器既未开始也未撤回时投递。
        // PostCommand 的异常（例如上一次投递还在排队）交给调用方记下。
        // Post the loader again (API context, one external event each time), only while the test is armed
        // and the loader has neither started nor been withdrawn. An exception from PostCommand (for
        // example while an earlier post is still queued) goes to the caller, which records it.
        private static object Repost(UIApplication app, AddinTestSession session)
        {
            if (!session.AwaitingLoader)
                return RepostOutcome.NotWaiting;
            RevitCommandId loader = RevitCommandId.LookupCommandId(LoaderAddInId);
            if (loader == null)
                return RepostOutcome.NotRegistered;
            if (!app.CanPostCommand(loader))
                return RepostOutcome.CannotPost;
            app.PostCommand(loader);
            return RepostOutcome.Posted;
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
