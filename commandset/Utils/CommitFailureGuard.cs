using Autodesk.Revit.DB;
using RevitMCPCommandSet.Commands.ExecuteDynamicCode;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// <para>提交时失败处理的 Revit 一侧：把 FailuresAccessor 交给 CommitFailureLog 决定，并把选项装到事务上。
    /// 预处理器在 Revit 线程、提交过程中运行：不显示界面、不阻塞、不向外抛异常。</para>
    /// <para>The Revit side of commit-time failure handling: hands the FailuresAccessor to
    /// CommitFailureLog for the decision and installs the options on a transaction. The
    /// preprocessor runs on Revit's thread inside the commit: no UI, no blocking call, no exception
    /// thrown out of it.</para>
    /// </summary>
    internal sealed class CommitFailureGuard : IFailuresPreprocessor
    {
        /// <param name="context">Prefix of the texts the log writes itself, e.g. "dry run".</param>
        public CommitFailureGuard(string context)
        {
            Log = new CommitFailureLog(context, CodeExecutor.FormatRuntimeError);
        }

        /// <summary>What was recorded and decided.</summary>
        public CommitFailureLog Log { get; }

        /// <summary>
        /// <para>新建一个并装到事务上，见 <see cref="InstallOn"/>。</para>
        /// <para>Create one and install it on the transaction; see <see cref="InstallOn"/>.</para>
        /// </summary>
        public static CommitFailureGuard Install(Transaction transaction, string context)
        {
            var guard = new CommitFailureGuard(context);
            guard.InstallOn(transaction);
            return guard;
        }

        /// <summary>
        /// <para>装到事务上（开始之后、提交或回滚之前任何时候都可以）：本预处理器、回滚后清除失败。
        /// 这正是 2026-11-12 真机验过的试跑配置；其余选项保持默认（强制模态）：预处理器已删除每条警告、
        /// 遇错误就回滚，正常情况下没有可显示的失败，而非模态处理会让 Commit 返回 Pending、结果未定。
        /// 之后只有 Commit 返回 Committed 才算提交成功。</para>
        /// <para>Install on a transaction (any time after it started and before it is committed or
        /// rolled back): this preprocessor and clear failures after a rollback. That is the dry-run
        /// setup verified in a real Revit on 2026-11-12; the other options keep their defaults
        /// (forced modal handling): the preprocessor deletes every warning and rolls back on any
        /// error, so normally nothing is left to show, while modeless handling could make Commit
        /// return Pending with the outcome still open. After that only a Commit that returns
        /// Committed is a success.</para>
        /// </summary>
        public void InstallOn(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        /// <summary>
        /// <para>Commit 返回的状态不是 Committed 时的错误文字：RolledBack 是"提交被 Revit 拒绝: "加记下的错误
        /// （模型没有改变）；其他状态是"提交没有完成: "，不说模型没变（结果未定）。</para>
        /// <para>Error text for a Commit that did not return Committed: RolledBack gives
        /// "提交被 Revit 拒绝: " and the recorded errors (the model is unchanged); any other status
        /// gives "提交没有完成: " and does not claim the model is unchanged (the outcome is open).</para>
        /// </summary>
        public static string FailureText(CommitFailureLog log, TransactionStatus status)
        {
            return status == TransactionStatus.RolledBack
                ? CommitFailureReport.RejectionText(log, status.ToString())
                : CommitFailureReport.UnfinishedText(log, status.ToString());
        }

        /// <summary>
        /// <para>提交。Commit 返回的状态不是 Committed 时抛 <see cref="CommitNotCompletedException"/>，
        /// 消息见 <see cref="FailureText"/>。RolledBack：事务已结束、模型没有改变；其他状态：结果未定。
        /// 两种情况下事务都不再是 Started，调用方的 catch 只能回滚仍是 Started 的事务。</para>
        /// <para>Commit. When Commit does not return Committed it throws
        /// <see cref="CommitNotCompletedException"/> with the message of <see cref="FailureText"/>.
        /// RolledBack: the transaction has ended and the model is unchanged; any other status: the
        /// outcome is open. Either way the transaction is no longer Started, so the caller's catch
        /// must roll back only a transaction whose GetStatus() is still Started.</para>
        /// </summary>
        public void CommitOrThrow(Transaction transaction)
        {
            TransactionStatus status = transaction.Commit();
            if (status != TransactionStatus.Committed)
                throw new CommitNotCompletedException(FailureText(Log, status), status == TransactionStatus.RolledBack);
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            // CommitFailureLog.Process 不抛异常：读失败出错时它记一条错误并选回滚。
            // CommitFailureLog.Process does not throw: when reading the failures fails it records
            // an error and chooses the rollback.
            return Log.Process(new Round(failuresAccessor)) == CommitFailureDecision.Continue
                ? FailureProcessingResult.Continue
                : FailureProcessingResult.ProceedWithRollBack;
        }

        private static CommitFailureLevel LevelOf(FailureSeverity severity)
        {
            if (severity == FailureSeverity.None)
                return CommitFailureLevel.None;
            if (severity == FailureSeverity.Warning)
                return CommitFailureLevel.Warning;
            // Error 与 DocumentCorruption。
            // Error and DocumentCorruption.
            return CommitFailureLevel.Error;
        }

        private sealed class Round : ICommitFailureRound
        {
            private readonly FailuresAccessor _accessor;

            public Round(FailuresAccessor accessor)
            {
                _accessor = accessor;
            }

            public IList<ICommitPostedFailure> GetFailures()
            {
                IList<FailureMessageAccessor> messages = _accessor.GetFailureMessages();
                var failures = new List<ICommitPostedFailure>(messages.Count);
                foreach (FailureMessageAccessor message in messages)
                    failures.Add(new Posted(message));
                return failures;
            }

            public void DeleteWarning(ICommitPostedFailure failure)
            {
                _accessor.DeleteWarning(((Posted)failure).Message);
            }

            public void SetClearAfterRollback()
            {
                FailureHandlingOptions options = _accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                _accessor.SetFailureHandlingOptions(options);
            }
        }

        private sealed class Posted : ICommitPostedFailure
        {
            public Posted(FailureMessageAccessor message)
            {
                Message = message;
            }

            public FailureMessageAccessor Message { get; }

            public CommitFailureLevel Level => LevelOf(Message.GetSeverity());

            public string Description => Message.GetDescriptionText();

            public IEnumerable<long> ElementIds
            {
                get
                {
                    foreach (long id in Values(Message.GetFailingElementIds()))
                        yield return id;
                    foreach (long id in Values(Message.GetAdditionalElementIds()))
                        yield return id;
                }
            }

            // GetValue 在 2024 前后分别读 IntegerValue 与 Value（ElementIdExtensions）。
            // GetValue reads IntegerValue before 2024 and Value from 2024 on (ElementIdExtensions).
            private static IEnumerable<long> Values(ICollection<ElementId> ids)
            {
                if (ids == null)
                    yield break;
                foreach (ElementId id in ids)
                {
                    if (id is null || id == ElementId.InvalidElementId)
                        continue;
                    yield return id.GetValue();
                }
            }
        }
    }

    /// <summary>
    /// <para>Commit 没有返回 Committed（CommitFailureGuard.CommitOrThrow）。RolledBack 为 true：Revit 回滚了事务，
    /// 模型没有改变；为 false：其他状态，结果未定，模型可能已改变。</para>
    /// <para>Commit did not return Committed (CommitFailureGuard.CommitOrThrow). RolledBack true:
    /// Revit rolled the transaction back and the model is unchanged; false: another status, the
    /// outcome is open and the model may have changed.</para>
    /// </summary>
    internal sealed class CommitNotCompletedException : System.InvalidOperationException
    {
        public CommitNotCompletedException(string message, bool rolledBack)
            : base(message)
        {
            RolledBack = rolledBack;
        }

        /// <summary>Revit rolled the transaction back; the model is unchanged.</summary>
        public bool RolledBack { get; }
    }
}
