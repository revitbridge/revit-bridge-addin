using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>只编译、不执行：用与 send_code_to_revit 相同的外壳、白名单和黑名单编译到内存流，
    /// 返回按用户代码行号映射的诊断。不加载程序集、不开事务、不经 ExternalEvent、不弹框，
    /// 直接在 Socket 线程上运行，超时 30 秒。</para>
    /// <para>Compile only, never run: the same wrapper, whitelist and blacklist as
    /// send_code_to_revit, emitted to a memory stream, with diagnostics mapped to user-code
    /// lines. Loads no assembly, opens no transaction, raises no ExternalEvent and shows no
    /// dialog; it runs directly on the socket thread with a 30 s timeout.</para>
    /// </summary>
    public class CheckCodeCommand : IRevitCommand
    {
        /// <summary>Compile timeout.</summary>
        public const int TimeoutMilliseconds = 30000;

        public string CommandName => "check_code_in_revit";

        public object Execute(JObject parameters, string requestId)
        {
            if (parameters == null || !parameters.ContainsKey("code"))
            {
                throw new ArgumentException("Missing required parameter: 'code'");
            }

            string code = parameters["code"].Type == JTokenType.Null
                ? string.Empty
                : parameters["code"].Value<string>() ?? string.Empty;

            // "parameters" 只用于形状，不求值，这里不读取。
            // "parameters" only describes the shape and is never evaluated, so it is not read here.

            var result = new CheckCodeResult { WrapperVersion = CodeExecutor.WrapperVersion };

            using (var cts = new CancellationTokenSource(TimeoutMilliseconds))
            {
                try
                {
                    var outcome = CodeExecutor.Compile(code, cts.Token);
                    result.Compiled = outcome.Compiled;
                    result.Diagnostics = outcome.Diagnostics;
                    result.Truncated = outcome.Truncated;
                    result.Fingerprint = outcome.Fingerprint;
                }
                catch (OperationCanceledException)
                {
                    result.ErrorMessage = $"compile timed out after {TimeoutMilliseconds / 1000} s";
                }
                catch (Exception ex)
                {
                    // 编译器本身出错（不是编译失败）。
                    // The compiler itself failed (as opposed to the code not compiling).
                    result.ErrorMessage = $"compiler error: {ex.GetType().Name}: {ex.Message}";
                }
            }

            if (result.Fingerprint == null)
            {
                try
                {
                    result.Fingerprint = CodeExecutor.ComputeFingerprint(code);
                }
                catch (Exception)
                {
                    // 留空：errorMessage 已说明原因。
                    // Leave it empty; errorMessage already says what went wrong.
                    result.Fingerprint = string.Empty;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// <para>check_code_in_revit 的返回结构。</para>
    /// <para>Result of check_code_in_revit.</para>
    /// </summary>
    public class CheckCodeResult
    {
        [JsonProperty("compiled")]
        public bool Compiled { get; set; }

        [JsonProperty("diagnostics")]
        public List<CodeDiagnostic> Diagnostics { get; set; } = new List<CodeDiagnostic>();

        [JsonProperty("truncated")]
        public int Truncated { get; set; }

        [JsonProperty("fingerprint")]
        public string Fingerprint { get; set; }

        [JsonProperty("wrapper_version")]
        public int WrapperVersion { get; set; }

        /// <summary>Compiler exceptions and timeouts only; a failed compile leaves it empty.</summary>
        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
