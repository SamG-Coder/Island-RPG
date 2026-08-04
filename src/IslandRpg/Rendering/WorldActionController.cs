using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    /// <summary>
    /// Owns action queuing, dispatch, cancellation, and update ordering.
    /// </summary>
    private sealed class WorldActionController(GameHostWindow window)
    {
    public void ProcessPendingPath()
    {
        if (window._pendingPathTask is not { IsCompleted: true } task)
            return;

        if (task.IsFaulted)
            throw task.Exception?.GetBaseException() ??
                  new InvalidOperationException("Path calculation failed.");
        if (task.IsCompletedSuccessfully)
        {
            var result = task.Result;
            if (result.RequestId == window._pathRequestId &&
                result.WorldLevel == window._activeWorldLevel)
            {
                if (result.Path.Count == 0)
                {
                    window.CancelMeleeCombat();
                    window.ReportBlockedAction(
                        "path-unreachable",
                        "You cannot reach that target from here.");
                }
                window._queuedAction = result.Action;
                window._player?.FollowPath(result.Path);
                if (result.Path.Count > 0)
                    window._moveMarker = new(
                        result.Path[^1], 0,
                        Action: result.Action is not null);
            }
        }

        window._pendingPathTask = null;
    }

    public void QueueTree(IslandTree tree, GameHostWindow.WorldActionType type)
    {
        var target = new Vector2(tree.X + .5f, tree.Y + .5f);
        QueuePath(
            target,
            window.TreeInteractionDistance(tree.GraphicName),
            type,
            clearTreeActions: true);
    }

    public void QueueGroundObjectPickup(WorldGroundObject groundObject)
    {
        QueuePath(
            new Vector2(groundObject.X, groundObject.Y),
            .46f,
            GameHostWindow.WorldActionType.PickUpGroundObject,
            groundObjectId: groundObject.Id,
            clearTreeActions: true);
    }

    public void QueueTrainingDummyAttack(WorldGroundObject groundObject)
    {
        if (window._combatTargetId == groundObject.Id)
            return;
        window.CancelMeleeCombat();
        var target = new Vector2(
            groundObject.X, groundObject.Y);
        var approachDirection =
            window._player is null
                ? Vector2.Zero
                : window._player.Position - target;
        QueuePath(
            target,
            MeleeCombatService.InteractionRange(
                approachDirection),
            GameHostWindow.WorldActionType.AttackTrainingDummy,
            groundObjectId: groundObject.Id,
            clearTreeActions: true);
    }

    public void QueueVillagerAttack(VillagerState villager)
    {
        var targetChanged = window._combatVillagerId != villager.Id ||
                            window._combatTargetId is not null ||
                            window._combatEnemyId is not null;
        if (targetChanged)
            window.CancelMeleeCombat();
        window._combatTargetId = null;
        window._combatVillagerId = villager.Id;
        window._combatEnemyId = null;
        window.AnnounceCombatTarget(
            targetChanged, villager.Name, ChatMessageStyle.Warning);
        window._villagerCombatPathTarget = new(
            villager.PositionX, villager.PositionY);
        window._villagerCombatRepathAt =
            window._clock +
            MeleeCombatService.MovingTargetRepathSeconds;
        QueuePath(
            new(villager.PositionX, villager.PositionY),
            MeleeCombatService.AttackRange,
            GameHostWindow.WorldActionType.AttackVillager,
            actorId: villager.Id,
            clearTreeActions: true);
    }

    public void QueueEnemyAttack(EnemyState enemy)
    {
        var targetChanged = window._combatEnemyId != enemy.Id ||
                            window._combatTargetId is not null ||
                            window._combatVillagerId is not null;
        if (targetChanged)
            window.CancelMeleeCombat();
        window._combatTargetId = null;
        window._combatVillagerId = null;
        window._combatEnemyId = enemy.Id;
        window.AnnounceCombatTarget(
            targetChanged,
            GameHostWindow.EnemyDisplayName(enemy.Kind).ToLowerInvariant(),
            ChatMessageStyle.Warning);
        window._enemyCombatPathTarget = enemy.Position;
        window._enemyCombatRepathAt =
            window._clock + MeleeCombatService.MovingTargetRepathSeconds;
        QueuePath(
            enemy.Position,
            MeleeCombatService.AttackRange,
            GameHostWindow.WorldActionType.AttackEnemy,
            actorId: enemy.Id.ToString("N"),
            clearTreeActions: true);
    }

    public void QueueVillagerGift(
        VillagerState villager,
        int inventorySlot,
        string itemId)
    {
        window.CancelMeleeCombat();
        QueuePath(
            new(villager.PositionX, villager.PositionY),
            VillagerSimulation.InteractionRange,
            GameHostWindow.WorldActionType.GiveItemToVillager,
            inventorySlot: inventorySlot,
            itemId: itemId,
            actorId: villager.Id,
            clearTreeActions: true);
    }

    public void QueueFish(WorldFish fish)
    {
        var target = new Vector2(fish.X, fish.Y);
        QueuePath(
            target,
            window.FishingNetReach(),
            GameHostWindow.WorldActionType.Fish,
            fishKey: fish.StableKey,
            clearTreeActions: true);
    }

    public void QueueFibreShrub(
        WorldVegetation vegetation, string stableKey)
    {
        QueuePath(
            new Vector2(vegetation.X, vegetation.Y),
            .72f,
            GameHostWindow.WorldActionType.GatherFibres,
            vegetationKey: stableKey,
            clearTreeActions: true);
    }

    public void QueueBerryBush(
        WorldVegetation vegetation, string stableKey)
    {
        QueuePath(
            new Vector2(vegetation.X, vegetation.Y),
            .72f,
            GameHostWindow.WorldActionType.GatherBerries,
            vegetationKey: stableKey,
            clearTreeActions: true);
    }

    public void QueueMining(WorldVegetation node, string stableKey) =>
        QueuePath(
            new Vector2(node.X, node.Y),
            .82f,
            GameHostWindow.WorldActionType.Mine,
            vegetationKey: stableKey,
            clearTreeActions: true);

    public void QueueWalk(Vector2 target)
    {
        if (window._player is null) return;
        window.CancelMeleeCombat();
        window._queuedAction = null;
        window._activeTreeId = null;
        window._activeTreeStickGatherId = null;
        window._player.PrepareForPathRequest();
        CancelPath();
        window._pathCancellation = new CancellationTokenSource();
        var token = window._pathCancellation.Token;
        var requestId = ++window._pathRequestId;
        var worldLevel = window._activeWorldLevel;
        var start = window._player.Position;
        var obstacles = window.ActiveNavigationObstacles();
        window._pendingPathTask = Task.Run(
            () => new GameHostWindow.PathResult(
                requestId,
                worldLevel,
                GridPathfinder.Find(
                    window._worldSeed,
                    start,
                    target,
                    cancellationToken: token,
                    worldLevel: worldLevel,
                    obstacles: obstacles)),
            token);
        window._moveMarker = new(target, 0);
    }

    public void QueuePath(
        Vector2 target,
        float range,
        GameHostWindow.WorldActionType type,
        Guid? groundObjectId = null,
        int inventorySlot = -1,
        string? itemId = null,
        string? fishKey = null,
        string? vegetationKey = null,
        string? actorId = null,
        bool clearTreeActions = false)
    {
        if (window._player is null) return;
        if (type is not (
                GameHostWindow.WorldActionType.AttackTrainingDummy or
                GameHostWindow.WorldActionType.AttackVillager or
                GameHostWindow.WorldActionType.AttackEnemy))
            window.CancelMeleeCombat();
        if (clearTreeActions)
        {
            window._activeTreeId = null;
            window._activeTreeStickGatherId = null;
        }

        window._player.PrepareForPathRequest();
        CancelPath();
        window._pathCancellation = new CancellationTokenSource();
        var token = window._pathCancellation.Token;
        var requestId = ++window._pathRequestId;
        var worldLevel = window._activeWorldLevel;
        var start = window._player.Position;
        var obstacles = window.ActiveNavigationObstacles();
        window._queuedAction = null;
        window._pendingPathTask = Task.Run(
            () => window.FindActionPath(
                requestId,
                worldLevel,
                start,
                target,
                range,
                type,
                token,
                groundObjectId,
                inventorySlot,
                itemId,
                fishKey,
                vegetationKey,
                actorId,
                obstacles),
            token);
        window._moveMarker = null;
    }

    public void CompleteQueuedAction()
    {
        if (window._player is null || window._queuedAction is null ||
            window._player.Action == EntityAction.Move)
            return;

        var action = window._queuedAction;
        window._queuedAction = null;
        if ((window._player.Position - action.Target).Length > action.Range)
            return;

        switch (action)
        {
            case { Type: GameHostWindow.WorldActionType.CutTree }:
                BeginTreeCutting(action.Target);
                break;
            case { Type: GameHostWindow.WorldActionType.GatherTreeSticks }:
                BeginTreeStickGather(action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.AttackTrainingDummy,
                GroundObjectId: { } dummyId
            }:
                window.BeginTrainingDummyCombat(dummyId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.AttackVillager,
                ActorId: { } villagerId
            }:
                window.BeginVillagerCombat(villagerId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.AttackEnemy,
                ActorId: { } enemyId
            } when Guid.TryParseExact(enemyId, "N", out var parsedEnemyId):
                window.BeginEnemyCombat(parsedEnemyId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.GiveItemToVillager,
                ActorId: { } villagerId,
                InventorySlot: >= 0,
                ItemId: { } itemId
            }:
                window.GiveItemToVillager(
                    villagerId, action.InventorySlot, itemId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.PickUpGroundObject,
                GroundObjectId: { } groundObjectId
            }:
                window.BeginGroundObjectPickup(groundObjectId, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.Fish,
                FishKey: { } fishKey
            }:
                window.BeginFishing(fishKey, action.Target);
                break;
            case { Type: GameHostWindow.WorldActionType.BoardFishingBoat }:
                window.BoardFishingBoat();
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.BuildConstruction,
                GroundObjectId: { } siteId
            }:
                window.BeginPlayerConstructionWork(siteId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.DropGroundObject,
                InventorySlot: >= 0,
                ItemId: { } itemId,
                GroundObjectId: var targetObjectId
            }:
                window.BeginGroundObjectDrop(
                    action.InventorySlot, itemId, action.Target,
                    targetObjectId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.LightCampfire,
                GroundObjectId: { } campfireId
            }:
                window.TryLightCampfire(campfireId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.TakeCampfireFuel,
                GroundObjectId: { } campfireId
            }:
                window.BeginCampfireFuelPickup(
                    campfireId, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.CookOnCampfire,
                GroundObjectId: { } campfireId,
                InventorySlot: >= 0,
                ItemId: { } itemId
            }:
                window.BeginCampfireCooking(
                    campfireId,
                    action.InventorySlot,
                    itemId,
                    action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.CookStew,
                GroundObjectId: { } potId
            }:
                window.BeginPotCooking(potId, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.GatherFibres,
                VegetationKey: { } vegetationKey
            }:
                window.BeginFibreGather(
                    vegetationKey, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.GatherBerries,
                VegetationKey: { } vegetationKey
            }:
                window.BeginBerryGather(
                    vegetationKey, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.Mine,
                VegetationKey: { } vegetationKey
            }:
                window.BeginMining(vegetationKey, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.DigCave,
                InventorySlot: >= 0
            }:
                window.TryDigCave(action.Target, action.InventorySlot);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.EnterCave,
                GroundObjectId: { } entranceId
            }:
                window.EnterCave(entranceId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.RestoreExcavation,
                GroundObjectId: { } excavationId
            }:
                window.RestoreExcavation(excavationId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.UseCraftingStation,
                GroundObjectId: { } stationId
            }:
                window.UseCraftingStation(stationId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.OpenStorage,
                GroundObjectId: { } storageId
            }:
                window.OpenWorldStorage(storageId);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.TakeCaveRope,
                GroundObjectId: { } entranceId
            }:
                window.TakeCaveRope(entranceId);
                break;
        }
    }

    public void BeginTreeCutting(Vector2 target) =>
        window.TryStartTreeCutting(target);

    public void BeginTreeStickGather(Vector2 target) =>
        window.TryStartTreeStickGather(target);

    public void Update()
    {
        window.UpdateActiveTreeCutting();
        window.UpdateActiveTreeStickGather();
        window.UpdateGroundObjectPickup();
        window.UpdateGroundObjectDrop();
        window.UpdatePlayerConstruction();
        window.UpdateCampfireFuelPickup();
        window.UpdateCooking();
        window.UpdatePotCooking();
        window.UpdateFishing();
        window.UpdateFibreGathering();
        window.UpdateBerryGathering();
        window.UpdateMining();
        window.UpdateCaveDigging();
        window.UpdateMeleeCombat();
    }

    public void CancelPath()
    {
        window._pathCancellation?.Cancel();
        window._pathCancellation?.Dispose();
        window._pathCancellation = null;
        if (window._pendingPathTask is { } abandoned)
            _ = abandoned.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        window._pendingPathTask = null;
        window._pathRequestId++;
    }

    public void StopPlayer()
    {
        window.CancelMeleeCombat();
        window._queuedAction = null;
        window._activeTreeId = null;
        window._activeTreeStickGatherId = null;
        window._moveMarker = null;
        CancelPath();
        window._player?.Stop();
    }
    }
}
