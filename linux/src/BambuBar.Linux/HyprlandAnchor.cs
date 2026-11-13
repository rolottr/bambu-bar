using System.Text.Json;
using Tmds.DBus;
namespace BambuBar.Linux;

[DBusInterface("dev.rolottr.BambuBar.Hyprland")]
public interface IHyprlandAnchor : IDBusObject
{
    Task<string> GetAnchorAsync();
    Task<string> PreparePopupAsync(string token);
    Task<bool> SetAutostartAsync(bool enabled);
    Task<bool> ShowPopupAsync(string token, int width, int height, bool fromTray);
    Task HidePopupAsync(string token);
    Task<IDisposable> WatchDismissedAsync(Action<string> handler);
}
internal sealed record HyprlandAnchor(string Connector, int X, int Y, int Width, int Height)
{
    internal static HyprlandAnchor? Parse(string json)
    {
        if (json.Length > 4096) return null;
        var value = JsonSerializer.Deserialize<HyprlandAnchor>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return value is { Width: > 0 and < 65536, Height: > 0 and < 65536, Connector.Length: > 0 and < 256 } && value.X >= 0 && value.X < value.Width && value.Y >= 0 && value.Y < value.Height ? value : null;
    }
}
