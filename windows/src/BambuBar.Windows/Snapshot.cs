using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BambuBar.AppWindow;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Theme;
using BambuBar.Tray;
using BambuBar.TrayPanel;

namespace BambuBar;

/// <summary>
/// <c>BambuBar.exe --snapshot &lt;dir&gt;</c> (P3.13): renders the app's pieces offscreen to PNGs, for CI artifacts
/// and review without a screen: the tray glyphs, the panel in every demo state, light and dark
/// (<c>panel-&lt;scene&gt;-&lt;light|dark&gt;.png</c> at 2×), and every page of the window over the demo fleet
/// (<c>window-&lt;page&gt;-&lt;light|dark&gt;.png</c>, 1100 × 720 at 1×). Problems are written to <c>panel-errors.txt</c>
/// and <c>window-errors.txt</c> rather than failing the run, so the images that did render still reach the CI artifact.
/// </summary>
public static class Snapshot
{
    public static string? Folder(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--snapshot");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        Save(TraySheet(), Path.Combine(folder, "tray-glyphs.png"));
        var errors = RenderPanels(folder);
        errors.AddRange(ThemeResources.Errors);
        Report(Path.Combine(folder, "panel-errors.txt"), errors);
        Report(Path.Combine(folder, "window-errors.txt"), RenderWindows(folder));
        return 0;
    }

    private static void Report(string path, List<string> errors)
    {
        if (errors.Count > 0) File.WriteAllLines(path, errors);
        else if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>A page of the window to render: its file name, the demo's command line, and the page.</summary>
    private sealed record WindowScene(string Name, string[] Args, AppPage Page, bool AddPrinter = false);

    private static readonly WindowScene[] WindowScenes =
    [
        .. AppPages.Order.Select(page => new WindowScene(page.ToString().ToLowerInvariant(), ["--demo", "printing", "--fleet"], page)),
        // One printer: the sidebar's footer instead of the picker, and no All Printers.
        new("printer-single", ["--demo", "printing"], AppPage.Printer),
        new("printer-offline", ["--demo", "offline"], AppPage.Printer),
        new("setup", ["--demo", "unconfigured"], AppPage.Printer),
        new("add-printer", ["--demo", "printing", "--fleet"], AppPage.Printer, AddPrinter: true),
    ];

    /// <summary>Every page of the main window, light and dark, as the window shows it at 1100 × 720.</summary>
    private static List<string> RenderWindows(string folder)
    {
        var errors = new List<string>();
        foreach (var scene in WindowScenes)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var name = $"window-{scene.Name}-{(variant == ThemeVariant.Dark ? "dark" : "light")}.png";
                try
                {
                    Save(RenderWindow(scene, variant), Path.Combine(folder, name));
                }
                catch (Exception e)
                {
                    errors.Add($"{name}: {e}");
                }
            }
        }
        return errors;
    }

    private static BitmapSource RenderWindow(WindowScene scene, ThemeVariant variant)
    {
        var settings = new AppSettings(null);
        using var store = new PrinterStore(new PrinterStore.Services
        {
            Settings = settings, History = new HistoryStore(null), Secrets = new MemorySecretStore(), Arguments = scene.Args,
        });
        store.Start();
        Pump(TimeSpan.FromSeconds(2), () => store.Sessions.All(s => s.Status is not null || s.Phase is Core.Protocol.PrinterConnection.Phase.Offline));
        var view = new WindowView(store, settings, new WindowServices { Offscreen = true }, variant, animate: false);
        try
        {
            if (scene.AddPrinter) view.AddPrinter(null);
            else view.Show(scene.Page);
            Pump(TimeSpan.FromMilliseconds(200), () => false);
            view.Refresh();
            const double width = 1100, height = 720;
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            // A second pass: pages that lay out by their width (the Tray page) settle after the first.
            Pump(TimeSpan.FromMilliseconds(100), () => false);
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            view.Dispose();
            store.Stop();
        }
    }

    /// <summary>A panel to render: the demo's command line, and what to set before drawing.</summary>
    private sealed record PanelScene(string Name, string[] Args, bool? ShowsAll = null, Action<PrinterStore, AppSettings, PanelView>? Setup = null);

    private static readonly PanelScene[] Scenes =
    [
        new("printing", ["--demo", "printing"]),
        new("preparing", ["--demo", "preparing"]),
        new("paused", ["--demo", "paused"]),
        new("finished", ["--demo", "finished"]),
        new("failed", ["--demo", "failed"]),
        new("idle", ["--demo", "idle"]),
        new("offline", ["--demo", "offline"]),
        new("unconfigured", ["--demo", "unconfigured"]),
        new("fleet", ["--demo", "printing", "--fleet"], ShowsAll: false),
        new("fleet-all", ["--demo", "printing", "--fleet"], ShowsAll: true),
        // What no demo state shows on its own: fans (opt-in), locked controls with their toast, the update row.
        new("extras", ["--demo", "printing"], Setup: (store, settings, view) =>
        {
            settings.Set(AppSettings.Keys.PanelFans, true);
            if (store.Current is { Status: { } status } session)
            {
                var locked = status.Clone();
                locked.ControlsLocked = true;
                session.ShowStatus(locked);
            }
            store.Pause();
            view.UpdateOffer = new UpdateOffer("3.0.0", () => { }, () => { });
        }),
    ];

    /// <summary>Runs Core's demo printers on this (STA) thread and draws the panel for each scene, light and dark.</summary>
    private static List<string> RenderPanels(string folder)
    {
        var errors = new List<string>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var strings = Strings.Load();
        strings.Use("en");
        foreach (var scene in Scenes)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var name = $"panel-{scene.Name}-{(variant == ThemeVariant.Dark ? "dark" : "light")}.png";
                try
                {
                    Save(RenderPanel(scene, variant), Path.Combine(folder, name));
                }
                catch (Exception e)
                {
                    errors.Add($"{name}: {e}");
                }
            }
        }
        return errors;
    }

    private static BitmapSource RenderPanel(PanelScene scene, ThemeVariant variant)
    {
        var settings = new AppSettings(null);
        using var store = new PrinterStore(new PrinterStore.Services
        {
            Settings = settings, History = new HistoryStore(null), Secrets = new MemorySecretStore(), Arguments = scene.Args,
        });
        store.Start();
        Pump(TimeSpan.FromSeconds(2), () => store.Current is null || store.Current.Status is not null
            || store.Current.Phase is Core.Protocol.PrinterConnection.Phase.Offline);
        var view = new PanelView(store, settings, variant, animate: false) { ShowsAllOverride = scene.ShowsAll };
        try
        {
            view.Opening();
            scene.Setup?.Invoke(store, settings, view);
            view.IsLive = true;
            Pump(TimeSpan.FromMilliseconds(150), () => false);
            view.Refresh();
            view.Measure(new Size(Metrics.PanelWidth, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();
            const double scale = 2;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(view);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            view.Dispose();
            store.Stop();
        }
    }

    /// <summary>Runs the dispatcher (the demo's posted reports, layout) until <paramref name="done"/> or the time is up.</summary>
    private static void Pump(TimeSpan max, Func<bool> done)
    {
        var until = DateTime.UtcNow + max;
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.ContextIdle);
            Dispatcher.PushFrame(frame);
            if (done()) return;
            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < until);
    }

    /// <summary>Every look × 16/20/24/32 px on a dark and a light taskbar, magnified 4× without smoothing.</summary>
    private static BitmapSource TraySheet()
    {
        (string Name, TrayGlyph.Look Look)[] looks =
        [
            ("not set up", new(null, 0, false, false)),
            ("idle", new(JobState.Idle, 0, false, false)),
            ("preparing", new(JobState.Preparing, 0, false, false)),
            ("printing 5%", new(JobState.Printing, 0.05, false, false)),
            ("printing 50%", new(JobState.Printing, 0.5, false, false)),
            ("printing 95%", new(JobState.Printing, 0.95, false, false)),
            ("paused", new(JobState.Paused, 0.6, false, false)),
            ("paused + alert", new(JobState.Paused, 0.6, true, false)),
            ("printing + alert", new(JobState.Printing, 0.4, true, false)),
            ("finished", new(JobState.Finished, 1, false, false)),
            ("failed", new(JobState.Failed, 0.3, true, false)),
            ("offline", new(null, 0, false, true)),
            ("show % 64", new(JobState.Printing, 0.64, false, false, "64")),
            ("show % 100", new(JobState.Printing, 1, false, false, "100")),
        ];
        int[] sizes = [16, 20, 24, 32];
        const int zoom = 4, label = 150, cell = 32 * zoom + 12;
        var width = label + sizes.Length * 2 * cell;
        var height = 40 + looks.Length * cell;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var face = new Typeface("Segoe UI");
            void Text(string s, double x, double y) => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.Black, 1.0), new Point(x, y));
            for (var t = 0; t < 2; t++)
            {
                var light = t == 1;
                for (var z = 0; z < sizes.Length; z++)
                    Text($"{sizes[z]} px {(light ? "light" : "dark")}", label + (t * sizes.Length + z) * cell + 4, 12);
            }
            for (var r = 0; r < looks.Length; r++)
            {
                var y = 40 + r * cell;
                Text(looks[r].Name, 8, y + cell / 2.0 - 9);
                for (var t = 0; t < 2; t++)
                {
                    var light = t == 1;
                    for (var z = 0; z < sizes.Length; z++)
                    {
                        var x = label + (t * sizes.Length + z) * cell;
                        dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20)), null,
                            new Rect(x, y, cell - 6, cell - 6));
                        var glyph = TrayGlyph.Render(looks[r].Look, sizes[z], light);
                        var image = new ImageDrawing(glyph, new Rect(x + 4, y + 4, sizes[z] * zoom, sizes[z] * zoom));
                        var group = new DrawingGroup();
                        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                        group.Children.Add(image);
                        dc.DrawDrawing(group);
                    }
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
