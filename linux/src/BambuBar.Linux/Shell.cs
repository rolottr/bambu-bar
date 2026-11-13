using System.ComponentModel;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal sealed partial class Shell : IDisposable
{
    private readonly Adw.Application _app;
    private readonly PrinterStore _store;
    private readonly AppSettings _settings;
    private readonly SecretStore _secrets;
    private readonly Notifier _notifier;
    private readonly bool _ephemeral;
    private readonly Licensing _license;
    private readonly Background _background = new();
    private readonly Adw.ApplicationWindow _window;
    private readonly Gtk.Box _pageHost;
    private readonly Gtk.ScrolledWindow _scroller;
    private readonly Gtk.Label _footer;
    private readonly Gtk.Label _message;
    private readonly Gtk.ListBox _nav;
    private readonly List<Action> _updates = [];
    private readonly List<Action> _cleanup = [];
    private readonly string[] _pages = ["Printer", "Filament", "All Printers", "Printers", "Notifications", "Settings", "Quick view", "About"];
    private PrinterConfig? _setupConfig;
    private string? _error;
    private string _schema = "";
    private string _page = "Printer";
    private bool _queued, _ready, _disposed;
    private Tray? _tray;
    private DesktopPanel? _desktop;
    private Adw.ApplicationWindow? _panel;
    private readonly List<Action> _panelUpdates = [];

    public Shell(Adw.Application app, PrinterStore store, AppSettings settings, SecretStore secrets, Notifier notifier, string[] args)
    {
        _app = app; _store = store; _settings = settings; _secrets = secrets; _notifier = notifier;
        _ephemeral = PrinterStore.IsEphemeralRun(args);
        var css = Gtk.CssProvider.New(); css.LoadFromPath(Path.Combine(AppContext.BaseDirectory, "style.css"));
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, css, 600);
        _window = Adw.ApplicationWindow.New(app);
        WindowSize.Track(_window);
        _window.SetTitle("Bambu Bar"); _window.SetDefaultSize(1040, 800); _window.SetSizeRequest(760, 460);
        _window.SetHideOnClose(true);
        var root = Column(0);
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(Label("Bambu Bar", "heading"));
        header.PackStart(Button("view-grid-symbolic", "Quick view", ShowPanel));
        header.PackEnd(Button("application-exit-symbolic", "Quit Bambu Bar", app.Quit));
        header.PackEnd(Button("emblem-favorite-symbolic", "Support this project", () => Open("https://bambubar.app/download")));
        root.Append(header);
        var split = Row(0); split.SetVexpand(true);
        var sidebar = Column(6); sidebar.SetSizeRequest(210, -1); sidebar.AddCssClass("sidebar");
        _nav = Gtk.ListBox.New(); _nav.SetSelectionMode(Gtk.SelectionMode.Single); _nav.AddCssClass("navigation-sidebar");
        var icons = new[] { "printer-symbolic", "media-optical-symbolic", "view-grid-symbolic", "network-wired-symbolic", "preferences-system-notifications-symbolic", "emblem-system-symbolic", "view-grid-symbolic", "help-about-symbolic" };
        for (var i = 0; i < _pages.Length; i++)
        {
            var row = Gtk.ListBoxRow.New(); var content = Row(10);
            content.Append(Gtk.Image.NewFromIconName(icons[i])); content.Append(Label(Loc.T(_pages[i]))); row.SetChild(content); _nav.Append(row);
        }
        _nav.OnRowSelected += (_, e) => { if (e.Row is not null) Navigate(_pages[e.Row.GetIndex()]); };
        var navScroll = Gtk.ScrolledWindow.New(); navScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic); navScroll.SetVexpand(true); navScroll.SetChild(_nav); sidebar.Append(navScroll);
        _footer = Label("", "muted"); _footer.SetWrap(true); _footer.SetMarginStart(12); _footer.SetMarginBottom(8); sidebar.Append(_footer);
        split.Append(sidebar); split.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
        var right = Column(0); right.SetHexpand(true);
        var scroller = _scroller = Gtk.ScrolledWindow.New(); scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic); scroller.SetVexpand(true);
        scroller.GetVadjustment().OnValueChanged += (_, _) => _store.Current?.Camera.ViewersChanged();
        _pageHost = Column(18); _pageHost.AddCssClass("page"); scroller.SetChild(_pageHost); right.Append(scroller);
        _message = Label("", "error"); _message.SetWrap(true); _message.SetVisible(false); _message.SetMarginBottom(12); right.Append(_message);
        split.Append(right); root.Append(split); _window.SetContent(root);
        var keys = Gtk.EventControllerKey.New(); keys.OnKeyPressed += (_, e) =>
        {
            if ((e.State & Gdk.ModifierType.ControlMask) == 0) return false;
            if (e.Keyval is >= 49 and <= 56) { _nav.SelectRow(_nav.GetRowAtIndex((int)e.Keyval - 49)); return true; }
            switch (e.Keyval) { case 114: _store.Refresh(); return true; case 108: _store.ToggleLight(); return true; case 113: _app.Quit(); return true; case 107: if (_store.Current is { } camera) OpenCamera(camera); return true; case 119: _window.Close(); return true; case 44: _nav.SelectRow(_nav.GetRowAtIndex(Array.IndexOf(_pages, "Settings"))); return true; }
            return false;
        }; _window.AddController(keys);
        _license = new Licensing(_ephemeral);
        _license.Changed += () => { if (_page == "About") Refresh(); };
        _window.OnNotify += (_, e) => { if (e.Pspec.GetName() == "suspended") _store.Current?.Camera.ViewersChanged(); };
        _window.OnMap += (_, _) => Refresh();
        _store.CameraSettingsChanged = () => { foreach (var entry in _cameraWindows.ToArray()) if (_store.Session(entry.Key)?.ShowsCamera != true) entry.Value.Window.Close(); };
        _store.PropertyChanged += Changed;
        ((System.Collections.Specialized.INotifyCollectionChanged)_store.Sessions).CollectionChanged += (_, _) => { SubscribeSessions(); if (_ready) Rebuild(); };
        foreach (var name in new[] { "show", "panel", "quit" })
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => { if (name == "quit") app.Quit(); else if (name == "panel") ShowPanel(); else Present(); };
            app.AddAction(action);
        }
        var showPrinter = Gio.SimpleAction.New("printer", GLib.VariantType.New("s"));
        showPrinter.OnActivate += (_, e) => { var serial = e.Parameter?.GetString(out var length); if (serial is not null && _store.Session(serial) is not null) _store.Select(serial); Navigate("Printer"); Present(); }; _app.AddAction(showPrinter);
        _nav.SelectRow(_nav.GetRowAtIndex(0));
        _window.OnCloseRequest += (_, _) => { HideMain(); return true; };
    }
    private void SubscribeSessions()
    {
        foreach (var s in _store.Sessions) { s.PropertyChanged -= Changed; s.PropertyChanged += Changed; }
    }
    private void HideMain() { _window.SetVisible(false); }
    public void Present() { Platform.Trace($"present before: disposed={_disposed} visible={_window.GetVisible()} mapped={_window.GetMapped()} suspended={_window.IsSuspended()}"); _window.Present(); Refresh(); Platform.Trace($"present after: visible={_window.GetVisible()} mapped={_window.GetMapped()}"); }
    public void ShowQuickView() => ShowPanel();
    public void Started() { _ready = true; SubscribeSessions(); Rebuild(); PreparePanel(); if (!_ephemeral) { _desktop = new DesktopPanel(_store, _settings, DesktopAction); _ = StartDesktop(); } _tray = new Tray(_store, ShowPanel, Present, _app.Quit); _ = StartTray(); if (!_ephemeral && _store.IsConfigured) _ = RequestBackground(); if (_pendingCommand is { } command) { _pendingCommand = null; var completion = _pendingCompletion; _pendingCompletion = null; HandleCommand(command, completion); } }
    private async Task StartDesktop() { try { await _desktop!.Start(); } catch (Exception) { Platform.Log("Native desktop panel unavailable; standard tray remains available."); } }
    private void DesktopAction(string action, PrinterSession? session)
    {
        switch (action)
        {
            case "select": break;
            case "show": Navigate("Printer"); Present(); break;
            case "license": Navigate("About"); Present(); break;
            case "dismiss-issue" when session?.Status?.MostSevereIssue is { } issue: session.Dismiss(issue); break;
            case "settings": Navigate("Settings"); Present(); break;
            case "filament": Navigate("Filament"); Present(); break;
            case "fleet": Navigate("All Printers"); Present(); break;
            case "quit": _app.Quit(); break;
            case "refresh": session?.Refresh(); break;
            case "camera" when session?.ShowsCamera == true && session.IsOnline: OpenCamera(session); break;
            case "light" when session?.IsOnline == true && (session.Config.Model?.HasChamberLight == true || session.Status?.ChamberLightOn is not null): session.ToggleLight(); break;
            case "pause" when _store.ShowPrinterActions && session?.IsOnline == true && session.Status is { ControlsLocked: false, State: JobState.Printing }: session.Pause(); break;
            case "resume" when _store.ShowPrinterActions && session?.IsOnline == true && session.Status is { ControlsLocked: false, State: JobState.Paused }: session.Resume(); break;
            case var speed when speed.StartsWith("speed:", StringComparison.Ordinal) && int.TryParse(speed[6..], out var raw) && Enum.IsDefined(typeof(SpeedLevel), raw) && _store.ShowPrinterActions && session?.IsOnline == true && session.Status is { ControlsLocked: false }: session.SetSpeed((SpeedLevel)raw); break;
            case "stop" when _store.ShowPrinterActions && session?.IsOnline == true && session.Status is { ControlsLocked: false } s && s.State.IsActive():
                if (_store.ConfirmStop) { Present(); Confirm(Loc.T("Stop this print?"), s.JobName, Loc.T("Stop"), session.StopPrint); }
                else session.StopPrint();
                break;
        }
    }
    private async Task RequestBackground()
    {
        try { if (await _background.Request(false, permissionOnly: true)) await _background.Status(Loc.T("Printers")); }
        catch (Exception) { /* Launcher and optional SNI remain usable when a portal is absent. */ }
    }
    private async Task StartTray() { try { await _tray!.Start(); } catch (Exception) { Console.Error.WriteLine("Tray host unavailable; launcher remains available."); } }
    private string Schema => _store.Current is { } s ? $"{s.Id}:{s.Status?.SecondNozzle is not null}:{s.Status?.Chamber is not null}:{s.Status?.DoorOpen is not null}:{s.Status?.NozzleDiameter is not null}:" + string.Join(',', s.Status?.AllSpools.Select(p => p.Id) ?? []) : "";
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(PrinterStore.SelectedSerial)) { Rebuild(); if (_panel is not null) BuildPanel(); }
        if (!_window.GetVisible() && _panel?.GetVisible() != true) return;
        if (_queued) return; _queued = true;
        GLib.Functions.IdleAdd(200, () => { _queued = false; if (!_disposed) Refresh(); return false; });
    }
    private void Refresh()
    {
        if (_ready && _schema != Schema) { Rebuild(); return; }
        _footer.SetText(_store.IsConfigured ? $"{_store.PrinterName}\n{_store.Current?.ShortState}" : Loc.T("No printer set up"));
        if (_window.GetVisible()) foreach (var update in _updates.ToArray()) update();
        if (_panel?.GetVisible() == true) foreach (var update in _panelUpdates.ToArray()) update();
        if (_store.Toast is { } toast) { _message.SetText(toast.Message); _message.SetVisible(true); }
        else { _message.SetText(_error ?? ""); _message.SetVisible(_error is not null); }
    }
    private void BeginSetup(PrinterConfig? config = null) { _setupConfig = config; Navigate("Setup"); }
    private void Navigate(string page)
    {
        var index = Array.IndexOf(_pages, page);
        if (index >= 0 && _nav.GetSelectedRow()?.GetIndex() != index) { _nav.SelectRow(_nav.GetRowAtIndex(index)); return; }
        if (_page != page) _error = null;
        _page = page; Rebuild();
    }
    private void Rebuild()
    {
        _schema = Schema;
        foreach (var clean in _cleanup) clean(); _cleanup.Clear(); _updates.Clear();
        while (_pageHost.GetFirstChild() is { } child) _pageHost.Remove(child);
        if (_page == "Setup") SetupPage();
        else if (_page == "Printer") { if (_store.IsConfigured) PrinterPage(); else SetupPage(); }
        else if (_page == "Filament") FilamentPage();
        else if (_page == "All Printers") FleetPage();
        else if (_page == "Printers") PrintersPage();
        else if (_page == "Notifications") NotificationsPage();
        else if (_page == "Settings") SettingsPage();
        else if (_page == "Quick view") QuickViewPage();
        else AboutPage();
        Refresh();
    }
    public void Error(string message) { _error = message; _message.SetText(message); _message.SetVisible(true); }
    private void Async(Func<Task> work) { _ = Execute(); async Task Execute() { try { await work(); } catch (Exception e) { Error(e.Message); } } }
    private void Open(string uri) => Gtk.Functions.ShowUri(_window, uri, 0);
    private static Gtk.Box Column(int gap = 12) => Gtk.Box.New(Gtk.Orientation.Vertical, gap);
    private static Gtk.Box Row(int gap = 12) => Gtk.Box.New(Gtk.Orientation.Horizontal, gap);
    private static Gtk.Label Label(string text, params string[] css)
    {
        var label = Gtk.Label.New(text); label.SetXalign(0); label.SetSelectable(false);
        foreach (var c in css) label.AddCssClass(c); return label;
    }
    private static Gtk.Button Button(string icon, string title, Action action, bool text = false)
    {
        var button = Gtk.Button.New(); button.SetTooltipText(title); button.AddCssClass("control");
        if (text) { var box = Row(7); box.SetHalign(Gtk.Align.Center); box.Append(Gtk.Image.NewFromIconName(icon)); box.Append(Label(title)); button.SetChild(box); }
        else button.SetChild(Gtk.Image.NewFromIconName(icon));
        button.OnClicked += (_, _) => action(); return button;
    }
    private Gtk.Box Header(string title, string? subtitle = null)
    {
        var row = Row(); var text = Column(3); text.SetHexpand(true); text.Append(Label(title, "page-title"));
        if (subtitle is not null) text.Append(Label(subtitle, "muted")); row.Append(text); _pageHost.Append(row); return row;
    }
    private static Gtk.Box Module(params Gtk.Widget[] children)
    {
        var box = Column(12); box.AddCssClass("module"); foreach (var c in children) box.Append(c); return box;
    }
    private Gtk.Label Live(Func<string> value, List<Action>? updates = null, params string[] css)
    {
        var label = Label(value(), css); (updates ?? _updates).Add(() => label.SetText(value())); return label;
    }
    private void Detail(Gtk.Box box, string title, Func<string> value)
    {
        var row = Row(); var key = Label(Loc.T(title), "muted"); key.SetHexpand(true); row.Append(key); row.Append(Live(value)); box.Append(row);
    }
    private void Toggle(Gtk.Box box, string title, bool value, Action<bool> set)
    {
        var row = Row(); row.AddCssClass("list-row"); var label = Label(Loc.T(title)); label.SetHexpand(true); label.SetWrap(true); row.Append(label);
        var toggle = Gtk.Switch.New(); toggle.SetActive(value); toggle.SetValign(Gtk.Align.Center);
        toggle.OnNotify += (_, e) => { if (e.Pspec.GetName() == "active") { try { set(toggle.GetActive()); } catch (Exception ex) { Error(ex.Message); toggle.SetActive(!toggle.GetActive()); } } };
        row.Append(toggle); box.Append(row);
    }
    private void Confirm(string title, string detail, string confirm, Action action)
    {
        var dialog = Adw.MessageDialog.New(_window, title, detail); dialog.AddResponse("cancel", Loc.T("Cancel")); dialog.AddResponse("confirm", confirm);
        dialog.SetResponseAppearance("confirm", Adw.ResponseAppearance.Destructive); dialog.SetDefaultResponse("cancel"); dialog.SetCloseResponse("cancel");
        dialog.OnResponse += (_, e) => { if (e.Response == "confirm") action(); }; dialog.Present();
    }
    public void Dispose()
    {
        if (_disposed) return; Platform.Trace("shell dispose"); _disposed = true;
        foreach (var clean in _cleanup) clean();
        foreach (var camera in _cameraWindows.Values.ToArray()) { camera.Surface.Dispose(); camera.Window.Destroy(); }
        _background.Dispose(); _license.Dispose(); _desktop?.Dispose(); _tray?.Dispose(); _store.PropertyChanged -= Changed; _store.Dispose();
    }
}
