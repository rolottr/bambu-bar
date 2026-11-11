using System.Diagnostics;
using BambuBar.Core.App;

namespace BambuBar.Widgets;

/// <summary>
/// Writes the widgets' state files on a worker thread instead of the UI thread. A write renders and PNG-encodes the
/// printer art when it changed, serializes JSON and, to reload the widgets, waits for Rainmeter's window: measured on a
/// Windows 11 VM, 300 ms of a frozen UI at launch (and each time a printer connects) and again whenever the art changes
/// during a print. Writes never overlap and only the newest is kept: a snapshot that arrives while one is being written
/// replaces any still waiting (asking for a reload if either did).
/// </summary>
public sealed class QueuedWidgetSink(IWidgetSink inner) : IWidgetSink
{
    private readonly object _gate = new();
    private (FleetSnapshot Fleet, bool Reload)? _next;
    private bool _running;

    public void Write(FleetSnapshot fleet, bool reload)
    {
        lock (_gate)
        {
            _next = (fleet, reload || (_next?.Reload ?? false));
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            (FleetSnapshot Fleet, bool Reload) job;
            lock (_gate)
            {
                if (_next is not { } next)
                {
                    _running = false;
                    return;
                }
                job = next;
                _next = null;
            }
            try
            {
                inner.Write(job.Fleet, job.Reload);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A locked file or a full disk: the next snapshot tries again.
                Debug.WriteLine($"[widgets] write failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
