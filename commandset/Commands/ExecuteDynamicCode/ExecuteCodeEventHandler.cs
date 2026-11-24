using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的外部事件处理器
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // 代码执行参数
        private string _generatedCode;
        private object[] _executionParameters;

        // 执行结果信息
        public ExecutionResultInfo ResultInfo { get; private set; }

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 设置要执行的代码和参数
        public void SetExecutionParameters(string code, object[] parameters = null)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // 等待执行完成 - IWaitableExternalEventHandler接口实现
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            // 先建结果对象，取文档失败时 catch 里也有处可写。
            // Create the result first so the catch below can still report a failure to get the document.
            ResultInfo = new ExecutionResultInfo();

            try
            {
                var doc = app.ActiveUIDocument.Document;

                // The per-run confirmation dialog lives in the plugin's transports
                // (ConfirmationPrompt), which own the confirmEachRun setting and answer
                // -32001 "declined on device" before this handler is reached. By the time
                // code arrives here it also passed the device handshake,
                // allowRemoteCodeExecution and the server-side review.

                // 先编译（与 check_code_in_revit 共用同一步骤）；编译不通过就不开事务，
                // 诊断按用户代码行号返回。
                // Compile first (the same step check_code_in_revit uses). When it fails no
                // transaction is opened and the diagnostics carry user-code line numbers.
                var outcome = CodeExecutor.Compile(_generatedCode);
                ResultInfo.Fingerprint = outcome.Fingerprint;

                if (!outcome.Compiled)
                {
                    ResultInfo.Success = false;
                    ResultInfo.Diagnostics = outcome.Diagnostics;
                    ResultInfo.ErrorMessage = $"执行失败: {outcome.FormatErrorMessage()}";
                    return;
                }

                using (var transaction = new Transaction(doc, "执行AI代码"))
                {
                    transaction.Start();

                    // 提交时 Revit 报的警告与错误由共用的 CommitFailureGuard 处理，不弹失败对话框：
                    // 警告记下后删除、提交照常；错误记下后静默回滚，不自动解决任何一条。
                    // Warnings and errors Revit posts at commit go to the shared CommitFailureGuard,
                    // without a failure dialog: warnings are recorded and deleted and the commit goes
                    // on; errors are recorded and the transaction rolls back silently, none resolved
                    // automatically.
                    CommitFailureGuard guard = CommitFailureGuard.Install(transaction, "send_code_to_revit");

                    // 反射调用执行方法
                    var result = CodeExecutor.Execute(outcome, doc, _executionParameters);

                    // 只有 Committed 是提交成功；RolledBack（或其他状态）是失败，错误文字列出记下的错误。
                    // Only Committed is a successful commit; RolledBack (or any other status) is a
                    // failure whose text lists the recorded errors.
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        ResultInfo.Success = false;
                        ResultInfo.ErrorMessage = $"执行失败: {CommitFailureReport.RejectionText(guard.Log, status.ToString())}";
                        ResultInfo.AddCommitFailures(guard.Log, rejected: true);
                        return;
                    }

                    // 没有失败时不加任何键，回复与以前逐字节相同。
                    // Nothing is added when there was no failure, so the reply is byte-identical to before.
                    ResultInfo.AddCommitFailures(guard.Log, rejected: false);

                    ResultInfo.Success = true;
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                }
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                // 运行失败：真正抛出的异常的类型全名与消息（如 Autodesk.Revit.Exceptions.ArgumentException: ...），
                // 反射调用的 TargetInvocationException 已解包。编译错误不走这里，已在上面经 FormatErrorMessage 返回。
                // A failed run: the full type name and message of the exception actually thrown (such as
                // Autodesk.Revit.Exceptions.ArgumentException: ...), the reflective call's
                // TargetInvocationException unwrapped. Compile errors do not get here; they return above
                // through FormatErrorMessage.
                ResultInfo.ErrorMessage = $"执行失败: {CodeExecutor.FormatRuntimeError(ex)}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "执行AI代码";
        }
    }

    // 执行结果数据结构
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// <para>编译失败时的诊断（形状同 check_code_in_revit），否则不输出。</para>
        /// <para>Compiler diagnostics when compiling failed (same shape as check_code_in_revit); omitted otherwise.</para>
        /// </summary>
        [JsonProperty("diagnostics", NullValueHandling = NullValueHandling.Ignore)]
        public List<CodeDiagnostic> Diagnostics { get; set; }

        /// <summary>
        /// <para>编译时的 fingerprint（同 check_code_in_revit）；未到编译这一步时不输出。</para>
        /// <para>Fingerprint of the compile (same as check_code_in_revit); omitted when compiling was not reached.</para>
        /// </summary>
        [JsonProperty("fingerprint", NullValueHandling = NullValueHandling.Ignore)]
        public string Fingerprint { get; set; }

        /// <summary>
        /// <para>提交时 Revit 报的警告（已删除，不再显示；最多 50 条、每条最多 20 个 id），按报的顺序。
        /// 只在有警告或提交被拒时输出；没有失败的回复不含此键。</para>
        /// <para>Warnings Revit posted at commit (deleted, not shown; at most 50, each with at most
        /// 20 ids), in the order posted. Present only when there were warnings or the commit was
        /// rejected; a reply without failures has no such key.</para>
        /// </summary>
        [JsonProperty("warnings", NullValueHandling = NullValueHandling.Ignore)]
        public List<CommitFailureEntry> Warnings { get; set; }

        /// <summary>True when a warning or an element id was left out of warnings; present with warnings.</summary>
        [JsonProperty("warningsTruncated", NullValueHandling = NullValueHandling.Ignore)]
        public bool? WarningsTruncated { get; set; }

        /// <summary>
        /// <para>使 Revit 回滚提交的错误（同样的上限）；只在提交被拒时输出，此时 success 为 false，
        /// errorMessage 以"执行失败: 提交被 Revit 拒绝: "开头，模型没有改变。</para>
        /// <para>The errors that made Revit roll the commit back (same limits); present only when the
        /// commit was rejected: success is then false, errorMessage starts with
        /// "执行失败: 提交被 Revit 拒绝: " and the model is unchanged.</para>
        /// </summary>
        [JsonProperty("errors", NullValueHandling = NullValueHandling.Ignore)]
        public List<CommitFailureEntry> Errors { get; set; }

        /// <summary>True when an error or an element id was left out of errors; present with errors.</summary>
        [JsonProperty("errorsTruncated", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ErrorsTruncated { get; set; }

        /// <summary>
        /// <para>按提交的记录填 warnings / errors。提交被拒（rejected）时两组都输出（可为空数组）；
        /// 提交成功时只输出确有条目的组，没有失败就什么也不加。</para>
        /// <para>Fill warnings / errors from the commit's log. A rejected commit outputs both (possibly
        /// empty arrays); a successful one only a group that has entries, and nothing when there was
        /// no failure.</para>
        /// </summary>
        internal void AddCommitFailures(CommitFailureLog log, bool rejected)
        {
            List<CommitFailureEntry> warnings = CommitFailureReport.Entries(log, false, out bool warningsTruncated);
            if (rejected || warnings.Count > 0)
            {
                Warnings = warnings;
                WarningsTruncated = warningsTruncated;
            }

            List<CommitFailureEntry> errors = CommitFailureReport.Entries(log, true, out bool errorsTruncated);
            if (rejected || errors.Count > 0)
            {
                Errors = errors;
                ErrorsTruncated = errorsTruncated;
            }
        }
    }
}
