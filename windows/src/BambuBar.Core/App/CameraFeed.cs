using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;

namespace BambuBar.Core.App;

/// <summary>Something on screen that shows a <see cref="CameraFeed"/> (the Mac's CameraSurfaceView). The feed streams
/// only while one of them <see cref="IsOnScreen"/>, and asks again whenever told <see cref="CameraFeed.ViewersChanged"/>.</summary>
public interface ICameraViewer
{
    /// <summary>In a window that's shown and not minimized, not hidden, and not scrolled out of sight.</summary>
    bool IsOnScreen { get; }
}

/// <summary>Runs work after a delay on the feed's thread; disposing the result cancels it. Tests pass a fake clock.</summary>
public interface ICameraScheduler
{
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>What the feed knows of its printer, asked afresh each time it decides something.</summary>
public sealed record CameraSituation
{
    public bool IsDemo { get; init; }
    /// <summary>Both camera switches are on: Settings → Camera, and this printer's on the Printers page.</summary>
    public bool ShowsCamera { get; init; }
    /// <summary>A run against the mock fleet (<c>--emulator</c>), whose printers have no camera.</summary>
    public bool IsMockFleet { get; init; }
    public bool IsOnline { get; init; }
    public CameraTransport Transport { get; init; }
    /// <summary>What the printer reports for LAN Only Liveview; null before it says.</summary>
    public bool? LiveViewEnabled { get; init; }
    /// <summary>A print is under way (preparing, printing or paused).</summary>
    public bool IsPrinting { get; init; }
    /// <summary>Settings → Camera → "Keep the camera connected during prints".</summary>
    public bool StaysOnWhilePrinting { get; init; }
    public string Host { get; init; } = "";
    public string Serial { get; init; } = "";
    public string? AccessCode { get; init; }
    public bool Insecure { get; init; }
}

/// <summary>How a feed reaches the camera and decodes it; the defaults are the real ones, tests replace them.</summary>
public sealed record CameraServices
{
    public Func<CameraConfig, ICameraClient> Client { get; init; } = CameraClient.Create;
    /// <summary>Makes the decoder for a stream, given where its pictures and failures go.</summary>
    public Func<Action<CameraFrame>, Action<string>, ICameraPipeline> Pipeline { get; init; } = DecodePipeline.Create;
    /// <summary>Why the camera can't be decoded on this computer, if it can't (FFmpeg missing): checked before connecting.</summary>
    public Func<string?> DecoderProblem { get; init; } = () => FFmpeg.Status.IsAvailable ? null : FFmpegStatus.Message;
    /// <summary>Null: timers on the feed's <see cref="SynchronizationContext"/>.</summary>
    public ICameraScheduler? Scheduler { get; init; }
}

/// <summary>
/// One printer's camera, shared by every view that shows it (a port of the Mac's CameraFeed.swift).
///
/// The stream runs while at least one <see cref="ICameraViewer"/> is on screen (a hidden window, a collapsed preview or
/// a closed panel don't count), and, with "Keep the camera connected during prints" on, for the whole of a print so
/// opening the panel shows it at once. Otherwise it stops 4 seconds after the last viewer goes, so switching from the
/// panel to the window doesn't reconnect. A dropped stream reconnects after 1, 3, 6, 12, then every 30 seconds; a
/// refused access code or certificate stops until <see cref="TryAgain"/>.
///
/// Pictures are decoded (off the UI thread, by an <see cref="ICameraPipeline"/>) only while a viewer is on screen;
/// when one comes back mid-stream, the video since the last keyframe is decoded to catch up. Every viewer shows the
/// same <see cref="Frame"/>. Lives on the UI thread, like its session.
/// </summary>
public sealed class CameraFeed : Observable, IDisposable
{
    public abstract record FeedState
    {
        public sealed record Idle : FeedState;
        public sealed record Connecting : FeedState;
        public sealed record Live : FeedState;
        /// <summary>Nothing to try right now (printer offline, Liveview off, demo).</summary>
        public sealed record Unavailable(string Reason) : FeedState;
        /// <summary>The last attempt failed; <paramref name="Retrying"/> says whether another one is scheduled.</summary>
        public sealed record Failed(string Reason, bool Retrying) : FeedState;
    }

    /// <summary>The Mac's back-off after a failed attempt.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(30)];
    /// <summary>How long the stream outlives its last viewer.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(4);
    /// <summary>At most this much video is kept to catch a viewer up (10 s at 30 fps).</summary>
    public const int CatchUpLimit = 300;

    private readonly Func<CameraSituation?> _situation;
    private readonly CameraServices _services;
    private readonly ICameraScheduler _scheduler;
    private readonly List<WeakReference<ICameraViewer>> _viewers = [];
    private ICameraClient? _client;
    private ICameraPipeline? _pipeline;
    /// <summary>The stream since its last keyframe, to bring a viewer back on screen up to date.</summary>
    private List<VideoFrame> _sinceKeyframe = [];
    private byte[]? _lastImage;
    /// <summary>Frames arrived while no viewer was on screen, so nothing decoded them.</summary>
    private bool _behind;
    private int _attempt;
    private IDisposable? _retry;
    private IDisposable? _idleStop;

    /// <param name="situation">The printer as it is now; null when there's none (it was forgotten).</param>
    public CameraFeed(Func<CameraSituation?> situation, CameraServices? services = null)
    {
        _situation = situation;
        _services = services ?? new CameraServices();
        _scheduler = _services.Scheduler ?? new ContextScheduler(SynchronizationContext.Current);
    }

    private FeedState _state = new FeedState.Idle();
    public FeedState State { get => _state; private set => Set(ref _state, value); }

    private string? _detail;
    /// <summary>"H.264 · 1920×1080", once the stream has started.</summary>
    public string? Detail { get => _detail; private set => Set(ref _detail, value); }

    private double _aspectRatio = 16.0 / 9;
    /// <summary>Width over height of the picture; 16:9 until the stream says otherwise.</summary>
    public double AspectRatio { get => _aspectRatio; private set => Set(ref _aspectRatio, value); }

    /// <summary>The newest decoded picture; null before the first and after <see cref="Stop"/>. Don't keep it: its
    /// pixels are reused once a newer one arrives.</summary>
    public CameraFrame? Frame { get; private set; }

    /// <summary><see cref="Frame"/> changed (a new picture, or none after a stop).</summary>
    public event Action? FrameChanged;

    public bool IsRunning => _client is not null;

    private bool HasViewers
    {
        get
        {
            _viewers.RemoveAll(w => !w.TryGetTarget(out _));
            return _viewers.Any(w => w.TryGetTarget(out var v) && v.IsOnScreen);
        }
    }

    /// <summary>A print is under way and the setting is on: keep the stream warm even with nothing on screen.</summary>
    private bool KeepsWarm => _situation() is { StaysOnWhilePrinting: true, IsPrinting: true } && Availability() is null;

    private bool Wanted => HasViewers || KeepsWarm;

    // MARK: Viewers

    public void Attach(ICameraViewer viewer)
    {
        if (!_viewers.Any(w => w.TryGetTarget(out var v) && ReferenceEquals(v, viewer))) _viewers.Add(new WeakReference<ICameraViewer>(viewer));
        ViewersChanged();
    }

    public void Detach(ICameraViewer viewer)
    {
        _viewers.RemoveAll(w => !w.TryGetTarget(out var v) || ReferenceEquals(v, viewer));
        ViewersChanged();
    }

    /// <summary>A viewer appeared or disappeared on screen, a print started or ended, or the setting changed.</summary>
    public void ViewersChanged()
    {
        var hasViewers = HasViewers;
        if (_behind && hasViewers) CatchUp();
        if (hasViewers || KeepsWarm)
        {
            _idleStop?.Dispose();
            _idleStop = null;
            if (_client is null && _retry is null) Connect();
        }
        else if ((_client is not null || _retry is not null) && _idleStop is null)
        {
            _idleStop = _scheduler.After(IdleStop, () =>
            {
                _idleStop = null;
                if (!Wanted) Stop();
            });
        }
    }

    // MARK: Lifecycle

    /// <summary>Something the stream depends on changed (connection, Liveview setting, print state, a switch): reconsider.</summary>
    public void Update()
    {
        if (Availability() is { } reason)
        {
            if (_client is not null || _retry is not null) Stop();
            State = new FeedState.Unavailable(reason);
            return;
        }
        switch (State)
        {
            case FeedState.Unavailable or FeedState.Failed { Retrying: false }:
                _retry?.Dispose();
                _retry = null;
                _attempt = 0;
                if (Wanted) Connect(); else State = new FeedState.Idle();
                break;
            default:
                ViewersChanged();
                break;
        }
    }

    public void TryAgain()
    {
        Stop();
        _attempt = 0;
        if (Wanted) Connect();
    }

    /// <summary>Reconnects if running, e.g. after the printer moved to a new address.</summary>
    public void Restart()
    {
        if (_client is null && _retry is null) return;
        Stop();
        Connect();
    }

    public void Stop()
    {
        _retry?.Dispose();
        _retry = null;
        _idleStop?.Dispose();
        _idleStop = null;
        StopClient();
        _sinceKeyframe = [];
        _lastImage = null;
        _behind = false;
        _pipeline?.Dispose();
        _pipeline = null;
        if (Frame is not null)
        {
            Frame = null;
            FrameChanged?.Invoke();
        }
        State = new FeedState.Idle();
        Detail = null;
    }

    public void Dispose() => Stop();

    private void StopClient()
    {
        if (_client is null) return;
        _client.OnEvent = null;
        _client.Stop();
        _client = null;
    }

    /// <summary>Why the camera can't be tried now, if it can't.</summary>
    private string? Availability()
    {
        if (_situation() is not { } s) return Loc.T("No printer.");
        if (s.IsDemo) return Loc.T("The demo printer has no camera.");
        if (!s.ShowsCamera) return Loc.T("The camera is off.");
        if (s.IsMockFleet) return "The mock fleet's printers have no camera.";
        if (!s.IsOnline) return Loc.T("The camera starts when the printer is connected.");
        if (s.Transport == CameraTransport.Rtsps && s.LiveViewEnabled == false)
            return Loc.T("Turn on LAN Only Liveview on the printer (Settings → Network) to see the camera here.");
        // Windows: the decoder is a library beside the app, which may be missing (the Mac's is the system's).
        return _services.DecoderProblem();
    }

    private void Connect()
    {
        _retry = null;
        if (Availability() is { } reason)
        {
            State = new FeedState.Unavailable(reason);
            return;
        }
        if (_situation() is not { AccessCode: { } code } s)
        {
            State = new FeedState.Unavailable(Loc.T("The access code isn't available."));
            return;
        }
        var client = _services.Client(new CameraConfig { Host = s.Host, Serial = s.Serial, AccessCode = code, Transport = s.Transport, Insecure = s.Insecure });
        client.OnEvent = e =>
        {
            if (ReferenceEquals(client, _client)) Handle(e);
        };
        _client = client;
        if (State is not FeedState.Live) State = new FeedState.Connecting();
        client.Start();
    }

    private void Handle(CameraEvent e)
    {
        switch (e)
        {
            case CameraEvent.Started started:
                Detail = started.Description;
                if (started.Width > 0 && started.Height > 0) AspectRatio = (double)started.Width / started.Height;
                break;
            case CameraEvent.Video video:
                if (video.IsKeyframe) _sinceKeyframe = [video.Frame];
                else if (_sinceKeyframe.Count > 0 && _sinceKeyframe.Count < CatchUpLimit) _sinceKeyframe.Add(video.Frame);
                if (!HasViewers) _behind = true;
                else if (_behind) CatchUp(); // already includes this frame
                else Pipeline.Decode(video.Frame);
                BecameLive();
                break;
            case CameraEvent.Image image:
                _lastImage = image.Jpeg;
                if (HasViewers)
                {
                    _behind = false;
                    Pipeline.DecodeJpeg(image.Jpeg);
                }
                else
                {
                    _behind = true;
                }
                BecameLive();
                break;
            case CameraEvent.Failed failed:
                Failed(failed.Failure);
                break;
        }
    }

    private void Failed(CameraFailure failure)
    {
        _client = null;
        _sinceKeyframe = [];
        // The picture stays until the next one; that one must start at a keyframe.
        _pipeline?.Reset();
        if (failure.IsPermanent)
        {
            State = new FeedState.Failed(failure.Message, Retrying: false);
            return;
        }
        var delay = RetryDelays[Math.Min(_attempt, RetryDelays.Length - 1)];
        _attempt++;
        // A stream that was fine and dropped once reconnects quietly.
        if (!(State is FeedState.Live && _attempt == 1)) State = new FeedState.Failed(failure.Message, Retrying: true);
        _retry = _scheduler.After(delay, Connect);
    }

    /// <summary>Brings the picture up to date after frames went undecoded: the last still, or the video since the
    /// last keyframe.</summary>
    private void CatchUp()
    {
        _behind = false;
        if (_lastImage is { } jpeg) Pipeline.DecodeJpeg(jpeg);
        if (_sinceKeyframe.Count > 0) Pipeline.CatchUp(_sinceKeyframe);
    }

    private void BecameLive()
    {
        if (State is FeedState.Live) return;
        State = new FeedState.Live();
        _attempt = 0;
    }

    private ICameraPipeline Pipeline
    {
        get
        {
            if (_pipeline is not null) return _pipeline;
            ICameraPipeline? created = null;
            created = _services.Pipeline(frame => Show(created!, frame), message => DecodeFailed(created!, message));
            _pipeline = created;
            return created;
        }
    }

    private void Show(ICameraPipeline from, CameraFrame frame)
    {
        if (!ReferenceEquals(from, _pipeline))
        {
            from.Release(frame);
            return;
        }
        var old = Frame;
        Frame = frame;
        FrameChanged?.Invoke();
        if (old is not null && !ReferenceEquals(old, frame)) from.Release(old);
    }

    /// <summary>The stream can't be decoded (FFmpeg missing, a codec it lacks): trying again won't help by itself.</summary>
    private void DecodeFailed(ICameraPipeline from, string message)
    {
        if (!ReferenceEquals(from, _pipeline)) return;
        _retry?.Dispose();
        _retry = null;
        StopClient();
        _sinceKeyframe = [];
        _lastImage = null;
        _behind = false;
        from.Dispose();
        _pipeline = null;
        State = new FeedState.Failed(message, Retrying: false);
    }

    /// <summary>Timers that fire on a <see cref="SynchronizationContext"/> (the UI thread), or the thread pool without one.</summary>
    private sealed class ContextScheduler(SynchronizationContext? context) : ICameraScheduler
    {
        public IDisposable After(TimeSpan delay, Action action)
        {
            var entry = new Entry();
            entry.Timer = new Timer(_ =>
            {
                if (context is null) entry.Run(action);
                else context.Post(_ => entry.Run(action), null);
            }, null, delay, Timeout.InfiniteTimeSpan);
            return entry;
        }

        private sealed class Entry : IDisposable
        {
            public Timer? Timer;
            private volatile bool _cancelled;

            public void Run(Action action)
            {
                if (_cancelled) return;
                _cancelled = true;
                Timer?.Dispose();
                action();
            }

            public void Dispose()
            {
                _cancelled = true;
                Timer?.Dispose();
            }
        }
    }
}
