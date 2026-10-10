using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace QARevit.Addin.Revit;

/// <summary>Menjalankan kode Revit API dari window modeless lewat ExternalEvent, hasilnya sebagai Task.</summary>
public sealed class RevitTaskRunner : IExternalEventHandler
{
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();
    private readonly ExternalEvent _event;

    public RevitTaskRunner() => _event = ExternalEvent.Create(this);

    public Task<T> Run<T>(Func<UIApplication, T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(app =>
        {
            try { tcs.SetResult(func(app)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _event.Raise();
        return tcs.Task;
    }

    public Task Run(Action<UIApplication> action) => Run<bool>(app => { action(app); return true; });

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var work)) work(app);
    }

    public string GetName() => "QA Revit";
}
