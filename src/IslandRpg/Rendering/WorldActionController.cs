using IslandRpg.Assets;
using IslandRpg.Gameplay;
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
            if (result.RequestId == window._pathRequestId)
            {
                window._queuedAction = result.Action;
                window._player?.FollowPath(result.Path);
                if (result.Action is not null && result.Path.Count > 0)
                    window._moveMarker = new(
                        result.Path[^1], 0, Action: true);
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

    public void QueueWalk(Vector2 target)
    {
        if (window._player is null) return;
        window._queuedAction = null;
        window._activeTreeId = null;
        window._activeTreeStickGatherId = null;
        window._player.Stop();
        CancelPath();
        window._pathCancellation = new CancellationTokenSource();
        var token = window._pathCancellation.Token;
        var requestId = ++window._pathRequestId;
        var start = window._player.Position;
        window._pendingPathTask = Task.Run(
            () => new GameHostWindow.PathResult(
                requestId,
                GridPathfinder.Find(
                    window._worldSeed,
                    start,
                    target,
                    cancellationToken: token)),
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
        bool clearTreeActions = false)
    {
        if (window._player is null) return;
        if (clearTreeActions)
        {
            window._activeTreeId = null;
            window._activeTreeStickGatherId = null;
        }

        window._player.Stop();
        CancelPath();
        window._pathCancellation = new CancellationTokenSource();
        var token = window._pathCancellation.Token;
        var requestId = ++window._pathRequestId;
        var start = window._player.Position;
        window._queuedAction = null;
        window._pendingPathTask = Task.Run(
            () => window.FindActionPath(
                requestId,
                start,
                target,
                range,
                type,
                token,
                groundObjectId,
                inventorySlot,
                itemId,
                fishKey,
                vegetationKey),
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
            case
            {
                Type: GameHostWindow.WorldActionType.DropGroundObject,
                InventorySlot: >= 0,
                ItemId: { } itemId
            }:
                window.BeginGroundObjectDrop(
                    action.InventorySlot, itemId, action.Target);
                break;
            case
            {
                Type: GameHostWindow.WorldActionType.GatherFibres,
                VegetationKey: { } vegetationKey
            }:
                window.BeginFibreGather(
                    vegetationKey, action.Target);
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
        window.UpdateFishing();
        window.UpdateFibreGathering();
    }

    public void CancelPath()
    {
        window._pathCancellation?.Cancel();
        window._pathCancellation?.Dispose();
        window._pathCancellation = null;
        window._pendingPathTask = null;
    }
    }
}
