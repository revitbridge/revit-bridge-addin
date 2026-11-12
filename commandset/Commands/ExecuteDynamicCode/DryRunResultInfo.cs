using Newtonsoft.Json;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>dry_run_code_in_revit 的返回结构（与 send_code_to_revit 的 ExecutionResultInfo 分开，后者不变）。
    /// 每个键恒在，未到达的步骤留空值。</para>
    /// <para>Result of dry_run_code_in_revit, separate from send_code_to_revit's ExecutionResultInfo,
    /// which is unchanged. Every key is always present; a step that was not reached leaves its empty value.</para>
    /// </summary>
    public class DryRunResultInfo
    {
        /// <summary>The code ran to the end, the inner transaction committed and no error-level failure, exception or rollback problem remained.</summary>
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>
        /// <para>本次运行没有在文档里留下改动：事务组已回滚，或者根本没有开事务组（编译失败、没有文档）。
        /// 为 false 即本方法的缺陷，error 里有说明。</para>
        /// <para>The run left no change in the document: the transaction group was rolled back, or none
        /// was started (compile failure, no document). False is a defect of this method; error says why.</para>
        /// </summary>
        [JsonProperty("rolled_back")]
        public bool RolledBack { get; set; }

        /// <summary>The code's return value serialized as JSON, as send_code_to_revit returns it; empty when the code did not return.</summary>
        [JsonProperty("result")]
        public string Result { get; set; } = string.Empty;

        /// <summary>
        /// <para>异常的类型全名与消息（CodeExecutor.FormatRuntimeError）：代码运行时抛出的，或试跑本身的问题。
        /// 编译失败与错误级失败不写这里，见 diagnostics 与 failures。</para>
        /// <para>Full type name and message of an exception (CodeExecutor.FormatRuntimeError): one the
        /// code threw while running, or a problem of the dry run itself. Compile errors and
        /// error-level failures are not written here; see diagnostics and failures.</para>
        /// </summary>
        [JsonProperty("error")]
        public string Error { get; set; } = string.Empty;

        /// <summary>Compiler diagnostics (same shape as check_code_in_revit) when compiling failed; empty otherwise.</summary>
        [JsonProperty("diagnostics")]
        public List<CodeDiagnostic> Diagnostics { get; set; } = new List<CodeDiagnostic>();

        /// <summary>What the committed inner transaction added, modified and deleted, read before the rollback.</summary>
        [JsonProperty("changes")]
        public DryRunChanges Changes { get; set; } = new DryRunChanges();

        /// <summary>Every warning and error Revit posted when the inner transaction was committed.</summary>
        [JsonProperty("failures")]
        public List<DryRunFailure> Failures { get; set; } = new List<DryRunFailure>();

        /// <summary>The compile fingerprint, as send_code_to_revit and check_code_in_revit report it.</summary>
        [JsonProperty("fingerprint")]
        public string Fingerprint { get; set; } = string.Empty;
    }

    /// <summary>
    /// <para>试跑的增、改、删。每组 ids 最多 <see cref="MaxIds"/> 个（按 id 升序），超出的只计入 count。</para>
    /// <para>Added, modified and deleted elements of a dry run. Each ids list holds at most
    /// <see cref="MaxIds"/> ids (ascending); the rest are only counted.</para>
    /// </summary>
    public class DryRunChanges
    {
        /// <summary>At most this many ids per list.</summary>
        public const int MaxIds = 50;

        /// <summary>The note every reply carries: the ids refer to a state that was rolled back.</summary>
        public const string RolledBackNote =
            "Rolled back: the ids in added no longer exist; modified and deleted elements are back as they were before the dry run.";

        [JsonProperty("added")]
        public DryRunChangeSet Added { get; set; } = new DryRunChangeSet();

        [JsonProperty("modified")]
        public DryRunChangeSet Modified { get; set; } = new DryRunChangeSet();

        [JsonProperty("deleted")]
        public DryRunDeletedSet Deleted { get; set; } = new DryRunDeletedSet();

        [JsonProperty("note")]
        public string Note { get; set; } = RolledBackNote;
    }

    /// <summary>
    /// <para>新增或修改的元素：总数、按类别计数、前 50 个 id。</para>
    /// <para>Added or modified elements: the count, counts by category and the first 50 ids.</para>
    /// </summary>
    public class DryRunChangeSet
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>
        /// <para>键是 BuiltInCategory 的名字；元素类型（ElementType）记在 "(types)"，没有类别的元素记在
        /// "(none)"，类别不是 BuiltInCategory 的记在 "(other)"。各项之和等于 count。</para>
        /// <para>Keys are BuiltInCategory names; element types (ElementType) count under "(types)",
        /// elements without a category under "(none)", a category that is not a BuiltInCategory under
        /// "(other)". The values add up to count.</para>
        /// </summary>
        [JsonProperty("by_category")]
        public SortedDictionary<string, int> ByCategory { get; set; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

        [JsonProperty("ids")]
        public List<long> Ids { get; set; } = new List<long>();
    }

    /// <summary>
    /// <para>删除的元素：提交时已不存在，只有总数与前 50 个 id。</para>
    /// <para>Deleted elements: gone when the transaction committed, so only the count and the first 50 ids.</para>
    /// </summary>
    public class DryRunDeletedSet
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("ids")]
        public List<long> Ids { get; set; } = new List<long>();
    }

    /// <summary>
    /// <para>Revit 在提交时报告的一条失败。</para>
    /// <para>One failure Revit posted while the transaction was committed.</para>
    /// </summary>
    public class DryRunFailure
    {
        /// <summary>At most this many element ids per failure.</summary>
        public const int MaxElementIds = 50;

        /// <summary>"Warning" or "Error" (document corruption counts as "Error").</summary>
        [JsonProperty("severity")]
        public string Severity { get; set; }

        /// <summary>The failure's description text as Revit shows it (in the Revit UI language).</summary>
        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;

        /// <summary>The failing elements, then the additional related elements, at most 50.</summary>
        [JsonProperty("element_ids")]
        public List<long> ElementIds { get; set; } = new List<long>();
    }
}
