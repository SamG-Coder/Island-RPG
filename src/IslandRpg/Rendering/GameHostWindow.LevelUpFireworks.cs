using IslandRpg.Gameplay;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void AddLevelUpParticle(
        string atlasKey, OpenTK.Mathematics.Vector2 world, float opacity) =>
        AddAtlasQuad(
            atlasKey, world, opacity,
            _worldRenderQueue.AtlasVertices);

    private void UpdateLevelUpFireworks(float elapsed)
    {
        _levelUpFireworks.Update(elapsed);
        if (_activePlayer is null) return;
        Span<int> levels = stackalloc int[9];
        CurrentSkillLevels(levels);
        if (!_skillLevelsObserved)
        {
            levels.CopyTo(_observedSkillLevels);
            _skillLevelsObserved = true;
            return;
        }
        var levelledUp = false;
        for (var index = 0; index < levels.Length; index++)
        {
            if (levels[index] > _observedSkillLevels[index])
                levelledUp = true;
            _observedSkillLevels[index] = levels[index];
        }
        if (!levelledUp || GetPlayerVisual() is not { } player)
            return;
        _levelUpFireworks.Burst(player.World);
    }

    private void CurrentSkillLevels(Span<int> levels)
    {
        levels[0] = SkillService.LevelForExperience(
            _activePlayer?.WoodcuttingExperience ?? 0);
        levels[1] = SkillService.LevelForExperience(
            _activePlayer?.FarmingExperience ?? 0);
        levels[2] = SkillService.LevelForExperience(
            _activePlayer?.CraftingExperience ?? 0);
        levels[3] = SkillService.LevelForExperience(
            _activePlayer?.FishingExperience ?? 0);
        levels[4] = SkillService.LevelForExperience(
            _activePlayer?.CookingExperience ?? 0);
        levels[5] = SkillService.LevelForExperience(
            _activePlayer?.FiremakingExperience ?? 0);
        levels[6] = SkillService.LevelForExperience(
            _activePlayer?.DiggingExperience ?? 0);
        levels[7] = SkillService.LevelForExperience(
            _activePlayer?.MiningExperience ?? 0);
        levels[8] = AdventureService.LevelForExperience(
            _activePlayer?.AdventureExperience ?? 0);
    }
}
