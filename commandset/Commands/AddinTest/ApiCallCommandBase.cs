using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>在 Revit API 上下文里依次执行若干小步骤的命令基类：每一步是一个委托，经本命令自己的外部事件执行。
    /// 一次只排一步；超时未开始的一步撤回，不会稍后再执行。</para>
    /// <para>Base of commands that run several small steps in Revit API context, one after the other:
    /// each step is a delegate run through this command's own external event. One step is queued at a
    /// time; a step that has not started when its time is up is withdrawn and never runs later.</para>
    /// </summary>
    public abstract class ApiCallCommandBase : ExternalEventCommandBase
    {
        private ApiCallHandler Calls => (ApiCallHandler)Handler;

        protected ApiCallCommandBase(UIApplication uiApp)
            : base(new ApiCallHandler(), uiApp)
        {
        }

        /// <summary>Run <paramref name="work"/> in API context and return its result; its exception is rethrown.</summary>
        internal object CallApi(Func<UIApplication, object> work, int timeoutMilliseconds)
        {
            if (!Calls.TryQueue(work))
                throw new InvalidOperationException("An earlier step of this command is still running in Revit.");
            if (!RaiseAndWaitForCompletion(timeoutMilliseconds))
            {
                bool withdrawn = Calls.WithdrawIfNotStarted();
                throw new TimeoutException(withdrawn
                    ? $"Revit did not run the step within {timeoutMilliseconds / 1000} s; it was withdrawn."
                    : $"The step did not finish within {timeoutMilliseconds / 1000} s.");
            }
            if (Calls.Error != null)
                throw new InvalidOperationException(Calls.Error.Message, Calls.Error);
            return Calls.Result;
        }
    }

    internal sealed class ApiCallHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly object _sync = new object();
        private readonly ManualResetEvent _done = new ManualResetEvent(false);
        private Func<UIApplication, object> _work;
        private bool _queued;
        private bool _running;

        public object Result { get; private set; }
        public Exception Error { get; private set; }

        public bool TryQueue(Func<UIApplication, object> work)
        {
            lock (_sync)
            {
                if (_queued || _running)
                    return false;
                _work = work;
                Result = null;
                Error = null;
                _queued = true;
                _done.Reset();
                return true;
            }
        }

        public bool WithdrawIfNotStarted()
        {
            lock (_sync)
            {
                if (_queued && !_running)
                {
                    _queued = false;
                    return true;
                }
                return false;
            }
        }

        // No Reset here: TryQueue already reset it, so a step that ends before the wait starts is not lost.
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _done.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            Func<UIApplication, object> work;
            lock (_sync)
            {
                if (!_queued)
                    return;
                _queued = false;
                _running = true;
                work = _work;
            }
            object result = null;
            Exception error = null;
            try
            {
                result = work(app);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                lock (_sync)
                {
                    Result = result;
                    Error = error;
                    _running = false;
                }
                _done.Set();
            }
        }

        public string GetName()
        {
            return "revit-bridge add-in test step";
        }
    }
}
