using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private SpriteFrame? _campfireLightFrame;
    private int _campfireLightTexture;

    private void InitializeCampfireLighting()
    {
        _campfireLightFrame = CampfireLightSource.CreateFrame();
        _campfireLightTexture = Upload(_campfireLightFrame);
    }

    private float SceneDarkness() =>
        WorldLighting.Darkness(
            WorldTime.At(_worldGameSeconds).Daylight,
            _activeWorldLevel);

    private void RenderCampfireLights()
    {
        if (_campfireLightFrame is null ||
            _campfireLightTexture == 0)
            return;
        var darkness = SceneDarkness();
        if (darkness <= .04f) return;
        var scene = SceneClientBounds();
        var sceneScale = scene.Z / ReferenceWidth;
        var baseOpacity =
            CampfireLightSource.Opacity(_clock, darkness);
        foreach (var campfire in _worldChunks.Values
                     .Where(IsChunkVisible)
                     .SelectMany(gpu => gpu.Chunk.GroundObjects)
                     .Where(item =>
                         CampfireService.State(
                             item, _worldGameSeconds) ==
                         CampfireState.Lit))
        {
            var diameter =
                FiremakingSkill.LightRadiusPixels(
                    campfire.FiremakingLevel) *
                2 * _zoom * sceneScale;
            var opacity = Math.Clamp(
                baseOpacity *
                FiremakingSkill.LightIntensity(
                    campfire.FiremakingLevel),
                0,
                1);
            var referenceAnchor = SpriteAnchor(
                GroundObjectWorld(campfire));
            var anchor = new Vector2(
                scene.X + referenceAnchor.X * sceneScale,
                scene.Y + referenceAnchor.Y * sceneScale -
                12 * _zoom * sceneScale);
            DrawUiSprite(
                _campfireLightFrame,
                _campfireLightTexture,
                new(
                    anchor.X - diameter * .5f,
                    anchor.Y - diameter * .5f,
                    diameter,
                    diameter),
                drawOpacity: opacity);
        }
    }
}
