using System.Buffers.Binary;
using System.IO.Compression;

namespace BambuBar.Core.Imaging;

/// <summary>An RGBA image, 8 bits per channel, straight (not premultiplied) alpha, rows top to bottom.</summary>
public sealed class Rgba(int width, int height, byte[]? pixels = null)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels ?? new byte[width * height * 4];

    public int Offset(int x, int y) => (y * Width + x) * 4;
}

/// <summary>
/// Just enough PNG for the printer art: reads 8-bit RGBA and RGB, non-interlaced (every file in shared/Printers), and
/// writes 8-bit RGBA. Built on the BCL's zlib, so it runs the same on Windows, macOS and Linux.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static Rgba Read(string path) => Decode(File.ReadAllBytes(path));

    public static Rgba Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG");
        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    var depth = body[8];
                    var color = body[9];
                    var interlace = body[12];
                    if (depth != 8 || interlace != 0 || color is not (2 or 6))
                        throw new InvalidDataException($"Unsupported PNG (depth {depth}, colour type {color}, interlace {interlace})");
                    channels = color == 6 ? 4 : 3;
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    pos = data.Length;
                    continue;
            }
            pos += 12 + length;
        }
        if (width == 0 || channels == 0) throw new InvalidDataException("PNG without a header");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        inflate.ReadExactly(raw);

        var image = new Rgba(width, height);
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(current);
            Unfilter(filter, current, previous, channels);
            for (var x = 0; x < width; x++)
            {
                var o = image.Offset(x, y);
                image.Pixels[o] = current[x * channels];
                image.Pixels[o + 1] = current[x * channels + 1];
                image.Pixels[o + 2] = current[x * channels + 2];
                image.Pixels[o + 3] = channels == 4 ? current[x * channels + 3] : (byte)255;
            }
            (previous, current) = (current, previous);
        }
        return image;
    }

    private static void Unfilter(byte filter, byte[] row, byte[] above, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + a),
                2 => (byte)(row[i] + b),
                3 => (byte)(row[i] + (a + b) / 2),
                4 => (byte)(row[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException($"PNG filter {filter}"),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] Encode(Rgba image)
    {
        var stride = image.Width * 4;
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            for (var y = 0; y < image.Height; y++)
            {
                // Filter 1 (Sub): small, fast, and plenty for renders with flat backgrounds.
                row[0] = 1;
                var src = image.Pixels.AsSpan(y * stride, stride);
                for (var i = 0; i < stride; i++) row[i + 1] = (byte)(src[i] - (i >= 4 ? src[i - 4] : 0));
                deflate.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        var crc = Crc32(typeBytes, body);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] body)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in body) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
