using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    // Authored against classic-pc-2005.png. The generated monitor glass is 4:3,
    // so the 16:9 game is fitted inside it without distorting the renderer.
    private static readonly Vector4 ClassicPcGameViewport = new(
        .297f, .2084f, .406f, .4132f);

    private int _classicPcTexture;
    private int _classicPcCaptureTexture;
    private int _classicPcCaptureFramebuffer;
    private int _classicPcScreenProgram;
    private Vector2i _classicPcCaptureSize;
    private bool _classicPcModeEnabled;
    private float _classicPcViewZoom = 1f;
    private float _classicPcTargetViewZoom = 1f;

    private void PrepareClassicPcDisplay()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources", "Images", "Display", "classic-pc-2005.png");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        _classicPcScreenProgram =
            GameShaderPrograms.CreateClassicPcScreenProgram();
        _classicPcTexture = Upload(image.Width, image.Height, image.Data);
        GL.BindTexture(TextureTarget.Texture2D, _classicPcTexture);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _classicPcCaptureTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _classicPcCaptureTexture);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _classicPcCaptureFramebuffer = GL.GenFramebuffer();
        ResizeClassicPcCapture(force: true);
    }

    private bool ClassicPcDisplayActive =>
        _crtModeEnabled && _classicPcModeEnabled && _classicPcTexture != 0;

    private void DrawClassicPcBackdrop(
        float leftNdc, float topNdc, float rightNdc, float bottomNdc)
    {
        GL.UseProgram(_program);
        GL.Uniform1(_shaderUniforms.Get(_program, "image"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "opacity"), 1f);
        GL.Uniform1(_shaderUniforms.Get(_program, "brightness"), 0f);
        GL.Uniform1(_shaderUniforms.Get(_program, "tintAmount"), 0f);
        GL.Uniform1(_shaderUniforms.Get(_program, "grayscaleAmount"), 0f);
        GL.Uniform1(_shaderUniforms.Get(_program, "outlineOnly"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "spriteOutline"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "recolorPlayer"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "wading"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "pixelArtFilter"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "sceneLighting"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "sceneFogAmount"), 0f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _classicPcTexture);
        Draw([
            leftNdc,topNdc,0,0,
            leftNdc,bottomNdc,0,1,
            rightNdc,bottomNdc,1,1,
            rightNdc,topNdc,1,0
        ]);
    }

    private void ResizeClassicPcCapture(bool force = false)
    {
        if (_classicPcCaptureTexture == 0 ||
            _classicPcCaptureFramebuffer == 0) return;
        var size = new Vector2i(
            Math.Max(1, FramebufferSize.X),
            Math.Max(1, FramebufferSize.Y));
        if (!force && size == _classicPcCaptureSize) return;
        _classicPcCaptureSize = size;
        GL.BindTexture(TextureTarget.Texture2D, _classicPcCaptureTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            size.X, size.Y, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.BindFramebuffer(
            FramebufferTarget.Framebuffer, _classicPcCaptureFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _classicPcCaptureTexture,
            0);
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException(
                $"Classic PC framebuffer is incomplete: {status}");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void RenderClassicPcComposite()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var width = Math.Max(1, FramebufferSize.X);
        var height = Math.Max(1, FramebufferSize.Y);
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(.015f, .012f, .008f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        var scene = ClassicPcSceneBounds(width, height);
        var left = scene.X;
        var top = scene.Y;
        var outputWidth = scene.Z;
        var outputHeight = scene.W;
        var leftNdc = left * 2 / width - 1;
        var rightNdc = (left + outputWidth) * 2 / width - 1;
        var topNdc = 1 - top * 2 / height;
        var bottomNdc = 1 - (top + outputHeight) * 2 / height;
        DrawClassicPcBackdrop(leftNdc, topNdc, rightNdc, bottomNdc);

        var screen = ClassicPcOutputBounds(
            left, top, outputWidth, outputHeight);
        leftNdc = screen.X * 2 / width - 1;
        rightNdc = (screen.X + screen.Z) * 2 / width - 1;
        topNdc = 1 - screen.Y * 2 / height;
        bottomNdc = 1 - (screen.Y + screen.W) * 2 / height;
        DrawPresentationTexture(
            _classicPcCaptureTexture,
            leftNdc, topNdc, rightNdc, bottomNdc);
    }

    private void DrawPresentationTexture(
        int texture, float leftNdc, float topNdc,
        float rightNdc, float bottomNdc)
    {
        GL.UseProgram(_classicPcScreenProgram);
        GL.Uniform1(
            _shaderUniforms.Get(_classicPcScreenProgram, "image"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        Draw([
            leftNdc,topNdc,0,1,
            leftNdc,bottomNdc,0,0,
            rightNdc,bottomNdc,1,0,
            rightNdc,topNdc,1,1
        ]);
    }

    private static Vector4 ClassicPcOutputBounds(
        float left, float top, float width, float height) =>
        new(
            left + width * ClassicPcGameViewport.X,
            top + height * ClassicPcGameViewport.Y,
            width * ClassicPcGameViewport.Z,
            height * ClassicPcGameViewport.W);

    private Vector2 MapClassicPcWorldPointer(Vector2 scenePosition)
    {
        if (!ClassicPcDisplayActive) return scenePosition;
        var scene = ClassicPcSceneBounds(
            ReferenceWidth, ReferenceHeight);
        var bounds = ClassicPcOutputBounds(scene.X, scene.Y, scene.Z, scene.W);
        return new Vector2(
            (scenePosition.X - bounds.X) / bounds.Z * ReferenceWidth,
            (scenePosition.Y - bounds.Y) / bounds.W * ReferenceHeight);
    }

    private bool IsClassicPcFurniturePointer()
    {
        if (!ClassicPcDisplayActive) return false;
        var position = MouseState.Position;
        var scene = ClassicPcSceneBounds(ClientSize.X, ClientSize.Y);
        var screen = ClassicPcOutputBounds(
            scene.X, scene.Y, scene.Z, scene.W);
        return position.X < screen.X ||
               position.X > screen.X + screen.Z ||
               position.Y < screen.Y ||
               position.Y > screen.Y + screen.W;
    }

    private Vector4 ClassicPcSceneBounds(float containerWidth, float containerHeight)
    {
        var baseScale = Math.Min(
            containerWidth / ReferenceWidth,
            containerHeight / ReferenceHeight);
        var baseWidth = ReferenceWidth * baseScale;
        var baseHeight = ReferenceHeight * baseScale;
        var baseLeft = (containerWidth - baseWidth) * .5f;
        var baseTop = (containerHeight - baseHeight) * .5f;
        var monitorCenter = new Vector2(
            ClassicPcGameViewport.X + ClassicPcGameViewport.Z * .5f,
            ClassicPcGameViewport.Y + ClassicPcGameViewport.W * .5f);
        var originalCenter = new Vector2(
            baseLeft + baseWidth * monitorCenter.X,
            baseTop + baseHeight * monitorCenter.Y);
        const float maximumZoom = 1.65f;
        var focus = Math.Clamp(
            (_classicPcViewZoom - 1f) / (maximumZoom - 1f), 0, 1);
        var desiredCenter = Vector2.Lerp(
            originalCenter,
            new Vector2(containerWidth, containerHeight) * .5f,
            focus);
        var width = baseWidth * _classicPcViewZoom;
        var height = baseHeight * _classicPcViewZoom;
        return new Vector4(
            desiredCenter.X - width * monitorCenter.X,
            desiredCenter.Y - height * monitorCenter.Y,
            width,
            height);
    }

    private bool TryZoomClassicPcView(float wheelOffset)
    {
        if (!ClassicPcDisplayActive ||
            !IsClassicPcFurniturePointer() || wheelOffset == 0) return false;
        var requested = _classicPcTargetViewZoom *
                        MathF.Pow(1.14f, wheelOffset);
        _classicPcTargetViewZoom = Math.Clamp(requested, 1f, 1.65f);
        return true;
    }

    private void UpdateClassicPcViewZoom(double elapsedSeconds)
    {
        if (!ClassicPcDisplayActive)
        {
            _classicPcViewZoom = 1;
            _classicPcTargetViewZoom = 1;
            return;
        }
        var blend = 1f - MathF.Exp(
            -(float)Math.Max(0, elapsedSeconds) * 5.5f);
        _classicPcViewZoom +=
            (_classicPcTargetViewZoom - _classicPcViewZoom) * blend;
        if (MathF.Abs(_classicPcTargetViewZoom - _classicPcViewZoom) < .0001f)
            _classicPcViewZoom = _classicPcTargetViewZoom;
    }
}
