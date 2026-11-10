using System.IO;
using System.Numerics;
using BambuBar.Core;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using SharpDX.Direct3D11;

namespace BambuBar.Model3D;

/// <summary>
/// One printer model as HelixToolkit scene nodes, with the Mac's behaviour (PrinterSceneView): the print grows slice
/// by slice (<c>Part_00…</c>) in the active filament's colour, the AMS spools take their filaments' colours and hide
/// when empty, the chamber light switches the lamp's glow and a warm spot, the bed (CoreXY, <c>BedCarriage</c>) drops
/// or the gantry (bed slingers, <c>Gantry</c>) rises with the print, and <see cref="Pose"/> moves the toolhead (and a
/// bed slinger's bed) along its loop. Glass and smoked panels get the Mac's own see-through materials. The model is
/// centred on a pivot that <see cref="Orient"/> turns, under fixed studio lights.
/// </summary>
internal sealed class PrinterScene
{
    public const double RestYaw = 0.42;
    public const double RestPitch = 0.16;
    public const double MinPitch = -0.15;
    public const double MaxPitch = 0.7;

    /// <summary>Bambu-ish green, the part's colour when the active filament is unknown (PrinterArt's accent).</summary>
    private static readonly Color4 Accent = new(0.13f, 0.80f, 0.42f, 1);

    public PrinterShape Shape { get; }
    /// <summary>Lights, dome and the turning model: what the viewport shows.</summary>
    public GroupNode Root { get; } = new() { Name = "Studio" };
    /// <summary>Half the model's largest extent, padded as on the Mac, for framing the camera.</summary>
    public float Radius { get; }
    public bool HasLamp => _lampAt is not null;

    private readonly GroupNode _pivot = new() { Name = "Pivot" };
    private readonly List<SceneNode> _parts = [];
    private readonly List<(SceneNode Root, PBRMaterialCore Filament)> _spools = [];
    private readonly SceneNode? _ams;
    private readonly PBRMaterialCore _part;
    private readonly List<PBRMaterialCore> _lampGlass = [];
    private readonly SpotLightNode _lamp = new()
    {
        Name = "ChamberLight", Color = new Color4(0, 0, 0, 1), InnerAngle = 50, OuterAngle = 120, FallOff = 1.5f,
        Range = 0.6f, Attenuation = new Vector3(1, 0, 9),
    };
    /// <summary>The lamp's warm fill, a little below it inside the chamber.</summary>
    private readonly PointLightNode _fill = new()
    {
        Name = "ChamberFill", Color = new Color4(0, 0, 0, 1), Range = 0.45f, Attenuation = new Vector3(1, 0, 14),
    };
    /// <summary>Where the lamp hangs, in the pivot's space (it turns with the model).</summary>
    private readonly Vector3? _lampAt;
    private readonly Tracked? _carriage, _gantry, _bed, _head;
    private readonly float _partHeight;

    private double _shown = -1;
    private Color4? _partColor;
    private bool? _lit;
    private string _spoolKey = "\0";

    /// <summary>A node moved by offsets given in the model's space (its parent may sit elsewhere).</summary>
    private sealed class Tracked(SceneNode node, Matrix4x4 rest, Matrix4x4 modelToParent)
    {
        private Vector3 _offset;

        public void Offset(Vector3 model)
        {
            if (model == _offset) return;
            _offset = model;
            var local = rest;
            local.Translation += Vector3.TransformNormal(model, modelToParent);
            node.ModelMatrix = local;
        }
    }

    private static readonly Dictionary<PrinterShape, Gltf?> Models = [];
    private static readonly Dictionary<(PrinterShape, int, int), MeshGeometry3D> Geometry = [];

    /// <summary>The shape's glTF from <c>Assets\Printers</c>, read once; null (logged) when missing or unreadable.</summary>
    public static Gltf? Model(PrinterShape shape)
    {
        if (Models.TryGetValue(shape, out var cached)) return cached;
        Gltf? model = null;
        var path = Assets.PathOf("Printers", shape.AssetName() + ".glb");
        try
        {
            model = Gltf.Load(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                      or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            Renderer3D.Write($"Can't read {path}: {e.GetType().Name}: {e.Message}");
        }
        Models[shape] = model;
        return model;
    }

    public PrinterScene(PrinterShape shape, Gltf model)
    {
        Shape = shape;
        var bounds = model.SceneBounds() ?? (Vector3.Zero, Vector3.One);
        var centre = (bounds.Min + bounds.Max) / 2;
        var size = bounds.Max - bounds.Min;
        Radius = Math.Max(size.X, Math.Max(size.Y, size.Z)) * 0.62f;

        var content = new GroupNode { Name = "Model", ModelMatrix = Matrix4x4.CreateTranslation(-centre) };
        _pivot.AddChildNode(content);
        Studio.Light(Root, _lamp, _fill);
        Root.AddChildNode(_pivot);

        _part = Pbr(Accent, roughness: 0.42f);
        var glass = Pbr(new Color4(0.08f, 0.08f, 0.08f, 0.16f), roughness: 0.05f);
        var smoke = Pbr(new Color4(0.02f, 0.02f, 0.02f, 0.55f), roughness: 0.08f);
        var materials = model.Materials.Select(Convert).ToList();
        var lampMaterials = new Dictionary<int, PBRMaterialCore>();

        // The slices and the spools, found by name, so their geometry takes their own materials.
        var partIndex = new HashSet<int>();
        for (var i = 0; i < 64; i++)
        {
            if (model.Find($"Part_{i:00}") is { } part) partIndex.Add(part.Index);
        }
        var filaments = new Dictionary<int, PBRMaterialCore>();
        var spoolNodes = new List<(int Spool, PBRMaterialCore Material)>();
        for (var i = 0; i < 9; i++)
        {
            if (model.Find($"Spool_{i}") is not { } spool) continue;
            var filament = Under(model, spool.Index).FirstOrDefault(n => n.Name == $"Filament_{i}");
            if (filament is null) continue;
            var material = Pbr(new Color4(0.86f, 0.86f, 0.86f, 1), roughness: 0.35f);
            filaments[filament.Index] = material;
            spoolNodes.Add((spool.Index, material));
        }

        var made = new Dictionary<int, GroupNode>();
        void Build(Gltf.Node node, GroupNodeBase parent, PBRMaterialCore? inherited)
        {
            var group = new GroupNode { Name = node.Name, ModelMatrix = node.Local };
            made[node.Index] = group;
            parent.AddChildNode(group);
            var forced = partIndex.Contains(node.Index) ? _part : filaments.TryGetValue(node.Index, out var f) ? f : inherited;
            if (node.Mesh >= 0)
            {
                var primitives = model.Meshes[node.Mesh].Primitives;
                for (var p = 0; p < primitives.Count; p++)
                {
                    var primitive = primitives[p];
                    var source = primitive.Material >= 0 ? model.Materials[primitive.Material] : null;
                    var name = source?.Name ?? "";
                    PBRMaterialCore material;
                    var transparent = false;
                    if (forced is not null) material = forced;
                    else if (name.StartsWith("BB_Glass", StringComparison.Ordinal)) (material, transparent) = (glass, true);
                    else if (name.StartsWith("BB_Smoke", StringComparison.Ordinal)) (material, transparent) = (smoke, true);
                    else if (name.StartsWith("BB_Lamp", StringComparison.Ordinal))
                    {
                        // Its own material, so the glow can be switched.
                        if (!lampMaterials.TryGetValue(primitive.Material, out var lamp))
                        {
                            lamp = lampMaterials[primitive.Material] = Convert(source!, primitive.Material);
                            _lampGlass.Add(lamp);
                        }
                        material = lamp;
                    }
                    else
                    {
                        material = primitive.Material >= 0 ? materials[primitive.Material] : Pbr(new Color4(0.8f, 0.8f, 0.8f, 1), 0.5f);
                        transparent = source?.Alpha == Gltf.AlphaMode.Blend;
                    }
                    group.AddChildNode(new MeshNode
                    {
                        Name = $"{node.Name}#{p}", Geometry = GeometryOf(shape, model, node.Mesh, p), Material = material, IsTransparent = transparent,
                        // Every material in the models is double-sided (thin panels, open shells).
                        CullMode = source is { DoubleSided: false } ? CullMode.Back : CullMode.None,
                        IsHitTestVisible = false,
                    });
                }
            }
            foreach (var child in node.Children) Build(model.Nodes[child], group, forced);
        }
        foreach (var root in model.Roots) Build(model.Nodes[root], content, null);

        _parts.AddRange(Enumerable.Range(0, 64).Select(i => model.Find($"Part_{i:00}")).OfType<Gltf.Node>().Select(n => (SceneNode)made[n.Index]));
        if (_parts.Count > 0 && model.Bounds(model.Find("Part_00")!.Index) is { } first
            && model.Bounds(model.Find($"Part_{_parts.Count - 1:00}")!.Index) is { } last)
            _partHeight = last.Max.Y - first.Min.Y;
        foreach (var (spool, material) in spoolNodes) _spools.Add((made[spool], material));
        _ams = model.Find("AMS") is { } ams ? made[ams.Index] : null;

        Tracked? Track(string name)
        {
            if (model.Find(name) is not { } node) return null;
            var parentWorld = node.Parent >= 0 ? model.World(node.Parent) : Matrix4x4.Identity;
            parentWorld.Translation = Vector3.Zero;
            Matrix4x4.Invert(parentWorld, out var modelToParent);
            return new Tracked(made[node.Index], node.Local, modelToParent);
        }
        _carriage = Track("BedCarriage");
        _gantry = Track("Gantry");
        _bed = Track("Bed");
        _head = Track("Toolhead");

        if (model.Find("Lamp") is { } lampNode) _lampAt = model.World(lampNode.Index).Translation - centre;

        // A soft shadow where the printer meets the desk, turning with it.
        var footprint = new Vector2(size.X, size.Z) * 1.2f;
        _pivot.AddChildNode(new MeshNode
        {
            Name = "Shadow", Geometry = Studio.FloorQuad(bounds.Min.Y - centre.Y + 0.001f, footprint.X, footprint.Y),
            Material = new DiffuseMaterialCore { DiffuseColor = new Color4(1, 1, 1, 1), DiffuseMap = Studio.ShadowTexture },
            IsTransparent = true, CullMode = CullMode.None, IsHitTestVisible = false, RenderOrder = 0,
        });
        Orient(RestYaw, RestPitch);
    }

    private static IEnumerable<Gltf.Node> Under(Gltf model, int index)
    {
        foreach (var child in model.Nodes[index].Children)
        {
            yield return model.Nodes[child];
            foreach (var deeper in Under(model, child)) yield return deeper;
        }
    }

    /// <summary>Vertex data is shared by every view of the shape (Helix keeps one GPU copy per geometry).</summary>
    private static MeshGeometry3D GeometryOf(PrinterShape shape, Gltf model, int mesh, int primitive)
    {
        var key = (shape, mesh, primitive);
        if (Geometry.TryGetValue(key, out var cached)) return cached;
        var p = model.Meshes[mesh].Primitives[primitive];
        var geometry = new MeshGeometry3D
        {
            Positions = new Vector3Collection(p.Positions),
            Indices = new IntCollection(p.Indices),
        };
        if (p.Normals is not null) geometry.Normals = new Vector3Collection(p.Normals);
        else geometry.UpdateNormals();
        if (p.TexCoords is not null) geometry.TextureCoordinates = new Vector2Collection(p.TexCoords);
        Geometry[key] = geometry;
        return geometry;
    }

    /// <summary>A PBR material lit by the studio dome as well as the lights.</summary>
    private static PBRMaterialCore Pbr(Color4 albedo, float roughness, float metallic = 0, float clearcoat = 0) => new()
    {
        AlbedoColor = albedo, RoughnessFactor = roughness, MetallicFactor = metallic, ReflectanceFactor = 0.5f,
        ClearCoatStrength = clearcoat, ClearCoatRoughness = 0.1f, AmbientOcclusionFactor = 1,
        RenderEnvironmentMap = true, IrradianceMap = Studio.Dome, RenderIrradianceMap = true, RenderShadowMap = false,
    };

    /// <summary>A glTF material: colours from linear light to the display's encoding, emission capped at full.</summary>
    private static PBRMaterialCore Convert(Gltf.Material m, int index)
    {
        var material = Pbr(Studio.Srgb(new Vector3(m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z), m.BaseColor.W), m.Roughness, m.Metallic, m.Clearcoat);
        material.ClearCoatRoughness = m.ClearcoatRoughness;
        if (m.Emissive != Vector3.Zero) material.EmissiveColor = Studio.Srgb(Vector3.Min(Vector3.One, m.Emissive * Math.Min(m.EmissiveStrength, 1.6f)));
        material.Name = m.Name;
        return material;
    }

    // MARK: - State

    /// <summary>The printer's state: slices, colours, spools, light, and where the bed or gantry stands. True when
    /// anything changed (a frame to draw).</summary>
    public bool Apply(PrinterArt.Scene scene)
    {
        var changed = false;
        var shown = PrinterArt.ShownFraction(scene.State, scene.Progress);
        if (shown != _shown)
        {
            changed = true;
            _shown = shown;
            var visible = (int)Math.Ceiling(shown * _parts.Count - 1e-9);
            for (var i = 0; i < _parts.Count; i++) _parts[i].Visible = i < visible;
            // The bed (CoreXY) drops, or the gantry (bed slinger) rises, by what has been printed.
            var rest = _partHeight * (float)(1 - shown);
            _carriage?.Offset(new Vector3(0, rest, 0));
            _gantry?.Offset(new Vector3(0, -rest, 0));
        }

        var part = ColorOf(scene.Filament) ?? Accent;
        if (part != _partColor)
        {
            changed = true;
            _partColor = part;
            _part.AlbedoColor = part;
        }

        var spoolKey = string.Join(',', scene.Spools);
        if (spoolKey != _spoolKey)
        {
            changed = true;
            _spoolKey = spoolKey;
            if (_ams is not null) _ams.Visible = scene.Spools.Count > 0;
            for (var i = 0; i < _spools.Count; i++)
            {
                var color = i < scene.Spools.Count ? ColorOf(scene.Spools[i]) : null;
                _spools[i].Root.Visible = color is not null;
                if (color is { } c) _spools[i].Filament.AlbedoColor = c;
            }
        }

        var lit = scene.LightOn && !scene.Dimmed;
        if (lit != _lit)
        {
            changed = true;
            _lit = lit;
            _lamp.Color = lit && HasLamp ? Studio.LampLight : new Color4(0, 0, 0, 1);
            _fill.Color = lit && HasLamp ? Studio.ChamberFill : new Color4(0, 0, 0, 1);
            foreach (var glow in _lampGlass) glow.EmissiveColor = lit ? Studio.LampGlow : new Color4(0, 0, 0, 1);
        }
        return changed;
    }

    /// <summary>A printer colour (<c>RRGGBB[AA]</c>, sRGB) as an opaque albedo; null for none.</summary>
    private static Color4? ColorOf(string? hex) =>
        PrinterArt.Color(hex) is { } c ? new Color4((float)c.R, (float)c.G, (float)c.B, 1) : (Color4?)null;

    /// <summary>Turns the model: <paramref name="yaw"/> about the vertical, then <paramref name="pitch"/> towards the
    /// viewer (radians). The lamp turns with it.</summary>
    public void Orient(double yaw, double pitch)
    {
        var turn = Matrix4x4.CreateRotationY((float)yaw) * Matrix4x4.CreateRotationX((float)pitch);
        _pivot.ModelMatrix = turn;
        if (_lampAt is { } at)
        {
            _lamp.Position = Vector3.Transform(at, turn);
            _lamp.Direction = Vector3.TransformNormal(-Vector3.UnitY, turn);
            // Mid-chamber and a little forward, so the walls, bed and part facing the viewer warm up.
            _fill.Position = Vector3.Transform(at + new Vector3(0, -0.12f, 0.06f), turn);
        }
    }

    /// <summary>The toolhead (and a bed slinger's bed) at <paramref name="time"/> seconds into its loop, scaled by
    /// <paramref name="amount"/> (0 parks them).</summary>
    public void Pose(double time, double amount)
    {
        var (head, bed) = Motion(time);
        _head?.Offset(head * (float)amount);
        _bed?.Offset(bed * (float)amount);
    }

    /// <summary>The Mac's 3 s loop: the toolhead traces the print's outline (2.8 × 1.8 cm), the bed slides under it.</summary>
    public static (Vector3 Head, Vector3 Bed) Motion(double time)
    {
        const float x = 0.028f, z = 0.018f, b = 0.022f;
        (double At, Vector3 To)[] head =
        [
            (0, Vector3.Zero), (0.7, new(x, 0, 0)), (1.15, new(x, 0, z)), (2.05, new(-x, 0, z)), (2.5, new(-x, 0, 0)), (3.0, Vector3.Zero),
        ];
        (double At, Vector3 To)[] slide = [(0, Vector3.Zero), (1.1, new(0, 0, b)), (2.5, new(0, 0, -b)), (3.1, Vector3.Zero)];
        return (Along(head, time), Along(slide, time));
    }

    private static Vector3 Along((double At, Vector3 To)[] keys, double time)
    {
        var t = time % keys[^1].At;
        for (var i = 1; i < keys.Length; i++)
        {
            if (t > keys[i].At) continue;
            var f = (t - keys[i - 1].At) / (keys[i].At - keys[i - 1].At);
            var eased = (float)(f * f * (3 - 2 * f));
            return Vector3.Lerp(keys[i - 1].To, keys[i].To, eased);
        }
        return keys[^1].To;
    }
}
