using System.Drawing;
using FontStashSharp.Interfaces;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering.Ui;

/// <summary>
/// FontStashSharp renderer that turns all adjacent glyphs from the same atlas
/// into one indexed-free triangle batch. Call <see cref="Flush"/> before
/// drawing non-text UI to preserve painter ordering.
/// </summary>
internal sealed class BatchedOpenGlFontRenderer :
    IFontStashRenderer2,
    ITexture2DManager,
    IDisposable
{
    private const int FloatsPerVertex = 8;
    private const int VerticesPerGlyph = 6;
    private readonly List<int> _textures = [];
    private float[] _vertices = new float[512 * VerticesPerGlyph * FloatsPerVertex];
    private int _floatCount;
    private int _currentTexture;
    private int _viewportWidth = 1;
    private int _viewportHeight = 1;
    private readonly int _program;
    private readonly int _imageUniform;
    private readonly int _vao;
    private readonly int _vbo;
    private int _gpuCapacityBytes;

    public BatchedOpenGlFontRenderer()
    {
        _program = CreateProgram();
        _imageUniform = GL.GetUniformLocation(_program, "image");
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVao);
        GL.GetInteger(
            GetPName.ArrayBufferBinding, out var previousArrayBuffer);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        const int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(
            1, 2, VertexAttribPointerType.Float, false, stride,
            2 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(
            2, 4, VertexAttribPointerType.Float, false, stride,
            4 * sizeof(float));
        GL.BindVertexArray(previousVao);
        GL.BindBuffer(
            BufferTarget.ArrayBuffer, previousArrayBuffer);
    }

    public ITexture2DManager TextureManager => this;

    public void BeginFrame(int viewportWidth, int viewportHeight)
    {
        Flush();
        _viewportWidth = Math.Max(1, viewportWidth);
        _viewportHeight = Math.Max(1, viewportHeight);
    }

    public object CreateTexture(int width, int height)
    {
        var texture = GL.GenTexture();
        _textures.Add(texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            IntPtr.Zero);
        GL.TexParameter(
            TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(
            TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    public Point GetTextureSize(object texture)
    {
        GL.BindTexture(TextureTarget.Texture2D, (int)texture);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D, 0,
            GetTextureParameter.TextureWidth, out int width);
        GL.GetTexLevelParameter(
            TextureTarget.Texture2D, 0,
            GetTextureParameter.TextureHeight, out int height);
        return new(width, height);
    }

    public void SetTextureData(object texture, Rectangle bounds, byte[] data)
    {
        GL.BindTexture(TextureTarget.Texture2D, (int)texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage2D(
            TextureTarget.Texture2D, 0,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            PixelFormat.Rgba, PixelType.UnsignedByte, data);
    }

    public void DrawQuad(
        object texture,
        ref VertexPositionColorTexture topLeft,
        ref VertexPositionColorTexture topRight,
        ref VertexPositionColorTexture bottomLeft,
        ref VertexPositionColorTexture bottomRight)
    {
        var textureId = (int)texture;
        if (_currentTexture != 0 && _currentTexture != textureId)
            Flush();
        _currentTexture = textureId;
        EnsureCapacity(VerticesPerGlyph * FloatsPerVertex);
        AddVertex(topLeft);
        AddVertex(bottomLeft);
        AddVertex(bottomRight);
        AddVertex(topLeft);
        AddVertex(bottomRight);
        AddVertex(topRight);
    }

    public void Flush()
    {
        if (_floatCount == 0 || _currentTexture == 0) return;
        GL.GetInteger(GetPName.CurrentProgram, out var previousProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out var previousVao);
        GL.GetInteger(
            GetPName.ArrayBufferBinding, out var previousArrayBuffer);
        GL.GetInteger(GetPName.ActiveTexture, out var previousActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(
            GetPName.TextureBinding2D, out var previousTexture);

        GL.UseProgram(_program);
        GL.Uniform1(_imageUniform, 0);
        GL.BindTexture(TextureTarget.Texture2D, _currentTexture);
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
        GL.BindBuffer(
            BufferTarget.ArrayBuffer, previousArrayBuffer);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
        GL.ActiveTexture((TextureUnit)previousActiveTexture);
        GL.UseProgram(previousProgram);
        _floatCount = 0;
        _currentTexture = 0;
    }

    public void Dispose()
    {
        Flush();
        foreach (var texture in _textures)
            GL.DeleteTexture(texture);
        _textures.Clear();
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
    }

    private void AddVertex(VertexPositionColorTexture vertex)
    {
        _vertices[_floatCount++] =
            vertex.Position.X * 2f / _viewportWidth - 1f;
        _vertices[_floatCount++] =
            1f - vertex.Position.Y * 2f / _viewportHeight;
        _vertices[_floatCount++] = vertex.TextureCoordinate.X;
        _vertices[_floatCount++] = vertex.TextureCoordinate.Y;
        _vertices[_floatCount++] = vertex.Color.R / 255f;
        _vertices[_floatCount++] = vertex.Color.G / 255f;
        _vertices[_floatCount++] = vertex.Color.B / 255f;
        _vertices[_floatCount++] = vertex.Color.A / 255f;
    }

    private void EnsureCapacity(int additionalFloats)
    {
        var required = _floatCount + additionalFloats;
        if (required <= _vertices.Length) return;
        Array.Resize(
            ref _vertices,
            Math.Max(required, _vertices.Length * 2));
    }

    private static int CreateProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec2 position;
            layout(location = 1) in vec2 textureCoordinate;
            layout(location = 2) in vec4 color;
            out vec2 uv;
            out vec4 tint;
            void main()
            {
                gl_Position = vec4(position, 0.0, 1.0);
                uv = textureCoordinate;
                tint = color;
            }
            """;
        const string fragmentSource = """
            #version 330 core
            uniform sampler2D image;
            in vec2 uv;
            in vec4 tint;
            out vec4 outputColor;
            void main()
            {
                outputColor = texture(image, uv) * tint;
            }
            """;
        var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragment = CompileShader(
            ShaderType.FragmentShader, fragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(
            program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (linked != (int)All.True)
            throw new InvalidOperationException(
                $"Font shader link failed: {GL.GetProgramInfoLog(program)}");
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(
            shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == (int)All.True) return shader;
        var error = GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        throw new InvalidOperationException(
            $"Font shader compilation failed: {error}");
    }
}
