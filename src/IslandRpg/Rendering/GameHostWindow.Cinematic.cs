using IslandRpg.Gameplay;
using IslandRpg.Assets;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float ShipImpactAt = 14.2f;
    private const float OpeningSeaEndsAt = 20f;
    private const float OpeningCinematicEndsAt = 27f;
    private const float CinematicWaterlineRatio = .61f;
    private CinematicSceneDirector? _sceneDirector;
    private bool _openingRevealInitialized;
    private SpriteFrame? _cinematicShipFrame;
    private int _cinematicShipTexture;
    private SpriteFrame[] _cinematicSinkingFrames = [];
    private int[] _cinematicSinkingTextures = [];
    private int _cinematicOceanProgram;

    private bool CinematicActive => _sceneDirector?.Active == true;

    private void StartOpeningCinematic()
    {
        _sceneDirector = new(
            duration: OpeningCinematicEndsAt,
            shots:
            [
                new(0, OpeningSeaEndsAt, SceneCameraTarget.Actor,
                    Vector2.Zero, 1, 1, "wreck-ship"),
                new(OpeningSeaEndsAt, OpeningCinematicEndsAt,
                    SceneCameraTarget.Player,
                    Vector2.Zero, 1.45f, .8f)
            ],
            cues:
            [
                // Light arrives first. These delays place the strikes at
                // roughly 250 m and 500 m using 3 seconds per kilometre.
                new(2.75, "thunder"),
                new(7.8, "thunder"),
                new(ShipImpactAt, "ship-impact")
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
                "COGX_1H", StringComparison.OrdinalIgnoreCase));
        var sails = _catalog.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals(
                "SHIP_3BF", StringComparison.OrdinalIgnoreCase));
        var sinking = _catalog.Graphics.Values.FirstOrDefault(value =>
            value.Definition.Name.Equals(
                "COGXX_DN", StringComparison.OrdinalIgnoreCase));
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
        if (sinking is not null)
        {
            _cinematicSinkingFrames = sinking.Sprite.Frames.ToArray();
            _cinematicSinkingTextures = _cinematicSinkingFrames
                .Select(Upload).ToArray();
        }
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
        if (_sceneDirector.Time >= OpeningSeaEndsAt && _player is not null)
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
        if (time < OpeningSeaEndsAt + .5f)
        {
            DrawUiColor(new(0, 0, width, height),
                new(.018f, .035f, .075f, 1));
            RenderCinematicSea(width, height, time);
            RenderWreckShip(width, height, time);
            // Storm ambient exposure applies to every authored sprite as well
            // as the ocean; lightning is composited afterwards so it can
            // briefly reveal the ship and reef.
            DrawUiColor(new(0, 0, width, height),
                new(.005f, .012f, .028f, .3f));
        }

        var flash = CinematicLightningIntensity();
        if (flash > 0)
            DrawUiColor(new(0, 0, width, height),
                new(.82f, .9f, 1, flash));

        var fade = time switch
        {
            < .7f => 1 - time / .7f,
            >= 19f and < 20.2f =>
                Math.Clamp((time - 19f) / 1.0f, 0, 1),
            >= 20.2f and < 22.4f =>
                1 - Math.Clamp((time - 20.2f) / 2.2f, 0, 1),
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
            CinematicLightningAt(time));
        var shake = CinematicCameraShake(time);
        var oceanTravel = new Vector2(time * .018f, time * .003f);
        GL.Uniform2(GL.GetUniformLocation(
            _cinematicOceanProgram, "cameraOffset"),
            oceanTravel.X + shake.X / Math.Max(1, width),
            oceanTravel.Y + shake.Y / Math.Max(1, height));
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
        var shipWidth = Math.Min(width * .42f, frame.Width * 2.8f);
        var shipHeight = shipWidth * frame.Height /
            Math.Max(1, frame.Width);
        // The ship enters the frame, then the virtual camera tracks it while
        // the rock and ocean move relative to the vessel.
        var entry = CinematicSceneDirector.SmoothStep(
            Math.Clamp(time / 9.5f, 0, 1));
        var shake = CinematicCameraShake(time);
        var trackedX = (width - shipWidth) * .5f +
                       Math.Max(0, time - 9.5f) * width * .006f;
        var x = -shipWidth + (trackedX + shipWidth) * entry + shake.X;
        var waterline = height * CinematicWaterlineRatio;
        var sinking = SinkingOffset(time, shipHeight);
        var y = AnchoredSpriteTop(
                    waterline, shipHeight, frame.HotspotY, frame.Height) +
                shipHeight * .045f + sinking +
                MathF.Sin(time * 1.35f) * 4 + shake.Y;
        var fire = CinematicShipFire(time);
        var wreckProgress = Math.Clamp(
            (time - ShipImpactAt) / 5.4f, 0, 1);
        var wreckBlend = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - ShipImpactAt) / .65f, 0, 1));
        DrawUiSprite(frame, _cinematicShipTexture,
            new(x, y, shipWidth, shipHeight),
            brightness: -.58f,
            tint: new(.018f, .035f, .075f),
            tintAmount: .64f,
            sceneDarkness: .96f,
            localLightUv: new(.58f, .66f),
            localLightRadius: new(.34f, .26f),
            localLightColor: new(1f, .34f, .07f),
            localLightIntensity: fire * 1.35f,
            drawOpacity: 1 - wreckBlend);
        if (wreckBlend > 0 && _cinematicSinkingFrames.Length > 0)
        {
            var index = SinkingFrameIndex(
                wreckProgress, _cinematicSinkingFrames.Length);
            var wreck = _cinematicSinkingFrames[index];
            var scale = shipWidth / frame.Width;
            var anchorX = x + frame.HotspotX * scale;
            var anchorY = y + frame.HotspotY * scale;
            var wreckWidth = wreck.Width * scale;
            var wreckHeight = wreck.Height * scale;
            DrawUiSprite(wreck, _cinematicSinkingTextures[index],
                new(anchorX - wreck.HotspotX * scale,
                    anchorY - wreck.HotspotY * scale + sinking,
                    wreckWidth, wreckHeight),
                brightness: -.58f,
                tint: new(.018f, .035f, .075f), tintAmount: .64f,
                drawOpacity: wreckBlend,
                sceneDarkness: .96f,
                localLightUv: new(.52f, .58f),
                localLightRadius: new(.42f, .34f),
                localLightColor: new(1f, .3f, .055f),
                localLightIntensity: fire * 1.25f);
        }
        if (fire > .02f)
            RenderCinematicShipFire(
                x + shipWidth * .58f,
                y + shipHeight * .67f,
                shipWidth, fire, time);
    }

    internal static float AnchoredSpriteTop(
        float waterline, float displayHeight, int hotspotY, int frameHeight) =>
        waterline - displayHeight * hotspotY / Math.Max(1, frameHeight);

    internal static float SinkingOffset(float time, float shipHeight)
    {
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - ShipImpactAt) / 5.4f, 0, 1));
        return shipHeight * .03f * progress;
    }

    internal static int SinkingFrameIndex(float progress, int frameCount) =>
        frameCount <= 1 ? 0 : Math.Clamp(
            (int)(Math.Clamp(progress, 0, 1) * frameCount),
            0, frameCount - 1);

    private static float CinematicShipFire(float time)
    {
        if (time <= ShipImpactAt) return 0;
        var growth = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - ShipImpactAt) / 2.2f, 0, 1));
        return growth * (.78f + MathF.Sin(time * 17f) * .12f +
                         MathF.Sin(time * 31f) * .07f);
    }

    private void RenderCinematicShipFire(
        float centerX, float deckY, float shipWidth, float strength, float time)
    {
        var scale = shipWidth * (.026f + strength * .018f);
        var flicker = MathF.Sin(time * 23f) * scale * .18f;
        DrawUiColor(new(centerX - scale * .75f, deckY - scale * 2.3f - flicker,
                scale * 1.5f, scale * 2.5f),
            new(1f, .18f, .025f, strength * .7f));
        DrawUiColor(new(centerX - scale * .42f, deckY - scale * 1.8f + flicker,
                scale * .84f, scale * 1.85f),
            new(1f, .68f, .08f, strength * .9f));
    }

    private static Vector2 CinematicCameraShake(float time)
    {
        var storm = new Vector2(
            MathF.Sin(time * 7.1f) * 1.8f + MathF.Sin(time * 2.3f) * 1.2f,
            MathF.Sin(time * 8.7f) * 1.4f);
        var impactAge = time - ShipImpactAt;
        if (impactAge is < 0 or > 1.4f) return storm;
        var strength = (1 - impactAge / 1.4f) * 11f;
        return storm + new Vector2(
            MathF.Sin(impactAge * 54f) * strength,
            MathF.Cos(impactAge * 43f) * strength * .65f);
    }

    private static float LightningStroke(float time, float at, float width,
        float strength)
    {
        var distance = MathF.Abs(time - at);
        if (distance > width) return 0;
        return (1 - distance / width) * strength;
    }

    private static float CinematicLightningAt(float time)
    {
        // Real flashes commonly contain several return strokes. The narrow
        // white pulse carries the brightness; its smaller echoes create the
        // irregular flicker perceived by the eye.
        var first = Math.Max(
            LightningStroke(time, 2f, .045f, .98f),
            Math.Max(LightningStroke(time, 2.09f, .035f, .58f),
                LightningStroke(time, 2.18f, .05f, .34f)));
        var second = Math.Max(
            LightningStroke(time, 6.3f, .055f, 1f),
            LightningStroke(time, 6.42f, .045f, .48f));
        return Math.Max(first, second);
    }

    private float CinematicLightningIntensity()
    {
        if (_sceneDirector is not { Active: true } director) return 0;
        var time = (float)director.Time;
        return CinematicLightningAt(time);
    }

    private float? CinematicDarknessOverride()
    {
        if (_sceneDirector is not { Active: true } director) return null;
        if (director.Time < 22.4) return .9f;
        var morning = WorldLighting.Darkness(
            WorldTime.At(_worldGameSeconds).Daylight,
            _activeWorldLevel);
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((float)((director.Time - 22.4) / 4.6), 0, 1));
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
