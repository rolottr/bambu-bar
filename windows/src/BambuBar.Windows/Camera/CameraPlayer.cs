using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Theme;
using State = BambuBar.Core.App.CameraFeed.FeedState;

namespace BambuBar.CameraViews;

/// <summary>
/// One feed's pictures as a bitmap every player of that feed shows, so a picture is copied once however many places
/// show it (the panel and the camera window at once). Lives as long as the feed.
/// </summary>
internal sealed class CameraPicture
{
    private static readonly ConditionalWeakTable<CameraFeed, CameraPicture> All = new();
    private readonly CameraFeed _feed;

    private CameraPicture(CameraFeed feed)
    {
        _feed = feed;
        feed.FrameChanged += Update;
        Update();
    }

    public static CameraPicture For(CameraFeed feed) => All.GetValue(feed, f => new CameraPicture(f));

    /// <summary>The picture; null before the first one and after the feed stops.</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary><see cref="Bitmap"/> was replaced (a new size) or dropped; its contents change without notice.</summary>
    public event Action? Replaced;

    private void Update()
    {
        if (_feed.Frame is not { } frame)
        {
            if (Bitmap is null) return;
            Bitmap = null;
            Replaced?.Invoke();
            return;
        }
        if (Bitmap is null || Bitmap.PixelWidth != frame.Width || Bitmap.PixelHeight != frame.Height)
        {
            // Bgr32: the decoder's BGRA with the alpha byte ignored (it's always opaque), no premultiplying.
            Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            Replaced?.Invoke();
        }
        Bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
    }
}

/// <summary>
/// The camera with its state on top (the Mac's CameraPlayer and CameraSurfaceView): the picture fitted to the frame,
/// and over it a spinner while connecting, or why it isn't showing, with Try Again where trying again could help.
/// It is one of its feed's viewers while it's loaded, and tells the feed whenever it may have come into or gone out of
/// sight (shown, hidden, minimized, scrolled, the session locked), at most once per dispatcher turn: a picture nobody
/// can see isn't streamed.
/// </summary>
public sealed class CameraPlayer : Border, ICameraViewer
{
    private static readonly List<WeakReference<CameraPlayer>> Players = [];
    private static bool _sessionLocked;

    private readonly bool _compact;
    private readonly Image _image;
    private readonly Grid _overlay;
    private readonly Stack _message;
    private readonly RectangleGeometry _clip = new();
    private CameraFeed? _source;
    private CameraFeed? _feed;
    private CameraPicture? _picture;
    private bool _loaded;
    private bool _checkPending;
    private Window? _window;
    private readonly List<ScrollViewer> _scrollers = [];
    private string _overlayKey = "";

    /// <summary>Snapshots draw players that are in no window: they count as on screen.</summary>
    public static bool ForceOnScreen { get; set; }

    /// <summary>The Windows session was locked or unlocked: nobody sees the screen while it's locked (the Mac's
    /// occlusion state covers that).</summary>
    public static void SessionLocked(bool locked)
    {
        _sessionLocked = locked;
        lock (Players)
        {
            Players.RemoveAll(p => !p.TryGetTarget(out _));
            foreach (var reference in Players) if (reference.TryGetTarget(out var player)) player.ScheduleCheck();
        }
    }

    /// <param name="compact">Smaller type, for the tray panel.</param>
    public CameraPlayer(bool compact, double cornerRadius)
    {
        _compact = compact;
        Background = Brushes.Black;
        CornerRadius = new CornerRadius(cornerRadius);
        _clip.RadiusX = _clip.RadiusY = cornerRadius;
        Clip = _clip;
        SnapsToDevicePixels = true;
        _image = new Image { Stretch = Stretch.Uniform };
        // Fant: a 1080p picture shrunk to the panel's width stays smooth.
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _message = new Stack { Spacing = compact ? 6 : 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420 };
        _message.Margin = new Thickness(compact ? 12 : 20);
        _overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), Children = { _message } };
        Child = new Grid { Children = { _image, _overlay } };
        SizeChanged += (_, _) =>
        {
            _clip.Rect = new Rect(RenderSize);
            ScheduleCheck();
        };
        IsVisibleChanged += (_, _) => ScheduleCheck();
        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();
        lock (Players) Players.Add(new WeakReference<CameraPlayer>(this));
    }

    /// <summary>The printer whose camera this shows.</summary>
    public PrinterSession? Session
    {
        set => Show(value?.Camera, value?.Config.Name);
    }

    /// <summary>Shows <paramref name="feed"/> (none: nothing), named for screen readers after the printer.</summary>
    public void Show(CameraFeed? feed, string? printerName)
    {
        AutomationProperties.SetName(this, printerName is null ? "" : Loc.F("%@ camera", printerName));
        if (ReferenceEquals(feed, _source)) return;
        Disconnect();
        _source = feed;
        // Snapshots draw it in no window, so it never loads: it watches at once.
        if (_loaded || ForceOnScreen) Connect();
        ShowPicture();
        ShowState();
    }

    /// <summary>In a shown, unminimized window, visible, and inside every scroll viewer around it.</summary>
    public bool IsOnScreen
    {
        get
        {
            if (ForceOnScreen) return _feed is not null && !_sessionLocked;
            if (!_loaded || _sessionLocked || !IsVisible || ActualWidth < 1 || ActualHeight < 1) return false;
            if (_window is not { IsVisible: true } window || window.WindowState == WindowState.Minimized) return false;
            foreach (var scroller in _scrollers)
            {
                try
                {
                    var bounds = TransformToAncestor(scroller).TransformBounds(new Rect(RenderSize));
                    if (!bounds.IntersectsWith(new Rect(0, 0, scroller.ActualWidth, scroller.ActualHeight))) return false;
                }
                catch (InvalidOperationException)
                {
                    return false; // no longer inside it
                }
            }
            return true;
        }
    }

    private void OnLoaded()
    {
        _loaded = true;
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.IsVisibleChanged += OnWindowVisibleChanged;
        }
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller)
            {
                scroller.ScrollChanged += OnScrolled;
                _scrollers.Add(scroller);
            }
        }
        Connect();
    }

    private void OnUnloaded()
    {
        _loaded = false;
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.IsVisibleChanged -= OnWindowVisibleChanged;
            _window = null;
        }
        foreach (var scroller in _scrollers) scroller.ScrollChanged -= OnScrolled;
        _scrollers.Clear();
        Disconnect();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => ScheduleCheck();
    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleCheck();
    private void OnScrolled(object sender, ScrollChangedEventArgs e) => ScheduleCheck();

    private void Connect()
    {
        if (_source is null || _feed is not null) return;
        _feed = _source;
        _picture = CameraPicture.For(_feed);
        _picture.Replaced += ShowPicture;
        _feed.PropertyChanged += OnFeedChanged;
        _feed.Attach(this);
        ShowPicture();
        ShowState();
    }

    private void Disconnect()
    {
        if (_feed is null) return;
        var feed = _feed;
        _feed.PropertyChanged -= OnFeedChanged;
        if (_picture is not null) _picture.Replaced -= ShowPicture;
        _feed = null;
        _picture = null;
        feed.Detach(this);
    }

    /// <summary>Tells the feed once per turn of the dispatcher (scrolling sends many events).</summary>
    private void ScheduleCheck()
    {
        if (_checkPending || _feed is null) return;
        _checkPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _checkPending = false;
            _feed?.ViewersChanged();
        });
    }

    private void OnFeedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(CameraFeed.State)) ShowState();
    }

    private void ShowPicture() => _image.Source = _picture?.Bitmap;

    private void ShowState()
    {
        var state = _source?.State ?? new State.Idle();
        var key = state.ToString();
        if (key == _overlayKey) return;
        _overlayKey = key;
        switch (state)
        {
            case State.Live:
                _overlay.Visibility = Visibility.Collapsed;
                return;
            case State.Unavailable u:
                Message(Icons.Camera, u.Reason, busy: false, retry: false);
                break;
            case State.Failed f:
                Message(Icons.Warning, f.Reason, busy: f.Retrying, retry: !f.Retrying);
                break;
            default:
                Message(null, Loc.T("Connecting to the camera…"), busy: true, retry: false);
                break;
        }
        _overlay.Visibility = Visibility.Visible;
    }

    private void Message(string? glyph, string text, bool busy, bool retry)
    {
        var white = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        _message.Children.Clear();
        if (busy)
        {
            _message.Children.Add(new Spinner(_compact ? 16 : 20, AppTheme.AnimationsEnabled) { HorizontalAlignment = HorizontalAlignment.Center });
        }
        else if (glyph is not null)
        {
            _message.Children.Add(Icons.Glyph(glyph, _compact ? 16 : 20, null, white));
        }
        var label = new TextBlock
        {
            Text = text, Foreground = white, FontSize = _compact ? 11.5 : 13, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _message.Children.Add(label);
        if (retry)
        {
            var button = new ChromeButton
            {
                Look = ButtonLook.Tinted(Colors.White, 0.16, 0.24, 0.32), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                Content = new TextBlock { Text = Loc.T("Try Again"), FontSize = _compact ? 11.5 : 12.5 }, Padding = new Thickness(12, 3, 12, 4),
            };
            AutomationProperties.SetName(button, Loc.T("Try Again"));
            button.Click += (_, _) => _feed?.TryAgain();
            _message.Children.Add(button);
        }
        AutomationProperties.SetHelpText(this, text);
    }
}

/// <summary>Full width of whatever holds it, as tall as the picture's aspect ratio makes it (the Mac's CameraFrame).</summary>
public sealed class AspectBox : Decorator
{
    private double _ratio = 16.0 / 9;

    /// <summary>Width over height.</summary>
    public double Ratio
    {
        get => _ratio;
        set
        {
            var ratio = double.IsFinite(value) && value > 0.1 ? value : 16.0 / 9;
            if (Math.Abs(ratio - _ratio) < 0.001) return;
            _ratio = ratio;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 480;
        var size = new Size(width, Math.Round(width / _ratio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(final));
        return final;
    }
}
