using Autodesk.Revit.UI;
using RevitMCPCommandSet.Commands.AddinTest;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Views
{
    public abstract class ViewCommandBase : ExternalEventCommandBase
    {
        protected ViewCommandBase(UIApplication uiApp) : base(new ApiCallHandler(), uiApp) { }

        // Only the calling worker waits. A queued step can be withdrawn. Once Revit
        // starts it, its result (and any export files) belong to this caller until it ends.
        protected T RunInRevit<T>(Func<UIApplication, T> work)
        {
            var calls = (ApiCallHandler)Handler;
            if (!Monitor.TryEnter(calls))
                throw new InvalidOperationException("A view request is already running. Try again when it finishes.");
            try
            {
                if (!calls.TryQueue(app =>
                {
                    if (AddinTestSession.IsBusy)
                        throw new InvalidOperationException("An add-in test is running. Try again when it finishes.");
                    return work(app);
                }))
                    throw new InvalidOperationException("Revit is still handling the previous view request.");

                bool completed;
                try
                {
                    completed = RaiseAndWaitForCompletion(30000);
                }
                catch
                {
                    if (!calls.WithdrawIfNotStarted())
                        calls.WaitForCompletion(Timeout.Infinite);
                    throw;
                }
                if (!completed)
                {
                    if (calls.WithdrawIfNotStarted())
                        throw new TimeoutException("Revit did not start the view request within 30 seconds.");
                    calls.WaitForCompletion(Timeout.Infinite);
                }
                if (calls.Error != null)
                    throw new InvalidOperationException(calls.Error.Message, calls.Error);
                return (T)calls.Result;
            }
            finally
            {
                Monitor.Exit(calls);
            }
        }
    }
}
