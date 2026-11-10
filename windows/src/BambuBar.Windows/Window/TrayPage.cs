using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Theme;
using BambuBar.Tray;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>
/// The tray in one place (the Mac's Menu Bar page): which printer the icon follows, what it says while printing,
/// the percentage in the icon, and what the panel above it holds, with a preview beside the settings that changes as
/// they do.
/// </summary>
internal sealed class TrayPage(WindowContext ctx) : Page(ctx)
{
    private const string Group = "trayStyle";
    private TrayPreview _preview = null!;
    private Grid _layout = null!;
    private FrameworkElement _settings = null!;
    private FrameworkElement _previewBlock = null!;
    private readonly Region _printerPick = new();
    private ToggleSwitch _chamber = null!;
    private bool _wide = true;

    private bool Setting(string key, bool fallback) => Ctx.Settings.Get(key, fallback);

    public override FrameworkElement Build()
    {
        _preview = new TrayPreview(Ctx);
        var caption = Ui.Paragraph(Loc.T("Sample print. Changes show here as you make them."), WRamp.Note);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.TextAlignment = TextAlignment.Center;
        _previewBlock = new Stack { Spacing = 10, Width = TrayPreview.PreviewWidth, HorizontalAlignment = HorizontalAlignment.Center, Children = { _preview, caption } };
        _settings = Settings();
        _layout = new Grid();
        _layout.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(800);
        return new Stack
        {
            Spacing = 22,
            Children =
            {
                W.PageHeader(Loc.T("Tray", "Sidebar page: the tray icon and its panel (the Mac's Menu Bar page)"),
                    Loc.T("What the tray icon shows while you print, and what you see when you click it.")),
                _layout,
            },
        };
    }

    /// <summary>Side by side when there's room, so each change shows next to the switch that made it; else the
    /// preview on top.</summary>
    private void Arrange(double width)
    {
        var wide = width >= TrayPreview.PreviewWidth + 16 + 260;
        if (wide == _wide && _layout.Children.Count > 0) return;
        _wide = wide;
        _layout.Children.Clear();
        _layout.ColumnDefinitions.Clear();
        _layout.RowDefinitions.Clear();
        if (wide)
        {
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _layout.Children.Add(Ui.At(_settings, 0));
            _previewBlock.VerticalAlignment = VerticalAlignment.Top;
            _layout.Children.Add(Ui.At(_previewBlock, 2));
        }
        else
        {
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.Children.Add(Ui.At(_previewBlock, 0, 0));
            _layout.Children.Add(Ui.At(_settings, 0, 2));
        }
    }

    private FrameworkElement Settings()
    {
        var tray = W.Group(Loc.T("Tray icon"), AppPage.Tray.Glyph(), out var trayBody);
        trayBody.Children.Add(_printerPick.Host);
        // The Mac's "While printing, show" has no place on Windows: nothing can sit beside a tray icon, and the
        // tooltip always gives the whole line (ledger D2). The percentage in the icon is the Windows option.
        trayBody.Children.Add(W.Switch(Loc.T("Show the percentage in the icon"), Setting(WindowSettings.TrayShowsPercent, false), on =>
        {
            Ctx.Settings.Set(WindowSettings.TrayShowsPercent, on);
            Ctx.Services.TrayChanged?.Invoke();
            _preview.Light(TrayPreview.Part.Icon);
        }));
        trayBody.Children.Add(W.Note(Loc.T("While printing, the icon shows the percentage in place of the printer.")));

        var panel = W.Group(Loc.T("What to show"), "", out var panelBody);
        void Toggle(string title, string key, bool fallback, TrayPreview.Part part, Action<bool>? also = null, Func<bool, bool>? lights = null)
        {
            panelBody.Children.Add(W.Switch(title, Setting(key, fallback), on =>
            {
                Ctx.Settings.Set(key, on);
                also?.Invoke(on);
                if (lights?.Invoke(on) ?? on) _preview.Light(part);
                else _preview.Refresh();
            }, out var toggle));
            if (key == AppSettings.Keys.PanelChamber) _chamber = toggle;
        }
        Toggle(Loc.T("Temperatures"), AppSettings.Keys.PanelTemperatures, true, TrayPreview.Part.Temperatures, on => _chamber.IsEnabled = on);
        Toggle(Loc.T("Chamber temperature"), AppSettings.Keys.PanelChamber, true, TrayPreview.Part.Temperatures,
            lights: on => on && Setting(AppSettings.Keys.PanelTemperatures, true));
        _chamber.IsEnabled = Setting(AppSettings.Keys.PanelTemperatures, true);
        Toggle(Loc.T("Fans"), AppSettings.Keys.PanelFans, false, TrayPreview.Part.Fans);
        Toggle(Loc.T("Filament"), AppSettings.Keys.PanelFilament, true, TrayPreview.Part.Filament);
        panelBody.Children.Add(W.Note(Loc.T("What the panel above the tray icon shows below the print. The chamber temperature appears only for printers with a chamber sensor (not the P1 and A series).")));
        panelBody.Children.Add(W.Divider());
        // One switch for every print control, here rather than in Settings: the panel is where they're used most.
        panelBody.Children.Add(W.Switch(Loc.T("Pause, stop and speed"), Store.ShowPrinterActions, on =>
        {
            Store.ShowPrinterActions = on;
            if (on) _preview.Light(TrayPreview.Part.Actions); else _preview.Refresh();
        }));
        panelBody.Children.Add(W.Note(Loc.T("Show pause, resume, stop and print speed controls in the tray panel and the main window, and print actions in the tray icon's menu.")));

        return new Stack { Spacing = 22, Children = { tray, panel } };
    }

    public override void Refresh()
    {
        var multiple = Store.HasMultiplePrinters;
        var names = string.Join("#", Store.Sessions.Select(s => s.Id + s.Config.Name));
        _printerPick.Show($"{multiple}|{Store.MenuBarSerial}|{names}", () => multiple ? PrinterPick() : null);
        _preview.Follow(Store.MenuBarSession ?? Store.Current);
    }

    /// <summary>"Printer in the tray": Automatic or one of them (the Mac's menuBarPrinter).</summary>
    private Stack PrinterPick()
    {
        var current = Store.MenuBarSerial is { } serial ? Store.Session(serial) : null;
        var dropdown = new Dropdown(Loc.T("Printer in the tray"), Ctx.Theme, () =>
        {
            var entries = new List<MenuEntry>
            {
                new(Loc.T("Automatic"), () => Choose(null), Store.MenuBarSerial is null),
                MenuEntry.Separator,
            };
            entries.AddRange(Store.Sessions.Select(s => new MenuEntry(s.Config.Name, () => Choose(s.Id), s.Id == Store.MenuBarSerial)));
            return entries;
        }) { Text = current?.Config.Name ?? Loc.T("Automatic"), HorizontalAlignment = HorizontalAlignment.Left };
        var stack = new Stack { Spacing = 8, Children = { Ui.Text(Loc.T("Printer in the tray"), WRamp.Body), dropdown } };
        if (current is null)
            stack.Children.Add(W.Note(Loc.T("Automatic shows whichever printer is busy (paused first, then printing), otherwise the one you last looked at.")));
        stack.Children.Add(W.Divider());
        return stack;
    }

    private void Choose(string? serial)
    {
        Store.MenuBarSerial = serial;
        Refresh();
        _preview.Light(TrayPreview.Part.Header);
    }

    public override void Dispose() => _preview.Dispose();
}

/// <summary>
/// The tray and its open panel over a desktop, playing a sample print under your printer's name. Built from the
/// panel's own sections over a demo printer, and only for looking: nothing in it can be clicked (the light switch
/// would reach the printer). Whatever was just turned on lights up for a moment.
/// </summary>
internal sealed class TrayPreview : AccessibleCard, IDisposable
{
    public enum Part { Icon, Header, Temperatures, Fans, Filament, Actions }

    public const double PreviewWidth = Metrics.PanelWidth + 2 * Inset;
    private const double Inset = 14;

    private readonly WindowContext _ctx;
    private readonly PrinterStore _demo;
    private readonly PanelContext _panel;
    private readonly HeaderSection _header;
    private readonly JobSection _job;
    private readonly TemperatureSection _temperatures;
    private readonly FanSection _fans;
    private readonly FilamentSection _filament;
    private readonly ControlsSection _controls;
    private readonly FooterSection _footer;
    private readonly Dictionary<Part, Lit> _lit = [];
    private readonly Image _icon = new() { Width = 20, Height = 20, Stretch = Stretch.Uniform };
    private readonly TextBlock _tooltip = Ui.Tabular(Ui.Text("", 12));
    private readonly TextBlock _clock = Ui.Tabular(Ui.Text("", 11));
    private readonly TextBlock _date = Ui.Tabular(Ui.Text("", 11));
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _dim = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private Part? _lighted;

    public TrayPreview(WindowContext ctx)
    {
        _ctx = ctx;
        _demo = new PrinterStore(new PrinterStore.Services
        {
            Settings = new AppSettings(null), History = new HistoryStore(null), Secrets = new MemorySecretStore(), Arguments = ["--demo", "printing"],
        });
        _demo.Start();
        // The panel's switches are the real ones; the printer is the demo's.
        _panel = new PanelContext
        {
            Store = _demo, Settings = ctx.Settings, Theme = ctx.Theme, Dark = ctx.Dark, Animate = ctx.Animate,
            Navigate = (_, _) => { }, Quit = () => { }, ShowsAll = () => false, SetShowsAll = _ => { },
        };
        _header = new HeaderSection(_panel);
        _job = new JobSection(_panel);
        _temperatures = new TemperatureSection(_panel);
        _fans = new FanSection(_panel);
        _filament = new FilamentSection(_panel);
        _controls = new ControlsSection(_panel);
        _footer = new FooterSection(_panel);

        var sections = new Stack { Spacing = Metrics.Gap };
        sections.Children.Add(Wrap(Part.Header, _header.Root));
        sections.Children.Add(_job.Root);
        sections.Children.Add(Wrap(Part.Temperatures, _temperatures.Root));
        sections.Children.Add(Wrap(Part.Fans, _fans.Root));
        sections.Children.Add(Wrap(Part.Filament, _filament.Root));
        sections.Children.Add(Wrap(Part.Actions, _controls.Root));
        sections.Children.Add(_footer.Root);
        var panelCard = new Border
        {
            Width = Metrics.PanelWidth, Padding = new Thickness(Metrics.PanelPadding), CornerRadius = Metrics.Overlay, BorderThickness = new Thickness(1),
            Child = sections, Margin = new Thickness(Inset, Inset, Inset, 8),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.28 },
        };
        panelCard.SetResourceReference(BackgroundProperty, ThemeKeys.PanelBackground);
        panelCard.SetResourceReference(BorderBrushProperty, ThemeKeys.PanelStroke);

        var tip = new Border
        {
            Child = _tooltip, Padding = new Thickness(8, 5, 8, 6), CornerRadius = Metrics.Control, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 70, 6),
        };
        tip.SetResourceReference(BackgroundProperty, ThemeKeys.FlyoutFill);
        tip.SetResourceReference(BorderBrushProperty, ThemeKeys.FlyoutStroke);

        Child = new Stack { Children = { panelCard, tip, Taskbar() } };
        CornerRadius = Metrics.Hero;
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, ThemeKeys.CardStroke);
        Background = Wallpaper(ctx.Dark);
        // Only for looking: no clicks, and Tab passes it by.
        IsHitTestVisible = false;
        Focusable = false;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(this, System.Windows.Input.KeyboardNavigationMode.None);
        System.Windows.Input.KeyboardNavigation.SetDirectionalNavigation(this, System.Windows.Input.KeyboardNavigationMode.None);
        AutomationProperties.SetName(this, Loc.T("Preview of the tray icon and its panel"));

        _tick.Tick += (_, _) => Refresh();
        _dim.Tick += (_, _) => Dim();
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
        Refresh();
    }

    private static LinearGradientBrush Wallpaper(bool dark)
    {
        var brush = dark
            ? new LinearGradientBrush(Color.FromRgb(0x10, 0x1C, 0x33), Color.FromRgb(0x24, 0x35, 0x58), 60)
            : new LinearGradientBrush(Color.FromRgb(0xC9, 0xDD, 0xF3), Color.FromRgb(0xEE, 0xF3, 0xFA), 60);
        brush.Freeze();
        return brush;
    }

    /// <summary>The right end of a taskbar: the overflow chevron, Bambu Bar's icon (lit when it changes), the network
    /// and volume icons and the clock.</summary>
    private Border Taskbar()
    {
        var tray = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement Glyph(string g) => new Border { Width = 28, Child = Icons.Glyph(g, 12, ThemeKeys.TextPrimary) };
        tray.Children.Add(Glyph(""));
        var iconCell = new Border { Width = 32, Height = 32, CornerRadius = Metrics.Control, Child = _icon };
        iconCell.SetResourceReference(BackgroundProperty, ThemeKeys.SubtleHover);
        tray.Children.Add(Wrap(Part.Icon, iconCell));
        tray.Children.Add(Glyph(""));
        tray.Children.Add(Glyph(""));
        var clock = new StackPanel { Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _clock, _date } };
        _clock.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Right;
        tray.Children.Add(clock);
        var bar = new Border { Height = 44, Child = tray, BorderThickness = new Thickness(0, 1, 0, 0) };
        bar.SetResourceReference(BackgroundProperty, WindowKeys.WindowBackground);
        bar.SetResourceReference(BorderBrushProperty, ThemeKeys.Divider);
        return bar;
    }

    private FrameworkElement Wrap(Part part, FrameworkElement content)
    {
        var lit = new Lit(content);
        _lit[part] = lit;
        return lit;
    }

    /// <summary>Shows the panel with the printer the tray icon follows: its name on the sample print.</summary>
    public void Follow(PrinterSession? session)
    {
        // The sample keeps its own name and model: your printer's name on a P2S's print misleads.
        Refresh();
    }

    public void Refresh()
    {
        var session = _demo.Current;
        var s = session?.Status;
        var live = session is not null && s is not null;
        _header.Update(true);
        _job.Update(live);
        _temperatures.Update(live && _panel.Setting(AppSettings.Keys.PanelTemperatures, true));
        _fans.Update(live && _panel.Setting(AppSettings.Keys.PanelFans, false) && FanSection.HasFans(s!));
        _filament.Update(live && _panel.Setting(AppSettings.Keys.PanelFilament, true) && (s!.Ams.Count > 0 || s.ExternalSpool is { IsLoaded: true }));
        _controls.Update(live && _ctx.Store.ShowPrinterActions && s!.State.IsActive());
        _footer.Update(true);
        foreach (var lit in _lit.Values) lit.Visibility = lit.Content.Visibility;

        var showsPercent = _ctx.Settings.Get(WindowSettings.TrayShowsPercent, false);
        var look = TrayGlyph.For(session, true, showsPercent);
        _icon.Source = TrayGlyph.Render(look, 40, lightTaskbar: !_ctx.Dark);
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        _tooltip.Text = Tray.TrayIcon.Tooltip(session);
        var now = DateTime.Now;
        _clock.Text = now.ToString("t", CultureInfo.CurrentCulture);
        _date.Text = now.ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>Lights a part up, then lets it fade; a newer change takes over.</summary>
    public void Light(Part part)
    {
        Refresh();
        if (_lighted is { } previous && previous != part) _lit[previous].Glow(false, _ctx.Animate);
        _lighted = part;
        _lit[part].Glow(true, _ctx.Animate);
        _dim.Stop();
        _dim.Start();
    }

    private void Dim()
    {
        _dim.Stop();
        if (_lighted is { } part) _lit[part].Glow(false, _ctx.Animate);
        _lighted = null;
    }

    public void Dispose()
    {
        _tick.Stop();
        _dim.Stop();
        _header.Dispose();
        _demo.Dispose();
    }

    /// <summary>A part of the preview with an accent outline and a soft glow that can light up.</summary>
    private sealed class Lit : Grid
    {
        private readonly Border _outline;

        public FrameworkElement Content { get; }

        public Lit(FrameworkElement content)
        {
            Content = content;
            _outline = new Border
            {
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(11), Margin = new Thickness(-3), IsHitTestVisible = false, Opacity = 0,
                BorderBrush = Palette.Brush(Palette.Accent),
                Effect = new DropShadowEffect { Color = Palette.Accent, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.7 },
            };
            Children.Add(content);
            Children.Add(_outline);
        }

        public void Glow(bool on, bool animate)
        {
            var to = on ? 1.0 : 0.0;
            if (!animate)
            {
                _outline.BeginAnimation(OpacityProperty, null);
                _outline.Opacity = to;
                return;
            }
            _outline.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(on ? 0.25 : 0.8)));
        }
    }
}
