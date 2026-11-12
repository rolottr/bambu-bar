using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace BambuBar.Model3D;

/// <summary>
/// A glTF 2.0 binary (<c>.glb</c>) as the printer models use it (artwork/generate_printers.py, <c>export_glb</c>): the
/// default scene's node tree with names and transforms, triangle meshes (positions, normals, texture coordinates,
/// indices) and the PBR factors of their materials (base colour and alpha, metallic, roughness, emissive with its
/// strength, clearcoat, alpha mode, double-sided). No textures, skins, morph targets or animations: the models have none.
/// Our own reader rather than Assimp, whose native library has no ARM64 build for Windows. Pure .NET, no WPF, so it
/// reads the same on every platform.
/// </summary>
public sealed class Gltf
{
    public enum AlphaMode { Opaque, Mask, Blend }

    public sealed record Material(
        string Name, Vector4 BaseColor, float Metallic, float Roughness, Vector3 Emissive, float EmissiveStrength,
        AlphaMode Alpha, bool DoubleSided, float Clearcoat, float ClearcoatRoughness);

    public sealed class Primitive
    {
        public required Vector3[] Positions { get; init; }
        public Vector3[]? Normals { get; init; }
        public Vector2[]? TexCoords { get; init; }
        public required int[] Indices { get; init; }
        /// <summary>Index into <see cref="Materials"/>, or -1 for glTF's default material.</summary>
        public int Material { get; init; } = -1;
    }

    public sealed record Mesh(string Name, IReadOnlyList<Primitive> Primitives);

    public sealed class Node
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        /// <summary>Row-vector (System.Numerics) local transform: scale, then rotation, then translation.</summary>
        public Matrix4x4 Local { get; init; } = Matrix4x4.Identity;
        public int Mesh { get; init; } = -1;
        public int[] Children { get; init; } = [];
        public int Parent { get; internal set; } = -1;
    }

    public IReadOnlyList<Node> Nodes { get; private init; } = [];
    public IReadOnlyList<Mesh> Meshes { get; private init; } = [];
    public IReadOnlyList<Material> Materials { get; private init; } = [];
    /// <summary>The default scene's root nodes (the export also carries Blender's own scene, which is skipped).</summary>
    public IReadOnlyList<int> Roots { get; private init; } = [];

    public static Gltf Load(string path) => Parse(File.ReadAllBytes(path), Path.GetDirectoryName(path));

    /// <summary>Reads a <c>.glb</c>. <paramref name="folder"/> resolves buffers stored beside the file (the models
    /// embed theirs). Throws <see cref="InvalidDataException"/> on anything malformed or unsupported.</summary>
    public static Gltf Parse(byte[] glb, string? folder = null)
    {
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != 0x46546C67) throw new InvalidDataException("Not a glTF binary (no glTF magic).");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4));
        if (version != 2) throw new InvalidDataException($"glTF version {version}; only 2 is supported.");
        var length = (int)Math.Min(glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        ReadOnlyMemory<byte> json = default, bin = default;
        for (var at = 12; at + 8 <= length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(at));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > length) throw new InvalidDataException("A glTF chunk runs past the end of the file.");
            var data = glb.AsMemory(at + 8, size);
            if (type == 0x4E4F534A && json.IsEmpty) json = data; // JSON
            else if (type == 0x004E4942 && bin.IsEmpty) bin = data; // BIN\0
            at += 8 + size;
        }
        if (json.IsEmpty) throw new InvalidDataException("The glTF has no JSON chunk.");
        using var doc = JsonDocument.Parse(json);
        return new Reader(doc.RootElement, bin, folder).Read();
    }

    /// <summary>The node's transform relative to the scene (its own, then each parent's).</summary>
    public Matrix4x4 World(int node)
    {
        var m = Matrix4x4.Identity;
        for (var n = node; n >= 0; n = Nodes[n].Parent) m *= Nodes[n].Local;
        return m;
    }

    /// <summary>The first node called <paramref name="name"/> in the default scene, or null.</summary>
    public Node? Find(string name)
    {
        foreach (var root in Roots)
        {
            if (FindUnder(root, name) is { } found) return found;
        }
        return null;
    }

    private Node? FindUnder(int index, string name)
    {
        var node = Nodes[index];
        if (node.Name == name) return node;
        foreach (var child in node.Children)
        {
            if (FindUnder(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Every node of the default scene, parents before children.</summary>
    public IEnumerable<Node> SceneNodes()
    {
        var stack = new Stack<int>(Roots.Reverse());
        while (stack.Count > 0)
        {
            var node = Nodes[stack.Pop()];
            yield return node;
            for (var i = node.Children.Length - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    /// <summary>The box around the meshes of <paramref name="node"/> and everything under it, in the space of
    /// <paramref name="relativeTo"/> (scene space when null); null when there are no meshes.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds(int node, Matrix4x4? relativeTo = null)
    {
        var toSpace = Matrix4x4.Identity;
        if (relativeTo is { } space && Matrix4x4.Invert(space, out var inverse)) toSpace = inverse;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var any = false;
        var stack = new Stack<int>([node]);
        while (stack.Count > 0)
        {
            var n = Nodes[stack.Pop()];
            foreach (var child in n.Children) stack.Push(child);
            if (n.Mesh < 0) continue;
            var m = World(n.Index) * toSpace;
            foreach (var primitive in Meshes[n.Mesh].Primitives)
            {
                foreach (var p in primitive.Positions)
                {
                    var q = Vector3.Transform(p, m);
                    min = Vector3.Min(min, q);
                    max = Vector3.Max(max, q);
                    any = true;
                }
            }
        }
        return any ? (min, max) : null;
    }

    /// <summary>The box around the whole default scene.</summary>
    public (Vector3 Min, Vector3 Max)? SceneBounds()
    {
        (Vector3 Min, Vector3 Max)? all = null;
        foreach (var root in Roots)
        {
            if (Bounds(root) is not { } b) continue;
            all = all is { } a ? (Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max)) : b;
        }
        return all;
    }

    private sealed class Reader(JsonElement root, ReadOnlyMemory<byte> bin, string? folder)
    {
        private readonly Dictionary<int, ReadOnlyMemory<byte>> _buffers = [];

        public Gltf Read()
        {
            var materials = Array(root, "materials").Select(ReadMaterial).ToList();
            var meshes = Array(root, "meshes").Select(ReadMesh).ToList();
            var nodes = Array(root, "nodes").Select((e, i) => ReadNode(e, i, meshes.Count)).ToList();
            foreach (var node in nodes)
            {
                foreach (var child in node.Children)
                {
                    if (child < 0 || child >= nodes.Count || nodes[child].Parent >= 0 || child == node.Index)
                        throw new InvalidDataException($"Node {node.Index} has a bad child {child}.");
                    nodes[child].Parent = node.Index;
                }
            }
            var scenes = Array(root, "scenes").ToList();
            var sceneIndex = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
            List<int> roots;
            if (scenes.Count == 0) roots = nodes.Where(n => n.Parent < 0).Select(n => n.Index).ToList();
            else
            {
                if (sceneIndex < 0 || sceneIndex >= scenes.Count) throw new InvalidDataException($"No scene {sceneIndex}.");
                roots = Array(scenes[sceneIndex], "nodes").Select(e => e.GetInt32()).ToList();
                if (roots.Any(r => r < 0 || r >= nodes.Count || nodes[r].Parent >= 0)) throw new InvalidDataException("A scene root is not a root node.");
            }
            foreach (var material in meshes.SelectMany(m => m.Primitives).Select(p => p.Material))
            {
                if (material >= materials.Count) throw new InvalidDataException($"No material {material}.");
            }
            return new Gltf { Nodes = nodes, Meshes = meshes, Materials = materials, Roots = roots };
        }

        private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

        private static float Number(JsonElement e, string name, float fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

        private static float[] Floats(JsonElement e, string name, float[] fallback)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return fallback;
            var values = v.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return values.Length == fallback.Length ? values : throw new InvalidDataException($"\"{name}\" has {values.Length} values, not {fallback.Length}.");
        }

        private static string Name(JsonElement e, string fallback) =>
            e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? fallback : fallback;

        private static Material ReadMaterial(JsonElement e, int index)
        {
            var pbr = e.TryGetProperty("pbrMetallicRoughness", out var p) ? p : default;
            float[] color = pbr.ValueKind == JsonValueKind.Object ? Floats(pbr, "baseColorFactor", [1, 1, 1, 1]) : [1, 1, 1, 1];
            var metallic = pbr.ValueKind == JsonValueKind.Object ? Number(pbr, "metallicFactor", 1) : 1;
            var roughness = pbr.ValueKind == JsonValueKind.Object ? Number(pbr, "roughnessFactor", 1) : 1;
            var emissive = Floats(e, "emissiveFactor", [0, 0, 0]);
            float strength = 1, clearcoat = 0, clearcoatRoughness = 0;
            if (e.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                if (ext.TryGetProperty("KHR_materials_emissive_strength", out var es)) strength = Number(es, "emissiveStrength", 1);
                if (ext.TryGetProperty("KHR_materials_clearcoat", out var cc))
                {
                    clearcoat = Number(cc, "clearcoatFactor", 0);
                    clearcoatRoughness = Number(cc, "clearcoatRoughnessFactor", 0);
                }
            }
            var alpha = e.TryGetProperty("alphaMode", out var am) ? am.GetString() switch
            {
                "BLEND" => AlphaMode.Blend,
                "MASK" => AlphaMode.Mask,
                _ => AlphaMode.Opaque,
            } : AlphaMode.Opaque;
            var doubleSided = e.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True;
            return new Material(Name(e, $"Material{index}"), new Vector4(color[0], color[1], color[2], color[3]), metallic, roughness,
                new Vector3(emissive[0], emissive[1], emissive[2]), strength, alpha, doubleSided, clearcoat, clearcoatRoughness);
        }

        private Mesh ReadMesh(JsonElement e, int index)
        {
            var primitives = new List<Primitive>();
            foreach (var p in Array(e, "primitives"))
            {
                // Triangles only (mode 4, the default); the models have nothing else.
                if (p.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;
                if (p.TryGetProperty("targets", out _)) throw new InvalidDataException("Morph targets aren't supported.");
                var attributes = p.GetProperty("attributes");
                if (!attributes.TryGetProperty("POSITION", out var position)) continue;
                var positions = Vectors3(position.GetInt32());
                var normals = attributes.TryGetProperty("NORMAL", out var normal) ? Vectors3(normal.GetInt32()) : null;
                var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uv) ? Vectors2(uv.GetInt32()) : null;
                if (normals is not null && normals.Length != positions.Length) throw new InvalidDataException("Normals and positions differ in count.");
                if (uvs is not null && uvs.Length != positions.Length) throw new InvalidDataException("Texture coordinates and positions differ in count.");
                int[] indices;
                if (p.TryGetProperty("indices", out var ix)) indices = Integers(ix.GetInt32());
                else indices = Enumerable.Range(0, positions.Length).ToArray();
                if (indices.Length % 3 != 0) throw new InvalidDataException("A triangle list's index count isn't a multiple of 3.");
                if (indices.Any(i => (uint)i >= (uint)positions.Length)) throw new InvalidDataException("An index points past the vertices.");
                primitives.Add(new Primitive
                {
                    Positions = positions, Normals = normals, TexCoords = uvs, Indices = indices,
                    Material = p.TryGetProperty("material", out var m) ? m.GetInt32() : -1,
                });
            }
            return new Mesh(Name(e, $"Mesh{index}"), primitives);
        }

        private static Node ReadNode(JsonElement e, int index, int meshCount)
        {
            Matrix4x4 local;
            if (e.TryGetProperty("matrix", out _))
            {
                // Column-major for column vectors: read in order, it is the row-vector matrix System.Numerics uses.
                var m = Floats(e, "matrix", new float[16]);
                local = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            }
            else
            {
                var t = Floats(e, "translation", [0, 0, 0]);
                var r = Floats(e, "rotation", [0, 0, 0, 1]);
                var s = Floats(e, "scale", [1, 1, 1]);
                local = Matrix4x4.CreateScale(s[0], s[1], s[2])
                    * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3])))
                    * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
            }
            var mesh = e.TryGetProperty("mesh", out var me) ? me.GetInt32() : -1;
            if (mesh >= meshCount) throw new InvalidDataException($"Node {index} uses a missing mesh {mesh}.");
            return new Node
            {
                Index = index, Name = Name(e, $"Node{index}"), Local = local, Mesh = mesh,
                Children = Array(e, "children").Select(c => c.GetInt32()).ToArray(),
            };
        }

        // MARK: Accessors

        private (ReadOnlyMemory<byte> Data, int Stride, int Count, int Components, int ComponentType, bool Normalized) Accessor(int index, string type)
        {
            var accessors = root.GetProperty("accessors");
            if (index < 0 || index >= accessors.GetArrayLength()) throw new InvalidDataException($"No accessor {index}.");
            var a = accessors[index];
            if (a.TryGetProperty("sparse", out _)) throw new InvalidDataException("Sparse accessors aren't supported.");
            if (a.GetProperty("type").GetString() != type) throw new InvalidDataException($"Accessor {index} isn't {type}.");
            var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException(type) };
            var componentType = a.GetProperty("componentType").GetInt32();
            var size = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"Component type {componentType}.") };
            var count = a.GetProperty("count").GetInt32();
            var normalized = a.TryGetProperty("normalized", out var n) && n.ValueKind == JsonValueKind.True;
            if (!a.TryGetProperty("bufferView", out var bv)) return (new byte[count * components * size], components * size, count, components, componentType, normalized);
            var view = root.GetProperty("bufferViews")[bv.GetInt32()];
            var buffer = Buffer(view.GetProperty("buffer").GetInt32());
            var viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            var viewLength = view.GetProperty("byteLength").GetInt32();
            var stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : components * size;
            var offset = a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            if (viewOffset < 0 || viewLength < 0 || viewOffset + viewLength > buffer.Length) throw new InvalidDataException("A buffer view runs past its buffer.");
            if (count > 0 && offset + (long)stride * (count - 1) + components * size > viewLength) throw new InvalidDataException($"Accessor {index} runs past its buffer view.");
            return (buffer.Slice(viewOffset + offset, viewLength - offset), stride, count, components, componentType, normalized);
        }

        private ReadOnlyMemory<byte> Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var cached)) return cached;
            var b = root.GetProperty("buffers")[index];
            ReadOnlyMemory<byte> data;
            if (!b.TryGetProperty("uri", out var uri)) data = index == 0 ? bin : throw new InvalidDataException($"Buffer {index} has no data.");
            else
            {
                var text = uri.GetString() ?? "";
                if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    var comma = text.IndexOf(',', StringComparison.Ordinal);
                    data = Convert.FromBase64String(text[(comma + 1)..]);
                }
                else
                {
                    var file = Path.GetFullPath(Path.Combine(folder ?? ".", Uri.UnescapeDataString(text)));
                    data = File.ReadAllBytes(file);
                }
            }
            var length = b.GetProperty("byteLength").GetInt32();
            if (length > data.Length) throw new InvalidDataException($"Buffer {index} is shorter than it says.");
            _buffers[index] = data[..length];
            return _buffers[index];
        }

        private static float Component(ReadOnlySpan<byte> s, int type, bool normalized) => type switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
            5121 => normalized ? s[0] / 255f : s[0],
            5120 => normalized ? Math.Max((sbyte)s[0] / 127f, -1) : (sbyte)s[0],
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(s),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
            _ => throw new InvalidDataException($"Component type {type}."),
        };

        private float[] Floats(int accessor, string type)
        {
            var (data, stride, count, components, componentType, normalized) = Accessor(accessor, type);
            var size = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            var values = new float[count * components];
            var span = data.Span;
            for (var i = 0; i < count; i++)
            {
                for (var c = 0; c < components; c++) values[i * components + c] = Component(span[(i * stride + c * size)..], componentType, normalized);
            }
            return values;
        }

        private Vector3[] Vectors3(int accessor)
        {
            var f = Floats(accessor, "VEC3");
            var v = new Vector3[f.Length / 3];
            for (var i = 0; i < v.Length; i++) v[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
            return v;
        }

        private Vector2[] Vectors2(int accessor)
        {
            var f = Floats(accessor, "VEC2");
            var v = new Vector2[f.Length / 2];
            for (var i = 0; i < v.Length; i++) v[i] = new Vector2(f[i * 2], f[i * 2 + 1]);
            return v;
        }

        private int[] Integers(int accessor)
        {
            var (data, stride, count, _, componentType, _) = Accessor(accessor, "SCALAR");
            if (componentType is not (5121 or 5123 or 5125)) throw new InvalidDataException("Indices must be unsigned integers.");
            var span = data.Span;
            var values = new int[count];
            for (var i = 0; i < count; i++)
            {
                var s = span[(i * stride)..];
                values[i] = componentType switch
                {
                    5121 => s[0],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                    _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(s)),
                };
            }
            return values;
        }
    }
}
