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
        /// <para>装到事务上（开始之后、提交或回滚之前任何时候都可以）：本预处理器、回滚后清除失败。</para>
        /// <para>Install on a transaction (any time after it started and before it is committed or
        /// rolled back): this preprocessor, clear failures after a rollback.</para>
        /// </summary>
        public void InstallOn(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(this);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
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
}
