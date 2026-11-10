using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.CameraViews;

/// <summary>
/// What the camera window holds (the Mac's CameraWindowView): the picture edge to edge, and, while the pointer is over
/// it, a bar with the print's progress, the stream's details and the "keep on top" pin. Separate from the window so
/// <c>--snapshot</c> can draw it.
/// </summary>
public sealed class CameraWindowView : Grid
{
    private readonly CameraFeed _feed;
    private readonly Func<string> _statusText;
    private readonly CameraPlayer _player;
    private readonly Border _bar;
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ChromeButton _pin;
    private readonly Func<bool> _onTop;
    private readonly Action<bool> _setOnTop;
    private bool _barShown;

    /// <param name="status">The bar's text, read again whenever <paramref name="statusSource"/> changes.</param>
    public CameraWindowView(CameraFeed feed, string printerName, Func<string> status, INotifyPropertyChanged? statusSource,
        Func<bool> onTop, Action<bool> setOnTop)
    {
        _feed = feed;
        _statusText = status;
        _onTop = onTop;
        _setOnTop = setOnTop;
        Background = Brushes.Black;
        _player = new CameraPlayer(compact: false, cornerRadius: 0);
        _player.Show(feed, printerName);
        var white = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        _status = new TextBlock { Foreground = white, FontSize = 12.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Tabular(_status);
        _detail = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        _pin = new ChromeButton
        {
            Width = 28, Height = 28, Padding = new Thickness(0), Foreground = white,
            Look = ButtonLook.Tinted(Colors.White, 0, 0.16, 0.24), CornerRadius = new CornerRadius(14),
        };
        _pin.Click += (_, _) =>
        {
            _setOnTop(!_onTop());
            ShowPin();
        };
        var row = Ui.Columns("*", "Auto", "Auto");
        row.Children.Add(Ui.At(_status, 0));
        row.Children.Add(Ui.At(_detail, 1));
        row.Children.Add(Ui.At(_pin, 2));
        _bar = new Border
        {
            Child = row, Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 4, 4, 4), Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
            IsHitTestVisible = false,
        };
        Children.Add(_player);
        Children.Add(_bar);
        MouseEnter += (_, _) => ShowBar(true);
        MouseLeave += (_, _) => ShowBar(false);
        Loaded += (_, _) =>
        {
            if (statusSource is not null) statusSource.PropertyChanged += OnChanged;
            feed.PropertyChanged += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (statusSource is not null) statusSource.PropertyChanged -= OnChanged;
            feed.PropertyChanged -= OnChanged;
        };
        Refresh();
        ShowPin();
    }

    /// <summary>Snapshots show the bar without a pointer.</summary>
    public void ShowBar(bool shown)
    {
        if (shown == _barShown) return;
        _barShown = shown;
        _bar.IsHitTestVisible = shown;
        if (AppTheme.AnimationsEnabled && IsLoaded) _bar.BeginAnimation(OpacityProperty, new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        else
        {
            _bar.BeginAnimation(OpacityProperty, null);
            _bar.Opacity = shown ? 1 : 0;
        }
    }

    /// <summary>Lets go of the feed (the window closed).</summary>
    public void Release() => _player.Show(null, null);

    public void ShowPin()
    {
        var on = _onTop();
        _pin.Content = Icons.Glyph(on ? Icons.Pinned : Icons.Pin, 13, null, _pin.Foreground);
        var label = Loc.T("Keep on top");
        AutomationProperties.SetName(_pin, label);
        AutomationProperties.SetItemStatus(_pin, on ? Loc.T("On") : Loc.T("Off"));
        Ui.Tip(_pin, label);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _status.Text = _statusText();
        _detail.Text = _feed.Detail ?? "";
    }

    /// <summary>"Printing · 64% · 1h 20m left", or the state ("Ready"), while connected.</summary>
    public static string Status(PrinterSession session)
    {
        if (session.Status is not { } status || !session.IsOnline) return session.LinkSummary;
        return status.State.IsActive()
            ? $"{status.State.Title()} · {status.Progress}% · {Loc.F("%@ left", Format.Duration(status.RemainingMinutes))}"
            : status.StateTitle;
    }
}

/// <summary>
/// A resizable window per printer with just the camera, kept above other windows (picture in picture) unless its pin
/// says otherwise (the Mac's CameraWindow, whose window always floats). The picture fills it, fitted; dragging an edge
/// keeps the picture's shape, as on the Mac. It comes back where and as large as it was, per printer. Closing it lets
/// go of the feed, which stops streaming 4 s later unless something else shows it.
/// </summary>
public sealed class CameraWindow : Window
{
    private const int WM_SIZING = 0x0214;
    private const int WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6;
    /// <summary>The window floats above others (the Mac's always does); the pin turns it off.</summary>
    public const string OnTopKey = "cameraWindowOnTop";
    private static readonly Dictionary<string, CameraWindow> Windows = [];

    private readonly PrinterSession _session;
    private readonly AppSettings _settings;
    private readonly CameraWindowView _view;
    private nint _hwnd;

    /// <summary>Where the windows keep their frames and the pin; the app sets it at start.</summary>
    public static AppSettings? Settings { get; set; }

    /// <summary>Opens the printer's camera window, or brings it forward (Ctrl+K, the pop-out buttons, the tray menu).</summary>
    public static void Show(PrinterSession session)
    {
        if (!session.ShowsCamera) return;
        if (!Windows.TryGetValue(session.Id, out var window))
        {
            window = new CameraWindow(session, Settings ?? new AppSettings(null));
            Windows[session.Id] = window;
        }
        window.Present();
    }

    /// <summary>Closes the windows of printers that were forgotten or whose camera is now off.</summary>
    public static void CloseWhereHidden(PrinterStore store)
    {
        foreach (var (serial, window) in Windows.ToList())
        {
            if (store.Session(serial) is not { ShowsCamera: true } session || !ReferenceEquals(session, window._session)) window.Close();
        }
    }

    public static void CloseAll()
    {
        foreach (var window in Windows.Values.ToList()) window.Close();
    }

    private sealed record Frame(double Left, double Top, double Width, double Height);

    private static string FrameKey(string serial) => $"cameraWindowFrame.{serial}";

    private CameraWindow(PrinterSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        Background = Brushes.Black;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        MinWidth = 320;
        MinHeight = 200;
        Topmost = settings.Get(OnTopKey, true);
        _view = new CameraWindowView(session.Camera, session.Config.Name, () => CameraWindowView.Status(session), session, () => Topmost, on =>
        {
            Topmost = on;
            _settings.Set(OnTopKey, on);
        });
        Content = _view;
        Retitle();
        if (!RestoreFrame())
        {
            // 800 wide, and as tall as the picture once the frame around it is known (Loaded).
            Width = 800;
            Height = Math.Round(800 / session.Camera.AspectRatio) + SystemParameters.CaptionHeight + 2 * SystemParameters.ResizeFrameHorizontalBorderHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Loaded += (_, _) => FitToPicture();
        }
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // The picture is dark, so is the title bar.
            Native.SetAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
            {
                Close();
                e.Handled = true;
            }
        };
        session.PropertyChanged += OnSessionChanged;
        session.Camera.PropertyChanged += OnFeedChanged;
        Closing += (_, _) => SaveFrame();
        Closed += (_, _) =>
        {
            session.PropertyChanged -= OnSessionChanged;
            session.Camera.PropertyChanged -= OnFeedChanged;
            _view.Release();
            if (Windows.TryGetValue(session.Id, out var open) && ReferenceEquals(open, this)) Windows.Remove(session.Id);
        };
    }

    private void Present()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_hwnd != 0) Native.SetForegroundWindow(_hwnd);
    }

    private void Retitle() => Title = Loc.F("%@ camera", _session.Config.Name);

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(PrinterSession.Config)) Retitle();
    }

    /// <summary>The stream says what shape its picture is: the window follows, keeping its width (the Mac's
    /// setAspectRatio).</summary>
    private void OnFeedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CameraFeed.AspectRatio)) FitToPicture();
    }

    private void FitToPicture()
    {
        if (WindowState != WindowState.Normal || !IsLoaded || _view.ActualWidth < 1) return;
        var chrome = ActualHeight - _view.ActualHeight;
        Height = Math.Round(_view.ActualWidth / _session.Camera.AspectRatio) + chrome;
    }

    /// <summary>Dragging an edge or corner keeps the picture's shape (the Mac's contentAspectRatio).</summary>
    private unsafe nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WM_SIZING || lParam == 0) return 0;
        var ratio = _session.Camera.AspectRatio;
        if (!double.IsFinite(ratio) || ratio <= 0) return 0;
        if (!Native.GetWindowRect(hwnd, out var window) || !Native.GetClientRect(hwnd, out var client)) return 0;
        var extraWidth = window.Width - client.Width;
        var extraHeight = window.Height - client.Height;
        ref var rect = ref *(Native.RECT*)lParam;
        var edge = (int)wParam;
        if (edge is WMSZ_TOP or WMSZ_BOTTOM)
        {
            // The height leads: the width follows to the right.
            var width = (int)Math.Round((rect.Height - extraHeight) * ratio) + extraWidth;
            rect.Right = rect.Left + width;
        }
        else
        {
            // The width leads (sides and corners): the height follows, at the edge being dragged.
            var height = (int)Math.Round((rect.Width - extraWidth) / ratio) + extraHeight;
            if (edge is WMSZ_TOPLEFT or WMSZ_TOPRIGHT) rect.Top = rect.Bottom - height;
            else rect.Bottom = rect.Top + height;
        }
        handled = true;
        return 1;
    }

    /// <summary>Back where it was for this printer, if that place is still on a screen.</summary>
    private bool RestoreFrame()
    {
        Frame? frame;
        try { frame = _settings.Get<Frame>(FrameKey(_session.Id)); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { frame = null; }
        if (frame is null || frame.Width < MinWidth || frame.Height < MinHeight) return false;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Width = frame.Width;
        Height = frame.Height;
        if (!screen.IntersectsWith(new Rect(frame.Left + 40, frame.Top, Math.Max(1, frame.Width - 80), 30)))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return true;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = frame.Left;
        Top = frame.Top;
        return true;
    }

    private void SaveFrame()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || bounds.Width <= 0) return;
        _settings.Set(FrameKey(_session.Id), new Frame(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
    }
}
