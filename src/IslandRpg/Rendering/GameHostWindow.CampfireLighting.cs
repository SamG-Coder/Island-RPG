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
            active && (underground || darkness > .01f) ? 1 : 0);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneDarkness"),
            darkness);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "sceneUnderground"),
            underground ? 1 : 0);

        var count = 0;
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
    }
}
