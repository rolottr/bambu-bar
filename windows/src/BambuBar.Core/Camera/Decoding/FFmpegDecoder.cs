using System.Runtime.InteropServices;

namespace BambuBar.Core.Camera.Decoding;

/// <summary>
/// Decodes the camera with FFmpeg (ledger D17): libavcodec's h264, hevc and mjpeg decoders, then libswscale to BGRA.
/// Everything goes through one path, JPEG stills included. A video decoder is opened with the stream's parameter sets
/// as its extradata, and opened again whenever they change. Low delay, one thread: a picture comes out for each
/// access unit that goes in, at once (the cameras send no B-frames). Calls are serialized by a lock; any thread will do.
///
/// FFmpeg has no functions for a few struct fields, so these are read and written at their offsets, which have been
/// the same since FFmpeg 4 and are checked against FFmpeg 9's headers (64-bit only):
/// AVFrame data[0] at 0, linesize[0] at 64, width 104, height 108, format 116;
/// AVCodecParameters codec_type 0, codec_id 4, extradata 16, extradata_size 24; AVCodec id at 20.
/// </summary>
public sealed unsafe class FFmpegDecoder : ICameraDecoder
{
    private const int FrameData = 0, FrameLinesize = 64, FrameWidth = 104, FrameHeight = 108, FrameFormat = 116;
    private const int ParametersType = 0, ParametersCodecId = 4, ParametersExtradata = 16, ParametersExtradataSize = 24;
    private const int CodecId = 20;
    /// <summary>AV_INPUT_BUFFER_PADDING_SIZE: zeroed bytes FFmpeg may read past the end of a packet or extradata.</summary>
    private const int Padding = 64;
    private const int MediaTypeVideo = 0;

    private readonly FFmpegApi _api;
    private readonly Lock _gate = new();
    private void* _decoded;
    private void* _converted;
    private void* _packet;
    private void* _scaler;
    private void* _video;
    private VideoFormat? _format;
    private void* _jpeg;
    private bool _needsKeyframe = true;
    private bool _disposed;

    /// <exception cref="FFmpegUnavailableException">FFmpeg couldn't be loaded.</exception>
    public FFmpegDecoder()
    {
        _api = FFmpeg.Api;
        _decoded = _api.av_frame_alloc();
        _converted = _api.av_frame_alloc();
        _packet = _api.av_packet_alloc();
        _scaler = _api.sws_alloc_context();
        if (_decoded is null || _converted is null || _packet is null || _scaler is null)
        {
            Release();
            throw new OutOfMemoryException("FFmpeg couldn't allocate its frames.");
        }
    }

    ~FFmpegDecoder() => Release();

    public CameraFrame? Decode(VideoFrame frame, CameraFrame? reuse = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_video is null || !SameFormat(frame.Format))
            {
                CloseVideo();
                _video = frame.Format.Codec == VideoCodec.H264
                    ? Open("h264"u8, "H.264", frame.Format.AnnexBParameterSets)
                    : Open("hevc"u8, "H.265", frame.Format.AnnexBParameterSets);
                _format = frame.Format;
                _needsKeyframe = true;
            }
            if (_needsKeyframe && !frame.IsKeyframe) return null;
            var result = Run(_video, frame.Data, reuse);
            if (result.Error)
            {
                // A damaged frame spoils those that refer to it: start over at the next keyframe.
                _api.avcodec_flush_buffers(_video);
                _needsKeyframe = true;
                return null;
            }
            _needsKeyframe = false;
            return result.Frame;
        }
    }

    public CameraFrame? DecodeJpeg(ReadOnlySpan<byte> jpeg, CameraFrame? reuse = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jpeg is null) _jpeg = Open("mjpeg"u8, "JPEG", null);
            var result = Run(_jpeg, jpeg, reuse);
            if (result.Error) _api.avcodec_flush_buffers(_jpeg);
            return result.Frame;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_video is not null) _api.avcodec_flush_buffers(_video);
            _needsKeyframe = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        CloseVideo();
        if (_jpeg is not null) { var c = _jpeg; _api.avcodec_free_context(&c); _jpeg = null; }
        if (_decoded is not null) { var f = _decoded; _api.av_frame_free(&f); _decoded = null; }
        if (_converted is not null)
        {
            // It may still point at a managed buffer; FFmpeg doesn't own that.
            *(byte**)((byte*)_converted + FrameData) = null;
            var f = _converted;
            _api.av_frame_free(&f);
            _converted = null;
        }
        if (_packet is not null) { var p = _packet; _api.av_packet_free(&p); _packet = null; }
        if (_scaler is not null) { var s = _scaler; _api.sws_free_context(&s); _scaler = null; }
    }

    private void CloseVideo()
    {
        if (_video is null) return;
        var c = _video;
        _api.avcodec_free_context(&c);
        _video = null;
        _format = null;
    }

    private bool SameFormat(VideoFormat format)
    {
        if (ReferenceEquals(format, _format)) return true;
        if (_format is null || format.Codec != _format.Codec || format.ParameterSets.Count != _format.ParameterSets.Count) return false;
        for (var i = 0; i < format.ParameterSets.Count; i++)
            if (!format.ParameterSets[i].AsSpan().SequenceEqual(_format.ParameterSets[i])) return false;
        return true;
    }

    /// <summary>Opens FFmpeg's decoder <paramref name="name"/> (UTF-8 literals carry their NUL), primed with
    /// <paramref name="extradata"/> (Annex-B parameter sets) when given.</summary>
    private void* Open(ReadOnlySpan<byte> name, string displayName, byte[]? extradata)
    {
        void* codec;
        fixed (byte* n = name) codec = _api.avcodec_find_decoder_by_name(n);
        if (codec is null) throw new CameraDecodeException(Loc.F("Bambu Bar can't decode this camera's video (%@).", displayName));
        var context = _api.avcodec_alloc_context3(codec);
        if (context is null) throw new OutOfMemoryException("FFmpeg couldn't allocate a decoder.");
        var opened = false;
        try
        {
            if (extradata is { Length: > 0 })
            {
                var parameters = _api.avcodec_parameters_alloc();
                if (parameters is null) throw new OutOfMemoryException("FFmpeg couldn't allocate the stream parameters.");
                try
                {
                    var bytes = (byte*)_api.av_mallocz((nuint)(extradata.Length + Padding));
                    if (bytes is null) throw new OutOfMemoryException("FFmpeg couldn't allocate the parameter sets.");
                    Marshal.Copy(extradata, 0, (IntPtr)bytes, extradata.Length);
                    var p = (byte*)parameters;
                    *(int*)(p + ParametersType) = MediaTypeVideo;
                    *(int*)(p + ParametersCodecId) = *(int*)((byte*)codec + CodecId);
                    // avcodec_parameters_free frees it.
                    *(byte**)(p + ParametersExtradata) = bytes;
                    *(int*)(p + ParametersExtradataSize) = extradata.Length;
                    Check(_api.avcodec_parameters_to_context(context, parameters), displayName);
                }
                finally
                {
                    _api.avcodec_parameters_free(&parameters);
                }
            }
            // A picture out for every access unit in: no reordering delay, no frame threads.
            fixed (byte* key = "flags"u8, value = "+low_delay"u8) _api.av_opt_set(context, key, value, 0);
            Check(_api.avcodec_open2(context, codec, null), displayName);
            opened = true;
            return context;
        }
        finally
        {
            if (!opened) _api.avcodec_free_context(&context);
        }
    }

    private void Check(int result, string displayName)
    {
        if (result < 0) throw new CameraDecodeException(Loc.F("Bambu Bar can't decode this camera's video (%@).", $"{displayName}: {_api.Describe(result)}"));
    }

    /// <summary>Sends one packet and takes the newest picture it yields, converted to BGRA.</summary>
    private (CameraFrame? Frame, bool Error) Run(void* context, ReadOnlySpan<byte> data, CameraFrame? reuse)
    {
        if (data.IsEmpty) return (null, true);
        var buffer = (byte*)_api.av_mallocz((nuint)(data.Length + Padding));
        if (buffer is null) return (null, true);
        data.CopyTo(new Span<byte>(buffer, data.Length));
        if (_api.av_packet_from_data(_packet, buffer, data.Length) < 0)
        {
            _api.av_free(buffer);
            return (null, true);
        }
        CameraFrame? picture = null;
        var error = false;
        try
        {
            var sent = _api.avcodec_send_packet(context, _packet);
            if (sent == _api.ErrorAgain)
            {
                // The decoder holds pictures not yet taken: take them, then it accepts the packet.
                picture = Receive(context, reuse, ref error) ?? picture;
                sent = _api.avcodec_send_packet(context, _packet);
            }
            if (sent < 0) return (picture, true);
            picture = Receive(context, picture ?? reuse, ref error) ?? picture;
            return (picture, error);
        }
        finally
        {
            _api.av_packet_unref(_packet);
        }
    }

    private CameraFrame? Receive(void* context, CameraFrame? reuse, ref bool error)
    {
        CameraFrame? picture = null;
        while (true)
        {
            var received = _api.avcodec_receive_frame(context, _decoded);
            if (received == _api.ErrorAgain || received == FFmpegApi.ErrorEof) return picture;
            if (received < 0)
            {
                error = true;
                return picture;
            }
            try
            {
                picture = Convert(picture ?? reuse) ?? picture;
                if (picture is null) error = true;
            }
            finally
            {
                _api.av_frame_unref(_decoded);
            }
        }
    }

    /// <summary>The decoded picture in BGRA, written straight into the frame's pinned pixels.</summary>
    private CameraFrame? Convert(CameraFrame? reuse)
    {
        var source = (byte*)_decoded;
        var width = *(int*)(source + FrameWidth);
        var height = *(int*)(source + FrameHeight);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;
        var target = reuse is { } r && r.Width == width && r.Height == height ? r : new CameraFrame(width, height);
        var converted = (byte*)_converted;
        fixed (byte* pixels = target.Pixels)
        {
            _api.av_frame_unref(converted);
            *(int*)(converted + FrameWidth) = width;
            *(int*)(converted + FrameHeight) = height;
            *(int*)(converted + FrameFormat) = _api.PixelFormatBgra;
            *(byte**)(converted + FrameData) = pixels;
            *(int*)(converted + FrameLinesize) = target.Stride;
            try
            {
                // Reads the picture's colour space and range from the frame itself (BT.709 limited, JPEG full).
                if (_api.sws_scale_frame(_scaler, converted, source) < 0) return null;
                var written = *(byte**)(converted + FrameData);
                if (written != pixels)
                {
                    // The scaler chose to allocate the output itself: copy it over.
                    var linesize = *(int*)(converted + FrameLinesize);
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(written + (long)y * linesize, pixels + (long)y * target.Stride, target.Stride, target.Stride);
                }
            }
            finally
            {
                if (*(byte**)(converted + FrameData) == pixels) *(byte**)(converted + FrameData) = null;
                _api.av_frame_unref(converted);
            }
        }
        return target;
    }
}
