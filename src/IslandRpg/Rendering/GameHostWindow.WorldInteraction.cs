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
    private readonly HashSet<long> _stumpHoverScratch = [];

    private sealed record GroundDropPreview(
        int InventorySlot,
        string ItemId,
        Vector2 Target,
        bool Valid,
        Guid? TargetObjectId = null);

    private sealed record ActiveGroundDrop(
        int InventorySlot,
        string ItemId,
        Vector2 Target,
        Guid? TargetObjectId = null);

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
        hoveredTree = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
            _stumpHoverScratch.Clear();
            foreach (var instance in gpu.Chunk.TreeInstances)
                if (instance.State == TreeLifecycleState.Stump)
                    _stumpHoverScratch.Add(
                        WorldHoverSelection.TileKey(
                            instance.X, instance.Y));
            foreach (var tree in gpu.Chunk.Trees)
            {
                if (_stumpHoverScratch.Contains(
                        WorldHoverSelection.TileKey(
                            tree.X, tree.Y)) ||
                    !_treeAtlas.TryGetValue(
                        WorldTreeCatalog.AtlasKey(tree),
                        out var entry))
                    continue;
                var tileX = PositiveMod(tree.X, WorldChunk.Size);
                var tileY = PositiveMod(tree.Y, WorldChunk.Size);
                var tile = gpu.Chunk.Tiles[
                    tileY * WorldChunk.Size + tileX];
                var height =
                    (tile.North + tile.East +
                     tile.South + tile.West) / 4f;
                var world = new Vector2(
                    (tree.X - tree.Y) * 48,
                    (tree.X + tree.Y + 1) * 24 -
                    height * 20);
                var bounds = SpriteBounds(entry.Frame, world);
                if (mouse.X < bounds.Left ||
                    mouse.X >= bounds.Right ||
                    mouse.Y < bounds.Top ||
                    mouse.Y >= bounds.Bottom)
                    continue;

                var scale = Math.Max(
                    SpritePixelScale(), .001f);
                var x = (int)(
                    (mouse.X - bounds.Left) / scale);
                var y = (int)(
                    (mouse.Y - bounds.Top) / scale);
                if ((uint)x >= (uint)entry.Frame.Width ||
                    (uint)y >= (uint)entry.Frame.Height ||
                    entry.Frame.Rgba[
                        (y * entry.Frame.Width + x) * 4 + 3] <= 24 ||
                    !WorldHoverSelection.Prefer(
                        world.Y, ref selectedDepth))
                    continue;
                hoveredTree = tree;
            }
        }
        return hoveredTree is not null;
    }

    private bool TryGetGroundObjectUnderMouse(
        Vector2 mouse,
        out WorldGroundObject groundObject,
        out GpuWorldChunk chunk)
    {
        groundObject = null!;
        chunk = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
        foreach (var candidate in gpu.Chunk.GroundObjects)
        {
            if (!TryGroundItemVisual(
                    candidate.ItemId, out var frame, out _, out _, out _))
                continue;
            var world = GroundObjectWorld(candidate);
            var visualBounds = SpriteBounds(
                frame, world);
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
            if (!WorldHoverSelection.Prefer(
                    world.Y, ref selectedDepth))
                continue;
            groundObject = candidate;
            chunk = gpu;
        }
        }
        return groundObject is not null;
    }

    private bool TryGetFishUnderMouse(
        Vector2 mouse, out WorldFish hoveredFish)
    {
        hoveredFish = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
        foreach (var cached in gpu.FishRenderItems)
        {
            if (IsFishDepleted(cached.Fish)) continue;
            if (!WorldFishPresentation.BaseHitTest(
                    mouse,
                    SpriteAnchor(cached.World),
                    SpritePixelScale()))
                continue;
            if (!WorldHoverSelection.Prefer(
                    cached.World.Y, ref selectedDepth))
                continue;
            hoveredFish = cached.Fish;
        }
        }
        return hoveredFish is not null;
    }

    private Vector2 GroundObjectWorld(WorldGroundObject groundObject)
    {
        var elevation = SamplePlayerTerrain(
            groundObject.X, groundObject.Y).Height;
        return new(
            (groundObject.X - groundObject.Y) * 48,
            (groundObject.X + groundObject.Y) * 24 -
            elevation * 20 +
            PlaceableObjectCatalog.ProjectedFrontOffsetPixels(
                groundObject.ItemId));
    }

    private void QueueGroundObjectPickup(WorldGroundObject groundObject)
        => _worldActions.QueueGroundObjectPickup(groundObject);

    private void TryPickUpGroundObject(Guid groundObjectId)
    {
        if (_player is null || _activePlayer is null) return;
        var chunk = _worldChunks.Values.FirstOrDefault(gpu =>
            IsActiveWorldChunk(gpu) &&
            gpu.Chunk.GroundObjects.Any(
                item => item.Id == groundObjectId));
        var groundObject = chunk?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == groundObjectId);
        if (chunk is null || groundObject is null) return;
        var itemId = groundObject.ItemId;
        if (PlaceableObjectCatalog.IsPlaceable(itemId))
        {
            ReportBlockedAction(
                "placed-object-fixed",
                "That has been built in place and cannot be picked up.");
            return;
        }
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

        var groundObject = _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(gpu => gpu.Chunk.GroundObjects)
            .FirstOrDefault(item => item.Id == groundObjectId);
        if (groundObject is null ||
            PlaceableObjectCatalog.IsPlaceable(groundObject.ItemId))
            return;

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
            _fishContext.HitTest(pointer) ||
            _vegetationContext.HitTest(pointer) ||
            _miningContext.HitTest(pointer) ||
            _minimapUi.HitTest(pointer))
            return;

        var inventory = _activePlayer.Inventory ?? [];
        if ((uint)_inventoryDraggingSlot >= (uint)inventory.Length ||
            inventory[_inventoryDraggingSlot] is not { } itemId ||
            !CanReleaseInventoryItemToWorld(itemId) ||
            !TryGroundItemVisual(itemId, out _, out _, out _, out _))
            return;

        if (itemId == ItemIds.Rope &&
            TryGetGroundObjectUnderMouse(
                SceneMousePosition(), out var caveHole, out _) &&
            CaveEntranceService.IsHole(caveHole))
        {
            _groundDropPreview = new(
                _inventoryDraggingSlot,
                itemId,
                new(caveHole.X, caveHole.Y),
                true,
                caveHole.Id);
            return;
        }
        if (itemId is ItemIds.Dirt or ItemIds.Sand &&
            TryGetGroundObjectUnderMouse(
                SceneMousePosition(), out var fillableHole, out _) &&
            CaveEntranceService.CanFill(fillableHole))
        {
            _groundDropPreview = new(
                _inventoryDraggingSlot,
                itemId,
                new(fillableHole.X, fillableHole.Y),
                CanFillExcavation(
                    fillableHole, itemId, out _),
                fillableHole.Id);
            return;
        }
        if (ItemCatalog.Get(itemId).HasTag(ItemTag.Log) &&
            TryGetGroundObjectUnderMouse(
                SceneMousePosition(), out var campfire, out _) &&
            CampfireService.IsCampfire(campfire))
        {
            _groundDropPreview = new(
                _inventoryDraggingSlot,
                itemId,
                new(campfire.X, campfire.Y),
                CampfireService.CanAddFuel(
                    campfire, itemId, _worldGameSeconds),
                campfire.Id);
            return;
        }
        if (CookingSkill.TryProfile(itemId, out _) &&
            TryGetGroundObjectUnderMouse(
                SceneMousePosition(), out var cookingFire, out _) &&
            CampfireService.IsCampfire(cookingFire))
        {
            _groundDropPreview = new(
                _inventoryDraggingSlot,
                itemId,
                new(cookingFire.X, cookingFire.Y),
                CanCookOnCampfire(
                    cookingFire, itemId, out _),
                cookingFire.Id);
            return;
        }

        var target = PlaceableObjectCatalog.SnapToGrid(
            itemId, ScreenToTerrain(SceneMousePosition()));
        var valid = CanPlaceInventoryItemAt(
            itemId, target, out _, out _);
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
               CanReleaseInventoryItemToWorld(itemId) &&
               TryGroundItemVisual(itemId, out _, out _, out _, out _);
    }

    private void RenderGroundDropPreview()
    {
        if (_groundDropPreview is not { } preview) return;
        if (preview.TargetObjectId is { } campfireId &&
            FindGroundObject(campfireId) is { } campfire &&
            _placeableObjectSprites.TryGetCampfireFueled(
                preview.ItemId, out var fueled))
        {
            DrawSprite(
                fueled.Frame,
                fueled.Texture,
                GroundObjectWorld(campfire),
                opacity: .68f,
                tint: preview.Valid
                    ? new Vector3(.28f, 1f, .34f)
                    : new Vector3(1f, .22f, .18f),
                tintAmount: .62f,
                preserveDarkTint: true);
            return;
        }
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
        if (preview.TargetObjectId is { } cookingFireId &&
            CookingSkill.TryProfile(preview.ItemId, out _) &&
            FindGroundObject(cookingFireId) is { } cookingFire)
        {
            QueueCampfireCooking(
                cookingFire,
                preview.InventorySlot,
                preview.ItemId);
            return;
        }
        _activeTreeId = null;
        _activeGroundPickupId = null;
        _activeGroundDrop = null;
        _player.PrepareForPathRequest();
        _pathCancellation?.Cancel();
        _pathCancellation?.Dispose();
        _pathCancellation = null;
        _pendingPathTask = null;
        _queuedAction = null;
        _pathRequestId++;
        _moveMarker = new(preview.Target, 0, Action: true);
        var interactionRange = PlaceableObjectCatalog.TryGet(
            preview.ItemId, out var placeable)
            ? Math.Max(
                placeable.FootprintWidth,
                placeable.FootprintDepth) * .5f + .55f
            : .46f;
        if ((_player.Position - preview.Target).Length <=
            Math.Max(interactionRange, .80f))
        {
            BeginGroundObjectDrop(
                preview.InventorySlot, preview.ItemId, preview.Target,
                preview.TargetObjectId);
            return;
        }
        _worldActions.QueuePath(
            preview.Target,
            interactionRange,
            WorldActionType.DropGroundObject,
            inventorySlot: preview.InventorySlot,
            itemId: preview.ItemId,
            groundObjectId: preview.TargetObjectId);
    }

    internal void BeginGroundObjectDrop(
        int inventorySlot,
        string itemId,
        Vector2 target,
        Guid? targetObjectId = null)
    {
        if (_player is null ||
            !InventoryContainsAt(inventorySlot, itemId) ||
            !CanReleaseInventoryItemToWorld(itemId))
        {
            ReportBlockedAction(
                "drop-item-unavailable",
                "That item is no longer available to drop.");
            return;
        }
        if (targetObjectId is { } campfireId)
        {
            var targetObject = FindGroundObject(campfireId);
            if (targetObject is null ||
                !(itemId == ItemIds.Rope &&
                  CaveEntranceService.IsHole(targetObject)) &&
                !(itemId is ItemIds.Dirt or ItemIds.Sand &&
                  CanFillExcavation(targetObject, itemId, out _)) &&
                !CampfireService.CanAddFuel(
                    targetObject, itemId, _worldGameSeconds))
            {
                ReportBlockedAction(
                    "campfire-fuel-blocked",
                    "That campfire cannot accept this log.");
                return;
            }
        }
        else if (!CanPlaceInventoryItemAt(
                     itemId, target, out _, out var reason))
        {
            ReportBlockedAction("drop-location-blocked", reason);
            return;
        }

        _activeTreeId = null;
        _activeGroundPickupId = null;
        _activeGroundDrop = new(
            inventorySlot, itemId, target, targetObjectId);
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
        if (drop.TargetObjectId is { } campfireId)
        {
            var targetObject = FindGroundObject(campfireId);
            if (drop.ItemId == ItemIds.Rope &&
                targetObject is not null &&
                CaveEntranceService.IsHole(targetObject))
            {
                TryInstallCaveRope(
                    campfireId, drop.InventorySlot);
                _player.Stop();
                return;
            }
            if (drop.ItemId is ItemIds.Dirt or ItemIds.Sand &&
                targetObject is not null &&
                CaveEntranceService.CanFill(targetObject))
            {
                TryFillExcavation(
                    campfireId, drop.InventorySlot, drop.ItemId);
                _player.Stop();
                return;
            }
            if (!TryAddCampfireFuel(
                    campfireId, drop.InventorySlot, drop.ItemId))
            {
                ReportBlockedAction(
                    "campfire-fuel-blocked",
                    "That campfire cannot accept this log.");
            }
            _player.Stop();
            return;
        }
        if (!CanPlaceInventoryItemAt(
                drop.ItemId, drop.Target, out var gpu, out var reason))
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
            PlaceableObjectCatalog.IsPlaceable(drop.ItemId)
                ? $"You place the {ItemCatalog.Get(drop.ItemId).Name}."
                : $"You drop the {ItemCatalog.Get(drop.ItemId).Name}.",
            ChatMessageStyle.Action);
        _player.Stop();
    }

    private WorldGroundObject? FindGroundObject(Guid id) =>
        _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(gpu => gpu.Chunk.GroundObjects)
            .FirstOrDefault(item => item.Id == id);

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
        if (itemId == ItemIds.CaveHole &&
            _activeWorldLevel == (int)WorldLevel.Underground)
        {
            frame = null!;
            texture = 0;
            atlasKey = "";
            shadowKey = null;
            return false;
        }
        var item = ItemCatalog.Get(itemId);
        if (item.SpriteCell is not { } sourceCell)
        {
            frame = null!;
            texture = 0;
            atlasKey = "";
            shadowKey = null;
            return false;
        }
        var cell =
            itemId == ItemIds.CaveEntrance &&
            _activeWorldLevel == (int)WorldLevel.Underground
                ? 11
                : sourceCell;

        if (item.HasTag(ItemTag.PlaceableObject))
        {
            if (!_placeableObjectSprites.TryGet(
                    item.Id, out var placeable))
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = placeable.Frame;
            texture = placeable.Texture;
            atlasKey = PlaceableObjectAtlasKey(
                item.Id, shadow: false);
            shadowKey = PlaceableObjectAtlasKey(
                item.Id, shadow: true);
            return true;
        }

        if (item.HasTag(ItemTag.Tool))
        {
            if (!_groundToolSprites.TryGetValue(
                    item.Id, out var toolSprite))
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = toolSprite.Frame;
            texture = toolSprite.Texture;
            atlasKey = GroundToolAtlasKey(item.Id, shadow: false);
            shadowKey = GroundToolAtlasKey(item.Id, shadow: true);
            return true;
        }

        if (item.HasTag(ItemTag.FibreNetSprite))
        {
            if ((uint)cell >= (uint)_fibreNetSprites.Frames.Length ||
                _fibreNetSprites.Frames[cell] is not { } fibreFrame ||
                _fibreNetSprites.Textures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = fibreFrame;
            texture = _fibreNetSprites.Textures[cell];
            atlasKey = FibreNetAtlasKey(cell, shadow: false);
            shadowKey = _fibreNetSprites.Shadows[cell] is null
                ? null
                : FibreNetAtlasKey(cell, shadow: true);
            return true;
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

        if (item.HasTag(ItemTag.MiningSprite))
        {
            if ((uint)cell >= (uint)_miningItemFrames.Length ||
                _miningItemFrames[cell] is not { } miningFrame ||
                _miningItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = miningFrame;
            texture = _miningItemTextures[cell];
            atlasKey = MiningAtlasKey(cell, false);
            shadowKey = _miningShadowFrames[cell] is null
                ? null
                : MiningAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.BerrySprite))
        {
            if ((uint)cell >= (uint)_berryItemFrames.Length ||
                _berryItemFrames[cell] is not { } berryFrame ||
                _berryItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = berryFrame;
            texture = _berryItemTextures[cell];
            atlasKey = BerryAtlasKey(cell, false);
            shadowKey = _berryShadowFrames[cell] is null
                ? null
                : BerryAtlasKey(cell, true);
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

        if (item.HasTag(ItemTag.Fish))
        {
            if ((uint)cell >= (uint)_fishItemFrames.Length ||
                _fishItemFrames[cell] is not { } fishFrame ||
                _fishItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = fishFrame;
            texture = _fishItemTextures[cell];
            atlasKey = FishItemAtlasKey(cell, shadow: false);
            shadowKey = _fishItemShadowFrames[cell] is null
                ? null
                : FishItemAtlasKey(cell, shadow: true);
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

    private bool CanPlaceInventoryItemAt(
        string itemId,
        Vector2 target,
        out GpuWorldChunk gpu,
        out string reason) =>
        PlaceableObjectCatalog.IsPlaceable(itemId)
            ? CanPlacePlaceableObjectAt(
                itemId, target, out gpu, out reason)
            : CanPlaceGroundObjectAt(
                target, out gpu, out reason);

    private static bool CanReleaseInventoryItemToWorld(
        string itemId) =>
        PlayerInventory.CanDrop(itemId) ||
        PlaceableObjectCatalog.IsPlaceable(itemId);

    private bool TryGetDropTerrain(
        int tileX,
        int tileY,
        out GpuWorldChunk gpu,
        out string reason)
    {
        var coordinate = new ChunkCoordinate(
            FloorDiv(tileX, WorldChunk.Size),
            FloorDiv(tileY, WorldChunk.Size),
            _activeWorldLevel);
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
        if (!chunk.IsRenderable(localX, localY))
        {
            reason = "You cannot drop that into the void.";
            return false;
        }
        if (_activeWorldLevel == (int)WorldLevel.Underground &&
            chunk.SampleUndergroundDensity(localX + .5f, localY + .5f) <
            CaveHydrologyField.Boundary)
        {
            reason = "You cannot drop that into the void.";
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
        foreach (var chunk in _worldChunks.Values
                     .Where(IsActiveWorldChunk)
                     .Select(value => value.Chunk))
        {
            if (chunk.Trees.Any(tree =>
                    (candidate - new Vector2(
                        tree.X + .5f, tree.Y + .5f)).LengthSquared <
                    treeClearance * treeClearance))
                return false;
            if (chunk.GroundObjects.Any(item =>
                    PlaceableObjectCatalog.TryGet(
                        item.ItemId, out var definition)
                        ? PlaceableObjectCatalog.ContainsPoint(
                            definition,
                            new Vector2(item.X, item.Y),
                            candidate,
                            itemClearance)
                        : (candidate - new Vector2(
                              item.X, item.Y)).LengthSquared <
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
