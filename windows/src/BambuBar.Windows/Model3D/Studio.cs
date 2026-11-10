using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model.Scene;

namespace BambuBar.Model3D;

/// <summary>
/// The studio the printer stands in, as on the Mac (PrinterSceneView.studio): a dome for reflections and soft fill
/// (dark floor, bright overhead), a warm key light from the front left, a cool fill from the right, a rim light from
/// behind, and a soft contact shadow under the printer. HelixToolkit lights and shades in the display's gamma (no
/// tone mapping, no sRGB target), so colours given in linear light (glTF) are encoded to sRGB first, and the
/// intensities are the Mac's ratios (950 : 280 : 650) rescaled to that.
/// </summary>
internal static class Studio
{
    /// <summary>The Mac's camera: a long lens, so the printer looks like a product shot.</summary>
    public const double FieldOfView = 17;

    /// <summary>Lights in world space (they don't turn with the model): (direction the light travels, colour).</summary>
    public static readonly (Vector3 Direction, Color4 Color)[] Lights =
    [
        (-new Vector3(-0.6f, 1.0f, 0.9f), new Color4(1.00f * 0.95f, 0.97f * 0.95f, 0.93f * 0.95f, 1)),
        (-new Vector3(0.8f, 0.25f, 0.5f), new Color4(0.85f * 0.28f, 0.90f * 0.28f, 1.00f * 0.28f, 1)),
        (-new Vector3(0.4f, 0.6f, -0.9f), new Color4(0.80f * 0.62f, 0.88f * 0.62f, 1.00f * 0.62f, 1)),
    ];

    /// <summary>The chamber light: warm, wide, from the ceiling (the Mac's spot, 50° inner, 120° outer). Far brighter
    /// than the Mac's numbers: without the Mac's tone mapping and exposure, 0.55 barely changed the picture.</summary>
    public static readonly Color4 LampLight = new(1.0f * 2.4f, 0.93f * 2.4f, 0.82f * 2.4f, 1);

    /// <summary>The warm light the lamp fills the chamber with (walls, bed, part), so a lit printer reads as lit.</summary>
    public static readonly Color4 ChamberFill = new(1.0f * 0.9f, 0.82f * 0.9f, 0.62f * 0.9f, 1);

    /// <summary>The lamp's glow when on (the Mac's emission colour).</summary>
    public static readonly Color4 LampGlow = new(1, 0.95f, 0.86f, 1);

    public static Color4 Srgb(Vector3 linear, float alpha = 1) => new(Encode(linear.X), Encode(linear.Y), Encode(linear.Z), alpha);

    private static float Encode(float c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
    }

    private static TextureModel? _dome;
    private static TextureModel? _shadow;

    /// <summary>The dome as a cube map, for reflections (and, as the irradiance map, the soft light from all around).
    /// The Mac's gradient: black floor (0.02) to grey at the horizon (0.14) to near white overhead (0.95).
    /// Kept open: Helix reads it again (from the start) for every device it's loaded on.</summary>
    public static TextureModel Dome => _dome ??= new TextureModel(new MemoryStream(DomeCube(64), writable: false), autoCloseStream: false);

    /// <summary>A soft black ellipse whose alpha is what the blend squares (Helix's alpha blend writes a²).</summary>
    public static TextureModel ShadowTexture => _shadow ??= Shadow(64);

    /// <summary>Brightness of the dome looking along <paramref name="y"/> (−1 down … 1 up).</summary>
    public static float DomeAt(float y)
    {
        var t = (MathF.Asin(Math.Clamp(y, -1, 1)) / MathF.PI) + 0.5f;
        return t < 0.55f ? 0.02f + (0.14f - 0.02f) * (t / 0.55f) : 0.14f + (0.95f - 0.14f) * ((t - 0.55f) / 0.45f);
    }

    /// <summary>A DirectX 9-style DDS cube map (A8R8G8B8, one mip; Helix makes the rest): six faces in D3D order.</summary>
    public static byte[] DomeCube(int size)
    {
        const int header = 4 + 124;
        var faceBytes = size * size * 4;
        var dds = new byte[header + faceBytes * 6];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at), value);
        Put(0, 0x20534444); // "DDS "
        const int h = 4;
        Put(h + 0, 124); // header size
        Put(h + 4, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000); // caps, height, width, pitch, pixel format
        Put(h + 8, (uint)size);
        Put(h + 12, (uint)size);
        Put(h + 16, (uint)(size * 4));
        Put(h + 24, 1); // mip levels
        Put(h + 72, 32); // pixel format: size
        Put(h + 76, 0x40 | 0x1); // RGB with alpha
        Put(h + 84, 32);
        Put(h + 88, 0x00FF0000);
        Put(h + 92, 0x0000FF00);
        Put(h + 96, 0x000000FF);
        Put(h + 100, 0xFF000000);
        Put(h + 104, 0x1000 | 0x8); // texture, complex
        Put(h + 108, 0x200 | 0x400 | 0x800 | 0x1000 | 0x2000 | 0x4000 | 0x8000); // cube map, all six faces
        for (var face = 0; face < 6; face++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    float u = 2 * (x + 0.5f) / size - 1, v = 2 * (y + 0.5f) / size - 1;
                    var dir = face switch
                    {
                        0 => new Vector3(1, -v, -u),
                        1 => new Vector3(-1, -v, u),
                        2 => new Vector3(u, 1, v),
                        3 => new Vector3(u, -1, -v),
                        4 => new Vector3(u, -v, 1),
                        _ => new Vector3(-u, -v, -1),
                    };
                    var g = (byte)Math.Clamp(MathF.Round(DomeAt(Vector3.Normalize(dir).Y) * 255), 0, 255);
                    var at = header + face * faceBytes + (y * size + x) * 4;
                    dds[at] = g; // B
                    dds[at + 1] = g; // G
                    dds[at + 2] = g; // R
                    dds[at + 3] = 255;
                }
            }
        }
        return dds;
    }

    private static TextureModel Shadow(int size)
    {
        var pixels = new Color4[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                var r = MathF.Sqrt(dx * dx + dy * dy);
                // The art's contact shadow: 45 % in the middle, 18 % halfway, gone at the rim.
                var a = r < 0.5f ? 0.45f + (0.18f - 0.45f) * (r / 0.5f) : r < 1 ? 0.18f * (1 - (r - 0.5f) / 0.5f) : 0;
                pixels[y * size + x] = new Color4(0, 0, 0, MathF.Sqrt(Math.Max(0, a)));
            }
        }
        return new TextureModel(pixels, size, size);
    }

    /// <summary>A flat quad on the floor (y = <paramref name="y"/>), <paramref name="width"/> × <paramref name="depth"/>,
    /// facing up, with texture coordinates for the shadow.</summary>
    public static MeshGeometry3D FloorQuad(float y, float width, float depth)
    {
        float w = width / 2, d = depth / 2;
        return new MeshGeometry3D
        {
            Positions = new Vector3Collection([new(-w, y, -d), new(w, y, -d), new(w, y, d), new(-w, y, d)]),
            Normals = new Vector3Collection([Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY]),
            TextureCoordinates = new Vector2Collection([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]),
            Indices = new IntCollection([0, 2, 1, 0, 3, 2]),
        };
    }

    /// <summary>The lights, the dome, and the lamp's spot (switched by <see cref="PrinterScene"/>).</summary>
    public static void Light(GroupNode root, SpotLightNode lamp, PointLightNode fill)
    {
        foreach (var (direction, color) in Lights) root.AddChildNode(new DirectionalLightNode { Direction = Vector3.Normalize(direction), Color = color });
        root.AddChildNode(new EnvironmentMapNode { Texture = Dome, SkipRendering = true });
        root.AddChildNode(lamp);
        root.AddChildNode(fill);
    }
}
