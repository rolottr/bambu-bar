namespace BambuBar.Core.Camera.Decoding;

/// <summary>A decoded camera picture: BGRA, 8 bits per channel, opaque (alpha 255), rows top to bottom
/// <see cref="Stride"/> bytes apart. What the Windows app copies into its bitmap (WPF's Bgra32/Bgr32).</summary>
public sealed class CameraFrame
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    public CameraFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[Stride * height];
    }

    /// <summary>The pixel at (x, y) as (red, green, blue): for tests and the probe.</summary>
    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        var i = y * Stride + x * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>The picture as RGBA, for <see cref="Imaging.Png"/>.</summary>
    public Imaging.Rgba ToRgba()
    {
        var image = new Imaging.Rgba(Width, Height);
        for (var y = 0; y < Height; y++)
        {
            var row = y * Stride;
            for (var x = 0; x < Width; x++)
            {
                var source = row + x * 4;
                var target = image.Offset(x, y);
                image.Pixels[target] = Pixels[source + 2];
                image.Pixels[target + 1] = Pixels[source + 1];
                image.Pixels[target + 2] = Pixels[source];
                image.Pixels[target + 3] = 255;
            }
        }
        return image;
    }
}

/// <summary>
/// Turns a camera's compressed pictures into <see cref="CameraFrame"/>s: H.264/H.265 access units from the RTSPS
/// stream, JPEG stills from the P1/A1 stream. One stream at a time, in order; safe to call from any one thread at a time
/// (calls are serialized). <see cref="FFmpegDecoder"/> is the real one.
/// </summary>
public interface ICameraDecoder : IDisposable
{
    /// <summary>Decodes one access unit; returns the picture it completes, or null (none yet, or a damaged frame: the
    /// decoder then waits for the next keyframe). Writes into <paramref name="reuse"/> when it has the right size.</summary>
    /// <exception cref="CameraDecodeException">The stream's codec can't be decoded at all.</exception>
    CameraFrame? Decode(VideoFrame frame, CameraFrame? reuse = null);

    /// <summary>Decodes one JPEG still, or returns null when it's broken.</summary>
    CameraFrame? DecodeJpeg(ReadOnlySpan<byte> jpeg, CameraFrame? reuse = null);

    /// <summary>Forgets the stream so far; the next video frame decoded must be a keyframe.</summary>
    void Flush();
}
