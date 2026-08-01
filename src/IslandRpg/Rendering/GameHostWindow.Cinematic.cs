using IslandRpg.Gameplay;
using IslandRpg.Assets;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float LightningStrikeAt = 18f;
    private const float FireIgnitionAt = 20.5f;
    private const float FireSpreadAt = 22.5f;
    private const float ShipDestructionAt = 27f;
    private const float OpeningSeaEndsAt = 36f;
    private const float OpeningCinematicEndsAt = 43f;
    private const float CinematicWaterlineRatio = .61f;
    private CinematicSceneDirector? _sceneDirector;
    private bool _openingRevealInitialized;
    private SpriteFrame? _cinematicShipFrame;
    private int _cinematicShipTexture;
    private SpriteFrame[] _cinematicSinkingFrames = [];
    private int[] _cinematicSinkingTextures = [];
    private int _cinematicOceanProgram;
    private int _cinematicLightningProgram;
    private double[] _cinematicLightningTimes = [];
    private readonly UiSpriteLight[] _cinematicShipLights =
        new UiSpriteLight[6];
    private Vector3[] _hullFireAnchors =
    [
        // X/Y coordinates stay within the composite cog's visible hull band.
        // Delay values spread fire outward from the lightning contact point.
        new(.56f, .67f, 0),
        new(.48f, .69f, .65f),
        new(.64f, .70f, .95f),
        new(.39f, .72f, 1.35f),
        new(.72f, .73f, 1.7f)
    ];

    private bool CinematicActive => _sceneDirector?.Active == true;

    private void StartOpeningCinematic()
    {
        var stormCues = BuildStormCues(Random.Shared);
        _cinematicLightningTimes = stormCues
            .Where(value => value.Name == "thunder-flash")
            .Select(value => value.At).ToArray();
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
            cues: stormCues);
        _openingRevealInitialized = false;
        _sceneDirector.Start();
    }

    internal static SceneTimedCue[] BuildStormCues(Random random)
    {
        var cues = new List<SceneTimedCue>(16);
        var at = 1.4 + random.NextDouble() * 2.4;
        while (at < OpeningSeaEndsAt - 1)
        {
            if (Math.Abs(at - LightningStrikeAt) > 1.2)
            {
                var flash = cues.Count == 0 || random.NextDouble() < .55;
                cues.Add(new(at, flash ? "thunder-flash" : "thunder"));
                if (random.NextDouble() < .38)
                    cues.Add(new(
                        Math.Min(at + .12 + random.NextDouble() * .26,
                            OpeningSeaEndsAt - .1),
                        "thunder"));
            }
            at += 3.8 + random.NextDouble() * 2.3;
        }
        cues.Add(new(LightningStrikeAt, "ship-impact"));
        cues.Add(new(
            LightningStrikeAt + .6 + random.NextDouble() * .6,
            "thunder"));
        return cues.OrderBy(value => value.At).ToArray();
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
        _hullFireAnchors = FindHullFireAnchors(
            hull.Sprite.Frames[hullIndex], canvasWidth, canvasHeight,
            anchorX, anchorY);
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
        _cinematicLightningProgram =
            GameShaderPrograms.CreateCinematicLightningProgram();
    }

    private bool UpdateCinematic(float elapsed)
    {
        if (_sceneDirector?.Active != true) return false;
        _sceneDirector.Advance(Math.Clamp(elapsed, 0, .1f));
        while (_sceneDirector.TryDequeueCue(out var cue))
            if (cue.StartsWith("thunder", StringComparison.Ordinal))
                PlayGeneratedSound("thunder.wav");
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
            RenderCinematicLightningStrike(width, height, time);
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
            >= 35f and < 36.2f =>
                Math.Clamp((time - 35f) / 1.0f, 0, 1),
            >= 36.2f and < 38.4f =>
                1 - Math.Clamp((time - 36.2f) / 2.2f, 0, 1),
            _ => 0
        };
        if (fade > 0)
            DrawUiColor(new(0, 0, width, height),
                new(0, 0, 0, fade));

        var bar = MathF.Round(height * .12f);
        DrawUiColor(new(0, 0, width, bar), new(0, 0, 0, 1));
        DrawUiColor(new(0, height - bar, width, bar), new(0, 0, 0, 1));
        RenderOpeningCredits(width, height, time, bar);
        var loopFade = CinematicSceneLoopFade(
            time, width,
            _cinematicShipFrame is { } loopShip
                ? Math.Min(width * .42f, loopShip.Width * 2.8f) *
                  CinematicSeaZoom(time)
                : width * .42f);
        if (loopFade > 0)
            DrawUiColor(new(0, 0, width, height),
                new(0, 0, 0, loopFade));
    }

    private void RenderOpeningCredits(
        int width, int height, float time, float barHeight)
    {
        var (heading, detail, startsAt, endsAt) = time switch
        {
            < 7 => ("ISLAND RPG", "A survival story shaped by its people", 0f, 7f),
            < 16 => ("CREATED & DEVELOPED BY SAMG-CODER",
                "Island RPG v0.2.0", 7f, 16f),
            < 25 => ("DEVELOPED WITH OPENAI CODEX",
                "AI-assisted engineering, testing, and asset workflows", 16f, 25f),
            _ => ("BUILT WITH .NET 10 | OPENTK | FONTSTASHSHARP",
                "Compatible classic graphics are loaded locally", 25f, 36f)
        };
        var alpha = CreditOpacity(time, startsAt, endsAt);
        if (alpha <= 0) return;
        var y = height - barHeight + 12;
        DrawCenteredUiText(heading,
            new(0, y, width, 24),
            new(224, 211, 174, (int)(alpha * 255)));
        DrawCenteredUiText(detail,
            new(0, y + 27, width, 20),
            new(148, 158, 174, (int)(alpha * 220)));
    }

    internal static float CreditOpacity(
        float time, float startsAt, float endsAt)
    {
        if (time < startsAt || time >= endsAt) return 0;
        var fadeIn = Math.Clamp((time - startsAt) / .8f, 0, 1);
        var fadeOut = Math.Clamp((endsAt - time) / .8f, 0, 1);
        return Math.Min(fadeIn, fadeOut);
    }

    internal static float CinematicSeaZoom(float time)
    {
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - 31f) / 5f, 0, 1));
        return 1 + .45f * progress;
    }

    private void RenderCinematicLightningStrike(
        int width, int height, float time)
    {
        if (_cinematicLightningProgram == 0) return;
        var intensity = Math.Max(
            LightningStroke(time, LightningStrikeAt, .09f, 1f),
            LightningStroke(time, LightningStrikeAt + .12f, .07f, .55f));
        if (intensity <= 0) return;
        _fontRenderer?.Flush();
        _uiColorBatch.Flush();
        GL.UseProgram(_cinematicLightningProgram);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicLightningProgram, "time"), time);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicLightningProgram, "intensity"), intensity);
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicLightningProgram, "aspect"),
            width / (float)Math.Max(1, height));
        var strikeTarget = CinematicShipStrikeTarget(width, height, time);
        GL.Uniform2(GL.GetUniformLocation(
            _cinematicLightningProgram, "target"),
            strikeTarget.X, strikeTarget.Y);
        Draw([
            -1, 1, 0, 0, -1, -1, 0, 1,
            1, -1, 1, 1, 1, 1, 1, 0
        ]);
    }

    private Vector2 CinematicShipStrikeTarget(
        int width, int height, float time)
    {
        if (_cinematicShipFrame is not { } frame)
            return new(.5f, .6f);
        var shipWidth = Math.Min(width * .42f, frame.Width * 2.8f) *
                        CinematicSeaZoom(time);
        var shipHeight = shipWidth * frame.Height /
                         Math.Max(1, frame.Width);
        var diagonal = (1 - CinematicSceneDirector.SmoothStep(
            Math.Clamp(time / 16f, 0, 1))) * height * .065f;
        var waterline = height * CinematicWaterlineRatio + diagonal;
        var shipX = CinematicShipScreenX(time, width, shipWidth);
        var shipY = AnchoredSpriteTop(
                        waterline, shipHeight,
                        frame.HotspotY, frame.Height) +
                    shipHeight * .045f + SinkingOffset(time, shipHeight) +
                    MathF.Sin(time * 1.35f) * 4;
        var contact = _hullFireAnchors[0];
        return new(
            Math.Clamp((shipX + shipWidth * contact.X) /
                       Math.Max(1, width), 0, 1),
            Math.Clamp((shipY + shipHeight * contact.Y + 50) /
                       Math.Max(1, height), 0, 1));
    }

    private void RenderCinematicSea(int width, int height, float time)
    {
        const float horizon = 0;
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
        GL.Uniform1(GL.GetUniformLocation(
            _cinematicOceanProgram, "cameraZoom"),
            CinematicSeaZoom(time));
        var shake = CinematicCameraShake(time);
        var shipWidth = _cinematicShipFrame is { } ship
            ? Math.Min(width * .42f, ship.Width * 2.8f) *
              CinematicSeaZoom(time)
            : width * .42f * CinematicSeaZoom(time);
        var oceanTravel = new Vector2(
            time * .018f,
            time * .003f);
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
            1, top, 1, 0
        ]);
    }

    private void RenderWreckShip(int width, int height, float time)
    {
        if (_cinematicShipFrame is not { } frame ||
            _cinematicShipTexture == 0) return;
        var shipWidth = Math.Min(width * .42f, frame.Width * 2.8f) *
                        CinematicSeaZoom(time);
        var shipHeight = shipWidth * frame.Height /
            Math.Max(1, frame.Width);
        var shake = CinematicCameraShake(time);
        var x = CinematicShipScreenX(
            time, width, shipWidth) + shake.X;
        var diagonal = (1 - CinematicSceneDirector.SmoothStep(
            Math.Clamp(time / 16f, 0, 1))) * height * .065f;
        var waterline = height * CinematicWaterlineRatio + diagonal;
        var sinking = SinkingOffset(time, shipHeight);
        var y = AnchoredSpriteTop(
                    waterline, shipHeight, frame.HotspotY, frame.Height) +
                shipHeight * .045f + sinking +
                MathF.Sin(time * 1.35f) * 4 + shake.Y;
        var fire = CinematicShipFire(time) * CinematicFireVisibility(time);
        UpdateCinematicShipLights(time, fire, 50 / Math.Max(1, shipHeight));
        var wreckProgress = Math.Clamp(
            (time - ShipDestructionAt) / 8f, 0, 1);
        var wreckBlend = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - ShipDestructionAt) / 1.1f, 0, 1));
        var wreckComplete = wreckProgress >= 1;
        if (!wreckComplete)
        {
            DrawUiSprite(frame, _cinematicShipTexture,
                new(x, y, shipWidth, shipHeight),
                brightness: -.58f,
                tint: new(.018f, .035f, .075f),
                tintAmount: .64f,
                sceneDarkness: .96f,
                localLights: _cinematicShipLights,
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
                    localLights: _cinematicShipLights);
            }
            if (fire > .02f)
                RenderCinematicShipFires(
                    x, y, shipWidth, shipHeight, fire, time);
        }
        RenderCinematicEmbers(x, y, shipWidth, shipHeight, time);
    }

    internal static float AnchoredSpriteTop(
        float waterline, float displayHeight, int hotspotY, int frameHeight) =>
        waterline - displayHeight * hotspotY / Math.Max(1, frameHeight);

    internal static float SinkingOffset(float time, float shipHeight)
    {
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - ShipDestructionAt) / 8f, 0, 1));
        return shipHeight * .03f * progress;
    }

    internal static int SinkingFrameIndex(float progress, int frameCount) =>
        frameCount <= 1 ? 0 : Math.Clamp(
            (int)(Math.Clamp(progress, 0, 1) * frameCount),
            0, frameCount - 1);

    internal static float ShipTrackedTravel(float time)
    {
        const float speed = .075f;
        if (time <= 0) return 0;
        var result = Math.Min(time, FireIgnitionAt) * speed;
        if (time <= FireIgnitionAt) return result;
        var burningFor = time - FireIgnitionAt;
        // Fire and storm damage steadily rob the vessel of momentum. Keeping
        // the velocity continuous avoids the old visible stop at impact.
        return result + speed * (1 - MathF.Exp(-burningFor * .34f)) / .34f;
    }

    internal static float CinematicShipScreenX(
        float time, float viewportWidth, float shipWidth)
    {
        viewportWidth = Math.Max(1, viewportWidth);
        var spawnX = -shipWidth;
        var distanceToCenter = viewportWidth * .75f + shipWidth * .5f;
        var cycle = Math.Max(1, distanceToCenter);
        if (time < FireIgnitionAt)
            return spawnX + ShipTrackedTravel(time) * viewportWidth % cycle;
        var ignitionTravel = FireIgnitionAt * .075f * viewportWidth;
        var ignitionPhase = ignitionTravel % cycle;
        var burningTravel = (ShipTrackedTravel(time) -
                             FireIgnitionAt * .075f) * viewportWidth;
        return spawnX + ignitionPhase + burningTravel;
    }

    internal static float CinematicSceneLoopFade(
        float time, float viewportWidth, float shipWidth)
    {
        if (time <= 0 || time >= FireIgnitionAt) return 0;
        var cycle = Math.Max(1, viewportWidth * .75f + shipWidth * .5f);
        var travelled = ShipTrackedTravel(time) * Math.Max(1, viewportWidth);
        var completed = (int)MathF.Floor(travelled / cycle);
        var phase = travelled % cycle;
        var fadeDistance = cycle * .07f;
        if (phase >= cycle - fadeDistance)
            return CinematicSceneDirector.SmoothStep(
                (phase - cycle + fadeDistance) / fadeDistance);
        if (completed > 0 && phase < fadeDistance)
            return 1 - CinematicSceneDirector.SmoothStep(
                phase / fadeDistance);
        return 0;
    }

    private static float CinematicShipFire(float time)
    {
        if (time <= FireIgnitionAt) return 0;
        var growth = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - FireIgnitionAt) / 2.2f, 0, 1));
        return growth * (.78f + MathF.Sin(time * 17f) * .12f +
                         MathF.Sin(time * 31f) * .07f);
    }

    internal static float CinematicFireVisibility(float time) =>
        1 - CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - 33f) / 3f, 0, 1));

    private void UpdateCinematicShipLights(
        float time, float fire, float verticalOffset)
    {
        var lightning = CinematicLightningAt(time);
        _cinematicShipLights[0] = new(
            new(.5f, .58f), new(.9f, .8f),
            new(.72f, .84f, 1f), lightning * 1.5f);
        var flicker = CampfireLightSource.Opacity(_clock, .96f) *
                      FiremakingSkill.LightIntensity(1);
        var fires = _hullFireAnchors;
        for (var index = 0; index < fires.Length; index++)
        {
            var point = fires[index];
            var growth = CinematicSceneDirector.SmoothStep(Math.Clamp(
                (time - FireIgnitionAt - point.Z) / 1.15f, 0, 1));
            _cinematicShipLights[index + 1] = new(
                new(point.X, Math.Clamp(point.Y + verticalOffset, 0, 1)),
                new(.30f, .24f),
                new(1f, .56f, .22f), fire * growth * flicker);
        }
    }

    private void RenderCinematicShipFires(
        float shipX, float shipY, float shipWidth, float shipHeight,
        float strength, float time)
    {
        var fires = _hullFireAnchors;
        for (var index = 0; index < fires.Length; index++)
        {
            var point = fires[index];
            var growth = CinematicSceneDirector.SmoothStep(Math.Clamp(
                (time - FireIgnitionAt - point.Z) / 1.15f, 0, 1));
            if (growth <= 0) continue;
            var animationFrame = CampfireService.AnimationFrame(
                _clock + index * .43);
            if (!_placeableObjectSprites.TryGetCampfireFlame(
                    animationFrame, out var flame))
                continue;
            var flameWidth = shipWidth * (.09f + growth * .035f);
            var flameHeight = flameWidth * flame.Frame.Height /
                              Math.Max(1, flame.Frame.Width);
            DrawUiSprite(flame.Frame, flame.Texture,
                new(shipX + shipWidth * point.X - flameWidth * .5f,
                    shipY + shipHeight * point.Y - flameHeight * .72f + 50,
                    flameWidth, flameHeight),
                brightness: .16f,
                drawOpacity: strength * growth);
        }
    }

    internal static Vector3[] FindHullFireAnchors(
        SpriteFrame hull, int canvasWidth, int canvasHeight,
        int anchorX, int anchorY)
    {
        ReadOnlySpan<(float X, float Delay)> samples =
        [
            (.5f, 0),
            (.35f, .65f),
            (.65f, .95f),
            (.2f, 1.35f),
            (.8f, 1.7f)
        ];
        var minX = hull.Width;
        var maxX = -1;
        for (var y = 0; y < hull.Height; y++)
        for (var x = 0; x < hull.Width; x++)
        {
            if (hull.Rgba[(y * hull.Width + x) * 4 + 3] <= 48) continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
        }
        if (maxX < minX)
            return
            [
                new(.5f, .67f, 0), new(.35f, .69f, 2),
                new(.65f, .69f, 2.35f), new(.2f, .72f, 2.85f),
                new(.8f, .72f, 3.15f)
            ];

        var result = new Vector3[samples.Length];
        var layerLeft = anchorX - hull.HotspotX;
        var layerTop = anchorY - hull.HotspotY;
        for (var index = 0; index < samples.Length; index++)
        {
            var targetX = minX + (int)MathF.Round(
                (maxX - minX) * samples[index].X);
            var foundX = targetX;
            var foundY = hull.Height - 1;
            var found = false;
            for (var radius = 0; radius <= 8 && !found; radius++)
            {
                var count = radius == 0 ? 1 : 2;
                for (var directionIndex = 0;
                     directionIndex < count && !found; directionIndex++)
                {
                    var direction = radius == 0
                        ? 0
                        : directionIndex * 2 - 1;
                    var x = Math.Clamp(targetX + radius * direction,
                        0, hull.Width - 1);
                    for (var y = 0; y < hull.Height; y++)
                        if (hull.Rgba[(y * hull.Width + x) * 4 + 3] > 48)
                        {
                            foundX = x;
                            foundY = y;
                            found = true;
                            break;
                        }
                }
            }
            result[index] = new(
                Math.Clamp((layerLeft + foundX) /
                           (float)Math.Max(1, canvasWidth), 0, 1),
                Math.Clamp((layerTop + foundY) /
                           (float)Math.Max(1, canvasHeight), 0, 1),
                samples[index].Delay);
        }
        return result;
    }

    private void RenderCinematicEmbers(
        float shipX, float shipY, float shipWidth, float shipHeight, float time)
    {
        var appear = CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - 32.5f) / .8f, 0, 1));
        var fade = 1 - CinematicSceneDirector.SmoothStep(
            Math.Clamp((time - 35.2f) / .8f, 0, 1));
        var opacity = appear * fade;
        if (opacity <= .01f) return;
        for (var index = 0; index < 14; index++)
        {
            var phase = time * (1.4f + index * .037f) + index * 2.17f;
            var rise = ((time - 32.5f) * (13 + index % 4 * 3) +
                        index * 7) % 62;
            var x = shipX + shipWidth * (.38f + index % 7 * .048f) +
                    MathF.Sin(phase) * (5 + index % 3 * 2);
            var y = shipY + shipHeight * .72f - rise;
            var size = 2f + index % 3;
            DrawUiColor(new(x - size * 1.8f, y - size * 1.8f,
                    size * 3.6f, size * 3.6f),
                new(1f, .2f, .025f, opacity * .16f));
            DrawUiColor(new(x, y, size, size),
                new(1f, .62f, .12f, opacity * .85f));
        }
    }

    private static Vector2 CinematicCameraShake(float time)
    {
        var storm = new Vector2(
            MathF.Sin(time * 7.1f) * 1.8f + MathF.Sin(time * 2.3f) * 1.2f,
            MathF.Sin(time * 8.7f) * 1.4f);
        var impactAge = time - LightningStrikeAt;
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

    private float CinematicLightningAt(float time)
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
        var ignition = Math.Max(
            LightningStroke(time, LightningStrikeAt, .05f, 1f),
            LightningStroke(time, LightningStrikeAt + .12f, .04f, .62f));
        var dynamicFlash = 0f;
        foreach (var at in _cinematicLightningTimes)
            dynamicFlash = Math.Max(dynamicFlash,
                LightningStroke(time, (float)at, .07f, .82f));
        return Math.Max(dynamicFlash,
            Math.Max(first, Math.Max(second, ignition)));
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
        if (director.Time < 38.4) return .9f;
        var morning = WorldLighting.Darkness(
            WorldTime.At(_worldGameSeconds).Daylight,
            _activeWorldLevel);
        var progress = CinematicSceneDirector.SmoothStep(
            Math.Clamp((float)((director.Time - 38.4) / 4.6), 0, 1));
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
