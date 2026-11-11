using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using BambuBar.Theme;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Utilities;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using D3D = SharpDX.Direct3D11;
using Media3D = System.Windows.Media.Media3D;

namespace BambuBar.Model3D;

/// <summary>
/// The printer as a live 3D model (the Mac's PrinterModelView): the print grows slice by slice in the active
/// filament, the bed (CoreXY) or gantry (bed slingers) follows the layers, the toolhead runs its 3 s loop while
/// printing, the spools take their filament colours and the chamber light follows the printer's. Drag to turn it
/// (pitch −0.15…0.7); double-click to reset. HelixToolkit draws it with Direct3D 11 (<see cref="Renderer3D"/>).
/// <para>
/// The drawing (Core's PrinterArt, the widgets' art) stands in until the first 3D frame is on screen, and for good
/// when 3D is off for the run: no device, an exception while rendering, no picture within a few seconds (a Direct3D
/// 9 interop surface that never came: VMs, remote sessions), <c>--no-3d</c>, or a missing model file. Offscreen
/// renders (the snapshot's panels and pages) never load the view, so they show the drawing too.
/// </para>
/// <para>
/// Frames are drawn only when something changed, and nothing at all while the view is out of sight (hidden or
/// minimized window, closed panel, scrolled away, locked session); the toolhead moves only while printing, in sight,
/// and with Windows' animations on (<see cref="SystemParameters.ClientAreaAnimation"/>, through the caller's
/// <c>animated</c>).
/// </para>
/// </summary>
public sealed class PrinterModelView : Grid
{
    private static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(6);
    private static readonly List<WeakReference<PrinterModelView>> Views = [];
    private static bool _sessionLocked;
    private static bool _loggedReady;

    private readonly double _renderScale;
    private readonly Image _art;
    /// <summary>Holds the viewport; faded in over the drawing at the first frame.</summary>
    private readonly Border _surface = new() { Opacity = 0, IsHitTestVisible = false };
    private Viewport3DX? _viewport;
    private PrinterScene? _scene;
    private SceneNodeGroupModel3D? _group;
    private PerspectiveCamera? _camera;
    private PrinterArt.Scene? _state;
    private bool _animated;
    private bool _ready;
    private bool _loaded;
    private bool _onScreen;
    private bool _checkPending;
    private Window? _window;
    private readonly List<ScrollViewer> _scrollers = [];

    private double _yaw = PrinterScene.RestYaw;
    private double _pitch = PrinterScene.RestPitch;
    private Point? _drag;
    private (double Yaw, double Pitch, TimeSpan Start)? _reset;

    /// <summary>Printing and on screen: the toolhead loops.</summary>
    private bool _moving;
    private double _motionTime;
    /// <summary>How far along its path the toolhead stands: 1 while moving, easing to 0 when the print stops in sight.</summary>
    private double _motionAmount;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _idle;
    private readonly DispatcherTimer _watchdog;
    private TimeSpan _quietUntil;
    private bool _looping;

    static PrinterModelView()
    {
        try
        {
            SystemEvents.SessionSwitch += (sender, e) =>
            {
                var locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
                var unlocked = e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect;
                if (!locked && !unlocked) return;
                _sessionLocked = locked;
                lock (Views)
                {
                    Views.RemoveAll(v => !v.TryGetTarget(out _));
                    foreach (var reference in Views) if (reference.TryGetTarget(out var view)) view.Dispatcher.BeginInvoke(view.ScheduleCheck);
                }
            };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
        {
            // No session notifications (a service, a test host): the view just keeps drawing while "visible".
        }
    }

    /// <param name="width">Size in DIPs, as the drawing's (ArtImage.Element).</param>
    /// <param name="renderScale">The layout transform the view is shown under (the Printer page's hero is 1.45×), so the
    /// model renders at the pixels it's shown at.</param>
    public PrinterModelView(double width, double height, double renderScale = 1)
    {
        Width = width;
        Height = height;
        _renderScale = renderScale;
        // Hit-testable everywhere, so the whole square drags.
        Background = Brushes.Transparent;
        _art = ArtImage.Element(width, height);
        Children.Add(_art);
        Children.Add(_surface);
        Focusable = false;
        // Nothing inside (HelixToolkit's viewport and its parts) is a Tab stop: the model is a picture with a value.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => Step();
        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _idle.Tick += (_, _) => RestIfQuiet();
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstFrameLimit };
        _watchdog.Tick += (_, _) =>
        {
            _watchdog.Stop();
            if (!_ready && _viewport is not null) Renderer3D.TurnOff($"no 3D picture within {FirstFrameLimit.TotalSeconds:0} s of the view showing (Direct3D 9 interop surface missing?)");
        };
        IsVisibleChanged += (_, _) => ScheduleCheck();
        SizeChanged += (_, _) => ScheduleCheck();
        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();
        AutomationProperties.SetName(this, Loc.T("Printer model"));
        lock (Views) Views.Add(new WeakReference<PrinterModelView>(this));
    }

    /// <summary>The 3D model is on screen (not the drawing).</summary>
    public bool IsLive => _ready;

    /// <summary>Frames Direct3D drew before the first one reached the screen (the snapshot tells "draws, but WPF never
    /// gets the picture" from "doesn't draw").</summary>
    public int FramesBeforeLive { get; private set; }

    /// <summary>Shows <paramref name="scene"/>: the printer's shape, print, filaments and light. <paramref name="animated"/>
    /// lets the toolhead move while printing (off with Windows' animations, and in snapshots).</summary>
    public void Show(PrinterArt.Scene scene, bool animated)
    {
        var shapeChanged = _state?.Shape != scene.Shape;
        var dimmedChanged = _state?.Dimmed != scene.Dimmed;
        // The device and the model take a moment the first time: get them ready while nothing else is happening, so
        // the view is quick when it first comes into sight.
        if (shapeChanged && !Renderer3D.IsOff) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Warm(scene.Shape));
        _state = scene;
        _animated = animated;
        if (!_ready) ArtImage.Show(_art, scene);
        // Offline: washed out, as on the Mac.
        _surface.Child?.SetValue(OpacityProperty, scene.Dimmed ? 0.6 : 1.0);
        var changed = shapeChanged || dimmedChanged;
        if (shapeChanged && _viewport is not null) Load(scene.Shape);
        else if (_scene?.Apply(scene) == true) changed = true;
        UpdatePlayback();
        // Status reports come every second or so; most change nothing the model shows.
        if (changed) Wake();
    }

    // MARK: - Visibility

    private void OnLoaded()
    {
        _loaded = true;
        Renderer3D.TurnedOff += OnTurnedOff;
        var window = Window.GetWindow(this);
        if (!ReferenceEquals(window, _window))
        {
            // A closed window takes HelixToolkit's surface with it: a new window gets a new viewport.
            if (_window is not null) DropViewport();
            _window = window;
        }
        if (_window is not null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.IsVisibleChanged += OnWindowVisibleChanged;
            _window.Closed += OnWindowClosed;
        }
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller)
            {
                scroller.ScrollChanged += OnScrolled;
                _scrollers.Add(scroller);
            }
        }
        ScheduleCheck();
    }

    private void OnUnloaded()
    {
        _loaded = false;
        Renderer3D.TurnedOff -= OnTurnedOff;
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.IsVisibleChanged -= OnWindowVisibleChanged;
            _window.Closed -= OnWindowClosed;
        }
        foreach (var scroller in _scrollers) scroller.ScrollChanged -= OnScrolled;
        _scrollers.Clear();
        Check();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => ScheduleCheck();
    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ScheduleCheck();
    private void OnScrolled(object sender, ScrollChangedEventArgs e) => ScheduleCheck();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DropViewport();
        _window = null;
    }

    /// <summary>In a shown, unminimized window, visible, inside every scroll viewer around it, session unlocked.</summary>
    private bool IsOnScreen
    {
        get
        {
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
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Once per dispatcher turn (scrolling sends many events).</summary>
    private void ScheduleCheck()
    {
        if (_checkPending) return;
        _checkPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _checkPending = false;
            Check();
        });
    }

    private void Check()
    {
        var onScreen = IsOnScreen;
        if (onScreen && _viewport is null && _state is { } state && !Renderer3D.IsOff) CreateViewport(state.Shape);
        var changed = onScreen != _onScreen;
        _onScreen = onScreen;
        if (changed && !onScreen)
        {
            // Parked at once when it goes out of sight; nothing is drawn until it's back.
            _motionAmount = 0;
            _scene?.Pose(0, 0);
            _reset = null;
            // A closed panel or window opens on the printer at rest, as on the Mac (scrolling away keeps the turn).
            if (!_loaded || _window is not { IsVisible: true })
            {
                (_yaw, _pitch) = (PrinterScene.RestYaw, PrinterScene.RestPitch);
                _scene?.Orient(_yaw, _pitch);
            }
            Rest();
            _idle.Stop();
            _watchdog.Stop();
        }
        if (onScreen && _viewport is not null)
        {
            _idle.Start();
            if (!_ready && !_watchdog.IsEnabled) _watchdog.Start();
            Wake();
        }
        UpdatePlayback();
    }

    // MARK: - The viewport

    private void CreateViewport(PrinterShape shape)
    {
        if (PrinterScene.Model(shape) is null) return;
        if (Renderer3D.Effects is not { } effects) return;
        try
        {
            _camera = new PerspectiveCamera
            {
                FieldOfView = Studio.FieldOfView, UpDirection = new Media3D.Vector3D(0, 1, 0), NearPlaneDistance = 0.02, FarPlaneDistance = 20,
            };
            _viewport = new Viewport3DX
            {
                EffectsManager = effects, Camera = _camera, BackgroundColor = Colors.Transparent,
                ShowViewCube = false, ShowCoordinateSystem = false, ShowFrameRate = false, ShowCameraInfo = false, ShowTriangleCountInfo = false,
                ShowFrameDetails = false, ShowCameraTarget = false, IsRotationEnabled = false, IsPanEnabled = false, IsZoomEnabled = false,
                IsMoveEnabled = false, IsChangeFieldOfViewEnabled = false, IsInertiaEnabled = false, UseDefaultGestures = false,
                IsTouchRotateEnabled = false, IsPinchZoomEnabled = false, IsThreeFingerPanningEnabled = false, ZoomExtentsWhenLoaded = false,
                EnableMouseButtonHitTest = false, EnableCursorPosition = false, EnableD2DRendering = false, EnableSwapChainRendering = false,
                EnableDeferredRendering = false, IsShadowMappingEnabled = false, EnableSSAO = false,
                // Plain sorted blending: its alpha over the transparent background composes correctly in WPF.
                // 4× multisampling, except in software (WARP), where it costs a CPU core while the printer moves.
                OITRenderMode = OITRenderType.None, MSAA = Renderer3D.IsSoftware ? MSAALevel.Disable : MSAALevel.Four, FXAALevel = FXAALevel.None,
                IsHitTestVisible = false, Focusable = false, IsTabStop = false,
                DpiScale = DeviceScale() * _renderScale,
            };
            _viewport.OnRendered += OnRendered;
            _viewport.RenderExceptionOccurred += OnRenderException;
            _group = new SceneNodeGroupModel3D();
            _viewport.Items.Add(_group);
            _surface.Child = new Border { Child = _viewport, Opacity = _state?.Dimmed == true ? 0.6 : 1 };
            Load(shape);
            // HelixToolkit restarts its loop itself (resizes, a recovered device): give those frames time to draw.
            _viewport.Loaded += (_, _) =>
            {
                if (_viewport?.RenderHost is { } host && !ReferenceEquals(host, _hookedHost))
                {
                    _hookedHost = host;
                    host.StartRenderLoop += (_, _) => _quietUntil = _clock.Elapsed + TimeSpan.FromMilliseconds(600);
                }
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Renderer3D.TurnOff($"couldn't create the 3D view: {e.GetType().Name}: {e.Message}");
        }
    }

    private void Load(PrinterShape shape)
    {
        if (_group is null || _camera is null) return;
        _group.Clear();
        _scene = null;
        if (PrinterScene.Model(shape) is not { } model)
        {
            // No model for this shape: the drawing, as on the Mac when the file is missing.
            ShowDrawing();
            return;
        }
        try
        {
            _scene = new PrinterScene(shape, model);
            _group.AddNode(_scene.Root);
            _scene.Orient(_yaw, _pitch);
            if (_state is { } state) _scene.Apply(state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _scene = null;
            Renderer3D.TurnOff($"couldn't build the {shape} model: {e.GetType().Name}: {e.Message}");
            return;
        }
        var distance = _scene.Radius / Math.Sin(Studio.FieldOfView / 2 * Math.PI / 180) * 0.92;
        _camera.Position = new Media3D.Point3D(0, 0, distance);
        _camera.LookDirection = new Media3D.Vector3D(0, 0, -distance);
        _camera.FarPlaneDistance = distance * 4;
        if (_ready) _surface.Opacity = 1;
    }

    private static void Warm(PrinterShape shape)
    {
        if (Renderer3D.IsOff || PrinterScene.Model(shape) is null) return;
        _ = Renderer3D.Effects;
    }

    private double DeviceScale()
    {
        try
        {
            return VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_viewport is not null) _viewport.DpiScale = newDpi.PixelsPerDip * _renderScale;
        Wake();
    }

    private DPFCanvas? _canvas;
    private IRenderHost? _hookedHost;

    private void OnRendered(object? sender, EventArgs e)
    {
        if (_ready || _viewport is null) return;
        FramesBeforeLive++;
        _canvas ??= Find<DPFCanvas>(_viewport);
        // The interop surface exists: the frame just drawn is on screen.
        if (_canvas?.Source is null) return;
        _ready = true;
        _watchdog.Stop();
        _viewport.OnRendered -= OnRendered;
        if (!_loggedReady)
        {
            _loggedReady = true;
            Renderer3D.Write($"First 3D frame on screen ({_viewport.RenderHost?.ActualWidth:0}×{_viewport.RenderHost?.ActualHeight:0} px, {_state?.Shape})");
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!_ready) return;
            Cursor = Cursors.Hand;
            // The toolhead may start now.
            UpdatePlayback();
            if (AppTheme.AnimationsEnabled)
            {
                var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) => { if (_ready) _art.Visibility = Visibility.Hidden; };
                _surface.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                _surface.Opacity = 1;
                _art.Visibility = Visibility.Hidden;
            }
        });
    }

    private void OnRenderException(object? sender, RelayExceptionEventArgs e)
    {
        e.Handled = true;
        Renderer3D.TurnOff($"rendering failed: {e.Exception.GetType().Name}: {e.Exception.Message}");
    }

    private void OnTurnedOff() => Dispatcher.BeginInvoke(ShowDrawing);

    /// <summary>The drawing, for good (3D is off for the run).</summary>
    private void ShowDrawing()
    {
        DropViewport();
        Cursor = null;
    }

    /// <summary>Takes the viewport out (a window closed, or 3D turned off); the drawing shows again.</summary>
    private void DropViewport()
    {
        _watchdog.Stop();
        _idle.Stop();
        _tick.Stop();
        _ready = false;
        _canvas = null;
        _surface.BeginAnimation(OpacityProperty, null);
        _surface.Opacity = 0;
        _art.Visibility = Visibility.Visible;
        if (_state is { } state) ArtImage.Show(_art, state);
        if (_viewport is { } viewport)
        {
            viewport.OnRendered -= OnRendered;
            viewport.RenderExceptionOccurred -= OnRenderException;
            try
            {
                // Without its effects manager the canvas lets go of its Direct3D surfaces when it unloads (with one,
                // it would keep them until the window closes).
                viewport.RenderHost?.StopRendering();
                _group?.Clear();
                viewport.Items.Clear();
                viewport.EffectsManager = null;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Renderer3D.Write($"Releasing a 3D view: {e.GetType().Name}: {e.Message}");
            }
            _surface.Child = null;
            try
            {
                viewport.Dispose();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Renderer3D.Write($"Disposing a 3D view: {e.GetType().Name}: {e.Message}");
            }
        }
        _viewport = null;
        _group = null;
        _scene = null;
        _camera = null;
        _hookedHost = null;
        _looping = false;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // MARK: - Drawing on demand

    /// <summary>Something changed: draw for a little while (HelixToolkit draws only what it was told changed).</summary>
    private void Wake()
    {
        _quietUntil = _clock.Elapsed + TimeSpan.FromMilliseconds(600);
        if (!_onScreen || _viewport?.RenderHost is not { } host) return;
        if (!_looping)
        {
            _looping = true;
            host.StartRendering();
        }
        host.InvalidateRender();
    }

    /// <summary>Stops HelixToolkit's per-frame loop (it keeps WPF ticking at the display's rate) once nothing moves;
    /// never before the first frame is on screen.</summary>
    private void RestIfQuiet()
    {
        if (!_ready || _moving || _motionAmount > 0 || _drag is not null || _reset is not null || _clock.Elapsed < _quietUntil) return;
        Rest();
    }

    private void Rest()
    {
        _viewport?.RenderHost?.StopRendering();
        _looping = false;
    }

    // MARK: - Motion

    private void UpdatePlayback()
    {
        // In software (WARP) the toolhead stays still: 20 frames a second there cost a fifth of a CPU core.
        var moving = _onScreen && _ready && _animated && !Renderer3D.IsSoftware && _state is { State: JobState.Printing, Dimmed: false };
        if (moving && !_moving) _motionTime = 0;
        _moving = moving;
        var needsTicks = _onScreen && _scene is not null && (_moving || _motionAmount > 0 || _reset is not null);
        if (needsTicks && !_tick.IsEnabled)
        {
            _lastTick = _clock.Elapsed;
            _tick.Start();
        }
        else if (!needsTicks && _tick.IsEnabled) _tick.Stop();
    }

    /// <summary>20 frames a second (the Mac's rate: the eased moves look the same, and cost a third).</summary>
    private void Step()
    {
        var now = _clock.Elapsed;
        var dt = Math.Min(0.25, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        if (_scene is null) return;
        if (_moving)
        {
            _motionTime += dt;
            _motionAmount = 1;
        }
        else if (_motionAmount > 0)
        {
            // The print stopped in sight: the toolhead eases home over 0.4 s.
            _motionAmount = Math.Max(0, _motionAmount - dt / 0.4);
        }
        var amount = _motionAmount * _motionAmount * (3 - 2 * _motionAmount);
        _scene.Pose(_motionTime, amount);
        if (_reset is { } reset)
        {
            var f = Math.Min(1, (now - reset.Start).TotalSeconds / 0.5);
            var eased = f * f * (3 - 2 * f);
            _yaw = reset.Yaw + (PrinterScene.RestYaw - reset.Yaw) * eased;
            _pitch = reset.Pitch + (PrinterScene.RestPitch - reset.Pitch) * eased;
            _scene.Orient(_yaw, _pitch);
            if (f >= 1) _reset = null;
        }
        Wake();
        UpdatePlayback();
    }

    // MARK: - Turning

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_ready || _scene is null) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _drag = null;
            ReleaseMouseCapture();
            if (AppTheme.AnimationsEnabled) _reset = (_yaw, _pitch, _clock.Elapsed);
            else
            {
                (_yaw, _pitch) = (PrinterScene.RestYaw, PrinterScene.RestPitch);
                _scene.Orient(_yaw, _pitch);
            }
            UpdatePlayback();
            Wake();
            return;
        }
        _reset = null;
        _drag = e.GetPosition(null);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } last || _scene is null || !IsMouseCaptured) return;
        // Positions in the window, so the Printer page's larger hero turns at the same rate under the pointer.
        var point = e.GetPosition(null);
        _yaw += (point.X - last.X) * 0.012;
        _pitch = Math.Clamp(_pitch + (point.Y - last.Y) * 0.008, PrinterScene.MinPitch, PrinterScene.MaxPitch);
        _drag = point;
        _scene.Orient(_yaw, _pitch);
        Wake();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null) return;
        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = null;
    }

    // MARK: - Snapshots

    /// <summary>A fresh frame read back from Direct3D (premultiplied BGRA, at the render's pixels, transparent
    /// background kept), or null without one. Helix's own RenderBitmap goes through a BMP and loses the alpha.</summary>
    /// <param name="drawnOnly">Also when the frames never reached the screen (diagnostics).</param>
    public BitmapSource? Capture(bool drawnOnly = false)
    {
        if (!(_ready || (drawnOnly && FramesBeforeLive > 0)) || _viewport?.RenderHost is not { } host || host.EffectsManager?.Device is not { } device) return null;
        host.InvalidateRender();
        host.UpdateAndRender();
        if (host.RenderBuffer?.BackBuffer?.Resource is not D3D.Texture2D texture) return null;
        var desc = texture.Description;
        var bgra = desc.Format is SharpDX.DXGI.Format.B8G8R8A8_UNorm or SharpDX.DXGI.Format.B8G8R8A8_UNorm_SRgb;
        if (!bgra && desc.Format is not (SharpDX.DXGI.Format.R8G8B8A8_UNorm or SharpDX.DXGI.Format.R8G8B8A8_UNorm_SRgb)) return null;
        desc.Usage = D3D.ResourceUsage.Staging;
        desc.BindFlags = D3D.BindFlags.None;
        desc.CpuAccessFlags = D3D.CpuAccessFlags.Read;
        desc.OptionFlags = D3D.ResourceOptionFlags.None;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0);
        using var staging = new D3D.Texture2D(device, desc);
        var context = device.ImmediateContext;
        context.CopySubresourceRegion(texture, 0, null, staging, 0);
        var box = context.MapSubresource(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
        int width = desc.Width, height = desc.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(box.DataPointer + y * box.RowPitch, pixels, y * stride, stride);
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
        if (!bgra)
        {
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    // MARK: - Accessibility

    protected override AutomationPeer OnCreateAutomationPeer() => new ModelPeer(this);

    /// <summary>"Printer model", valued "Printing 64 percent" (the Mac's VoiceOver label and value).</summary>
    private sealed class ModelPeer(PrinterModelView view) : FrameworkElementAutomationPeer(view), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(PrinterModelView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override string GetNameCore() => Loc.T("Printer model");

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        public bool IsReadOnly => true;

        public string Value => ((PrinterModelView)Owner)._state is { } s
            ? s.State.IsActive() ? Loc.F("%@ %lld percent", s.State.Title(), (long)Math.Round(s.Progress * 100)) : s.State.Title()
            : "";

        public void SetValue(string value) => throw new InvalidOperationException("The printer model is read-only.");
    }
}
