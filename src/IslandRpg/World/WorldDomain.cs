using IslandRpg.Gameplay;

namespace IslandRpg.World;

internal enum WorldLevel
{
    Underground = -1,
    Overworld = 0
}

internal readonly record struct ChunkCoordinate(
    int X,
    int Y,
    int Level = (int)WorldLevel.Overworld)
{
    public override string ToString() => $"{X},{Y}@{Level}";
}

internal enum TreeLifecycleState : byte
{
    Standing,
    Stump
}

internal sealed record WorldTreeInstance(
    Guid Id,
    int X,
    int Y,
    string TreeType,
    int Health,
    int MaxHealth,
    TreeLifecycleState State,
    int SticksRemaining = -1,
    int InitialStickCount = -1);

internal sealed record WorldGroundObject(
    Guid Id,
    string ItemId,
    float X,
    float Y,
    string? FuelItemId = null,
    double LitUntilGameSeconds = 0,
    int FiremakingLevel = 1,
    int Health = 0,
    int MaxHealth = 0,
    WorldContainerContents? Container = null,
    string? OwnerId = null,
    string? GroupOwnerId = null,
    int VisualFrame = -1,
    GateAccessState GateState = GateAccessState.Unlocked,
    string[]? ResidentIds = null);

internal sealed record WorldContainerContents(
    string?[] Items,
    int[] Quantities,
    string?[]? OwnerIds = null);
