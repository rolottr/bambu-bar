using System.Collections.Concurrent;

namespace BambuBar.Core;

/// <summary>
/// A <see cref="SynchronizationContext"/> with one thread of its own, for code written for the UI thread that runs
/// without one (tests, the console probe): everything posted to it runs in order, one at a time.
/// </summary>
public sealed class SerialContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SerialContext(string name = "BambuBar.Serial")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Post(s => { try { d(s); } finally { done.Set(); } }, state);
        done.Wait();
    }

    /// <summary>Runs <paramref name="func"/> on the context's thread and returns its result.</summary>
    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        Send(_ => result = func(), null);
        return result;
    }

    public override SynchronizationContext CreateCopy() => this;

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
    }

    public void Dispose() => _queue.CompleteAdding();
}
