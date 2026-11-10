using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;

namespace BambuBar.Core.Tests.Camera;

/// <summary>The pipeline's own rules, with a decoder that records what it's given: a slow decoder skips to the next
/// keyframe instead of lagging, a waiting still replaces an older one, the UI gets only the newest picture, buffers are
/// reused, and a decoder that can't be made is reported once.</summary>
public class DecodePipelineTests
{
    private sealed class Decoder : ICameraDecoder
    {
        public readonly List<string> Seen = [];
        public readonly ManualResetEventSlim Gate = new(true);
        public readonly List<CameraFrame?> Reused = [];
        public readonly SemaphoreSlim Entered = new(0);

        public CameraFrame? Decode(VideoFrame frame, CameraFrame? reuse = null)
        {
            Entered.Release();
            Gate.Wait(TimeSpan.FromSeconds(10));
            lock (Seen) { Seen.Add($"{frame.Timestamp}"); Reused.Add(reuse); }
            return reuse ?? new CameraFrame(32, 32);
        }

        public CameraFrame? DecodeJpeg(ReadOnlySpan<byte> jpeg, CameraFrame? reuse = null)
        {
            Entered.Release();
            Gate.Wait(TimeSpan.FromSeconds(10));
            lock (Seen) Seen.Add($"jpeg {jpeg.Length}");
            return new CameraFrame(64, 48);
        }

        public void Flush() { lock (Seen) Seen.Add("flush"); }
        public void Dispose() { }
    }

    private static readonly VideoFormat Format = VideoFormat.Create(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps])!;
    private static VideoFrame Frame(uint timestamp, bool keyframe = false) => new(Fixtures.H264P, keyframe, timestamp, Format);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Token);
        Assert.True(condition());
    }

    [Fact]
    public async Task ASlowDecoderSkipsToTheNextKeyframe()
    {
        var decoder = new Decoder();
        var shown = new List<CameraFrame>();
        using var pipeline = new DecodePipeline(() => decoder, f => { lock (shown) shown.Add(f); }, _ => { });
        decoder.Gate.Reset();
        pipeline.Decode(Frame(0, keyframe: true));
        // The decoder is busy with the first frame while the rest pile up.
        Assert.True(await decoder.Entered.WaitAsync(TimeSpan.FromSeconds(10), Token));
        for (uint i = 1; i <= DecodePipeline.MaxBacklog + 5; i++) pipeline.Decode(Frame(i));
        pipeline.Decode(Frame(100, keyframe: true));
        pipeline.Decode(Frame(101));
        decoder.Gate.Set();
        await Until(() => { lock (decoder.Seen) return decoder.Seen.Contains("101"); });
        lock (decoder.Seen) Assert.Equal(["0", "flush", "100", "101"], decoder.Seen);
    }

    [Fact]
    public async Task OnlyTheNewestWaitingStillIsDecoded()
    {
        var decoder = new Decoder();
        using var pipeline = new DecodePipeline(() => decoder, _ => { }, _ => { });
        decoder.Gate.Reset();
        pipeline.DecodeJpeg(new byte[1]);
        Assert.True(await decoder.Entered.WaitAsync(TimeSpan.FromSeconds(10), Token));
        pipeline.DecodeJpeg(new byte[2]);
        pipeline.DecodeJpeg(new byte[3]);
        decoder.Gate.Set();
        await Until(() => { lock (decoder.Seen) return decoder.Seen.Contains("jpeg 3"); });
        lock (decoder.Seen) Assert.DoesNotContain("jpeg 2", decoder.Seen);
    }

    [Fact]
    public async Task ReleasedBuffersAreReused()
    {
        var decoder = new Decoder();
        using var context = new SerialContext("DecodePipelineTests");
        var shown = new List<CameraFrame>();
        var pipeline = context.Invoke(() => new DecodePipeline(() => decoder, f => { lock (shown) shown.Add(f); }, _ => { }));
        using (pipeline)
        {
            pipeline.Decode(Frame(0, keyframe: true));
            await Until(() => { lock (shown) return shown.Count == 1; });
            CameraFrame first;
            lock (shown) first = shown[0];
            pipeline.Release(first);
            pipeline.Decode(Frame(1));
            await Until(() => { lock (shown) return shown.Count == 2; });
            lock (decoder.Seen) Assert.Same(first, decoder.Reused[1]);
        }
    }

    [Fact]
    public async Task ADecoderThatCannotBeMadeIsReportedOnce()
    {
        var errors = new List<string>();
        using var pipeline = new DecodePipeline(() => throw new CameraDecodeException("no FFmpeg"), _ => { }, e => { lock (errors) errors.Add(e); });
        pipeline.Decode(Frame(0, keyframe: true));
        pipeline.DecodeJpeg([1]);
        pipeline.CatchUp([Frame(0, keyframe: true)]);
        await Until(() => { lock (errors) return errors.Count > 0; });
        await Task.Delay(100, Token);
        lock (errors) Assert.Equal(["no FFmpeg"], errors);
    }
}
