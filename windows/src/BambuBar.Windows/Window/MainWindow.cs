using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using BambuBar.Core.App;
using BambuBar.Core.Protocol;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>
/// The main window (P3.5): Windows 11's Mica behind a sidebar and the pages, following the Windows app theme, at
/// least 960 × 600 (four AMS cards side by side, as on the Mac), back where it was last time. Windows 10 and high
/// contrast get a solid background and the usual title bar. Closing it only hides the app's window: Bambu Bar
/// keeps running in the tray, as on the Mac.
/// </summary>
public sealed class MainWindow : Window
{
    private const int DwmsbtMainWindow = 2;

    private readonly AppSettings _settings;
    private readonly WindowView _view;
    private readonly bool _chrome;
    private nint _hwnd;

    public MainWindow(PrinterStore store, AppSettings settings, WindowServices services)
    {
        _settings = settings;
        Title = "Bambu Bar";
        MinWidth = 960;
        MinHeight = 600;
        Width = 1040;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        _view = new WindowView(store, settings, services, AppTheme.Current, animate: true);
        _view.CloseRequested += Close;
        Content = _view;
        UseTheme();

        // Windows 11 22H2+: the content runs up under the title bar on Mica, with Windows' own caption buttons; the
        // strip at the top of the view is the caption.
        _chrome = AppTheme.HasSystemBackdrop;
        if (_chrome)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = WindowView.TitleHeight, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true,
                ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness, CornerRadius = new CornerRadius(0),
            });
            _view.CaptionReserve = 140;
        }
        // Windows 10 and older 11: Windows' own title bar says "Bambu Bar" already.
        else _view.ShowsAppName = false;

        RestoreFrame();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyBackdrop();
        };
        StateChanged += (_, _) => FitMaximized();
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, _) => _view.IsLive = IsVisible;
        Closing += (_, _) => SaveFrame();
        Closed += (_, _) =>
        {
            AppTheme.Changed -= OnThemeChanged;
            _view.Dispose();
        };
        AppTheme.Changed += OnThemeChanged;
    }

    public WindowView View => _view;

    public void Show(AppPage page) => _view.Show(page);

    public void AddPrinter(DiscoveredPrinter? candidate) => _view.AddPrinter(candidate);

    /// <summary>Brings the window forward, restoring it if minimized.</summary>
    public void Present()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_hwnd != 0) Native.SetForegroundWindow(_hwnd);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // Ctrl+, etc. Keyboard.Modifiers ignores Alt Gr combinations that type characters.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_view.HandleShortcut(key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void OnThemeChanged()
    {
        _view.ApplyTheme(AppTheme.Current);
        UseTheme();
        if (_hwnd != 0) ApplyBackdrop();
    }

    /// <summary>The window's resources are the view's, so focus rectangles (in the window's adorner layer) and popups
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
        var mica = _chrome && variant != ThemeVariant.HighContrast
            && Native.SetAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, DwmsbtMainWindow);
        if (mica && HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        if (mica) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, WindowKeys.WindowBackground);
        _view.TransparentBackground = mica;
        FitMaximized();
    }

    /// <summary>A maximized window with a custom frame hangs its resize border off the screen: pad it back in.</summary>
    private void FitMaximized()
    {
        if (!_chrome) return;
        var border = SystemParameters.WindowResizeBorderThickness;
        _view.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
    }

    // MARK: Frame

    private sealed record Frame(double Left, double Top, double Width, double Height, bool Maximized);

    /// <summary>Back where it was, at least the minimum size, and only if that place is still on a screen (a monitor may
    /// have gone since).</summary>
    private void RestoreFrame()
    {
        Frame? frame;
        try { frame = _settings.Get<Frame>(WindowSettings.Frame); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { frame = null; }
        if (frame is null || frame.Width <= 0 || frame.Height <= 0) return;
        var width = Math.Max(frame.Width, MinWidth);
        var height = Math.Max(frame.Height, MinHeight);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(frame.Left + 40, frame.Top, Math.Max(1, width - 80), WindowView.TitleHeight);
        Width = width;
        Height = height;
        if (screen.IntersectsWith(title))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = frame.Left;
            Top = frame.Top;
        }
        if (frame.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveFrame()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || double.IsInfinity(bounds.Width)) return;
        _settings.Set(WindowSettings.Frame, new Frame(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
    }
}
