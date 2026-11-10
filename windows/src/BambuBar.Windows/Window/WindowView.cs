using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>
/// The main window's content (the Mac's MainView): a title strip, the sidebar (printer picker with several printers,
/// the pages, the printer's footer with one) and the page on a layer over the backdrop, washed in the colour of the
/// printer's state. Lives in <see cref="MainWindow"/>, or alone for <c>--snapshot</c>. Follows the store while live
/// and redraws at most once per dispatcher turn.
/// </summary>
public sealed class WindowView : Border, IDisposable
{
    /// <summary>The strip at the top that is the window's caption (drag, double-click, Windows' own buttons).</summary>
    public const double TitleHeight = 40;
    private const double SidebarWidth = 248;

    private readonly PrinterStore _store;
    private readonly AppSettings _settings;
    private readonly WindowServices _services;
    private readonly bool _animate;
    private readonly HashSet<PrinterSession> _watched = [];
    private readonly Dictionary<AppPage, NavItem> _nav = [];
    private WindowContext _ctx = null!;
    private Border _pageHost = null!;
    private ScrollViewer _scroller = null!;
    private Region _picker = null!;
    private Region _footer = null!;
    private Region _supporter = null!;
    private Rectangle _wash = null!;
    private FrameworkElement _appName = null!;
    private Color? _washColor;
    private bool _showsAppName = true;
    private Page? _page;
    private string _route = "";
    private bool _pending;
    private bool _live = true;
    private bool _transparent;
    private double _captionReserve;

    public WindowView(PrinterStore store, AppSettings settings, WindowServices services, ThemeVariant variant, bool animate)
    {
        _store = store;
        _settings = settings;
        _services = services;
        _animate = animate && AppTheme.AnimationsEnabled;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Variant = variant;
        Build();
        store.PropertyChanged += OnChanged;
        ((INotifyCollectionChanged)store.Sessions).CollectionChanged += OnSessionsChanged;
        if (services.License is { } license) license.PropertyChanged += OnChanged;
        Watch();
    }

    public ThemeVariant Variant { get; private set; }
    public ResourceDictionary Theme { get; private set; } = null!;

    /// <summary>The page the sidebar has selected.</summary>
    public AppPage Section { get; private set; } = AppPage.Printer;

    /// <summary>The Printer page shows setup for another printer (the Mac's Navigation.addingPrinter).</summary>
    public bool AddingPrinter { get; private set; }

    /// <summary>A printer found on the network, picked in the panel to set up.</summary>
    public DiscoveredPrinter? SetupCandidate { get; private set; }

    /// <summary>Ctrl+W.</summary>
    public event Action? CloseRequested;

    /// <summary>Follows the store; off while the window is hidden.</summary>
    public bool IsLive
    {
        get => _live;
        set
        {
            _live = value;
            if (value) Refresh();
        }
    }

    /// <summary>Over Mica the window paints no background of its own.</summary>
    public bool TransparentBackground
    {
        get => _transparent;
        set { _transparent = value; PaintBackground(); }
    }

    /// <summary>The app's mark and name in the title strip; off where Windows draws its own title bar (Windows 10).</summary>
    public bool ShowsAppName
    {
        get => _showsAppName;
        set { _showsAppName = value; _appName.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>Room kept free at the right of the title strip for Windows' caption buttons.</summary>
    public double CaptionReserve
    {
        get => _captionReserve;
        set { _captionReserve = value; if (_supporter.Host.Parent is FrameworkElement f) f.Margin = new Thickness(0, 0, value + 12, 0); }
    }

    public void Show(AppPage page)
    {
        Section = page;
        AddingPrinter = false;
        SetupCandidate = null;
        Refresh();
    }

    /// <summary>Opens setup for another printer, prefilled with one found on the network when given.</summary>
    public void AddPrinter(DiscoveredPrinter? candidate)
    {
        Section = AppPage.Printer;
        AddingPrinter = true;
        SetupCandidate = candidate;
        Refresh();
    }

    public void ApplyTheme(ThemeVariant variant)
    {
        if (variant == Variant) return;
        Variant = variant;
        _page?.Dispose();
        _page = null;
        _route = "";
        Build();
        Refresh();
    }

    // MARK: Building

    private void Build()
    {
        Theme = WindowTheme.Create(Variant);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(Theme);
        SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontText);
        SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextPrimary);
        PaintBackground();
        var dark = ThemeResources.IsDark(Variant);
        var panel = new PanelContext
        {
            Store = _store, Settings = _settings, Theme = Theme, Dark = dark, Animate = _animate,
            Navigate = (destination, printer) => Go(destination, printer),
            Quit = () => { }, ShowsAll = () => false, SetShowsAll = _ => { },
        };
        _ctx = new WindowContext
        {
            Store = _store, Settings = _settings, Services = _services, Theme = Theme, Dark = dark, Animate = _animate,
            Show = Show, AddPrinter = AddPrinter, Panel = panel,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = TitleStrip();
        Grid.SetColumnSpan(title, 2);
        root.Children.Add(Ui.At(title, 0, 0));
        root.Children.Add(Ui.At(Sidebar(), 0, 1));
        root.Children.Add(Ui.At(Layer(), 1, 1));
        Child = root;
    }

    private void PaintBackground()
    {
        if (_transparent) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, WindowKeys.WindowBackground);
    }

    /// <summary>The app's mark and name on the left, the supporter link on the right (the Mac floats it in the title
    /// bar too); the rest is the window's caption.</summary>
    private Grid TitleStrip()
    {
        var grid = Ui.Columns("Auto", "*", "Auto");
        var mark = Icons.Glyph(Icons.Printer, 14, null, Palette.Brush(Palette.Accent));
        mark.Margin = new Thickness(16, 0, 10, 0);
        var name = Ui.Text("Bambu Bar", 12, null, ThemeKeys.TextSecondary);
        _appName = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, name }, Visibility = _showsAppName ? Visibility.Visible : Visibility.Collapsed };
        grid.Children.Add(Ui.At(_appName, 0));
        _supporter = new Region();
        var right = new Border { Child = _supporter.Host, Margin = new Thickness(0, 0, _captionReserve + 12, 0), VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(_supporter.Host, true);
        grid.Children.Add(Ui.At(right, 2));
        return grid;
    }

    private FrameworkElement Sidebar()
    {
        _picker = new Region();
        _footer = new Region();
        var list = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        _nav.Clear();
        foreach (var page in AppPages.Order)
        {
            var item = new NavItem(page);
            item.Click += (_, _) => Show(page);
            _nav[page] = item;
            list.Children.Add(item);
        }
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        AutomationProperties.SetName(list, "Bambu Bar");
        var dock = new DockPanel { LastChildFill = true };
        _picker.Host.Margin = new Thickness(12, 0, 12, 8);
        DockPanel.SetDock(_picker.Host, Dock.Top);
        DockPanel.SetDock(_footer.Host, Dock.Bottom);
        dock.Children.Add(_picker.Host);
        dock.Children.Add(_footer.Host);
        dock.Children.Add(scroller);
        return dock;
    }

    /// <summary>The page on Windows 11's content layer, with the Mac's faint wash of the state colour behind it.</summary>
    private FrameworkElement Layer()
    {
        _wash = new Rectangle { IsHitTestVisible = false };
        var accentWash = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = Radial(Palette.WithAlpha(Palette.Accent, 0.05), new Point(1, 1)),
        };
        _pageHost = new Border { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
        _scroller = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(28, 24, 28, 28), Child = _pageHost },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        var content = new Grid { Children = { _wash, accentWash, _scroller } };
        // The washes would square off the layer's rounded corner.
        content.SizeChanged += (_, e) => content.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        var layer = new Border
        {
            CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0), Child = content,
        };
        layer.SetResourceReference(BackgroundProperty, WindowKeys.LayerFill);
        layer.SetResourceReference(BorderBrushProperty, WindowKeys.LayerStroke);
        return layer;
    }

    private static RadialGradientBrush Radial(Color color, Point center)
    {
        var brush = new RadialGradientBrush(color, Palette.WithAlpha(color, 0))
        {
            MappingMode = BrushMappingMode.Absolute, Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 700, RadiusY = 700,
        };
        if (center.X > 0)
        {
            // Bottom right: relative, since the layer's size isn't known here.
            brush.MappingMode = BrushMappingMode.RelativeToBoundingBox;
            brush.Center = brush.GradientOrigin = center;
            brush.RadiusX = brush.RadiusY = 0.8;
        }
        brush.Freeze();
        return brush;
    }

    // MARK: Routing

    /// <summary>What the selected page shows now (the Mac's MainContentView.page).</summary>
    private (string Route, Func<Page> Make) Route()
    {
        var store = _store;
        var multiple = store.HasMultiplePrinters;
        var configured = store.IsConfigured;
        var id = store.Current?.Id ?? "";
        Page Printer() => new PrinterPage(_ctx);
        Page Setup() => new SetupPage(_ctx, SetupCandidate);
        return Section switch
        {
            AppPage.Fleet when multiple && !AddingPrinter => ("fleet", () => new FleetPage(_ctx)),
            AppPage.Fleet or AppPage.Printer when configured && !AddingPrinter => ("printer|" + id, Printer),
            AppPage.Fleet or AppPage.Printer => ("setup|" + SetupCandidate?.Serial, Setup),
            AppPage.Filament => ("filament", () => new FilamentPage(_ctx)),
            AppPage.Tray => ("tray", () => new TrayPage(_ctx)),
            AppPage.Notifications => ("notifications", () => new NotificationsPage(_ctx)),
            AppPage.Printers => ("printers|" + id, () => new PrintersPage(_ctx)),
            AppPage.Widgets => ("widgets", () => new WidgetsPage(_ctx)),
            AppPage.Settings => ("settings", () => new SettingsPage(_ctx)),
            _ => ("about", () => new AboutPage(_ctx)),
        };
    }

    /// <summary>Opens the page a panel button leads to.</summary>
    public void Go(PanelDestination destination, DiscoveredPrinter? printer)
    {
        switch (destination)
        {
            case PanelDestination.Settings: Show(AppPage.Settings); break;
            case PanelDestination.Printers: Show(AppPage.Printers); break;
            case PanelDestination.License: Show(AppPage.About); break;
            case PanelDestination.AddPrinter or PanelDestination.Setup: AddPrinter(printer); break;
            default: Show(AppPage.Printer); break;
        }
    }

    // MARK: Updating

    /// <summary>Redraws from the store: the sidebar, the wash, the page (a new one when the route changed).</summary>
    public void Refresh()
    {
        _pending = false;
        var (route, make) = Route();
        if (route != _route || _page is null)
        {
            _page?.Dispose();
            _route = route;
            _page = make();
            _pageHost.Child = _page.Build();
            _scroller.ScrollToTop();
        }
        FillSidebar();
        FillSupporter();
        var status = _store.Current?.Status;
        var wash = status is null ? (Color?)null : Palette.For(status);
        if (wash != _washColor || _wash.Fill is null)
        {
            _washColor = wash;
            _wash.Fill = wash is { } c ? Radial(Palette.WithAlpha(c, 0.10), new Point(0, 0)) : Brushes.Transparent;
        }
        _page.Refresh();
    }

    private void FillSidebar()
    {
        var multiple = _store.HasMultiplePrinters;
        var shown = Section == AppPage.Fleet && !multiple ? AppPage.Printer : Section;
        foreach (var (page, item) in _nav)
        {
            item.Visibility = page == AppPage.Fleet && !multiple ? Visibility.Collapsed : Visibility.Visible;
            item.Selected = page == shown;
        }
        var current = _store.Current;
        if (multiple && current is not null)
        {
            _picker.Show($"{current.Id}|{current.Config.Name}|{current.ShortState}|{Dot(current)}|{ArtKey(current)}", () => new SidebarPicker(_ctx, current));
            _footer.Hide();
        }
        else
        {
            _picker.Hide();
            var line = FooterLine();
            _footer.Show($"{current?.Id}|{_store.PrinterName}|{line}|{(current is null ? "" : ArtKey(current))}", () => Footer(current, line));
        }
    }

    private static string ArtKey(PrinterSession s) =>
        $"{s.Status?.State}|{s.Status?.Progress / 5}|{s.IsOnline}|{s.ChamberLightOn}|{s.Status?.ActiveSpool?.Color}";

    internal static Color Dot(PrinterSession session)
    {
        if (session.Status is not { } status || !session.IsOnline)
            return session.Phase is PrinterConnection.Phase.Connecting ? Palette.Paused : Palette.WithAlpha(Palette.Failed, 0.8);
        return Palette.For(status);
    }

    private string FooterLine()
    {
        if (!_store.IsConfigured) return Loc.T("Set up to begin");
        if (_store.Current is not { Status: { } s, IsOnline: true }) return _store.LinkSummary;
        return s.State.IsActive() ? $"{s.State.Title()} · {s.Progress}%" : s.State.Title();
    }

    /// <summary>The printer's name and state under the sidebar, when there is only one.</summary>
    private FrameworkElement Footer(PrinterSession? current, string line)
    {
        var art = ArtImage.Element(26, 31);
        ArtImage.Show(art, ArtImage.Scene(current?.Status, current?.Config.ModelName, current?.ChamberLightOn ?? false, dimmed: !(current?.IsOnline ?? false)));
        var name = Ui.Text(_store.IsConfigured ? _store.PrinterName : Loc.T("No printer"), 13, FontWeights.SemiBold);
        var detail = Ui.Text(line, 12, null, ThemeKeys.TextSecondary);
        var grid = Ui.Columns("Auto", "*");
        art.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(Ui.At(art, 0));
        grid.Children.Add(Ui.At(new Stack { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, detail } }, 1));
        grid.Margin = new Thickness(18, 12, 14, 14);
        AutomationProperties.SetName(grid, $"{name.Text}, {line}");
        return grid;
    }

    /// <summary>"Hero Supporter" for supporters (opens About), else "Support this project" (opens the page).</summary>
    private void FillSupporter()
    {
        var access = _services.License?.HasAccess ?? false;
        _supporter.Show(access.ToString(), () =>
        {
            var button = access
                ? W.Link(Loc.T("Hero Supporter"), () => Show(AppPage.About), "", 12)
                : W.Link(Loc.T("Support this project"), () => W.Open(PolarConfiguration.SupportUrl(PolarConfiguration.SupportPlacement.MainWindow)), "", 12);
            if (access) Ui.Tip(button, Loc.T("Thanks for supporting Bambu Bar"));
            return button;
        });
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_live || _pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => { if (_pending) Refresh(); });
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Watch();
        OnChanged(sender, new PropertyChangedEventArgs(null));
    }

    private void Watch()
    {
        var now = _store.Sessions.ToHashSet();
        foreach (var gone in _watched.Except(now).ToList())
        {
            gone.PropertyChanged -= OnChanged;
            _watched.Remove(gone);
        }
        foreach (var added in now.Except(_watched).ToList())
        {
            added.PropertyChanged += OnChanged;
            _watched.Add(added);
        }
    }

    // MARK: Keyboard

    /// <summary>The Mac's menu shortcuts with Ctrl: pages (Ctrl+0…8), Settings (Ctrl+,), Refresh (Ctrl+R), the chamber
    /// light (Ctrl+L), the camera (Ctrl+K), and Ctrl+W to close.</summary>
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control) return false;
        var digit = key switch
        {
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
            _ => '\0',
        };
        if (digit != '\0')
        {
            if (AppPages.Order.FirstOrDefault(p => p.Shortcut() == digit) is var page && page.Shortcut() == digit
                && (page != AppPage.Fleet || _store.HasMultiplePrinters))
            {
                Show(page);
                return true;
            }
            return false;
        }
        switch (key)
        {
            case Key.OemComma:
                Show(AppPage.Settings);
                return true;
            case Key.R:
                _store.Refresh();
                return true;
            case Key.L:
                if (_store.Current is { IsOnline: true, Status.ChamberLightOn: not null }) _store.ToggleLight();
                return true;
            case Key.K:
                // The camera window comes with P3.9.
                return _store.Current?.ShowsCamera == true;
            case Key.W:
                CloseRequested?.Invoke();
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _store.PropertyChanged -= OnChanged;
        ((INotifyCollectionChanged)_store.Sessions).CollectionChanged -= OnSessionsChanged;
        if (_services.License is { } license) license.PropertyChanged -= OnChanged;
        foreach (var session in _watched) session.PropertyChanged -= OnChanged;
        _watched.Clear();
        _page?.Dispose();
        _page = null;
        _live = false;
    }

    /// <summary>One page in the sidebar: Windows' NavigationView item, with the accent bar when selected.</summary>
    private sealed class NavItem : ChromeButton
    {
        private readonly Border _indicator;
        private bool _selected;

        public NavItem(AppPage page)
        {
            _indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
            _indicator.SetResourceReference(BackgroundProperty, ThemeKeys.AccentFill);
            var icon = Icons.Glyph(page.Glyph(), 16, ThemeKeys.TextPrimary);
            icon.Margin = new Thickness(10, 0, 14, 0);
            var label = Ui.Text(page.Title(), WRamp.Body);
            var grid = Ui.Columns("Auto", "Auto", "*");
            grid.Children.Add(Ui.At(_indicator, 0));
            grid.Children.Add(Ui.At(icon, 1));
            grid.Children.Add(Ui.At(label, 2));
            Content = grid;
            Height = 36;
            Margin = new Thickness(0, 2, 0, 2);
            Padding = new Thickness(0, 0, 8, 0);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var shortcut = $"Ctrl+{page.Shortcut()}";
            AutomationProperties.SetName(this, page.Title());
            AutomationProperties.SetAcceleratorKey(this, shortcut);
            Ui.Tip(this, $"{page.Title()} ({shortcut})");
            Paint();
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Paint();
            }
        }

        private void Paint()
        {
            _indicator.Visibility = _selected ? Visibility.Visible : Visibility.Hidden;
            Look = _selected
                ? new ButtonLook(WindowKeys.NavSelected, WindowKeys.NavSelected, WindowKeys.NavPressed)
                : new ButtonLook(null, WindowKeys.NavHover, WindowKeys.NavPressed);
            AutomationProperties.SetItemStatus(this, _selected ? Loc.T("Showing") : "");
        }
    }
}

/// <summary>The printer switcher at the top of the sidebar (the Mac's SidebarPrinterPicker): the drawn printer, its
/// name and state; a click lists every printer and "Add Printer…".</summary>
internal sealed class SidebarPicker : ChromeButton
{
    public SidebarPicker(WindowContext ctx, PrinterSession current)
    {
        var art = ArtImage.Element(26, 31);
        ArtImage.Show(art, ArtImage.Scene(current.Status, current.Config.ModelName, current.ChamberLightOn, dimmed: !current.IsOnline));
        art.Margin = new Thickness(0, 0, 10, 0);
        var name = Ui.Text(current.Config.Name, 13, FontWeights.SemiBold);
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Palette.Brush(WindowView.Dot(current)), Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var state = Ui.Text(current.ShortState, 12, null, ThemeKeys.TextSecondary);
        var line = Ui.Columns("Auto", "*");
        line.Children.Add(Ui.At(dot, 0));
        line.Children.Add(Ui.At(state, 1));
        var chevrons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        chevrons.Children.Add(Icons.Glyph("", 8));
        chevrons.Children.Add(Icons.Glyph("", 8));
        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(art, 0));
        grid.Children.Add(Ui.At(new Stack { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, line } }, 1));
        grid.Children.Add(Ui.At(chevrons, 2));
        Content = grid;
        Look = ButtonLook.Card;
        CornerRadius = Metrics.Tile;
        Padding = new Thickness(10, 8, 10, 8);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, current.Config.Name);
        AutomationProperties.SetItemStatus(this, current.ShortState);
        Ui.Tip(this, Loc.T("Choose a printer"));
        Click += (_, _) =>
        {
            var store = ctx.Store;
            var entries = store.Sessions.Select(s => new MenuEntry(Loc.F("%@ — %@", s.Config.Name, s.ShortState), () => store.Select(s.Id),
                Checked: s.Id == store.Current?.Id)).ToList();
            entries.Add(MenuEntry.Separator);
            entries.Add(new MenuEntry(Loc.T("Add Printer…"), () => ctx.AddPrinter(null)));
            MenuPopup.Show(this, ctx.Theme, entries);
        };
    }
}
