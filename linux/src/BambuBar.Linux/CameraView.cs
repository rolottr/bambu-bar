using BambuBar.Core;
using BambuBar.Core.App;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private readonly Dictionary<string, (Adw.ApplicationWindow Window, CameraSurface Surface)> _cameraWindows = [];
    private void AddCamera(PrinterSession session)
    {
        var expander = Gtk.Expander.New(Loc.T("Camera"));
        var surface = new CameraSurface(session.Camera, () => expander.GetExpanded() && VisibleInPage(surfaceWidget: expander));
        expander.SetChild(surface.Content); _pageHost.Append(expander);
        expander.OnNotify += (_, e) => { if (e.Pspec.GetName() == "expanded") session.Camera.ViewersChanged(); };
        _pageHost.Append(Button("window-new-symbolic", Loc.T("Open the camera in its own window"), () => OpenCamera(session), true));
        _cleanup.Add(surface.Dispose);
    }
    private bool VisibleInPage(Gtk.Widget surfaceWidget)
    {
        if (!_window.GetVisible() || _window.IsSuspended()) return false;
        if (!surfaceWidget.ComputeBounds(_scroller, out var bounds)) return false;
        using (bounds) return bounds.GetY() < _scroller.GetHeight() && bounds.GetY() + bounds.GetHeight() > 0;
    }
    private void OpenCamera(PrinterSession session)
    {
        if (!session.ShowsCamera) return;
        if (_cameraWindows.TryGetValue(session.Id, out var existing)) { existing.Window.Present(); return; }
        var window = Adw.ApplicationWindow.New(_app); WindowSize.Track(window); window.SetTitle(session.Config.Name + " — " + Loc.T("Camera")); window.SetDefaultSize(900, 580);
        var header = Adw.HeaderBar.New(); header.SetTitleWidget(Label(session.Config.Name, "heading"));
        var surface = new CameraSurface(session.Camera, () => window.GetVisible() && !window.IsSuspended());
        var root = Column(0); root.Append(header); root.Append(surface.Content); window.SetContent(root);
        _cameraWindows[session.Id] = (window, surface);
        window.OnCloseRequest += (_, _) => { surface.Dispose(); _cameraWindows.Remove(session.Id); return false; };
        var keys = Gtk.EventControllerKey.New(); keys.OnKeyPressed += (_, e) => { if (e.Keyval == 65307 || (e.Keyval == 119 && (e.State & Gdk.ModifierType.ControlMask) != 0)) { window.Close(); return true; } return false; }; window.AddController(keys);
        window.OnNotify += (_, e) => { if (e.Pspec.GetName() == "suspended") session.Camera.ViewersChanged(); };
        window.Present();
    }
    private sealed class CameraSurface : IDisposable
    {
        public Gtk.Box Content { get; } = Column(8);
        private readonly Gtk.Picture _picture = Gtk.Picture.New();
        private readonly Gtk.Label _status = Label("", "muted");
        private readonly CameraFeed _feed;
        private readonly CameraViewer _viewer;
        private bool _disposed;
        public CameraSurface(CameraFeed feed, Func<bool> visible)
        {
            _feed = feed;
            _picture.SetSizeRequest(-1, 250); _picture.SetCanShrink(true); _picture.SetContentFit(Gtk.ContentFit.Contain); _picture.SetAlternativeText(Loc.T("Live View")); _picture.SetVexpand(true);
            _status.SetWrap(true); _status.SetMarginStart(12); _status.SetMarginEnd(12); Content.Append(_picture); Content.Append(_status);
            Content.Append(Button("view-refresh-symbolic", Loc.T("Try Again"), feed.TryAgain, true));
            _viewer = new CameraViewer(_picture, feed, visible);
            feed.FrameChanged += Frame; feed.PropertyChanged += Changed;
        }
        private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Frame();
        private void Frame()
        {
            if (_disposed) return;
            if (_feed.Frame is { } frame)
            {
                using var bytes = GLib.Bytes.New(frame.Pixels);
                using var texture = Gdk.MemoryTexture.New(frame.Width, frame.Height, Gdk.MemoryFormat.B8g8r8a8, bytes, (nuint)frame.Stride);
                _picture.SetPaintable(texture);
            }
            else _picture.SetPaintable(null);
            _status.SetText(_feed.State switch
            {
                CameraFeed.FeedState.Unavailable unavailable => unavailable.Reason,
                CameraFeed.FeedState.Failed failed => failed.Reason,
                CameraFeed.FeedState.Connecting => Loc.T("Connecting…"),
                CameraFeed.FeedState.Live => _feed.Detail ?? Loc.T("Live View"),
                _ => "",
            });
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _feed.FrameChanged -= Frame; _feed.PropertyChanged -= Changed; _viewer.Dispose(); }
    }
    private sealed class CameraViewer : ICameraViewer, IDisposable
    {
        private readonly Gtk.Widget _widget; private readonly CameraFeed _feed; private readonly Func<bool> _visible; private bool _disposed;
        public bool IsOnScreen => _widget.GetMapped() && _visible();
        public CameraViewer(Gtk.Widget widget, CameraFeed feed, Func<bool> visible)
        {
            _widget = widget; _feed = feed; _visible = visible; widget.OnMap += Mapped; widget.OnUnmap += Mapped; feed.Attach(this);
        }
        private void Mapped(Gtk.Widget sender, EventArgs args) => GLib.Functions.IdleAdd(200, () => { if (!_disposed) _feed.ViewersChanged(); return false; });
        public void Dispose() { _disposed = true; _widget.OnMap -= Mapped; _widget.OnUnmap -= Mapped; _feed.Detach(this); }
    }
}
