using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BambuBar.AppWindow;
using BambuBar.CameraViews;
using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Core.Widgets;
using BambuBar.Theme;
using BambuBar.Tray;
using BambuBar.TrayPanel;
using BambuBar.Widgets;

namespace BambuBar;

/// <summary>
/// <c>BambuBar.exe --snapshot &lt;dir&gt;</c> (P3.13): renders the app's pieces offscreen to PNGs, for CI artifacts
/// and review without a screen: the tray glyphs, the panel in every demo state, light and dark
/// (<c>panel-&lt;scene&gt;-&lt;light|dark&gt;.png</c> at 2×), and every page of the window over the demo fleet
/// (<c>window-&lt;page&gt;-&lt;light|dark&gt;.png</c>, 1100 × 720 at 1×), and the widgets as the Widgets page draws them, every
/// state in every size (<c>widgets-cards.png</c>), and the camera over a picture FFmpeg decoded (<c>camera.png</c>). Problems are written to <c>panel-errors.txt</c>
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
        var cameraErrors = new List<string>();
        try
        {
            cameraErrors.AddRange(RenderCamera(folder));
        }
        catch (Exception e)
        {
            cameraErrors.Add($"camera.png: {e}");
        }
        Report(Path.Combine(folder, "camera-errors.txt"), cameraErrors);
        var windowErrors = RenderWindows(folder);
        try
        {
            Save(WidgetSheet(), Path.Combine(folder, "widgets-cards.png"));
        }
        catch (Exception e)
        {
            windowErrors.Add($"widgets-cards.png: {e}");
        }
        Report(Path.Combine(folder, "window-errors.txt"), windowErrors);
        return 0;
    }

    private static void Report(string path, List<string> errors)
    {
        if (errors.Count > 0) File.WriteAllLines(path, errors);
        else if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>A page of the window to render: its file name, the demo's command line, and the page. <c>Slicer</c>
    /// imports a Bambu Studio settings file with three printers first (setup's rows and Add All); <c>Candidate</c> opens
    /// setup on one of them; <c>Widgets</c> gives the page a widget host in a temporary folder with two placed widgets.</summary>
    private sealed record WindowScene(string Name, string[] Args, AppPage Page, bool AddPrinter = false, bool Slicer = false,
        string? Candidate = null, bool Widgets = false);

    /// <summary>The printers in the settings file the setup scenes import (a P2S, an X1C and an A1).</summary>
    private static readonly (string Serial, string Host, string Code)[] SlicerPrinters =
    [
        ("22E00A000000123", "192.168.1.60", "12345678"),
        ("00M00A000000456", "192.168.1.61", "23456789"),
        ("03900A000000789", "192.168.1.62", "34567890"),
    ];

    private static readonly WindowScene[] WindowScenes =
    [
        .. AppPages.Order.Select(page => new WindowScene(page.ToString().ToLowerInvariant(), ["--demo", "printing", "--fleet"], page,
            Widgets: page == AppPage.Widgets)),
        // One printer: the sidebar's footer instead of the picker, and no All Printers.
        new("printer-single", ["--demo", "printing"], AppPage.Printer),
        new("printer-offline", ["--demo", "offline"], AppPage.Printer),
        new("setup", ["--demo", "unconfigured"], AppPage.Printer),
        new("setup-slicer", ["--demo", "unconfigured"], AppPage.Printer, Slicer: true),
        new("setup-picked", ["--demo", "unconfigured"], AppPage.Printer, Slicer: true, Candidate: SlicerPrinters[0].Serial),
        new("add-printer", ["--demo", "printing", "--fleet"], AppPage.Printer, AddPrinter: true),
        new("widgets-single", ["--demo", "paused"], AppPage.Widgets, Widgets: true),
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
        var temp = Path.Combine(Path.GetTempPath(), "BambuBarSnapshot-" + Guid.NewGuid().ToString("N"));
        WidgetHost? widgets = null;
        if (scene.Slicer)
        {
            Directory.CreateDirectory(temp);
            var conf = Path.Combine(temp, "BambuStudio.conf");
            File.WriteAllText(conf, "{\"access_code\": {" + string.Join(", ", SlicerPrinters.Select(p => $"\"{p.Serial}\": \"{p.Code}\"")) +
                "}, \"ip_address\": {" + string.Join(", ", SlicerPrinters.Select(p => $"\"{p.Serial}\": \"{p.Host}\"")) + "}}");
            store.ImportSlicerSettings(conf);
        }
        if (scene.Widgets)
        {
            Directory.CreateDirectory(temp);
            widgets = new WidgetHost(temp, temp);
            widgets.Add(WidgetHost.Size.Medium, store.Sessions.FirstOrDefault()?.Id, 1500, 16);
            widgets.Add(WidgetHost.Size.Small, null, 1500, 240);
        }
        var view = new WindowView(store, settings, new WindowServices { Offscreen = true, Widgets = widgets }, variant, animate: false);
        try
        {
            if (scene.Candidate is { } serial && SlicerPrinters.FirstOrDefault(p => p.Serial == serial) is { Serial.Length: > 0 } picked)
                view.AddPrinter(new DiscoveredPrinter { Serial = picked.Serial, Host = picked.Host, Name = picked.Serial, ModelCode = "" });
            else if (scene.AddPrinter) view.AddPrinter(null);
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
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* Left in %TEMP%. */ }
        }
    }

    /// <summary>The widgets as the Widgets page draws them: every showcase scene and every link state, in each size,
    /// on the showcase's dark backdrop.</summary>
    private static BitmapSource WidgetSheet()
    {
        var now = DateTimeOffset.Now;
        var printer = ShowcasePrinter.For(null);
        var rows = new List<(string Name, WidgetState State, Core.Imaging.PrinterArt.Scene Art)>();
        foreach (var scene in Enum.GetValues<ShowcaseScene>())
        {
            var (state, art) = ShowcaseScenes.Build(scene, Showcase.SceneSeconds / 2, now, printer);
            rows.Add((scene.ToString(), state, art));
        }
        var lastJob = new JobRecord
        {
            Name = "Articulated Dragon", StartedAt = now.AddHours(-5), EndedAt = now.AddHours(-2), Outcome = JobOutcome.Finished, TotalLayers = 284,
        };
        foreach (var link in new[] { PrinterSnapshot.LinkState.Offline, PrinterSnapshot.LinkState.Connecting, PrinterSnapshot.LinkState.Unauthorized, PrinterSnapshot.LinkState.Unconfigured })
        {
            var snapshot = new PrinterSnapshot
            {
                Serial = link == PrinterSnapshot.LinkState.Unconfigured ? "" : "showcase", PrinterName = printer.Name, Model = printer.Model,
                Link = link, LastJob = link == PrinterSnapshot.LinkState.Offline ? lastJob : null, SavedAt = now,
            };
            rows.Add((link.ToString(), WidgetStateWriter.Build(snapshot, now), ArtImage.Scene(null, printer.Model, lightOn: false, dimmed: true)));
        }

        var sheet = new System.Windows.Controls.StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x22, 0x40)) };
        foreach (var (name, state, art) in rows)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(16, 16, 16, 0) };
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = name, Width = 110, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var size in Enum.GetValues<WidgetHost.Size>())
            {
                var card = new WidgetCard(size) { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
                card.Show(state, art, now);
                row.Children.Add(card);
            }
            sheet.Children.Add(row);
        }
        sheet.Children.Add(new System.Windows.Controls.Border { Height = 16 });
        sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        sheet.Arrange(new Rect(sheet.DesiredSize));
        sheet.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth), (int)Math.Ceiling(sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        bitmap.Freeze();
        return bitmap;
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

    /// <summary>A camera that sends what it's given (a JPEG still, or a failure) and nothing else.</summary>
    private sealed class StillCamera(byte[]? jpeg, (int Width, int Height) size, CameraFailure? failure) : ICameraClient
    {
        public Action<CameraEvent>? OnEvent { get; set; }
        public Action<string>? Log { get; set; }

        public void Start()
        {
            var context = SynchronizationContext.Current;
            void Send()
            {
                if (jpeg is not null)
                {
                    OnEvent?.Invoke(new CameraEvent.Started($"JPEG · {size.Width}×{size.Height}", size.Width, size.Height));
                    OnEvent?.Invoke(new CameraEvent.Image(jpeg, size.Width, size.Height));
                }
                if (failure is not null) OnEvent?.Invoke(new CameraEvent.Failed(failure));
            }
            if (context is null) Send(); else context.Post(_ => Send(), null);
        }

        public void Stop() { }
    }

    /// <summary>
    /// The camera as the app draws it, over a picture that went through the real decoder: a JPEG of the printer art
    /// "streamed" by a stand-in camera, decoded by FFmpeg from the app's <c>ffmpeg\</c> folder (so CI proves the bundled
    /// libraries load and decode), shown in the camera window's view with its bar, and in the panel's size in each state:
    /// live, connecting, unavailable, failed. <c>camera.png</c>; problems (FFmpeg missing, nothing decoded) go to
    /// <c>camera-errors.txt</c>.
    /// </summary>
    private static List<string> RenderCamera(string folder)
    {
        var errors = new List<string>();
        var ffmpeg = FFmpeg.Status;
        if (!ffmpeg.IsAvailable) errors.Add($"FFmpeg: {ffmpeg.Problem}");
        const int width = 1280, height = 720;
        var jpeg = PrinterJpeg(width, height);
        CameraFeed Feed(CameraSituation situation, ICameraClient client) =>
            new(() => situation, new CameraServices { Client = _ => client });
        var online = new CameraSituation { ShowsCamera = true, IsOnline = true, Transport = CameraTransport.Jpeg, AccessCode = "12345678", Host = "192.0.2.1", Serial = "01P00A000000001" };
        var live = Feed(online, new StillCamera(jpeg, (width, height), null));
        var feeds = new (string Name, CameraFeed Feed)[]
        {
            ("live", live),
            ("connecting", Feed(online, new StillCamera(null, default, null))),
            ("unavailable", Feed(online with { IsOnline = false }, new StillCamera(null, default, null))),
            ("failed", Feed(online, new StillCamera(null, default, new CameraFailure.Unauthorized()))),
        };
        CameraPlayer.ForceOnScreen = true;
        try
        {
            var window = new CameraWindowView(live, "P1S", () => "Printing · 64% · 1h 20m left", null, () => true, _ => { }) { Width = 800, Height = 450 };
            window.ShowBar(true);
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(16) };
            foreach (var (_, feed) in feeds)
            {
                var player = new CameraPlayer(compact: true, cornerRadius: Metrics.Tile.TopLeft) { Width = 348, Height = 196, Margin = new Thickness(0, 0, 16, 0) };
                player.Show(feed, "P1S");
                row.Children.Add(player);
            }
            Pump(TimeSpan.FromSeconds(5), () => live.Frame is not null && feeds.All(f => f.Feed.State is not CameraFeed.FeedState.Idle));
            if (live.Frame is null) errors.Add($"camera.png: no picture was decoded ({live.State})");
            else if (live.Frame.Width != width || live.Frame.Height != height) errors.Add($"camera.png: decoded {live.Frame.Width}×{live.Frame.Height}, sent {width}×{height}");
            var sheet = new System.Windows.Controls.StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) };
            sheet.Children.Add(new System.Windows.Controls.Border { Child = window, Margin = new Thickness(16, 16, 16, 0), HorizontalAlignment = HorizontalAlignment.Left });
            sheet.Children.Add(row);
            sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sheet.Arrange(new Rect(sheet.DesiredSize));
            sheet.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(100), () => false);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth), (int)Math.Ceiling(sheet.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sheet);
            bitmap.Freeze();
            Save(bitmap, Path.Combine(folder, "camera.png"));
        }
        finally
        {
            CameraPlayer.ForceOnScreen = false;
            foreach (var (_, feed) in feeds) feed.Stop();
        }
        return errors;
    }

    /// <summary>The printer art on a dark backdrop, as a JPEG: something recognisable for the camera to show.</summary>
    private static byte[] PrinterJpeg(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x2D, 0x34), Color.FromRgb(0x0E, 0x0F, 0x12), 90), null, new Rect(0, 0, width, height));
            if (ArtImage.For(ArtImage.Scene(null, "P1S", lightOn: true, dimmed: false), height * 0.75, height * 0.9) is { } art)
                dc.DrawImage(art, new Rect((width - height * 0.75) / 2, height * 0.05, height * 0.75, height * 0.9));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
