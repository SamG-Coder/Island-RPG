using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const int MaximumSceneLights = 16;
    private static readonly string[] LocalLightUvUniforms =
        Enumerable.Range(0, MaximumSceneLights)
            .Select(index => $"localLightUv[{index}]").ToArray();
    private static readonly string[] LocalLightRadiusUniforms =
        Enumerable.Range(0, MaximumSceneLights)
            .Select(index => $"localLightRadius[{index}]").ToArray();
    private static readonly string[] LocalLightColorUniforms =
        Enumerable.Range(0, MaximumSceneLights)
            .Select(index => $"localLightColor[{index}]").ToArray();
    private static readonly string[] LocalLightIntensityUniforms =
        Enumerable.Range(0, MaximumSceneLights)
            .Select(index => $"localLightIntensity[{index}]").ToArray();

    private float SceneDarkness() =>
        CinematicDarknessOverride() ??
        WorldLighting.Darkness(
            WorldTime.At(_worldGameSeconds).Daylight,
            _activeWorldLevel);

    private void UploadSceneLighting()
    {
        var active =
            _screen == ScreenState.WorldPreview &&
            _mode == PreviewMode.Game &&
            !_atlasOpen;
        var underground =
            _activeWorldLevel == (int)WorldLevel.Underground;
        var darkness = active ? SceneDarkness() : 0f;
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneLighting"),
            active && (underground || darkness > .01f ||
                       _levelUpFireworks.Active ||
                       _slimeAttackEffects.Active) ? 1 : 0);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneDarkness"),
            darkness);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneUnderground"),
            underground ? 1 : 0);
        UploadUnlimitedZoomFog(active);

        var count = 0;
        var lightning = CinematicLightningIntensity();
        if (active && lightning > .01f)
            AddLight(
                new(ReferenceWidth * .5f, ReferenceHeight * .5f),
                new(1.2f, 1.2f),
                new(.72f, .84f, 1f),
                lightning);
        if (active && _levelUpFireworks.Active)
        {
            var radius = 92f * _zoom;
            AddLight(
                SpriteAnchor(_levelUpFireworks.LightWorld),
                new(
                    radius / ReferenceWidth,
                    radius * .78f / ReferenceHeight),
                _levelUpFireworks.LightColor,
                _levelUpFireworks.LightIntensity);
        }
        if (active && _slimeAttackEffects.Active)
        {
            foreach (var light in _slimeAttackEffects.Lights())
            {
                if (count >= MaximumSceneLights) break;
                var radius = light.RadiusPixels * _zoom;
                AddLight(
                    SpriteAnchor(light.World),
                    new(
                        radius / ReferenceWidth,
                        radius * .72f / ReferenceHeight),
                    light.Color,
                    light.Intensity);
            }
        }
        if (active && underground && _player is not null)
        {
            var player = GetPlayerVisual();
            var anchor = player is null
                ? new Vector2(ReferenceWidth * .5f, ReferenceHeight * .5f)
                : SpriteAnchor(player.World);
            AddLight(
                anchor,
                new(.175f * _zoom, .115f * _zoom),
                new(.96f, .98f, 1f),
                .92f);
            if (_caveEntranceLightWorld is { } entrance)
            {
                AddLight(
                    SpriteAnchor(GroundObjectWorld(new(
                        Guid.Empty, ItemIds.CaveHole,
                        entrance.X, entrance.Y))),
                    new(.11f * _zoom, .075f * _zoom),
                    new(.96f, .98f, 1f),
                    .88f);
            }
        }

        if (active && (underground || darkness > .04f))
        {
            if (_activePlayer?.Inventory.Contains(
                    ItemIds.PortableTorch) == true && _player is not null)
            {
                var visual = GetPlayerVisual();
                AddTorchLight(visual is null
                    ? new(ReferenceWidth * .5f, ReferenceHeight * .5f)
                    : SpriteAnchor(visual.World));
            }
            foreach (var villager in _villagers)
            {
                if (count >= MaximumSceneLights) break;
                if (villager.Health <= 0 ||
                    villager.WorldLevel != _activeWorldLevel ||
                    !villager.Inventory.Contains(ItemIds.PortableTorch))
                    continue;
                AddTorchLight(SpriteAnchor(new(
                    villager.PositionX, villager.PositionY)));
            }
        }

        if (active && darkness > .04f)
        {
            var flicker = CampfireLightSource.Opacity(_clock, darkness);
            foreach (var gpu in _worldChunks.Values)
            {
                if (count >= MaximumSceneLights ||
                    gpu.Chunk.Coordinate.Level != _activeWorldLevel ||
                    !IsChunkVisible(gpu))
                    continue;
                foreach (var campfire in gpu.Chunk.GroundObjects)
                {
                    if (count >= MaximumSceneLights) break;
                    if (CampfireService.State(
                            campfire, _worldGameSeconds) !=
                        CampfireState.Lit)
                        continue;
                    var radius = FiremakingSkill.LightRadiusPixels(
                        campfire.FiremakingLevel) * _zoom;
                    var anchor = SpriteAnchor(GroundObjectWorld(campfire));
                    anchor.Y -= 12 * _zoom;
                    AddLight(
                        anchor,
                        new(
                            radius / ReferenceWidth,
                            radius * .72f / ReferenceHeight),
                        new(1f, .56f, .22f),
                        flicker * FiremakingSkill.LightIntensity(
                            campfire.FiremakingLevel));
                }
            }
        }

        GL.Uniform1(
            _shaderUniforms.Get(_program, "localLightCount"),
            count);
        return;

        void AddLight(
            Vector2 anchor,
            Vector2 radiusUv,
            Vector3 color,
            float intensity)
        {
            if (count >= MaximumSceneLights) return;
            GL.Uniform2(
                _shaderUniforms.Get(
                    _program, LocalLightUvUniforms[count]),
                anchor.X / ReferenceWidth,
                1f - anchor.Y / ReferenceHeight);
            GL.Uniform2(
                _shaderUniforms.Get(
                    _program, LocalLightRadiusUniforms[count]),
                radiusUv);
            GL.Uniform3(
                _shaderUniforms.Get(
                    _program, LocalLightColorUniforms[count]),
                color);
            GL.Uniform1(
                _shaderUniforms.Get(
                    _program, LocalLightIntensityUniforms[count]),
                intensity);
            count++;
        }


        void AddTorchLight(Vector2 anchor)
        {
            var flicker = .92f + MathF.Sin((float)_clock * 9f) * .06f;
            AddLight(
                anchor - new Vector2(0, 12 * _zoom),
                new(.105f * _zoom, .072f * _zoom),
                new(1f, .62f, .28f),
                flicker);
        }
    }

    private void UploadUnlimitedZoomFog(bool active)
    {
        var fogAmount = UnlimitedZoomFogPolicy.Amount(
            active,
            _unlimitedZoomToggle.IsChecked,
            _zoomScaledLoadingToggle.IsChecked,
            _player is not null,
            _zoom);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneFogAmount"),
            fogAmount);
        if (fogAmount <= 0) return;

        var player = GetPlayerVisual();
        var anchor = player is null
            ? new Vector2(ReferenceWidth * .5f, ReferenceHeight * .5f)
            : SpriteAnchor(player.World);
        GL.Uniform2(
            _shaderUniforms.Get(_program, "sceneFogCenter"),
            anchor.X / ReferenceWidth,
            1f - anchor.Y / ReferenceHeight);
        const float visibleTileRadius = 80f;
        var radiusX = visibleTileRadius * 48f *
                      MathF.Sqrt(2f) * _zoom;
        var radiusY = visibleTileRadius * 24f *
                      MathF.Sqrt(2f) * _zoom;
        GL.Uniform2(
            _shaderUniforms.Get(_program, "sceneFogRadius"),
            radiusX / ReferenceWidth,
            radiusY / ReferenceHeight);
    }
}

internal static class UnlimitedZoomFogPolicy
{
    public static float Amount(
        bool sceneActive,
        bool unlimitedZoom,
        bool zoomScaledLoading,
        bool hasPlayer,
        float zoom) =>
        sceneActive && unlimitedZoom && !zoomScaledLoading && hasPlayer
            ? Math.Clamp((.22f - zoom) / .04f, 0, 1)
            : 0;
}
