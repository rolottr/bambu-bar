using Tmds.DBus;

namespace BambuBar.Linux;

[DBusInterface("org.freedesktop.portal.Background")]
public interface IBackgroundPortal : IDBusObject
{
    Task<ObjectPath> RequestBackgroundAsync(string parentWindow, IDictionary<string, object> options);
    Task SetStatusAsync(IDictionary<string, object> options);
}
[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(Action<(uint code, IDictionary<string, object> result)> handler);
    Task CloseAsync();
}
internal sealed class Background : IDisposable
{
    private readonly Connection _connection = new(Address.Session);
    private bool _connected;
    private string _localName = "";
    public async Task<bool> Request(bool autostart)
    {
        if (!_connected) { _localName = (await _connection.ConnectAsync()).LocalName; _connected = true; }
        // xdg-desktop-portal-hyprland/gtk have no Background interface. The
        // optional host companion manages only our fixed Flatpak login command.
        if (Platform.IsHyprland)
        {
            try { return await _connection.CreateProxy<IHyprlandAnchor>("dev.rolottr.BambuBar.Hyprland", "/dev/rolottr/BambuBar/Hyprland").SetAutostartAsync(autostart).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { throw new IOException("Install the Bambu Bar Hyprland integration to manage startup at login on this desktop."); }
        }
        var portal = _connection.CreateProxy<IBackgroundPortal>("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop");
        var token = "bambubar" + Guid.NewGuid().ToString("N");
        var request = _connection.CreateProxy<IPortalRequest>("org.freedesktop.portal.Desktop", $"/org/freedesktop/portal/desktop/request/{_localName.TrimStart(':').Replace('.', '_')}/{token}");
        var done = new TaskCompletionSource<bool>();
        using var subscription = await request.WatchResponseAsync(response => done.TrySetResult(response.code == 0 && response.result.TryGetValue(autostart ? "autostart" : "background", out var allowed) && allowed is true));
        var options = new Dictionary<string, object> { { "handle_token", token }, { "reason", "Monitor Bambu Lab printers and notify you when a print needs attention." } };
        // Omitting autostart means false to the portal, even for a permission refresh.
        // Always send the saved preference so launching the app cannot erase its login entry.
        options["autostart"] = autostart;
        options["commandline"] = new[] { "bambu-bar", "--background" };
        options["dbus-activatable"] = false;
        await portal.RequestBackgroundAsync("", options);
        try { return await done.Task.WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { await request.CloseAsync(); return false; }
    }
    public async Task Status(string message)
    {
        if (!_connected) return;
        try { await _connection.CreateProxy<IBackgroundPortal>("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop").SetStatusAsync(new Dictionary<string, object> { { "message", message } }); }
        catch (Exception) { /* Older portal backends have no status method. */ }
    }
    public void Dispose() { _connection.Dispose(); }
}
