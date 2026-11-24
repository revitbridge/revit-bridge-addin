using Newtonsoft.Json;

namespace RevitMCPCommandSet.Utils
{
    // 本文件不引用 Revit 类型：提交时失败处理的决定与回复内容都在这里，可以脱离 Revit 测试。
    // Revit 一侧（FailuresAccessor 的适配、事务选项）在 CommitFailureGuard.cs。
    // This file uses no Revit type: the decision taken on commit failures and the reply content
    // built from them live here and can be tested without Revit. The Revit side (adapting
    // FailuresAccessor, the transaction options) is in CommitFailureGuard.cs.

    /// <summary>
    /// <para>纯逻辑看到的失败级别（CommitFailureGuard 由 FailureSeverity 映射）。</para>
    /// <para>A failure's level as the pure logic sees it (CommitFailureGuard maps it from FailureSeverity).</para>
    /// </summary>
    internal enum CommitFailureLevel
    {
        /// <summary>FailureSeverity.None: not a failure; skipped.</summary>
        None,

        /// <summary>FailureSeverity.Warning.</summary>
        Warning,

        /// <summary>FailureSeverity.Error or FailureSeverity.DocumentCorruption.</summary>
        Error,
    }

    /// <summary>One failure posted in the document, as the pure logic reads it.</summary>
    internal interface ICommitPostedFailure
    {
        CommitFailureLevel Level { get; }

        /// <summary>The description text as Revit shows it (in the Revit UI language).</summary>
        string Description { get; }

        /// <summary>The failing element ids, then the additional ones; invalid ids left out, repeats allowed.</summary>
        IEnumerable<long> ElementIds { get; }
    }

    /// <summary>One round of failure processing (a FailuresAccessor during PreprocessFailures).</summary>
    internal interface ICommitFailureRound
    {
        IList<ICommitPostedFailure> GetFailures();

        void DeleteWarning(ICommitPostedFailure failure);

        /// <summary>Clear the posted failures silently when the transaction is rolled back.</summary>
        void SetClearAfterRollback();
    }

    /// <summary>What the preprocessor answers Revit.</summary>
    internal enum CommitFailureDecision
    {
        /// <summary>
        /// <para>FailureProcessingResult.Continue：只有警告，已全部删除，没有可显示的，提交照常。</para>
        /// <para>FailureProcessingResult.Continue: warnings only, all deleted; nothing is left to show and the commit goes on.</para>
        /// </summary>
        Continue,

        /// <summary>
        /// <para>FailureProcessingResult.ProceedWithRollBack，并已设"回滚后清除"：什么也不留，什么也不显示。</para>
        /// <para>FailureProcessingResult.ProceedWithRollBack with clear-after-rollback set: nothing is kept, nothing is shown.</para>
        /// </summary>
        RollBack,
    }

    /// <summary>One recorded failure: level, Revit's text and at most <see cref="CommitFailureLog.MaxElementIds"/> distinct ids.</summary>
    internal sealed class CommitFailureRecord
    {
        public bool IsError { get; set; }

        public string Text { get; set; } = string.Empty;

        public List<long> ElementIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// <para>提交时失败的记录与决定（send_code_to_revit、dry_run_code_in_revit、加载器测试与其余改模型的命令共用）：
    /// 每条警告与错误都记下；没有错误时删除全部警告，提交照常；有错误时不自动解决任何一条，
    /// 设"回滚后清除"并回滚。读失败本身出错按错误处理。本类不向外抛异常。</para>
    /// <para>Records commit-time failures and decides what to do with them (shared by
    /// send_code_to_revit, dry_run_code_in_revit, the add-in test loader and the other commands that
    /// change the model): every warning and error is recorded; with no error every warning is deleted
    /// and the commit goes on; with an error none is resolved automatically, clear-after-rollback is
    /// set and the transaction rolls back. A failure to read the failures counts as an error. Nothing
    /// is thrown out of this class.</para>
    /// </summary>
    internal sealed class CommitFailureLog
    {
        /// <summary>At most this many ids are kept per failure: the dry run's documented limit, DryRunFailure.MaxElementIds.</summary>
        public const int MaxElementIds = 50;

        private readonly string _context;
        private readonly Func<Exception, string> _describe;
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="context">Prefix of the texts this class writes itself, e.g. "dry run".</param>
        /// <param name="describe">Turns an exception into text (CodeExecutor.FormatRuntimeError in the add-in).</param>
        public CommitFailureLog(string context, Func<Exception, string> describe)
        {
            _context = string.IsNullOrEmpty(context) ? "commit" : context;
            _describe = describe;
        }

        /// <summary>Every failure recorded, in the order Revit posted them; the same failure seen again is not repeated.</summary>
        public List<CommitFailureRecord> Records { get; } = new List<CommitFailureRecord>();

        /// <summary>An error-level failure was posted, or reading the failures failed; the transaction was rolled back.</summary>
        public bool HadError { get; private set; }

        /// <summary>At least one warning was recorded.</summary>
        public bool HasWarnings => Records.Exists(record => !record.IsError);

        /// <summary>Decide one round. Never throws.</summary>
        public CommitFailureDecision Process(ICommitFailureRound round)
        {
            try
            {
                IList<ICommitPostedFailure> failures = round.GetFailures();
                bool error = false;
                foreach (ICommitPostedFailure failure in failures)
                {
                    CommitFailureLevel level = failure.Level;
                    if (level == CommitFailureLevel.None)
                        continue;
                    if (level != CommitFailureLevel.Warning)
                        error = true;
                    Record(failure, level);
                }

                if (!error)
                {
                    // 只有警告：全部删除，提交照常进行，不显示给用户。
                    // Warnings only: delete them all; the commit goes on and nothing is shown.
                    foreach (ICommitPostedFailure failure in failures)
                    {
                        if (failure.Level == CommitFailureLevel.Warning)
                            round.DeleteWarning(failure);
                    }
                    return CommitFailureDecision.Continue;
                }

                HadError = true;
                return RollBackSilently(round);
            }
            catch (Exception ex)
            {
                // 读失败本身出错：按错误处理，静默回滚，并把原因记成一条错误。
                // Reading the failures failed: treat it as an error, roll back silently and
                // record the reason as one error.
                HadError = true;
                AddProblem("reading Revit's failures failed: " + Describe(ex));
                return RollBackSilently(round);
            }
        }

        private CommitFailureDecision RollBackSilently(ICommitFailureRound round)
        {
            // ProceedWithRollBack 要配合"回滚后清除失败"才不会把失败交给用户（RevitAPI 对 ProceedWithRollBack 的说明）。
            // ProceedWithRollBack must come with "clear after rollback", or the failures may still
            // be delivered to the user (RevitAPI remarks on ProceedWithRollBack).
            try
            {
                round.SetClearAfterRollback();
            }
            catch (Exception ex)
            {
                // 事务上已预先设了同一选项；这里失败只记一条，不改变回滚。
                // The transaction already carries the same option; a failure here is recorded
                // and does not change the rollback.
                AddProblem("setting clear-after-rollback failed: " + Describe(ex));
            }
            return CommitFailureDecision.RollBack;
        }

        private void Record(ICommitPostedFailure failure, CommitFailureLevel level)
        {
            var ids = new List<long>();
            var seenIds = new HashSet<long>();
            IEnumerable<long> source = failure.ElementIds;
            if (source != null)
            {
                foreach (long id in source)
                {
                    if (ids.Count >= MaxElementIds)
                        break;
                    if (seenIds.Add(id))
                        ids.Add(id);
                }
            }

            var record = new CommitFailureRecord
            {
                IsError = level != CommitFailureLevel.Warning,
                Text = failure.Description ?? string.Empty,
                ElementIds = ids,
            };

            // 预处理器可能被调用不止一次；同一条只记一次。
            // The preprocessor may be called more than once; record each failure once.
            string key = (record.IsError ? "Error" : "Warning") + "\n" + record.Text + "\n" + string.Join(",", ids);
            if (_seen.Add(key))
                Records.Add(record);
        }

        private void AddProblem(string text)
        {
            Records.Add(new CommitFailureRecord { IsError = true, Text = _context + ": " + text });
        }

        private string Describe(Exception ex)
        {
            try
            {
                return _describe != null ? _describe(ex) : ex.GetType().FullName + ": " + ex.Message;
            }
            catch (Exception describeError)
            {
                // 描述本身出错时退回到两个类型名，仍记下一条，不让异常离开预处理器。
                // Describing failed: fall back to the two type names, still record a line, and
                // keep the exception inside the preprocessor.
                return ex.GetType().FullName + " (describing it failed: " + describeError.GetType().FullName + ")";
            }
        }
    }

    /// <summary>
    /// <para>send_code_to_revit 回复里的一条警告或错误。</para>
    /// <para>One warning or error in a send_code_to_revit reply.</para>
    /// </summary>
    public class CommitFailureEntry
    {
        /// <summary>The failure's description text as Revit shows it (in the Revit UI language).</summary>
        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>The failing elements, then the additional related elements, at most 20.</summary>
        [JsonProperty("elementIds")]
        public List<long> ElementIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// <para>由 CommitFailureLog 生成回复内容：条目列表（有上限）与提交被拒时的错误文字。</para>
    /// <para>Reply content built from a CommitFailureLog: capped entry lists and the error text of a
    /// rejected commit.</para>
    /// </summary>
    internal static class CommitFailureReport
    {
        /// <summary>At most this many warnings, and this many errors, per reply.</summary>
        public const int MaxEntries = 50;

        /// <summary>At most this many element ids per entry.</summary>
        public const int MaxElementIdsPerEntry = 20;

        /// <summary>Starts the error text of a commit Revit did not complete, so that a client can tell it apart.</summary>
        public const string RejectedPrefix = "提交被 Revit 拒绝: ";

        /// <summary>
        /// <para>按记录顺序取警告（errors 为 false）或错误，最多 50 条、每条最多 20 个 id；
        /// 去掉了条目或 id 时 truncated 为 true。</para>
        /// <para>The warnings (errors false) or errors in recorded order, at most 50, each with at
        /// most 20 ids; truncated is true when an entry or an id was left out.</para>
        /// </summary>
        public static List<CommitFailureEntry> Entries(CommitFailureLog log, bool errors, out bool truncated)
        {
            var entries = new List<CommitFailureEntry>();
            truncated = false;
            foreach (CommitFailureRecord record in log.Records)
            {
                if (record.IsError != errors)
                    continue;
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }
                List<long> ids = record.ElementIds ?? new List<long>();
                if (ids.Count > MaxElementIdsPerEntry)
                {
                    truncated = true;
                    ids = ids.GetRange(0, MaxElementIdsPerEntry);
                }
                entries.Add(new CommitFailureEntry
                {
                    Message = record.Text ?? string.Empty,
                    ElementIds = new List<long>(ids),
                });
            }
            return entries;
        }

        /// <summary>"text (元素 1, 2)", or just the text when the entry has no element.</summary>
        public static string Line(CommitFailureEntry entry)
        {
            return entry.ElementIds.Count == 0
                ? entry.Message
                : entry.Message + " (元素 " + string.Join(", ", entry.ElementIds) + ")";
        }

        /// <summary>
        /// <para>提交没有完成（status 不是 Committed）时的错误文字：前缀加每条错误；没有记录到错误时说明事务状态。</para>
        /// <para>Error text for a commit that did not complete (status other than Committed): the
        /// prefix and every error; with no recorded error, the transaction status.</para>
        /// </summary>
        public static string RejectionText(CommitFailureLog log, string status)
        {
            List<CommitFailureEntry> errors = Entries(log, true, out bool truncated);
            if (errors.Count == 0)
                return RejectedPrefix + "事务状态为 " + status + "，没有记录到错误";

            string text = RejectedPrefix + string.Join("; ", errors.Select(Line));
            if (truncated)
                text += "; ...（错误或元素过多，未全部列出）";
            return text;
        }
    }
}
