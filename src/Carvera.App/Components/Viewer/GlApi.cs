using System.Runtime.InteropServices;
using System.Text;

namespace Carvera.App.Components.Viewer;

/// <summary>
/// The few OpenGL calls the path renderer needs, loaded through the context's own <c>GetProcAddress</c> so they work with
/// whatever Avalonia gave us (ANGLE's OpenGL ES 3 on Windows, or desktop OpenGL 3.3 core).
/// </summary>
internal sealed unsafe class GlApi
{
    public const int ArrayBuffer = 0x8892, StaticDraw = 0x88E4, Float = 0x1406, Lines = 0x0001, Triangles = 0x0004;
    public const int VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82, InfoLogLength = 0x8B84;
    public const int Blend = 0x0BE2, One = 1, SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303, ColorBufferBit = 0x4000;

    private readonly delegate* unmanaged<int, int, int, int, void> _viewport;
    private readonly delegate* unmanaged<float, float, float, float, void> _clearColor;
    private readonly delegate* unmanaged<int, void> _clear, _enable, _disable, _useProgram, _bindVertexArray, _enableVertexAttribArray, _deleteProgram, _deleteShader, _compileShader, _linkProgram;
    private readonly delegate* unmanaged<int, int, void> _blendFunc, _bindBuffer;
    private readonly delegate* unmanaged<int, int*, void> _genBuffers, _genVertexArrays, _deleteBuffers, _deleteVertexArrays;
    private readonly delegate* unmanaged<int, nint, void*, int, void> _bufferData;
    private readonly delegate* unmanaged<int, int> _createShader;
    private readonly delegate* unmanaged<int> _createProgram, _getError;
    private readonly delegate* unmanaged<int, int, byte**, int*, void> _shaderSource;
    private readonly delegate* unmanaged<int, int, int*, void> _getShaderiv, _getProgramiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getShaderInfoLog, _getProgramInfoLog;
    private readonly delegate* unmanaged<int, int, void> _attachShader;
    private readonly delegate* unmanaged<int, byte*, int> _getUniformLocation;
    private readonly delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
    private readonly delegate* unmanaged<int, float, void> _uniform1f;
    private readonly delegate* unmanaged<int, int, void> _uniform1i;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, int, float*, void> _uniform4fv;
    private readonly delegate* unmanaged<int, int, int, byte, int, nint, void> _vertexAttribPointer;
    private readonly delegate* unmanaged<int, int, int, void> _drawArrays;
    private readonly delegate* unmanaged<int, int, int, int, void> _drawArraysInstanced;
    private readonly delegate* unmanaged<int, int, void> _vertexAttribDivisor;
    private readonly delegate* unmanaged<int, float, float, void> _uniform2f;

    public GlApi(Func<string, nint> getProcAddress)
    {
        nint P(string name)
        {
            var address = getProcAddress(name);
            if (address == 0) throw new InvalidOperationException($"The OpenGL function {name} is not available.");
            return address;
        }
        _viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        _clear = (delegate* unmanaged<int, void>)P("glClear");
        _enable = (delegate* unmanaged<int, void>)P("glEnable");
        _disable = (delegate* unmanaged<int, void>)P("glDisable");
        _blendFunc = (delegate* unmanaged<int, int, void>)P("glBlendFunc");
        _genVertexArrays = (delegate* unmanaged<int, int*, void>)P("glGenVertexArrays");
        _bindVertexArray = (delegate* unmanaged<int, void>)P("glBindVertexArray");
        _deleteVertexArrays = (delegate* unmanaged<int, int*, void>)P("glDeleteVertexArrays");
        _genBuffers = (delegate* unmanaged<int, int*, void>)P("glGenBuffers");
        _bindBuffer = (delegate* unmanaged<int, int, void>)P("glBindBuffer");
        _bufferData = (delegate* unmanaged<int, nint, void*, int, void>)P("glBufferData");
        _deleteBuffers = (delegate* unmanaged<int, int*, void>)P("glDeleteBuffers");
        _createShader = (delegate* unmanaged<int, int>)P("glCreateShader");
        _shaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)P("glShaderSource");
        _compileShader = (delegate* unmanaged<int, void>)P("glCompileShader");
        _getShaderiv = (delegate* unmanaged<int, int, int*, void>)P("glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)P("glGetShaderInfoLog");
        _deleteShader = (delegate* unmanaged<int, void>)P("glDeleteShader");
        _createProgram = (delegate* unmanaged<int>)P("glCreateProgram");
        _attachShader = (delegate* unmanaged<int, int, void>)P("glAttachShader");
        _linkProgram = (delegate* unmanaged<int, void>)P("glLinkProgram");
        _getProgramiv = (delegate* unmanaged<int, int, int*, void>)P("glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)P("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged<int, void>)P("glUseProgram");
        _deleteProgram = (delegate* unmanaged<int, void>)P("glDeleteProgram");
        _getUniformLocation = (delegate* unmanaged<int, byte*, int>)P("glGetUniformLocation");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        _uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        _uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        _uniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
        _enableVertexAttribArray = (delegate* unmanaged<int, void>)P("glEnableVertexAttribArray");
        _vertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, nint, void>)P("glVertexAttribPointer");
        _drawArrays = (delegate* unmanaged<int, int, int, void>)P("glDrawArrays");
        _getError = (delegate* unmanaged<int>)P("glGetError");
        _drawArraysInstanced = (delegate* unmanaged<int, int, int, int, void>)P("glDrawArraysInstanced");
        _vertexAttribDivisor = (delegate* unmanaged<int, int, void>)P("glVertexAttribDivisor");
        _uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
    }

    public void Viewport(int x, int y, int width, int height) => _viewport(x, y, width, height);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(int mask) => _clear(mask);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void BlendFunc(int source, int destination) => _blendFunc(source, destination);
    public void UseProgram(int program) => _useProgram(program);
    public void BindVertexArray(int array) => _bindVertexArray(array);
    public void BindBuffer(int target, int buffer) => _bindBuffer(target, buffer);
    public void EnableVertexAttribArray(int index) => _enableVertexAttribArray(index);
    public void VertexAttribPointer(int index, int size, int stride, int offset) => _vertexAttribPointer(index, size, Float, 0, stride, offset);
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);
    public void DrawArraysInstanced(int mode, int first, int count, int instances) => _drawArraysInstanced(mode, first, count, instances);
    /// <summary>Makes an attribute advance once per instance (1) instead of once per vertex (0).</summary>
    public void VertexAttribDivisor(int index, int divisor) => _vertexAttribDivisor(index, divisor);
    public int GetError() => _getError();
    public void DeleteProgram(int program) => _deleteProgram(program);

    public int GenBuffer() { int id; _genBuffers(1, &id); return id; }
    public int GenVertexArray() { int id; _genVertexArrays(1, &id); return id; }
    public void DeleteBuffer(int id) => _deleteBuffers(1, &id);
    public void DeleteVertexArray(int id) => _deleteVertexArrays(1, &id);

    public void BufferData(int target, ReadOnlySpan<float> data)
    {
        fixed (float* p = data) _bufferData(target, data.Length * sizeof(float), p, StaticDraw);
    }

    public int GetUniformLocation(int program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return _getUniformLocation(program, p);
    }

    public void UniformMatrix(int location, in System.Numerics.Matrix4x4 matrix)
    {
        // System.Numerics is row-major with row vectors; uploaded untransposed it is column-major for GLSL's mat * vec.
        fixed (System.Numerics.Matrix4x4* p = &matrix) _uniformMatrix4fv(location, 1, 0, (float*)p);
    }

    public void Uniform(int location, float value) => _uniform1f(location, value);
    public void Uniform(int location, float x, float y) => _uniform2f(location, x, y);
    public void Uniform1i(int location, int value) => _uniform1i(location, value);
    public void Uniform(int location, float r, float g, float b, float a) => _uniform4f(location, r, g, b, a);

    public void Uniform4(int location, ReadOnlySpan<float> values)
    {
        fixed (float* p = values) _uniform4fv(location, values.Length / 4, p);
    }

    /// <summary>Compiles and links a program; throws with the driver's message when that fails.</summary>
    public int BuildProgram(string vertexSource, string fragmentSource)
    {
        var vertex = Compile(VertexShader, vertexSource);
        var fragment = Compile(FragmentShader, fragmentSource);
        var program = _createProgram();
        _attachShader(program, vertex);
        _attachShader(program, fragment);
        _linkProgram(program);
        _deleteShader(vertex);
        _deleteShader(fragment);
        int ok;
        _getProgramiv(program, LinkStatus, &ok);
        if (ok == 0) throw new InvalidOperationException("Linking the shader program failed: " + Log(program, isShader: false));
        return program;
    }

    private int Compile(int type, string source)
    {
        var shader = _createShader(type);
        var bytes = Encoding.UTF8.GetBytes(source + "\0");
        fixed (byte* text = bytes)
        {
            var pointer = text;
            _shaderSource(shader, 1, &pointer, null);
        }
        _compileShader(shader);
        int ok;
        _getShaderiv(shader, CompileStatus, &ok);
        if (ok == 0) throw new InvalidOperationException($"Compiling the {(type == VertexShader ? "vertex" : "fragment")} shader failed: " + Log(shader, isShader: true));
        return shader;
    }

    private string Log(int id, bool isShader)
    {
        int length;
        if (isShader) _getShaderiv(id, InfoLogLength, &length); else _getProgramiv(id, InfoLogLength, &length);
        if (length <= 1) return "(no message)";
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            int written;
            if (isShader) _getShaderInfoLog(id, length, &written, p); else _getProgramInfoLog(id, length, &written, p);
        }
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0', '\n', '\r');
    }
}
