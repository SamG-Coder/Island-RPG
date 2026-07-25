using IslandRpg.Assets;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed class DemoWindow : GameWindow
{
    private readonly Sprite _sprite;
    private readonly int _graphicId;
    private readonly double _secondsPerFrame;
    private readonly int _animationFrameCount;
    private int[] _textures = [];
    private readonly IReadOnlyList<Sprite>? _catalogSprites;
    private int[][]? _catalogTextures;
    private int _program;
    private int _vao;
    private double _time;
    private bool _isDragging;
    private Vector2 _lastMousePosition;
    private Vector2 _cameraOffsetPixels;
    private float _zoom = 1f;

    private const float MinZoom = 0.65f;
    private const float MaxZoom = 1.75f;

    public DemoWindow(
        Sprite sprite,
        int graphicId,
        float? secondsPerFrame = null,
        ushort? animationFrameCount = null) : base(
        GameWindowSettings.Default,
        new NativeWindowSettings { ClientSize = new Vector2i(1280, 720), Title = $"Island RPG prototype — graphic {graphicId}" })
    {
        _sprite = sprite;
        _graphicId = graphicId;
        _secondsPerFrame = secondsPerFrame is > 0 and < 60 ? secondsPerFrame.Value : 0.125;
        _animationFrameCount = Math.Clamp(animationFrameCount ?? sprite.Frames.Count, 1, sprite.Frames.Count);
    }

    public DemoWindow(IReadOnlyList<Sprite> sprites) : base(
        GameWindowSettings.Default,
        new NativeWindowSettings { ClientSize = new Vector2i(1280, 720), Title = "Island RPG prototype - tree catalogue" })
    {
        if (sprites.Count == 0) throw new ArgumentException("At least one tree is required.", nameof(sprites));
        _catalogSprites = sprites;
        _sprite = sprites[0];
        _graphicId = -1;
        _secondsPerFrame = 1;
        _animationFrameCount = 1;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.ClearColor(0.12f, 0.12f, 0.12f, 1);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _program = GameShaderPrograms.CreateDemoProgram();
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        if (_catalogSprites is null)
            _textures = _sprite.Frames.Select(Upload).ToArray();
        else
            _catalogTextures = _catalogSprites
                .Select(sprite => sprite.Frames.Select(Upload).ToArray())
                .ToArray();
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        if (KeyboardState.IsKeyDown(Keys.Escape)) Close();

        var mousePosition = MouseState.Position;
        if (MouseState.IsButtonDown(MouseButton.Left))
        {
            if (_isDragging)
                _cameraOffsetPixels += mousePosition - _lastMousePosition;
            _lastMousePosition = mousePosition;
            _isDragging = true;
        }
        else
        {
            _isDragging = false;
        }

        _time += e.Time;
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.OffsetY == 0) return;

        var oldZoom = _zoom;
        _zoom = Math.Clamp(
            oldZoom * MathF.Pow(1.12f, e.OffsetY),
            MinZoom,
            MaxZoom);
        if (Math.Abs(_zoom - oldZoom) < 0.0001f) return;

        // Keep the world point currently beneath the cursor fixed on screen.
        var cursorFromCenter = MouseState.Position - new Vector2(Size.X / 2f, Size.Y / 2f);
        var ratio = _zoom / oldZoom;
        _cameraOffsetPixels = cursorFromCenter -
                              (cursorFromCenter - _cameraOffsetPixels) * ratio;
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        if (_catalogSprites is null)
        {
            // A directional SLP stores multiple angles consecutively. Animate one
            // direction; camera-facing angle selection comes with unit movement.
            var index = (int)(_time / _secondsPerFrame) % _animationFrameCount;
            DrawSprite(_sprite.Frames[index], _textures[index], Vector2.Zero);
        }
        else
        {
            const int columns = 4;
            const float cellWidth = 220;
            const float cellHeight = 190;
            var rows = (int)Math.Ceiling(_catalogSprites.Count / (double)columns);
            for (var i = 0; i < _catalogSprites.Count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var offset = new Vector2(
                    (column - (columns - 1) / 2f) * cellWidth,
                    (row - (rows - 1) / 2f) * cellHeight);
                DrawSprite(_catalogSprites[i].Frames[0], _catalogTextures![i][0], offset);
            }
        }
        SwapBuffers();
    }

    private void DrawSprite(SpriteFrame frame, int texture, Vector2 worldOffsetPixels)
    {
        // Source SLP dimensions are authored for 1:1 pixels at the default zoom.
        var scale = _zoom;
        var viewportWidth = Math.Max(1, Size.X);
        var viewportHeight = Math.Max(1, Size.Y);
        var halfW = frame.Width * scale / viewportWidth;
        var halfH = frame.Height * scale / viewportHeight;

        // The SLP hotspot is the world/ground anchor inside the bitmap.
        // Camera dragging is stored in screen pixels, then converted to NDC.
        var centerX = (((frame.Width / 2f - frame.HotspotX) + worldOffsetPixels.X) * scale +
                       _cameraOffsetPixels.X) * 2f / viewportWidth;
        var centerY = ((frame.HotspotY - frame.Height / 2f) * scale -
                       _cameraOffsetPixels.Y - worldOffsetPixels.Y * scale) * 2f / viewportHeight;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "useTexture"), 1);
        GL.Uniform4(GL.GetUniformLocation(_program, "tint"), 1f, 1f, 1f, 1f);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        Draw([
            centerX-halfW, centerY+halfH, 0,0,
            centerX-halfW, centerY-halfH, 0,1,
            centerX+halfW, centerY-halfH, 1,1,
            centerX+halfW, centerY+halfH, 1,0
        ]);
    }

    private void Draw(float[] vertices)
    {
        var vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StreamDraw);
        var textured = vertices.Length == 16;
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (textured ? 4 : 2) * sizeof(float), 0);
        if (textured)
        {
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        }
        GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
        GL.DisableVertexAttribArray(1);
        GL.DeleteBuffer(vbo);
    }

    private static int Upload(SpriteFrame frame)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, frame.Width, frame.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, frame.Rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return texture;
    }

    protected override void OnUnload()
    {
        foreach (var texture in _textures) GL.DeleteTexture(texture);
        if (_catalogTextures is not null)
        foreach (var spriteTextures in _catalogTextures)
        foreach (var texture in spriteTextures)
            GL.DeleteTexture(texture);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
        base.OnUnload();
    }
}
