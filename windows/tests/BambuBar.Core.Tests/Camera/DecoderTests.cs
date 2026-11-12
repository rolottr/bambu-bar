using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;

namespace BambuBar.Core.Tests.Camera;

/// <summary>Annex-B elementary streams cut into access units the way the depacketizer hands them on: parameter sets
/// into the <see cref="VideoFormat"/>, SEI and delimiters dropped, one unit per picture.</summary>
internal static class AnnexBStream
{
    public static string Fixture(string name) => Path.Combine(Repo.Root, "windows", "tests", "BambuBar.Core.Tests", "Camera", "Fixtures", name);

    public static List<VideoFrame> Read(string name, VideoCodec codec) => Split(File.ReadAllBytes(Fixture(name)), codec);

    public static List<VideoFrame> Split(byte[] data, VideoCodec codec)
    {
        var frames = new List<VideoFrame>();
        var sets = new Dictionary<int, byte[]>();
        VideoFormat? format = null;
        var unit = new List<byte[]>();
        var keyframe = false;
        void Flush()
        {
            if (unit.Count == 0) return;
            List<byte[]> current = codec == VideoCodec.H264 ? [sets[7], sets[8]] : [sets[32], sets[33], sets[34]];
            if (format is null || !current.Zip(format.ParameterSets).All(p => p.First.AsSpan().SequenceEqual(p.Second)))
                format = VideoFormat.Create(codec, current) ?? throw new InvalidDataException("unreadable SPS");
            frames.Add(new VideoFrame(AnnexB.Join(unit), keyframe, (uint)(frames.Count * 6000), format));
            unit = [];
            keyframe = false;
        }
        foreach (var nal in Nals(data))
        {
            if (codec == VideoCodec.H264)
            {
                var type = nal[0] & 0x1F;
                if (type is 7 or 8) { sets[type] = nal; continue; }
                if (type is not (1 or 5)) continue; // SEI, delimiters
                if ((nal[1] & 0x80) != 0) Flush(); // first_mb_in_slice == 0: a new picture
                if (type == 5) keyframe = true;
            }
            else
            {
                var type = (nal[0] >> 1) & 0x3F;
                if (type is 32 or 33 or 34) { sets[type] = nal; continue; }
                if (type > 31) continue;
                if ((nal[2] & 0x80) != 0) Flush(); // first_slice_segment_in_pic_flag
                if (type is >= 16 and <= 21) keyframe = true;
            }
            unit.Add(nal);
        }
        Flush();
        return frames;
    }

    private static IEnumerable<byte[]> Nals(byte[] data)
    {
        var starts = new List<(int Start, int Code)>();
        for (var i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1) { starts.Add((i + 3, 3)); i += 2; }
            else if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1) { starts.Add((i + 4, 4)); i += 3; }
        }
        for (var n = 0; n < starts.Count; n++)
        {
            var end = n + 1 < starts.Count ? starts[n + 1].Start - starts[n + 1].Code : data.Length;
            yield return data[starts[n].Start..end];
        }
    }
}

/// <summary>
/// FFmpeg decodes what the camera clients hand on (ledger D17): 1080p H.264 and H.265 access units and a 720p JPEG made
/// with ffmpeg (Fixtures/: three stripes, red, green and blue, and a white square top left), and the mock camera's own
/// tiny streams (shared/scripts/camera/camera.py). Skips where FFmpeg 9 isn't found (Homebrew's on a Mac,
/// BAMBUBAR_FFMPEG, or the system's).
/// </summary>
public class DecoderTests
{
    /// <summary>A 64×48 JPEG, as camera.py sends it.</summary>
    private static readonly byte[] MockJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgI"
        + "CQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEwAAQEAAAAAAAAAAAAAAAAAAAAGAQEBAAAAAAAAAAAAAAAAAAAABhABAAAAAAAAAAAAAAAAAAAAABEB"
        + "AAAAAAAAAAAAAAAAAAAAAP/AABEIADAAQAMBIgACEQADEQD/2gAMAwEAAhEDEQA/AIsBKIkAAAAAAAAAAAAAAAAAAAAAB//Z");

    internal static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.Status.IsAvailable, $"FFmpeg 9 isn't available: {FFmpeg.Status.Problem}");

    /// <summary>Close to a colour, allowing for 4:2:0 and limited-range rounding.</summary>
    private static void AssertColor(CameraFrame frame, int x, int y, (int R, int G, int B) expected)
    {
        var (r, g, b) = frame.Pixel(x, y);
        Assert.True(Math.Abs(r - expected.R) <= 16 && Math.Abs(g - expected.G) <= 16 && Math.Abs(b - expected.B) <= 16,
            $"pixel ({x}, {y}) is ({r}, {g}, {b}), expected about {expected}");
    }

    private static void AssertStripes(CameraFrame frame)
    {
        var third = frame.Width / 3;
        var middle = frame.Height / 2;
        AssertColor(frame, third / 2, middle, (255, 0, 0));
        AssertColor(frame, third + third / 2, middle, (0, 255, 0));
        AssertColor(frame, 2 * third + third / 2, middle, (0, 0, 255));
        Assert.Equal(255, frame.Pixels[3]); // opaque
    }

    [Theory]
    [InlineData("h264-1080p.h264", VideoCodec.H264)]
    [InlineData("h265-1080p.h265", VideoCodec.H265)]
    public void DecodesEveryFrameOf1080pVideo(string file, VideoCodec codec)
    {
        RequireFFmpeg();
        var frames = AnnexBStream.Read(file, codec);
        Assert.Equal(6, frames.Count);
        Assert.Equal([true, false, false, true, false, false], frames.Select(f => f.IsKeyframe));
        Assert.Equal((1920, 1080), (frames[0].Format.Width, frames[0].Format.Height));
        using var decoder = new FFmpegDecoder();
        CameraFrame? previous = null;
        foreach (var frame in frames)
        {
            var picture = decoder.Decode(frame, previous);
            Assert.NotNull(picture);
            Assert.Equal((1920, 1080, 1920 * 4), (picture.Width, picture.Height, picture.Stride));
            // The same buffer, written again: no allocation per frame.
            if (previous is not null) Assert.Same(previous, picture);
            AssertStripes(picture);
            AssertColor(picture, 20, 20, (255, 255, 255));
            previous = picture;
        }
    }

    [Fact]
    public void DecodesA720pJpeg()
    {
        RequireFFmpeg();
        using var decoder = new FFmpegDecoder();
        var picture = decoder.DecodeJpeg(File.ReadAllBytes(AnnexBStream.Fixture("frame-720p.jpg")));
        Assert.NotNull(picture);
        Assert.Equal((1280, 720), (picture.Width, picture.Height));
        AssertStripes(picture);
    }

    /// <summary>What the mock camera streams: the tests' 32×32 H.264, 64×64 H.265 and 64×48 JPEG.</summary>
    [Fact]
    public void DecodesTheMockCamerasStreams()
    {
        RequireFFmpeg();
        using var decoder = new FFmpegDecoder();
        var h264 = VideoFormat.Create(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps])!;
        var picture = decoder.Decode(new VideoFrame(AnnexB.Join([Fixtures.H264Idr]), true, 0, h264));
        Assert.Equal((32, 32), (picture?.Width, picture?.Height));
        Assert.NotNull(decoder.Decode(new VideoFrame(AnnexB.Join([Fixtures.H264P]), false, 3000, h264)));
        // Another codec on the same decoder (a new stream): it opens a new one.
        var h265 = VideoFormat.Create(VideoCodec.H265, [Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps])!;
        picture = decoder.Decode(new VideoFrame(AnnexB.Join([Fixtures.H265Idr]), true, 0, h265));
        Assert.Equal((64, 64), (picture?.Width, picture?.Height));
        picture = decoder.DecodeJpeg(MockJpeg);
        Assert.Equal((64, 48), (picture?.Width, picture?.Height));
    }

    /// <summary>Delta frames before the first keyframe can't be decoded: they're skipped, as on the Mac.</summary>
    [Fact]
    public void WaitsForAKeyframe()
    {
        RequireFFmpeg();
        var frames = AnnexBStream.Read("h264-1080p.h264", VideoCodec.H264);
        using var decoder = new FFmpegDecoder();
        Assert.Null(decoder.Decode(frames[1]));
        Assert.Null(decoder.Decode(frames[2]));
        Assert.NotNull(decoder.Decode(frames[3]));
        Assert.NotNull(decoder.Decode(frames[4]));
        decoder.Flush();
        Assert.Null(decoder.Decode(frames[5]));
    }

    /// <summary>Garbage from a broken camera is dropped, not thrown, and the next keyframe plays.</summary>
    [Fact]
    public void SurvivesDamagedData()
    {
        RequireFFmpeg();
        var frames = AnnexBStream.Read("h265-1080p.h265", VideoCodec.H265);
        using var decoder = new FFmpegDecoder();
        var junk = new byte[4096];
        new Random(7).NextBytes(junk);
        Assert.Null(decoder.Decode(frames[0] with { Data = [0, 0, 0, 1, 0x26, 0x01, .. junk] }));
        Assert.Null(decoder.DecodeJpeg(junk));
        Assert.Null(decoder.DecodeJpeg([0xFF, 0xD8, 0xFF]));
        Assert.NotNull(decoder.Decode(frames[3]));
        Assert.NotNull(decoder.DecodeJpeg(MockJpeg));
    }

    [Fact]
    public void ReportsAFolderWithoutFFmpeg()
    {
        var folder = Directory.CreateTempSubdirectory("bambubar-ffmpeg-").FullName;
        try
        {
            var (status, api) = FFmpeg.Load(folder);
            Assert.Null(api);
            Assert.False(status.IsAvailable);
            Assert.Contains(FFmpeg.FileName("avutil", FFmpeg.AvutilMajor), status.Problem);
            // Another major version beside it: the problem says which, and what's needed.
            File.WriteAllBytes(Path.Combine(folder, FFmpeg.FileName("avutil", 60)), []);
            Assert.Contains("FFmpeg 9 is needed", FFmpeg.Load(folder).Status.Problem);
            Assert.Contains("doesn't exist", FFmpeg.Load(Path.Combine(folder, "nowhere")).Status.Problem);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FileNamesFollowThePlatform()
    {
        var expected = OperatingSystem.IsWindows() ? "avcodec-63.dll" : OperatingSystem.IsMacOS() ? "libavcodec.63.dylib" : "libavcodec.so.63";
        Assert.Equal(expected, FFmpeg.FileName("avcodec", FFmpeg.AvcodecMajor));
    }

    /// <summary>The pipeline decodes on its own thread and hands pictures to the context it was made on.</summary>
    [Fact]
    public async Task PipelineDecodesOffTheCallersThread()
    {
        RequireFFmpeg();
        using var context = new SerialContext("PipelineTests");
        var frames = AnnexBStream.Read("h264-1080p.h264", VideoCodec.H264);
        var shown = new TaskCompletionSource<(CameraFrame Frame, bool OnContext)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = context.Invoke(() => DecodePipeline.Create(
            f => shown.TrySetResult((f, SynchronizationContext.Current == context)), e => shown.TrySetException(new Exception(e))));
        try
        {
            // A viewer came back mid-stream: everything since the keyframe is decoded, the last one shown.
            pipeline.CatchUp(frames[3..]);
            var (frame, onContext) = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(onContext);
            Assert.Equal((1920, 1080), (frame.Width, frame.Height));
            AssertStripes(frame);
        }
        finally
        {
            pipeline.Dispose();
        }
    }
}
