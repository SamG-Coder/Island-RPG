namespace IslandRpg.Rendering;

using IslandRpg.Resources;

/// <summary>
/// Mirrors the authority's placement lifecycle rule for sparse procedural
/// resources without mutating the client's replicated projection.
/// </summary>
internal static class NetworkResourceObstacleRules
{
    public static bool BlocksWorld(
        ResourceNodeKind kind,
        double regrowthGameSeconds,
        ResourceNodeSparseState? sparseState)
    {
        // Omitted sparse state means the canonical, live procedural default.
        if (sparseState is null) return true;

        // Replication validates sparse state before publishing it. Keep this
        // presentation path fail-closed if a malformed or mismatched value is
        // ever observed rather than predicting free placement through it.
        if (!double.IsFinite(regrowthGameSeconds) ||
            regrowthGameSeconds < 0 ||
            sparseState.Kind != kind ||
            !ResourceNodeStateRules.IsShapeValid(sparseState))
            return true;

        // Depleted health resources are permanent and release their space.
        // Remaining-backed fibre and berry nodes retain it while awaiting the
        // authoritative regrowth that will reuse the same procedural point.
        return !sparseState.Depleted || regrowthGameSeconds > 0;
    }
}
