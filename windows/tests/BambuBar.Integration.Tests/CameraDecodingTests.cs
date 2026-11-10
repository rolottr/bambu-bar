using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;

namespace BambuBar.Integration.Tests;

/// <summary>
/// The whole camera path against the mock camera (shared/scripts/camera/camera.py): TLS, RTSP or the JPEG login, the
/// depacketizer, FFmpeg, BGRA. First the decoder on the clients' own output, then a <see cref="CameraFeed"/> as the app
/// runs it, with a viewer on screen, until it shows a picture. Skips without the simulators or without FFmpeg 9.
/// </summary>
[Collection(nameof(Simulators))]
public class CameraDecodingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Require()
    {
        Simulators.Require();
        Assert.SkipUnless(FFmpeg.Status.IsAvailable, $"FFmpeg 9 isn't available: {FFmpeg.Status.Problem}");
    }

    /// <summary>Decodes everything the client hands on for a second or so; returns the picture sizes.</summary>
    private static async Task<List<(int Width, int Height)>> DecodeAsync(string scenario, CameraTransport transport)
    {
        Require();
        await CameraSimulator.SetScenarioAsync(scenario, Token);
        using var decoder = new FFmpegDecoder();
        var sizes = new List<(int, int)>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CameraClient.Create(new CameraConfig
        {
            Host = CameraSimulator.Host, Serial = CameraSimulator.Serial, AccessCode = CameraSimulator.Code, Transport = transport, Insecure = true,
        });
        client.OnEvent = e =>
        {
            if (e is CameraEvent.Failed f)
            {
                enough.TrySetException(new InvalidOperationException(f.Failure.Message));
                return;
            }
            var picture = e switch
            {
                CameraEvent.Video v => decoder.Decode(v.Frame),
                CameraEvent.Image i => decoder.DecodeJpeg(i.Jpeg),
                _ => null,
            };
            if (picture is null) return;
            lock (sizes)
            {
                sizes.Add((picture.Width, picture.Height));
                if (sizes.Count >= 4) enough.TrySetResult();
            }
        };
        client.Start();
        try
        {
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally
        {
            client.Stop();
        }
        lock (sizes) return [.. sizes];
    }

    [Fact]
    public async Task DecodesTheH264Stream() =>
        Assert.All(await DecodeAsync("h264-digest", CameraTransport.Rtsps), size => Assert.Equal((32, 32), size));

    [Fact]
    public async Task DecodesTheH265Stream() =>
        Assert.All(await DecodeAsync("h265", CameraTransport.Rtsps), size => Assert.Equal((64, 64), size));

    [Fact]
    public async Task DecodesTheJpegStream() =>
        Assert.All(await DecodeAsync("jpeg", CameraTransport.Jpeg), size => Assert.Equal((64, 48), size));

    private sealed class Viewer : ICameraViewer
    {
        public bool IsOnScreen => true;
    }

    /// <summary>The feed as the app runs it: on its own (UI) thread, the real client and the real pipeline.</summary>
    [Theory]
    [InlineData("h264-digest", CameraTransport.Rtsps, 32, 32, "H.264 · 32×32")]
    [InlineData("h265", CameraTransport.Rtsps, 64, 64, "H.265 · 64×64")]
    [InlineData("jpeg", CameraTransport.Jpeg, 64, 48, "JPEG · 64×48")]
    public async Task AFeedWithAViewerShowsThePicture(string scenario, CameraTransport transport, int width, int height, string detail)
    {
        Require();
        await CameraSimulator.SetScenarioAsync(scenario, Token);
        using var context = new SerialContext("CameraDecodingTests");
        var shown = new TaskCompletionSource<(int, int, string?, CameraFeed.FeedState)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewer = new Viewer();
        var feed = context.Invoke(() =>
        {
            var situation = new CameraSituation
            {
                ShowsCamera = true, IsOnline = true, Transport = transport, Host = CameraSimulator.Host, Serial = CameraSimulator.Serial,
                AccessCode = CameraSimulator.Code, Insecure = true,
            };
            var feed = new CameraFeed(() => situation);
            feed.FrameChanged += () =>
            {
                if (feed.Frame is { } f) shown.TrySetResult((f.Width, f.Height, feed.Detail, feed.State));
            };
            feed.Attach(viewer);
            return feed;
        });
        try
        {
            var (w, h, d, state) = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal((width, height, detail), (w, h, d));
            Assert.Equal(new CameraFeed.FeedState.Live(), state);
        }
        finally
        {
            context.Send(_ => feed.Stop(), null);
        }
        GC.KeepAlive(viewer);
    }
}
