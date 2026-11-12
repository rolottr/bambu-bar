using System.Threading.Channels;

namespace BambuBar.Core.Camera.Decoding;

/// <summary>
/// Where a <see cref="App.CameraFeed"/> sends what it wants shown. Calls come from the feed's thread (the UI thread);
/// pictures and failures go back there.
/// </summary>
public interface ICameraPipeline : IDisposable
{
    /// <summary>The next access unit of the stream, to decode and show.</summary>
    void Decode(VideoFrame frame);

    /// <summary>A JPEG still to decode and show.</summary>
    void DecodeJpeg(byte[] jpeg);

    /// <summary>Starts afresh from <paramref name="frames"/>, which begin with a keyframe: all are decoded, the last
    /// one shown (a viewer came back on screen mid-stream).</summary>
    void CatchUp(IReadOnlyList<VideoFrame> frames);

    /// <summary>The stream broke off: wait for a keyframe before showing anything new.</summary>
    void Reset();

    /// <summary>The feed no longer shows <paramref name="frame"/>; its pixels may be reused.</summary>
    void Release(CameraFrame frame);
}

/// <summary>
/// Decodes off the UI thread, one item at a time and in order, on a worker of its own, and hands on only the newest
/// picture: a slow UI thread gets fewer, never older, pictures. A decoder that falls behind (more than
/// <see cref="MaxBacklog"/> video frames waiting) skips to the next keyframe rather than lag further; a JPEG still is
/// skipped when a newer one waits. Pixel buffers are reused, three at most at a time (decoding, waiting, shown).
/// The decoder is made on the worker, on first use; if it can't be (FFmpeg missing, a codec it lacks), the failure is
/// reported once and the pipeline stops decoding.
/// </summary>
public sealed class DecodePipeline : ICameraPipeline
{
    public const int MaxBacklog = 15;
    private const int PoolSize = 3;

    private abstract record Work
    {
        public sealed record Video(VideoFrame Frame) : Work;
        public sealed record Jpeg(byte[] Data) : Work;
        public sealed record CatchUp(IReadOnlyList<VideoFrame> Frames) : Work;
        public sealed record Reset : Work;
    }

    private readonly Func<ICameraDecoder> _makeDecoder;
    private readonly Action<CameraFrame> _onFrame;
    private readonly Action<string> _onError;
    private readonly SynchronizationContext? _context;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly List<CameraFrame> _pool = [];
    private int _videoQueued;
    private int _jpegQueued;
    private CameraFrame? _pending;
    private bool _posted;
    private volatile bool _disposed;
    // Worker only.
    private ICameraDecoder? _decoder;
    private bool _failed;
    private bool _skipping;

    /// <param name="onFrame">Gets each new picture on the context the pipeline was made on (or the worker, without one).
    /// The picture is the receiver's until it hands it back with <see cref="Release"/>.</param>
    /// <param name="onError">Gets the reason, for the person, when the stream can't be decoded at all.</param>
    public DecodePipeline(Func<ICameraDecoder> makeDecoder, Action<CameraFrame> onFrame, Action<string> onError)
    {
        _makeDecoder = makeDecoder;
        _onFrame = onFrame;
        _onError = onError;
        _context = SynchronizationContext.Current;
        _ = Task.Run(RunAsync);
    }

    /// <summary>The real thing: FFmpeg.</summary>
    public static ICameraPipeline Create(Action<CameraFrame> onFrame, Action<string> onError) =>
        new DecodePipeline(() => new FFmpegDecoder(), onFrame, onError);

    public void Decode(VideoFrame frame)
    {
        Interlocked.Increment(ref _videoQueued);
        Post(new Work.Video(frame));
    }

    public void DecodeJpeg(byte[] jpeg)
    {
        Interlocked.Increment(ref _jpegQueued);
        Post(new Work.Jpeg(jpeg));
    }

    public void CatchUp(IReadOnlyList<VideoFrame> frames)
    {
        if (frames.Count > 0) Post(new Work.CatchUp([.. frames]));
    }

    public void Reset() => Post(new Work.Reset());

    public void Release(CameraFrame frame)
    {
        lock (_gate)
        {
            if (_pool.Count < PoolSize && !_pool.Contains(frame)) _pool.Add(frame);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _work.Writer.TryComplete();
        lock (_gate)
        {
            _pending = null;
            _pool.Clear();
        }
    }

    private void Post(Work work)
    {
        if (!_disposed) _work.Writer.TryWrite(work);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_disposed) break;
                Handle(work);
            }
        }
        finally
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }

    private void Handle(Work work)
    {
        switch (work)
        {
            case Work.Video video:
            {
                var backlog = Interlocked.Decrement(ref _videoQueued);
                if (_failed) return;
                if (backlog > MaxBacklog && !_skipping)
                {
                    _skipping = true;
                    Decoder()?.Flush();
                }
                if (_skipping)
                {
                    if (!video.Frame.IsKeyframe || backlog > MaxBacklog) return;
                    _skipping = false;
                }
                Show(DecodeInto(video.Frame.Format.Width, video.Frame.Format.Height, (d, reuse) => d.Decode(video.Frame, reuse)));
                break;
            }
            case Work.Jpeg jpeg:
            {
                // A newer still is waiting: this one would be replaced before anyone saw it.
                if (Interlocked.Decrement(ref _jpegQueued) > 0 || _failed) return;
                Show(DecodeInto(0, 0, (d, reuse) => d.DecodeJpeg(jpeg.Data, reuse)));
                break;
            }
            case Work.CatchUp catchUp:
            {
                if (_failed) return;
                Decoder()?.Flush();
                _skipping = false;
                CameraFrame? last = null;
                foreach (var frame in catchUp.Frames)
                {
                    if (DecodeInto(frame.Format.Width, frame.Format.Height, (d, reuse) => d.Decode(frame, reuse)) is not { } picture) continue;
                    if (last is not null && !ReferenceEquals(last, picture)) Release(last);
                    last = picture;
                }
                Show(last);
                break;
            }
            case Work.Reset:
                Decoder()?.Flush();
                _skipping = false;
                break;
        }
    }

    private ICameraDecoder? Decoder()
    {
        if (_decoder is not null || _failed) return _decoder;
        try
        {
            _decoder = _makeDecoder();
        }
        catch (CameraDecodeException e)
        {
            Fail(e.Message);
        }
        return _decoder;
    }

    /// <summary>Decodes into a pooled buffer of the given size (any size: 0), giving the buffer back when the decoder
    /// didn't use it.</summary>
    private CameraFrame? DecodeInto(int width, int height, Func<ICameraDecoder, CameraFrame?, CameraFrame?> decode)
    {
        if (Decoder() is not { } decoder) return null;
        var reuse = Rent(width, height);
        CameraFrame? picture = null;
        try
        {
            picture = decode(decoder, reuse);
        }
        catch (CameraDecodeException e)
        {
            Fail(e.Message);
        }
        if (reuse is not null && !ReferenceEquals(reuse, picture)) Release(reuse);
        return picture;
    }

    private void Fail(string message)
    {
        if (_failed) return;
        _failed = true;
        Deliver(() => _onError(message));
    }

    /// <summary>A free buffer, of the given size when one is (the decoder makes its own otherwise).</summary>
    private CameraFrame? Rent(int width, int height)
    {
        lock (_gate)
        {
            var i = _pool.FindIndex(f => width == 0 || (f.Width == width && f.Height == height));
            if (i < 0) return null;
            var frame = _pool[i];
            _pool.RemoveAt(i);
            return frame;
        }
    }

    /// <summary>Makes <paramref name="frame"/> the one waiting for the UI, replacing (and recycling) any that the UI
    /// hasn't taken yet.</summary>
    private void Show(CameraFrame? frame)
    {
        if (frame is null || _disposed) return;
        bool post;
        CameraFrame? stale;
        lock (_gate)
        {
            stale = _pending;
            _pending = frame;
            post = !_posted;
            _posted = true;
        }
        if (stale is not null && !ReferenceEquals(stale, frame)) Release(stale);
        if (post) Deliver(TakePending);
    }

    private void TakePending()
    {
        CameraFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
            _posted = false;
        }
        if (frame is not null && !_disposed) _onFrame(frame);
    }

    private void Deliver(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => { if (!_disposed) action(); }, null);
    }
}
