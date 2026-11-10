using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>Where a panel button leads; the app opens its window there (P3.5 fills in the pages).</summary>
public enum PanelDestination { Overview, Settings, Printers, AddPrinter, Setup, License }

/// <summary>A newer release to offer above the footer (the Mac's UpdateRow). Placeholder until the Windows updater's
/// UI (P5.4) sets it.</summary>
public sealed record UpdateOffer(string Version, Action Install, Action Later);

/// <summary>What the sections share: the store, the settings, the theme, and the way out to the app.</summary>
internal sealed class PanelContext
{
    public required PrinterStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required ResourceDictionary Theme { get; init; }
    public required bool Dark { get; init; }
    /// <summary>Motion is on: the caller asked for it (not a snapshot) and Windows' "Show animations" is on, read each
    /// time, so turning it off (Settings → Accessibility → Visual effects) stops the next animation.</summary>
    public required bool Animate { get => field && AppTheme.AnimationsEnabled; init; }
    public required Action<PanelDestination, DiscoveredPrinter?> Navigate { get; init; }
    public required Action Quit { get; init; }
    public required Func<bool> ShowsAll { get; init; }
    public required Action<bool> SetShowsAll { get; init; }
    /// <summary>A section changed its height on its own (the camera unfolding): the panel window re-anchors.</summary>
    public Action? Relayout { get; init; }

    public Brush Brush(string key) => (Brush?)Theme[key] ?? Brushes.Gray;

    /// <summary>A state colour as text in this theme.</summary>
    public SolidColorBrush Ink(Color color) => Palette.Brush(Palette.Ink(color, Dark));

    public bool Setting(string key, bool fallback) => Settings.Get(key, fallback);
}

/// <summary>A part of the panel that updates its own elements in place, so focus and hover survive a report a second.</summary>
internal abstract class PanelSection(PanelContext context)
{
    protected PanelContext Ctx { get; } = context;
    protected PrinterStore Store => Ctx.Store;
    public abstract FrameworkElement Root { get; }

    /// <summary>Shows or hides the section, then fills it when shown.</summary>
    public void Update(bool visible)
    {
        Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) Fill();
    }

    protected abstract void Fill();
}

/// <summary>
/// The tray panel's content (the Mac's MenuPanel): header, then the job and its details for the selected printer, the
/// list of every printer, the setup prompt or the connection card, then update, toast, support and footer. Watches the
/// store and redraws at most once per dispatcher turn, and only while it's live (on screen or being snapshotted).
/// </summary>
public sealed class PanelView : Border, IDisposable
{
    /// <summary>The Mac's "panelShowsAllPrinters" (Core's AppSettings.Keys has no constant for it).</summary>
    public const string ShowsAllKey = "panelShowsAllPrinters";

    private readonly PrinterStore _store;
    private readonly AppSettings _settings;
    private readonly bool _animate;
    private readonly HashSet<PrinterSession> _watched = [];
    private PanelContext _ctx = null!;
    private HeaderSection _header = null!;
    private SetupSection _setup = null!;
    private FleetSection _fleet = null!;
    private JobSection _job = null!;
    private IssueSection _issue = null!;
    private CameraSection _camera = null!;
    private TemperatureSection _temperatures = null!;
    private FanSection _fans = null!;
    private FilamentSection _filament = null!;
    private ControlsSection _controls = null!;
    private ConnectionSection _connection = null!;
    private UpdateSection _update = null!;
    private ToastSection _toast = null!;
    private SupportSection _support = null!;
    private FooterSection _footer = null!;
    private bool _pending;
    private bool _live;
    private bool _transparent;

    public PanelView(PrinterStore store, AppSettings settings, ThemeVariant variant, bool animate)
    {
        _store = store;
        _settings = settings;
        _animate = animate;
        Width = Metrics.PanelWidth;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Variant = variant;
        Build();

        store.PropertyChanged += OnChanged;
        ((INotifyCollectionChanged)store.Sessions).CollectionChanged += OnSessionsChanged;
        Watch();
    }

    public event Action<PanelDestination, DiscoveredPrinter?>? Navigate;
    public event Action? QuitRequested;
    /// <summary>The content changed and may have a new height (the window re-anchors).</summary>
    public event Action? ContentChanged;

    public ThemeVariant Variant { get; private set; }
    public ResourceDictionary Theme { get; private set; } = null!;

    /// <summary>Snapshots show all printers (or one) without touching the setting.</summary>
    public bool? ShowsAllOverride { get; set; }

    /// <summary>Placeholder for the updater's offer (P5.4); null hides the row.</summary>
    public UpdateOffer? UpdateOffer { get; set; }

    /// <summary>"Support this project" shows until a supporter license is active. True until the app wires its
    /// LicenseStore (P5.3).</summary>
    public bool ShowsSupport { get; set; } = true;

    /// <summary>Over a Windows 11 backdrop the panel draws no background of its own.</summary>
    public bool TransparentBackground
    {
        get => _transparent;
        set { _transparent = value; PaintBackground(); }
    }

    /// <summary>On screen: follow the store. Off screen the panel sleeps, like the Mac's MenuPanelHost.</summary>
    public bool IsLive
    {
        get => _live;
        set
        {
            _live = value;
            if (value) Refresh();
        }
    }

    /// <summary>Animations run (Windows' setting on now, and not a snapshot).</summary>
    public bool Animates => _animate && AppTheme.AnimationsEnabled;

    /// <summary>Asks the app to open its window at <paramref name="destination"/> (Ctrl+O, the footer).</summary>
    public void Go(PanelDestination destination) => Navigate?.Invoke(destination, null);

    public void Quit() => QuitRequested?.Invoke();

    /// <summary>Ctrl+K: the selected printer's camera window, when its camera is on.</summary>
    public bool ShowCamera()
    {
        if (_store.Current is not { ShowsCamera: true } session) return false;
        CameraViews.CameraWindow.Show(session);
        return true;
    }

    private bool ShowsAll => ShowsAllOverride ?? _settings.Get(ShowsAllKey, false);

    /// <summary>The panel opens on the printer the tray icon shows (the Mac's onAppear).</summary>
    public void Opening()
    {
        if (ShowsAllOverride is null && _store.MenuBarSession is { } session) _store.Select(session.Id);
        _filament.ScrollToActiveOnNextFill();
    }

    /// <summary>Follows a change of the Windows theme: rebuilds everything with the new dictionary.</summary>
    public void ApplyTheme(ThemeVariant variant)
    {
        if (variant == Variant) return;
        Variant = variant;
        Build();
        if (_live) Refresh();
    }

    private void Build()
    {
        // A rebuild (theme change) retires the old sections' timers.
        if (_header is not null) _header.Dispose();
        if (_issue is not null) _issue.Dispose();
        Theme = ThemeResources.Create(Variant);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(Theme);
        PaintBackground();
        SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontText);
        _ctx = new PanelContext
        {
            Store = _store, Settings = _settings, Theme = Theme, Dark = ThemeResources.IsDark(Variant), Animate = _animate,
            Navigate = (d, p) => Navigate?.Invoke(d, p), Quit = () => QuitRequested?.Invoke(),
            ShowsAll = () => ShowsAll,
            SetShowsAll = on =>
            {
                if (ShowsAllOverride is not null) ShowsAllOverride = on;
                else _settings.Set(ShowsAllKey, on);
                Refresh();
            },
            Relayout = () => ContentChanged?.Invoke(),
        };
        _header = new HeaderSection(_ctx);
        _setup = new SetupSection(_ctx);
        _fleet = new FleetSection(_ctx);
        _job = new JobSection(_ctx);
        _issue = new IssueSection(_ctx);
        _camera = new CameraSection(_ctx);
        _temperatures = new TemperatureSection(_ctx);
        _fans = new FanSection(_ctx);
        _filament = new FilamentSection(_ctx);
        _controls = new ControlsSection(_ctx);
        _connection = new ConnectionSection(_ctx);
        _update = new UpdateSection(_ctx);
        _toast = new ToastSection(_ctx);
        _support = new SupportSection(_ctx);
        _footer = new FooterSection(_ctx);

        // Header and footer stay put; what's between scrolls when the screen is too short for it all.
        var body = new Stack { Spacing = Metrics.Gap, Margin = new Thickness(0, Metrics.Gap, 0, 0) };
        foreach (var section in new PanelSection[] { _setup, _fleet, _job, _issue, _camera, _temperatures, _fans, _filament, _controls, _connection })
            body.Children.Add(section.Root);
        var scroller = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Padding = new Thickness(0, 0, 0, 0),
        };
        var bottom = new Stack { Spacing = Metrics.Gap, Margin = new Thickness(0, Metrics.Gap, 0, 0) };
        foreach (var section in new PanelSection[] { _update, _toast, _support, _footer }) bottom.Children.Add(section.Root);

        var grid = new Grid { Margin = new Thickness(Metrics.PanelPadding) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.At(_header.Root, 0, 0));
        grid.Children.Add(Ui.At(scroller, 0, 1));
        grid.Children.Add(Ui.At(bottom, 0, 2));
        KeyboardNavigation.SetTabNavigation(grid, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(grid, KeyboardNavigationMode.Contained);
        Child = grid;
    }

    private void PaintBackground()
    {
        if (_transparent)
        {
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
        }
        else
        {
            SetResourceReference(BackgroundProperty, ThemeKeys.PanelBackground);
            SetResourceReference(BorderBrushProperty, ThemeKeys.PanelStroke);
            BorderThickness = new Thickness(AppTheme.IsWindows11 ? 0 : 1);
        }
    }

    /// <summary>Redraws every section from the store's current state.</summary>
    public void Refresh()
    {
        _pending = false;
        var store = _store;
        var current = store.Current;
        var status = current?.Status;
        var configured = store.IsConfigured;
        var all = ShowsAll && store.HasMultiplePrinters;
        var live = configured && !all && status is not null && current!.IsOnline;

        _header.Update(true);
        _setup.Update(!configured);
        _fleet.Update(configured && all);
        _job.Update(live);
        _issue.Update(live && status!.MostSevereIssue is { Severity: <= IssueSeverity.Common });
        _camera.Update(live && current!.ShowsCamera);
        _temperatures.Update(live && _ctx.Setting(AppSettings.Keys.PanelTemperatures, true));
        _fans.Update(live && _ctx.Setting(AppSettings.Keys.PanelFans, false) && FanSection.HasFans(status!));
        _filament.Update(live && _ctx.Setting(AppSettings.Keys.PanelFilament, true)
            && (status!.Ams.Count > 0 || status.ExternalSpool is { IsLoaded: true }));
        _controls.Update(live && store.ShowPrinterActions && status!.State.IsActive());
        _connection.Update(configured && !all && !live);
        _update.Offer = UpdateOffer;
        _update.Update(UpdateOffer is not null);
        _toast.Update(store.Toast is not null);
        _support.Update(ShowsSupport);
        _footer.Update(true);
        ContentChanged?.Invoke();
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

    /// <summary>Listens to every printer: the list mode and the header's menu show them all.</summary>
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

    public void Dispose()
    {
        _store.PropertyChanged -= OnChanged;
        ((INotifyCollectionChanged)_store.Sessions).CollectionChanged -= OnSessionsChanged;
        foreach (var session in _watched) session.PropertyChanged -= OnChanged;
        _watched.Clear();
        _header.Dispose();
        _issue.Dispose();
        _live = false;
    }
}
