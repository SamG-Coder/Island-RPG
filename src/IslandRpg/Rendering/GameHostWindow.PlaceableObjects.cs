using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly Dictionary<string, SpriteGroundContact>
        _resourceGroundContacts =
            new(StringComparer.OrdinalIgnoreCase);

    private void BeginPlaceableObjectPlacement(
        int inventorySlot, string itemId)
    {
        if (_craftingWindowOpen ||
            !InventoryContainsAt(inventorySlot, itemId) ||
            !PlaceableObjectCatalog.IsPlaceable(itemId))
            return;
        _activeInventorySlot = inventorySlot;
        _placeableObjectPlacement.Begin(inventorySlot, itemId);
        _groundDropPreview = null;
        _inventoryContext.Close();
        _chatUi.AddMessage(
            $"Choose clear, level ground for the " +
            $"{ItemCatalog.Get(itemId).Name}. Right-click to cancel.",
            ChatMessageStyle.Action);
    }

    private bool UpdatePlaceableObjectPlacementInput(
        bool leftDown, bool rightDown)
    {
        if (!_placeableObjectPlacement.Active ||
            _placeableObjectPlacement.ItemId is not { } itemId)
            return false;

        if (_buildingPlacementAwaitingRelease)
        {
            if (!leftDown) _buildingPlacementAwaitingRelease = false;
            return true;
        }

        if (_activeBuildingRecipe is { } buildingRecipe &&
            WallCatalog.IsWall(buildingRecipe.ResultItemId))
            return UpdateWallPlacementInput(leftDown, rightDown);

        var slot = _placeableObjectPlacement.InventorySlot;
        if (_placeableObjectPlacement.ConsumesInventoryItem &&
            !InventoryContainsAt(slot, itemId))
        {
            CancelPlaceableObjectPlacement();
            return false;
        }

        UpdatePlaceableObjectPreview(slot, itemId);
        if (rightDown && !_gameRightWasDown)
        {
            CancelPlaceableObjectPlacement();
            return true;
        }

        if (leftDown && !_gameLeftWasDown &&
            _groundDropPreview is { } preview)
        {
            if (_activeBuildingRecipe is { } recipe)
            {
                if (preview.Valid)
                {
                    if (!PlacePlayerBuildingFoundation(preview, recipe))
                        return true;
                }
                else
                {
                    ReportBlockedAction(
                        $"building-placement-{recipe.Id}",
                        CanAffordBuilding(recipe)
                            ? "That location cannot hold this building."
                            : $"You need {DescribeBuildingMaterials(recipe)} to place this foundation.");
                    return true;
                }
            }
            else if (preview.Valid)
                QueueGroundObjectDrop(preview);
            else
                return true;
            _placeableObjectPlacement.Cancel();
            _activeBuildingRecipe = null;
            _groundDropPreview = null;
        }
        return true;
    }

    private void UpdatePlaceableObjectPreview(
        int inventorySlot, string itemId)
    {
        _groundDropPreview = null;
        var pointer = MouseState.Position;
        if (IsPointerOverGameUi(pointer) ||
            _minimapUi.HitTest(pointer))
            return;
        var terrainTarget = ScreenToTerrain(SceneMousePosition());
        var target = _activeBuildingRecipe is not null
            ? PlaceableObjectCatalog.SnapBuildingToTile(terrainTarget)
            : PlaceableObjectCatalog.SnapToGrid(itemId, terrainTarget);
        var valid = CanPlacePlaceableObjectAt(
            itemId, target, out _, out _);
        if (_activeBuildingRecipe is { } recipe)
            valid &= CanAffordBuilding(recipe);
        _groundDropPreview = new(
            inventorySlot,
            itemId,
            target,
            valid);
    }

    private void CancelPlaceableObjectPlacement()
    {
        _placeableObjectPlacement.Cancel();
        _activeBuildingRecipe = null;
        _buildingPlacementAwaitingRelease = false;
        _wallPlacementAnchor = null;
        _wallDragOrientation = null;
        _wallPlacementPreview.Clear();
        _groundDropPreview = null;
    }

    private bool IsPlaceablePlacementActiveOverWorld() =>
        _placeableObjectPlacement.Active &&
        !IsPointerOverGameUi(MouseState.Position);

    private NavigationObstacle[] ActiveNavigationObstacles()
    {
        if (_noClipMode) return [];
        var obstacles = new List<NavigationObstacle>();
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            foreach (var tree in gpu.Chunk.Trees)
            {
                var atlasKey = WorldTreeCatalog.AtlasKey(tree);
                if (!TryGroundContact(atlasKey, out var contact))
                    continue;
                obstacles.Add(ResourceObstacle(
                    new Vector2(tree.X + .5f, tree.Y + .5f),
                    contact));
            }

            foreach (var vegetation in gpu.Chunk.Vegetation)
            {
                if (!MiningNodeCatalog.TryGet(vegetation, out _))
                    continue;
                var stableKey =
                    $"vegetation:{vegetation.X:0.000}:{vegetation.Y:0.000}";
                if (gpu.Chunk.MiningStates.Any(state =>
                        state.StableKey == stableKey &&
                        state.Health == 0))
                    continue;
                var atlasKey =
                    $"{vegetation.GraphicName}#{vegetation.FrameIndex}";
                if (!TryGroundContact(atlasKey, out var contact))
                    continue;
                obstacles.Add(ResourceObstacle(
                    new Vector2(vegetation.X, vegetation.Y),
                    contact));
            }

            foreach (var groundObject in gpu.Chunk.GroundObjects)
            {
                if (!PlaceableObjectCatalog.TryGet(
                        groundObject.ItemId, out var definition))
                    continue;
                if (GateService.IsOpen(groundObject)) continue;
                if (WallCatalog.IsWall(groundObject.ItemId))
                    obstacles.Add(
                        PlaceableObjectCatalog.WallNavigationObstacle(
                            groundObject));
                else
                    obstacles.Add(new(
                        PlaceableObjectCatalog.GroundContactCenter(
                            groundObject.ItemId,
                            new Vector2(groundObject.X, groundObject.Y)),
                        definition.GroundContactWidth,
                        definition.GroundContactDepth));
            }
        }
        return obstacles.ToArray();

        bool TryGroundContact(
            string atlasKey,
            out SpriteGroundContact contact)
        {
            if (_resourceGroundContacts.TryGetValue(
                    atlasKey, out contact))
                return true;
            if (!_treeAtlas.TryGetValue(atlasKey, out var entry))
                return false;
            contact = SpriteGroundContactCalculator.Measure(entry.Frame);
            _resourceGroundContacts[atlasKey] = contact;
            return true;
        }

        static NavigationObstacle ResourceObstacle(
            Vector2 anchor,
            SpriteGroundContact contact)
        {
            // A horizontal sprite offset projects equally along the two
            // opposing isometric world axes.
            var center = anchor + new Vector2(
                contact.LateralOffset,
                -contact.LateralOffset);
            return new(center, contact.Width, contact.Depth);
        }
    }

    private bool CanPlacePlaceableObjectAt(
        string itemId,
        Vector2 target,
        out GpuWorldChunk gpu,
        out string reason)
    {
        gpu = null!;
        if (!PlaceableObjectCatalog.TryGet(
                itemId, out var definition))
        {
            reason = "That object cannot be placed.";
            return false;
        }

        target = _activeBuildingRecipe is not null
            ? PlaceableObjectCatalog.SnapBuildingToTile(target)
            : PlaceableObjectCatalog.SnapToGrid(itemId, target);
        var minimumX = (int)MathF.Floor(
            target.X - definition.FootprintWidth * .5f + .001f);
        var maximumX = (int)MathF.Ceiling(
            target.X + definition.FootprintWidth * .5f - .001f) - 1;
        var minimumY = (int)MathF.Floor(
            target.Y - definition.FootprintDepth * .5f + .001f);
        var maximumY = (int)MathF.Ceiling(
            target.Y + definition.FootprintDepth * .5f - .001f) - 1;
        var lowestHeight = float.MaxValue;
        var highestHeight = float.MinValue;

        for (var tileY = minimumY; tileY <= maximumY; tileY++)
        for (var tileX = minimumX; tileX <= maximumX; tileX++)
        {
            if (!TryGetDropTerrain(
                    tileX, tileY, out var tileGpu, out reason))
                return false;
            gpu ??= tileGpu;
            var localX =
                tileX - tileGpu.Chunk.Coordinate.X * WorldChunk.Size;
            var localY =
                tileY - tileGpu.Chunk.Coordinate.Y * WorldChunk.Size;
            var tile = tileGpu.Chunk.Tiles[
                localY * WorldChunk.Size + localX];
            lowestHeight = Math.Min(
                lowestHeight,
                Math.Min(
                    Math.Min(tile.North, tile.East),
                    Math.Min(tile.South, tile.West)));
            highestHeight = Math.Max(
                highestHeight,
                Math.Max(
                    Math.Max(tile.North, tile.East),
                    Math.Max(tile.South, tile.West)));
        }

        var footprintTileCount =
            (maximumX - minimumX + 1) *
            (maximumY - minimumY + 1);
        if (!BuildingTerrainPlacement.IsSupported(
                footprintTileCount, lowestHeight, highestHeight))
        {
            reason = "The full footprint must be on level ground.";
            return false;
        }

        foreach (var chunk in _worldChunks.Values
                     .Where(IsActiveWorldChunk)
                     .Select(value => value.Chunk))
        {
            if (chunk.Trees.Any(tree =>
                    PlaceableObjectCatalog.ContainsPoint(
                        definition,
                        target,
                        new Vector2(tree.X + .5f, tree.Y + .5f),
                        .28f)))
            {
                reason = "A tree is blocking part of the footprint.";
                return false;
            }

            if (chunk.Vegetation.Any(vegetation =>
                    vegetation.Kind != WorldVegetationKind.Plant &&
                    PlaceableObjectCatalog.ContainsPoint(
                        definition,
                        target,
                        new Vector2(vegetation.X, vegetation.Y),
                        vegetation.Kind == WorldVegetationKind.BerryBush
                            ? .34f
                            : .22f)))
            {
                reason = "Vegetation is blocking part of the footprint.";
                return false;
            }

            foreach (var existing in chunk.GroundObjects)
            {
                var existingCenter = new Vector2(
                    existing.X, existing.Y);
                if (PlaceableObjectCatalog.TryGet(
                        existing.ItemId, out var existingDefinition))
                {
                    if (PlaceableObjectCatalog.Overlaps(
                            definition, target,
                            existingDefinition, existingCenter,
                            PlaceableObjectCatalog.PlacementPadding(
                                definition, existingDefinition)))
                    {
                        reason = "Another object is blocking the footprint.";
                        return false;
                    }
                }
                else if (PlaceableObjectCatalog.ContainsPoint(
                             definition, target,
                             existingCenter, .18f))
                {
                    reason = "An item is blocking part of the footprint.";
                    return false;
                }
            }
        }

        if (_player is not null &&
            PlaceableObjectCatalog.ContainsPoint(
                definition, target, _player.Position, .24f))
        {
            reason = "Move clear of the footprint before placing that.";
            return false;
        }

        reason = "";
        return true;
    }
}
