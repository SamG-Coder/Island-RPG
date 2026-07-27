using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
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

        var slot = _placeableObjectPlacement.InventorySlot;
        if (!InventoryContainsAt(slot, itemId))
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
            _groundDropPreview is
            {
                Valid: true
            } preview)
        {
            QueueGroundObjectDrop(preview);
            _placeableObjectPlacement.Cancel();
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
        var target = PlaceableObjectCatalog.SnapToGrid(
            itemId, ScreenToTerrain(SceneMousePosition()));
        _groundDropPreview = new(
            inventorySlot,
            itemId,
            target,
            CanPlacePlaceableObjectAt(
                itemId, target, out _, out _));
    }

    private void CancelPlaceableObjectPlacement()
    {
        _placeableObjectPlacement.Cancel();
        _groundDropPreview = null;
    }

    private bool IsPlaceablePlacementActiveOverWorld() =>
        _placeableObjectPlacement.Active &&
        !IsPointerOverGameUi(MouseState.Position);

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

        target = PlaceableObjectCatalog.SnapToGrid(itemId, target);
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

        if (highestHeight - lowestHeight > 2)
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

            foreach (var existing in chunk.GroundObjects)
            {
                var existingCenter = new Vector2(
                    existing.X, existing.Y);
                if (PlaceableObjectCatalog.TryGet(
                        existing.ItemId, out var existingDefinition))
                {
                    if (PlaceableObjectCatalog.Overlaps(
                            definition, target,
                            existingDefinition, existingCenter))
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
