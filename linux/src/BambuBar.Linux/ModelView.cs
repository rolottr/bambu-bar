using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using BambuBar.Model3D;
using Silk.NET.OpenGL;

namespace BambuBar.Linux;

/// <summary>Native GTK GL surface. Geometry is uploaded once; status changes only uniforms/visibility.
/// Reports and gestures request frames; print animation is capped at 15 fps while visible.</summary>
internal sealed class ModelView : IDisposable
{
    private static readonly ConcurrentDictionary<PrinterShape, Task<Gltf>> Models = new();
    public Gtk.GLArea Widget { get; } = Gtk.GLArea.New();
    private readonly PrinterSession _session;
    private readonly Func<Gtk.Widget, bool> _visible;
    private readonly Task<Gltf> _loading;
    private readonly List<Draw> _draws = [];
    private Draw[] _ordered = [];
    private Gltf.Node[] _nodes = [];
    private Matrix4x4[] _worlds = [];
    private Spool[] _spools = [];
    private float _partHeight;
    private GL? _gl;
    private uint _program;
    private int _mvp, _world, _color, _metal, _rough, _lamp;
    private Vector3 _center;
    private float _radius;
    private float _yaw = .42f, _pitch = .16f, _dragYaw, _dragPitch;
    private bool _disposed;
    private uint _tick;
    private long _lastFrame;
    private double _time;
    private string _key = "";
    private sealed record Draw(uint Vao, uint Vertices, uint Indices, int Count, int Node, Gltf.Material? Material, int Part, int Spool, bool Filament, bool Glass);
    [DllImport("libEGL.so.1")] private static extern nint eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    public ModelView(PrinterSession session, bool compact, Func<Gtk.Widget, bool> visible)
    {
        _session = session; _visible = visible;
        _loading = Models.GetOrAdd(session.Config.Model?.Shape ?? PrinterShape.Enclosed, shape => Task.Run(() => Gltf.Load(Assets.PathOf("Printers", shape.AssetName() + ".glb"))));
        Widget.SetSizeRequest(compact ? 120 : 210, compact ? 150 : 230); Widget.SetHasDepthBuffer(true); Widget.SetRequiredVersion(3, 3); Widget.SetUseEs(false); Widget.SetAutoRender(false);
        Widget.SetTooltipText(Loc.T("Drag to rotate"));
        Widget.OnRealize += (_, _) => { Widget.MakeCurrent(); if (Widget.GetError() is null) { _gl = GL.GetApi(eglGetProcAddress); Widget.QueueRender(); } };
        Widget.OnRender += (_, _) => { try { Render(); } catch (Exception) { Console.Error.WriteLine("3D rendering unavailable."); } return true; };
        Widget.OnResize += (_, _) => Widget.QueueRender();
        Widget.OnUnrealize += (_, _) => Release();
        Widget.OnUnmap += (_, _) => { if (_tick != 0) { Widget.RemoveTickCallback(_tick); _tick = 0; } };
        Widget.OnMap += (_, _) => GLib.Functions.IdleAdd(200, () => { if (!_disposed) { _key = ""; Update(); Widget.QueueRender(); } return false; });
        var drag = Gtk.GestureDrag.New(); drag.OnDragBegin += (_, _) => { _dragYaw = _yaw; _dragPitch = _pitch; };
        drag.OnDragUpdate += (_, e) => { _yaw = _dragYaw + (float)e.OffsetX * .009f; _pitch = Math.Clamp(_dragPitch + (float)e.OffsetY * .006f, -.15f, .7f); Widget.QueueRender(); }; Widget.AddController(drag);
        var context = SynchronizationContext.Current!;
        _ = _loading.ContinueWith(_ => context.Post(_ => { if (!_disposed) Widget.QueueRender(); }, null), TaskScheduler.Default);
    }
    public void Update()
    {
        var s = _session.Status;
        _spools = s?.Ams.SelectMany(a => a.Spools).ToArray() ?? [];
        var key = $"{s?.State}:{s?.Progress}:{s?.ActiveSpool?.Color}:{_session.ChamberLightOn}:{_session.IsOnline}:" + string.Join(',', s?.AllSpools.Select(p => p.Color) ?? []);
        if (_tick == 0 && Widget.GetMapped() && _visible(Widget) && s?.State == JobState.Printing && Gtk.Settings.GetDefault()?.GtkEnableAnimations != false)
            _tick = Widget.AddTickCallback((_, clock) =>
            {
                if (_disposed || !Widget.GetMapped() || !_visible(Widget) || Widget.GetRoot() is Gtk.Window { Suspended: true } || _session.Status?.State != JobState.Printing || Gtk.Settings.GetDefault()?.GtkEnableAnimations == false) { _tick = 0; return false; }
                var now = clock.GetFrameTime();
                if (now - _lastFrame > 66000) { _lastFrame = now; _time = now / 1_000_000.0; Widget.QueueRender(); }
                return true;
            });
        if (_key == key || !Widget.GetMapped() || !_visible(Widget)) return;
        _key = key; Widget.QueueRender();
    }
    private unsafe void Initialize(Gltf model)
    {
        var gl = _gl!;
        uint Shader(ShaderType type, string source)
        {
            var shader = gl.CreateShader(type); gl.ShaderSource(shader, source); gl.CompileShader(shader); gl.GetShader(shader, ShaderParameterName.CompileStatus, out var okay);
            if (okay == 0) throw new InvalidDataException(gl.GetShaderInfoLog(shader)); return shader;
        }
        var vertex = Shader(ShaderType.VertexShader, """
            #version 330 core
            layout(location=0) in vec3 position;
            layout(location=1) in vec3 normal;
            uniform mat4 mvp; uniform mat4 world;
            out vec3 n; out vec3 p;
            void main(){ gl_Position=mvp*vec4(position,1); p=vec3(world*vec4(position,1)); n=normalize(transpose(inverse(mat3(world)))*normal); }
            """);
        var fragment = Shader(ShaderType.FragmentShader, """
            #version 330 core
            in vec3 n; in vec3 p; out vec4 outputColor;
            uniform vec4 color; uniform float metal; uniform float rough; uniform float lamp;
            void main(){
              vec3 N=normalize(n); if(!gl_FrontFacing) N=-N;
              vec3 V=normalize(vec3(0,0,4)-p);
              vec3 L=normalize(vec3(-.6,1,.9)); vec3 H=normalize(L+V);
              float d=max(dot(N,L),0); float rim=max(dot(N,normalize(vec3(.4,.6,-.9))),0);
              float fill=max(dot(N,normalize(vec3(.8,.25,.5))),0);
              float spec=pow(max(dot(N,H),0),mix(100,5,rough))*mix(.18,.8,metal);
              vec3 lit=color.rgb*(.28+.66*d+.22*fill+.28*rim)+vec3(spec);
              lit+=color.rgb*vec3(1,.86,.65)*lamp*.10;
              outputColor=vec4(pow(max(lit,vec3(0)),vec3(1./2.2)),color.a);
            }
            """);
        _program = gl.CreateProgram(); gl.AttachShader(_program, vertex); gl.AttachShader(_program, fragment); gl.LinkProgram(_program); gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out var linked);
        gl.DeleteShader(vertex); gl.DeleteShader(fragment); if (linked == 0) throw new InvalidDataException(gl.GetProgramInfoLog(_program));
        _mvp = gl.GetUniformLocation(_program, "mvp"); _world = gl.GetUniformLocation(_program, "world"); _color = gl.GetUniformLocation(_program, "color"); _metal = gl.GetUniformLocation(_program, "metal"); _rough = gl.GetUniformLocation(_program, "rough"); _lamp = gl.GetUniformLocation(_program, "lamp");
        var bounds = model.SceneBounds()!.Value; _center = (bounds.Min + bounds.Max) / 2; var size = bounds.Max - bounds.Min; _radius = Math.Max(size.X, Math.Max(size.Y, size.Z)) * .62f;
        foreach (var node in model.SceneNodes().Where(n => n.Mesh >= 0))
        {
            var names = new List<string>(); for (var i = node.Index; i >= 0; i = model.Nodes[i].Parent) names.Add(model.Nodes[i].Name);
            var partName = names.FirstOrDefault(n => n.StartsWith("Part_", StringComparison.Ordinal));
            var spoolName = names.FirstOrDefault(n => n.StartsWith("Spool_", StringComparison.Ordinal));
            var part = partName is not null && int.TryParse(partName[5..], out var partId) ? partId : -1;
            var spool = spoolName is not null && int.TryParse(spoolName[6..], out var spoolId) ? spoolId : -1;
            foreach (var primitive in model.Meshes[node.Mesh].Primitives)
            {
                var vertices = new float[primitive.Positions.Length * 6];
                for (var i = 0; i < primitive.Positions.Length; i++) { var p = primitive.Positions[i]; var n = primitive.Normals?[i] ?? Vector3.UnitY; var k = i * 6; vertices[k] = p.X; vertices[k + 1] = p.Y; vertices[k + 2] = p.Z; vertices[k + 3] = n.X; vertices[k + 4] = n.Y; vertices[k + 5] = n.Z; }
                var vao = gl.GenVertexArray(); gl.BindVertexArray(vao); var vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
                gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
                var ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo); gl.BufferData<int>(BufferTargetARB.ElementArrayBuffer, primitive.Indices, BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0); gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);
                var material = primitive.Material >= 0 ? model.Materials[primitive.Material] : null;
                var glass = material?.Alpha == Gltf.AlphaMode.Blend || material?.Name.Contains("Glass", StringComparison.OrdinalIgnoreCase) == true;
                _draws.Add(new(vao, vbo, ebo, primitive.Indices.Length, node.Index, material, part, spool, names.Any(n => n.StartsWith("Filament_", StringComparison.Ordinal)), glass));
            }
        }
        _ordered = _draws.OrderBy(d => d.Glass).ToArray();
        _nodes = model.SceneNodes().ToArray(); _worlds = new Matrix4x4[model.Nodes.Count];
        if (model.Find("Part_00") is { } first && model.Find("Part_63") is { } last && model.Bounds(first.Index) is { } bottom && model.Bounds(last.Index) is { } top)
            _partHeight = top.Max.Y - bottom.Min.Y;
    }
    private unsafe void Render()
    {
        if (_gl is not { } gl || _disposed) return;
        gl.ClearColor(0, 0, 0, 0); gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (!_loading.IsCompletedSuccessfully) return;
        var model = _loading.Result; if (_program == 0) Initialize(model);
        var width = Widget.GetWidth(); var height = Widget.GetHeight(); if (width <= 0 || height <= 0) return;
        var s = _session.Status; var shown = PrinterArt.ShownFraction(s?.State ?? JobState.Idle, (s?.Progress ?? 0) / 100.0);
        var rotate = Matrix4x4.CreateRotationY(_yaw) * Matrix4x4.CreateRotationX(_pitch);
        var view = Matrix4x4.CreateLookAt(new(0, 0, _radius / MathF.Tan(17 * MathF.PI / 360)), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(17 * MathF.PI / 180, (float)width / height, .01f, 100);
        gl.Enable(EnableCap.DepthTest); gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); gl.UseProgram(_program);
        var worlds = _worlds;
        var rest = _partHeight * (float)(1 - shown);
        foreach (var node in _nodes)
        {
            var local = node.Local;
            var offset = node.Name switch
            {
                "BedCarriage" => new Vector3(0, rest, 0),
                "Gantry" => new Vector3(0, -rest, 0),
                "Toolhead" when s?.State == JobState.Printing => Motion(_time),
                "Bed" when s?.State == JobState.Printing && _session.Config.Model?.Shape.IsBedSlinger() == true => new Vector3(0, 0, .022f * MathF.Sin((float)_time * 2)),
                _ => Vector3.Zero,
            };
            if (offset != Vector3.Zero && node.Parent >= 0 && Matrix4x4.Invert(model.World(node.Parent), out var inverse)) offset = Vector3.TransformNormal(offset, inverse);
            local.Translation += offset;
            worlds[node.Index] = node.Parent >= 0 ? local * worlds[node.Parent] : local;
        }
        var spools = _spools;
        foreach (var draw in _ordered)
        {
            if (draw.Part >= 0 && draw.Part >= Math.Ceiling(shown * 64 - 1e-9)) continue;
            if (draw.Spool >= 0 && (draw.Spool >= spools.Length || !spools[draw.Spool].IsLoaded)) continue;
            var color = draw.Material?.BaseColor ?? new Vector4(.4f, .4f, .4f, 1);
            if (draw.Part >= 0) color = Color(s?.ActiveSpool?.Color) ?? new(.13f, .8f, .42f, 1);
            else if (draw.Filament && draw.Spool >= 0) color = Color(spools[draw.Spool].Color) ?? color;
            if (draw.Glass) color = new(.012f, .018f, .018f, .16f);
            var transform = worlds[draw.Node] * Matrix4x4.CreateTranslation(-_center) * rotate;
            var mvp = transform * view * projection;
            gl.UniformMatrix4(_world, 1, false, (float*)&transform); gl.UniformMatrix4(_mvp, 1, false, (float*)&mvp);
            gl.Uniform4(_color, color.X, color.Y, color.Z, color.W); gl.Uniform1(_metal, draw.Material?.Metallic ?? 0); gl.Uniform1(_rough, draw.Material?.Roughness ?? .5f); gl.Uniform1(_lamp, _session.ChamberLightOn ? 1f : 0f);
            gl.DepthMask(!draw.Glass); gl.BindVertexArray(draw.Vao); gl.DrawElements(PrimitiveType.Triangles, (uint)draw.Count, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.DepthMask(true); gl.BindVertexArray(0); gl.UseProgram(0);
    }
    private static Vector3 Motion(double time)
    {
        (double At, Vector3 To)[] keys = [(0, Vector3.Zero), (.7, new(.028f, 0, 0)), (1.15, new(.028f, 0, .018f)), (2.05, new(-.028f, 0, .018f)), (2.5, new(-.028f, 0, 0)), (3, Vector3.Zero)];
        var t = time % 3;
        for (var i = 1; i < keys.Length; i++) { if (t > keys[i].At) continue; var f = (float)((t - keys[i - 1].At) / (keys[i].At - keys[i - 1].At)); return Vector3.Lerp(keys[i - 1].To, keys[i].To, f * f * (3 - 2 * f)); }
        return Vector3.Zero;
    }
    private static Vector4? Color(string? hex) => PrinterArt.Color(hex) is { } c ? new((float)Math.Pow(c.R, 2.2), (float)Math.Pow(c.G, 2.2), (float)Math.Pow(c.B, 2.2), 1) : null;
    private void Release()
    {
        if (_gl is not { } gl) return; Widget.MakeCurrent();
        foreach (var d in _draws) { gl.DeleteVertexArray(d.Vao); gl.DeleteBuffer(d.Vertices); gl.DeleteBuffer(d.Indices); }
        _draws.Clear(); if (_program != 0) gl.DeleteProgram(_program); _program = 0; gl.Dispose(); _gl = null;
    }
    public void Dispose() { _disposed = true; if (_tick != 0) { Widget.RemoveTickCallback(_tick); _tick = 0; } if (Widget.GetRealized()) Release(); }
}
