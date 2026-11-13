using System.Collections.Concurrent;
using System.Net.Http;
using BambuBar.Core;
using BambuBar.Core.Licensing;

namespace BambuBar.Linux;

/// <summary>License/vault work lives on its own serial context. Unlock dialogs and network calls never block GTK.</summary>
internal sealed class Licensing : IDisposable
{
    private readonly SerialContext _worker = new("BambuBar.License");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LicenseStore _store;
    public bool HasAccess { get; private set; }
    public bool Busy { get; private set; }
    public string? Message { get; private set; }
    public bool HasRecord { get; private set; }
    public event Action? Changed;
    private bool _disposed;
    public Licensing(bool ephemeral)
    {
        var ui = SynchronizationContext.Current!;
        _store = _worker.Invoke(() => new LicenseStore(new PolarClient(activationLabel: id => $"Linux · {id[..Math.Min(8, id.Length)]}"), ephemeral ? new MemoryLicenseVault() : new Vault(_lifetime.Token)));
        _worker.Post(_ =>
        {
            _store.PropertyChanged += (_, _) =>
            {
                var access = _store.HasAccess; var busy = _store.IsBusy; var message = _store.Message; var saved = _store.Record is not null;
                ui.Post(_ => { if (_disposed) return; HasAccess = access; Busy = busy; Message = message; HasRecord = saved; Changed?.Invoke(); }, null);
            };
            if (!ephemeral) _store.Start();
        }, null);
    }
    public void Activate(string key) => _worker.Post(async _ => await _store.ActivateAsync(key), null);
    public void Deactivate() => _worker.Post(async _ => await _store.DeactivateAsync(), null);
    public void Refresh() => _worker.Post(async _ => await _store.RefreshAsync(), null);
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _worker.Post(_ => _store.Stop(), null); _worker.Dispose(); _lifetime.Dispose(); }
    private sealed class Vault(CancellationToken lifetime) : ILicenseVault
    {
        public LicenseRecord? Load()
        {
            var result = Task.Run(() => Platform.Run("secret-tool", ["lookup", "application", Platform.AppId, "kind", "supporter-license"], waitForUser: true, cancellationToken: lifetime)).GetAwaiter().GetResult();
            return result.Exit == 0 && result.Output.Length > 0 ? LicenseRecord.FromJson(result.Output.TrimEnd('\r', '\n')) : null;
        }
        public void Save(LicenseRecord? record)
        {
            var result = Task.Run(() => record is null ? Platform.Run("secret-tool", ["clear", "application", Platform.AppId, "kind", "supporter-license"], waitForUser: true, cancellationToken: lifetime) : Platform.Run("secret-tool", ["store", "--label=Bambu Bar supporter license", "application", Platform.AppId, "kind", "supporter-license"], record.ToJson(), waitForUser: true, cancellationToken: lifetime)).GetAwaiter().GetResult();
            if (result.Exit != 0) throw new IOException("Unlock your keyring to save the supporter license.");
        }
    }
}
