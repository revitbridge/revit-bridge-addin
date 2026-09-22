using System;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>逐次确认的判定结果。</para>
    /// <para>Outcome of the per-run confirmation check.</para>
    /// </summary>
    public enum ConfirmationDecision
    {
        /// <summary>No confirm object in the request, or the device switch is off.</summary>
        NotRequired,
        /// <summary>The designer clicked Yes.</summary>
        Approved,
        /// <summary>The designer clicked No, closed the dialog, or did not answer in time.</summary>
        Declined
    }

    /// <summary>
    /// <para>即席代码的设备侧确认。服务器给需要确认的请求（今天只有即席代码的
    /// <c>send_code_to_revit</c>）加一个 <c>confirm</c> 参数；带它且 <c>confirmEachRun</c>
    /// 为真时，在 Revit UI 线程弹 Yes/No 对话框，默认 No。能力包、探针、读取不带
    /// <c>confirm</c>，永不弹窗。两个传输（TCP 与 WebSocket）共用本类。</para>
    /// <para>Device-side confirmation for ad-hoc code. The server marks a request that
    /// needs it (today only <c>send_code_to_revit</c> for ad-hoc code) with a
    /// <c>confirm</c> object; when it is present and <c>confirmEachRun</c> is on, a
    /// Yes/No TaskDialog is shown on the Revit UI thread, defaulting to No. Capability
    /// packs, probes and reads carry no <c>confirm</c> and never prompt. Shared by both
    /// transports (TCP and WebSocket).</para>
    /// </summary>
    public static class ConfirmationPrompt
    {
        /// <summary>JSON-RPC error returned when the device declines (phase 7 spec).</summary>
        public const int DeclinedErrorCode = -32001;
        public const string DeclinedMessage = "declined on device";

        /// <summary>The dialog shows at most this many characters of confirm.message.</summary>
        public const int MaxMessageLength = 2000;

        /// <summary>
        /// 等待设计师作答的上限。服务器对带 confirm 的请求至少等 180 秒，这里留出余量，
        /// 超时按拒绝处理（fail closed）。
        /// How long to wait for an answer. The server waits at least 180 s for a request
        /// carrying confirm, so stay below that and treat a timeout as declined.
        /// </summary>
        public const int WaitMilliseconds = 170000;

        private static readonly object PromptLock = new object();
        private static ConfirmationEventHandler _handler;
        private static ExternalEvent _externalEvent;

        /// <summary>
        /// <para>在 Revit UI 线程上创建外部事件（由两个服务的 Initialize 调用）。</para>
        /// <para>Create the external event on the Revit UI thread; called from both
        /// services' Initialize().</para>
        /// </summary>
        public static void Initialize()
        {
            if (_externalEvent != null) return;

            _handler = new ConfirmationEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
        }

        /// <summary>
        /// <para>判断一个请求是否需要确认，需要则弹窗并等待作答。</para>
        /// <para>Decide whether a request needs confirmation and, if so, prompt and wait.</para>
        /// </summary>
        /// <param name="parameters">The request's params object (may be null).</param>
        /// <param name="confirmEachRun">The device switch from settings.</param>
        public static ConfirmationDecision Decide(JObject parameters, bool confirmEachRun, ILogger logger)
        {
            JObject confirm = parameters?["confirm"] as JObject;
            if (confirm == null)
                return ConfirmationDecision.NotRequired;

            if (!confirmEachRun)
            {
                logger?.Info("请求带 confirm，但设备已关闭逐次确认，直接执行\nRequest carries confirm but confirmEachRun is off; running without a prompt.");
                return ConfirmationDecision.NotRequired;
            }

            string title = confirm.Value<string>("title");
            if (string.IsNullOrWhiteSpace(title))
                title = "revit-bridge";

            string message = FormatMessage(confirm.Value<string>("message"));

            try
            {
                return Ask(title, message, logger) ? ConfirmationDecision.Approved : ConfirmationDecision.Declined;
            }
            catch (Exception ex)
            {
                // 征求同意的过程本身出错：按拒绝处理，调用方返回 -32001，代码不执行。
                // The prompt itself failed: decline, so the caller answers -32001 and
                // the code does not run.
                logger?.Error("确认对话框异常，已拒绝本次执行: {0}\nConfirmation dialog failed; the run was declined: {0}", ex.Message);
                return ConfirmationDecision.Declined;
            }
        }

        /// <summary>
        /// <para>对话框正文：confirm.message 的前 2000 字，被截断时加一行说明。</para>
        /// <para>Dialog body: the first 2000 characters of confirm.message, with a note
        /// appended when it was cut.</para>
        /// </summary>
        public static string FormatMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "";
            if (message.Length <= MaxMessageLength)
                return message;
            return message.Substring(0, MaxMessageLength) + "\n... (truncated)";
        }

        private static bool Ask(string title, string message, ILogger logger)
        {
            if (_externalEvent == null)
            {
                // 没有 UI 线程入口就无法征求同意 —— 按拒绝处理，不静默执行。
                // Without a way onto the UI thread we cannot ask: decline rather than
                // run unconfirmed code.
                logger?.Error("确认对话框不可用（外部事件未初始化），已拒绝本次执行\nConfirmation dialog unavailable (external event not initialized); the run was declined.");
                return false;
            }

            lock (PromptLock)
            {
                _handler.SetPrompt(title, message);
                _externalEvent.Raise();

                if (!_handler.WaitForCompletion(WaitMilliseconds))
                {
                    logger?.Warning("确认对话框超时未作答，按拒绝处理\nNo answer to the confirmation dialog in time; treated as declined.");
                    return false;
                }

                bool approved = _handler.Approved;
                logger?.Info(approved
                    ? "设计师已确认本次执行 / The designer approved this run."
                    : "设计师拒绝本次执行 / The designer declined this run.");
                return approved;
            }
        }
    }

    /// <summary>
    /// <para>在 Revit UI 线程上显示确认对话框的外部事件处理器。</para>
    /// <para>External event handler that shows the confirmation dialog on the Revit UI thread.</para>
    /// </summary>
    internal class ConfirmationEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        private string _title;
        private string _message;

        public bool Approved { get; private set; }

        public void SetPrompt(string title, string message)
        {
            _title = title;
            _message = message;
            Approved = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var dialog = new TaskDialog(_title)
                {
                    MainInstruction = "Run this code in the current model?",
                    MainContent = _message,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                    TitleAutoPrefix = false
                };

                Approved = dialog.Show() == TaskDialogResult.Yes;
            }
            catch (Exception)
            {
                // 弹窗失败按拒绝处理
                // A dialog that cannot be shown counts as declined.
                Approved = false;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "revit-bridge run confirmation";
        }
    }
}
