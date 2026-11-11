using System.IO;
using System.Windows.Threading;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using BambuBar.Model3D;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>
/// Gets the tray panel ready before the first click, so it opens at once. Opening it cold cost seconds of a frozen UI
/// (measured on a Windows 11 VM: 1–2 s warm, over 20 s right after installing, when every file still comes off the
/// disk): the printer drawing's images decoded, the 3D model parsed, the Direct3D device created, the panel's views built
/// and their code compiled, all at the moment of the click. Here the file work runs on a worker thread a few seconds
/// after launch, then the device is made and the panel built and drawn once out of sight (PanelWindow.PrimeAsync),
/// while the app sits in the tray, at idle priority so anything the person does comes first.
/// </summary>
public static partial class PanelWarmup
{
    /// <summary>Waits (up to a minute) for 1.5 s without keyboard or mouse input, then for the dispatcher to be idle.</summary>
    private static async Task UserIdle()
    {
        for (var waited = 0; waited < 60000 && InputIdleMs() < 1500; waited += 300) await Task.Delay(300);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static uint InputIdleMs()
    {
        var info = new LastInput { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : uint.MaxValue;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInput info);

    public static async Task RunAsync(PrinterStore store, Func<Task> preparePanel, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            var shapes = store.Sessions.Select(s => s.Config.ModelName).Append(null).Select(PrinterShapes.FromModel).Distinct().ToList();
            await Task.Run(() =>
            {
                foreach (var shape in shapes)
                {
                    // The drawing's images (decoded once, then cached) and the 3D model (read once).
                    PrinterArt.Render(new PrinterArt.Scene { Shape = shape, State = JobState.Idle, Spools = [] },
                        (int)(Metrics.ArtWidth * 2), (int)(Metrics.ArtHeight * 2));
                    if (!Renderer3D.IsOff) PrinterScene.Model(shape);
                }
            });
            // The steps below each hold the UI thread for a moment (0.2–0.6 s on a machine without a graphics card): only
            // while the person isn't typing or moving the mouse, so it never lands in the middle of something they do.
            await UserIdle();
            if (!Renderer3D.IsOff) _ = Renderer3D.Effects;
            await UserIdle();
            await preparePanel();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // Only a head start: the panel still builds itself on the first click.
            Renderer3D.Write($"Warm-up stopped: {e.GetType().Name}: {e.Message}");
        }
    }
}
