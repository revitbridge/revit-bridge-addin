using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Commands.ExecuteDynamicCode;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>进行中的一次插件测试：test_addin_command_in_revit（套接字线程）与加载器外部命令（Revit 线程）共用。
    /// 同一时间只有一个。加载器在插件程序集里，经 AppDomain 数据槽 <see cref="LoaderSlot"/> 调到这里的
    /// <see cref="RunFromLoader"/>（两边不互相引用）。</para>
    /// <para>The add-in test in progress, shared by test_addin_command_in_revit (socket thread) and the
    /// loader external command (Revit thread). One at a time. The loader lives in the plugin assembly and
    /// reaches <see cref="RunFromLoader"/> through the AppDomain data slot <see cref="LoaderSlot"/> (the
    /// two assemblies do not reference each other).</para>
    /// </summary>
    internal sealed class AddinTestSession
    {
        /// <summary>Must equal revit_mcp_plugin.Core.AddinTestLoader.SlotName in the plugin.</summary>
        public const string LoaderSlot = "revit-bridge.addin-test.loader";

        public const string ChangesNote =
            "Restored: the ids in added no longer exist; modified and deleted elements are back as they were before the test (see restored_clean).";

        private static readonly object Gate = new object();
        private static AddinTestSession _current;

        private readonly Dictionary<string, Assembly> _shadowAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private bool _armed;
        private bool _loaderStarted;
        private bool _cancelled;
        private bool _callerDone;

        // Set by the caller before the loader is posted.
        public IExternalCommand Command { get; set; }
        public string Restore { get; set; } = AddinTestRequest.RestoreGroupRollback;
        public CompileOutcome Readback { get; set; }
        public string ShadowDirectory { get; set; }

        // Signals for the caller.
        public ManualResetEvent Executing { get; } = new ManualResetEvent(false);
        public ManualResetEvent CommandReturned { get; } = new ManualResetEvent(false);
        public ManualResetEvent Finished { get; } = new ManualResetEvent(false);

        // Written by the loader before Finished is set.
        public string Returned { get; private set; } = "(none)";
        public string CommandMessage { get; private set; } = string.Empty;
        public string ReadbackJson { get; private set; } = string.Empty;
        public string ReadbackError { get; private set; } = string.Empty;
        public string LoaderError { get; private set; } = string.Empty;
        public string RestoreError { get; private set; } = string.Empty;
        public DryRunChanges Changes { get; private set; }
        public ChangeCollector Collector { get; private set; }
        public DryRunCodeEventHandler.FailureRecorder Recorder { get; private set; }
        public bool RolledBack { get; private set; }
        public bool RestoredClean { get; private set; }
        public Document Document { get; private set; }
        public RevitApplication Application { get; private set; }

        public static bool TryBegin(out AddinTestSession session)
        {
            lock (Gate)
            {
                if (_current != null)
                {
                    session = null;
                    return false;
                }
                session = _current = new AddinTestSession();
                return true;
            }
        }

        public static bool IsBusy
        {
            get { lock (Gate) return _current != null; }
        }

        /// <summary>
        /// <para>（API 上下文）Command 与 Restore 设好之后、第一次投递加载器之前调用。此前的加载器调用（例如上一次测试
        /// 迟到的重复投递）不认领本次测试。</para>
        /// <para>(API context) Called after Command and Restore are set, right before the loader is posted
        /// for the first time. A loader invocation before this (for example a late duplicate post from the
        /// previous test) does not take this test.</para>
        /// </summary>
        public void Arm()
        {
            lock (Gate)
                _armed = true;
        }

        /// <summary>True while the test is armed and the loader has neither started nor been withdrawn.</summary>
        public bool AwaitingLoader
        {
            get { lock (Gate) return _armed && !_loaderStarted && !_cancelled; }
        }

        /// <summary>True when the loader had not started and now never will.</summary>
        public bool CancelIfNotStarted()
        {
            lock (Gate)
            {
                if (_loaderStarted)
                    return false;
                _cancelled = true;
                return true;
            }
        }

        /// <summary>The caller is done; the session ends now, or when the loader ends if it is still running.</summary>
        public void ReleaseByCaller()
        {
            lock (Gate)
            {
                _callerDone = true;
                if (!_loaderStarted)
                    _cancelled = true;
                if (!_loaderStarted || Finished.WaitOne(0))
                    Clear();
            }
        }

        private void Clear()
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        }

        /// <summary>
        /// <para>加载器调用的入口：没有待运行的测试时什么也不做。总是返回 Succeeded 或 Cancelled（不让 Revit 弹失败框）。
        /// 加载器可能被投递多次，多出来的调用（运行中、运行后、下一次测试就绪之前）都返回 Cancelled，不动会话。</para>
        /// <para>The loader's entry: does nothing when no test is waiting. Always returns Succeeded or
        /// Cancelled (Revit shows no failure dialog). The loader may be posted more than once; an extra
        /// invocation (while or after the loader ran, or before the next test is armed) returns Cancelled
        /// and leaves the session alone.</para>
        /// </summary>
        internal static Result RunFromLoader(ExternalCommandData commandData, ElementSet elements)
        {
            AddinTestSession session;
            lock (Gate)
            {
                session = _current;
                if (session == null || session._cancelled || session._loaderStarted)
                    return Result.Cancelled;
                // 还没就绪的测试（命令未设好、未投递）不认领，也不改它的状态。
                // A test that is not armed yet (command not set, nothing posted) is not taken and not changed.
                if (!session._armed)
                    return Result.Cancelled;
                session._loaderStarted = true;
            }
            try
            {
                session.Run(commandData, elements);
            }
            catch (Exception ex)
            {
                session.LoaderError = "loader: " + CodeExecutor.FormatRuntimeError(ex);
            }
            finally
            {
                session.Executing.Set();
                session.CommandReturned.Set();
                session.Finished.Set();
                lock (Gate)
                {
                    if (session._callerDone)
                        session.Clear();
                }
            }
            return Result.Succeeded;
        }

        private void Run(ExternalCommandData commandData, ElementSet elements)
        {
            Document = commandData.Application.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active document.");
            Application = commandData.Application.Application;
            Collector = new ChangeCollector(Document);
            Recorder = new DryRunCodeEventHandler.FailureRecorder();

            TransactionGroup group = null;
            Application.DocumentChanged += Collector.OnDocumentChanged;
            Application.FailuresProcessing += OnFailuresProcessing;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromShadow;
            try
            {
                if (Restore == AddinTestRequest.RestoreGroupRollback)
                {
                    group = new TransactionGroup(Document, "revit-bridge add-in test");
                    TransactionStatus status = group.Start();
                    if (status != TransactionStatus.Started)
                        throw new InvalidOperationException("The test's transaction group did not start (status " + status + ").");
                }

                // 被测命令即将进入 Execute：调用方从这里开始点选。
                // The command is about to run: the caller starts its picks from here.
                Executing.Set();
                string message = string.Empty;
                try
                {
                    Result result = Command.Execute(commandData, ref message, elements);
                    Returned = result.ToString();
                    CommandMessage = message ?? string.Empty;
                }
                catch (Exception ex)
                {
                    Returned = "Failed";
                    CommandMessage = "unhandled exception: " + CodeExecutor.FormatRuntimeError(ex);
                }
                finally
                {
                    CommandReturned.Set();
                }

                // 还原之前：读改动的类别（新增的元素此刻还在），再跑读回片段（不开事务，只能读）。
                // Before the restore: categories of the changes (added elements still exist), then the
                // read-back snippet (no transaction is open, so it can only read).
                Changes = new DryRunChanges { Note = ChangesNote };
                Collector.Fill(Changes);
                if (Readback != null)
                {
                    try
                    {
                        ReadbackJson = JsonConvert.SerializeObject(CodeExecutor.Execute(Readback, Document, Array.Empty<object>()));
                    }
                    catch (Exception ex)
                    {
                        ReadbackError = CodeExecutor.FormatRuntimeError(ex);
                    }
                }
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromShadow;
                Application.FailuresProcessing -= OnFailuresProcessing;
                if (group != null)
                {
                    try
                    {
                        if (group.GetStatus() == TransactionStatus.Started)
                        {
                            TransactionStatus status = group.RollBack();
                            RolledBack = status == TransactionStatus.RolledBack;
                            if (!RolledBack)
                                RestoreError = "the test's transaction group ended as " + status;
                        }
                        else
                        {
                            RestoreError = "the test's transaction group was no longer open (status " + group.GetStatus() + ")";
                        }
                    }
                    catch (Exception ex)
                    {
                        RolledBack = false;
                        RestoreError = "rolling back the test's transaction group failed: " + CodeExecutor.FormatRuntimeError(ex);
                    }
                    try
                    {
                        group.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RestoreError += (RestoreError.Length > 0 ? " | " : string.Empty) + "disposing the group failed: " + CodeExecutor.FormatRuntimeError(ex);
                    }
                    RestoredClean = RolledBack && ExistenceRestored();
                    Application.DocumentChanged -= Collector.OnDocumentChanged;
                }
                // Undo: the collector stays subscribed until the caller's undo steps end (FinishUndo).
                if (Changes == null)
                {
                    Changes = new DryRunChanges { Note = ChangesNote };
                    try
                    {
                        Collector.Fill(Changes);
                    }
                    catch (Exception ex)
                    {
                        LoaderError += (LoaderError.Length > 0 ? " | " : string.Empty) + "reading the changes failed: " + CodeExecutor.FormatRuntimeError(ex);
                    }
                }
            }
        }

        /// <summary>
        /// <para>撤销还原的一步（API 上下文）：还有未撤回的改动、且撤销次数少于测试期间的提交次数时，投递一次撤销。
        /// 不会撤销测试之前的操作。返回是否投递了。</para>
        /// <para>One step of the undo restore (API context): post one Undo while changes are outstanding
        /// and fewer undos were posted than the test committed, so nothing from before the test is
        /// undone. Returns whether one was posted.</para>
        /// </summary>
        public bool PostUndoIfNeeded(UIApplication app, int undoneSoFar)
        {
            if (Collector == null || Collector.CountOutstanding() == 0 || undoneSoFar >= Collector.CommitEvents)
                return false;
            RevitCommandId undo = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (undo == null || !app.CanPostCommand(undo))
            {
                RestoreError = "Revit cannot post Undo now";
                return false;
            }
            app.PostCommand(undo);
            return true;
        }

        /// <summary>Last undo step (API context): stop listening and check the result.</summary>
        public void FinishUndo()
        {
            if (Collector == null)
                return;
            if (Application != null)
                Application.DocumentChanged -= Collector.OnDocumentChanged;
            RestoredClean = Collector.CountOutstanding() == 0 && ExistenceRestored();
        }

        // 新增的都不在了，删除的都回来了（"修改"无法按存在与否判断，见报告）。
        // Every added element is gone and every deleted one is back ("modified" cannot be checked by existence).
        private bool ExistenceRestored()
        {
            try
            {
                return Collector.AddedIds.All(id => Document.GetElement(id) == null)
                    && Collector.DeletedIds.All(id => Document.GetElement(id) != null);
            }
            catch (Exception ex)
            {
                RestoreError += (RestoreError.Length > 0 ? " | " : string.Empty) + "checking the restore failed: " + CodeExecutor.FormatRuntimeError(ex);
                return false;
            }
        }

        // 测试期间的应用级失败处理：同 dry run 的预处理器（警告记录后删除；错误记录后静默回滚）。
        // Application-level failure handling during the test: the dry run's recorder (warnings recorded
        // and deleted; errors recorded and rolled back silently).
        private void OnFailuresProcessing(object sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
        {
            try
            {
                e.SetProcessingResult(Recorder.PreprocessFailures(e.GetFailuresAccessor()));
            }
            catch (Exception ex)
            {
                LoaderError += (LoaderError.Length > 0 ? " | " : string.Empty) + "failure handling: " + CodeExecutor.FormatRuntimeError(ex);
            }
        }

        /// <summary>
        /// <para>被测 DLL 的依赖：从影子目录按文件载入（LoadFile，与被测 DLL 相同），只回答影子目录里有的程序集。</para>
        /// <para>Dependencies of the DLL under test: loaded from the shadow folder (LoadFile, like the DLL
        /// itself), answering only for assemblies that folder holds.</para>
        /// </summary>
        internal Assembly ResolveFromShadow(object sender, ResolveEventArgs args)
        {
            try
            {
                string folder = ShadowDirectory;
                if (string.IsNullOrEmpty(folder))
                    return null;
                string name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name) || name.Equals("RevitAPI", StringComparison.OrdinalIgnoreCase) || name.Equals("RevitAPIUI", StringComparison.OrdinalIgnoreCase))
                    return null;
                string path = Path.Combine(folder, name + ".dll");
                if (!File.Exists(path))
                    return null;
                lock (_shadowAssemblies)
                {
                    if (!_shadowAssemblies.TryGetValue(path, out Assembly assembly))
                    {
                        assembly = Assembly.LoadFile(path);
                        _shadowAssemblies[path] = assembly;
                    }
                    return assembly;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("revit-bridge add-in test could not resolve " + args.Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
