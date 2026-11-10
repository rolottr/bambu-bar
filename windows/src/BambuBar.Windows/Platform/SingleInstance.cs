using System.IO;
using System.IO.Pipes;
using System.Text;

namespace BambuBar.Platform;

/// <summary>
/// One Bambu Bar per user session. A second launch (Start menu, a widget click, a <c>bambubar://</c> link) hands its
/// command line to the running copy over a named pipe and exits; the running copy shows its window unless the
/// arguments say <c>--background</c>.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static string Name => $"BambuBar-{Environment.UserName}-{Environment.UserDomainName}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>True for the first copy; the others should call <see cref="HandOver"/> and exit.</summary>
    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var created);
        IsFirst = created;
    }

    /// <summary>Sends this launch's arguments to the running copy. False when it didn't answer in time.</summary>
    public static bool HandOver(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(string.Join('\0', args));
            pipe.Write(payload);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="onArguments"/> (on the pipe's thread) with each later launch's arguments.</summary>
    public void Listen(Action<string[]> onArguments)
    {
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new MemoryStream();
                    await server.CopyToAsync(reader, token);
                    var text = Encoding.UTF8.GetString(reader.ToArray());
                    onArguments(text.Length == 0 ? [] : text.Split('\0'));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* A launch that gave up halfway: wait for the next. */ }
            }
        }, token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
