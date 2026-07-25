using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float GroundItemActionSeconds = .75f;

    private sealed record GroundDropPreview(
        int InventorySlot,
        string ItemId,
        Vector2 Target,
        bool Valid);

    private sealed record ActiveGroundDrop(
        int InventorySlot,
        string ItemId,
        Vector2 Target);

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
                    WorldTreeCatalog.AtlasKey(tree), out var entry))
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
            if (!TryGroundItemVisual(
                    candidate.ItemId, out var frame, out _, out _, out _))
                continue;
            var visualBounds = SpriteBounds(
                frame, GroundObjectWorld(candidate));
            const float minimumHitSize = 24;
            var centerX = (visualBounds.Left + visualBounds.Right) * .5f;
            var centerY = (visualBounds.Top + visualBounds.Bottom) * .5f;
            var bounds = (
                Left: Math.Min(
                    visualBounds.Left, centerX - minimumHitSize * .5f),
                Top: Math.Min(
                    visualBounds.Top, centerY - minimumHitSize * .5f),
                Right: Math.Max(
                    visualBounds.Right, centerX + minimumHitSize * .5f),
                Bottom: Math.Max(
                    visualBounds.Bottom, centerY + minimumHitSize * .5f));
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

    private bool TryGetFishUnderMouse(
        Vector2 mouse, out WorldFish hoveredFish)
    {
        foreach (var gpu in _worldChunks.Values.Where(IsChunkVisible))
        foreach (var cached in gpu.FishRenderItems
                     .OrderByDescending(item => item.World.Y))
        {
            if (!WorldFishPresentation.BaseHitTest(
                    mouse,
                    SpriteAnchor(cached.World),
                    SpritePixelScale()))
                continue;
            hoveredFish = cached.Fish;
            return true;
        }
        hoveredFish = null!;
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
        => _worldActions.QueueGroundObjectPickup(groundObject);

    private void TryPickUpGroundObject(Guid groundObjectId)
    {
        if (_player is null || _activePlayer is null) return;
        var chunk = _worldChunks.Values.FirstOrDefault(gpu =>
            gpu.Chunk.GroundObjects.Any(
                item => item.Id == groundObjectId));
        var groundObject = chunk?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == groundObjectId);
        if (chunk is null || groundObject is null) return;
        var itemId = groundObject.ItemId;
        if (!PlayerInventory.TryAdd(
                _activePlayer.Inventory, itemId, out var inventory))
        {
            ReportBlockedAction(
                "pickup-inventory-full",
                "Your inventory is too full to pick that up.");
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

    internal void BeginGroundObjectPickup(Guid groundObjectId, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        if (PlayerInventory.IsFull(_activePlayer.Inventory))
        {
            ReportBlockedAction(
                "pickup-inventory-full",
                "Your inventory is too full to pick that up.");
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

    internal void UpdateGroundObjectPickup()
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

        if (_player.ActionTime < GroundItemActionSeconds) return;

        var groundObjectId = _activeGroundPickupId.Value;
        _activeGroundPickupId = null;
        TryPickUpGroundObject(groundObjectId);
        _player.Stop();
    }

    private void UpdateGroundDropPreview(Vector2 pointer)
    {
        _groundDropPreview = null;
        if (_activePlayer is null ||
            _gameUi.BlocksWorldInput(pointer) ||
            _chatUi.BlocksWorldInput(pointer) ||
            _inventoryContext.HitTest(pointer) ||
            _treeContext.HitTest(pointer) ||
            _groundObjectContext.HitTest(pointer) ||
            _minimapUi.HitTest(pointer))
            return;

        var inventory = _activePlayer.Inventory ?? [];
        if ((uint)_inventoryDraggingSlot >= (uint)inventory.Length ||
            inventory[_inventoryDraggingSlot] is not { } itemId ||
            !PlayerInventory.CanDrop(itemId) ||
            !TryGroundItemVisual(itemId, out _, out _, out _, out _))
            return;

        var target = ScreenToTerrain(SceneMousePosition());
        var valid = CanPlaceGroundObjectAt(target, out _, out _);
        _groundDropPreview = new(
            _inventoryDraggingSlot, itemId, target, valid);
    }

    private bool IsWorldDropDragOutsideInventory()
    {
        if (!_inventoryInteraction.AllowsCurrentDragOutsideToGame)
            return false;
        if (_gameUi.Panel.Bounds.Contains(MouseState.Position))
            return false;
        var inventory = _activePlayer?.Inventory ?? [];
        return (uint)_inventoryDraggingSlot < (uint)inventory.Length &&
               inventory[_inventoryDraggingSlot] is { } itemId &&
               PlayerInventory.CanDrop(itemId) &&
               TryGroundItemVisual(itemId, out _, out _, out _, out _);
    }

    private void RenderGroundDropPreview()
    {
        if (_groundDropPreview is not { } preview) return;
        if (!TryGroundItemVisual(
                preview.ItemId,
                out var frame,
                out var texture,
                out var atlasKey,
                out var shadowKey))
            return;

        var world = GroundObjectWorld(new(
            Guid.Empty, preview.ItemId, preview.Target.X, preview.Target.Y));
        if (shadowKey is not null &&
            _treeAtlas.ContainsKey(shadowKey))
        {
            GL.UseProgram(_program);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "tintAmount"), 0f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "preserveDarkTint"), 0);
            var shadowVertices = new List<float>();
            AddAtlasQuad(
                shadowKey, world, .34f, shadowVertices);
            DrawTreeBatch(shadowVertices);
        }
        DrawSprite(
            frame,
            texture,
            world,
            opacity: .58f,
            tint: preview.Valid
                ? new Vector3(.28f, 1f, .34f)
                : new Vector3(1f, .22f, .18f),
            tintAmount: .72f,
            preserveDarkTint: true);
    }

    private void QueueGroundObjectDrop(GroundDropPreview preview)
    {
        if (_player is null || !preview.Valid) return;
        _activeTreeId = null;
        _activeGroundPickupId = null;
        _activeGroundDrop = null;
        _player.Stop();
        _pathCancellation?.Cancel();
        _pathCancellation?.Dispose();
        _pathCancellation = null;
        _pendingPathTask = null;
        _queuedAction = null;
        _pathRequestId++;
        _moveMarker = new(preview.Target, 0, Action: true);
        if ((_player.Position - preview.Target).Length <= .80f)
        {
            BeginGroundObjectDrop(
                preview.InventorySlot, preview.ItemId, preview.Target);
            return;
        }
        _worldActions.QueuePath(
            preview.Target,
            .46f,
            WorldActionType.DropGroundObject,
            inventorySlot: preview.InventorySlot,
            itemId: preview.ItemId);
    }

    internal void BeginGroundObjectDrop(
        int inventorySlot, string itemId, Vector2 target)
    {
        if (_player is null ||
            !InventoryContainsAt(inventorySlot, itemId) ||
            !PlayerInventory.CanDrop(itemId))
        {
            ReportBlockedAction(
                "drop-item-unavailable",
                "That item is no longer available to drop.");
            return;
        }
        if (!CanPlaceGroundObjectAt(target, out _, out var reason))
        {
            ReportBlockedAction("drop-location-blocked", reason);
            return;
        }

        _activeTreeId = null;
        _activeGroundPickupId = null;
        _activeGroundDrop = new(
            inventorySlot, itemId, target);
        _player.GatherAt(target);
    }

    internal void UpdateGroundObjectDrop()
    {
        if (_player is null || _activeGroundDrop is not { } drop) return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeGroundDrop = null;
            return;
        }
        if (_player.ActionTime < GroundItemActionSeconds) return;

        _activeGroundDrop = null;
        if (!InventoryContainsAt(drop.InventorySlot, drop.ItemId))
        {
            ReportBlockedAction(
                "drop-item-unavailable",
                "That item is no longer available to drop.");
            _player.Stop();
            return;
        }
        if (!CanPlaceGroundObjectAt(
                drop.Target, out var gpu, out var reason))
        {
            ReportBlockedAction("drop-location-blocked", reason);
            _player.Stop();
            return;
        }
        if (!PlayerInventory.TryRemove(
                _activePlayer!.Inventory,
                drop.InventorySlot,
                out var inventory))
        {
            _player.Stop();
            return;
        }

        gpu.Chunk.GroundObjects.Add(new(
            Guid.NewGuid(),
            drop.ItemId,
            drop.Target.X,
            drop.Target.Y));
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == drop.InventorySlot)
            _activeInventorySlot = -1;
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(gpu.Chunk);
        _chatUi.AddMessage(
            $"You drop the {ItemCatalog.Get(drop.ItemId).Name}.",
            ChatMessageStyle.Action);
        _player.Stop();
    }

    private bool InventoryContainsAt(int slot, string itemId)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        return (uint)slot < (uint)inventory.Length &&
               string.Equals(
                   inventory[slot], itemId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGroundItemVisual(
        string itemId,
        out SpriteFrame frame,
        out int texture,
        out string atlasKey,
        out string? shadowKey)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.SpriteCell is not { } cell)
        {
            frame = null!;
            texture = 0;
            atlasKey = "";
            shadowKey = null;
            return false;
        }

        if (item.HasTag(ItemTag.StoneToolSprite))
        {
            if ((uint)cell >= (uint)_stoneToolFrames.Length ||
                _stoneToolFrames[cell] is not { } stoneToolFrame ||
                _stoneToolTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = stoneToolFrame;
            texture = _stoneToolTextures[cell];
            atlasKey = StoneToolAtlasKey(cell, shadow: false);
            shadowKey = _stoneToolShadowFrames[cell] is null
                ? null
                : StoneToolAtlasKey(cell, shadow: true);
            return true;
        }

        if (item.HasTag(ItemTag.SupplementalSprite))
        {
            if ((uint)cell >= (uint)_supplementalItemFrames.Length ||
                _supplementalItemFrames[cell] is not
                    { } supplementalFrame ||
                _supplementalItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = supplementalFrame;
            texture = _supplementalItemTextures[cell];
            atlasKey = SupplementalAtlasKey(cell, shadow: false);
            shadowKey = _supplementalShadowFrames[cell] is null
                ? null
                : SupplementalAtlasKey(cell, shadow: true);
            return true;
        }

        if (item.HasTag(ItemTag.CoastalSprite))
        {
            if ((uint)cell >= (uint)_coastalSprites.GroundFrames.Length ||
                _coastalSprites.GroundFrames[cell] is not
                    { } coastalFrame ||
                _coastalSprites.GroundTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = coastalFrame;
            texture = _coastalSprites.GroundTextures[cell];
            atlasKey = CoastalAtlasKey(cell, shadow: false);
            shadowKey = _coastalSprites.GroundShadows[cell] is null
                ? null
                : CoastalAtlasKey(cell, shadow: true);
            return true;
        }

        if (item.HasTag(ItemTag.NaturalMaterial))
        {
            if ((uint)cell >= (uint)_naturalItemFrames.Length ||
                _naturalItemFrames[cell] is not { } naturalFrame ||
                _naturalItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = naturalFrame;
            texture = _naturalItemTextures[cell];
            atlasKey = NaturalAtlasKey(cell, shadow: false);
            shadowKey = _naturalShadowFrames[cell] is null
                ? null
                : NaturalAtlasKey(cell, shadow: true);
            return true;
        }

        if ((uint)cell >= (uint)_woodcuttingItemFrames.Length ||
            _woodcuttingItemFrames[cell] is not { } itemFrame ||
            _woodcuttingItemTextures[cell] == 0)
        {
            frame = null!;
            texture = 0;
            atlasKey = "";
            shadowKey = null;
            return false;
        }
        frame = itemFrame;
        texture = _woodcuttingItemTextures[cell];
        atlasKey = ItemAtlasKey(cell, shadow: false);
        shadowKey = _woodcuttingShadowFrames[cell] is null
            ? null
            : ItemAtlasKey(cell, shadow: true);
        return true;
    }

    private void TryDropGroundObject(int inventorySlot, string itemId)
    {
        if (_player is null || _activePlayer is null) return;
        if (!PlayerInventory.CanDrop(itemId) ||
            !TryGroundItemVisual(itemId, out _, out _, out _, out _))
            return;

        if (!TryFindGroundObjectDrop(
                _player.Position, out _, out var dropPosition,
                out var reason))
        {
            ReportBlockedAction("drop-location-blocked", reason);
            return;
        }

        QueueGroundObjectDrop(new(
            inventorySlot,
            itemId,
            dropPosition,
            Valid: true));
    }

    private bool TryFindGroundObjectDrop(
        Vector2 origin,
        out GpuWorldChunk gpu,
        out Vector2 dropPosition,
        out string reason)
    {
        const float minimumReach = .28f;
        const float maximumReach = .82f;
        const int placementAttempts = 40;

        gpu = null!;
        dropPosition = default;
        var originX = (int)MathF.Floor(origin.X);
        var originY = (int)MathF.Floor(origin.Y);
        if (!TryGetDropTerrain(originX, originY, out _, out reason))
            return false;

        for (var attempt = 0; attempt < placementAttempts; attempt++)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var radius = MathF.Sqrt(Random.Shared.NextSingle()) *
                         (maximumReach - minimumReach) + minimumReach;
            var candidate = origin + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * radius;
            var tileX = (int)MathF.Floor(candidate.X);
            var tileY = (int)MathF.Floor(candidate.Y);
            if (!TryGetDropTerrain(
                    tileX, tileY, out var candidateGpu, out _))
                continue;
            if (!HasGroundObjectClearance(candidate)) continue;

            gpu = candidateGpu;
            dropPosition = candidate;
            reason = "";
            return true;
        }

        reason = "There is no clear space within reach to drop that.";
        return false;
    }

    private bool CanPlaceGroundObjectAt(
        Vector2 target,
        out GpuWorldChunk gpu,
        out string reason)
    {
        var tileX = (int)MathF.Floor(target.X);
        var tileY = (int)MathF.Floor(target.Y);
        if (!TryGetDropTerrain(tileX, tileY, out gpu, out reason))
            return false;
        if (HasGroundObjectClearance(target))
            return true;

        reason = "There is already something blocking that spot.";
        return false;
    }

    private bool TryGetDropTerrain(
        int tileX,
        int tileY,
        out GpuWorldChunk gpu,
        out string reason)
    {
        var coordinate = new ChunkCoordinate(
            FloorDiv(tileX, WorldChunk.Size),
            FloorDiv(tileY, WorldChunk.Size));
        if (!_worldChunks.TryGetValue(coordinate, out gpu!))
        {
            reason = "You cannot drop that here.";
            return false;
        }

        var chunk = gpu.Chunk;
        var localX = tileX - chunk.Coordinate.X * WorldChunk.Size;
        var localY = tileY - chunk.Coordinate.Y * WorldChunk.Size;
        if (localX is < 0 or >= WorldChunk.Size ||
            localY is < 0 or >= WorldChunk.Size)
        {
            reason = "You cannot drop that here.";
            return false;
        }

        var tile = chunk.Tiles[localY * WorldChunk.Size + localX];
        if (tile.Biome is Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows)
        {
            reason = "You cannot drop that in water.";
            return false;
        }

        var highest = Math.Max(
            Math.Max(tile.North, tile.East),
            Math.Max(tile.South, tile.West));
        var lowest = Math.Min(
            Math.Min(tile.North, tile.East),
            Math.Min(tile.South, tile.West));
        if (highest - lowest > 2)
        {
            reason = "The ground is too steep to drop that here.";
            return false;
        }

        reason = "";
        return true;
    }

    private bool HasGroundObjectClearance(Vector2 candidate)
    {
        const float treeClearance = .46f;
        const float itemClearance = .30f;
        foreach (var chunk in _worldChunks.Values.Select(value => value.Chunk))
        {
            if (chunk.Trees.Any(tree =>
                    (candidate - new Vector2(
                        tree.X + .5f, tree.Y + .5f)).LengthSquared <
                    treeClearance * treeClearance))
                return false;
            if (chunk.GroundObjects.Any(item =>
                    (candidate - new Vector2(item.X, item.Y)).LengthSquared <
                    itemClearance * itemClearance))
                return false;
        }

        return true;
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
