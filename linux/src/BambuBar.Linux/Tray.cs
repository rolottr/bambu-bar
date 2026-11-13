using Tmds.DBus;
using BambuBar.Core;
using BambuBar.Core.App;

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
    private readonly Action _panel, _show, _quit;
    private readonly Menu _menu;
    private IDisposable? _watcher;
    private bool _disposed;
    private readonly Signal _icon = new(), _title = new(), _tooltip = new();
    private readonly Signal<string> _status = new();
    private string _last = "";
    public ObjectPath ObjectPath => new("/StatusNotifierItem");
    public bool Available { get; private set; }

    public Tray(PrinterStore store, Action panel, Action show, Action quit)
    {
        _store = store; _panel = panel; _show = show; _quit = quit;
        _menu = new Menu(panel, show, quit);
    }
    public async Task Start()
    {
        await _connection.ConnectAsync();
        await _connection.RegisterObjectAsync(this);
        await _connection.RegisterObjectAsync(_menu);
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
    private string Title => _store.MenuBarSession is { } s ? $"{s.Config.Name} · {s.ShortState}" : "Bambu Bar";
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_last == Title || _disposed) return; _last = Title;
        _title.Emit(); _icon.Emit(); _tooltip.Emit(); _status.Emit(Status);
    }
    private string Status => _store.MenuBarSession?.Status?.State is BambuBar.Core.Model.JobState.Paused or BambuBar.Core.Model.JobState.Failed ? "NeedsAttention" : "Active";
    private IDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["Category"] = "Hardware",
        ["Id"] = Platform.AppId,
        ["Title"] = Title,
        ["Status"] = Status,
        ["WindowId"] = 0,
        ["IconName"] = "printer-symbolic",
        ["IconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["OverlayIconName"] = "",
        ["OverlayIconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["AttentionIconName"] = "printer-error-symbolic",
        ["AttentionIconPixmap"] = Array.Empty<(int, int, byte[])>(),
        ["AttentionMovieName"] = "",
        ["ToolTip"] = ("printer-symbolic", Array.Empty<(int, int, byte[])>(), "Bambu Bar", Title),
        ["IconAccessibleDesc"] = Title,
        ["AttentionAccessibleDesc"] = Title,
        ["OverlayIconAccessibleDesc"] = "",
        ["ItemIsMenu"] = false,
        ["Menu"] = _menu.ObjectPath,
    };
    public Task<object> GetAsync(string property) => Task.FromResult(Properties[property]);
    public Task<IDictionary<string, object>> GetAllAsync() => Task.FromResult(Properties);
    public Task ActivateAsync(int x, int y) { _panel(); return Task.CompletedTask; }
    public Task SecondaryActivateAsync(int x, int y) { _show(); return Task.CompletedTask; }
    public Task ContextMenuAsync(int x, int y) => Task.CompletedTask;
    public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;
    public Task<IDisposable> WatchNewIconAsync(Action handler) => Task.FromResult(_icon.Watch(handler));
    public Task<IDisposable> WatchNewTitleAsync(Action handler) => Task.FromResult(_title.Watch(handler));
    public Task<IDisposable> WatchNewToolTipAsync(Action handler) => Task.FromResult(_tooltip.Watch(handler));
    public Task<IDisposable> WatchNewStatusAsync(Action<string> handler) => Task.FromResult(_status.Watch(handler));
    public void Dispose() { _disposed = true; _store.PropertyChanged -= Changed; _watcher?.Dispose(); _connection.Dispose(); }

    private sealed class Menu(Action panel, Action show, Action quit) : ITrayMenu
    {
        public ObjectPath ObjectPath => new("/Menu");
        private readonly Action[] _actions = [panel, show, quit];
        private static IDictionary<string, object> Props(int id) => id == 0 ? new Dictionary<string, object> { ["children-display"] = "submenu" } : new Dictionary<string, object> { ["label"] = id switch { 1 => "Quick view", 2 => "Open Bambu Bar", _ => "Quit" }, ["enabled"] = true, ["visible"] = true };
        public Task<(uint, (int, IDictionary<string, object>, object[]))> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames) =>
            Task.FromResult((1u, (parentId, Props(parentId), parentId == 0 && recursionDepth != 0 ? new object[] { (1, Props(1), Array.Empty<object>()), (2, Props(2), Array.Empty<object>()), (3, Props(3), Array.Empty<object>()) } : Array.Empty<object>())));
        public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames) => Task.FromResult((ids.Length == 0 ? [0, 1, 2, 3] : ids).Select(id => (id, Props(id))).ToArray());
        public Task<object> GetPropertyAsync(int id, string name) => Task.FromResult(Props(id)[name]);
        public Task EventAsync(int id, string eventId, object data, uint timestamp) { if (eventId == "clicked" && id is >= 1 and <= 3) _actions[id - 1](); return Task.CompletedTask; }
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
