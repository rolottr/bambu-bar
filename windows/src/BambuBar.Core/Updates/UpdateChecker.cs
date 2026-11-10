using BambuBar.Core.App;

namespace BambuBar.Core.Updates;

// Ported from the Mac's Sources/BambuBar/Services/UpdateChecker.swift, without Sparkle: on Windows this class reads
// the shared appcast itself, picks this PC's item and downloads and verifies the installer. Running it (silently,
// then relaunching) and the Authenticode check are the app's part.

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Supporter updates: a daily check of the shared appcast (ledger §8), "Skip This Version", "Later" and an optional
/// automatic download, all behind the supporter license like the Mac's. Windows compares its own appcast item's
/// version with this app's, never GitHub's tag, so a Mac-only release is simply "up to date" here.
/// <para>Single-threaded like the Mac's <c>@MainActor</c> checker: create it and call every member on the UI thread.
/// Its awaits resume on the caller's context and it starts no threads of its own. Wire the license with
/// <c>license.AccessChanged += _ =&gt; checker.SupporterStatusChanged()</c>.</para>
/// </summary>
public sealed class UpdateChecker : Observable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>The soonest a scheduled check runs after <see cref="Start"/> (the Mac's timer waits at least 1 s).</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(1);

    /// <summary>Settings under the Mac's UserDefaults names; the skipped version is Windows' own (Sparkle keeps the
    /// Mac's, as a build number).</summary>
    public static class Keys
    {
        public const string AutomaticChecks = "supporterAutomaticChecks";
        public const string AutomaticDownloads = "supporterAutomaticDownloads";
        public const string LastCheck = "supporterLastUpdateCheck";
        public const string SkippedVersion = "supporterSkippedVersion";
    }

    private readonly Func<bool> _hasAccess;
    private readonly AppSettings _settings;
    private readonly UpdateFeed _feed;
    private readonly UpdateSignature _signature;
    private readonly string _downloadFolder;
    private readonly string _platform;
    private readonly Version _osVersion;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _checksPreference;
    private bool _downloadsPreference;
    private CancellationTokenSource? _schedule;
    private CancellationTokenSource? _operation;
    private DateTimeOffset? _lastAttempt;

    /// <param name="hasAccess">Whether the supporter license is active (<c>LicenseStore.HasAccess</c>).</param>
    /// <param name="settings">Where the preferences, the last check and the skipped version are kept.</param>
    /// <param name="feed">The appcast and downloads.</param>
    /// <param name="downloadFolder">Where installers are saved (e.g. %LOCALAPPDATA%\Bambu Bar\Updates).</param>
    /// <param name="currentVersion">This app's X.Y.Z; <see cref="AppVersion.Marketing"/>.</param>
    /// <param name="platform">This PC's <c>sparkle:os</c>; <see cref="UpdatePlatform.Current"/>.</param>
    /// <param name="osVersion">Compared with items' minimum system version; <see cref="Environment.OSVersion"/>.</param>
    /// <param name="signature">The update key; the Mac's.</param>
    /// <param name="now">The clock (tests set it).</param>
    /// <param name="delay">Waits between scheduled checks (tests replace it); <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public UpdateChecker(Func<bool> hasAccess, AppSettings settings, UpdateFeed feed, string downloadFolder,
        string? currentVersion = null, string? platform = null, Version? osVersion = null, UpdateSignature? signature = null,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _hasAccess = hasAccess;
        _settings = settings;
        _feed = feed;
        _downloadFolder = downloadFolder;
        CurrentVersion = currentVersion ?? AppVersion.Marketing;
        _platform = platform ?? UpdatePlatform.Current;
        _osVersion = osVersion ?? Environment.OSVersion.Version;
        _signature = signature ?? new UpdateSignature();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _checksPreference = settings.Get(Keys.AutomaticChecks, true);
        _downloadsPreference = settings.Get(Keys.AutomaticDownloads, false);
        LastChecked = settings.Get<DateTimeOffset?>(Keys.LastCheck);
        SkippedVersion = settings.Get<string>(Keys.SkippedVersion);
    }

    public string CurrentVersion { get; }
    public UpdateState State { get; private set => Set(ref field, value); }

    /// <summary>The newer release the last check found for this PC (kept while it downloads, is ready or its download
    /// failed).</summary>
    public AppcastItem? Release { get; private set => Set(ref field, value); }

    /// <summary>The verified installer for <see cref="Release"/>, once <see cref="State"/> is Ready.</summary>
    public VerifiedInstaller? Installer { get; private set => Set(ref field, value); }

    /// <summary>Why the last check or download failed (<see cref="UpdateState.Failed"/>).</summary>
    public string? Error { get; private set => Set(ref field, value); }

    public DateTimeOffset? LastChecked { get; private set => Set(ref field, value); }
    public string? SkippedVersion { get; private set => Set(ref field, value); }

    /// <summary>Hidden from the tray panel for this run ("Later").</summary>
    public bool OfferDismissed { get; private set => Set(ref field, value); }

    public bool IsRunning { get; private set => Set(ref field, value); }

    public bool SupportsAutomaticUpdates => _hasAccess();

    public bool ChecksAutomatically
    {
        get => SupportsAutomaticUpdates && _checksPreference;
        set
        {
            _checksPreference = value;
            _settings.Set(Keys.AutomaticChecks, value);
            ApplySettings();
            Changed();
            Changed(nameof(DownloadsAutomatically));
        }
    }

    public bool DownloadsAutomatically
    {
        get => ChecksAutomatically && _downloadsPreference;
        set
        {
            _downloadsPreference = value;
            _settings.Set(Keys.AutomaticDownloads, value);
            Changed();
        }
    }

    public bool CanCheckForUpdates => IsRunning && SupportsAutomaticUpdates;

    /// <summary>The newer release, while the license allows updates.</summary>
    public AppcastItem? Available => SupportsAutomaticUpdates ? Release : null;

    /// <summary>The release to offer in the tray panel: not skipped and not put off for later.</summary>
    public AppcastItem? Offered =>
        Available is { } release && !OfferDismissed && release.DisplayVersion != SkippedVersion ? release : null;

    /// <summary>Starts the daily schedule. Demo, emulator and developer runs leave it off.</summary>
    public void Start()
    {
        if (!SupportsAutomaticUpdates || IsRunning) return;
        IsRunning = true;
        ApplySettings();
        Derived();
    }

    /// <summary>Stops the schedule and whatever check or download is running.</summary>
    public void Stop()
    {
        CancelSchedule();
        _operation?.Cancel();
        IsRunning = false;
        Derived();
    }

    /// <summary>"Check for Updates…": checks now. A version skipped earlier is offered again, as Sparkle does for a
    /// check the user asked for. Never downloads by itself.</summary>
    public Task CheckAsync(CancellationToken cancellation = default) => CheckAsync(scheduled: false, cancellation);

    /// <summary>Downloads and verifies <see cref="Available"/>. Null when there is nothing to download, the license
    /// lapsed, or it failed (<see cref="Error"/> says why).</summary>
    public async Task<VerifiedInstaller?> DownloadAsync(CancellationToken cancellation = default)
    {
        if (!CanCheckForUpdates || Available is not { } release || _operation is not null) return null;
        if (Installer is { } ready && ready.Item == release && File.Exists(ready.Path)) return ready;
        using var operation = _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        State = UpdateState.Downloading;
        Error = null;
        try
        {
            var installer = await _feed.DownloadAsync(release, _downloadFolder, _signature, operation.Token);
            if (operation.IsCancellationRequested || !CanCheckForUpdates || Release != release) return null;
            Installer = installer;
            State = UpdateState.Ready;
            return installer;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return null; }
        catch (UpdateException e)
        {
            if (operation.IsCancellationRequested || !CanCheckForUpdates) return null;
            State = UpdateState.Failed;
            Error = e.Message;
            return null;
        }
        finally { _operation = null; }
    }

    /// <summary>"Skip This Version": the tray panel stops offering it, and scheduled checks don't download it.</summary>
    public void SkipVersion()
    {
        if (Release?.DisplayVersion is not { } version) return;
        SkippedVersion = version;
        _settings.Set(Keys.SkippedVersion, version);
        Derived();
    }

    /// <summary>"Later": hidden from the tray panel until a check finds it again.</summary>
    public void RemindLater()
    {
        OfferDismissed = true;
        Derived();
    }

    /// <summary>The license was activated, revalidated, refused or deactivated.</summary>
    public void SupporterStatusChanged()
    {
        if (SupportsAutomaticUpdates) Start();
        if (IsRunning) ApplySettings();
        if (!SupportsAutomaticUpdates)
        {
            _operation?.Cancel();
            State = UpdateState.Idle;
            OfferDismissed = true;
        }
        Changed(null);
    }

    private async Task CheckAsync(bool scheduled, CancellationToken cancellation = default)
    {
        if (!CanCheckForUpdates || _operation is not null) return;
        using var operation = _operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        State = UpdateState.Checking;
        Error = null;
        var download = false;
        try
        {
            var appcast = await _feed.FetchAsync(operation.Token);
            if (operation.IsCancellationRequested || !CanCheckForUpdates) return;
            var item = appcast.Latest(_platform, _osVersion);
            if (!ReleaseVersion.TryParse(CurrentVersion, out var current)) throw new UpdateException(UpdateFailure.InvalidVersion);
            LastChecked = _now();
            _settings.Set(Keys.LastCheck, LastChecked);
            if (item is null || !ReleaseVersion.TryParse(item.DisplayVersion, out var latest) || latest <= current)
            {
                Release = null;
                Installer = null;
                State = UpdateState.UpToDate;
                return;
            }
            if (Release != item) Installer = null;
            Release = item;
            OfferDismissed = false;
            if (!scheduled && SkippedVersion == item.DisplayVersion)
            {
                SkippedVersion = null;
                _settings.Set<string>(Keys.SkippedVersion, null);
            }
            State = Installer is not null && File.Exists(Installer.Path) ? UpdateState.Ready : UpdateState.Available;
            download = scheduled && State == UpdateState.Available && DownloadsAutomatically && item.DisplayVersion != SkippedVersion;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (UpdateException e)
        {
            if (operation.IsCancellationRequested || !CanCheckForUpdates) return;
            Release = null;
            Installer = null;
            State = UpdateState.Failed;
            Error = e.Message;
        }
        finally
        {
            _operation = null;
            Derived();
        }
        if (download) await DownloadAsync(cancellation);
    }

    private void ApplySettings()
    {
        if (IsRunning && ChecksAutomatically && _schedule is null)
        {
            _schedule = new CancellationTokenSource();
            _ = RunScheduleAsync(_schedule.Token);
        }
        else if (!ChecksAutomatically)
        {
            CancelSchedule();
        }
    }

    private void CancelSchedule()
    {
        _schedule?.Cancel();
        _schedule = null;
    }

    /// <summary>Checks a day after the last check (or the last scheduled try, so being offline doesn't retry every
    /// second), and at least <see cref="FirstCheckDelay"/> from now.</summary>
    internal TimeSpan NextCheckDelay()
    {
        var last = LastChecked > _lastAttempt || _lastAttempt is null ? LastChecked : _lastAttempt;
        if (last is not { } previous) return FirstCheckDelay;
        var wait = CheckInterval - (_now() - previous);
        // A clock set back must not push the next check past a day.
        return wait < FirstCheckDelay ? FirstCheckDelay : wait > CheckInterval ? CheckInterval : wait;
    }

    private async Task RunScheduleAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { await _delay(NextCheckDelay(), cancellation); }
            catch (OperationCanceledException) { return; }
            if (cancellation.IsCancellationRequested) return;
            _lastAttempt = _now();
            await CheckAsync(scheduled: true);
        }
    }

    /// <summary>The computed properties may read differently now.</summary>
    private void Derived()
    {
        Changed(nameof(CanCheckForUpdates));
        Changed(nameof(Available));
        Changed(nameof(Offered));
    }
}
