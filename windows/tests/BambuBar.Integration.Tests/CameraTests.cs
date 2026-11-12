using System.Text.Json;
using BambuBar.Core.Camera;
using BambuBar.Core.Protocol;

namespace BambuBar.Integration.Tests;

/// <summary>
/// The camera clients over real TLS against the mock camera (shared/scripts/camera/camera.py), one scenario per test:
/// the good streams of both protocols, and what a broken or hostile camera may send. Whatever arrives, the client must
/// end with a failure the app can show, never crash it (review finding P1). Ported from the Mac's
/// CameraIntegrationTests.swift, plus the Windows trap of ledger §4.1: every connection shows the certificate.
/// </summary>
[Collection(nameof(Simulators))]
public class CameraTests
{
    private abstract record Event
    {
        public sealed record Started(string Description) : Event;
        public sealed record Video(bool Keyframe) : Event;
        public sealed record Image(int Width, int Height) : Event;
        public sealed record Failed(CameraFailure Failure) : Event;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Streams from the mock camera in <paramref name="scenario"/> until <paramref name="done"/> says so (or a
    /// failure arrives) within <paramref name="timeoutSeconds"/>, then stops the client.</summary>
    private static async Task<List<Event>> RecordAsync(string scenario, CameraTransport transport, Func<List<Event>, bool> done,
        string code = CameraSimulator.Code, bool insecure = true, int timeoutSeconds = 10, Action<string?>? onCertificate = null)
    {
        Simulators.Require();
        await CameraSimulator.SetScenarioAsync(scenario, Token);
        var events = new List<Event>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CameraClient.Create(new CameraConfig
        {
            Host = CameraSimulator.Host, Serial = CameraSimulator.Serial, AccessCode = code, Transport = transport, Insecure = insecure,
            OnCertificate = onCertificate is null ? null : leaf => onCertificate(BambuTrust.CommonName(leaf)),
        });
        client.OnEvent = e =>
        {
            Event recorded = e switch
            {
                CameraEvent.Started s => new Event.Started(s.Description),
                CameraEvent.Video v => new Event.Video(v.IsKeyframe),
                CameraEvent.Image i => new Event.Image(i.Width, i.Height),
                CameraEvent.Failed f => new Event.Failed(f.Failure),
                _ => throw new InvalidOperationException($"{e}"),
            };
            lock (events)
            {
                events.Add(recorded);
                if (done(events) || recorded is Event.Failed) finished.TrySetResult();
            }
        };
        client.Start();
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), Token);
        }
        finally
        {
            client.Stop();
        }
        lock (events) return [.. events];
    }

    private static CameraFailure? FailureIn(List<Event> events) => events.OfType<Event.Failed>().FirstOrDefault()?.Failure;

    private static List<Event.Video> VideoIn(List<Event> events) => [.. events.OfType<Event.Video>()];

    private static string Describe(List<Event> events) => string.Join(", ", events);

    private static async Task<List<JsonElement>> RequestsAsync(string method) =>
        [.. (await CameraSimulator.EventsAsync(Token)).Where(e =>
            e.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == method)];

    // MARK: RTSPS: good streams

    private static async Task AssertPlaysAsync(string scenario, string codec)
    {
        var events = await RecordAsync(scenario, CameraTransport.Rtsps, list => VideoIn(list).Count >= 12);
        Assert.True(events.FirstOrDefault() == new Event.Started(codec), Describe(events));
        Assert.True(FailureIn(events) is null, Describe(events));
        var video = VideoIn(events);
        Assert.True(video.Count >= 12);
        Assert.True(video[0].Keyframe, "a keyframe first");
        Assert.Contains(new Event.Video(false), video.Skip(1));
        Assert.True(video.Count(v => v.Keyframe) >= 2, "the next keyframe too");
    }

    [Fact]
    public Task RtspBasicAuthH264() => AssertPlaysAsync("h264-basic", "H.264 · 32×32");

    [Fact]
    public Task RtspDigestAuthH264() => AssertPlaysAsync("h264-digest", "H.264 · 32×32");

    [Fact]
    public Task RtspDigestAuthH265() => AssertPlaysAsync("h265", "H.265 · 64×64");

    /// <summary>Responses and packets cut into three-byte writes, so every boundary falls somewhere unexpected.</summary>
    [Fact]
    public Task RtspStreamInTinyPieces() => AssertPlaysAsync("trickle", "H.264 · 32×32");

    /// <summary>Stopping tells the printer, which serves only a few viewers at a time.</summary>
    [Fact]
    public async Task StoppingSendsTeardown()
    {
        await RecordAsync("h264-digest", CameraTransport.Rtsps, list => VideoIn(list).Count >= 2);
        var teardowns = new List<JsonElement>();
        for (var i = 0; i < 20 && teardowns.Count == 0; i++)
        {
            await Task.Delay(100, Token);
            teardowns = await RequestsAsync("TEARDOWN");
        }
        Assert.Single(teardowns);
    }

    // MARK: RTSPS: refused and broken

    /// <summary>Credentials refused twice (the second time against a fresh challenge, in case the first nonce was stale)
    /// mean the code is wrong: no more tries.</summary>
    [Fact]
    public async Task RtspWrongCodeIsUnauthorizedWithoutRetryingForever()
    {
        var events = await RecordAsync("h264-digest", CameraTransport.Rtsps, _ => false, code: "WRONG000");
        Assert.Equal(new CameraFailure.Unauthorized(), FailureIn(events));
        await Task.Delay(300, Token);
        var describes = await RequestsAsync("DESCRIBE");
        Assert.Equal([false, true, true], describes.Select(d => d.GetProperty("authorization").GetBoolean()));
    }

    /// <summary>Review finding P1: this response crashed the Mac app (an inverted range in the parser).</summary>
    [Fact]
    public async Task NegativeContentLengthFailsWithoutCrashing()
    {
        var events = await RecordAsync("negative-length", CameraTransport.Rtsps, _ => false);
        var malformed = Assert.IsType<CameraFailure.Malformed>(FailureIn(events));
        Assert.Contains("Content-Length", malformed.Reason);
    }

    /// <summary>Would overflow the body's end offset.</summary>
    [Fact]
    public async Task HugeContentLengthFailsWithoutCrashing()
    {
        var events = await RecordAsync("huge-length", CameraTransport.Rtsps, _ => false);
        Assert.IsType<CameraFailure.Malformed>(FailureIn(events));
    }

    /// <summary>Review finding P3: VP8 was handed to the H.264 depacketizer.</summary>
    [Fact]
    public async Task UnsupportedCodecIsReported()
    {
        var events = await RecordAsync("vp8", CameraTransport.Rtsps, _ => false);
        Assert.IsType<CameraFailure.Unsupported>(FailureIn(events));
        Assert.True((await RequestsAsync("SETUP")).Count == 0, "no stream is set up for a codec that can't play");
    }

    /// <summary>Review finding P2: a unit's first fragment and its last carry different timestamps; they were glued into
    /// a bogus keyframe. Only the real keyframe and the delta frame after it may play.</summary>
    [Fact]
    public async Task FragmentsFromDifferentFramesAreNotJoined()
    {
        var events = await RecordAsync("split-fragment", CameraTransport.Rtsps, list => VideoIn(list).Count >= 2);
        await Task.Delay(1000, Token); // anything else the camera sent has arrived by now
        Assert.True(FailureIn(events) is null, Describe(events));
        Assert.Equal([new Event.Video(true), new Event.Video(false)], VideoIn(events));
    }

    [Fact]
    public async Task CameraThatIsNotStreamingIsUnreachable()
    {
        var events = await RecordAsync("not-streaming", CameraTransport.Rtsps, _ => false);
        var unreachable = Assert.IsType<CameraFailure.Unreachable>(FailureIn(events));
        Assert.Contains("503", unreachable.Reason);
    }

    /// <summary>The mock camera's certificate is self-signed: without <c>Insecure</c> it must be refused before anything is
    /// sent.</summary>
    [Fact]
    public async Task UntrustedCertificateIsRefused()
    {
        var events = await RecordAsync("h264-digest", CameraTransport.Rtsps, _ => false, insecure: false);
        Assert.IsType<CameraFailure.Certificate>(FailureIn(events));
        Assert.True((await RequestsAsync("DESCRIBE")).Count == 0, "the access code never left");
    }

    /// <summary>A connection to the same camera a moment ago must not let the next one skip the certificate check (a
    /// resumed TLS session has no certificate to check): the certificate is the only proof the camera is the expected
    /// printer.</summary>
    [Fact]
    public async Task CertificateIsCheckedOnEveryConnection()
    {
        var insecure = await RecordAsync("h264-digest", CameraTransport.Rtsps, list => VideoIn(list).Count >= 2);
        Assert.True(FailureIn(insecure) is null, Describe(insecure));
        var secure = await RecordAsync("h264-digest", CameraTransport.Rtsps, _ => false, insecure: false);
        Assert.IsType<CameraFailure.Certificate>(FailureIn(secure));
    }

    /// <summary>Windows only (ledger §4.1): SChannel resumes TLS sessions unless told not to, and a resumed session never
    /// shows the certificate. Each of several RTSPS connections in a row must show the camera's.</summary>
    [Fact]
    public async Task EveryRtspsConnectionShowsTheCertificate()
    {
        var names = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            var events = await RecordAsync("h264-digest", CameraTransport.Rtsps, list => VideoIn(list).Count >= 1,
                onCertificate: name => { lock (names) names.Add(name); });
            Assert.True(FailureIn(events) is null, Describe(events));
        }
        Assert.Equal([CameraSimulator.Serial, CameraSimulator.Serial, CameraSimulator.Serial], names);
    }

    /// <summary>The handshake gets no answer: the client gives up after 20 seconds instead of waiting forever.</summary>
    [Fact]
    public async Task StalledCameraTimesOut()
    {
        var events = await RecordAsync("stall", CameraTransport.Rtsps, _ => false, timeoutSeconds: 30);
        Assert.Equal(new CameraFailure.TimedOut(), FailureIn(events));
    }

    // MARK: JPEG stream

    [Fact]
    public async Task JpegStream()
    {
        var events = await RecordAsync("jpeg", CameraTransport.Jpeg, list => list.Count >= 4);
        Assert.Equal(new Event.Started("JPEG · 64×48"), events[0]);
        Assert.Equal(Enumerable.Repeat<Event>(new Event.Image(64, 48), events.Count - 1), events.Skip(1));
    }

    [Fact]
    public async Task JpegStreamInSmallPieces()
    {
        var events = await RecordAsync("jpeg-trickle", CameraTransport.Jpeg, list => list.Count >= 4);
        Assert.Equal(new Event.Started("JPEG · 64×48"), events[0]);
        Assert.True(FailureIn(events) is null, Describe(events));
    }

    /// <summary>The printer hangs up on a wrong code without a word.</summary>
    [Fact]
    public async Task JpegWrongCodeIsReported()
    {
        var events = await RecordAsync("jpeg", CameraTransport.Jpeg, _ => false, code: "WRONG000");
        var unreachable = Assert.IsType<CameraFailure.Unreachable>(FailureIn(events));
        Assert.Contains("access code", unreachable.Reason);
    }

    [Fact]
    public async Task JpegFrameOfNoBytesIsMalformed()
    {
        var events = await RecordAsync("jpeg-zero", CameraTransport.Jpeg, _ => false);
        Assert.Equal(new CameraFailure.Malformed("frame header"), FailureIn(events));
    }
}
