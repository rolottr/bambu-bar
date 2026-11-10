using BambuBar.Core.Camera;

namespace BambuBar.Core.Tests.Camera;

/// <summary>NAL units from a 32×32 H.264 (baseline) and a 64×64 H.265 stream made with ffmpeg, SEI removed.</summary>
internal static class Fixtures
{
    public static readonly byte[] H264Sps = Convert.FromHexString("6742c00ad9096c044000000300400000030103c48992");
    public static readonly byte[] H264Pps = Convert.FromHexString("68cb83cb20");
    public static readonly byte[] H264Idr = Convert.FromHexString("658884053c98a0002053275d78");
    public static readonly byte[] H264P = Convert.FromHexString("419a38097960");
    public static readonly byte[] H265Vps = Convert.FromHexString("40010c01ffff01600000030090000003000003001e928090");
    public static readonly byte[] H265Sps = Convert.FromHexString("42010101600000030090000003000003001ea0208105964a924caf0168080000030008000003001040");
    public static readonly byte[] H265Pps = Convert.FromHexString("4401c172b42240");
    public static readonly byte[] H265Idr = Convert.FromHexString("2801af78ebaf3affac95f2d3c0");
    public static readonly byte[] H265P = Convert.FromHexString("0201d0097e10c6f0f57c");
}

/// <summary>Ported from the Mac's VideoDepacketizerTests.swift. Where the Mac checks a CMSampleBuffer (AVCC, NotSync
/// attachment), these check the Annex-B access unit and its keyframe flag.</summary>
public class VideoDepacketizerTests
{
    private ushort _sequence = 100;

    /// <summary>Pushes one packet with the next sequence number.</summary>
    private List<VideoFrame> Push(VideoDepacketizer d, byte[] payload, uint timestamp, bool marker = false, ushort skip = 0)
    {
        _sequence = unchecked((ushort)(_sequence + 1 + skip));
        return d.Push(payload, _sequence, timestamp, marker);
    }

    private static byte[] AnnexB(params byte[][] nals) => [.. nals.SelectMany(nal => (byte[])[0, 0, 0, 1, .. nal])];

    private static byte[] Fu(byte indicator, byte header, ReadOnlySpan<byte> body) => [indicator, header, .. body];

    [Fact]
    public void H264SingleNalUnits()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, []);
        Assert.Empty(Push(d, Fixtures.H264Sps, 1000));
        Assert.Empty(Push(d, Fixtures.H264Pps, 1000));
        var key = Push(d, Fixtures.H264Idr, 1000, marker: true);
        Assert.Single(key);
        Assert.True(key[0].IsKeyframe);
        Assert.Equal("32×32", Assert.IsType<VideoFormat>(d.Format).DimensionsText);
        // Parameter sets live in the format, not in the frame.
        Assert.Equal(AnnexB(Fixtures.H264Idr), key[0].Data);
        Assert.Equal([Fixtures.H264Sps, Fixtures.H264Pps], key[0].Format.ParameterSets);
        Assert.Equal(AnnexB(Fixtures.H264Sps, Fixtures.H264Pps), key[0].Format.AnnexBParameterSets);
        Assert.Equal(1000u, key[0].Timestamp);

        var delta = Push(d, Fixtures.H264P, 4000, marker: true);
        Assert.Single(delta);
        Assert.False(delta[0].IsKeyframe);
        Assert.Equal(AnnexB(Fixtures.H264P), delta[0].Data);
        Assert.Equal(4000u, delta[0].Timestamp);
    }

    [Fact]
    public void H264StapAndFuA()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, []);
        var stap = new List<byte> { 24 };
        foreach (var set in new[] { Fixtures.H264Sps, Fixtures.H264Pps }) stap.AddRange([0, (byte)set.Length, .. set]);
        Assert.Empty(Push(d, [.. stap], 1000));
        var idr = Fixtures.H264Idr;
        var indicator = (byte)((idr[0] & 0xE0) | 28);
        var type = (byte)(idr[0] & 0x1F);
        Assert.Empty(Push(d, Fu(indicator, (byte)(0x80 | type), idr.AsSpan(1, 4)), 1000));
        Assert.Empty(Push(d, Fu(indicator, type, idr.AsSpan(5, 4)), 1000));
        var frames = Push(d, Fu(indicator, (byte)(0x40 | type), idr.AsSpan(9)), 1000, marker: true);
        Assert.Single(frames);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(AnnexB(idr), frames[0].Data);
    }

    [Fact]
    public void FrameEndsWhenTheTimestampChangesWithoutAMarker()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        Assert.Empty(Push(d, Fixtures.H264Idr, 1000));
        var frames = Push(d, Fixtures.H264P, 4000);
        Assert.Single(frames);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(1000u, frames[0].Timestamp);
    }

    [Fact]
    public void FramesBeforeTheFirstKeyframeAreDropped()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        Assert.Empty(Push(d, Fixtures.H264P, 1000, marker: true));
        Assert.Single(Push(d, Fixtures.H264Idr, 4000, marker: true));
    }

    [Fact]
    public void ALostPacketDropsTheFrame()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        var idr = Fixtures.H264Idr;
        var indicator = (byte)((idr[0] & 0xE0) | 28);
        Assert.Empty(Push(d, Fu(indicator, 0x85, idr.AsSpan(1, 4)), 1000));
        Assert.Empty(Push(d, Fu(indicator, 0x45, idr.AsSpan(9)), 1000, marker: true, skip: 1));
        // The next whole keyframe plays.
        Assert.Single(Push(d, idr, 4000, marker: true));
    }

    /// <summary>A unit's first fragment at one timestamp and its last at the next belong to different frames: nothing of
    /// the two is played, and the next whole keyframe is, unaltered.</summary>
    [Fact]
    public void AnUnfinishedFragmentDoesNotCrossIntoTheNextFrame()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        var idr = Fixtures.H264Idr;
        var indicator = (byte)((idr[0] & 0xE0) | 28);
        Assert.Empty(Push(d, Fu(indicator, 0x85, idr.AsSpan(1, 4)), 1000));
        // The end of a unit from another frame isn't a keyframe.
        Assert.Empty(Push(d, Fu(indicator, 0x45, idr.AsSpan(5)), 4000, marker: true));
        var frames = Push(d, idr, 7000, marker: true);
        Assert.Single(frames);
        Assert.Equal(AnnexB(idr), frames[0].Data);
    }

    /// <summary>The frame's marker arrives while a unit is still incomplete (its last fragment was lost without a gap in
    /// sequence numbers, or never sent): the frame is dropped instead of played without that unit.</summary>
    [Fact]
    public void AFrameEndingMidFragmentIsDropped()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        var idr = Fixtures.H264Idr;
        var indicator = (byte)((idr[0] & 0xE0) | 28);
        Assert.Empty(Push(d, Fixtures.H264P, 1000));
        Assert.Empty(Push(d, Fu(indicator, 0x85, idr.AsSpan(1, 4)), 1000, marker: true));
        Assert.Single(Push(d, idr, 4000, marker: true));
    }

    /// <summary>A middle or last fragment with no first one: its start is gone, so is the frame.</summary>
    [Fact]
    public void AFragmentWithoutItsStartSpoilsTheFrame()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        var idr = Fixtures.H264Idr;
        var indicator = (byte)((idr[0] & 0xE0) | 28);
        Assert.Empty(Push(d, idr, 1000));
        Assert.Empty(Push(d, Fu(indicator, 0x45, idr.AsSpan(5)), 1000, marker: true));
    }

    [Fact]
    public void H265UnfinishedFragmentDoesNotCrossIntoTheNextFrame()
    {
        var d = new VideoDepacketizer(VideoCodec.H265, [Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps]);
        var idr = Fixtures.H265Idr;
        var type = (byte)((idr[0] >> 1) & 0x3F);
        byte[] header = [(byte)((idr[0] & 0x81) | (49 << 1)), idr[1]];
        Assert.Empty(Push(d, [.. header, (byte)(0x80 | type), .. idr[2..7]], 1000));
        Assert.Empty(Push(d, [.. header, (byte)(0x40 | type), .. idr[7..]], 4000, marker: true));
        Assert.Single(Push(d, idr, 7000, marker: true));
    }

    [Fact]
    public void SequenceNumbersWrapAround()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [Fixtures.H264Sps, Fixtures.H264Pps]);
        Assert.Single(d.Push(Fixtures.H264Idr, ushort.MaxValue, 1000, marker: true));
        Assert.Single(d.Push(Fixtures.H264P, 0, 4000, marker: true));
    }

    [Fact]
    public void H265FragmentationUnits()
    {
        var d = new VideoDepacketizer(VideoCodec.H265, [Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps]);
        var idr = Fixtures.H265Idr;
        var type = (byte)((idr[0] >> 1) & 0x3F);
        byte[] header = [(byte)((idr[0] & 0x81) | (49 << 1)), idr[1]];
        Assert.Empty(Push(d, [.. header, (byte)(0x80 | type), .. idr[2..7]], 1000));
        var frames = Push(d, [.. header, (byte)(0x40 | type), .. idr[7..]], 1000, marker: true);
        Assert.Single(frames);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal("64×64", Assert.IsType<VideoFormat>(d.Format).DimensionsText);
        Assert.Equal(AnnexB(idr), frames[0].Data);
        Assert.Equal([Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps], frames[0].Format.ParameterSets);

        var delta = Push(d, Fixtures.H265P, 4000, marker: true);
        Assert.Single(delta);
        Assert.False(delta[0].IsKeyframe);
    }

    [Fact]
    public void H265AggregationPacketCarriesParameterSets()
    {
        var d = new VideoDepacketizer(VideoCodec.H265, []);
        var ap = new List<byte> { 48 << 1, 1 };
        foreach (var set in new[] { Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps }) ap.AddRange([0, (byte)set.Length, .. set]);
        Assert.Empty(Push(d, [.. ap], 1000));
        Assert.Single(Push(d, Fixtures.H265Idr, 1000, marker: true));
        Assert.NotNull(d.Format);
    }

    /// <summary>Windows only: a keyframe whose SPS can't be read gives no format (the Mac's VideoToolbox refuses one),
    /// so nothing plays.</summary>
    [Fact]
    public void AnUnreadableSpsPlaysNothing()
    {
        var d = new VideoDepacketizer(VideoCodec.H264, [[0x67, 0x42], Fixtures.H264Pps]);
        Assert.Empty(Push(d, Fixtures.H264Idr, 1000, marker: true));
        Assert.Null(d.Format);
    }
}
