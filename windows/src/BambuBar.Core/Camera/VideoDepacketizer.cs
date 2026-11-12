namespace BambuBar.Core.Camera;

public enum VideoCodec
{
    H264,
    H265,
}

public static class VideoCodecExtensions
{
    /// <summary>"H.264" or "H.265", as the stream's description shows it.</summary>
    public static string DisplayName(this VideoCodec codec) => codec == VideoCodec.H264 ? "H.264" : "H.265";
}

/// <summary>
/// What a decoder needs before the first frame: the codec, its parameter sets (SPS and PPS; VPS, SPS and PPS for
/// H.265) and the picture size they describe. Stands in for the Mac's <c>CMVideoFormatDescription</c>.
/// </summary>
public sealed class VideoFormat
{
    public VideoCodec Codec { get; }
    /// <summary>The parameter set NAL units without start codes: SPS, PPS (H.264) or VPS, SPS, PPS (H.265).</summary>
    public IReadOnlyList<byte[]> ParameterSets { get; }
    /// <summary>The displayed picture size (after the SPS's cropping).</summary>
    public int Width { get; }
    public int Height { get; }

    private VideoFormat(VideoCodec codec, IReadOnlyList<byte[]> sets, int width, int height)
    {
        Codec = codec;
        ParameterSets = sets;
        Width = width;
        Height = height;
    }

    /// <summary>"32×32".</summary>
    public string DimensionsText => $"{Width}×{Height}";

    /// <summary>The parameter sets in Annex-B (each behind 00 00 00 01): a decoder's extradata, or what goes in front of
    /// a keyframe for a decoder that wants them in-band.</summary>
    public byte[] AnnexBParameterSets => AnnexB.Join(ParameterSets);

    /// <summary>A format from complete parameter sets, or null if the SPS can't be read (as the Mac's
    /// <c>CMVideoFormatDescriptionCreateFrom…ParameterSets</c> fails on one).</summary>
    public static VideoFormat? Create(VideoCodec codec, IReadOnlyList<byte[]> sets)
    {
        var sps = codec == VideoCodec.H264 ? sets[0] : sets[1];
        var size = codec == VideoCodec.H264 ? SequenceParameterSet.H264Size(sps) : SequenceParameterSet.H265Size(sps);
        return size is var (width, height) ? new VideoFormat(codec, sets, width, height) : null;
    }
}

/// <summary>One whole compressed picture (an access unit) for the decoder.</summary>
/// <param name="Data">The frame's NAL units in Annex-B, each behind a 00 00 00 01 start code. Parameter sets aren't
/// in it: they're in <see cref="Format"/>.</param>
/// <param name="Timestamp">The RTP timestamp (90 kHz).</param>
public sealed record VideoFrame(byte[] Data, bool IsKeyframe, uint Timestamp, VideoFormat Format);

/// <summary>
/// Turns RTP payloads (RFC 6184 for H.264, RFC 7798 for H.265) into whole frames in Annex-B, ready for a decoder.
/// Parameter sets come from the SDP and from the stream itself; a new <see cref="VideoFormat"/> is made whenever they
/// change. Frames before the first keyframe are dropped. A port of the Mac's VideoDepacketizer.swift, which builds
/// CMSampleBuffers instead.
/// </summary>
public sealed class VideoDepacketizer
{
    public VideoCodec Codec { get; }
    public VideoFormat? Format { get; private set; }

    private byte[]? _vps, _sps, _pps;
    private List<byte[]> _formatSets = [];

    private List<byte[]> _nals = [];
    private bool _isKeyframe;
    private bool _damaged;
    private List<byte>? _fragment;
    private uint? _timestamp;
    private ushort? _lastSequence;
    private bool _sawKeyframe;

    public VideoDepacketizer(VideoCodec codec, IEnumerable<byte[]> parameterSets)
    {
        Codec = codec;
        foreach (var nal in parameterSets) TakeParameterSet(nal);
    }

    /// <summary>One RTP packet's payload. Returns the frames it completed (usually none or one).</summary>
    public List<VideoFrame> Push(ReadOnlySpan<byte> payload, ushort sequence, uint timestamp, bool marker)
    {
        var frames = new List<VideoFrame>();
        if (_lastSequence is { } last && sequence != unchecked((ushort)(last + 1)))
        {
            // A lost packet spoils the frame being assembled.
            _damaged = true;
            _fragment = null;
        }
        _lastSequence = sequence;
        if (_timestamp is { } current && current != timestamp && Flush() is { } previous) frames.Add(previous);
        _timestamp = timestamp;
        if (payload.IsEmpty) return frames;
        if (Codec == VideoCodec.H264) UnpackH264(payload); else UnpackH265(payload);
        if (marker && Flush() is { } frame) frames.Add(frame);
        return frames;
    }

    // MARK: Payloads

    private void UnpackH264(ReadOnlySpan<byte> p)
    {
        var type = p[0] & 0x1F;
        switch (type)
        {
            case >= 1 and <= 23:
                Add(p.ToArray());
                break;
            case 24: // STAP-A: 16-bit sizes, each followed by a NAL unit
                UnpackAggregate(p, 1);
                break;
            case 28: // FU-A
                if (p.Length <= 2) return;
                var header = p[1];
                if ((header & 0x80) != 0)
                {
                    if (_fragment is not null) _damaged = true; // the previous unit never got its end
                    _fragment = [(byte)((p[0] & 0xE0) | (header & 0x1F)), .. p[2..]];
                }
                else if (_fragment is not null)
                {
                    _fragment.AddRange(p[2..]);
                }
                else
                {
                    _damaged = true; // its start was lost
                    return;
                }
                if ((header & 0x40) != 0 && _fragment is { } nal)
                {
                    _fragment = null;
                    Add([.. nal]);
                }
                break;
        }
    }

    private void UnpackH265(ReadOnlySpan<byte> p)
    {
        if (p.Length <= 2) return;
        var type = (p[0] >> 1) & 0x3F;
        switch (type)
        {
            case < 48:
                Add(p.ToArray());
                break;
            case 48: // aggregation packet
                UnpackAggregate(p, 2);
                break;
            case 49: // fragmentation unit
                if (p.Length <= 3) return;
                var header = p[2];
                if ((header & 0x80) != 0)
                {
                    if (_fragment is not null) _damaged = true; // the previous unit never got its end
                    _fragment = [(byte)((p[0] & 0x81) | ((header & 0x3F) << 1)), p[1], .. p[3..]];
                }
                else if (_fragment is not null)
                {
                    _fragment.AddRange(p[3..]);
                }
                else
                {
                    _damaged = true; // its start was lost
                    return;
                }
                if ((header & 0x40) != 0 && _fragment is { } nal)
                {
                    _fragment = null;
                    Add([.. nal]);
                }
                break;
        }
    }

    private void UnpackAggregate(ReadOnlySpan<byte> p, int start)
    {
        var i = start;
        while (i + 2 <= p.Length)
        {
            var size = p[i] << 8 | p[i + 1];
            i += 2;
            if (size <= 0 || i + size > p.Length) return;
            Add(p.Slice(i, size).ToArray());
            i += size;
        }
    }

    // MARK: NAL units

    private void Add(byte[] nal)
    {
        if (nal.Length == 0 || TakeParameterSet(nal)) return;
        if (Codec == VideoCodec.H264)
        {
            var type = nal[0] & 0x1F;
            if (type == 9) return; // access unit delimiter
            if (type == 5) _isKeyframe = true;
        }
        else
        {
            var type = (nal[0] >> 1) & 0x3F;
            if (type == 35) return; // access unit delimiter
            if (type is >= 16 and <= 21) _isKeyframe = true;
        }
        _nals.Add(nal);
    }

    /// <summary>Keeps SPS/PPS (and VPS) aside instead of in the frame. Returns whether <paramref name="nal"/> was one.</summary>
    private bool TakeParameterSet(byte[] nal)
    {
        if (nal.Length == 0) return false;
        if (Codec == VideoCodec.H264)
        {
            switch (nal[0] & 0x1F)
            {
                case 7: _sps = nal; break;
                case 8: _pps = nal; break;
                default: return false;
            }
        }
        else
        {
            switch ((nal[0] >> 1) & 0x3F)
            {
                case 32: _vps = nal; break;
                case 33: _sps = nal; break;
                case 34: _pps = nal; break;
                default: return false;
            }
        }
        return true;
    }

    // MARK: Frames

    /// <summary>Ends the frame being assembled. A unit still being reassembled from fragments belongs to this frame
    /// (frames are told apart by their RTP timestamp), so it can't continue into the next one: the frame is incomplete
    /// and dropped, and the fragment with it.</summary>
    private VideoFrame? Flush()
    {
        var incomplete = _fragment is not null;
        var nals = _nals;
        var isKeyframe = _isKeyframe;
        var damaged = _damaged;
        _nals = [];
        _isKeyframe = false;
        _damaged = false;
        _fragment = null;
        if (nals.Count == 0 || damaged || incomplete) return null;
        if (isKeyframe)
        {
            UpdateFormat();
            _sawKeyframe = Format is not null;
        }
        if (!_sawKeyframe || Format is null) return null;
        return new VideoFrame(AnnexB.Join(nals), isKeyframe, _timestamp ?? 0, Format);
    }

    private void UpdateFormat()
    {
        List<byte[]> sets;
        if (Codec == VideoCodec.H264)
        {
            if (_sps is null || _pps is null) return;
            sets = [_sps, _pps];
        }
        else
        {
            if (_vps is null || _sps is null || _pps is null) return;
            sets = [_vps, _sps, _pps];
        }
        var same = sets.Count == _formatSets.Count && sets.Zip(_formatSets).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second));
        if (same && Format is not null) return;
        if (VideoFormat.Create(Codec, sets) is not { } format) return;
        Format = format;
        _formatSets = sets;
    }
}

internal static class AnnexB
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>NAL units, each behind a four-byte start code.</summary>
    public static byte[] Join(IEnumerable<byte[]> nals)
    {
        var list = nals as IReadOnlyCollection<byte[]> ?? [.. nals];
        var bytes = new byte[list.Sum(n => n.Length + 4)];
        var offset = 0;
        foreach (var nal in list)
        {
            StartCode.CopyTo(bytes, offset);
            nal.CopyTo(bytes, offset + 4);
            offset += nal.Length + 4;
        }
        return bytes;
    }
}

/// <summary>Reads the picture size from an H.264 or H.265 sequence parameter set (ITU-T H.264 7.3.2.1.1, H.265
/// 7.3.2.2), which the Mac gets from VideoToolbox.</summary>
internal static class SequenceParameterSet
{
    public static (int Width, int Height)? H264Size(byte[] nal)
    {
        try
        {
            var r = new BitReader(Unescape(nal.AsSpan(1)));
            var profile = r.Bits(8);
            r.Skip(16); // constraint flags, level
            r.UE(); // seq_parameter_set_id
            var chromaFormat = 1;
            var separatePlanes = false;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormat = (int)r.UE();
                if (chromaFormat == 3) separatePlanes = r.Bit();
                r.UE(); // bit_depth_luma_minus8
                r.UE(); // bit_depth_chroma_minus8
                r.Skip(1); // qpprime_y_zero_transform_bypass_flag
                if (r.Bit()) // seq_scaling_matrix_present_flag
                {
                    for (var i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                    {
                        if (!r.Bit()) continue;
                        int last = 8, next = 8;
                        for (var j = 0; j < (i < 6 ? 16 : 64) && next != 0; j++)
                        {
                            next = (last + r.SE() + 256) % 256;
                            if (next != 0) last = next;
                        }
                    }
                }
            }
            r.UE(); // log2_max_frame_num_minus4
            var pocType = r.UE();
            if (pocType == 0)
            {
                r.UE();
            }
            else if (pocType == 1)
            {
                r.Skip(1);
                r.SE();
                r.SE();
                var cycle = r.UE();
                if (cycle > 255) return null;
                for (var i = 0u; i < cycle; i++) r.SE();
            }
            r.UE(); // max_num_ref_frames
            r.Skip(1); // gaps_in_frame_num_value_allowed_flag
            var widthMbs = r.UE() + 1;
            var heightMapUnits = r.UE() + 1;
            var frameMbsOnly = r.Bit();
            if (!frameMbsOnly) r.Skip(1);
            r.Skip(1); // direct_8x8_inference_flag
            long left = 0, right = 0, top = 0, bottom = 0;
            if (r.Bit())
            {
                left = r.UE();
                right = r.UE();
                top = r.UE();
                bottom = r.UE();
            }
            var chromaArrayType = separatePlanes ? 0 : chromaFormat;
            var fieldFactor = frameMbsOnly ? 1 : 2;
            int cropX, cropY;
            if (chromaArrayType == 0)
            {
                cropX = 1;
                cropY = fieldFactor;
            }
            else
            {
                cropX = chromaArrayType == 3 ? 1 : 2;
                cropY = (chromaArrayType == 1 ? 2 : 1) * fieldFactor;
            }
            var width = widthMbs * 16 - cropX * (left + right);
            var height = fieldFactor * heightMapUnits * 16 - cropY * (top + bottom);
            return Valid(width, height);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public static (int Width, int Height)? H265Size(byte[] nal)
    {
        try
        {
            if (nal.Length < 3) return null;
            var r = new BitReader(Unescape(nal.AsSpan(2)));
            r.Skip(4); // sps_video_parameter_set_id
            var maxSubLayersMinus1 = r.Bits(3);
            r.Skip(1); // sps_temporal_id_nesting_flag
            // profile_tier_level(1, maxSubLayersMinus1)
            r.Skip(2 + 1 + 5 + 32 + 4 + 43 + 1 + 8);
            var profilePresent = new bool[maxSubLayersMinus1];
            var levelPresent = new bool[maxSubLayersMinus1];
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                profilePresent[i] = r.Bit();
                levelPresent[i] = r.Bit();
            }
            if (maxSubLayersMinus1 > 0)
            {
                for (var i = maxSubLayersMinus1; i < 8; i++) r.Skip(2);
            }
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i]) r.Skip(88);
                if (levelPresent[i]) r.Skip(8);
            }
            r.UE(); // sps_seq_parameter_set_id
            var chromaFormat = r.UE();
            var separatePlanes = chromaFormat == 3 && r.Bit();
            var width = r.UE();
            var height = r.UE();
            if (r.Bit()) // conformance_window_flag
            {
                var chromaArrayType = separatePlanes ? 0 : chromaFormat;
                var subWidth = chromaArrayType is 1 or 2 ? 2 : 1;
                var subHeight = chromaArrayType == 1 ? 2 : 1;
                long left = r.UE(), right = r.UE(), top = r.UE(), bottom = r.UE();
                width -= subWidth * (left + right);
                height -= subHeight * (top + bottom);
            }
            return Valid(width, height);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static (int, int)? Valid(long width, long height) =>
        width is > 0 and <= 16384 && height is > 0 and <= 16384 ? ((int)width, (int)height) : null;

    /// <summary>The RBSP: drops each emulation prevention byte (the 03 in 00 00 03).</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>(data.Length);
        var zeros = 0;
        foreach (var b in data)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            zeros = b == 0 ? zeros + 1 : 0;
            result.Add(b);
        }
        return [.. result];
    }

    private sealed class BitReader(byte[] data)
    {
        private long _position;

        public bool Bit()
        {
            if (_position >= data.LongLength * 8) throw new EndOfStreamException();
            var bit = (data[_position >> 3] >> (7 - (int)(_position & 7))) & 1;
            _position++;
            return bit == 1;
        }

        public int Bits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++) value = value << 1 | (Bit() ? 1 : 0);
            return value;
        }

        public void Skip(int count)
        {
            if (_position + count > data.LongLength * 8) throw new EndOfStreamException();
            _position += count;
        }

        /// <summary>Exp-Golomb, unsigned.</summary>
        public long UE()
        {
            var zeros = 0;
            while (!Bit())
            {
                if (++zeros > 31) throw new EndOfStreamException();
            }
            return (1L << zeros) - 1 + Bits(zeros);
        }

        /// <summary>Exp-Golomb, signed.</summary>
        public int SE()
        {
            var k = UE();
            return (int)((k & 1) == 1 ? (k + 1) / 2 : -(k / 2));
        }
    }
}
