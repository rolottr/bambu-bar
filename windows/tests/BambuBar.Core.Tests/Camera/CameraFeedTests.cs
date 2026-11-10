using BambuBar.Core.App;
using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;
using State = BambuBar.Core.App.CameraFeed.FeedState;

namespace BambuBar.Core.Tests.Camera;

/// <summary>
/// The feed's lifecycle, after the Mac's CameraFeed.swift: when it may stream (gating), when it does (viewers on screen,
/// or a print with "keep connected" on), the 4-second stop, the back-off, the texts, and what reaches the decoder
/// (nothing while nobody watches, a catch-up from the last keyframe when someone comes back). Clients, the decoder and
/// the clock are fakes; everything runs on the test's thread, as on the UI thread.
/// </summary>
public class CameraFeedTests
{
    private sealed class Clock : ICameraScheduler
    {
        private sealed class Entry(TimeSpan due, Action action) : IDisposable
        {
            public TimeSpan Due { get; } = due;
            public Action Action { get; } = action;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }

        private readonly List<Entry> _entries = [];
        public TimeSpan Now { get; private set; }

        public IDisposable After(TimeSpan delay, Action action)
        {
            var entry = new Entry(Now + delay, action);
            _entries.Add(entry);
            return entry;
        }

        public IEnumerable<TimeSpan> Pending => _entries.Where(e => !e.Cancelled).Select(e => e.Due - Now);

        public void Advance(TimeSpan by)
        {
            var end = Now + by;
            while (_entries.Where(e => !e.Cancelled && e.Due <= end).OrderBy(e => e.Due).FirstOrDefault() is { } next)
            {
                Now = next.Due;
                next.Dispose();
                next.Action();
            }
            Now = end;
        }

        public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }

    private sealed class Client(CameraConfig config) : ICameraClient
    {
        public CameraConfig Config { get; } = config;
        public Action<CameraEvent>? OnEvent { get; set; }
        public Action<string>? Log { get; set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public void Start() => Started = true;
        public void Stop() => Stopped = true;
        public void Send(CameraEvent e) => OnEvent?.Invoke(e);
    }

    private sealed class Pipeline(Action<CameraFrame> show, Action<string> fail) : ICameraPipeline
    {
        public readonly List<string> Calls = [];
        public readonly List<CameraFrame> Released = [];
        public bool Disposed { get; private set; }
        public void Decode(VideoFrame frame) => Calls.Add($"decode {frame.Timestamp}");
        public void DecodeJpeg(byte[] jpeg) => Calls.Add($"jpeg {jpeg.Length}");
        public void CatchUp(IReadOnlyList<VideoFrame> frames) => Calls.Add("catch up " + string.Join(",", frames.Select(f => f.Timestamp)));
        public void Reset() => Calls.Add("reset");
        public void Release(CameraFrame frame) => Released.Add(frame);
        public void Dispose() => Disposed = true;
        public void Show(CameraFrame frame) => show(frame);
        public void Fail(string message) => fail(message);
    }

    private sealed class Viewer : ICameraViewer
    {
        public bool IsOnScreen { get; set; } = true;
    }

    private readonly Clock _clock = new();
    private readonly List<Client> _clients = [];
    private readonly List<Pipeline> _pipelines = [];
    private string? _decoderProblem;
    private CameraSituation? _situation = new()
    {
        ShowsCamera = true, IsOnline = true, Transport = CameraTransport.Rtsps, LiveViewEnabled = true,
        Host = "192.168.1.50", Serial = "22E00A000000123", AccessCode = "12345678",
    };
    private readonly CameraFeed _feed;
    /// <summary>The feed holds its viewers weakly, as the Mac's NSHashTable does: the test keeps them alive.</summary>
    private readonly List<Viewer> _held = [];

    public CameraFeedTests()
    {
        _feed = new CameraFeed(() => _situation, new CameraServices
        {
            Client = config => { var c = new Client(config); _clients.Add(c); return c; },
            Pipeline = (show, fail) => { var p = new Pipeline(show, fail); _pipelines.Add(p); return p; },
            DecoderProblem = () => _decoderProblem,
            Scheduler = _clock,
        });
    }

    private Client Current => _clients[^1];
    private Pipeline Decoder => _pipelines[^1];

    private static readonly VideoFormat Format = VideoFormat.Create(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps])!;

    private static CameraEvent Video(uint timestamp, bool keyframe = false) =>
        new CameraEvent.Video(new VideoFrame(keyframe ? Fixtures.H264Idr : Fixtures.H264P, keyframe, timestamp, Format));

    private Viewer Watch()
    {
        var viewer = new Viewer();
        _held.Add(viewer);
        _feed.Attach(viewer);
        return viewer;
    }

    private void Situation(Func<CameraSituation, CameraSituation> change)
    {
        _situation = change(_situation!);
        _feed.Update();
    }

    // MARK: Gating

    [Fact]
    public void NothingStreamsWithoutAViewer()
    {
        _feed.Update();
        Assert.Empty(_clients);
        Assert.Equal(new State.Idle(), _feed.State);
    }

    [Fact]
    public void AViewerOnScreenStartsTheStream()
    {
        Watch();
        Assert.Single(_clients);
        Assert.True(Current.Started);
        Assert.Equal(new CameraConfig { Host = "192.168.1.50", Serial = "22E00A000000123", AccessCode = "12345678", Transport = CameraTransport.Rtsps }, Current.Config);
        Assert.Equal(new State.Connecting(), _feed.State);
        Current.Send(new CameraEvent.Started("H.264 · 1920×1080", 1920, 1080));
        Assert.Equal("H.264 · 1920×1080", _feed.Detail);
        Assert.Equal(16.0 / 9, _feed.AspectRatio, 6);
        Current.Send(Video(0, keyframe: true));
        Assert.Equal(new State.Live(), _feed.State);
    }

    [Fact]
    public void AViewerOffScreenDoesNotCount()
    {
        var viewer = new Viewer { IsOnScreen = false };
        _held.Add(viewer);
        _feed.Attach(viewer);
        Assert.Empty(_clients);
    }

    public static TheoryData<string, Func<CameraSituation, CameraSituation>> Unavailable => new()
    {
        { "The demo printer has no camera.", s => s with { IsDemo = true } },
        { "The camera is off.", s => s with { ShowsCamera = false } },
        { "The mock fleet's printers have no camera.", s => s with { IsMockFleet = true } },
        { "The camera starts when the printer is connected.", s => s with { IsOnline = false } },
        { "Turn on LAN Only Liveview on the printer (Settings → Network) to see the camera here.", s => s with { LiveViewEnabled = false } },
    };

    [Theory]
    [MemberData(nameof(Unavailable))]
    public void SaysWhyItCannotStream(string reason, Func<CameraSituation, CameraSituation> change)
    {
        _situation = change(_situation!);
        Watch();
        Assert.Empty(_clients);
        Assert.Equal(new State.Unavailable(reason), _feed.State);
    }

    /// <summary>The JPEG cameras (P1, A1) don't need LAN Only Liveview; an unknown setting isn't "off" either.</summary>
    [Fact]
    public void LiveviewMattersOnlyForRtsps()
    {
        _situation = _situation! with { Transport = CameraTransport.Jpeg, LiveViewEnabled = false };
        Watch();
        Assert.Single(_clients);
        _feed.Stop();
        _situation = _situation with { Transport = CameraTransport.Rtsps, LiveViewEnabled = null };
        _feed.ViewersChanged();
        Assert.Equal(2, _clients.Count);
    }

    [Fact]
    public void NoAccessCodeNoStream()
    {
        _situation = _situation! with { AccessCode = null };
        Watch();
        Assert.Equal(new State.Unavailable("The access code isn't available."), _feed.State);
        Assert.Empty(_clients);
    }

    [Fact]
    public void ForgottenPrinterHasNoCamera()
    {
        _situation = null;
        Watch();
        Assert.Equal(new State.Unavailable("No printer."), _feed.State);
    }

    /// <summary>Windows: the decoder is a library beside the app. Without it the feed says so and doesn't connect.</summary>
    [Fact]
    public void MissingDecoderIsReportedBeforeConnecting()
    {
        _decoderProblem = FFmpegStatus.Message;
        Watch();
        Assert.Empty(_clients);
        Assert.Equal(new State.Unavailable(FFmpegStatus.Message), _feed.State);
    }

    [Fact]
    public void GoingOfflineStopsAndComingBackResumes()
    {
        Watch();
        var first = Current;
        Situation(s => s with { IsOnline = false });
        Assert.True(first.Stopped);
        Assert.Equal(new State.Unavailable("The camera starts when the printer is connected."), _feed.State);
        Situation(s => s with { IsOnline = true });
        Assert.Equal(2, _clients.Count);
        Assert.Equal(new State.Connecting(), _feed.State);
    }

    [Fact]
    public void BackAfterUnavailableWithoutViewersIsIdle()
    {
        Situation(s => s with { ShowsCamera = false });
        Assert.IsType<State.Unavailable>(_feed.State);
        Situation(s => s with { ShowsCamera = true });
        Assert.Equal(new State.Idle(), _feed.State);
        Assert.Empty(_clients);
    }

    // MARK: Viewers and timing

    [Fact]
    public void StopsFourSecondsAfterTheLastViewer()
    {
        var viewer = Watch();
        viewer.IsOnScreen = false;
        _feed.ViewersChanged();
        _clock.Advance(3.9);
        Assert.False(Current.Stopped);
        _clock.Advance(0.1);
        Assert.True(Current.Stopped);
        Assert.Equal(new State.Idle(), _feed.State);
    }

    /// <summary>Switching from the panel to the window doesn't reconnect.</summary>
    [Fact]
    public void AViewerBackWithinFourSecondsKeepsTheStream()
    {
        var panel = Watch();
        _feed.Detach(panel);
        _clock.Advance(2);
        var window = Watch();
        _clock.Advance(10);
        Assert.Single(_clients);
        Assert.False(Current.Stopped);
        Assert.Empty(_clock.Pending);
        _feed.Detach(window);
        _clock.Advance(4);
        Assert.True(Current.Stopped);
    }

    [Fact]
    public void KeepsWarmDuringAPrintWhenAsked()
    {
        _situation = _situation! with { StaysOnWhilePrinting = true, IsPrinting = true };
        _feed.Update();
        Assert.Single(_clients);
        // Nobody watches: frames arrive but nothing is decoded.
        Current.Send(Video(0, keyframe: true));
        Current.Send(Video(3000));
        Assert.Equal(new State.Live(), _feed.State);
        Assert.Empty(_pipelines);
        // Someone opens the panel: caught up from the keyframe at once, then frame by frame.
        var viewer = Watch();
        Assert.Equal(["catch up 0,3000"], Decoder.Calls);
        Current.Send(Video(6000));
        Assert.Equal(["catch up 0,3000", "decode 6000"], Decoder.Calls);
        _feed.Detach(viewer);
        _clock.Advance(60);
        Assert.False(Current.Stopped);
        // The print ends: the stream goes 4 s later.
        Situation(s => s with { IsPrinting = false });
        _clock.Advance(4);
        Assert.True(Current.Stopped);
    }

    [Fact]
    public void WithoutKeepWarmAPrintDoesNotStream()
    {
        _situation = _situation! with { IsPrinting = true };
        _feed.Update();
        Assert.Empty(_clients);
    }

    /// <summary>A viewer scrolled out of sight misses frames; back in sight, it's caught up from the last keyframe
    /// rather than shown a broken picture.</summary>
    [Fact]
    public void CatchesUpFromTheLastKeyframe()
    {
        var viewer = Watch();
        Current.Send(Video(0, keyframe: true));
        Current.Send(Video(3000));
        viewer.IsOnScreen = false;
        _feed.ViewersChanged();
        Current.Send(Video(6000));
        Current.Send(Video(9000, keyframe: true));
        Current.Send(Video(12000));
        viewer.IsOnScreen = true;
        _feed.ViewersChanged();
        Current.Send(Video(15000));
        Assert.Equal(["decode 0", "decode 3000", "catch up 9000,12000", "decode 15000"], Decoder.Calls);
    }

    /// <summary>A frame arriving for a viewer that's back on screen catches it up, and includes itself.</summary>
    [Fact]
    public void AFrameForAViewerBackOnScreenCatchesUp()
    {
        var viewer = Watch();
        Current.Send(Video(0, keyframe: true));
        viewer.IsOnScreen = false;
        Current.Send(Video(3000));
        viewer.IsOnScreen = true;
        Current.Send(Video(6000));
        Assert.Equal(["decode 0", "catch up 0,3000,6000"], Decoder.Calls);
    }

    [Fact]
    public void JpegStillsDecodeOnlyTheNewest()
    {
        _situation = _situation! with { Transport = CameraTransport.Jpeg };
        var viewer = Watch();
        Current.Send(new CameraEvent.Started("JPEG · 64×48", 64, 48));
        Current.Send(new CameraEvent.Image(new byte[10], 64, 48));
        viewer.IsOnScreen = false;
        Current.Send(new CameraEvent.Image(new byte[11], 64, 48));
        Current.Send(new CameraEvent.Image(new byte[12], 64, 48));
        viewer.IsOnScreen = true;
        _feed.ViewersChanged();
        Assert.Equal(["jpeg 10", "jpeg 12"], Decoder.Calls);
        Assert.Equal(4.0 / 3, _feed.AspectRatio, 6);
    }

    [Fact]
    public void KeepsAtMostTenSecondsToCatchUp()
    {
        var viewer = Watch();
        viewer.IsOnScreen = false;
        _feed.ViewersChanged();
        Current.Send(Video(0, keyframe: true));
        for (uint i = 1; i < 400; i++) Current.Send(Video(i));
        viewer.IsOnScreen = true;
        _feed.ViewersChanged();
        Assert.Equal(CameraFeed.CatchUpLimit, Decoder.Calls.Single().Split(',').Length);
    }

    // MARK: Pictures

    [Fact]
    public void ShowsTheNewestPictureAndReleasesTheOldOne()
    {
        Watch();
        Current.Send(Video(0, keyframe: true));
        var changes = 0;
        _feed.FrameChanged += () => changes++;
        var first = new CameraFrame(4, 4);
        var second = new CameraFrame(4, 4);
        Decoder.Show(first);
        Assert.Same(first, _feed.Frame);
        Decoder.Show(second);
        Assert.Same(second, _feed.Frame);
        Assert.Equal([first], Decoder.Released);
        Assert.Equal(2, changes);
        var pipeline = Decoder;
        _feed.Stop();
        Assert.True(pipeline.Disposed);
        Assert.Null(_feed.Frame);
        Assert.Equal(3, changes);
        Assert.Null(_feed.Detail);
    }

    [Fact]
    public void PicturesFromAnOldPipelineAreIgnored()
    {
        Watch();
        Current.Send(Video(0, keyframe: true));
        var old = Decoder;
        _feed.TryAgain();
        Current.Send(Video(0, keyframe: true));
        var stale = new CameraFrame(4, 4);
        old.Show(stale);
        Assert.Null(_feed.Frame);
        Assert.Equal([stale], old.Released);
    }

    [Fact]
    public void ADecoderThatCannotDecodeStopsTheStream()
    {
        Watch();
        Current.Send(Video(0, keyframe: true));
        Decoder.Fail("Bambu Bar can't decode this camera's video (H.265).");
        Assert.True(Current.Stopped);
        Assert.Equal(new State.Failed("Bambu Bar can't decode this camera's video (H.265).", Retrying: false), _feed.State);
        Assert.Empty(_clock.Pending);
        _feed.TryAgain();
        Assert.Equal(2, _clients.Count);
    }

    // MARK: Failures

    [Fact]
    public void BacksOffOneThreeSixTwelveThenThirty()
    {
        Watch();
        var delays = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            Current.Send(new CameraEvent.Failed(new CameraFailure.TimedOut()));
            Assert.Equal(new State.Failed("The camera stopped sending video.", Retrying: true), _feed.State);
            delays.Add(_clock.Pending.Single().TotalSeconds);
            var count = _clients.Count;
            _clock.Advance(_clock.Pending.Single());
            Assert.Equal(count + 1, _clients.Count);
        }
        Assert.Equal([1, 3, 6, 12, 30, 30, 30], delays);
    }

    /// <summary>A stream that was fine and dropped once reconnects quietly; going live again resets the back-off.</summary>
    [Fact]
    public void ALiveStreamThatDropsOnceReconnectsQuietly()
    {
        Watch();
        Current.Send(Video(0, keyframe: true));
        Current.Send(new CameraEvent.Failed(new CameraFailure.Closed()));
        Assert.Equal(new State.Live(), _feed.State);
        Assert.Equal(["decode 0", "reset"], Decoder.Calls);
        _clock.Advance(1);
        Assert.Equal(new State.Live(), _feed.State);
        Current.Send(new CameraEvent.Failed(new CameraFailure.Closed()));
        Assert.Equal(new State.Failed("The printer closed the camera connection.", Retrying: true), _feed.State);
        _clock.Advance(3);
        Current.Send(Video(0, keyframe: true));
        Assert.Equal(new State.Live(), _feed.State);
        Current.Send(new CameraEvent.Failed(new CameraFailure.Closed()));
        Assert.Equal(1, _clock.Pending.Single().TotalSeconds);
    }

    [Fact]
    public void AWrongCodeStopsUntilTriedAgain()
    {
        Watch();
        Current.Send(new CameraEvent.Failed(new CameraFailure.Unauthorized()));
        Assert.Equal(new State.Failed("The printer didn't accept the access code for the camera.", Retrying: false), _feed.State);
        Assert.Empty(_clock.Pending);
        _clock.Advance(60);
        Assert.Single(_clients);
        // A new report (say, the printer reconnected) is a reason to try again.
        _feed.Update();
        Assert.Equal(2, _clients.Count);
        Current.Send(new CameraEvent.Failed(new CameraFailure.Certificate("not signed by Bambu Lab")));
        _feed.TryAgain();
        Assert.Equal(3, _clients.Count);
    }

    [Fact]
    public void EventsFromAStoppedClientAreIgnored()
    {
        Watch();
        var first = Current;
        _feed.Restart();
        Assert.True(first.Stopped);
        first.Send(new CameraEvent.Failed(new CameraFailure.TimedOut()));
        Assert.Equal(new State.Connecting(), _feed.State);
        Assert.Empty(_clock.Pending);
    }

    [Fact]
    public void RestartOnlyReconnectsARunningStream()
    {
        _feed.Restart();
        Assert.Empty(_clients);
        Watch();
        _feed.Restart();
        Assert.Equal(2, _clients.Count);
    }

    [Fact]
    public void ViewersAreHeldWeakly()
    {
        AttachAndForget();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _feed.ViewersChanged();
        _clock.Advance(4);
        Assert.True(Current.Stopped);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void AttachAndForget() => _feed.Attach(new Viewer());
}

/// <summary>The store and session tell their feeds what changed: the camera switches, the connection, the print.</summary>
public sealed class CameraStoreTests : IDisposable
{
    private readonly SerialContext _context = new("CameraStoreTests");
    private PrinterStore? _store;

    public void Dispose()
    {
        if (_store is { } store) _context.Send(_ => store.Dispose(), null);
        _context.Dispose();
    }

    [Fact]
    public void SwitchesAndConnectionReachTheFeed()
    {
        var states = _context.Invoke(() =>
        {
            var store = _store = new PrinterStore(new PrinterStore.Services
            {
                Settings = new AppSettings(null), History = new HistoryStore(null), Secrets = new MemorySecretStore(),
                DiscoveryPorts = [], Arguments = [],
                Camera = new CameraServices { DecoderProblem = () => null, Client = _ => throw new InvalidOperationException("no connection") },
            });
            // Port 1 on loopback: refused at once, so the printer stays offline.
            store.Connect(new() { Host = "127.0.0.1:1", Serial = "22E00A000000123", Name = "P2S", ModelCode = "N7" }, "12345678");
            var session = store.Sessions.Single();
            var feed = session.Camera;
            var seen = new List<string>();
            void Note() => seen.Add(feed.State is CameraFeed.FeedState.Unavailable u ? u.Reason : feed.State.ToString());
            // No report yet, so offline as far as the camera is concerned.
            store.CameraEnabled = true;
            Note();
            store.SetCameraEnabled(true, session.Id);
            Note();
            store.CameraEnabled = false;
            Note();
            return seen;
        });
        Assert.Equal(["The camera is off.", "The camera starts when the printer is connected.", "The camera is off."], states);
    }
}
