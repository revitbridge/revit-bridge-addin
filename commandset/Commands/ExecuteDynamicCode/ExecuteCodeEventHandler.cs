using Autodesk.Revit.UI;
using Newtonsoft.Json;
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

                    // 反射调用执行方法
                    var result = CodeExecutor.Execute(outcome, doc, _executionParameters);

                    transaction.Commit();

                    ResultInfo.Success = true;
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                }
            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                var innerMsg = ex.InnerException != null
                    ? $"{ex.InnerException.GetType().Name}: {ex.InnerException.Message}"
                    : ex.Message;
                ResultInfo.ErrorMessage = $"执行失败: {innerMsg}";
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
    }
}
