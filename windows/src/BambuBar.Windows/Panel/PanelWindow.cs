using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>
/// The flyout that holds the <see cref="PanelView"/> (ledger D2, P3.3): borderless, 372 DIPs wide, anchored above the
/// tray icon on whichever monitor and taskbar edge it was clicked, in that monitor's DPI. It closes when it loses
/// activation (light dismiss), on Esc, and on a second click of the tray icon. On Windows 11 22H2+ it's acrylic
/// (DWM's transient-window backdrop) with rounded corners; elsewhere, and in high contrast, a solid surface.
/// </summary>
public sealed class PanelWindow : Window
{
    /// <summary>Within this long after a light dismiss, a tray click is the one that caused it: stay closed.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(450);

    private readonly PanelView _view;
    private readonly TranslateTransform _slide = new();
    private nint _hwnd;
    private bool _backdrop;
    private Native.POINT _anchor;
    private uint _edge = Native.ABE_BOTTOM;
    private DateTime _dismissedAt = DateTime.MinValue;
    private int _holds;
    private bool _placing;
    private bool _placePending;
    private bool _quitting;

    public PanelWindow(PanelView view)
    {
        _view = view;
        Title = "Bambu Bar";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = Metrics.PanelWidth;
        FocusVisualStyle = null;
        // The caption and borders go; DWM keeps the shadow, the rounded corners and (with glass) the backdrop.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            GlassFrameThickness = AppTheme.HasSystemBackdrop ? new Thickness(-1) : new Thickness(0), UseAeroCaptionButtons = false,
        });
        _view.RenderTransform = _slide;
        Content = _view;
        UseTheme();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            // No system menu: with it, Windows draws a close button over the header (on the settings button, where a
            // click never reached it). The flyout closes on Esc, Alt+F4 and a click outside.
            var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
            Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (nint)(style & ~(Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX)));
            ApplyBackdrop();
        };
        Deactivated += (_, _) =>
        {
            if (_holds > 0 || !IsVisible || _priming) return;
            _dismissedAt = DateTime.UtcNow;
            HidePanel();
        };
        KeyDown += OnKeyDown;
        DpiChanged += (_, _) => SchedulePlace();
        _view.ContentChanged += SchedulePlace;
        _view.HideRequested += HidePanel;
        AppTheme.Changed += OnThemeChanged;
    }

    public ThemeVariant Variant => _view.Variant;

    /// <summary>The tray icon was clicked: open, or close if open (or if that very click just dismissed it).</summary>
    public void Toggle()
    {
        // While it's being drawn out of sight (PrimeAsync), a click opens it for real.
        if (IsVisible && !_priming) HidePanel();
        else if (DateTime.UtcNow - _dismissedAt > ReopenGuard) ShowPanel();
    }

    public void ShowPanel()
    {
        _shownByClick = true;
        if (!Native.GetCursorPos(out _anchor)) _anchor = default;
        _view.Opening();
        _view.IsLive = true;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Place(moveToMonitor: true, force: true);
        Show();
        Activate();
        if (_hwnd != 0) Native.SetForegroundWindow(_hwnd);
        // Keys (Esc, Tab, Ctrl+O) reach the panel before anything inside has been clicked.
        Focus();
        // Show() may have put it where WPF thought best; this is the word that counts.
        Place(moveToMonitor: false, force: true);
        AnimateIn();
    }

    public void HidePanel()
    {
        _shownByClick = false;
        _view.IsLive = false;
        Hide();
    }

    /// <summary>
    /// Draws the panel once out of sight (<see cref="PanelWarmup"/>), so the first click finds its layout, its first
    /// frame and the 3D view already made, and opens as fast as every later click (measured: 665 ms → under 100 ms). The
    /// window shows far off every screen, not activated, until the 3D printer is live (at most 5 s), then hides.
    /// </summary>
    public async Task PrimeAsync()
    {
        if (IsVisible || _quitting) return;
        var activated = ShowActivated;
        ShowActivated = false;
        Left = Top = -32000;
        _view.Opening();
        _view.IsLive = true;
        _priming = true;
        try
        {
            Show();
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            do await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            while (DateTime.UtcNow < until && !_quitting && IsVisible && !Model3D.Renderer3D.IsOff && !ModelsLive(_view));
        }
        finally
        {
            _priming = false;
            ShowActivated = activated;
            // A click that came in meanwhile opened it for real: leave it open.
            if (!_shownByClick)
            {
                _view.IsLive = false;
                Hide();
            }
        }
    }

    private bool _priming;
    private bool _shownByClick;

    private static bool ModelsLive(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        var live = true;
        var any = false;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Model3D.PrinterModelView view)
            {
                if (view.IsVisible) { any = true; live &= view.IsLive; }
                continue;
            }
            if (!ModelsLive(child)) return false;
        }
        return !any || live;
    }

    /// <summary>Keeps the panel open while a dialog it opened (the stop confirmation) has the focus.</summary>
    public IDisposable HoldOpen()
    {
        _holds++;
        return new Release(() =>
        {
            _holds = Math.Max(0, _holds - 1);
            if (IsVisible) Activate();
        });
    }

    /// <summary>Closes for good (the app is quitting).</summary>
    public void Quit()
    {
        _quitting = true;
        AppTheme.Changed -= OnThemeChanged;
        _view.Dispose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides the flyout; only quitting closes it.
        if (!_quitting)
        {
            e.Cancel = true;
            HidePanel();
        }
        base.OnClosing(e);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape || (e.Key == Key.System && e.SystemKey == Key.F4))
        {
            HidePanel();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            _view.Go(PanelDestination.Overview);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            e.Handled = _view.ShowCamera();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Q)
        {
            _view.Quit();
            e.Handled = true;
        }
    }

    private void OnThemeChanged()
    {
        _view.ApplyTheme(AppTheme.Current);
        UseTheme();
        if (_hwnd != 0) ApplyBackdrop();
    }

    /// <summary>The window shares the panel's dictionary, so focus rectangles (drawn in the window's adorner layer)
    /// find their brushes.</summary>
    private void UseTheme()
    {
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(_view.Theme);
    }

    private void ApplyBackdrop()
    {
        var variant = _view.Variant;
        Native.SetAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, variant == ThemeVariant.Dark ? 1 : 0);
        if (AppTheme.IsWindows11) Native.SetAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        _backdrop = variant != ThemeVariant.HighContrast && AppTheme.HasSystemBackdrop
            && Native.SetAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_TRANSIENTWINDOW);
        if (_backdrop)
        {
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            try { Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _backdrop = false; }
        }
        if (_backdrop) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, ThemeKeys.PanelBackground);
        _view.TransparentBackground = _backdrop;
    }

    /// <summary>Swallows moving, sizing, minimizing and maximizing (Alt+Space, Win+arrows): a flyout stays put.</summary>
    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WM_SYSCOMMAND)
        {
            var command = (int)(wParam.ToInt64() & 0xFFF0);
            if (command is Native.SC_MOVE or Native.SC_SIZE or Native.SC_MINIMIZE or Native.SC_MAXIMIZE or Native.SC_RESTORE) handled = true;
        }
        return 0;
    }

    private void SchedulePlace()
    {
        if (!IsVisible || _placing || _placePending) return;
        _placePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _placePending = false;
            if (IsVisible) Place(moveToMonitor: false, force: false);
        });
    }

    /// <summary>Sizes the window to its content (capped to the work area; the middle scrolls past that) and puts it
    /// against the taskbar, centred on the tray icon where the click came from the taskbar, else in the corner.</summary>
    private void Place(bool moveToMonitor, bool force)
    {
        if (_hwnd == 0 || _placing) return;
        _placing = true;
        try
        {
            if (Native.MonitorAt(_anchor) is not { } screen) return;
            var (monitor, work) = screen;
            // Onto the clicked monitor first, so WPF adopts its DPI (per-monitor v2) before the size is worked out.
            if (moveToMonitor) Native.SetWindowPos(_hwnd, 0, work.Left, work.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            var dpi = Native.GetDpiForWindow(_hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
            var margin = (int)Math.Round(Metrics.ScreenMargin * scale);
            var (edge, limit) = Edge(monitor, work);
            _edge = edge;

            var maxHeight = Math.Max(200, (limit.Height - 2 * margin) / scale);
            _view.Measure(new Size(Metrics.PanelWidth, double.PositiveInfinity));
            var height = Math.Ceiling(Math.Min(_view.DesiredSize.Height, maxHeight));
            if (!force && Math.Abs(height - Height) < 0.5) return;
            var w = (int)Math.Round(Metrics.PanelWidth * scale);
            var h = (int)Math.Round(height * scale);

            // From the taskbar (or just above it, in the overflow flyout) the panel centres on the icon; from anywhere
            // else (a keyboard shortcut) it goes to the corner by the clock.
            var band = (int)(240 * scale);
            int x, y;
            int Along(int anchor, int low, int high, int size, bool near) =>
                Math.Clamp(near ? anchor - size / 2 : high - size - margin, low + margin, Math.Max(low + margin, high - size - margin));
            switch (edge)
            {
                case Native.ABE_TOP:
                    x = Along(_anchor.X, limit.Left, limit.Right, w, _anchor.Y <= limit.Top + band);
                    y = limit.Top + margin;
                    break;
                case Native.ABE_LEFT:
                    x = limit.Left + margin;
                    y = Along(_anchor.Y, limit.Top, limit.Bottom, h, _anchor.X <= limit.Left + band);
                    break;
                case Native.ABE_RIGHT:
                    x = limit.Right - w - margin;
                    y = Along(_anchor.Y, limit.Top, limit.Bottom, h, _anchor.X >= limit.Right - band);
                    break;
                default:
                    x = Along(_anchor.X, limit.Left, limit.Right, w, _anchor.Y >= limit.Bottom - band);
                    y = limit.Bottom - h - margin;
                    break;
            }
            Width = Metrics.PanelWidth;
            Height = height;
            Left = x / scale;
            Top = y / scale;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>Which edge the taskbar is on for this monitor, and the area the panel may use. A taskbar that hides
    /// itself leaves the work area whole; then the shell says where it pops up.</summary>
    private static (uint Edge, Native.RECT Limit) Edge(Native.RECT monitor, Native.RECT work)
    {
        if (work.Bottom < monitor.Bottom) return (Native.ABE_BOTTOM, work);
        if (work.Top > monitor.Top) return (Native.ABE_TOP, work);
        if (work.Left > monitor.Left) return (Native.ABE_LEFT, work);
        if (work.Right < monitor.Right) return (Native.ABE_RIGHT, work);
        var limit = work;
        if (Native.Taskbar() is { } bar && bar.Rect.Left < monitor.Right && bar.Rect.Right > monitor.Left
            && bar.Rect.Top < monitor.Bottom && bar.Rect.Bottom > monitor.Top)
        {
            switch (bar.Edge)
            {
                case Native.ABE_TOP: limit.Top = Math.Max(limit.Top, bar.Rect.Bottom); break;
                case Native.ABE_LEFT: limit.Left = Math.Max(limit.Left, bar.Rect.Right); break;
                case Native.ABE_RIGHT: limit.Right = Math.Min(limit.Right, bar.Rect.Left); break;
                default: limit.Bottom = Math.Min(limit.Bottom, bar.Rect.Top); break;
            }
            return (bar.Edge, limit);
        }
        return (Native.ABE_BOTTOM, limit);
    }

    /// <summary>Fluent's entrance: a short fade while sliding in from the taskbar.</summary>
    private void AnimateIn()
    {
        if (!_view.Animates) return;
        var (property, from) = _edge switch
        {
            Native.ABE_TOP => (TranslateTransform.YProperty, -16.0),
            Native.ABE_LEFT => (TranslateTransform.XProperty, -16.0),
            Native.ABE_RIGHT => (TranslateTransform.XProperty, 16.0),
            _ => (TranslateTransform.YProperty, 16.0),
        };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.BeginAnimation(property, new DoubleAnimation(from, 0, Metrics.Entrance) { EasingFunction = ease });
        _view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Metrics.Fast));
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
