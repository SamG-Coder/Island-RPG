using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

/// <summary>
/// Draws antialiased circles and content-sized capsules from one SDF shader.
/// Equal half-width and radius produces a circle; wider bounds produce a pill.
/// </summary>
internal sealed class UiPillRenderer : IDisposable
{
    private int _program;
    private int _vao;
    private int _vbo;
    private int _viewportWidth = 1;
    private int _viewportHeight = 1;

    public void Initialize()
    {
        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 corner;
            uniform vec2 viewport;
            uniform vec2 center;
            uniform vec2 halfSize;
            out vec2 localPosition;
            void main() {
                vec2 pixel = center + corner * halfSize;
                gl_Position = vec4(
                    pixel.x * 2.0 / viewport.x - 1.0,
                    1.0 - pixel.y * 2.0 / viewport.y,
                    0.0, 1.0);
                localPosition = corner * halfSize;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 localPosition;
            uniform vec2 halfSize;
            uniform float radius;
            uniform float borderWidth;
            uniform vec4 edgeColor;
            uniform vec4 faceColor;
            out vec4 color;
            void main() {
                float straight = max(halfSize.x - radius, 0.0);
                vec2 capsule = localPosition;
                capsule.x -= clamp(capsule.x, -straight, straight);
                float distanceToEdge = length(capsule) - radius;
                float smoothing = max(fwidth(distanceToEdge), 0.75);
                float coverage = 1.0 - smoothstep(
                    -smoothing, smoothing, distanceToEdge);
                float inner = 1.0 - smoothstep(
                    -borderWidth - smoothing,
                    -borderWidth + smoothing,
                    distanceToEdge);
                color = mix(edgeColor, faceColor, inner);
                color.a *= coverage;
                if (color.a <= 0.001) discard;
            }
            """;
        _program = CreateProgram(vertex, fragment);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        float[] corners =
        [
            -1, -1, -1, 1, 1, 1,
            -1, -1, 1, 1, 1, -1
        ];
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            corners.Length * sizeof(float),
            corners,
            BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0, 2, VertexAttribPointerType.Float, false,
            2 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    public void BeginFrame(int viewportWidth, int viewportHeight)
    {
        _viewportWidth = Math.Max(1, viewportWidth);
        _viewportHeight = Math.Max(1, viewportHeight);
    }

    public void Draw(
        float centerX,
        float centerY,
        float halfWidth,
        float radius,
        Vector4 edge,
        Vector4 face,
        float borderWidth = 2)
    {
        if (_program == 0 || radius <= 0 || halfWidth < radius) return;
        GL.GetInteger(GetPName.CurrentProgram, out var previousProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVao);
        GL.UseProgram(_program);
        GL.Uniform2(
            GL.GetUniformLocation(_program, "viewport"),
            (float)_viewportWidth,
            (float)_viewportHeight);
        GL.Uniform2(
            GL.GetUniformLocation(_program, "center"), centerX, centerY);
        GL.Uniform2(
            GL.GetUniformLocation(_program, "halfSize"),
            halfWidth + 1, radius + 1);
        GL.Uniform1(GL.GetUniformLocation(_program, "radius"), radius);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "borderWidth"), borderWidth);
        GL.Uniform4(GL.GetUniformLocation(_program, "edgeColor"), edge);
        GL.Uniform4(GL.GetUniformLocation(_program, "faceColor"), face);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(previousVao);
        GL.UseProgram(previousProgram);
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_program != 0) GL.DeleteProgram(_program);
        _vbo = 0;
        _vao = 0;
        _program = 0;
    }

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertex = Compile(ShaderType.VertexShader, vertexSource);
        var fragment = Compile(ShaderType.FragmentShader, fragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (linked == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        return program;
    }

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
