using FontStashSharp;
using IslandRpg.Assets;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float AssetLoadingShare = .72f;
    private const double LoadingCompleteHoldSeconds = .35;

    private int _loadingBackgroundTexture;
    private SpriteFrame? _loadingBackgroundFrame;
    private ScreenState _loadingDestination;
    private double _loadingCompletedAt = -1;

    private void PrepareLoadingScreen()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources", "Images", "loading-world.png");
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        _loadingBackgroundFrame = new SpriteFrame(
            image.Width, image.Height, 0, 0, image.Data);
        _loadingBackgroundTexture = Upload(
            image.Width, image.Height, image.Data);
        GL.BindTexture(TextureTarget.Texture2D, _loadingBackgroundTexture);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
    }

    private void CompleteLoading(ScreenState destination)
    {
        _done = _total;
        _current = "Ready to explore";
        _loadingDestination = destination;
        _loadingCompletedAt = _clock;
        _screen = ScreenState.LoadingComplete;
    }

    private bool FinishLoadingTransition()
    {
        if (_screen != ScreenState.LoadingComplete ||
            _clock - _loadingCompletedAt < LoadingCompleteHoldSeconds)
            return false;

        _screen = _loadingDestination;
        return true;
    }

    private float LoadingRatio() =>
        _screen switch
        {
            ScreenState.LoadingAssets =>
                AssetLoadingShare *
                Math.Clamp(_done / (float)Math.Max(1, _total), 0, 1),
            ScreenState.PreparingGpu =>
                AssetLoadingShare +
                (1 - AssetLoadingShare) *
                Math.Clamp(_done / (float)Math.Max(1, _total), 0, 1),
            ScreenState.LoadingComplete => 1,
            _ => 0
        };

    private void RenderLoading()
    {
        GL.ClearColor(.018f, .023f, .023f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
    }

    private void RenderLoadingUi()
    {
        var width = Math.Max(1, ClientSize.X);
        var height = Math.Max(1, ClientSize.Y);
        if (_loadingBackgroundFrame is not null &&
            _loadingBackgroundTexture != 0)
        {
            var sourceAspect = _loadingBackgroundFrame.Width /
                               (float)_loadingBackgroundFrame.Height;
            var targetAspect = width / (float)height;
            var uv = new Vector4(0, 0, 1, 1);
            if (targetAspect > sourceAspect)
            {
                var visibleHeight = sourceAspect / targetAspect;
                uv.Y = (1 - visibleHeight) * .5f;
                uv.W = visibleHeight;
            }
            else
            {
                var visibleWidth = targetAspect / sourceAspect;
                uv.X = (1 - visibleWidth) * .5f;
                uv.Z = visibleWidth;
            }
            DrawUiSprite(
                _loadingBackgroundFrame,
                _loadingBackgroundTexture,
                new(0, 0, width, height),
                uvRectangle: uv);
        }

        var bandHeight = Math.Clamp(height * .25f, 150, 210);
        var bandTop = height - bandHeight;
        DrawUiColor(
            new(0, 0, width, height),
            new(0, 0, 0, .14f));
        const int fadeSteps = 24;
        for (var step = 0; step < fadeSteps; step++)
        {
            var start = step / (float)fadeSteps;
            var end = (step + 1) / (float)fadeSteps;
            var stripTop = bandTop + bandHeight * start;
            var stripHeight = bandHeight * (end - start) + 1;
            var opacity = .18f + .54f * end * end;
            DrawUiColor(
                new(0, stripTop, width, stripHeight),
                new(.008f, .016f, .016f, opacity));
        }

        var margin = Math.Clamp(width * .06f, 28, 92);
        var contentWidth = width - margin * 2;
        var titleY = bandTop + 24;
        var titleWidth = _menuTitleFont?.MeasureString("ISLAND RPG").X ?? 210;
        DrawCenteredMenuTitle(
            "ISLAND RPG",
            new(margin, titleY - 8, titleWidth, 38),
            new FSColor(244, 236, 207, 255));

        var stage = _screen switch
        {
            ScreenState.LoadingAssets => "LOADING ASSETS",
            ScreenState.PreparingGpu => "PREPARING WORLD",
            _ => "READY"
        };
        var detail = _screen switch
        {
            ScreenState.LoadingAssets => "Gathering world data",
            ScreenState.PreparingGpu => "Building terrain and world graphics",
            _ => "Ready to explore"
        };
        var ratio = LoadingRatio();
        var percent = $"{(int)MathF.Round(ratio * 100)}%";
        var percentSize = _chatFont?.MeasureString(percent) ??
                          System.Numerics.Vector2.Zero;
        var statusY = titleY + 45;
        DrawUiText(
            stage,
            new(margin, statusY),
            new FSColor(204, 215, 209, 255));
        DrawUiText(
            percent,
            new(width - margin - percentSize.X, statusY),
            new FSColor(244, 236, 207, 255));

        var track = new Vector4(
            margin, statusY + 30, contentWidth, 8);
        DrawUiColor(track, new(.11f, .15f, .15f, 1));
        if (ratio > 0)
        {
            DrawUiColor(
                new(track.X, track.Y, track.Z * ratio, track.W),
                new(.82f, .58f, .22f, 1));
            DrawUiColor(
                new(track.X, track.Y, track.Z * ratio, 2),
                new(.96f, .78f, .39f, 1));
        }
        DrawUiText(
            detail,
            new(margin, track.Y + 20),
            new FSColor(164, 178, 171, 255));
    }
}
