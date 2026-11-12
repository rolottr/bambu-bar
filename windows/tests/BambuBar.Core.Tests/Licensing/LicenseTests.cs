using System.Net;
using System.Text;
using System.Text.Json;
using BambuBar.Core.Licensing;

namespace BambuBar.Core.Tests.Licensing;

// Ported from the Mac's Tests/LicensingTests/LicenseTests.swift, plus the Windows activation label (D14), the
// 15-minute schedule and the record's vault format. Everything runs offline: Polar is a fake or a fake handler.

public class LicenseTests
{
    private const string ActivationId = "7103adad-ddca-4c23-a523-911fb02fc5f2";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static PolarLicense Granted(string? activation = ActivationId, string benefit = PolarConfiguration.BenefitId,
        DateTimeOffset? expiry = null) =>
        new(PolarConfiguration.OrganizationId, benefit, "granted", expiry, activation);

    private static LicenseRecord Saved(DateTimeOffset? at) => new("test-key", ActivationId, "installation", at);
    private static LicenseRecord Saved() => Saved(FixedNow);

    private sealed class MemoryLicense(LicenseRecord? record = null) : ILicenseVault
    {
        public LicenseRecord? Record = record;
        public bool FailsWrites;
        public LicenseRecord? Load() => Record;
        public void Save(LicenseRecord? record)
        {
            if (FailsWrites) throw new IOException("write refused");
            Record = record;
        }
    }

    private sealed class FakePolar : ILicenseApi
    {
        public Func<PolarLicense> Validation = () => Granted();
        public Func<PolarActivation> Activation = () => new(ActivationId, Granted(activation: null));
        public LicenseException? DeactivationError;
        public int Activations, Deactivations;
        public LicenseRecord? ValidatedRecord;
        public TaskCompletionSource<PolarLicense>? PendingValidation;

        public Task<PolarActivation> ActivateAsync(string key, string installationId, CancellationToken cancellation = default)
        {
            Activations++;
            return Task.FromResult(Activation());
        }

        public Task<PolarLicense> ValidateAsync(LicenseRecord record, CancellationToken cancellation = default)
        {
            ValidatedRecord = record;
            return PendingValidation?.Task ?? Task.FromResult(Validation());
        }

        public Task DeactivateAsync(LicenseRecord record, CancellationToken cancellation = default)
        {
            Deactivations++;
            return DeactivationError is { } e ? Task.FromException(e) : Task.CompletedTask;
        }
    }

    private static Func<T> Fails<T>(LicenseFailure failure) => () => throw new LicenseException(failure);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupWaitsForValidationBeforeShowingAccessOrActivation(bool rejected)
    {
        var api = new FakePolar { PendingValidation = new() };
        var store = new LicenseStore(api, new MemoryLicense(Saved()), () => FixedNow);
        Assert.True(store.IsInitializing);
        Assert.False(store.HasAccess);

        var refresh = store.RefreshAsync();
        // Polar has received the check but has not answered yet.
        Assert.NotNull(api.ValidatedRecord);
        Assert.True(store.IsInitializing);
        Assert.True(store.IsBusy);
        Assert.False(store.HasAccess);
        if (rejected) api.PendingValidation.SetException(new LicenseException(LicenseFailure.Rejected));
        else api.PendingValidation.SetResult(Granted());
        await refresh;
        Assert.False(store.IsInitializing);
        Assert.False(store.IsBusy);
        Assert.Equal(!rejected, store.HasAccess);
    }

    [Fact]
    public async Task FirstLaunchWithoutLicenseFinishesInitialization()
    {
        var store = new LicenseStore(new FakePolar(), new MemoryLicense(), () => FixedNow);
        Assert.True(store.IsInitializing);
        await store.RefreshAsync();
        Assert.False(store.IsInitializing);
        Assert.False(store.HasAccess);
        Assert.Null(store.Record);
    }

    [Fact]
    public async Task SupporterChangesNotifyUpdaterWithoutConsumingAnotherActivation()
    {
        var api = new FakePolar();
        var store = new LicenseStore(api, new MemoryLicense(), () => FixedNow);
        var changes = new List<bool>();
        store.AccessChanged += changes.Add;
        await store.RefreshAsync();
        Assert.Equal([false], changes);
        await store.ActivateAsync("test-key");
        Assert.Equal([false, true], changes);
        await store.RefreshAsync();
        Assert.Equal([false, true], changes);
        api.Validation = Fails<PolarLicense>(LicenseFailure.Rejected);
        await store.RefreshAsync();
        Assert.Equal([false, true, false], changes);
        api.Validation = () => Granted();
        await store.RefreshAsync();
        Assert.Equal([false, true, false, true], changes);
        await store.DeactivateAsync();
        Assert.Equal([false, true, false, true, false], changes);
        Assert.Equal(1, api.Activations);
    }

    [Fact]
    public async Task ActivationPersistsAndRelaunchValidatesSameSlot()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense();
        var first = new LicenseStore(api, storage, () => FixedNow);
        await first.ActivateAsync("  test-key\n");
        Assert.True(first.HasAccess);
        Assert.Equal("test-key", storage.Record?.Key);
        var second = new LicenseStore(api, storage, () => FixedNow);
        Assert.False(second.HasAccess);
        await second.RefreshAsync();
        Assert.True(second.HasAccess);
        Assert.Equal(1, api.Activations);
        Assert.Equal(ActivationId, api.ValidatedRecord?.ActivationId);
        Assert.Equal(storage.Record?.InstallationId, api.ValidatedRecord?.InstallationId);
    }

    [Fact]
    public async Task ExistingActivationCannotConsumeAnotherSlot()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense(Saved());
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.ActivateAsync("another-key");
        Assert.Equal(0, api.Activations);
        Assert.Equal("test-key", storage.Record?.Key);
    }

    [Fact]
    public async Task RevokedActivationRemovesSupporterBenefitsAndNeverAutoReactivates()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense(Saved());
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.RefreshAsync();
        Assert.True(store.HasAccess);
        api.Validation = Fails<PolarLicense>(LicenseFailure.Rejected);
        await store.RefreshAsync();
        Assert.False(store.HasAccess);
        Assert.Null(storage.Record?.ValidatedAt);
        api.Validation = Fails<PolarLicense>(LicenseFailure.Unavailable);
        var restarted = new LicenseStore(api, storage, () => FixedNow);
        await restarted.RefreshAsync();
        Assert.False(restarted.HasAccess);
        Assert.Equal(0, api.Activations);
    }

    [Fact]
    public async Task OutagesAllowOnlyBoundedOfflineUse()
    {
        var api = new FakePolar { Validation = Fails<PolarLicense>(LicenseFailure.Unavailable) };
        var storage = new MemoryLicense(Saved());
        var clock = FixedNow.AddDays(6);
        var store = new LicenseStore(api, storage, () => clock);
        await store.RefreshAsync();
        Assert.True(store.HasAccess);
        clock = FixedNow.AddDays(7);
        await store.RefreshAsync();
        Assert.False(store.HasAccess);
        Assert.Equal(FixedNow, storage.Record?.ValidatedAt); // Offline retries never extend grace.
    }

    [Fact]
    public void ExpiryAndClockRollbackCannotExtendOfflineUse()
    {
        Assert.False(Saved().PermitsOfflineUse(FixedNow.AddSeconds(-1)));
        var record = Saved() with { ExpiresAt = FixedNow.AddSeconds(30) };
        Assert.False(record.PermitsOfflineUse(FixedNow.AddSeconds(31)));
        Assert.False(Saved(null).PermitsOfflineUse(FixedNow));
    }

    [Fact]
    public async Task OtherProductAndMissingActivationAreRejected()
    {
        foreach (var license in new[] { Granted(benefit: "other-product"), Granted(activation: null), Granted(activation: "another-install"), Granted(expiry: FixedNow) })
        {
            var api = new FakePolar { Validation = () => license };
            var storage = new MemoryLicense(Saved());
            var store = new LicenseStore(api, storage, () => FixedNow);
            await store.RefreshAsync();
            Assert.False(store.HasAccess);
            Assert.Null(storage.Record?.ValidatedAt);
        }
    }

    [Fact]
    public async Task ActivationLimitDoesNotUnlockOrSaveKey()
    {
        var api = new FakePolar { Activation = Fails<PolarActivation>(LicenseFailure.ActivationLimit) };
        var storage = new MemoryLicense();
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.ActivateAsync("test-key");
        Assert.False(store.HasAccess);
        Assert.Null(storage.Record);
        Assert.Contains("deactivate", store.Message);
    }

    [Fact]
    public async Task FailedPersistenceRollsBackNewActivation()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense { FailsWrites = true };
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.ActivateAsync("test-key");
        Assert.False(store.HasAccess);
        Assert.Equal(1, api.Deactivations);
        Assert.Null(storage.Record);
    }

    [Fact]
    public async Task FailedDeactivationKeepsRecordForRetry()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense(Saved());
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.RefreshAsync();
        api.DeactivationError = new LicenseException(LicenseFailure.Unavailable);
        await store.DeactivateAsync();
        Assert.True(store.HasAccess);
        Assert.NotNull(storage.Record);
        api.DeactivationError = null;
        await store.DeactivateAsync();
        Assert.False(store.HasAccess);
        Assert.Null(storage.Record);
    }

    [Fact]
    public async Task DeactivationRemovesSupporterBenefitsEvenIfVaultDeleteFails()
    {
        var api = new FakePolar();
        var storage = new MemoryLicense(Saved());
        var store = new LicenseStore(api, storage, () => FixedNow);
        await store.RefreshAsync();
        storage.FailsWrites = true;
        await store.DeactivateAsync();
        Assert.False(store.HasAccess);
        Assert.NotNull(store.Record);
        Assert.Null(store.Record.ValidatedAt);
    }

    /// <summary>Answers every request with <see cref="Status"/> and <see cref="Response"/>, and keeps what was sent.</summary>
    private sealed class PolarHttpFixture : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = "";
        public HttpRequestMessage? LastRequest;
        public JsonElement LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8) };
        }
    }

    private static string Wire(string expiresAt) =>
        $$$"""
        {"organization_id":"{{{PolarConfiguration.OrganizationId}}}","benefit_id":"{{{PolarConfiguration.BenefitId}}}",
         "status":"granted","expires_at":{{{expiresAt}}},"activation":{"id":"{{{ActivationId}}}"}}
        """;

    [Fact]
    public async Task PublicClientSendsScopedActivationAndHandlesHttpFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new PolarHttpFixture { Response = Wire("null") };
        var api = new PolarClient(http);
        var result = await api.ValidateAsync(Saved(), ct);
        result.Verify(ActivationId, FixedNow);
        Assert.Equal("https://api.polar.sh/v1/customer-portal/license-keys/validate", http.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, http.LastRequest?.Method);
        Assert.Equal("application/json", http.LastRequest?.Content?.Headers.ContentType?.ToString());
        Assert.Null(http.LastRequest?.Headers.Authorization);
        Assert.False(http.LastRequest?.Headers.Contains("Authorization"));
        Assert.Equal(ActivationId, http.LastBody.GetProperty("activation_id").GetString());
        Assert.Equal(PolarConfiguration.BenefitId, http.LastBody.GetProperty("benefit_id").GetString());
        Assert.Equal(PolarConfiguration.OrganizationId, http.LastBody.GetProperty("organization_id").GetString());
        Assert.Equal("installation", http.LastBody.GetProperty("conditions").GetProperty("installation_id").GetString());
        Assert.False(http.LastBody.TryGetProperty("increment_usage", out _));

        http.Status = HttpStatusCode.NotFound;
        await api.DeactivateAsync(Saved(), ct); // Portal already removed it.
        var e = await Assert.ThrowsAsync<LicenseException>(() => api.ValidateAsync(Saved(), ct));
        Assert.Equal(LicenseFailure.Rejected, e.Failure); // 404 must reject validation.
        http.Status = HttpStatusCode.Forbidden;
        e = await Assert.ThrowsAsync<LicenseException>(() => api.DeactivateAsync(Saved(), ct));
        Assert.Equal(LicenseFailure.Rejected, e.Failure); // 403 must not clear a local activation.
        e = await Assert.ThrowsAsync<LicenseException>(() => api.ActivateAsync("test-key", "installation", ct));
        Assert.Equal(LicenseFailure.ActivationLimit, e.Failure);
        http.Status = HttpStatusCode.ServiceUnavailable;
        e = await Assert.ThrowsAsync<LicenseException>(() => api.ValidateAsync(Saved(), ct));
        Assert.Equal(LicenseFailure.Unavailable, e.Failure); // 503 must be an outage.
    }

    [Fact]
    public void ActualWireResponseDecodesAndChecksScope()
    {
        var license = PolarLicense.FromJson(Encoding.UTF8.GetBytes(Wire("\"2030-01-01T12:30:00.123456Z\"")));
        license.Verify(ActivationId, FixedNow);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 12, 30, 0, 123, 456, TimeSpan.Zero), license.ExpiresAt);
        Assert.Throws<LicenseException>(() => license.Verify("wrong", FixedNow));
    }

    // Beyond the Swift tests.

    /// <summary>The activation the Windows app claims: same body as the Mac's, labelled "Windows · " (D14), and a
    /// 200 without the fields Polar always sends is not a license.</summary>
    [Fact]
    public async Task ActivationIsLabelledWindowsAndMalformedAnswersAreInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new PolarHttpFixture { Response = $$"""{"id":"{{ActivationId}}","license_key":""" + Wire("\"2030-01-01T12:30:00Z\"") + "}" };
        var api = new PolarClient(http);
        var activation = await api.ActivateAsync("test-key", "0123456789abcdef", ct);
        Assert.Equal(ActivationId, activation.Id);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 12, 30, 0, TimeSpan.Zero), activation.LicenseKey.ExpiresAt);
        Assert.Equal("https://api.polar.sh/v1/customer-portal/license-keys/activate", http.LastRequest?.RequestUri?.ToString());
        Assert.Equal("test-key", http.LastBody.GetProperty("key").GetString());
        Assert.Equal("Windows · 01234567", http.LastBody.GetProperty("label").GetString());
        Assert.Equal("0123456789abcdef", http.LastBody.GetProperty("conditions").GetProperty("installation_id").GetString());
        Assert.Equal(PolarConfiguration.OrganizationId, http.LastBody.GetProperty("organization_id").GetString());

        foreach (var body in new[] { "", "[]", """{"status":"granted"}""", Wire("\"2030-01-01 12:30:00\""), Wire("\"2030-01-01T12:30:00\"") })
        {
            http.Response = body;
            var e = await Assert.ThrowsAsync<LicenseException>(() => api.ValidateAsync(Saved(), ct));
            Assert.Equal(LicenseFailure.InvalidResponse, e.Failure);
        }
        http.Status = HttpStatusCode.Found;
        Assert.Equal(LicenseFailure.InvalidResponse, (await Assert.ThrowsAsync<LicenseException>(() => api.ValidateAsync(Saved(), ct))).Failure);
    }

    /// <summary>Validation at start, then every 15 minutes, on the caller's context; Start twice runs one schedule.</summary>
    [Fact]
    public async Task StartValidatesNowAndEveryFifteenMinutes()
    {
        var api = new FakePolar();
        var waits = new List<(TimeSpan, TaskCompletionSource)>();
        var store = new LicenseStore(api, new MemoryLicense(Saved()), () => FixedNow, (span, _) =>
        {
            var wait = new TaskCompletionSource();
            waits.Add((span, wait));
            return wait.Task;
        });
        var validations = 0;
        store.AccessChanged += _ => validations++;
        store.Start();
        store.Start();
        Assert.True(store.HasAccess);
        Assert.Equal(1, validations);
        Assert.Equal([TimeSpan.FromMinutes(15)], waits.Select(w => w.Item1));
        api.Validation = Fails<PolarLicense>(LicenseFailure.Rejected);
        waits[0].Item2.SetResult();
        await Task.Yield();
        Assert.False(store.HasAccess);
        Assert.Equal(2, waits.Count);
        store.Stop();
    }

    [Fact]
    public void RecordRoundTripsThroughTheVaultFormat()
    {
        var record = Saved() with { ExpiresAt = FixedNow.AddDays(365) };
        Assert.Equal(record, LicenseRecord.FromJson(record.ToJson()));
        Assert.Equal(Saved(null), LicenseRecord.FromJson(Saved(null).ToJson()));
        Assert.Throws<JsonException>(() => LicenseRecord.FromJson("""{"key":"k"}"""));
    }

    [Fact]
    public async Task UnreadableVaultIsAStorageFailure()
    {
        var store = new LicenseStore(new FakePolar(), new ThrowingVault(), () => FixedNow);
        await store.RefreshAsync();
        Assert.False(store.HasAccess);
        Assert.False(store.IsInitializing);
        Assert.Equal(new LicenseException(LicenseFailure.Storage).Message, store.Message);
    }

    private sealed class ThrowingVault : ILicenseVault
    {
        public LicenseRecord? Load() => throw new UnauthorizedAccessException();
        public void Save(LicenseRecord? record) => throw new UnauthorizedAccessException();
    }
}
