using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal sealed record MiningNodeDefinition(
    string GraphicName,
    string DisplayName,
    int MaximumHealth,
    string? RewardItemId,
    int CompletionExperience);

internal static class MiningNodeCatalog
{
    public static bool TryGet(
        WorldVegetation vegetation,
        out MiningNodeDefinition definition)
    {
        if (!IslandRpg.Resources.UndergroundMiningCatalog.TryGetVisual(
                vegetation.GraphicName, out var visual))
        {
            definition = null!;
            return false;
        }
        definition = new(
            visual.GraphicName,
            visual.DisplayName,
            visual.MaximumHealth,
            visual.RewardItemId,
            visual.CompletionExperience);
        return true;
    }
}
