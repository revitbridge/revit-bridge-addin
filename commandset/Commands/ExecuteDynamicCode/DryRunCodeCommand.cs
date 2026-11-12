using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>试跑即席代码：参数与 send_code_to_revit 相同（code、parameters），另可带 sources（与片段同一次编译的
    /// 完整 C# 文件，见 CodeSources），不带 confirm，不弹确认对话框。
    /// 代码在事务组里的一个事务中运行；提交该事务以取得 Revit 报告的增、改、删与警告、错误，
    /// 随后总是回滚整个事务组，文档不留改动，撤销栈不留条目。超时与 send_code_to_revit 相同（60 秒）。</para>
    /// <para>Dry run of ad-hoc code: the same parameters as send_code_to_revit (code, parameters),
    /// plus optional sources (complete C# files compiled together with the snippet, see
    /// CodeSources), no confirm and no confirmation dialog. The code runs in one transaction inside a transaction
    /// group; that transaction is committed to get what Revit reports as added, modified and deleted
    /// and the warnings and errors it raised, then the whole group is always rolled back, so the
    /// document keeps no change and the undo stack no entry. Same 60 s timeout as send_code_to_revit.</para>
    /// </summary>
    public class DryRunCodeCommand : ExternalEventCommandBase
    {
        /// <summary>Same as send_code_to_revit.</summary>
        public const int TimeoutMilliseconds = 60000;

        private DryRunCodeEventHandler _handler => (DryRunCodeEventHandler)Handler;

        public override string CommandName => "dry_run_code_in_revit";

        public DryRunCodeCommand(UIApplication uiApp)
            : base(new DryRunCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                if (parameters == null || !parameters.ContainsKey("code"))
                {
                    throw new ArgumentException("Missing required parameter: 'code'");
                }

                string code = parameters["code"].Type == JTokenType.Null
                    ? string.Empty
                    : parameters["code"].Value<string>() ?? string.Empty;
                JArray parametersArray = parameters["parameters"] as JArray;
                object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();

                // 可选的附加源文件：在 Socket 线程上、进 Revit 之前检查上限，不合规直接报错。
                // Optional extra source files: checked against the limits on the socket thread,
                // before anything reaches Revit; a request that breaks them fails here.
                IReadOnlyList<CodeSource> sources = CodeSources.FromRequest(parameters);

                // 上一次试跑超时后仍在 Revit 里运行时不排新的：处理器同一时间只持有一个请求，
                // 结果不会被后来的请求覆盖或错配。
                // While an earlier dry run that timed out is still running in Revit, refuse a new
                // one: the handler holds one request at a time, so no result is overwritten or
                // handed to the wrong caller.
                if (!_handler.TryQueue(code, executionParameters, sources))
                {
                    throw new InvalidOperationException(
                        "An earlier dry run is still running in Revit; try again when it has finished.");
                }

                if (RaiseAndWaitForCompletion(TimeoutMilliseconds))
                {
                    return _handler.TakeResult();
                }

                // 还没开始的请求撤回，之后不再为无人等待的调用方运行；已经开始的会自己跑完并回滚。
                // Withdraw a request Revit has not started, so it does not run later for a caller
                // that is gone; one that has started finishes and rolls back on its own.
                bool withdrawn = _handler.WithdrawIfNotStarted();
                throw new TimeoutException(withdrawn
                    ? $"Revit did not start the dry run within {TimeoutMilliseconds / 1000} s; it was withdrawn and will not run."
                    : $"The dry run did not finish within {TimeoutMilliseconds / 1000} s; it keeps running in Revit and is rolled back when it ends.");
            }
            catch (Exception ex)
            {
                throw new Exception($"试跑失败 / dry run failed: {ex.Message}", ex);
            }
        }
    }
}
