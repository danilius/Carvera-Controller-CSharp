using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace Carvera.App.Components.Viewer;

/// <summary>What the path shader needs to colour the toolpath; refreshed by the view before every frame.</summary>
internal sealed record PathStyle(
    double ScrubSegment, double SelectedOperation, double DoneLine, bool ColorByOperation, int OperationCount,
    Color Faded, Color Rapid, Color Feed, Color Done, IReadOnlyList<Color> OperationColors);

/// <summary>
/// Draws the whole toolpath on the GPU. Every segment is one instance holding its two ends and four numbers (segment index,
/// operation, G-code line, rapid flag); the vertex shader turns it into a screen-space quad of a chosen pixel width (OpenGL ES
/// has no wide lines), picks the colour from those numbers and from uniforms describing the scrub position, selected operation
/// and executed line, so scrubbing and playback never rebuild or re-upload anything. Rapid moves are dashed in the fragment
/// shader. Only a new program uploads a new buffer. The 2D parts (grid, tool, gizmo, text) stay on the layers above and below.
/// </summary>
internal sealed class GlPathLayer : OpenGlControlBase
{
    /// <summary>Per segment: start x y z, end x y z, then segment index, operation, G-code line and rapid flag.</summary>
    public const int FloatsPerSegment = 10;
    /// <summary>Line widths in device-independent pixels, and the dash pattern of rapid moves.</summary>
    public const float FeedWidth = 1.6f, FadedWidth = 1.0f, RapidWidth = 1.0f, DashOn = 6f, DashOff = 5f;
    private const int MaxOperations = 64;

    private readonly ToolpathView _owner;
    private readonly OrbitCamera _camera;
    private readonly Func<PathStyle> _style;
    private readonly Action<string> _failed;
    private GlApi? _api;
    private int _program, _vao, _buffer;
    private int _mvp, _viewport, _width, _dash, _scrub, _selected, _doneLine, _pass, _byOperation, _operationCount, _palette, _colors;
    private float[] _data = [];
    private int _segmentCount;
    private bool _dirty = true;
    private bool _dead;

    /// <summary>True once a frame has been drawn; the view gives up on the GPU if that never happens.</summary>
    public bool HasRendered { get; private set; }

    /// <summary>The last OpenGL error code after drawing (0 is none), and the driver's description, for diagnostics.</summary>
    public int LastGlError { get; private set; }
    public string ContextDescription { get; private set; } = "";
    public long Frames { get; private set; }

    public GlPathLayer(ToolpathView owner, OrbitCamera camera, Func<PathStyle> style, Action<string> failed)
    {
        _owner = owner;
        _camera = camera;
        _style = style;
        _failed = failed;
        IsHitTestVisible = false; // pointer input belongs to the view underneath
    }

    /// <summary>Replaces the path (the vertex data built by <see cref="Build"/>); it is uploaded on the next frame.</summary>
    public void SetData(float[] data)
    {
        _data = data;
        _segmentCount = data.Length / FloatsPerSegment;
        _dirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>Builds the per-segment instance data: both ends, then segment index, operation, G-code line and rapid flag.</summary>
    public static float[] Build(IReadOnlyList<Vector3> starts, IReadOnlyList<Vector3> ends, IReadOnlyList<int> operations, IReadOnlyList<int> lines, IReadOnlyList<bool> rapid)
    {
        var data = new float[starts.Count * FloatsPerSegment];
        var at = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            data[at++] = starts[i].X;
            data[at++] = starts[i].Y;
            data[at++] = starts[i].Z;
            data[at++] = ends[i].X;
            data[at++] = ends[i].Y;
            data[at++] = ends[i].Z;
            data[at++] = i;
            data[at++] = operations[i];
            data[at++] = lines[i];
            data[at++] = rapid[i] ? 1 : 0;
        }
        return data;
    }

    // ---------------------------------------------------------------- shaders

    private static string Header(GlInterface gl) =>
        gl.ContextInfo.Version.Type == GlProfileType.OpenGLES ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";

    private const string VertexShader = """
        layout(location = 0) in vec3 aStart;
        layout(location = 1) in vec3 aEnd;
        layout(location = 2) in vec4 aInfo;   // segment, operation, line, rapid
        uniform mat4 uMvp;
        uniform vec2 uViewport;      // drawing surface in device pixels
        uniform float uWidth;        // line width in device pixels for the current pass
        uniform float uScrub;       // last visible segment, or -1 for all
        uniform float uSelected;     // the one operation shown, or -1 for all
        uniform float uDoneLine;     // lines up to here have been executed
        uniform int uPass;           // 0 faded, 1 rapid, 2 feed, 3 done
        uniform float uByOperation;
        uniform float uOperationCount;
        uniform vec4 uColors[4];     // faded, rapid, feed, done
        uniform vec4 uPalette[64];   // one colour per operation
        out vec4 vColor;
        out float vAlong;            // device pixels from the segment's start
        const vec2 kCorners[6] = vec2[6](vec2(0.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0), vec2(0.0, -1.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
        void main() {
            float segment = aInfo.x;
            float operation = aInfo.y;
            int kind;
            if ((uScrub >= 0.0 && segment > uScrub) || (uSelected >= 0.0 && operation != uSelected)) kind = 0;
            else if (aInfo.z <= uDoneLine) kind = 3;
            else if (aInfo.w > 0.5) kind = 1;
            else kind = 2;
            vec4 a = uMvp * vec4(aStart, 1.0);
            vec4 b = uMvp * vec4(aEnd, 1.0);
            const float eps = 0.001;
            if (kind != uPass || (a.w < eps && b.w < eps)) {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // outside the view: not drawn in this pass
                vColor = vec4(0.0);
                vAlong = 0.0;
                return;
            }
            // Keep both ends in front of the eye so the divide below is sound; the hardware clips the rest.
            if (a.w < eps) a = mix(a, b, (eps - a.w) / (b.w - a.w));
            else if (b.w < eps) b = mix(b, a, (eps - b.w) / (a.w - b.w));
            vec2 corner = kCorners[gl_VertexID];
            vec2 sa = a.xy / a.w * 0.5 * uViewport;
            vec2 sb = b.xy / b.w * 0.5 * uViewport;
            vec2 d = sb - sa;
            float len = length(d);
            vec2 dir = len > 0.0001 ? d / len : vec2(1.0, 0.0);
            vec2 normal = vec2(-dir.y, dir.x);
            vec4 p = corner.x < 0.5 ? a : b;
            // Square caps (half a width past each end) close the gaps where segments meet.
            vec2 offset = (normal * corner.y + dir * (corner.x * 2.0 - 1.0)) * uWidth * 0.5;
            p.xy += offset * 2.0 / uViewport * p.w;
            gl_Position = p;
            vAlong = corner.x * len;
            if (kind == 2 && uByOperation > 0.5 && operation >= 0.0)
                vColor = uPalette[int(mod(operation, uOperationCount))];
            else
                vColor = uColors[kind];
        }
        """;

    private const string FragmentShader = """
        in vec4 vColor;
        in float vAlong;
        uniform vec2 uDash;          // dash and gap length in device pixels; a zero dash draws solid lines
        out vec4 fragColor;
        void main() {
            if (uDash.x > 0.0 && mod(vAlong, uDash.x + uDash.y) > uDash.x) discard;
            fragColor = vec4(vColor.rgb * vColor.a, vColor.a);
        }
        """;

    // ---------------------------------------------------------------- GL lifetime

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _api = new GlApi(gl.GetProcAddress);
            ContextDescription = $"{gl.ContextInfo.Version.Type} {gl.ContextInfo.Version.Major}.{gl.ContextInfo.Version.Minor}";
            var header = Header(gl);
            _program = _api.BuildProgram(header + VertexShader, header + FragmentShader);
            _mvp = _api.GetUniformLocation(_program, "uMvp");
            _viewport = _api.GetUniformLocation(_program, "uViewport");
            _width = _api.GetUniformLocation(_program, "uWidth");
            _dash = _api.GetUniformLocation(_program, "uDash");
            _scrub = _api.GetUniformLocation(_program, "uScrub");
            _selected = _api.GetUniformLocation(_program, "uSelected");
            _doneLine = _api.GetUniformLocation(_program, "uDoneLine");
            _pass = _api.GetUniformLocation(_program, "uPass");
            _byOperation = _api.GetUniformLocation(_program, "uByOperation");
            _operationCount = _api.GetUniformLocation(_program, "uOperationCount");
            _colors = _api.GetUniformLocation(_program, "uColors");
            _palette = _api.GetUniformLocation(_program, "uPalette");
            _vao = _api.GenVertexArray();
            _buffer = _api.GenBuffer();
            _dirty = true;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_api is null) return;
        try
        {
            _api.UseProgram(0);
            _api.BindVertexArray(0);
            if (_buffer != 0) _api.DeleteBuffer(_buffer);
            if (_vao != 0) _api.DeleteVertexArray(_vao);
            if (_program != 0) _api.DeleteProgram(_program);
        }
        catch (Exception) { /* the context is going away regardless */ }
        _api = null;
        _program = _vao = _buffer = 0;
    }

    private void Fail(Exception ex)
    {
        if (_dead) return;
        _dead = true;
        _failed(ex.Message);
    }

    // ---------------------------------------------------------------- drawing

    private static void AddColor(List<float> list, Color c)
    {
        list.Add(c.R / 255f);
        list.Add(c.G / 255f);
        list.Add(c.B / 255f);
        list.Add(c.A / 255f);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_api is null || _dead || _program == 0) return;
        try
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
            _api.Viewport(0, 0, width, height);
            _api.ClearColor(0, 0, 0, 0);
            _api.Clear(GlApi.ColorBufferBit);
            if (_segmentCount == 0)
            {
                HasRendered = true;
                return;
            }

            _api.BindVertexArray(_vao);
            _api.BindBuffer(GlApi.ArrayBuffer, _buffer);
            if (_dirty)
            {
                _api.BufferData(GlApi.ArrayBuffer, _data);
                _dirty = false;
            }
            const int stride = FloatsPerSegment * sizeof(float);
            _api.EnableVertexAttribArray(0);
            _api.VertexAttribPointer(0, 3, stride, 0);
            _api.VertexAttribDivisor(0, 1);
            _api.EnableVertexAttribArray(1);
            _api.VertexAttribPointer(1, 3, stride, 3 * sizeof(float));
            _api.VertexAttribDivisor(1, 1);
            _api.EnableVertexAttribArray(2);
            _api.VertexAttribPointer(2, 4, stride, 6 * sizeof(float));
            _api.VertexAttribDivisor(2, 1);

            var style = _style();
            _api.UseProgram(_program);
            _api.UniformMatrix(_mvp, ViewMatrix.For(_camera));
            _api.Uniform(_viewport, width, height);
            _api.Uniform(_scrub, (float)style.ScrubSegment);
            _api.Uniform(_selected, (float)style.SelectedOperation);
            _api.Uniform(_doneLine, (float)style.DoneLine);
            _api.Uniform(_byOperation, style.ColorByOperation ? 1f : 0f);
            var operations = Math.Clamp(style.OperationCount, 1, MaxOperations);
            _api.Uniform(_operationCount, operations);
            var colors = new List<float>(16);
            AddColor(colors, style.Faded);
            AddColor(colors, style.Rapid);
            AddColor(colors, style.Feed);
            AddColor(colors, style.Done);
            _api.Uniform4(_colors, colors.ToArray());
            var palette = new List<float>(MaxOperations * 4);
            for (var i = 0; i < MaxOperations; i++) AddColor(palette, i < style.OperationColors.Count ? style.OperationColors[i] : style.Feed);
            _api.Uniform4(_palette, palette.ToArray());

            _api.Enable(GlApi.Blend);
            _api.BlendFunc(GlApi.One, GlApi.OneMinusSrcAlpha);
            var pixel = (float)scale;
            for (var pass = 0; pass < 4; pass++)
            {
                _api.Uniform1i(_pass, pass);
                _api.Uniform(_width, (pass == 0 ? FadedWidth : pass == 1 ? RapidWidth : FeedWidth) * pixel);
                // Rapid moves are dashed, as in the CPU view.
                if (pass == 1) _api.Uniform(_dash, DashOn * pixel, DashOff * pixel); else _api.Uniform(_dash, 0f, 0f);
                _api.DrawArraysInstanced(GlApi.Triangles, 0, 6, _segmentCount);
            }
            _api.Disable(GlApi.Blend);
            LastGlError = _api.GetError();
            Frames++;
            HasRendered = true;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
}
