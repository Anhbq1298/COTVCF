using System.Windows.Threading;
namespace COT_VCF.Infrastructure;

public sealed class StaWorker : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Thread thread;
    public StaWorker()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "SAP2000 API" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); dispatcher = ready.Task.GetAwaiter().GetResult();
    }
    public Task<T> Run<T>(Func<T> work) => dispatcher.InvokeAsync(work).Task;
    public void Dispose() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
}
