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
        Guid? TargetObjectId = null,
        int Rotation = 0);

    private sealed record ActiveGroundDrop(
        int InventorySlot,
        string ItemId,
        Vector2 Target,
        Guid? TargetObjectId = null);

    private bool AtlasOverlapsActor(
        SpriteAtlasEntry entry,
        (float Left, float Top, float Right, float Bottom) objectBounds,
        ActorVisual actor,
        (float Left, float Top, float Right, float Bottom) actorBounds,
        float scale)
    {
        if (objectBounds.Left >= actorBounds.Right ||
            objectBounds.Right <= actorBounds.Left ||
            objectBounds.Top >= actorBounds.Bottom ||
            objectBounds.Bottom <= actorBounds.Top)
            return false;

        if (scale <= 0) return false;
        var actorFrame = actor.Frame;
        for (var displayY = 0; displayY < actorFrame.Height; displayY++)
        for (var displayX = 0; displayX < actorFrame.Width; displayX++)
        {
            var sourceX = actor.Mirror
                ? actorFrame.Width - 1 - displayX
                : displayX;
            var actorAlpha =
                actorFrame.Rgba[
                    (displayY * actorFrame.Width + sourceX) * 4 + 3];
            if (actorAlpha < 32) continue;
            var screenX =
                actorBounds.Left + (displayX + .5f) * scale;
            var screenY =
                actorBounds.Top + (displayY + .5f) * scale;
            if (screenX < objectBounds.Left ||
                screenX >= objectBounds.Right ||
                screenY < objectBounds.Top ||
                screenY >= objectBounds.Bottom)
                continue;
            var objectX = (int)(
                (screenX - objectBounds.Left) / scale);
            var objectY = (int)(
                (screenY - objectBounds.Top) / scale);
            if ((uint)objectX >= entry.Frame.Width ||
                (uint)objectY >= entry.Frame.Height)
                continue;
            var objectAlpha =
                entry.Frame.Rgba[
                    (objectY * entry.Frame.Width + objectX) * 4 + 3];
            if (objectAlpha >= 32)
                return true;
        }
        return false;
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
            if (!IsNetworkWorld)
            {
                foreach (var instance in gpu.Chunk.TreeInstances)
                    if (instance.State == TreeLifecycleState.Stump)
                        _stumpHoverScratch.Add(
                            WorldHoverSelection.TileKey(
                                instance.X, instance.Y));
            }
            foreach (var tree in gpu.Chunk.Trees)
            {
                if (_stumpHoverScratch.Contains(
                        WorldHoverSelection.TileKey(
                            tree.X, tree.Y)) ||
                    IsNetworkWorld && IsNetworkTreeDepleted(tree) ||
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
        if (IsNetworkWorld &&
            TryGetNetworkGroundObjectUnderMouse(
                mouse, out groundObject, out chunk))
            return true;
        groundObject = null!;
        chunk = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
        foreach (var candidate in gpu.Chunk.GroundObjects)
        {
            if (IsNetworkWorld &&
                _networkKnownWorldObjectIds.Contains(candidate.Id))
                continue;
            if (ConstructionService.IsConstructible(candidate.ItemId) &&
                !ConstructionService.IsConstructionSite(candidate))
                continue;
            if (!TryGroundObjectVisual(
                    candidate, out var frame, out _, out _, out _))
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

    private bool TryGroundObjectVisual(
        WorldGroundObject value,
        out SpriteFrame frame,
        out int texture,
        out string atlasKey,
        out string? shadowKey)
    {
        if (HouseCatalog.IsHouse(value.ItemId))
        {
            atlasKey = HouseVisuals.Resolve(value);
            shadowKey = null;
            if (_treeAtlas.TryGetValue(atlasKey, out var houseAtlas))
            {
                frame = houseAtlas.Frame;
                texture = houseAtlas.Texture;
                return true;
            }
            texture = 0;
            frame = null!;
            return false;
        }
        if (DefenceBuildingCatalog.IsDefence(value.ItemId))
        {
            atlasKey = DefenceBuildingVisuals.Resolve(value);
            shadowKey = null;
            if (_treeAtlas.TryGetValue(atlasKey, out var defenceAtlas))
            {
                frame = defenceAtlas.Frame;
                texture = defenceAtlas.Texture;
                return true;
            }
            texture = 0;
            frame = null!;
            return false;
        }
        if (GateCatalog.IsGate(value.ItemId))
        {
            atlasKey = GateVisuals.Resolve(value);
            shadowKey = GateVisuals.ResolveShadow(value);
            if (_treeAtlas.TryGetValue(atlasKey, out var gateAtlas))
            {
                frame = gateAtlas.Frame;
                texture = gateAtlas.Texture;
                return true;
            }
            texture = 0;
            frame = null!;
            return false;
        }
        if (!WallCatalog.IsWall(value.ItemId))
            return TryGroundItemVisual(
                value.ItemId,
                out frame, out texture, out atlasKey, out shadowKey);

        var visualFrame = value.VisualFrame is >= 0 and < 5
            ? value.VisualFrame
            : ConstructionService.Angle(value);
        (atlasKey, shadowKey) =
            PalisadeWallVisuals.Resolve(value, visualFrame);
        if (_treeAtlas.TryGetValue(atlasKey, out var atlas))
        {
            frame = atlas.Frame;
            texture = atlas.Texture;
            return true;
        }
        texture = 0;
        frame = null!;
        return false;
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
    {
        if (IsNetworkWorld && CropService.IsCrop(groundObject))
        {
            QueueNetworkObjectAction(
                NetworkWorldActionKind.HarvestCrop, groundObject);
            return;
        }
        _worldActions.QueueGroundObjectPickup(groundObject);
        if (IsNetworkWorld)
            SendNetworkWalkCommand(
                new Vector2(groundObject.X, groundObject.Y));
    }

    private void TryPickUpGroundObject(Guid groundObjectId)
    {
        if (_player is null || _activePlayer is null) return;
        if (IsNetworkWorld)
        {
            var target = FindGroundObject(groundObjectId);
            if (target is null)
            {
                ReportBlockedAction(
                    "network-pickup-missing",
                    "That object is no longer there.");
                return;
            }
            SendNetworkGroundPickup(target);
            return;
        }
        var chunk = _worldChunks.Values.FirstOrDefault(gpu =>
            IsActiveWorldChunk(gpu) &&
            gpu.Chunk.GroundObjects.Any(
                item => item.Id == groundObjectId));
        var groundObject = chunk?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == groundObjectId);
        if (chunk is null || groundObject is null) return;
        if (!CanPlayerAccessGroundObject(groundObject))
        {
            ReportBlockedAction(
                "settlement-cache-forbidden",
                "You are no longer permitted to take from this settlement's cache.");
            return;
        }
        var itemId = groundObject.ItemId;
        var crop = CropService.IsCrop(groundObject);
        if (crop && !CropService.IsReady(
                groundObject, _worldGameSeconds))
        {
            ReportBlockedAction(
                "crop-still-growing",
                "That crop is still growing.");
            return;
        }
        if (PlaceableObjectCatalog.IsPlaceable(itemId))
        {
            ReportBlockedAction(
                "placed-object-fixed",
                "That has been built in place and cannot be picked up.");
            return;
        }
        if (crop) itemId = groundObject.FuelItemId!;
        var inventory = ActivePlayerInventory();
        var harvestedCount = crop
            ? CropService.HarvestCount(_activePlayer.Inventory)
            : 1;
        if (!inventory.TryAdd(itemId, harvestedCount))
        {
            ReportBlockedAction(
                "pickup-inventory-full",
                "Your inventory is too full to pick that up.");
            return;
        }
        if (!chunk.Chunk.GroundObjects.Remove(groundObject)) return;
        NotifyVillagersOfTaking(crop
            ? groundObject with { ItemId = itemId }
            : groundObject);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        if (crop)
            AwardFarmingExperience(
                FarmingSkill.PlantingExperience * harvestedCount);
        QueueChunkSave(chunk.Chunk);
        _chatUi.AddMessage(
            crop
                ? $"You harvest the {ItemCatalog.Get(itemId).Name}."
                : $"You pick up the {ItemCatalog.Get(itemId).Name}.",
            ChatMessageStyle.Action);
        RecordQuestEvent(new(
            QuestEventType.GatherItem, itemId));
    }

    private bool CanPlayerAccessGroundObject(WorldGroundObject groundObject) =>
        _activePlayer is not null &&
        SettlementGroupService.CanAccess(
            _settlementGroup,
            _activePlayer.Id,
            groundObject.OwnerId,
            groundObject.GroupOwnerId);

    internal void BeginGroundObjectPickup(Guid groundObjectId, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var groundObject = FindGroundObject(groundObjectId);
        if (groundObject is null ||
            PlaceableObjectCatalog.IsPlaceable(groundObject.ItemId))
            return;
        if (!CanPlayerAccessGroundObject(groundObject))
        {
            ReportBlockedAction(
                "settlement-cache-forbidden",
                "You are no longer permitted to take from this settlement's cache.");
            return;
        }
        var pickupItemId = CropService.IsCrop(groundObject)
            ? groundObject.FuelItemId
            : groundObject.ItemId;
        if (pickupItemId is null ||
            !ActivePlayerInventory().CanAdd(pickupItemId))
        {
            ReportBlockedAction(
                "pickup-inventory-full",
                "Your inventory is too full to pick that up.");
            _player.Stop();
            return;
        }

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
        if (_wallPlacementPreview.Count > 0)
        {
            RenderWallPlacementPreview();
            return;
        }
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
                    : new Vector3(1f, .48f, .42f),
                tintAmount: .58f,
                grayscaleAmount: 1f,
                preserveDarkTint: true);
            return;
        }
        var world = GroundObjectWorld(new(
            Guid.Empty, preview.ItemId, preview.Target.X, preview.Target.Y));
        if (WallCatalog.IsWall(preview.ItemId))
        {
            var wallPreviewKey =
                PalisadeWallVisuals.FrontFrameKeyFor(preview.ItemId);
            var vertices = new AtlasDrawBatch();
            AddAtlasQuad(wallPreviewKey, world, .58f, vertices);
            GL.UseProgram(_program);
            GL.Uniform3(
                GL.GetUniformLocation(_program, "tint"),
                preview.Valid
                    ? new Vector3(.28f, 1f, .34f)
                    : new Vector3(1f, .48f, .42f));
            GL.Uniform1(
                GL.GetUniformLocation(_program, "tintAmount"), .58f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "grayscaleAmount"), 1f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "preserveDarkTint"), 1);
            DrawTreeBatch(vertices);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "tintAmount"), 0f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "grayscaleAmount"), 0f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "preserveDarkTint"), 0);
            return;
        }
        if (HouseCatalog.IsHouse(preview.ItemId) ||
            DefenceBuildingCatalog.IsDefence(preview.ItemId) ||
            GateCatalog.IsGate(preview.ItemId))
        {
            var vertices = new AtlasDrawBatch();
            var buildingKey = HouseCatalog.IsHouse(preview.ItemId)
                ? HouseVisuals.AtlasKey(preview.ItemId)
                : DefenceBuildingCatalog.IsDefence(preview.ItemId)
                    ? DefenceBuildingVisuals.AtlasKey(preview.ItemId)
                    : GateVisuals.AtlasKey(
                        preview.ItemId, preview.Rotation);
            AddAtlasQuad(buildingKey, world, .58f,
                vertices);
            GL.UseProgram(_program);
            GL.Uniform3(
                GL.GetUniformLocation(_program, "tint"),
                preview.Valid
                    ? new Vector3(.28f, 1f, .34f)
                    : new Vector3(1f, .48f, .42f));
            GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), .58f);
            GL.Uniform1(GL.GetUniformLocation(_program, "grayscaleAmount"), 1f);
            GL.Uniform1(GL.GetUniformLocation(_program, "preserveDarkTint"), 1);
            DrawTreeBatch(vertices);
            GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 0f);
            GL.Uniform1(GL.GetUniformLocation(_program, "grayscaleAmount"), 0f);
            GL.Uniform1(GL.GetUniformLocation(_program, "preserveDarkTint"), 0);
            return;
        }
        if (!TryGroundItemVisual(
                preview.ItemId,
                out var frame,
                out var texture,
                out var atlasKey,
                out var shadowKey))
            return;

        if (shadowKey is not null &&
            _treeAtlas.ContainsKey(shadowKey))
        {
            GL.UseProgram(_program);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "tintAmount"), 0f);
            GL.Uniform1(
                GL.GetUniformLocation(_program, "preserveDarkTint"), 0);
            var shadowVertices = new AtlasDrawBatch();
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
                : new Vector3(1f, .48f, .42f),
            tintAmount: .58f,
            grayscaleAmount: 1f,
            preserveDarkTint: true);
    }

    private void QueueGroundObjectDrop(GroundDropPreview preview)
    {
        if (_player is null || !preview.Valid) return;
        if (IsNetworkWorld)
        {
            if (preview.TargetObjectId is { } targetObjectId &&
                _networkWorldObjects.TryGetValue(
                    targetObjectId, out var targetObject))
            {
                if (preview.ItemId == ItemIds.Rope &&
                    CaveEntranceService.IsHole(targetObject))
                {
                    QueueNetworkCaveObjectAction(
                        NetworkWorldActionKind.InstallCaveRope,
                        targetObject, preview.InventorySlot);
                    return;
                }
                if (CanFillExcavation(
                        targetObject, preview.ItemId, out _) &&
                    FindNetworkCaveFillSlot(targetObject) ==
                    preview.InventorySlot)
                {
                    QueueNetworkCaveObjectAction(
                        NetworkWorldActionKind.FillExcavation,
                        targetObject, preview.InventorySlot);
                    return;
                }
                if (CampfireService.IsCampfire(targetObject))
                {
                    QueueNetworkObjectAction(
                        NetworkWorldActionKind.AddCampfireFuel,
                        targetObject,
                        preview.InventorySlot);
                    return;
                }
            }
            if (PlaceableObjectCatalog.IsPlaceable(preview.ItemId))
            {
                QueueNetworkPointAction(
                    NetworkWorldActionKind.PlaceInventoryWorldObject,
                    preview.Target,
                    preview.InventorySlot,
                    definitionId: preview.ItemId,
                    rotation: preview.Rotation);
                return;
            }
        }
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
        if (IsNetworkWorld)
            SendNetworkWalkCommand(preview.Target);
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
        if (IsNetworkWorld)
        {
            SendNetworkGroundDrop(drop.InventorySlot, drop.Target);
            _player.Stop();
            return;
        }
        var inventory = ActivePlayerInventory();
        WorldGroundObject placed;
        if (PlaceableObjectCatalog.IsPlaceable(drop.ItemId))
        {
            if (!inventory.TryTake(drop.InventorySlot, 1, out _))
            {
                _player.Stop();
                return;
            }
            placed = new(
                Guid.NewGuid(), drop.ItemId,
                drop.Target.X, drop.Target.Y,
                OwnerId: _activePlayer!.Id);
            if (ConstructionService.IsConstructible(drop.ItemId))
                placed = ConstructionService.Begin(placed);
        }
        else
        {
            if (!inventory.TryTake(drop.InventorySlot, 1, out _))
            {
                _player.Stop();
                return;
            }
            placed = new(
                Guid.NewGuid(), drop.ItemId,
                drop.Target.X, drop.Target.Y,
                OwnerId: _activePlayer!.Id);
        }

        gpu.Chunk.GroundObjects.Add(placed);
        if (ConstructionService.IsConstructionSite(placed))
        {
            _activePlayerConstructionId = placed.Id;
            _player.WorkAt(new Vector2(placed.X, placed.Y));
        }
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
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
        if (PlaceableObjectCatalog.IsPlaceable(drop.ItemId))
            RecordQuestEvent(new(
                QuestEventType.BuildObject,
                drop.ItemId));
        _player.Stop();
    }

    private WorldGroundObject? FindGroundObject(Guid id)
    {
        if (IsNetworkWorld &&
            _networkWorldObjects.TryGetValue(id, out var network))
            return network;
        return _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(gpu => gpu.Chunk.GroundObjects)
            .FirstOrDefault(item => item.Id == id);
    }

    private bool InventoryContainsAt(int slot, string itemId)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        return (uint)slot < (uint)inventory.Length &&
               string.Equals(
                   inventory[slot], itemId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool InventoryHasTagAt(int slot, ItemTag tag)
    {
        var inventory = _activePlayer?.Inventory;
        return inventory is not null &&
               (uint)slot < (uint)inventory.Length &&
               inventory[slot] is { } itemId &&
               ItemCatalog.Get(itemId).HasTag(tag);
    }

    private bool TryGroundItemVisual(
        string itemId,
        out SpriteFrame frame,
        out int texture,
        out string atlasKey,
        out string? shadowKey)
    {
        if (HouseCatalog.IsHouse(itemId))
        {
            frame = null!;
            texture = 0;
            atlasKey = HouseVisuals.AtlasKey(itemId);
            shadowKey = null;
            return false;
        }
        if (DefenceBuildingCatalog.IsDefence(itemId))
        {
            frame = null!;
            texture = 0;
            atlasKey = DefenceBuildingVisuals.AtlasKey(itemId);
            shadowKey = null;
            return false;
        }
        if (GateCatalog.IsGate(itemId))
        {
            frame = null!;
            texture = 0;
            atlasKey = GateVisuals.AtlasKey(itemId);
            shadowKey = null;
            return false;
        }
        if (WallCatalog.IsWall(itemId))
        {
            frame = null!;
            texture = 0;
            atlasKey = PalisadeWallVisuals.FrontFrameKeyFor(itemId);
            shadowKey = null;
            return false;
        }
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

        if (item.HasTag(ItemTag.MetalToolSprite))
        {
            if ((uint)cell >= (uint)_metalToolFrames.Length ||
                _metalToolFrames[cell] is not { } metalFrame ||
                _metalToolTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = metalFrame;
            texture = _metalToolTextures[cell];
            atlasKey = MetalToolAtlasKey(cell, false);
            shadowKey = _metalToolShadowFrames[cell] is null
                ? null
                : MetalToolAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.MetalMaterialSprite))
        {
            if ((uint)cell >= (uint)_metalMaterialFrames.Length ||
                _metalMaterialFrames[cell] is not { } metalFrame ||
                _metalMaterialTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = metalFrame;
            texture = _metalMaterialTextures[cell];
            atlasKey = MetalMaterialAtlasKey(cell, false);
            shadowKey = _metalMaterialShadowFrames[cell] is null
                ? null
                : MetalMaterialAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.ProgressionSprite))
        {
            if ((uint)cell >= (uint)_progressionItemFrames.Length ||
                _progressionItemFrames[cell] is not
                    { } progressionFrame ||
                _progressionItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = progressionFrame;
            texture = _progressionItemTextures[cell];
            atlasKey = ProgressionAtlasKey(cell, false);
            shadowKey = _progressionItemShadowFrames[cell] is null
                ? null
                : ProgressionAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.PersonalGoalSprite))
        {
            if ((uint)cell >= (uint)_personalGoalItemFrames.Length ||
                _personalGoalItemFrames[cell] is not { } goalFrame ||
                _personalGoalItemTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = goalFrame;
            texture = _personalGoalItemTextures[cell];
            atlasKey = PersonalGoalAtlasKey(cell, false);
            shadowKey = _personalGoalItemShadowFrames[cell] is null
                ? null
                : PersonalGoalAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.CropSprite))
        {
            if ((uint)cell >= (uint)_cropFrames.Length ||
                _cropFrames[cell] is not { } cropFrame ||
                _cropTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = cropFrame;
            texture = _cropTextures[cell];
            atlasKey = CropAtlasKey(cell, false);
            shadowKey = _cropShadowFrames[cell] is null
                ? null
                : CropAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.SlimeLootSprite))
        {
            if ((uint)cell >= (uint)_slimeLootFrames.Length ||
                _slimeLootFrames[cell] is not { } lootFrame ||
                _slimeLootTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = lootFrame;
            texture = _slimeLootTextures[cell];
            atlasKey = SlimeLootAtlasKey(cell, false);
            shadowKey = _slimeLootShadowFrames[cell] is null
                ? null
                : SlimeLootAtlasKey(cell, true);
            return true;
        }

        if (item.HasTag(ItemTag.SlimeCraftedSprite))
        {
            if ((uint)cell >= (uint)_slimeCraftedFrames.Length ||
                _slimeCraftedFrames[cell] is not { } craftedFrame ||
                _slimeCraftedTextures[cell] == 0)
            {
                frame = null!;
                texture = 0;
                atlasKey = "";
                shadowKey = null;
                return false;
            }
            frame = craftedFrame;
            texture = _slimeCraftedTextures[cell];
            atlasKey = SlimeCraftedAtlasKey(cell, false);
            shadowKey = _slimeCraftedShadowFrames[cell] is null
                ? null
                : SlimeCraftedAtlasKey(cell, true);
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

        if (IsNetworkWorld)
        {
            foreach (var gpu in _worldChunks.Values)
            {
                if (!IsActiveWorldChunk(gpu) ||
                    !_networkWorldObjectIdsByChunk.TryGetValue(
                        gpu.Chunk.Coordinate, out var ids))
                    continue;
                foreach (var objectId in ids)
                {
                    if (!_networkWorldObjects.TryGetValue(
                            objectId, out var item))
                        continue;
                    if (PlaceableObjectCatalog.TryGet(
                            item.ItemId, out var definition)
                            ? PlaceableObjectCatalog.ContainsPoint(
                                definition,
                                new Vector2(item.X, item.Y),
                                candidate,
                                itemClearance)
                            : (candidate - new Vector2(
                                  item.X, item.Y)).LengthSquared <
                              itemClearance * itemClearance)
                        return false;
                }
            }
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
