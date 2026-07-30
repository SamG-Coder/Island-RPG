using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

/// <summary>
/// Batches flat UI rectangles into one draw while preserving their individual
/// colours and opacity. This avoids submitting every panel edge separately.
/// </summary>
internal sealed class UiColorBatch : IDisposable
{
    private const int FloatsPerVertex = 6;
    private const int VerticesPerRectangle = 6;
    private float[] _vertices = new float[4096];
    private int _floatCount;
    private int _program;
    private int _vao;
    private int _vbo;
    private int _gpuCapacityBytes;
    private int _viewportWidth = 1;
    private int _viewportHeight = 1;

    public bool IsEmpty => _floatCount == 0;

    public void Initialize()
    {
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVao);
        GL.GetInteger(
            GetPName.ArrayBufferBinding, out var previousArrayBuffer);
        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 pixel;
            layout(location=1) in vec4 vertexColor;
            uniform vec2 viewport;
            out vec4 tint;
            void main() {
                gl_Position = vec4(
                    pixel.x * 2.0 / viewport.x - 1.0,
                    1.0 - pixel.y * 2.0 / viewport.y,
                    0.0, 1.0);
                tint = vertexColor;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec4 tint;
            out vec4 color;
            void main() { color = tint; }
            """;

        var vertexShader = Compile(ShaderType.VertexShader, vertex);
        var fragmentShader = Compile(ShaderType.FragmentShader, fragment);
        _program = GL.CreateProgram();
        GL.AttachShader(_program, vertexShader);
        GL.AttachShader(_program, fragmentShader);
        GL.LinkProgram(_program);
        GL.GetProgram(
            _program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        if (linked == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(_program));

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0, 2, VertexAttribPointerType.Float, false,
            FloatsPerVertex * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(
            1, 4, VertexAttribPointerType.Float, false,
            FloatsPerVertex * sizeof(float), 2 * sizeof(float));
        GL.BindVertexArray(previousVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousArrayBuffer);
    }

    public void BeginFrame(int viewportWidth, int viewportHeight)
    {
        Flush();
        _viewportWidth = Math.Max(1, viewportWidth);
        _viewportHeight = Math.Max(1, viewportHeight);
    }

    public void Add(Vector4 rectangle, Vector4 color, float opacity)
    {
        if (!IsFinite(rectangle) ||
            !IsFinite(color) ||
            !float.IsFinite(opacity) ||
            rectangle.Z <= 0 ||
            rectangle.W <= 0 ||
            opacity <= 0)
            return;

        // Layout is expressed in logical client pixels. Clip it here before
        // uploading vertices so a transient off-screen or oversized control
        // can never become a huge clipped triangle in the OpenGL viewport.
        var left = Math.Clamp(rectangle.X, 0, _viewportWidth);
        var top = Math.Clamp(rectangle.Y, 0, _viewportHeight);
        var right = Math.Clamp(
            rectangle.X + rectangle.Z, 0, _viewportWidth);
        var bottom = Math.Clamp(
            rectangle.Y + rectangle.W, 0, _viewportHeight);
        if (right <= left || bottom <= top) return;

        EnsureCapacity(VerticesPerRectangle * FloatsPerVertex);
        var alpha = Math.Clamp(color.W * opacity, 0, 1);
        AddVertex(left, top, color, alpha);
        AddVertex(left, bottom, color, alpha);
        AddVertex(right, bottom, color, alpha);
        AddVertex(left, top, color, alpha);
        AddVertex(right, bottom, color, alpha);
        AddVertex(right, top, color, alpha);
    }

    public void AddLine(
        float x1,
        float y1,
        float x2,
        float y2,
        float width,
        Vector4 color,
        float opacity)
    {
        if (!float.IsFinite(x1) ||
            !float.IsFinite(y1) ||
            !float.IsFinite(x2) ||
            !float.IsFinite(y2) ||
            !float.IsFinite(width) ||
            !IsFinite(color) ||
            !float.IsFinite(opacity) ||
            width <= 0 ||
            opacity <= 0)
            return;

        var deltaX = x2 - x1;
        var deltaY = y2 - y1;
        var length = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (!float.IsFinite(length) || length <= float.Epsilon) return;

        var halfWidth = width * .5f;
        var normalX = -deltaY / length * halfWidth;
        var normalY = deltaX / length * halfWidth;
        var alpha = Math.Clamp(color.W * opacity, 0, 1);
        EnsureCapacity(VerticesPerRectangle * FloatsPerVertex);

        AddVertex(x1 + normalX, y1 + normalY, color, alpha);
        AddVertex(x1 - normalX, y1 - normalY, color, alpha);
        AddVertex(x2 - normalX, y2 - normalY, color, alpha);
        AddVertex(x1 + normalX, y1 + normalY, color, alpha);
        AddVertex(x2 - normalX, y2 - normalY, color, alpha);
        AddVertex(x2 + normalX, y2 + normalY, color, alpha);
    }

    public void Flush()
    {
        if (_floatCount == 0) return;
        GL.GetInteger(GetPName.CurrentProgram, out var previousProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVao);
        GL.GetInteger(
            GetPName.ArrayBufferBinding, out var previousArrayBuffer);

        GL.UseProgram(_program);
        GL.Uniform2(
            GL.GetUniformLocation(_program, "viewport"),
            (float)_viewportWidth,
            (float)_viewportHeight);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        var usedBytes = _floatCount * sizeof(float);
        if (usedBytes > _gpuCapacityBytes)
        {
            _gpuCapacityBytes = Math.Max(
                usedBytes, Math.Max(4096, _gpuCapacityBytes * 2));
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                _gpuCapacityBytes,
                IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
        }
        GL.BufferSubData(
            BufferTarget.ArrayBuffer,
            IntPtr.Zero,
            usedBytes,
            _vertices);
        GL.DrawArrays(
            PrimitiveType.Triangles,
            0,
            _floatCount / FloatsPerVertex);

        GL.BindVertexArray(previousVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousArrayBuffer);
        GL.UseProgram(previousProgram);
        _floatCount = 0;
    }

    public void Dispose()
    {
        Flush();
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_program != 0) GL.DeleteProgram(_program);
        _vbo = 0;
        _vao = 0;
        _program = 0;
    }

    private void AddVertex(float x, float y, Vector4 color, float alpha)
    {
        _vertices[_floatCount++] = x;
        _vertices[_floatCount++] = y;
        _vertices[_floatCount++] = color.X;
        _vertices[_floatCount++] = color.Y;
        _vertices[_floatCount++] = color.Z;
        _vertices[_floatCount++] = alpha;
    }

    private void EnsureCapacity(int additionalFloats)
    {
        var required = _floatCount + additionalFloats;
        if (required <= _vertices.Length) return;
        Array.Resize(
            ref _vertices,
            Math.Max(required, _vertices.Length * 2));
    }

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static int Compile(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == 0)
            throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
        return shader;
    }
}
