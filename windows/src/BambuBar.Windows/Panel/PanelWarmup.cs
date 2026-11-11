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
public static class PanelWarmup
{
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
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!Renderer3D.IsOff) _ = Renderer3D.Effects;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
