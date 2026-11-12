using System.Threading.Channels;

namespace BambuBar.Core;

/// <summary>
/// Hands events to their handler in order, on the <see cref="SynchronizationContext"/> the owner was created on (the
/// UI thread in the app) or, without one (tests, the console probe), on one background reader at a time.
/// </summary>
public sealed class EventPump<T>
{
    private readonly SynchronizationContext? _context;
    private readonly Channel<T>? _queue;
    private readonly Action<T> _handler;

    public EventPump(Action<T> handler)
    {
        _handler = handler;
        _context = SynchronizationContext.Current;
        if (_context is not null) return;
        _queue = Channel.CreateUnbounded<T>(new() { SingleReader = true });
        _ = Task.Run(async () =>
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) _handler(item);
        });
    }

    public void Post(T item)
    {
        if (_queue is not null) _queue.Writer.TryWrite(item);
        else _context!.Post(_ => _handler(item), null);
    }

    /// <summary>Stops delivering; items posted later are dropped.</summary>
    public void Complete() => _queue?.Writer.TryComplete();
}
