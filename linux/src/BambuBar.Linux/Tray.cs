using Tmds.DBus;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string service);
}
[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusItem : IDBusObject
{
    Task ActivateAsync(int x, int y);
    Task SecondaryActivateAsync(int x, int y);
    Task ContextMenuAsync(int x, int y);
    Task ScrollAsync(int delta, string orientation);
    Task<object> GetAsync(string property);
    Task<IDictionary<string, object>> GetAllAsync();
    Task<IDisposable> WatchNewIconAsync(Action handler);
    Task<IDisposable> WatchNewOverlayIconAsync(Action handler);
    Task<IDisposable> WatchNewTitleAsync(Action handler);
    Task<IDisposable> WatchNewToolTipAsync(Action handler);
    Task<IDisposable> WatchNewStatusAsync(Action<string> handler);
}
[DBusInterface("com.canonical.dbusmenu")]
public interface ITrayMenu : IDBusObject
{
    Task<(uint revision, (int id, IDictionary<string, object> properties, object[] children) layout)> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames);
    Task<(int id, IDictionary<string, object> properties)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames);
    Task<object> GetPropertyAsync(int id, string name);
    Task EventAsync(int id, string eventId, object data, uint timestamp);
    Task<int[]> EventGroupAsync((int id, string eventId, object data, uint timestamp)[] events);
    Task<bool> AboutToShowAsync(int id);
    Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] ids);
    Task<object> GetAsync(string property);
    Task<IDictionary<string, object>> GetAllAsync();
    Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler);
}

/// <summary>Optional SNI/DBusMenu adapter. Never assumes a desktop name or installs a shell extension.</summary>
internal sealed class Tray : IStatusItem, IDisposable
{
    private readonly Connection _connection = new(Address.Session);
    private readonly PrinterStore _store;
    private readonly Action<int, int> _panel;
    private readonly Action _show, _quit;
    private readonly Menu _menu;
    private IDisposable? _watcher;
    private IDisposable? _shellWatcher;
    private bool _nativePanel;
    private bool _disposed;
    private readonly Signal _icon = new(), _overlay = new(), _title = new(), _tooltip = new();
    private readonly Signal<string> _status = new();
    private string _lastTitle = "", _lastIcon = "";
    public ObjectPath ObjectPath => new("/StatusNotifierItem");
    public bool Available { get; private set; }

    public Tray(PrinterStore store, Action<int, int> panel, Action show, Action quit, Action<string> navigate)
    {
        var context = SynchronizationContext.Current ?? throw new InvalidOperationException("Create the tray on the GTK thread.");
        Action Dispatch(Action action) => () => context.Post(_ => { if (!_disposed) action(); }, null);
        _store = store; _panel = (x, y) => context.Post(_ => { if (!_disposed) panel(x, y); }, null); _show = Dispatch(show); _quit = Dispatch(quit);
        _menu = new Menu([
            ("Quick view", () => _panel(-1, -1)), ("Open Bambu Bar", _show),
            ("Filament", Dispatch(() => navigate("filament"))), ("Widgets", Dispatch(() => navigate("widgets"))),
            ("Settings", Dispatch(() => navigate("settings"))), ("Support this project", Dispatch(() => navigate("support-menu"))),
            ("Manage License…", Dispatch(() => navigate("license"))), ("Quit", _quit)
        ]);
    }
    public async Task Start()
    {
        await _connection.ConnectAsync();
        await _connection.RegisterObjectAsync(this);
        await _connection.RegisterObjectAsync(_menu);
        _shellWatcher = await _connection.ResolveServiceOwnerAsync(DesktopPanel.ShellName, owner =>
        {
            _nativePanel = !string.IsNullOrEmpty(owner.NewOwner);
            _status.Emit(Status);
        }, _ => { _nativePanel = false; _status.Emit(Status); });
        _watcher = await _connection.ResolveServiceOwnerAsync("org.kde.StatusNotifierWatcher", owner =>
        {
            Available = !string.IsNullOrEmpty(owner.NewOwner);
            if (Available) _ = Register();
        }, _ => Available = false);
        _store.PropertyChanged += Changed;
        async Task Register()
        {
            try { await _connection.CreateProxy<IStatusWatcher>("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher").RegisterStatusNotifierItemAsync("/StatusNotifierItem"); }
            catch (Exception) { Available = false; }
        }
    }
    private string Title
    {
        get
        {
            if (_store.MenuBarSession is not { } session) return "Bambu Bar";
            var state = session.IsOnline ? session.Status : null;
            var label = state?.StateTitle ?? session.LinkSummary;
            var parts = new List<string> { session.Config.Name, label };
            if (state?.State.IsActive() == true && _store.MenuBarStyle.Text(state) is { Length: > 0 } text)
                parts.AddRange(text.Split(" · ").Where(part => part != label));
            return string.Join(" · ", parts);
        }
    }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        var title = Title;
        if (_lastTitle != title) { _lastTitle = title; _title.Emit(); _tooltip.Emit(); }
        var icon = IconName;
        if (_lastIcon != icon) { _lastIcon = icon; _icon.Emit(); }
    }
    // Plasma animates NeedsAttention indefinitely. The glyph itself keeps long-lived
    // paused/failed states visible without continuously repainting the compositor.
    private string Status => _nativePanel ? "Passive" : "Active";
    private string IconName => TrayIcon.Name(TrayIcon.Key(_store.MenuBarSession, _store.IsConfigured));
    private IDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["Category"] = "Hardware",
        ["Id"] = Platform.AppId,
        ["Title"] = Title,
        ["Status"] = Status,
        ["WindowId"] = 0,
        ["IconName"] = IconName,
        ["IconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["OverlayIconName"] = "",
        ["OverlayIconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["AttentionIconName"] = IconName,
        ["AttentionIconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["AttentionMovieName"] = "",
        ["ToolTip"] = (IconName, Array.Empty<(int, int, byte[])>(), "Bambu Bar", Title),
        ["IconAccessibleDesc"] = Title,
        ["AttentionAccessibleDesc"] = Title,
        ["OverlayIconAccessibleDesc"] = "",
        ["ItemIsMenu"] = false,
        ["Menu"] = _menu.ObjectPath,
    };
    public Task<object> GetAsync(string property) => Task.FromResult(Properties[property]);
    public Task<IDictionary<string, object>> GetAllAsync() => Task.FromResult(Properties);
    public Task ActivateAsync(int x, int y) { _panel(x, y); return Task.CompletedTask; }
    public Task SecondaryActivateAsync(int x, int y) { _show(); return Task.CompletedTask; }
    public Task ContextMenuAsync(int x, int y) => Task.CompletedTask;
    public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;
    public Task<IDisposable> WatchNewIconAsync(Action handler) => Task.FromResult(_icon.Watch(handler));
    public Task<IDisposable> WatchNewOverlayIconAsync(Action handler) => Task.FromResult(_overlay.Watch(handler));
    public Task<IDisposable> WatchNewTitleAsync(Action handler) => Task.FromResult(_title.Watch(handler));
    public Task<IDisposable> WatchNewToolTipAsync(Action handler) => Task.FromResult(_tooltip.Watch(handler));
    public Task<IDisposable> WatchNewStatusAsync(Action<string> handler) => Task.FromResult(_status.Watch(handler));
    public void Dispose() { _disposed = true; _store.PropertyChanged -= Changed; _watcher?.Dispose(); _shellWatcher?.Dispose(); _connection.Dispose(); }

    private sealed class Menu((string Label, Action Run)[] entries) : ITrayMenu
    {
        public ObjectPath ObjectPath => new("/Menu");
        private IDictionary<string, object> Props(int id) => id == 0 ? new Dictionary<string, object> { ["children-display"] = "submenu" } : new Dictionary<string, object> { ["label"] = id > 0 && id <= entries.Length ? Loc.T(entries[id - 1].Label) : "", ["enabled"] = true, ["visible"] = true };
        public Task<(uint, (int, IDictionary<string, object>, object[]))> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames) =>
            Task.FromResult((1u, (parentId, Props(parentId), parentId == 0 && recursionDepth != 0 ? Enumerable.Range(1, entries.Length).Select(id => (object)(id, Props(id), Array.Empty<object>())).ToArray() : Array.Empty<object>())));
        public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames) => Task.FromResult((ids.Length == 0 ? Enumerable.Range(0, entries.Length + 1).ToArray() : ids).Select(id => (id, Props(id))).ToArray());
        public Task<object> GetPropertyAsync(int id, string name) => Task.FromResult(Props(id)[name]);
        public Task EventAsync(int id, string eventId, object data, uint timestamp) { if (eventId == "clicked" && id >= 1 && id <= entries.Length) entries[id - 1].Run(); return Task.CompletedTask; }
        public async Task<int[]> EventGroupAsync((int id, string eventId, object data, uint timestamp)[] events) { foreach (var e in events) await EventAsync(e.id, e.eventId, e.data, e.timestamp); return []; }
        public Task<bool> AboutToShowAsync(int id) => Task.FromResult(false);
        public Task<(int[], int[])> AboutToShowGroupAsync(int[] ids) => Task.FromResult((Array.Empty<int>(), Array.Empty<int>()));
        public Task<object> GetAsync(string property) => Task.FromResult(Properties[property]);
        private static IDictionary<string, object> Properties => new Dictionary<string, object> { ["Version"] = 3u, ["TextDirection"] = "ltr", ["Status"] = "normal", ["IconThemePath"] = Array.Empty<string>() };
        public Task<IDictionary<string, object>> GetAllAsync() => Task.FromResult(Properties);
        public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler) => Task.FromResult<IDisposable>(new Subscription(() => { }));
    }
    private sealed class Subscription(Action unsubscribe) : IDisposable { public void Dispose() => unsubscribe(); }
    private sealed class Signal
    {
        private event Action? Handlers;
        public IDisposable Watch(Action handler) { Handlers += handler; return new Subscription(() => Handlers -= handler); }
        public void Emit() => Handlers?.Invoke();
    }
    private sealed class Signal<T>
    {
        private event Action<T>? Handlers;
        public IDisposable Watch(Action<T> handler) { Handlers += handler; return new Subscription(() => Handlers -= handler); }
        public void Emit(T value) => Handlers?.Invoke(value);
    }
}
