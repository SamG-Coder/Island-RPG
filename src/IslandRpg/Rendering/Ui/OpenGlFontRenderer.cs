using System.Drawing;
using FontStashSharp.Interfaces;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering.Ui;

internal sealed class OpenGlFontRenderer(
    Action<int, VertexPositionColorTexture, VertexPositionColorTexture,
        VertexPositionColorTexture, VertexPositionColorTexture> drawQuad)
    : IFontStashRenderer2, ITexture2DManager, IDisposable
{
    private readonly List<int> _textures = [];

    public ITexture2DManager TextureManager => this;

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
        ref VertexPositionColorTexture bottomRight) =>
        drawQuad((int)texture, topLeft, topRight, bottomLeft, bottomRight);

    public void Dispose()
    {
        foreach (var texture in _textures)
            GL.DeleteTexture(texture);
        _textures.Clear();
    }
}
