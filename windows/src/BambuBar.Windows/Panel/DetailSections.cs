using System.ComponentModel;
using System.Net.Http;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>Opens links in the default browser.</summary>
internal static class Shell
{
    public static void Open(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No browser to hand it to: nothing more to do from a tray panel.
        }
    }
}

/// <summary>
/// The printer's most serious issue (the Mac's IssueBanner): the message, its code with a copy button that ticks for
/// 1.5 s, a link to the code's page on Bambu Lab's wiki (resolved by Core's HmsWiki when clicked), and a dismiss button
/// that hides it until the printer clears it.
/// </summary>
internal sealed class IssueSection : PanelSection, IDisposable
{
    private static readonly HmsWiki Wiki = new();
    private readonly Border _root;
    private readonly ContentControl _icon = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
    private readonly TextBlock _message;
    private readonly TextBox _code;
    private readonly ChromeButton _copy;
    private readonly ChromeButton _wiki;
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private PrinterIssue? _issue;

    public IssueSection(PanelContext ctx) : base(ctx)
    {
        _message = Ui.Paragraph("", Ramp.Body, ThemeKeys.TextPrimary, FontWeights.Medium);
        _code = new TextBox
        {
            IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            FontSize = Ramp.Micro, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center,
        };
        _code.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        _code.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextSecondary);
        _copy = new ChromeButton { Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        _copy.Click += (_, _) => CopyCode();
        _copiedTimer.Tick += (_, _) => ShowCopied(false);
        _wiki = new ChromeButton { Padding = new Thickness(5, 1, 5, 2), Margin = new Thickness(6, 0, 0, 0) };
        var wikiText = Ui.Text(Loc.T("Wiki"), Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextLink);
        _wiki.Content = wikiText;
        AutomationProperties.SetName(_wiki, Loc.T("Wiki"));
        _wiki.Click += async (_, _) =>
        {
            if (_issue?.WikiUrl is not { } url) return;
            // Resolved on click, so it lands on the code's page rather than the HMS index where it can.
            Uri? page = null;
            try { page = await Wiki.ResolveAsync(url); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
            Shell.Open(page ?? url);
        };
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _code, _copy, _wiki } };
        var text = new Stack { Spacing = 3, Children = { _message, codeRow } };

        var dismiss = new ChromeButton { Width = 24, Height = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -3, -3, 0) };
        dismiss.Content = Icons.Glyph(Icons.Close, 10);
        AutomationProperties.SetName(dismiss, Loc.T("Dismiss until the printer clears it"));
        Ui.Tip(dismiss, Loc.T("Dismiss until the printer clears it"));
        dismiss.Click += (_, _) => { if (_issue is { } issue) Store.Current?.Dismiss(issue); };

        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(_icon, 0));
        grid.Children.Add(Ui.At(text, 1));
        grid.Children.Add(Ui.At(dismiss, 2));
        _root = Ui.Card(grid, Metrics.Tile, new Thickness(11));
        ShowCopied(false);
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        var issue = Store.Current!.Status!.MostSevereIssue!;
        if (issue == _issue) return;
        _issue = issue;
        var color = issue.Severity <= IssueSeverity.Fatal ? Palette.Failed : issue.Severity == IssueSeverity.Serious ? Palette.Paused : Palette.Preparing;
        Ui.Tint(_root, color);
        _icon.Content = Icons.Glyph(issue.Severity <= IssueSeverity.Serious ? Icons.Warning : Icons.Info, 14, null, Ctx.Ink(color));
        _message.Text = issue.Message;
        _code.Text = issue.Id;
        _wiki.Visibility = issue.WikiUrl is null ? Visibility.Collapsed : Visibility.Visible;
        if (issue.WikiUrl is { } url) Ui.Tip(_wiki, url.AbsoluteUri);
    }

    /// <summary>The code as shown, for a search or a support ticket; the icon turns into a tick for a moment.</summary>
    private void CopyCode()
    {
        if (_issue is null) return;
        try { Clipboard.SetText(_issue.Id); }
        catch (Exception e) when (e is ExternalException) { return; }
        ShowCopied(true);
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void ShowCopied(bool copied)
    {
        if (!copied) _copiedTimer.Stop();
        _copy.Content = Icons.Glyph(copied ? Icons.Check : Icons.Copy, 10, copied ? null : ThemeKeys.TextSecondary, copied ? Ctx.Ink(Palette.Accent) : null);
        var label = copied ? Loc.T("Copied") : Loc.T("Copy code");
        AutomationProperties.SetName(_copy, label);
        Ui.Tip(_copy, label);
    }

    public void Dispose() => _copiedTimer.Stop();
}

/// <summary>Where the camera shows (the Mac's CameraPreview): a header that folds it away and a 16:9 frame. The video
/// itself comes with P3.9; until then the frame holds a placeholder. Never shown for demo printers, as on the Mac.</summary>
internal sealed class CameraSection : PanelSection
{
    private const string ExpandedKey = "cameraInMenuBar";
    private readonly Stack _root;
    private readonly ChromeButton _header;
    private readonly RotateTransform _chevron = new();
    private readonly Border _frame;

    public CameraSection(PanelContext ctx) : base(ctx)
    {
        var chevron = Icons.Glyph(Icons.ChevronRight, 9);
        chevron.RenderTransform = _chevron;
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        var video = Icons.Glyph(Icons.Camera, 12);
        video.Margin = new Thickness(6, 0, 5, 0);
        var title = Ui.Text(Loc.T("Camera"), Ramp.Body, FontWeights.SemiBold, ThemeKeys.TextSecondary);
        _header = new ChromeButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, video, title } },
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2, 8, 3),
        };
        AutomationProperties.SetName(_header, Loc.T("Camera"));
        _header.Click += (_, _) =>
        {
            Ctx.Settings.Set(ExpandedKey, !Ctx.Setting(ExpandedKey, true));
            Fill();
        };
        _frame = new Border { Height = (Metrics.PanelWidth - 2 * Metrics.PanelPadding) * 9 / 16, CornerRadius = Metrics.Tile, BorderThickness = new Thickness(1) };
        _frame.SetResourceReference(Border.BackgroundProperty, ThemeKeys.CardFill);
        _frame.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardStroke);
        _frame.Child = Icons.Glyph(Icons.Camera, 28, ThemeKeys.TextTertiary);
        _root = new Stack { Spacing = 6, Children = { _header, _frame } };
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        var expanded = Ctx.Setting(ExpandedKey, true);
        _frame.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Angle = expanded ? 90 : 0;
        Ui.Tip(_header, expanded ? Loc.T("Hide the camera") : Loc.T("Show the camera"));
        AutomationProperties.SetItemStatus(_header, expanded ? Loc.T("On") : Loc.T("Off"));
    }
}

/// <summary>The temperature tiles (the Mac's TemperatureTiles): nozzle (two on two-nozzle models), bed, and the
/// chamber where the model has a sensor and the setting allows it.</summary>
internal sealed class TemperatureSection : PanelSection
{
    private readonly Grid _root = new();
    private string _key = "";

    public TemperatureSection(PanelContext ctx) : base(ctx) { }

    public override FrameworkElement Root => _root;

    private sealed record Tile(string Title, Icons.Vector Icon, Temperature? Temp, double? Plain, Color Tint, bool InUse = false)
    {
        public double? Current => Temp?.Current ?? Plain;
        public bool Heating => (Temp?.Target ?? 0) > 0;
        /// <summary>Still on its way to the target.</summary>
        public bool Changing => Temp is { Target: > 0 } t && !t.IsAtTarget;
        public string Key => $"{Title}|{Current?.ToString("0") ?? "-"}|{Temp?.Target}|{InUse}|{Changing}";
    }

    protected override void Fill()
    {
        var s = Store.Current!.Status!;
        var model = Store.Current.Config.Model;
        var tiles = new List<Tile>();
        if (s.SecondNozzle is { } left)
        {
            // Both nozzles: the idle one can be still warm, so the one printing gets a dot.
            var printing = s.State.IsActive();
            tiles.Add(new(Loc.T("Left"), Icons.Vector.Flame, left, null, Palette.Nozzle, printing && s.LeftNozzleInUse == true));
            tiles.Add(new(Loc.T("Right"), Icons.Vector.Flame, s.Nozzle, null, Palette.Nozzle, printing && s.LeftNozzleInUse == false));
        }
        else tiles.Add(new(Loc.T("Nozzle"), Icons.Vector.Flame, s.Nozzle, null, Palette.Nozzle));
        tiles.Add(new(Loc.T("Bed"), Icons.Vector.HeatWaves, s.Bed, null, Palette.Bed));
        if (Ctx.Setting(AppSettings.Keys.PanelChamber, true))
        {
            if (s.Chamber is { } chamber)
            {
                tiles.Add(model?.ChamberHeaterMax is not null
                    ? new(Loc.T("Chamber"), Icons.Vector.Thermometer, new Temperature(chamber, s.ChamberTarget ?? 0), null, Palette.Chamber)
                    : new(Loc.T("Chamber"), Icons.Vector.Thermometer, null, chamber, Palette.Chamber));
            }
            // A model with a sensor that hasn't reported yet. An unknown model shows a chamber only once it reports one.
            else if (model?.HasChamberSensor == true) tiles.Add(new(Loc.T("Chamber"), Icons.Vector.Thermometer, null, null, Palette.Chamber));
        }

        var key = string.Join("#", tiles.Select(t => t.Key));
        if (key == _key) return;
        _key = key;
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i > 0) _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Metrics.Gap) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _root.Children.Add(Ui.At(Build(tiles[i]), i * 2));
        }
    }

    private FrameworkElement Build(Tile t)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Icons.Draw(t.Icon, 11, t.Heating ? Palette.Brush(t.Tint) : Ctx.Brush(ThemeKeys.TextSecondary)));
        var title = Ui.Text(t.Title, Ramp.Footnote, FontWeights.Medium, ThemeKeys.TextSecondary);
        title.Margin = new Thickness(4, 0, 0, 0);
        header.Children.Add(title);
        if (t.InUse)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Palette.Brush(t.Tint), Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Ui.Tip(dot, Loc.T("Printing with this nozzle"));
            header.Children.Add(dot);
        }

        var reading = Ui.Tabular(Ui.Text("", Ramp.Reading, FontWeights.SemiBold, display: true));
        reading.Inlines.Add(new Run(t.Current is { } c ? $"{(long)Math.Round(c, MidpointRounding.AwayFromZero)}°" : "–"));
        if (t.Changing && t.Temp is { } temp)
        {
            reading.Inlines.Add(new Run($"  → {(long)temp.Target}°") { FontSize = Ramp.Footnote, Foreground = Ctx.Ink(t.Tint) });
        }
        reading.TextTrimming = TextTrimming.None;
        // Four tiles (two nozzles and a chamber) leave little room: the reading shrinks rather than clips.
        var fit = new Viewbox { Child = reading, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };

        var tile = new AccessibleCard
        {
            Child = new Stack { Spacing = 6, Children = { header, fit } }, CornerRadius = Metrics.Tile, Padding = new Thickness(11),
            BorderThickness = new Thickness(1),
        };
        Ui.Tint(tile, null);
        AutomationProperties.SetName(tile, Loc.F("%@, %@", t.Title, Spoken(t)));
        return tile;
    }

    private static string Spoken(Tile t)
    {
        var parts = new List<string>();
        if (t.InUse) parts.Add(Loc.T("in use", "A nozzle that is printing, read by VoiceOver"));
        parts.Add(t.Current is { } c ? Loc.P("%lld degrees", (long)c) : Loc.T("unknown", "Temperature not reported yet"));
        if (t.Changing && t.Temp is { } temp)
            parts.Add(temp.Current < temp.Target ? Loc.F("heating to %lld", (long)temp.Target) : Loc.F("cooling to %lld", (long)temp.Target));
        return string.Join(", ", parts);
    }
}

/// <summary>The fans in one row (the Mac's FanStrip, opt-in from the Tray page): part cooling, aux, chamber, hotend
/// as the model has them.</summary>
internal sealed class FanSection : PanelSection
{
    private readonly Border _root;
    private readonly Grid _grid = new();
    private string _key = "";

    public FanSection(PanelContext ctx) : base(ctx) => _root = Ui.Card(_grid, Metrics.Tile, new Thickness(11));

    public override FrameworkElement Root => _root;

    public static bool HasFans(PrinterStatus s) =>
        s.PartFan is not null || s.AuxFan is not null || s.LeftAuxFan is not null || s.ExhaustFan is not null
        || s.CirculationFan is not null || s.HeatbreakFan is not null;

    /// <summary>Part cooling, then the aux and chamber fans where the model has them, and the hotend fan when one of
    /// those is missing; the H3C's two aux fans and chamber fan named as Bambu Studio does.</summary>
    public static List<(string Title, int? Value)> Readings(PrinterStatus s)
    {
        var fans = new List<(string, int?)> { (Loc.T("Part"), s.PartFan) };
        if (s.AuxFan is { } aux) fans.Add((s.LeftAuxFan is null ? Loc.T("Aux") : Loc.T("Aux R"), aux));
        if (s.LeftAuxFan is { } auxLeft) fans.Add((Loc.T("Aux L"), auxLeft));
        if (s.ExhaustFan is { } exhaust) fans.Add((s.CirculationFan is null ? Loc.T("Chamber") : Loc.T("Exhaust"), exhaust));
        if (s.CirculationFan is { } circulation) fans.Add((Loc.T("Chamber"), circulation));
        if (s.HeatbreakFan is { } hotend && (s.AuxFan is null || s.ExhaustFan is null)) fans.Add((Loc.T("Hotend"), hotend));
        return fans;
    }

    protected override void Fill()
    {
        var s = Store.Current!.Status!;
        var fans = Readings(s);
        var key = string.Join("#", fans.Select(f => $"{f.Title}{f.Value}")) + (s.PartFan > 0);
        if (key == _key) return;
        _key = key;
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        // Five fans (the H3C) leave too little room for a name and its speed side by side: the speed goes below.
        var stacked = fans.Count > 4;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icons.Draw(Icons.Vector.Fan, 12, (s.PartFan ?? 0) > 0 ? Palette.Brush(Palette.Chamber) : Ctx.Brush(ThemeKeys.TextSecondary));
        icon.Margin = new Thickness(0, 0, stacked ? 10 : 12, 0);
        _grid.Children.Add(Ui.At(icon, 0));
        for (var i = 0; i < fans.Count; i++)
        {
            if (i > 0) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stacked ? 10 : 12) });
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (title, value) = fans[i];
            var name = Ui.Text(title, Ramp.Caption, null, ThemeKeys.TextSecondary);
            var percent = Ui.Tabular(Ui.Text(value is { } v ? $"{v}%" : "–", Ramp.Caption, FontWeights.Medium));
            FrameworkElement top;
            if (stacked) top = new StackPanel { Children = { name, percent } };
            else
            {
                var row = Ui.Columns("*", "Auto");
                row.Children.Add(Ui.At(name, 0));
                percent.Margin = new Thickness(4, 0, 0, 0);
                row.Children.Add(Ui.At(percent, 1));
                top = row;
            }
            var bar = new ProgressCapsule(4) { Margin = new Thickness(0, 4, 0, 0), Tint = Palette.WithAlpha(Palette.Chamber, 0.85) };
            bar.SetProgress((value ?? 0) / 100.0, false);
            var item = new AccessibleStack { Children = { top, bar } };
            AutomationProperties.SetName(item, $"{title}, {(value is { } p ? $"{p}%" : "–")}");
            _grid.Children.Add(Ui.At(item, 1 + i * 2));
        }
    }
}

/// <summary>The filament strip (the Mac's FilamentStrip): one row per AMS unit with its climate, each spool with its
/// material and what's left, the external spool at the end. Scrolls past 360 DIPs (an H2 can carry twelve units) and
/// opens on the unit feeding the print.</summary>
internal sealed class FilamentSection : PanelSection
{
    private readonly Border _root;
    private readonly ScrollViewer _scroller;
    private readonly Stack _units = new() { Spacing = 9 };
    private string _key = "";
    private FrameworkElement? _activeUnit;
    private bool _scrollPending;

    public FilamentSection(PanelContext ctx) : base(ctx)
    {
        _scroller = new ScrollViewer
        {
            Content = _units, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(12),
        };
        _root = Ui.Card(_scroller, Metrics.Card, new Thickness(0));
    }

    public override FrameworkElement Root => _root;

    public void ScrollToActiveOnNextFill() => _scrollPending = true;

    protected override void Fill()
    {
        var s = Store.Current!.Status!;
        var activeId = s.State.IsActive() ? s.ActiveSpoolId : null;
        var ext = s.ExternalSpool is { IsLoaded: true } e ? e : null;
        var key = string.Join("#", s.Ams.Select(u => $"{u.Id}{u.Kind}{Climate(u)}|" + string.Join(",", u.Spools.Select(SpoolKey))))
            + $"|{(ext is null ? "" : SpoolKey(ext))}|{activeId}";
        if (key != _key)
        {
            _key = key;
            Rebuild(s, ext, activeId);
        }
        if (_scrollPending && _activeUnit is { } unit)
        {
            _scrollPending = false;
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => unit.BringIntoView());
        }
    }

    private static string SpoolKey(Spool s) => $"{s.Id}:{s.Color}:{s.Remain}:{s.Material}:{s.IsLoaded}:{s.Weight}";

    private void Rebuild(PrinterStatus s, Spool? ext, int? activeId)
    {
        _units.Children.Clear();
        _activeUnit = null;
        foreach (var unit in s.Ams)
        {
            var row = new Grid();
            var columns = 0;
            void Add(UIElement e, GridLength width)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                row.Children.Add(Ui.At(e, columns++));
            }
            foreach (var spool in unit.Spools) Add(Column(spool, spool.Id == activeId), new GridLength(1, GridUnitType.Star));
            if (unit == s.Ams[^1] && ext is not null)
            {
                var divider = new Border { Width = 1, Height = 40, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                divider.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Divider);
                Add(divider, GridLength.Auto);
                Add(Column(ext, ext.Id == activeId), new GridLength(1, GridUnitType.Star));
            }
            var block = new Stack { Spacing = 8, Children = { Ui.SectionLabel(unit.Title, Climate(unit)), row } };
            if (unit.Spools.Any(sp => sp.Id == activeId)) _activeUnit = block;
            _units.Children.Add(block);
        }
        if (s.Ams.Count == 0 && ext is not null)
        {
            _units.Children.Add(Ui.SectionLabel(Loc.T("External spool"), null));
            var row = Ui.Columns("*", "*", "*", "*");
            row.Children.Add(Ui.At(Column(ext, ext.Id == activeId), 0));
            _units.Children.Add(row);
        }
    }

    private static string? Climate(AmsUnit unit)
    {
        var parts = new List<string>();
        if (unit.DryingMinutesLeft > 0) parts.Add(Loc.F("Drying · %@", Format.Duration(unit.DryingMinutesLeft)));
        if (unit.Humidity is { } h) parts.Add(Loc.F("%lld%% RH", h));
        else if (unit.HumidityLevel is { } level) parts.Add(Loc.F("Humidity %lld/5", level));
        if (unit.Temperature is { } t) parts.Add(Format.Temperature(t));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private FrameworkElement Column(Spool spool, bool active)
    {
        var swatch = new Grid { Width = Metrics.Swatch, Height = Metrics.Swatch, HorizontalAlignment = HorizontalAlignment.Center };
        swatch.Children.Add(new SpoolSwatch(spool, active, Metrics.Swatch));
        if (active)
        {
            var dot = new Ellipse
            {
                Width = 9, Height = 9, Fill = Palette.Brush(Palette.Accent), StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0),
            };
            dot.SetResourceReference(Shape.StrokeProperty, ThemeKeys.PanelBackground);
            swatch.Children.Add(dot);
        }
        var material = Ui.Text(spool.IsLoaded ? spool.Material : spool.Label, Ramp.Caption, FontWeights.SemiBold,
            spool.IsLoaded ? ThemeKeys.TextPrimary : ThemeKeys.TextTertiary);
        material.HorizontalAlignment = HorizontalAlignment.Center;
        var amount = Ui.Tabular(Ui.Text(spool.AmountText, Ramp.Micro, null, ThemeKeys.TextSecondary));
        amount.HorizontalAlignment = HorizontalAlignment.Center;
        var column = new AccessibleStack { Children = { swatch, material, amount }, Margin = new Thickness(1, 0, 1, 0) };
        material.Margin = new Thickness(0, 4, 0, 0);
        var help = Help(spool);
        Ui.Tip(column, help);
        AutomationProperties.SetName(column, help);
        return column;
    }

    private static string Help(Spool spool)
    {
        if (!spool.IsLoaded) return Loc.F("%@: empty", spool.SlotName);
        var name = string.Join(" ", new[] { spool.Brand, spool.DisplayName }.Where(p => !string.IsNullOrEmpty(p)));
        var parts = new[] { name, spool.ColorName, spool.Remain is null ? null : Loc.F("%@ left", spool.RemainDetail) }
            .Where(p => !string.IsNullOrEmpty(p));
        return $"{spool.SlotName}: " + string.Join(", ", parts);
    }
}

/// <summary>Pause or resume, stop (with the confirmation Core asks for), and the speed menu (the Mac's PrintControls).
/// When the printer refuses commands (LAN Only + Developer Mode off), the icons become locks and the tooltips say why;
/// pressing anyway shows the store's toast.</summary>
internal sealed class ControlsSection : PanelSection
{
    private readonly Grid _root;
    private readonly ChromeButton _pause;
    private readonly ChromeButton _stop;
    private readonly ChromeButton _speed;
    private string _key = "";

    public ControlsSection(PanelContext ctx) : base(ctx)
    {
        _pause = new ChromeButton { Height = Metrics.ControlHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        _pause.Click += (_, _) =>
        {
            if (Store.Current?.Status?.State == JobState.Paused) Store.Resume(); else Store.Pause();
        };
        _stop = new ChromeButton { Height = Metrics.ControlHeight, HorizontalAlignment = HorizontalAlignment.Stretch, Look = ButtonLook.Tinted(Palette.Failed) };
        _stop.Click += (_, _) => Store.StopPrint();
        _speed = new ChromeButton { Height = Metrics.ControlHeight, Width = 44, Padding = new Thickness(0), Look = ButtonLook.Standard };
        AutomationProperties.SetName(_speed, Loc.T("Speed"));
        _speed.Click += (_, _) => ShowSpeedMenu();
        _root = Ui.Columns("*", "8", "*", "8", "44");
        _root.Children.Add(Ui.At(_pause, 0));
        _root.Children.Add(Ui.At(_stop, 2));
        _root.Children.Add(Ui.At(_speed, 4));
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        var session = Store.Current!;
        var s = session.Status!;
        var locked = s.ControlsLocked;
        var key = $"{s.State}|{locked}|{session.Speed}";
        if (key == _key) return;
        _key = key;
        var lockedHelp = locked ? Loc.T("The printer accepts this only in LAN Only mode with Developer Mode on (Settings → Network on its screen).") : null;
        var paused = s.State == JobState.Paused;

        _pause.Look = paused ? ButtonLook.Accent : ButtonLook.Standard;
        _pause.Content = Label(locked ? Icons.Lock : paused ? Icons.Play : Icons.Pause, paused ? Loc.T("Resume") : Loc.T("Pause"),
            paused ? Ctx.Brush(ThemeKeys.TextOnAccent) : Ctx.Brush(ThemeKeys.TextPrimary));
        AutomationProperties.SetName(_pause, paused ? Loc.T("Resume") : Loc.T("Pause"));
        Ui.Tip(_pause, lockedHelp ?? (paused ? Loc.T("Resume the print") : Loc.T("Pause the print")));

        _stop.Content = Label(locked ? Icons.Lock : Icons.Stop, Loc.T("Stop"), Ctx.Ink(Palette.Failed));
        AutomationProperties.SetName(_stop, Loc.T("Stop"));
        Ui.Tip(_stop, lockedHelp ?? Loc.T("Stop the print"));

        _speed.Content = Icons.Speed(session.Speed, 16, Ctx.Brush(ThemeKeys.TextPrimary));
        Ui.Tip(_speed, Loc.F("Print speed: %@", session.Speed.Title()));
        AutomationProperties.SetItemStatus(_speed, session.Speed.Title());
    }

    private static StackPanel Label(string glyph, string text, Brush brush)
    {
        var label = Ui.Text(text, Ramp.Control, FontWeights.SemiBold);
        label.Foreground = brush;
        label.Margin = new Thickness(6, 0, 0, 1);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { Icons.Glyph(glyph, 12, null, brush), label } };
    }

    private void ShowSpeedMenu()
    {
        var current = Store.Current?.Speed ?? SpeedLevel.Standard;
        var entries = Enum.GetValues<SpeedLevel>().Select(level => new MenuEntry(
            Loc.F("%@ (%lld%%)", level.Title(), level.Percent()), () => Store.SetSpeed(level), level == current,
            brush => Icons.Speed(level, 16, brush))).ToList();
        MenuPopup.Show(_speed, Ctx.Theme, entries, Loc.T("Speed"));
    }
}
