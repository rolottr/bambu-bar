using System.Runtime.InteropServices;
using Tmds.DBus;

namespace BambuBar.Linux;

// A regular xdg_toplevel cannot choose its location on Wayland. Plasma/Hyprland's
// Quick view uses layer shell; the main window and GNOME remain normal GTK windows.
[DBusInterface("org.freedesktop.portal.Settings")]
public interface ITrayCoordinateSettings : IDBusObject
{
    Task<object> ReadAsync(string ns, string key);
}

internal sealed class TrayPlacement(Gtk.Window window)
{
    private const string Library = "libgtk4-layer-shell.so.0";
    private static bool _loaded;
    private static bool _nativeCoordinates = true;
    public int AvailableHeight { get; private set; } = 700;
    private (int X, int Y)? _lastAnchor;
    private nint Window => window.Handle.DangerousGetHandle();

    public static void LoadBeforeGtk()
    {
        if ((!Platform.IsPlasma && !Platform.IsHyprland) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        var library = Path.Combine(AppContext.BaseDirectory, "layer-shell", Library);
        if (!File.Exists(library)) return;
        // Interpose Wayland calls before GTK loads libwayland-client (upstream requirement).
        _loaded = dlopen(library, 2 | 0x100) != 0; // RTLD_NOW | RTLD_GLOBAL
    }
    public static async Task ReadCoordinateMode()
    {
        if (!_loaded) return;
        if (Platform.IsHyprland) { _nativeCoordinates = false; return; }
        // Plasma's SNI coordinates follow this KDE setting, including on Wayland.
        // Absent means true in Plasma. The portal exposes KDE values as strings.
        try
        {
            using var connection = new Connection(Address.Session);
            await connection.ConnectAsync();
            var settings = connection.CreateProxy<ITrayCoordinateSettings>("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop");
            var value = await settings.ReadAsync("org.kde.kdeglobals.KScreen", "XwaylandClientsScale").WaitAsync(TimeSpan.FromSeconds(2));
            if (bool.TryParse(value.ToString(), out var native)) _nativeCoordinates = native;
        }
        catch (Exception) { /* Missing key/provider: use Plasma's default. */ }
    }
    public static TrayPlacement? Create(Gtk.Window window)
    {
        if (!_loaded || gtk_layer_is_supported() == 0) return null;
        var placement = new TrayPlacement(window);
        gtk_layer_init_for_window(placement.Window);
        gtk_layer_set_namespace(placement.Window, Platform.AppId + ".QuickView");
        gtk_layer_set_layer(placement.Window, 3); // Overlay, above the desktop panel.
        gtk_layer_set_exclusive_zone(placement.Window, -1); // Position against full monitor geometry.
        gtk_layer_set_keyboard_mode(placement.Window, 2); // On demand, never lock focus.
        return placement;
    }
    public async Task Position(int x, int y)
    {
        if ((x, y) != (-1, -1)) _lastAnchor = (x, y);
        // Waybar uses logical coordinates; Quickshell sends (0,0). Neither uses
        // Plasma's XwaylandClientsScale convention. The read-only host bridge also
        // identifies the connector because Wayland GDK monitor origins may all be 0.
        var hypr = Platform.IsHyprland ? await HyprlandAnchor.Read() : null;
        var monitors = Gdk.Display.GetDefault()!.GetMonitors();
        Gdk.Monitor? chosen = null;
        for (uint i = 0; i < monitors.GetNItems(); i++)
        {
            if (monitors.GetObject(i) is not Gdk.Monitor monitor) continue;
            chosen ??= monitor;
            if (hypr is not null) { if (monitor.GetConnector() == hypr.Connector) { chosen = monitor; break; } continue; }
            monitor.GetGeometry(out var bounds);
            using (bounds)
                if (_lastAnchor is { } point)
                {
                    var scale = _nativeCoordinates ? monitor.GetScale() : 1;
                    var logicalX = point.X / scale; var logicalY = point.Y / scale;
                    if (logicalX >= bounds.X && logicalX < bounds.X + bounds.Width && logicalY >= bounds.Y && logicalY < bounds.Y + bounds.Height) { chosen = monitor; break; }
                }
        }
        if (chosen is null) return;
        gtk_layer_set_monitor(Window, chosen.Handle.DangerousGetHandle());
        chosen.GetGeometry(out var area);
        using (area)
        {
            var scale = _nativeCoordinates ? chosen.GetScale() : 1;
            var anchor = _lastAnchor is { } raw ? ((int)Math.Round(raw.X / scale), (int)Math.Round(raw.Y / scale)) : (area.X + area.Width - 32, area.Y + area.Height - 32);
            var width = hypr?.Width ?? area.Width; var height = hypr?.Height ?? area.Height;
            var px = hypr?.X ?? Math.Clamp(anchor.Item1 - area.X, 0, width);
            var py = hypr?.Y ?? Math.Clamp(anchor.Item2 - area.Y, 0, height);
            // A missing bridge with a coordinate-less host has no truthful anchor.
            // Use the top-right tray area as a documented fallback, never the center.
            if (Platform.IsHyprland && hypr is null && (x, y) is (0, 0) or (-1, -1)) { px = width - 32; py = 13; }
            var distances = new[] { px, width - px, py, height - py };
            var edge = Array.IndexOf(distances, distances.Min());
            Platform.Trace($"tray coordinates {x},{y}; scale={scale}; monitor={area.X},{area.Y} {area.Width}x{area.Height}; local={px},{py}; edge={edge}");
            AvailableHeight = Math.Max(1, height - (edge >= 2 ? distances[edge] + 24 + 8 : 16));
            for (var e = 0; e < 4; e++) { gtk_layer_set_anchor(Window, e, 0); gtk_layer_set_margin(Window, e, 0); }
            gtk_layer_set_anchor(Window, edge, 1);
            // Leave half an icon plus a gap between the click and the popup edge.
            gtk_layer_set_margin(Window, edge, distances[edge] + 24);
            if (edge >= 2)
            {
                gtk_layer_set_anchor(Window, 0, 1);
                gtk_layer_set_margin(Window, 0, Math.Clamp(px - 210, 8, Math.Max(8, width - 428)));
            }
            else
            {
                gtk_layer_set_anchor(Window, py < height / 2 ? 2 : 3, 1);
                gtk_layer_set_margin(Window, py < height / 2 ? 2 : 3, 8);
            }
        }
    }
    private readonly List<Gtk.Window> _backdrops = [];
    public void ShowDismissalSurfaces(Action dismiss)
    {
        if (!Platform.IsHyprland) return;
        ClearDismissalSurfaces();
        // Hyprland doesn't change keyboard focus when clicking empty wallpaper.
        // A transparent input surface gives the popup normal outside-click semantics,
        // including clicks on another output. It has no keyboard focus or reserved area.
        var monitors = Gdk.Display.GetDefault()!.GetMonitors();
        for (uint i = 0; i < monitors.GetNItems(); i++)
        {
            if (monitors.GetObject(i) is not Gdk.Monitor monitor) continue;
            var backdrop = Gtk.Window.New();
            backdrop.AddCssClass("tray-dismissal"); backdrop.SetDecorated(false);
            var handle = backdrop.Handle.DangerousGetHandle();
            gtk_layer_init_for_window(handle);
            gtk_layer_set_namespace(handle, Platform.AppId + ".Dismiss");
            gtk_layer_set_monitor(handle, monitor.Handle.DangerousGetHandle());
            gtk_layer_set_layer(handle, 3); gtk_layer_set_keyboard_mode(handle, 0);
            gtk_layer_set_exclusive_zone(handle, -1);
            for (var edge = 0; edge < 4; edge++) gtk_layer_set_anchor(handle, edge, 1);
            var area = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            var clicks = Gtk.GestureClick.New(); clicks.SetButton(0);
            clicks.OnPressed += (_, _) => dismiss(); area.AddController(clicks);
            backdrop.SetChild(area); _backdrops.Add(backdrop); backdrop.SetVisible(true);
        }
    }
    public void ClearDismissalSurfaces()
    {
        foreach (var backdrop in _backdrops) { backdrop.Destroy(); backdrop.Dispose(); }
        _backdrops.Clear();
    }
    [DllImport("libdl.so.2")] private static extern nint dlopen(string filename, int flags);
    [DllImport(Library)] private static extern int gtk_layer_is_supported();
    [DllImport(Library)] private static extern void gtk_layer_init_for_window(nint window);
    [DllImport(Library)] private static extern void gtk_layer_set_namespace(nint window, string name);
    [DllImport(Library)] private static extern void gtk_layer_set_layer(nint window, int layer);
    [DllImport(Library)] private static extern void gtk_layer_set_exclusive_zone(nint window, int zone);
    [DllImport(Library)] private static extern void gtk_layer_set_keyboard_mode(nint window, int mode);
    [DllImport(Library)] private static extern void gtk_layer_set_monitor(nint window, nint monitor);
    [DllImport(Library)] private static extern void gtk_layer_set_anchor(nint window, int edge, int anchor);
    [DllImport(Library)] private static extern void gtk_layer_set_margin(nint window, int edge, int margin);
}
