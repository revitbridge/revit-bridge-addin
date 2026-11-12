using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>dry_run_code_in_revit 的外部事件处理器。顺序：编译（与 send_code_to_revit 共用
    /// CodeExecutor.Compile，失败则不开事务）；开事务组；组内开一个带失败预处理器的事务并运行代码；
    /// 代码正常结束则提交该事务，由 DocumentChanged 取得增、改、删；代码抛异常则回滚该事务；
    /// 最后无论哪条路径都在 finally 里回滚事务组。</para>
    /// <para>External event handler of dry_run_code_in_revit. Order: compile (CodeExecutor.Compile,
    /// shared with send_code_to_revit; no transaction when it fails); start a transaction group;
    /// inside it, one transaction with a failures preprocessor runs the code; when the code returns
    /// that transaction is committed and DocumentChanged reports what was added, modified and
    /// deleted; when the code throws it is rolled back; on every path the group is rolled back in a
    /// finally block.</para>
    /// </summary>
    public class DryRunCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const string GroupName = "revit-bridge dry run";
        private const string TransactionName = "revit-bridge dry run";

        private readonly object _sync = new object();
        private readonly ManualResetEvent _done = new ManualResetEvent(false);

        private string _code;
        private object[] _parameters;
        private bool _queued;
        private bool _running;
        private DryRunResultInfo _result;

        /// <summary>
        /// <para>登记一次试跑；已有一次在排队或运行时返回 false。</para>
        /// <para>Register a dry run; false while another one is queued or running.</para>
        /// </summary>
        public bool TryQueue(string code, object[] parameters)
        {
            lock (_sync)
            {
                if (_queued || _running)
                    return false;

                _code = code ?? string.Empty;
                _parameters = parameters ?? Array.Empty<object>();
                _result = null;
                _queued = true;
                _done.Reset();
                return true;
            }
        }

        /// <summary>
        /// <para>撤回一个 Revit 还没开始运行的请求；已开始运行时返回 false（它会自己跑完并回滚）。</para>
        /// <para>Withdraw a request Revit has not started; false when it is already running (it then
        /// finishes and rolls back on its own).</para>
        /// </summary>
        public bool WithdrawIfNotStarted()
        {
            lock (_sync)
            {
                if (_queued && !_running)
                {
                    _queued = false;
                    return true;
                }
                return false;
            }
        }

        /// <summary>The result of the last finished dry run.</summary>
        public DryRunResultInfo TakeResult()
        {
            lock (_sync)
            {
                return _result;
            }
        }

        // 不在这里 Reset：TryQueue 已经 Reset，处理器先于等待结束时信号也不会丢。
        // No Reset here: TryQueue already reset it, so a handler that finishes before the wait
        // starts does not lose its signal.
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _done.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            string code;
            object[] parameters;
            lock (_sync)
            {
                // 超时后已撤回，或没有排队的请求：什么也不做。
                // Withdrawn after a timeout, or nothing queued: do nothing.
                if (!_queued)
                    return;
                _queued = false;
                _running = true;
                code = _code;
                parameters = _parameters;
            }

            DryRunResultInfo result = null;
            try
            {
                result = Run(app, code, parameters);
            }
            catch (Exception ex)
            {
                // Run 自己处理所有异常并回滚；到这里说明它的收尾本身出错，回滚是否完成不得而知。
                // Run handles every exception and rolls back itself; getting here means its own
                // clean-up failed, so whether the rollback completed is unknown.
                result = new DryRunResultInfo
                {
                    Success = false,
                    RolledBack = false,
                    Error = "dry run failed outside its own error handling: " + CodeExecutor.FormatRuntimeError(ex),
                };
            }
            finally
            {
                lock (_sync)
                {
                    _result = result;
                    _running = false;
                }
                _done.Set();
            }
        }

        public string GetName()
        {
            return "revit-bridge dry run";
        }

        /// <summary>
        /// <para>一次完整的试跑。只在 Revit API 上下文中调用。返回前事务组必已回滚（或从未开始）。</para>
        /// <para>One complete dry run. Call it in Revit API context only. Before it returns the
        /// transaction group has been rolled back (or was never started).</para>
        /// </summary>
        internal static DryRunResultInfo Run(UIApplication app, string code, object[] parameters)
        {
            var result = new DryRunResultInfo();
            RevitApplication application = null;
            ChangeCollector collector = null;
            FailureRecorder recorder = null;
            TransactionGroup group = null;
            bool groupStarted = false;

            try
            {
                // 1. 编译（与 send_code_to_revit、check_code_in_revit 同一步）；不通过就不开事务。
                // 1. Compile (the same step as send_code_to_revit and check_code_in_revit); no
                //    transaction is opened when it fails.
                var outcome = CodeExecutor.Compile(code);
                result.Fingerprint = outcome.Fingerprint ?? string.Empty;
                if (!outcome.Compiled)
                {
                    result.Diagnostics = outcome.Diagnostics ?? new List<CodeDiagnostic>();
                    return result;
                }

                Document document = app?.ActiveUIDocument?.Document;
                if (document == null)
                {
                    throw new InvalidOperationException("No active document: open a model in Revit before a dry run.");
                }

                application = app.Application;
                collector = new ChangeCollector(document);
                recorder = new FailureRecorder();

                // 2. 事务组：回滚它即撤销组内已提交的事务，并且不留撤销条目。
                // 2. Transaction group: rolling it back undoes the transactions committed inside it
                //    and leaves no undo entry.
                group = new TransactionGroup(document, GroupName);
                TransactionStatus groupStatus = group.Start();
                if (groupStatus != TransactionStatus.Started)
                {
                    throw new InvalidOperationException($"The dry-run transaction group did not start (status {groupStatus}).");
                }
                groupStarted = true;

                // DocumentChanged 只在本次内层事务提交时读取；回滚事务组之前退订。
                // DocumentChanged is read only for this run's inner commit; unsubscribed before the
                // group is rolled back.
                application.DocumentChanged += collector.OnDocumentChanged;

                object returned;
                bool committed;
                using (var transaction = new Transaction(document, TransactionName))
                {
                    TransactionStatus startStatus = transaction.Start();
                    if (startStatus != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException($"The dry-run transaction did not start (status {startStatus}).");
                    }

                    // 3. 失败处理：警告记录后删除；错误记录后静默回滚，不弹对话框。
                    //    回滚后清除失败也用于代码抛异常时的回滚。
                    // 3. Failure handling: warnings are recorded and deleted; errors are recorded and
                    //    the transaction rolls back silently, without a dialog. Clear-after-rollback
                    //    also covers the rollback after the code threw.
                    FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(recorder);
                    options.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(options);

                    try
                    {
                        returned = CodeExecutor.Execute(outcome, document, parameters);
                    }
                    catch (Exception ex)
                    {
                        // 4. 代码抛异常：回滚内层事务；事务组在 finally 里回滚。
                        // 4. The code threw: roll the inner transaction back; the group is rolled
                        //    back in the finally block.
                        result.Error = CodeExecutor.FormatRuntimeError(ex);
                        try
                        {
                            if (transaction.GetStatus() == TransactionStatus.Started)
                            {
                                transaction.RollBack();
                            }
                        }
                        catch (Exception rollbackError)
                        {
                            AppendError(result, "Rolling back the dry-run transaction failed: " + CodeExecutor.FormatRuntimeError(rollbackError));
                        }
                        return result;
                    }

                    // 提交是为了让 Revit 报告增改删并跑完失败处理；之后整个组仍会回滚。
                    // Committed so that Revit reports what changed and finishes failure handling;
                    // the whole group is still rolled back afterwards.
                    TransactionStatus commitStatus = transaction.Commit();
                    committed = commitStatus == TransactionStatus.Committed;
                    if (!committed && !recorder.HadError)
                    {
                        result.Error = $"The dry-run transaction ended as {commitStatus} without a recorded error.";
                    }
                }

                application.DocumentChanged -= collector.OnDocumentChanged;

                // 回滚之前读类别：新增的元素此刻还在。
                // Read categories before the rollback, while the added elements still exist.
                collector.Fill(result.Changes);
                if (collector.Problem != null && string.IsNullOrEmpty(result.Error))
                {
                    result.Error = collector.Problem;
                }

                // 与 send_code_to_revit 相同：提交之后序列化返回值。
                // As in send_code_to_revit: the return value is serialized after the commit.
                try
                {
                    result.Result = JsonConvert.SerializeObject(returned);
                }
                catch (Exception ex)
                {
                    // Revit 已回滚内层事务时，返回值里的元素可能已失效；说明这一点，以免误认为是代码的错。
                    // After Revit rolled the inner transaction back, elements in the return value may
                    // be gone; say so, so that it is not taken for a fault of the code.
                    AppendError(result, committed
                        ? CodeExecutor.FormatRuntimeError(ex)
                        : "The return value could not be serialized after Revit rolled the transaction back (see failures): " + CodeExecutor.FormatRuntimeError(ex));
                }

                result.Success = committed && !recorder.HadError && string.IsNullOrEmpty(result.Error);
            }
            catch (Exception ex)
            {
                result.Success = false;
                AppendError(result, CodeExecutor.FormatRuntimeError(ex));
            }
            finally
            {
                if (application != null && collector != null)
                {
                    // 未订阅时退订也无害。
                    // Unsubscribing when not subscribed is harmless.
                    application.DocumentChanged -= collector.OnDocumentChanged;
                }

                if (recorder != null)
                {
                    result.Failures = recorder.Failures;
                }

                // 5. 任何路径：事务组在返回前回滚。
                // 5. Every path: the group is rolled back before returning.
                if (groupStarted)
                {
                    try
                    {
                        TransactionStatus rollbackStatus = group.RollBack();
                        result.RolledBack = rollbackStatus == TransactionStatus.RolledBack;
                        if (!result.RolledBack)
                        {
                            AppendError(result, $"The dry-run transaction group was not rolled back (status {rollbackStatus}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.RolledBack = false;
                        AppendError(result, "Rolling back the dry-run transaction group failed: " + CodeExecutor.FormatRuntimeError(ex));
                    }
                }
                else
                {
                    // 没有开事务组（编译失败、没有文档、组未能开始）：没有可留下的改动。
                    // No group was started (compile failure, no document, the group did not start):
                    // there is no change that could stay.
                    result.RolledBack = true;
                }

                try
                {
                    group?.Dispose();
                }
                catch (Exception ex)
                {
                    AppendError(result, "Disposing the dry-run transaction group failed: " + CodeExecutor.FormatRuntimeError(ex));
                }

                if (!result.RolledBack)
                {
                    result.Success = false;
                }
            }

            if (string.IsNullOrEmpty(result.Fingerprint))
            {
                try
                {
                    result.Fingerprint = CodeExecutor.ComputeFingerprint(code);
                }
                catch (Exception)
                {
                    // 留空：error 已说明编译这一步为什么没到。
                    // Leave it empty; error already says why compiling was not reached.
                    result.Fingerprint = string.Empty;
                }
            }

            return result;
        }

        private static void AppendError(DryRunResultInfo result, string text)
        {
            result.Error = string.IsNullOrEmpty(result.Error) ? text : result.Error + " | " + text;
        }

        /// <summary>
        /// <para>记录本事务的每条失败；没有错误时删除警告继续提交，有错误时静默回滚。</para>
        /// <para>Records every failure of the transaction; with no error it deletes the warnings and
        /// lets the commit continue, with an error it rolls back silently.</para>
        /// </summary>
        private sealed class FailureRecorder : IFailuresPreprocessor
        {
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

            public List<DryRunFailure> Failures { get; } = new List<DryRunFailure>();

            public bool HadError { get; private set; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                try
                {
                    IList<FailureMessageAccessor> messages = failuresAccessor.GetFailureMessages();
                    bool error = false;
                    foreach (FailureMessageAccessor message in messages)
                    {
                        FailureSeverity severity = message.GetSeverity();
                        if (severity == FailureSeverity.None)
                            continue;
                        if (severity != FailureSeverity.Warning)
                            error = true;
                        Record(message, severity);
                    }

                    if (!error)
                    {
                        // 只有警告：全部删除，提交照常进行，不显示给用户。
                        // Warnings only: delete them all; the commit goes on and nothing is shown.
                        foreach (FailureMessageAccessor message in messages)
                        {
                            if (message.GetSeverity() == FailureSeverity.Warning)
                                failuresAccessor.DeleteWarning(message);
                        }
                        return FailureProcessingResult.Continue;
                    }

                    HadError = true;
                    return RollBackSilently(failuresAccessor);
                }
                catch (Exception ex)
                {
                    // 读失败本身出错：按错误处理，静默回滚，并把原因记成一条错误。
                    // Reading the failures failed: treat it as an error, roll back silently and
                    // record the reason as one error.
                    HadError = true;
                    Failures.Add(new DryRunFailure
                    {
                        Severity = "Error",
                        Text = "dry run: reading Revit's failures failed: " + CodeExecutor.FormatRuntimeError(ex),
                    });
                    return RollBackSilently(failuresAccessor);
                }
            }

            private FailureProcessingResult RollBackSilently(FailuresAccessor failuresAccessor)
            {
                // ProceedWithRollBack 要配合"回滚后清除失败"才不会把失败交给用户（RevitAPI 对 ProceedWithRollBack 的说明）。
                // ProceedWithRollBack must come with "clear after rollback", or the failures may still
                // be delivered to the user (RevitAPI remarks on ProceedWithRollBack).
                try
                {
                    FailureHandlingOptions options = failuresAccessor.GetFailureHandlingOptions();
                    options.SetClearAfterRollback(true);
                    failuresAccessor.SetFailureHandlingOptions(options);
                }
                catch (Exception ex)
                {
                    // 事务上已预先设了同一选项；这里失败只记一条，不改变回滚。
                    // The transaction already carries the same option; a failure here is recorded
                    // and does not change the rollback.
                    Failures.Add(new DryRunFailure
                    {
                        Severity = "Error",
                        Text = "dry run: setting clear-after-rollback failed: " + CodeExecutor.FormatRuntimeError(ex),
                    });
                }
                return FailureProcessingResult.ProceedWithRollBack;
            }

            private void Record(FailureMessageAccessor message, FailureSeverity severity)
            {
                var ids = new List<long>();
                var seenIds = new HashSet<long>();
                AddIds(ids, seenIds, message.GetFailingElementIds());
                AddIds(ids, seenIds, message.GetAdditionalElementIds());

                var failure = new DryRunFailure
                {
                    Severity = severity == FailureSeverity.Warning ? "Warning" : "Error",
                    Text = message.GetDescriptionText() ?? string.Empty,
                    ElementIds = ids,
                };

                // 预处理器可能被调用不止一次；同一条只记一次。
                // The preprocessor may be called more than once; record each failure once.
                string key = failure.Severity + "\n" + failure.Text + "\n" + string.Join(",", ids);
                if (_seen.Add(key))
                    Failures.Add(failure);
            }

            private static void AddIds(List<long> ids, HashSet<long> seen, ICollection<ElementId> source)
            {
                if (source == null)
                    return;
                foreach (ElementId id in source)
                {
                    if (ids.Count >= DryRunFailure.MaxElementIds)
                        return;
                    if (id is null || id == ElementId.InvalidElementId)
                        continue;
                    long value = id.GetValue();
                    if (seen.Add(value))
                        ids.Add(value);
                }
            }
        }

        /// <summary>
        /// <para>收集本文档本次提交的增、改、删（只认 TransactionCommitted；事务组回滚另有事件，不计入）。</para>
        /// <para>Collects what this document's commit added, modified and deleted (TransactionCommitted
        /// only; the group rollback raises its own event, which is not counted).</para>
        /// </summary>
        private sealed class ChangeCollector
        {
            private readonly Document _document;
            private readonly HashSet<ElementId> _added = new HashSet<ElementId>();
            private readonly HashSet<ElementId> _modified = new HashSet<ElementId>();
            private readonly HashSet<ElementId> _deleted = new HashSet<ElementId>();

            public ChangeCollector(Document document)
            {
                _document = document;
            }

            /// <summary>Set when an event could not be read; the counts are then incomplete.</summary>
            public string Problem { get; private set; }

            public void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
            {
                // 事件处理器不能把异常抛回 Revit：记下来，由 Run 报告。
                // An event handler must not throw into Revit: record it for Run to report.
                try
                {
                    if (e.Operation != UndoOperation.TransactionCommitted)
                        return;
                    Document changed = e.GetDocument();
                    if (changed is null || !changed.Equals(_document))
                        return;

                    _added.UnionWith(e.GetAddedElementIds());
                    _modified.UnionWith(e.GetModifiedElementIds());
                    _deleted.UnionWith(e.GetDeletedElementIds());
                }
                catch (Exception ex)
                {
                    Problem = "dry run: reading the document changes failed, the counts are incomplete: " + CodeExecutor.FormatRuntimeError(ex);
                }
            }

            /// <summary>
            /// <para>写入计数、类别与前 50 个 id。必须在事务组回滚之前调用（新增的元素此时还在）。</para>
            /// <para>Fill counts, categories and the first 50 ids. Call it before the group is rolled
            /// back, while the added elements still exist.</para>
            /// </summary>
            public void Fill(DryRunChanges changes)
            {
                FillSet(changes.Added, _added);
                FillSet(changes.Modified, _modified);

                changes.Deleted.Count = _deleted.Count;
                changes.Deleted.Ids = FirstIds(_deleted);
            }

            private void FillSet(DryRunChangeSet set, HashSet<ElementId> ids)
            {
                set.Count = ids.Count;
                set.Ids = FirstIds(ids);
                foreach (ElementId id in ids)
                {
                    string key = CategoryKey(id);
                    set.ByCategory.TryGetValue(key, out int count);
                    set.ByCategory[key] = count + 1;
                }
            }

            private static List<long> FirstIds(HashSet<ElementId> ids)
            {
                return ids
                    .Select(id => id.GetValue())
                    .OrderBy(value => value)
                    .Take(DryRunChanges.MaxIds)
                    .ToList();
            }

            private string CategoryKey(ElementId id)
            {
                // 逐项容错：读不出的元素记在 "(none)"，不让一个元素拖垮整次统计。
                // Per item: an element that cannot be read counts under "(none)" instead of
                // failing the whole summary.
                try
                {
                    Element element = _document.GetElement(id);
                    if (element is null)
                        return "(none)";
                    if (element is ElementType)
                        return "(types)";

                    Category category = element.Category;
                    if (category is null)
                        return "(none)";

                    long value = category.Id.GetValue();
                    if (value < int.MinValue || value > int.MaxValue)
                        return "(other)";
                    var builtIn = (BuiltInCategory)(int)value;
                    return Enum.IsDefined(typeof(BuiltInCategory), builtIn) ? builtIn.ToString() : "(other)";
                }
                catch (Exception ex)
                {
                    if (Problem == null)
                        Problem = $"dry run: reading the category of element {id.GetValue()} failed: " + CodeExecutor.FormatRuntimeError(ex);
                    return "(none)";
                }
            }
        }
    }
}
