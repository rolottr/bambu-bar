using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuBar.Core.App;

namespace BambuBar.Core.Licensing;

// Ported from the Mac's Sources/BambuBar/Licensing/LicenseStore.swift.

/// <summary>This installation's activation, kept locally so upgrades never consume another one.</summary>
public sealed record LicenseRecord(
    string Key, string ActivationId, string InstallationId, DateTimeOffset? ValidatedAt = null, DateTimeOffset? ExpiresAt = null)
{
    /// <summary>How long supporter benefits outlast the last successful check while Polar can't be reached.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(7);

    /// <summary>True within <see cref="OfflineGrace"/> of the last successful check, never before it (a clock moved
    /// backwards) and never past the license's expiry.</summary>
    public bool PermitsOfflineUse(DateTimeOffset now)
    {
        if (ValidatedAt is not { } validated || now < validated || now - validated >= OfflineGrace) return false;
        return ExpiresAt is not { } expiry || expiry > now;
    }

    /// <summary>The record as one JSON string, for a vault that stores text (Credential Manager).</summary>
    public string ToJson() => new JsonObject
    {
        ["key"] = Key,
        ["activationID"] = ActivationId,
        ["installationID"] = InstallationId,
        ["validatedAt"] = ValidatedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["expiresAt"] = ExpiresAt?.ToString("O", CultureInfo.InvariantCulture),
    }.ToJsonString();

    /// <summary>Reads <see cref="ToJson"/>'s text; throws <see cref="JsonException"/> or
    /// <see cref="FormatException"/> when it isn't one.</summary>
    public static LicenseRecord FromJson(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        static string Required(JsonObject o, string name) =>
            o[name]?.GetValue<string>() ?? throw new JsonException($"missing {name}");
        static DateTimeOffset? Date(JsonObject o, string name) => o[name]?.GetValue<string>() is { } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
        return new(Required(o, "key"), Required(o, "activationID"), Required(o, "installationID"),
            Date(o, "validatedAt"), Date(o, "expiresAt"));
    }
}

/// <summary>Where the license record lives (the Mac's Keychain item). The Windows app keeps it in Credential Manager
/// (<c>Platform/CredentialStore.Read/Write</c>, as <see cref="LicenseRecord.ToJson"/>), local to this PC and user.
/// Failures may throw anything; <see cref="LicenseStore"/> reports them as <see cref="LicenseFailure.Storage"/>.</summary>
public interface ILicenseVault
{
    LicenseRecord? Load();

    /// <summary>Replaces the record; null deletes it.</summary>
    void Save(LicenseRecord? record);
}

/// <summary>A record held for this run only: demo and developer sessions, and tests.</summary>
public sealed class MemoryLicenseVault(LicenseRecord? record = null) : ILicenseVault
{
    public LicenseRecord? Record { get; private set; } = record;
    public LicenseRecord? Load() => Record;
    public void Save(LicenseRecord? record) => Record = record;
}

/// <summary>
/// The supporter license: activation, revalidation every 15 minutes, a 7-day offline grace and deactivation.
/// Single-threaded like the Mac's <c>@MainActor</c> store: create it and call every member on the UI thread. Its
/// awaits resume on the caller's context and it starts no threads of its own.
/// </summary>
public sealed class LicenseStore : Observable
{
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(15);

    private readonly ILicenseApi _api;
    private readonly ILicenseVault _vault;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _schedule;
    private bool _storageReady;

    /// <param name="now">The clock (tests set it).</param>
    /// <param name="delay">Waits between revalidations (tests replace it); <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public LicenseStore(ILicenseApi api, ILicenseVault vault, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _vault = vault;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public bool HasAccess { get; private set => Set(ref field, value); }
    public bool IsInitializing { get; private set => Set(ref field, value); } = true;
    public bool IsBusy { get; private set => Set(ref field, value); }
    public string? Message { get; private set => Set(ref field, value); }
    public LicenseRecord? Record { get; private set => Set(ref field, value); }

    /// <summary>Supporter access changed (and once at the end of initialization): the updater listens.</summary>
    public event Action<bool>? AccessChanged;

    /// <summary>Validates now, then every <see cref="RevalidationInterval"/> until <see cref="Stop"/>.</summary>
    public void Start()
    {
        if (_schedule is not null) return;
        _schedule = new CancellationTokenSource();
        _ = RunScheduleAsync(_schedule.Token);
    }

    public void Stop()
    {
        _schedule?.Cancel();
        _schedule = null;
    }

    private async Task RunScheduleAsync(CancellationToken cancellation)
    {
        await RefreshAsync();
        while (!cancellation.IsCancellationRequested)
        {
            try { await _delay(RevalidationInterval, cancellation); }
            catch (OperationCanceledException) { return; }
            await RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!_storageReady)
            {
                Record = LoadRecord();
                _storageReady = true;
            }
            if (Record is not { } saved)
            {
                SetAccess(false);
                return;
            }
            try
            {
                var license = await _api.ValidateAsync(saved);
                license.Verify(saved.ActivationId, _now());
                saved = saved with { ValidatedAt = _now(), ExpiresAt = license.ExpiresAt };
                // Persist before enabling: a failed vault write is not an offline network failure.
                SaveRecord(saved);
                Record = saved;
                Message = null;
                SetAccess(true);
            }
            catch (LicenseException e) when (e.Failure == LicenseFailure.Unavailable)
            {
                SetAccess(saved.PermitsOfflineUse(_now()));
                Message = HasAccess
                    ? Loc.T("The licensing service is temporarily unreachable. Supporter benefits remain active for up to seven days after your last successful license check.")
                    : Loc.T("Connect to the internet to restore supporter benefits. Printer monitoring remains available.");
            }
            catch (LicenseException)
            {
                // Retain the activation so a refusal never silently claims a new slot.
                saved = saved with { ValidatedAt = null };
                Record = saved;
                SetAccess(false);
                SaveRecord(saved);
                throw;
            }
        }
        catch (LicenseException e)
        {
            SetAccess(false);
            Message = e.Message;
        }
        finally
        {
            IsBusy = false;
            IsInitializing = false;
        }
    }

    public async Task ActivateAsync(string key)
    {
        if (IsBusy) return;
        key = key.Trim();
        if (key.Length == 0) return;
        IsBusy = true;
        try
        {
            if (!_storageReady)
            {
                Record = LoadRecord();
                _storageReady = true;
            }
            if (Record is not null)
            {
                Message = Loc.T("Deactivate this installation before entering a different license. Use Check License to retry your saved activation.");
                return;
            }
            var installationId = Guid.NewGuid().ToString("D");
            var activation = await _api.ActivateAsync(key, installationId);
            var saved = new LicenseRecord(key, activation.Id, installationId, _now(), activation.LicenseKey.ExpiresAt);
            try
            {
                if (!Guid.TryParseExact(activation.Id, "D", out _)) throw new LicenseException(LicenseFailure.InvalidResponse);
                activation.LicenseKey.Verify(null, _now());
                SaveRecord(saved);
            }
            catch (LicenseException)
            {
                // If local storage fails, release the newly claimed slot when possible.
                try { await _api.DeactivateAsync(saved); }
                catch (LicenseException) { }
                throw;
            }
            Record = saved;
            Message = null;
            SetAccess(true);
        }
        catch (LicenseException e)
        {
            Message = Loc.F("%@ If an activation appeared in your account, remove it there before retrying.", e.Message);
        }
        finally
        {
            IsBusy = false;
            IsInitializing = false;
        }
    }

    public async Task DeactivateAsync()
    {
        if (IsBusy || Record is not { } saved) return;
        IsBusy = true;
        try
        {
            await _api.DeactivateAsync(saved);
            // Never keep access after server-side deactivation, even if local deletion fails.
            Record = saved with { ValidatedAt = null };
            SetAccess(false);
            SaveRecord(null);
            Record = null;
            Message = null;
        }
        catch (LicenseException e) { Message = e.Message; }
        finally { IsBusy = false; }
    }

    private LicenseRecord? LoadRecord()
    {
        try { return _vault.Load(); }
        catch (Exception e) when (e is not LicenseException) { throw new LicenseException(LicenseFailure.Storage, e); }
    }

    private void SaveRecord(LicenseRecord? record)
    {
        try { _vault.Save(record); }
        catch (Exception e) when (e is not LicenseException) { throw new LicenseException(LicenseFailure.Storage, e); }
    }

    private void SetAccess(bool value)
    {
        if (value == HasAccess && !IsInitializing) return;
        HasAccess = value;
        AccessChanged?.Invoke(value);
    }
}
