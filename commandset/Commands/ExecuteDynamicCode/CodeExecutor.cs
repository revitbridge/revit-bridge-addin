using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using RevitMCPCommandSet.Commands.Bridge;
using RoslynLocation = Microsoft.CodeAnalysis.Location;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>即席代码的编译与执行。send_code_to_revit 与 check_code_in_revit 共用这里的
    /// 同一个外壳、同一份引用白名单和同一份符号黑名单：<see cref="Compile"/> 只编译到内存流，
    /// <see cref="Execute"/> 才加载并调用。</para>
    /// <para>Compiles and runs ad-hoc code. send_code_to_revit and check_code_in_revit share
    /// the one wrapper, reference whitelist and symbol blacklist defined here:
    /// <see cref="Compile"/> only emits to a memory stream, <see cref="Execute"/> is the
    /// only step that loads and invokes the result.</para>
    /// </summary>
    public static class CodeExecutor
    {
        /// <summary>
        /// <para>外壳版本：外壳文本、引用白名单或符号黑名单变化时加一（进入 fingerprint）。</para>
        /// <para>Wrapper version: bump when the wrapper text, the reference whitelist or the
        /// symbol blacklist changes (it is part of the fingerprint).</para>
        /// </summary>
        public const int WrapperVersion = 1;

        /// <summary>At most this many diagnostics are returned; the rest are counted in Truncated.</summary>
        public const int MaxDiagnostics = 50;

        /// <summary>Diagnostic id for a blacklisted symbol.</summary>
        public const string ForbiddenSymbolId = "RB0001";

        // FormatRuntimeError 最多描述这么多层异常（真正抛出的那个加它的内层），也是解包的层数上限。
        // FormatRuntimeError describes at most this many exceptions (the one thrown plus its inner
        // ones); also the most wrappers it peels off.
        private const int MaxExceptionDepth = 8;

        private const string WrapperAssemblyName = "AIGeneratedCode";
        private const string WrapperTypeName = "AIGeneratedCode.CodeExecutor";
        private const string WrapperMethodName = "Execute";

        // 外壳前缀以换行结束，用户代码从自己那一行的第 1 列开始；行号偏移由它实际算出，不写死。
        // The prefix ends with a line break, so user code starts at column 1 of its own line;
        // the line offset is computed from this text, never hard-coded.
        private static readonly string WrapperPrefix = string.Join("\n", new[]
        {
            "using System;",
            "using System.Linq;",
            "using Autodesk.Revit.DB;",
            "using Autodesk.Revit.UI;",
            "using System.Collections.Generic;",
            "",
            "namespace AIGeneratedCode",
            "{",
            "    public static class CodeExecutor",
            "    {",
            "        public static object Execute(Document document, object[] parameters)",
            "        {",
            "            // user code",
            "",
        });

        private const string WrapperSuffix = "\n        }\n    }\n}\n";

        // 引用白名单：只引用固定的基础库与 Revit API，而非「全部已加载程序集」，缩小可被滥用的攻击面。
        // Reference whitelist: only a fixed set of base libraries plus the Revit API, instead of
        // every loaded assembly, to shrink the abuse surface.
        private static readonly HashSet<string> AllowedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System",
            "System.Core",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
            "System.Linq",
            "netstandard",
            "RevitAPI",
            "RevitAPIUI",
        };

        // 符号黑名单：被禁止的标识符（含命名空间片段，如 System.IO 的 "IO"）。
        // Symbol blacklist: forbidden identifiers (namespace segments included, e.g. "IO" of System.IO).
        private static readonly HashSet<string> ForbiddenIdentifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "IO",        // System.IO
            "Process",   // System.Diagnostics.Process
            "Assembly",  // System.Reflection.Assembly
            "DllImport", // P/Invoke
            "File",      // System.IO.File
            "Registry",  // Microsoft.Win32.Registry
        };

        // 只对这些编译错误提取 symbol。
        // Only these compiler errors get a symbol.
        private static readonly HashSet<string> SymbolDiagnosticIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "CS1061", "CS0117", "CS0246", "CS0103", "CS1501", "CS1503", "CS7036",
        };

        // 这两个错误的消息先给类型、再给缺失的成员名（'Wall' ... 'LocationCurve'）；symbol 取成员名。
        // For these two the message names the type first and the missing member second
        // ('Wall' ... 'LocationCurve'); the symbol is the member.
        private static readonly HashSet<string> MemberSecondIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "CS1061", "CS0117",
        };

        /// <summary>
        /// <para>编译用户代码：套外壳、查黑名单、按白名单引用、Emit 到内存流。不加载程序集、
        /// 不开事务、不碰 Revit 对象，可在任意线程调用。</para>
        /// <para>Compile user code: wrap it, check the blacklist, reference the whitelist and
        /// emit to a memory stream. Loads nothing, opens no transaction and touches no Revit
        /// object, so it may run on any thread.</para>
        /// </summary>
        /// <param name="code">User code: the body of <c>Execute(Document document, object[] parameters)</c>.</param>
        /// <param name="cancellationToken">Cancels parsing and emitting (check_code_in_revit uses a 30 s timeout).</param>
        public static CompileOutcome Compile(string code, CancellationToken cancellationToken = default(CancellationToken))
        {
            code = code ?? string.Empty;

            List<Assembly> referencedAssemblies = CollectReferencedAssemblies();
            string fingerprint = ComputeFingerprint(code, referencedAssemblies);

            string wrappedCode = WrapperPrefix + code + WrapperSuffix;
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode, cancellationToken: cancellationToken);
            var map = new UserCodeMap(syntaxTree.GetText(cancellationToken), WrapperPrefix.Length, code.Length);

            // 符号黑名单静态检查：命中即返回 RB0001，不编译、不抛异常。
            // Symbol blacklist: a hit becomes an RB0001 diagnostic; nothing is compiled or thrown.
            CodeDiagnostic forbidden = FindForbiddenSymbol(syntaxTree, map, cancellationToken);
            if (forbidden != null)
            {
                return new CompileOutcome
                {
                    Compiled = false,
                    Diagnostics = new List<CodeDiagnostic> { forbidden },
                    Fingerprint = fingerprint,
                    ForbiddenSymbol = forbidden.Symbol,
                };
            }

            var references = referencedAssemblies
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            var compilation = CSharpCompilation.Create(
                WrapperAssemblyName,
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms, cancellationToken: cancellationToken);

                // 错误在前、警告其次、信息最后，同级按位置；超过上限的只计数。
                // Errors first, then warnings, then info, each in source order; beyond the cap only counted.
                var reported = emitResult.Diagnostics
                    .Where(d => !d.IsSuppressed && d.Severity != DiagnosticSeverity.Hidden)
                    .OrderBy(d => SeverityRank(d.Severity))
                    .ThenBy(d => d.Location.IsInSource ? d.Location.SourceSpan.Start : -1)
                    .ToList();

                return new CompileOutcome
                {
                    Compiled = emitResult.Success,
                    Compilation = compilation,
                    Diagnostics = reported.Take(MaxDiagnostics).Select(d => ToCodeDiagnostic(d, map)).ToList(),
                    Truncated = Math.Max(0, reported.Count - MaxDiagnostics),
                    Fingerprint = fingerprint,
                    Image = emitResult.Success ? ms.ToArray() : null,
                };
            }
        }

        /// <summary>
        /// <para>加载 <see cref="Compile"/> 的结果并调用入口。必须在 Revit API 上下文中、由调用方开好事务后调用。</para>
        /// <para>Load a successful <see cref="Compile"/> result and invoke its entry point. Call it
        /// in Revit API context, inside the caller's transaction.</para>
        /// </summary>
        public static object Execute(CompileOutcome outcome, Document document, object[] parameters)
        {
            if (outcome == null || !outcome.Compiled || outcome.Image == null)
                throw new InvalidOperationException("Execute needs a successful Compile result.");

            var assembly = Assembly.Load(outcome.Image);
            var executorType = assembly.GetType(WrapperTypeName);
            var executeMethod = executorType.GetMethod(WrapperMethodName);

            return executeMethod.Invoke(null, new object[] { document, parameters ?? Array.Empty<object>() });
        }

        /// <summary>
        /// <para>代码运行失败时 errorMessage 的说明文本：<c>&lt;异常类型全名&gt;: &lt;Message&gt;</c>。
        /// 反射调用套上的 TargetInvocationException、只含一个异常的 AggregateException 先解包到真正抛出的异常；
        /// 该异常若还有内层异常，依次以 <c> ---&gt; &lt;类型全名&gt;: &lt;Message&gt;</c> 接在后面（同 Exception.ToString()）；
        /// 含多个异常的 AggregateException 到此为止，它的 Message 已列出各内层异常的消息。</para>
        /// <para>The errorMessage text for a run that threw: <c>&lt;full exception type name&gt;: &lt;Message&gt;</c>.
        /// The TargetInvocationException the reflective call wraps around it, and an AggregateException
        /// holding a single exception, are unwrapped to the exception actually thrown; inner exceptions of
        /// that one follow as <c> ---&gt; &lt;full type name&gt;: &lt;Message&gt;</c>, as in Exception.ToString().
        /// An AggregateException holding several exceptions ends the text: its Message already lists theirs.</para>
        /// </summary>
        public static string FormatRuntimeError(Exception exception)
        {
            var text = new StringBuilder();
            Exception current = Unwrap(exception);
            for (int depth = 0; current != null && depth < MaxExceptionDepth; depth++)
            {
                if (depth > 0)
                    text.Append(" ---> ");
                Type type = current.GetType();
                text.Append(type.FullName ?? type.Name).Append(": ").Append(current.Message);
                current = current is AggregateException ? null : Unwrap(current.InnerException);
            }
            return text.ToString();
        }

        // 剥掉包装：TargetInvocationException 与只含一个异常的 AggregateException，可以相互嵌套。
        // Peel off wrappers: TargetInvocationException and single-exception AggregateException, nested in any order.
        private static Exception Unwrap(Exception exception)
        {
            for (int depth = 0; exception != null && depth < MaxExceptionDepth; depth++)
            {
                if (exception is TargetInvocationException && exception.InnerException != null)
                    exception = exception.InnerException;
                else if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                    exception = aggregate.InnerExceptions[0];
                else
                    break;
            }
            return exception;
        }

        /// <summary>
        /// <para>只算 fingerprint（编译本身抛错或超时时用）。</para>
        /// <para>The fingerprint alone, for when compiling itself threw or timed out.</para>
        /// </summary>
        public static string ComputeFingerprint(string code)
        {
            return ComputeFingerprint(code ?? string.Empty, CollectReferencedAssemblies());
        }

        // sha256(code \0 wrapper_version \0 "name/version" lines of the references \0 addin_version)
        private static string ComputeFingerprint(string code, IEnumerable<Assembly> referencedAssemblies)
        {
            var referenceIds = referencedAssemblies
                .Select(a => a.GetName())
                .Select(n => n.Name + "/" + (n.Version != null ? n.Version.ToString() : "0.0.0.0"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal);

            var input = new StringBuilder();
            input.Append(code).Append('\0');
            input.Append(WrapperVersion.ToString(CultureInfo.InvariantCulture)).Append('\0');
            input.Append(string.Join("\n", referenceIds)).Append('\0');
            input.Append(BridgeInfo.AddinVersion);

            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input.ToString()));
            }
            return "sha256:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static List<Assembly> CollectReferencedAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Where(a => AllowedAssemblyNames.Contains(a.GetName().Name))
                .ToList();
        }

        /// <summary>
        /// <para>遍历语法树的标识符，返回第一个被禁止的符号（无则 null）。</para>
        /// <para>Walk the identifiers of the tree and return the first forbidden one, or null.</para>
        /// </summary>
        private static CodeDiagnostic FindForbiddenSymbol(SyntaxTree syntaxTree, UserCodeMap map, CancellationToken cancellationToken)
        {
            foreach (var token in syntaxTree.GetRoot(cancellationToken).DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken) || !ForbiddenIdentifiers.Contains(token.ValueText))
                    continue;

                var diagnostic = new CodeDiagnostic
                {
                    Id = ForbiddenSymbolId,
                    Severity = "error",
                    Message = "forbidden symbol: " + token.ValueText,
                    Symbol = token.ValueText,
                };
                map.Apply(token.GetLocation(), diagnostic);
                return diagnostic;
            }
            return null;
        }

        private static CodeDiagnostic ToCodeDiagnostic(Diagnostic d, UserCodeMap map)
        {
            // 消息固定用英文（不随 Revit 界面语言变化），symbol 的提取依赖它。
            // Messages are always English (independent of the Revit UI language); symbol extraction relies on it.
            string message = d.GetMessage(CultureInfo.InvariantCulture);
            var diagnostic = new CodeDiagnostic
            {
                Id = d.Id,
                Severity = SeverityName(d.Severity),
                Message = message,
                Symbol = ExtractSymbol(d.Id, message),
            };
            map.Apply(d.Location, diagnostic);
            return diagnostic;
        }

        /// <summary>
        /// <para>从消息里的引号取 symbol：CS1061 / CS0117 取第二个（成员名），其余取第一个。</para>
        /// <para>Take the symbol from the quoted names in the message: the second one (the
        /// member) for CS1061 / CS0117, the first one otherwise.</para>
        /// </summary>
        internal static string ExtractSymbol(string id, string message)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(message) || !SymbolDiagnosticIds.Contains(id))
                return null;

            int wanted = MemberSecondIds.Contains(id) ? 1 : 0;
            int searchFrom = 0;
            for (int index = 0; ; index++)
            {
                int open = message.IndexOf('\'', searchFrom);
                if (open < 0) return null;
                int close = message.IndexOf('\'', open + 1);
                if (close < 0) return null;
                if (index == wanted)
                {
                    string symbol = message.Substring(open + 1, close - open - 1);
                    return symbol.Length == 0 ? null : symbol;
                }
                searchFrom = close + 1;
            }
        }

        private static int SeverityRank(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return 0;
                case DiagnosticSeverity.Warning: return 1;
                default: return 2;
            }
        }

        private static string SeverityName(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return "error";
                case DiagnosticSeverity.Warning: return "warning";
                default: return "info";
            }
        }

        /// <summary>
        /// <para>把外壳内的位置换算成用户代码的 1 起行列；落在用户代码之外的标记为 in_wrapper。</para>
        /// <para>Maps positions in the wrapped text to 1-based user-code line/column; anything
        /// outside the user code is marked in_wrapper.</para>
        /// </summary>
        private sealed class UserCodeMap
        {
            private readonly SourceText _text;
            private readonly int _start;
            private readonly int _end;
            private readonly int _startLine;
            private readonly int _startCharacter;

            public UserCodeMap(SourceText text, int userCodeStart, int userCodeLength)
            {
                _text = text;
                _start = userCodeStart;
                _end = userCodeStart + userCodeLength;
                var startPosition = text.Lines.GetLinePosition(userCodeStart);
                _startLine = startPosition.Line;
                _startCharacter = startPosition.Character;
            }

            public void Apply(RoslynLocation location, CodeDiagnostic diagnostic)
            {
                diagnostic.Line = null;
                diagnostic.Column = null;
                diagnostic.InWrapper = true;

                if (location == null || !location.IsInSource)
                    return;

                // 结束位置也算用户代码：缺分号等错误报在最后一个 token 之后。
                // The end position still counts as user code: a missing ';' is reported just after the last token.
                int position = location.SourceSpan.Start;
                if (position < _start || position > _end)
                    return;

                var linePosition = _text.Lines.GetLinePosition(position);
                diagnostic.Line = linePosition.Line - _startLine + 1;
                diagnostic.Column = (linePosition.Line == _startLine
                    ? linePosition.Character - _startCharacter
                    : linePosition.Character) + 1;
                diagnostic.InWrapper = false;
            }
        }
    }

    /// <summary>
    /// <para><see cref="CodeExecutor.Compile"/> 的结果。</para>
    /// <para>Result of <see cref="CodeExecutor.Compile"/>.</para>
    /// </summary>
    public sealed class CompileOutcome
    {
        /// <summary>True when the code compiled without errors.</summary>
        public bool Compiled { get; internal set; }

        /// <summary>The Roslyn compilation; null when the blacklist rejected the code before compiling.</summary>
        public CSharpCompilation Compilation { get; internal set; }

        /// <summary>Mapped diagnostics, errors first, at most <see cref="CodeExecutor.MaxDiagnostics"/>.</summary>
        public List<CodeDiagnostic> Diagnostics { get; internal set; } = new List<CodeDiagnostic>();

        /// <summary>How many diagnostics were left out.</summary>
        public int Truncated { get; internal set; }

        /// <summary>"sha256:&lt;hex&gt;" over code, wrapper version, references and add-in version.</summary>
        public string Fingerprint { get; internal set; }

        /// <summary>The blacklisted symbol that stopped compilation, or null.</summary>
        public string ForbiddenSymbol { get; internal set; }

        /// <summary>The emitted assembly; only set when compiled, only read by Execute.</summary>
        internal byte[] Image { get; set; }

        /// <summary>
        /// <para>send_code_to_revit 的 errorMessage 文本（行号为用户代码行号）。</para>
        /// <para>The errorMessage text for send_code_to_revit, with user-code line numbers.</para>
        /// </summary>
        public string FormatErrorMessage()
        {
            if (ForbiddenSymbol != null)
            {
                return $"代码包含被禁止的符号，已拒绝执行: '{ForbiddenSymbol}'\n" +
                       $"Code contains a forbidden symbol and was rejected: '{ForbiddenSymbol}'";
            }

            var errors = Diagnostics
                .Where(d => d.Severity == "error")
                .Select(d => d.Line.HasValue
                    ? $"Line {d.Line.Value}: {d.Message}"
                    : $"Wrapper: {d.Message}");
            return $"代码编译错误:\n{string.Join("\n", errors)}";
        }
    }

    /// <summary>
    /// <para>一条编译诊断（check_code_in_revit 与 send_code_to_revit 共用的形状）。</para>
    /// <para>One compiler diagnostic, in the shape shared by check_code_in_revit and send_code_to_revit.</para>
    /// </summary>
    public class CodeDiagnostic
    {
        /// <summary>Compiler id such as "CS1061", or "RB0001" for a blacklisted symbol.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>"error", "warning" or "info".</summary>
        [JsonProperty("severity")]
        public string Severity { get; set; }

        /// <summary>1-based line in the user code; null inside the wrapper.</summary>
        [JsonProperty("line")]
        public int? Line { get; set; }

        /// <summary>1-based column in the user code; null inside the wrapper.</summary>
        [JsonProperty("column")]
        public int? Column { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>The unresolved name for CS1061 / CS0117 / CS0246 / CS0103 / CS1501 / CS1503 / CS7036 and RB0001, else null.</summary>
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        /// <summary>True when the location is in the wrapper (or nowhere in source), not in the user code.</summary>
        [JsonProperty("in_wrapper")]
        public bool InWrapper { get; set; }
    }
}
