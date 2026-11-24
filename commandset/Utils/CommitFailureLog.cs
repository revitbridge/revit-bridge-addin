namespace RevitMCPCommandSet.Utils
{
    // 本文件不引用 Revit 类型：提交时失败处理的决定在这里，可以脱离 Revit 测试。
    // Revit 一侧（FailuresAccessor 的适配、事务选项）在 CommitFailureGuard.cs。
    // This file uses no Revit type: the decision taken on commit failures lives here and can be
    // tested without Revit. The Revit side (adapting FailuresAccessor, the transaction options) is
    // in CommitFailureGuard.cs.

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
}
