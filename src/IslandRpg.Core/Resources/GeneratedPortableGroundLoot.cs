using IslandRpg.Simulation;

namespace IslandRpg.Resources;

/// <summary>
/// Every locally generated portable ground item the dedicated-server pickup
/// path can resolve: inland sticks/rocks/seeds and coastal shells/seaweed.
/// Dropped and seeded objects stay on the regular world-object revision path.
/// </summary>
public static class GeneratedPortableGroundLoot
{
    /// <summary>
    /// Client command convention for loot that was generated locally and has
    /// never been published on the wire. Distinct from the public removal
    /// delta, which advertises previous revision 0 so observers that never
    /// saw the object can apply the tombstone.
    /// </summary>
    public const uint VirginCommandRevision = 1;

    /// <summary>
    /// Public previous revision for generated loot that was never a published
    /// world object. Clients treat a missing object as revision 0.
    /// </summary>
    public const uint UnpublishedObjectRevision = 0;

    public static IReadOnlyList<string> AllItemIds { get; } =
        ProceduralGroundLootCatalog.PortableItemIds
            .Concat(ProceduralCoastalLootCatalog.PortableItemIds)
            .ToArray();

    public static bool TryResolve(
        long worldSeed,
        WorldChunkKey chunk,
        Guid objectId,
        out ProceduralGroundLootCatalog.Placement placement)
    {
        if (ProceduralGroundLootCatalog.TryResolve(
                worldSeed, chunk, objectId, out placement))
            return true;
        if (!ProceduralCoastalLootCatalog.TryResolve(
                worldSeed, chunk, objectId, out var coastal))
            return false;
        placement = new(
            coastal.Id, coastal.ItemId, coastal.X, coastal.Y);
        return true;
    }
}
