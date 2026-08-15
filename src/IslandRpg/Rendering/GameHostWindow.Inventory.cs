using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void RenderInventoryPanel()
    {
        RenderInventoryPanel(
            new(
                _gameUi.Panel.Bounds,
                _activePlayer?.Inventory ?? [],
                _activeInventorySlot,
                _inventoryDraggingSlot,
                allowDragOutsideToGame: true,
                quantities: _activePlayer?.InventoryQuantities),
            renderDragPreview: true);
    }

    private void RenderInventoryPanel(
        InventoryPanelState inventoryPanel,
        bool renderDragPreview)
    {
        var panel = inventoryPanel.Bounds;
        var inventory = inventoryPanel.Inventory;
        var count = inventory
            .Take(inventoryPanel.Capacity)
            .Count(item => item is not null);
        DrawPanelCaption(inventoryPanel.Title, panel);
        if (inventoryPanel.ShowCount)
            DrawUiText(
                $"{count}/{inventoryPanel.Capacity}",
                new System.Numerics.Vector2(
                    panel.X + panel.Z - 48, panel.Y + 13),
                count >= inventoryPanel.Capacity
                    ? new(228, 135, 108, 255)
                    : new(184, 177, 149, 255));

        foreach (var slot in inventoryPanel.VisibleSlots)
        {
            var bounds = inventoryPanel.SlotBounds(slot);
            if (slot >= inventory.Length ||
                inventory[slot] is not { } itemId ||
                slot == inventoryPanel.DraggingSlot)
                continue;
            var itemUv = InventoryItemUv(itemId);
            var itemTexture = InventoryItemTexture(itemId);
            var itemFrame = InventoryItemFrame(itemId);
            var pixelFrame = InventoryItemPixelFrame(itemId);
            var hasSprite = itemTexture != 0 && itemUv is not null;
            if (hasSprite)
                DrawUiSprite(
                    itemFrame,
                    itemTexture,
                    SpritePixelLayout.CenterOpaquePixels(
                        pixelFrame, bounds),
                    brightness: InventoryItemBrightness(itemId),
                    uvRectangle: itemUv,
                    grayscaleAmount: InventoryItemGrayscale(itemId),
                    spriteOutline: slot == inventoryPanel.ActiveSlot
                        ? Vector3.One
                        : Vector3.Zero);
            else
                DrawCenteredUiText(
                    InventoryItemCaption(itemId),
                    bounds, new(211, 198, 158, 255));
            if (slot == inventoryPanel.ActiveSlot && !hasSprite)
            {
                DrawPanelOutline(bounds, 0, new(.96f, .95f, .88f, 1));
                    DrawPanelOutline(bounds, 1, new(.74f, .72f, .65f, 1));
            }
            var quantity = inventoryPanel.QuantityAt(slot);
            if (quantity > 1)
            {
                var text = quantity.ToString();
                var width =
                    _quantityFont?.MeasureString(text).X ??
                    MeasureUiText(text);
                var position = new System.Numerics.Vector2(
                    bounds.X + bounds.Z - width - 3,
                    bounds.Y + 2);
                if (_quantityFont is not null &&
                    _fontRenderer is not null)
                {
                    _uiColorBatch.Flush();
                    _quantityFont.DrawText(
                        _fontRenderer,
                        text,
                        position + System.Numerics.Vector2.One,
                        new FontStashSharp.FSColor(0, 0, 0, 120));
                    _quantityFont.DrawText(
                        _fontRenderer,
                        text,
                        position,
                        new FontStashSharp.FSColor(
                            255, 255, 255, 205));
                }
            }
        }

        if (renderDragPreview &&
            inventoryPanel.Bounds.Contains(MouseState.Position) &&
            (uint)inventoryPanel.DraggingSlot < (uint)inventory.Length &&
            inventory[inventoryPanel.DraggingSlot] is { } draggedItemId)
        {
            var itemUv = InventoryItemUv(draggedItemId);
            var dragBounds = new Vector4(
                MouseState.Position.X - 16,
                MouseState.Position.Y - 16, 32, 32);
            var itemTexture = InventoryItemTexture(draggedItemId);
            var itemFrame = InventoryItemFrame(draggedItemId);
            var pixelFrame = InventoryItemPixelFrame(draggedItemId);
            if (itemTexture != 0 && itemUv is not null)
                DrawUiSprite(
                    itemFrame,
                    itemTexture,
                    SpritePixelLayout.CenterOpaquePixels(
                        pixelFrame, dragBounds),
                    brightness: InventoryItemBrightness(draggedItemId),
                    uvRectangle: itemUv,
                    grayscaleAmount: InventoryItemGrayscale(draggedItemId),
                    drawOpacity: .62f,
                    spriteOutline:
                    inventoryPanel.DraggingSlot == inventoryPanel.ActiveSlot
                        ? Vector3.One
                        : Vector3.Zero);
            else
            {
                DrawCenteredUiText(
                    InventoryItemCaption(draggedItemId), dragBounds,
                    new(211, 198, 158, 180));
                DrawPanelOutline(
                    dragBounds, 0, new(.96f, .95f, .88f, .7f));
            }
        }
    }

    private void UpdateInventoryInteraction(
        InventoryPanelState panel,
        Vector2 pointer,
        bool leftDown,
        bool rightDown)
    {
        var interaction = _inventoryInteraction.Update(
            panel, pointer, leftDown, rightDown,
            interactionBlocked: _inventoryContext.Visible);
        if (_inventoryDraggingSlot >= 0 &&
            panel.AllowDragOutsideToGame)
            UpdateGroundDropPreview(pointer);
        else if (!panel.AllowDragOutsideToGame)
            _groundDropPreview = null;

        switch (interaction.Type)
        {
            case InventoryInteractionType.Activate:
                ActivateInventorySlot(interaction.SourceSlot);
                break;
            case InventoryInteractionType.Swap:
                SwapInventorySlots(
                    interaction.SourceSlot, interaction.TargetSlot);
                break;
            case InventoryInteractionType.DropOutsideToGame:
                if (_groundDropPreview is
                    {
                        Valid: true
                    } preview &&
                    preview.InventorySlot == interaction.SourceSlot)
                    QueueGroundObjectDrop(preview);
                break;
            case InventoryInteractionType.OpenContextMenu:
                OpenInventoryContextMenu(
                    interaction.SourceSlot, pointer);
                break;
            case InventoryInteractionType.ClearSelection:
                _activeInventorySlot = -1;
                break;
        }
        if (!leftDown)
            _groundDropPreview = null;
    }

    private void OpenInventoryContextMenu(int slot, Vector2 pointer)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } itemId)
            return;
        _inventoryContextSlot = slot;
        _treeContext.Close();
        _groundObjectContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _inventoryContext.Open(
            pointer,
            ItemCatalog.Get(itemId) switch
            {
                { } item when item.HasTag(ItemTag.Medicine) =>
                    ["Apply", "Drop", "Examine"],
                { } item when
                    SurvivalService.TryFoodEffect(item.Id, out _) =>
                    ["Eat", "Drop", "Examine"],
                { } item when item.HasTag(ItemTag.Shovel) =>
                    ["Dig", "Drop", "Examine"],
                { } item when item.HasTag(ItemTag.Seed) =>
                    ["Plant", "Drop", "Examine"],
                { } item when item.HasTag(ItemTag.PlaceableObject) =>
                    ["Place", "Examine"],
                { } item when BucketService.IsFilled(item.Id) =>
                    ["Empty", "Drop", "Examine"],
                _ => ["Use", "Drop", "Examine"]
            },
            SceneClientBounds());
    }

    private void SwapInventorySlots(int source, int target)
    {
        if (_activePlayer is null || source == target || target < 0)
            return;
        if (IsNetworkWorld)
        {
            SendNetworkInventorySwap(source, target);
            return;
        }
        var inventory = ActivePlayerInventory();
        if (!inventory.TrySwap(source, target))
            return;
        if (_activeInventorySlot == source)
            _activeInventorySlot = target;
        else if (_activeInventorySlot == target)
            _activeInventorySlot = source;
        SaveActivePlayerInventory(inventory);
    }

    private void ActivateInventorySlot(int slot)
    {
        if (IsNetworkWorld && TryToggleNetworkTradeOffer(slot))
            return;
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } itemId)
            return;
        if (ItemCatalog.Get(itemId).HasTag(
                ItemTag.PlaceableObject))
        {
            BeginPlaceableObjectPlacement(slot, itemId);
            return;
        }
        if (BucketService.IsEmpty(itemId))
        {
            BeginBucketFillTargeting(slot);
            return;
        }
        if (BucketService.IsFilled(itemId))
        {
            EmptyBucket(slot);
            return;
        }
        if (_activeInventorySlot == slot)
        {
            _activeInventorySlot = -1;
            return;
        }
        if (_activeInventorySlot < 0 ||
            (uint)_activeInventorySlot >= (uint)inventory.Length ||
            inventory[_activeInventorySlot] is null)
        {
            _activeInventorySlot = slot;
            return;
        }

        var sourceSlot = _activeInventorySlot;
        var source = inventory[sourceSlot]!;
        var target = inventory[slot]!;
        if (IsNetworkWorld)
        {
            SendNetworkItemCombination(sourceSlot, slot);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.SmallRocks &&
            ToolUpkeepService.TrySharpenStoneTool(
                inventory, sourceSlot, slot, out var sharpenedTool))
        {
            var toolName = ItemCatalog.Get(sharpenedTool[slot]!).Name;
            SaveActivePlayerInventory(
                PlayerInventory.Load(sharpenedTool));
            _chatUi.AddMessage(
                $"You use the small rocks to sharpen the {toolName}.",
                ChatMessageStyle.Action);
            _activeInventorySlot = -1;
            return;
        }
        var recipe = ItemCombinationService.FindRecipe(source, target);
        if (recipe is not null)
        {
            TryCraftRecipe(recipe);
            _activeInventorySlot = -1;
            return;
        }
        _chatUi.AddMessage(
            $"You try to use {ItemCatalog.Get(source).Name} with " +
            $"{ItemCatalog.Get(target).Name}, but nothing happens.",
            ChatMessageStyle.Action);
        _activeInventorySlot = slot;
    }

    private void HandleInventoryContextSelection(int option)
    {
        if (_activePlayer is null) return;
        var inventory = _activePlayer.Inventory ?? [];
        var slot = _inventoryContextSlot;
        _inventoryContextSlot = -1;
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } itemId)
            return;
        var item = ItemCatalog.Get(itemId);
        if (item.HasTag(ItemTag.PlaceableObject))
        {
            if (option == 0)
                BeginPlaceableObjectPlacement(slot, itemId);
            else if (option == 1)
                _chatUi.AddMessage(
                    item.Examine, ChatMessageStyle.Normal);
            return;
        }
        if (option == 0)
        {
            if (SurvivalService.TryFoodEffect(itemId, out _))
            {
                EatInventoryItem(slot, itemId);
                return;
            }
            if (item.HasTag(ItemTag.Shovel))
            {
                BeginCaveDigTargeting(slot);
                return;
            }
            if (item.HasTag(ItemTag.Seed))
            {
                TryPlantSeed(slot, itemId);
                return;
            }
            if (BucketService.IsEmpty(itemId))
            {
                BeginBucketFillTargeting(slot);
                return;
            }
            if (BucketService.IsFilled(itemId))
            {
                EmptyBucket(slot);
                return;
            }
            ActivateInventorySlot(slot);
            return;
        }
        if (option == 2)
        {
            _chatUi.AddMessage(
                ItemDescriptionService.Describe(item),
                ChatMessageStyle.Normal);
            return;
        }
        if (option != 1) return;
        if (!PlayerInventory.CanDrop(itemId))
        {
            ReportBlockedAction(
                "item-cannot-be-dropped",
                "You cannot drop that item.");
            return;
        }
        TryDropGroundObject(slot, itemId);
    }
}
