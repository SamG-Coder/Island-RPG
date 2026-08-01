using IslandRpg.Gameplay;
using IslandRpg.Assets;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private CinematicSceneDirector? _sceneDirector;
    private bool _openingRevealInitialized;
    private SpriteFrame? _cinematicShipFrame;
    private int _cinematicShipTexture;
    private int _cinematicOceanProgram;

    private bool CinematicActive => _sceneDirector?.Active == true;

    private void StartOpeningCinematic()
    {
        _sceneDirector = new(
            duration: 14,
            shots:
            [
                new(0, 8, SceneCameraTarget.Actor,
                    Vector2.Zero, 1, 1, "wreck-ship"),
                new(8, 14, SceneCameraTarget.Player,
                    Vector2.Zero, 1.45f, .8f)
            ],
            cues:
            [
                new(1.7, "thunder"),
                new(4.4, "thunder"),
                new(6.65, "ship-impact")
            ]);
        _openingRevealInitialized = false;
        _sceneDirector.Start();
    }

    private void PrepareCinematicShip()
    {
        const int canvasWidth = 384;
        const int canvasHeight = 320;
        const int anchorX = canvasWidth / 2;
        const int anchorY = 282;
        var hull = _catalog!.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals(
                "COGXX_A0", StringComparison.OrdinalIgnoreCase));
        var sails = _catalog.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals(
                "SHIP_3BF", StringComparison.OrdinalIgnoreCase));
        if (hull is null || sails is null ||
            hull.Sprite.Frames.Count == 0 ||
            sails.Sprite.Frames.Count == 0)
            return;
        var hullIndex = Math.Min(4, hull.Sprite.Frames.Count - 1);
        var sailIndex = Math.Min(4, sails.Sprite.Frames.Count - 1);
        var pixels = new byte[canvasWidth * canvasHeight * 4];
        CompositeFishingBoatLayer(
            sails.Sprite.Frames[sailIndex], pixels,
            canvasWidth, canvasHeight, anchorX, anchorY);
        CompositeFishingBoatLayer(
            hull.Sprite.Frames[hullIndex], pixels,
            canvasWidth, canvasHeight, anchorX, anchorY);
        _cinematicShipFrame = new(
            canvasWidth, canvasHeight, anchorX, anchorY, pixels);
        _cinematicShipTexture = Upload(_cinematicShipFrame);
        _cinematicOceanProgram =
            GameShaderPrograms.CreateCinematicOceanProgram();
    }

    private bool UpdateCinematic(float elapsed)
    {
        if (_sceneDirector?.Active != true) return false;
        _sceneDirector.Advance(Math.Clamp(elapsed, 0, .1f));
        while (_sceneDirector.TryDequeueCue(out var cue))
            if (cue == "thunder") PlayGeneratedSound("thunder.wav");
            else PlaySoundCue(cue);
        if (_sceneDirector.Time >= 8 && _player is not null)
        {
            if (!_openingRevealInitialized)
            {
                _openingRevealInitialized = true;
                SetZoomImmediate(1.45f);
            }
            if (_sceneDirector.CurrentShot() is { } shot)
                SetZoomImmediate(_sceneDirector.CurrentZoom(shot));
            CenterCinematicCameraOnShore();
            StreamWorld();
        }
        if (!_sceneDirector.Active)
        {
            SetZoomImmediate(.8f);
            CenterCinematicCameraOnShore();
            _sceneDirector = null;
        }
        return true;
    }

    private void RenderCinematic()
    {
        if (_sceneDirector is not { Active: true } director) return;
        var width = Math.Max(1, ClientSize.X);
        var height = Math.Max(1, ClientSize.Y);
        var time = (float)director.Time;
        if (time < 8.5f)
        {
            DrawUiColor(new(0, 0, width, height),
                new(.018f, .035f, .075f, 1));
            RenderCinematicSea(width, height, time);
            RenderWreckShip(width, height, time);
            RenderSeaRock(width, height, time);
        }

        var flash = CinematicLightningIntensity();
        if (flash > 0)
            DrawUiColor(new(0, 0, width, height),
                new(.82f, .9f, 1, flash));

        var fade = time switch
        {
            < .7f => 1 - time / .7f,
            >= 6.7f and < 8.2f =>
                Math.Clamp((time - 6.7f) / .8f, 0, 1),
            >= 8.2f and < 10f =>
                1 - Math.Clamp((time - 8.2f) / 1.8f, 0, 1),
            _ => 0
        };
        if (fade > 0)
            DrawUiColor(new(0, 0, width, height),
                new(0, 0, 0, fade));

        var bar = MathF.Round(height * .12f);
        DrawUiColor(new(0, 0, width, bar), new(0, 0, 0, 1));
        DrawUiColor(new(0, height - bar, width, bar), new(0, 0, 0, 1));
    }

    private void RenderCinematicSea(int width, int height, float time)
    {
        var horizon = height * .36f;
        if (_cinematicOceanProgram == 0) return;
        _fontRenderer?.Flush();
        _uiColorBatch.Flush();
        var top = 1f - horizon * 2f / height;
        GL.UseProgram(_cinematicOceanProgram);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicOceanProgram, "terrain"), 0);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicOceanProgram, "waterNormals"), 1);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicOceanProgram, "time"), time);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicOceanProgram, "lightning"),
            Math.Max(LightningFlash(time, 1.7f),
                LightningFlash(time, 4.4f)));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2DArray, _terrainArray);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2DArray, _waterNormalArray);
        Draw([
            -1, top, 0, 0,
            -1, -1, 0, 1,
            1, -1, 1, 1,
            -1, top, 0, 0,
            1, -1, 1, 1,
            1, top, 1, 0
        ]);
    }

    private void RenderWreckShip(int width, int height, float time)
    {
        if (_cinematicShipFrame is not { } frame ||
            _cinematicShipTexture == 0) return;
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp(time / 7f, 0, 1));
        var shipWidth = Math.Min(width * .42f, frame.Width * 2.8f);
        var shipHeight = shipWidth * frame.Height /
            Math.Max(1, frame.Width);
        var x = -shipWidth + (width * .82f + shipWidth) * progress;
        var y = height * .48f - shipHeight * .62f +
                MathF.Sin(time * 2.2f) * 5;
        DrawUiSprite(frame, _cinematicShipTexture,
            new(x, y, shipWidth, shipHeight),
            brightness: -.18f,
            tint: new(.12f, .22f, .34f),
            tintAmount: .34f);
    }

    private void RenderSeaRock(int width, int height, float time)
    {
        if (time < 4.8f) return;
        var x = width * .79f;
        var water = height * .61f;
        DrawUiColor(new(x + 22, water - 64, 18, 64),
            new(.075f, .085f, .09f, 1));
        DrawUiColor(new(x + 10, water - 42, 44, 42),
            new(.065f, .075f, .08f, 1));
        DrawUiColor(new(x, water - 20, 66, 22),
            new(.055f, .065f, .07f, 1));
    }

    private static float LightningFlash(float time, float at)
    {
        var distance = MathF.Abs(time - at);
        if (distance > .16f) return 0;
        return (1 - distance / .16f) * .72f;
    }

    private float CinematicLightningIntensity()
    {
        if (_sceneDirector is not { Active: true } director) return 0;
        var time = (float)director.Time;
        return Math.Max(
            LightningFlash(time, 1.7f),
            LightningFlash(time, 4.4f));
    }

    private float? CinematicDarknessOverride()
    {
        if (_sceneDirector is not { Active: true } director) return null;
        if (director.Time < 10) return .9f;
        var morning = WorldLighting.Darkness(
            WorldTime.At(_worldGameSeconds).Daylight,
            _activeWorldLevel);
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((float)((director.Time - 10) / 4), 0, 1));
        return .9f + (morning - .9f) * progress;
    }

    private void CenterCinematicCameraOnShore()
    {
        if (_player is null) return;
        var terrain = SamplePlayerTerrain(
            _player.Position.X, _player.Position.Y);
        var projected = IsometricTerrainProjection.Project(
            _player.Position.X, _player.Position.Y, terrain.Height);
        _camera = -projected * _zoom;
    }
}
