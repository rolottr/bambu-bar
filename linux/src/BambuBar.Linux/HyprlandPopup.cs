using Tmds.DBus;
namespace BambuBar.Linux;

// Hyprland deliberately hides layer-shell from Flatpak clients. The host companion
// positions only this randomly identified Quick view, never an arbitrary window.
internal sealed class HyprlandPopup(Action dismiss) : IDisposable
{
    private readonly Connection _connection = new(Address.Session);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private IDisposable? _subscription;
    private IHyprlandAnchor? _proxy;
    private bool _disposed;
    internal string Token { get; } = Guid.NewGuid().ToString("N");
    public async Task<bool> Show(int width, int height, bool fromTray)
    {
        try
        {
            if (_proxy is null)
            {
                await _connection.ConnectAsync();
                _proxy = _connection.CreateProxy<IHyprlandAnchor>("dev.rolottr.BambuBar.Hyprland", "/dev/rolottr/BambuBar/Hyprland");
                _subscription = await _proxy.WatchDismissedAsync(token =>
                {
                    if (token == Token) _context?.Post(_ => { if (!_disposed) dismiss(); }, null);
                });
            }
            return await _proxy.ShowPopupAsync(Token, width, height, fromTray).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { return false; }
    }
    public async Task Hide()
    {
        try { if (_proxy is not null) await _proxy.HidePopupAsync(Token).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* Owner loss also clears the host dismissal surfaces. */ }
    }
    public void Dispose() { _disposed = true; _subscription?.Dispose(); _connection.Dispose(); }
}
