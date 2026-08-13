using IslandRpg.Gameplay;
using IslandRpg.World;

namespace IslandRpg.NetworkingChecks;

internal static class WorldRuleChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "campfire rules preserve deterministic fuel and burn state",
            CampfireFuelAndBurnState);
        checks.Add(
            "construction rules preserve staged world-object health",
            ConstructionStagesWorldObjects);
        checks.Add(
            "storage rules round-trip persistent container contents",
            StorageRoundTripsContents);
        checks.Add(
            "gate rules enforce completion and management access",
            GateRulesEnforceAccess);
    }

    private static void CampfireFuelAndBurnState()
    {
        const double litAt = 100;
        var empty = new WorldGroundObject(
            Guid.NewGuid(), ItemIds.Campfire, 4.5f, 6.5f);

        CheckAssert.Equal(
            CampfireState.Empty,
            CampfireService.State(empty, litAt),
            "a new campfire must be empty");
        CheckAssert.True(
            CampfireService.CanAddFuel(empty, ItemIds.Logs, litAt),
            "an empty campfire must accept log fuel");
        CheckAssert.False(
            CampfireService.CanAddFuel(empty, ItemIds.LargeRock, litAt),
            "a campfire must reject non-log fuel");

        var fueled = CampfireService.AddFuel(empty, ItemIds.Logs, litAt);
        CheckAssert.Equal(
            CampfireLightFailure.SmallRocksMissing,
            CampfireService.LightFailure(fueled, [], litAt),
            "lighting must require small rocks");
        CheckAssert.Equal(
            CampfireLightFailure.KnifeMissing,
            CampfireService.LightFailure(
                fueled, [ItemIds.SmallRocks], litAt),
            "lighting must require a knife after rocks are available");

        var lit = CampfireService.Light(
            fueled, litAt, FiremakingSkill.MaximumLevel);
        CheckAssert.Equal(
            CampfireState.Lit,
            CampfireService.State(lit, litAt),
            "a lit campfire must remain lit until its deterministic deadline");
        CheckAssert.Equal(
            litAt + FiremakingSkill.DurationGameSeconds(
                FiremakingSkill.MaximumLevel),
            lit.LitUntilGameSeconds,
            "firemaking level must determine the burn deadline");
        CheckAssert.True(
            CharcoalService.IsReady(lit, lit.LitUntilGameSeconds),
            "expired log fuel must become ready charcoal");

        var expired = CampfireService.Expire(
            lit, lit.LitUntilGameSeconds);
        CheckAssert.Equal(
            CampfireState.Empty,
            CampfireService.State(expired, lit.LitUntilGameSeconds),
            "expiring a burnt fire must clear its fuel state");
    }

    private static void ConstructionStagesWorldObjects()
    {
        var wall = new WorldGroundObject(
            Guid.NewGuid(), ItemIds.WoodenWall, 2.5f, 3.5f);
        var planned = ConstructionService.Begin(wall);

        CheckAssert.True(
            ConstructionService.IsConstructionSite(planned),
            "beginning a wall must create a construction site");
        CheckAssert.Equal(
            ConstructionStage.Planned,
            ConstructionService.Stage(planned),
            "new construction must start in the planned stage");
        CheckAssert.Equal(
            ItemIds.Logs,
            ConstructionService.DemolitionRefund(planned),
            "unfinished wooden construction must retain its refund rule");

        var completed = ConstructionService.AddWork(
            planned, planned.MaxHealth);
        CheckAssert.Equal(
            completed.MaxHealth,
            completed.Health,
            "construction work must clamp health at the structure maximum");
        CheckAssert.Equal(
            ConstructionStage.Complete,
            ConstructionService.Stage(completed),
            "maximum-health construction must be complete");
        CheckAssert.False(
            ConstructionService.IsConstructionSite(completed),
            "completed structures must stop behaving as construction sites");
    }

    private static void StorageRoundTripsContents()
    {
        var storage = new WorldGroundObject(
            Guid.NewGuid(), ItemIds.StorageChest, 8.5f, 9.5f);
        var open = StorageContainerService.Open(storage);

        CheckAssert.Equal(
            storage.Id,
            open.Definition.Id,
            "storage definitions must retain their world-object identity");
        CheckAssert.Equal(
            48,
            open.Definition.Capacity,
            "wooden chests must retain their canonical capacity");
        CheckAssert.True(
            open.TryAdd(ItemIds.SlimeGel, 6, "owner-a"),
            "persistent storage must accept a valid stack");

        var saved = StorageContainerService.Save(storage, open);
        var reopened = StorageContainerService.Open(saved);
        CheckAssert.Equal(
            ItemIds.SlimeGel,
            reopened.Items[0],
            "saved storage must restore its item ID");
        CheckAssert.Equal(
            6,
            reopened.Quantities[0],
            "saved storage must restore its stack quantity");
        CheckAssert.Equal(
            "owner-a",
            reopened.OwnerIds[0],
            "saved storage must restore its stack owner");
    }

    private static void GateRulesEnforceAccess()
    {
        var gate = new WorldGroundObject(
            Guid.NewGuid(), GateCatalog.All[0].ItemId, 10.5f, 11.5f);
        var site = ConstructionService.Begin(gate);
        CheckAssert.False(
            GateService.TryOpen(site, out _),
            "unfinished gates must not open");

        var completed = ConstructionService.AddWork(site, site.MaxHealth);
        CheckAssert.True(
            GateService.TryOpen(completed, out var opened) &&
            GateService.IsOpen(opened),
            "completed unlocked gates must open");
        CheckAssert.True(
            GateService.TryClose(opened, out var closed),
            "opened gates must close back to unlocked state");
        CheckAssert.False(
            GateService.TryLock(closed, false, out _),
            "unmanaged gates must reject lock attempts");
        CheckAssert.True(
            GateService.TryLock(closed, true, out var locked),
            "managed gates must permit locking");
        CheckAssert.False(
            GateService.TryUnlock(locked, false, out _),
            "unmanaged gates must reject unlock attempts");
        CheckAssert.True(
            GateService.TryUnlock(locked, true, out var unlocked),
            "managed gates must permit unlocking");
        CheckAssert.Equal(
            GateAccessState.Unlocked,
            unlocked.GateState,
            "unlocking must restore the canonical unlocked state");
    }
}
