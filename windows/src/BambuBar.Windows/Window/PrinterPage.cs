using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>
/// The window's main page (the Mac's OverviewPage): the printer's name (renamable) with its model, address and
/// firmware, how fresh the data is; then the job, the print controls, the issues, the camera, temperatures and
/// fans, the filament, and what the printer has and is. Offline, the connection card instead. The job card, tiles,
/// filament strip, controls and connection card are the panel's own sections, drawn larger where the Mac does.
/// </summary>
internal sealed class PrinterPage(WindowContext ctx) : Page(ctx)
{
    /// <summary>The Mac's window hero is about 1.5× the panel's (art 190 vs 92 pt, the percentage 56 vs 34).</summary>
    private const double HeroScale = 1.45;

    private NameField _name = null!;
    private TextBlock _subtitle = null!;
    private RefreshControl _refresh = null!;
    private Border _hero = null!;
    private JobSection _job = null!;
    private ControlsSection _controls = null!;
    private Border _controlsHost = null!;
    private FrameworkElement _lockedNote = null!;
    private readonly Region _issues = new();
    private readonly Region _camera = new();
    private TextBlock _metricsLabel = null!;
    private FrameworkElement _metrics = null!;
    private TemperatureSection _temperatures = null!;
    private readonly Region _fans = new();
    private FrameworkElement _filamentBlock = null!;
    private FilamentSection _filament = null!;
    private readonly Region _machine = new();
    private ConnectionSection _connection = null!;
    private PanelContext _panel = null!;

    public override FrameworkElement Build()
    {
        // The page's tiles always show the chamber, whatever the panel's switch says (the Mac's TemperatureTiles here
        // ignore it), so the sections get settings of their own with the defaults.
        var p = Ctx.Panel;
        _panel = new PanelContext
        {
            Store = p.Store, Settings = new AppSettings(null), Theme = p.Theme, Dark = p.Dark, Animate = p.Animate,
            Navigate = p.Navigate, Quit = p.Quit, ShowsAll = p.ShowsAll, SetShowsAll = p.SetShowsAll,
        };
        _name = new NameField(Ctx);
        _subtitle = W.Subtitle("");
        _refresh = new RefreshControl(Ctx);
        var header = W.PageHeader(_name, _subtitle, _refresh);

        _job = new JobSection(_panel);
        _hero = new Border { Child = _job.Root, LayoutTransform = new ScaleTransform(HeroScale, HeroScale) };

        _controls = new ControlsSection(_panel);
        _controlsHost = new Border { Child = _controls.Root, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        _controlsHost.Width = 520;
        var lockIcon = Icons.Glyph(Icons.Lock, 12);
        lockIcon.VerticalAlignment = VerticalAlignment.Top;
        lockIcon.Margin = new Thickness(0, 2, 8, 0);
        var note = W.Note(Loc.T("The printer only accepts pause, stop and speed changes from apps in **LAN Only** mode with **Developer Mode** on (printer screen → Settings → Network). Status and the chamber light work either way."));
        var locked = Ui.Columns("Auto", "*");
        locked.MaxWidth = 620;
        locked.HorizontalAlignment = HorizontalAlignment.Left;
        locked.Children.Add(Ui.At(lockIcon, 0));
        locked.Children.Add(Ui.At(note, 1));
        _lockedNote = locked;

        _temperatures = new TemperatureSection(_panel);
        var metricsRow = Ui.Columns("*", "10", "Auto");
        metricsRow.Children.Add(Ui.At(_temperatures.Root, 0));
        metricsRow.Children.Add(Ui.At(_fans.Host, 2));
        _metricsLabel = Ui.Text("", Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextSecondary);
        _metrics = new Stack { Spacing = 8, Children = { _metricsLabel, metricsRow } };

        _filament = new FilamentSection(_panel);
        _filamentBlock = new Stack { Spacing = 8, Children = { Ui.SectionLabel(Loc.T("Filament"), null), _filament.Root } };

        _connection = new ConnectionSection(_panel);

        return new Stack
        {
            Spacing = 18,
            Children =
            {
                header, _hero, _controlsHost, _lockedNote, _issues.Host, _camera.Host, _metrics, _filamentBlock, _machine.Host, _connection.Root,
            },
        };
    }

    public override void Refresh()
    {
        var session = Store.Current;
        var status = session?.Status;
        var online = session is not null && status is not null && session.IsOnline;
        _name.Refresh();
        _subtitle.Text = Subtitle(session);
        _refresh.Update();

        _hero.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        _job.Update(online);
        var controls = online && Store.ShowPrinterActions && status!.State.IsActive();
        _controlsHost.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
        _controls.Update(controls);
        _lockedNote.Visibility = controls && status!.ControlsLocked ? Visibility.Visible : Visibility.Collapsed;

        if (online)
        {
            var issues = status!.Issues;
            _issues.Show(string.Join("#", issues.Select(i => i.Id)) + "|" + session!.Id, () =>
            {
                if (issues.Count == 0) return null;
                var list = new Stack { Spacing = 10 };
                foreach (var issue in issues) list.Children.Add(new IssueBanner(Ctx, issue, () => Store.Current?.Dismiss(issue)));
                return list;
            });
            var camera = session.ShowsCamera;
            _camera.Show($"{camera}|{Ctx.Settings.Get(WindowSettings.CameraOnPrinterPage, true)}", () => camera ? CameraCard.Build(Ctx, Refresh) : null);
            var model = session.Config.Model;
            _metricsLabel.Text = (status.Chamber is null && model?.IsEnclosed == false ? Loc.T("Temperatures & cooling") : Loc.T("Temperatures & airflow")).ToUpperInvariant();
            _fans.Show(FanTile.Key(status), () => FanTile.Build(Ctx, status));
            _machine.Show(MachineKey(session), () => Machine(session));
        }
        else
        {
            _issues.Hide();
            _camera.Hide();
            _fans.Hide();
            _machine.Hide();
        }
        _metrics.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        _temperatures.Update(online);
        var filament = online && (status!.Ams.Count > 0 || status.ExternalSpool is { IsLoaded: true });
        _filamentBlock.Visibility = filament ? Visibility.Visible : Visibility.Collapsed;
        _filament.Update(filament);
        _connection.Update(session is not null && !online);
    }

    /// <summary>"Bambu Lab P2S · 192.168.1.40 · Firmware 01.03.00.00".</summary>
    private static string Subtitle(PrinterSession? session)
    {
        if (session is null) return "";
        var parts = new List<string> { Loc.F("Bambu Lab %@", session.Config.ModelName), session.Config.Host };
        if (session.Status?.Firmware is { } fw) parts.Add(Loc.F("Firmware %@", fw));
        return string.Join(" · ", parts);
    }

    // MARK: Printer details

    private static string MachineKey(PrinterSession session)
    {
        var s = session.Status!;
        return string.Join("|", session.Id, session.Speed, s.NozzleDiameter, s.NozzleType, s.DoorOpen, s.ChamberLightOn is null, session.ChamberLightOn,
            s.WifiSignal, s.HasSdCard, s.LiveViewEnabled, s.Firmware, s.UpdateAvailable, Loc.Language);
    }

    private FrameworkElement Machine(PrinterSession session)
    {
        var s = session.Status!;
        const string dash = "—";
        var left = new Stack { Spacing = 10 };
        left.Children.Add(W.DetailRow(Loc.T("Speed"), Loc.F("%@ (%lld%%)", session.Speed.Title(), session.Speed.Percent()), Icons.Speed(session.Speed, 13, Ctx.Brush(ThemeKeys.TextSecondary))));
        left.Children.Add(W.DetailRow(Loc.T("Nozzle"), NozzleText(s), W.Vector(Icons.Vector.Flame, Ctx)));
        if (s.DoorOpen is { } open)
            left.Children.Add(W.DetailRow(Loc.T("Door"), open ? Loc.T("Open", "Door") : Loc.T("Closed", "Door"), W.Glyph("")));
        if (s.ChamberLightOn is not null)
            left.Children.Add(W.DetailRow(Loc.T("Light"), session.ChamberLightOn ? Loc.T("On") : Loc.T("Off"), W.Vector(Icons.Vector.Bulb, Ctx)));
        var right = new Stack { Spacing = 10 };
        right.Children.Add(W.DetailRow(Loc.T("Network"), s.WifiSignal is { } dbm ? $"{PanelText.WiFi(dbm)} ({dbm} dBm)" : dash, W.Glyph("")));
        right.Children.Add(W.DetailRow(Loc.T("Storage"), s.HasSdCard switch { true => Loc.T("SD card inserted"), false => Loc.T("No SD card"), _ => dash }, W.Glyph("")));
        right.Children.Add(W.DetailRow(Loc.T("Live View"), s.LiveViewEnabled switch { true => Loc.T("On"), false => Loc.T("Off"), _ => dash }, W.Glyph(Icons.Camera)));
        right.Children.Add(W.DetailRow(Loc.T("Firmware"), (s.Firmware ?? dash) + (s.UpdateAvailable ? " · " + Loc.T("Update available") : ""), W.Glyph("")));
        var block = new Stack { Spacing = 8, Children = { Ui.SectionLabel(Loc.T("Printer"), null), Pair(left, right) } };
        if (session.Config.Model is { } model) block.Children.Add(ModelCard(model));
        return block;
    }

    /// <summary>What this model is built for, from Bambu Lab's published profiles (the Mac's ModelCard).</summary>
    private Grid ModelCard(PrinterModel model)
    {
        var left = new Stack { Spacing = 10 };
        left.Children.Add(W.DetailRow(Loc.T("Build volume"), model.BuildVolumeText, W.Glyph("")));
        left.Children.Add(W.DetailRow(Loc.T("Chamber"), model.ChamberText, W.Vector(Icons.Vector.Thermometer, Ctx)));
        left.Children.Add(W.DetailRow(Loc.T("Nozzles"), model.NozzleCount == 2 ? Loc.F("Two, up to %lld °C", model.NozzleMax) : Loc.F("One, up to %lld °C", model.NozzleMax), W.Vector(Icons.Vector.Flame, Ctx)));
        var right = new Stack { Spacing = 10 };
        right.Children.Add(W.DetailRow(Loc.T("Bed"), Loc.F("Up to %lld °C", model.BedMax), W.Vector(Icons.Vector.HeatWaves, Ctx)));
        var fans = new List<string> { Loc.T("Part cooling") };
        if (model.HasAuxFan) fans.Add(Loc.T("aux", "Auxiliary fan, in a list after “Part cooling”"));
        if (model.HasChamberFan) fans.Add(Loc.T("chamber", "Chamber fan, in a list after “Part cooling”"));
        right.Children.Add(W.DetailRow(Loc.T("Fans"), string.Join(", ", fans), W.Vector(Icons.Vector.Fan, Ctx)));
        right.Children.Add(W.DetailRow(Loc.T("Filament"), model.UsesAmsLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT", W.Glyph("")));
        return Pair(left, right);
    }

    private static Grid Pair(UIElement left, UIElement right)
    {
        var grid = Ui.Columns("*", "10", "*");
        grid.Children.Add(Ui.At(W.Card(left, 13), 0));
        grid.Children.Add(Ui.At(W.Card(right, 13), 2));
        return grid;
    }

    private static string NozzleText(PrinterStatus s)
    {
        var parts = new List<string>();
        if (s.NozzleDiameter is { } d) parts.Add(d.ToString("0.0", CultureInfo.CurrentCulture) + " mm");
        if (s.NozzleType is { } t) parts.Add(NozzleType(t));
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>"HS01" → "Hardened steel", "HH01" → "Hardened steel, high flow" (the Mac's NozzleType.describe).</summary>
    public static string NozzleType(string code)
    {
        var upper = code.ToUpperInvariant();
        if (upper.Length < 4)
            return upper.Contains("HARDENED") ? Loc.T("Hardened steel") : upper.Contains("STAINLESS") ? Loc.T("Stainless steel") : code;
        var material = upper[2..4] switch
        {
            "00" => Loc.T("Stainless steel"),
            "01" => Loc.T("Hardened steel"),
            "05" => Loc.T("Tungsten carbide"),
            _ => code,
        };
        return upper[1] switch
        {
            'U' => Loc.F("%@, TPU high flow", material),
            'H' or 'E' => Loc.F("%@, high flow", material),
            _ => material,
        };
    }

    public override void Dispose()
    {
        _refresh.Dispose();
        _issues.Hide();
    }

    /// <summary>
    /// The printer's name as the page title, with a pencil to rename it (the Mac's PrinterNameField). The name is Bambu
    /// Bar's own: it starts as the one the printer announces, and changing it here doesn't rename the printer.
    /// </summary>
    private sealed class NameField : Grid
    {
        private readonly WindowContext _ctx;
        private readonly TextBlock _title = Ui.Text("", WRamp.PageTitle, FontWeights.SemiBold, display: true);
        private readonly ChromeButton _pencil;
        private readonly Stack _editor;
        private readonly TextField _field;
        private readonly ChromeButton _save;
        private readonly TextBlock _hint = W.Note("");
        private readonly StackPanel _display;

        public NameField(WindowContext ctx)
        {
            _ctx = ctx;
            _pencil = new ChromeButton
            {
                Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = Metrics.Pill, Look = ButtonLook.Standard,
                Content = Icons.Glyph("", 12), Margin = new Thickness(10, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(_pencil, Loc.T("Rename this printer in Bambu Bar"));
            Ui.Tip(_pencil, Loc.T("Rename this printer in Bambu Bar"));
            _pencil.Click += (_, _) => Edit();
            _display = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _pencil } };

            _field = new TextField(Loc.T("Printer name"), width: 420);
            _field.Box!.FontSize = 20;
            _field.OnChange(UpdateEditor).OnKeys(Save, Cancel);
            var cancel = W.Button(Loc.T("Cancel"), Cancel);
            _save = W.Button(Loc.T("Save"), Save, accent: true);
            var row = new Stack { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _field, cancel, _save } };
            _editor = new Stack { Spacing = 6, Children = { row, _hint }, Visibility = Visibility.Collapsed };
            Children.Add(_display);
            Children.Add(_editor);
        }

        private bool Editing => _editor.Visibility == Visibility.Visible;

        public void Refresh()
        {
            _title.Text = _ctx.Store.PrinterName;
            _pencil.Visibility = _ctx.Store.Current is null ? Visibility.Collapsed : Visibility.Visible;
            if (Editing) UpdateEditor();
        }

        private void Edit()
        {
            _field.Text = _ctx.Store.PrinterName;
            _display.Visibility = Visibility.Collapsed;
            _editor.Visibility = Visibility.Visible;
            UpdateEditor();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _field.FocusInput);
        }

        private void UpdateEditor()
        {
            var draft = _field.Text;
            _save.IsEnabled = draft.Trim().Length > 0;
            var announced = _ctx.Store.Current is { } current ? _ctx.Store.AnnouncedName(current.Id) : null;
            W.Rich(_hint, announced is not null && announced != draft
                ? Loc.F("Only in Bambu Bar. The printer still calls itself “%@”; rename it there in Bambu Handy.", announced)
                : Loc.T("Only in Bambu Bar, for the tray, window and widgets. The printer keeps its own name."));
        }

        private void Save()
        {
            if (_ctx.Store.Current is not { } current || _field.Text.Trim().Length == 0) return;
            _ctx.Store.Rename(current.Id, _field.Text);
            Cancel();
        }

        private void Cancel()
        {
            _editor.Visibility = Visibility.Collapsed;
            _display.Visibility = Visibility.Visible;
            Refresh();
            _pencil.Focus();
        }
    }
}
