namespace IslandRpg.Gameplay;

using IslandRpg.World;

internal static class GateService
{
    public static bool IsOpen(WorldGroundObject value) =>
        GateCatalog.IsGate(value.ItemId) &&
        value.GateState == GateAccessState.Opened &&
        !ConstructionService.IsConstructionSite(value);

    public static bool TryOpen(
        WorldGroundObject value, out WorldGroundObject updated)
    {
        updated = value;
        if (!GateCatalog.IsGate(value.ItemId) ||
            ConstructionService.IsConstructionSite(value) ||
            value.GateState != GateAccessState.Unlocked)
            return false;
        updated = value with { GateState = GateAccessState.Opened };
        return true;
    }

    public static bool TryClose(
        WorldGroundObject value, out WorldGroundObject updated)
    {
        updated = value;
        if (!IsOpen(value)) return false;
        updated = value with { GateState = GateAccessState.Unlocked };
        return true;
    }

    public static bool TryLock(
        WorldGroundObject value, bool canManage,
        out WorldGroundObject updated)
    {
        updated = value;
        if (!canManage || !GateCatalog.IsGate(value.ItemId) ||
            ConstructionService.IsConstructionSite(value) ||
            value.GateState != GateAccessState.Unlocked)
            return false;
        updated = value with { GateState = GateAccessState.Locked };
        return true;
    }

    public static bool TryUnlock(
        WorldGroundObject value, bool canManage,
        out WorldGroundObject updated)
    {
        updated = value;
        if (!canManage || !GateCatalog.IsGate(value.ItemId) ||
            value.GateState != GateAccessState.Locked)
            return false;
        updated = value with { GateState = GateAccessState.Unlocked };
        return true;
    }
}
