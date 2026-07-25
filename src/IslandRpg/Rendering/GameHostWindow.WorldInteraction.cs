using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool AtlasOverlapsPlayer(
        string atlasKey, Vector2 world, PlayerVisual player)
    {
        if (!_treeAtlas.TryGetValue(atlasKey, out var entry)) return false;
        var objectBounds = SpriteBounds(entry.Frame, world);
        var playerBounds = SpriteBounds(
            player.Frame, player.World, player.Mirror);
        return objectBounds.Left < playerBounds.Right &&
               objectBounds.Right > playerBounds.Left &&
               objectBounds.Top < playerBounds.Bottom &&
               objectBounds.Bottom > playerBounds.Top;
    }

    private bool TryGetTreeUnderMouse(
        Vector2 mouse, out IslandTree hoveredTree)
    {
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible)
                     .OrderByDescending(gpu =>
                         gpu.Chunk.Coordinate.X + gpu.Chunk.Coordinate.Y))
        foreach (var tree in gpu.Chunk.Trees
                     .OrderByDescending(tree => tree.X + tree.Y))
        {
            if (gpu.Chunk.TreeInstances.Any(instance =>
                    instance.X == tree.X && instance.Y == tree.Y &&
                    instance.State == TreeLifecycleState.Stump))
                continue;
            if (!_treeAtlas.TryGetValue(
                    tree.GraphicName, out var entry))
                continue;
            var tileX = PositiveMod(tree.X, WorldChunk.Size);
            var tileY = PositiveMod(tree.Y, WorldChunk.Size);
            var tile = gpu.Chunk.Tiles[
                tileY * WorldChunk.Size + tileX];
            var height =
                (tile.North + tile.East + tile.South + tile.West) / 4f;
            var world = new Vector2(
                (tree.X - tree.Y) * 48,
                (tree.X + tree.Y + 1) * 24 - height * 20);
            var bounds = SpriteBounds(entry.Frame, world);
            if (mouse.X < bounds.Left || mouse.X >= bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y >= bounds.Bottom)
                continue;

            var scale = Math.Max(SpritePixelScale(), .001f);
            var x = (int)((mouse.X - bounds.Left) / scale);
            var y = (int)((mouse.Y - bounds.Top) / scale);
            if ((uint)x >= (uint)entry.Frame.Width ||
                (uint)y >= (uint)entry.Frame.Height)
                continue;
            if (entry.Frame.Rgba[
                    (y * entry.Frame.Width + x) * 4 + 3] <= 24)
                continue;
            hoveredTree = tree;
            return true;
        }
        hoveredTree = null!;
        return false;
    }

    private bool TryGetGroundObjectUnderMouse(
        Vector2 mouse,
        out WorldGroundObject groundObject,
        out GpuWorldChunk chunk)
    {
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible)
                     .OrderByDescending(value =>
                         value.Chunk.Coordinate.X +
                         value.Chunk.Coordinate.Y))
        foreach (var candidate in gpu.Chunk.GroundObjects
                     .OrderByDescending(value => value.X + value.Y))
        {
            var cell = candidate.Kind == GroundObjectKind.Sticks ? 0 : 1;
            if (_naturalItemFrames[cell] is not { } frame) continue;
            var bounds = SpriteBounds(
                frame, GroundObjectWorld(candidate));
            if (mouse.X < bounds.Left || mouse.X >= bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y >= bounds.Bottom)
                continue;
            groundObject = candidate;
            chunk = gpu;
            return true;
        }
        groundObject = null!;
        chunk = null!;
        return false;
    }

    private Vector2 GroundObjectWorld(WorldGroundObject groundObject)
    {
        var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, groundObject.X, groundObject.Y);
        return new(
            (groundObject.X - groundObject.Y) * 48,
            (groundObject.X + groundObject.Y) * 24 - elevation * 20);
    }

    private void QueueGroundObjectPickup(WorldGroundObject groundObject)
    {
        if (_player is null) return;
        var target = new Vector2(groundObject.X, groundObject.Y);
        _activeTreeId = null;
        _player.Stop();
        _pathCancellation?.Cancel();
        _pathCancellation?.Dispose();
        _pathCancellation = new CancellationTokenSource();
        var token = _pathCancellation.Token;
        var requestId = ++_pathRequestId;
        var start = _player.Position;
        _queuedAction = null;
        _pendingPathTask = Task.Run(
            () => FindActionPath(
                requestId, start, target, .46f,
                WorldActionType.PickUpGroundObject, token,
                groundObject.Id),
            token);
        _moveMarker = null;
    }

    private void TryPickUpGroundObject(Guid groundObjectId)
    {
        if (_player is null || _activePlayer is null) return;
        var chunk = _worldChunks.Values.FirstOrDefault(gpu =>
            gpu.Chunk.GroundObjects.Any(
                item => item.Id == groundObjectId));
        var groundObject = chunk?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == groundObjectId);
        if (chunk is null || groundObject is null) return;
        var itemId = groundObject.Kind == GroundObjectKind.Sticks
            ? ItemIds.Sticks
            : ItemIds.LargeRock;
        if (!PlayerInventory.TryAdd(
                _activePlayer.Inventory, itemId, out var inventory))
        {
            _chatUi.AddMessage(
                "Your inventory is too full to pick that up.",
                ChatMessageStyle.Warning);
            return;
        }
        if (!chunk.Chunk.GroundObjects.Remove(groundObject)) return;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(chunk.Chunk);
        _chatUi.AddMessage(
            $"You pick up the {ItemCatalog.Get(itemId).Name}.",
            ChatMessageStyle.Action);
    }

    private void BeginGroundObjectPickup(Guid groundObjectId, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        if (PlayerInventory.IsFull(_activePlayer.Inventory))
        {
            _chatUi.AddMessage(
                "Your inventory is too full to pick that up.",
                ChatMessageStyle.Warning);
            _player.Stop();
            return;
        }

        var exists = _worldChunks.Values.Any(gpu =>
            gpu.Chunk.GroundObjects.Any(item => item.Id == groundObjectId));
        if (!exists) return;

        _activeTreeId = null;
        _activeGroundPickupId = groundObjectId;
        _player.GatherAt(target);
    }

    private void UpdateGroundObjectPickup()
    {
        if (_player is null || _activeGroundPickupId is null) return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeGroundPickupId = null;
            return;
        }

        if (!_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Gather), out var animation))
        {
            var pickupWithoutAnimation = _activeGroundPickupId.Value;
            _activeGroundPickupId = null;
            TryPickUpGroundObject(pickupWithoutAnimation);
            _player.Stop();
            return;
        }

        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        if (_player.ActionTime < cycleDuration) return;

        var groundObjectId = _activeGroundPickupId.Value;
        _activeGroundPickupId = null;
        TryPickUpGroundObject(groundObjectId);
        _player.Stop();
    }

    private static string StumpAtlasKey(
        string treeType, bool shadow)
    {
        if (shadow) return "";
        if (treeType.StartsWith(
                "FBAM", StringComparison.OrdinalIgnoreCase))
            return "STUMB_NN#0";
        if (treeType.StartsWith(
                "FPIN", StringComparison.OrdinalIgnoreCase) ||
            treeType.StartsWith(
                "FSNO", StringComparison.OrdinalIgnoreCase))
            return "STUMP_NN#1";
        if (treeType.StartsWith(
                "FPAL", StringComparison.OrdinalIgnoreCase) ||
            treeType.StartsWith(
                "FJUN", StringComparison.OrdinalIgnoreCase))
            return "STUMP_NN#2";
        return "STUMP_NN#0";
    }
}
