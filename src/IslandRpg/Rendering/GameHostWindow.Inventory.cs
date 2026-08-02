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
                _ => ["Use", "Drop", "Examine"]
            },
            SceneClientBounds());
    }

    private void SwapInventorySlots(int source, int target)
    {
        if (_activePlayer is null || source == target || target < 0)
            return;
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

        var source = inventory[_activeInventorySlot]!;
        var target = inventory[slot]!;
        if (source is ItemIds.SharpenedRock or ItemIds.PlantFibres &&
            target is ItemIds.SharpenedRock or ItemIds.PlantFibres &&
            CanCraftRecipe("stone-knife") &&
            TryCraftInventoryRecipe(
                "stone-knife", out var beforeKnife,
                out var craftedKnife))
        {
            SaveActivePlayerInventory(craftedKnife);
            _chatUi.AddMessage(
                "You bind the sharp rock with fibre and create a stone knife.",
                ChatMessageStyle.Action);
            CompletePlayerCraft("stone-knife", beforeKnife, craftedKnife);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.SmallRocks &&
            TrySharpenInventoryTool(
                _activeInventorySlot, slot,
                out var sharpenedTool))
        {
            var toolName = ItemCatalog.Get(
                sharpenedTool[slot]!.ItemId).Name;
            SaveActivePlayerInventory(sharpenedTool);
            _chatUi.AddMessage(
                $"You use the small rocks to sharpen the {toolName}.",
                ChatMessageStyle.Action);
            _activeInventorySlot = -1;
            return;
        }
        if (ItemCatalog.Get(source).HasTag(ItemTag.Knife) &&
            ItemCatalog.Get(target).HasTag(ItemTag.Log) &&
            CanCraftRecipe("plank") &&
            TryCraftInventoryRecipe(
                "plank", out var beforePlank,
                out var carvedPlank))
        {
            SaveActivePlayerInventory(carvedPlank);
            _chatUi.AddMessage(
                $"You carve the log into a plank with the " +
                $"{ItemCatalog.Get(source).Name}.",
                ChatMessageStyle.Action);
            CompletePlayerCraft("plank", beforePlank, carvedPlank);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.SharpenedRock &&
            target == ItemIds.Sticks &&
            CanCraftRecipe("stone-axe") &&
            TryCraftInventoryRecipe(
                "stone-axe", out var beforeAxe,
                out var craftedAxe))
        {
            SaveActivePlayerInventory(craftedAxe);
            _chatUi.AddMessage(
                "You fasten the sharp rock to the sticks and create a stone axe.",
                ChatMessageStyle.Action);
            CompletePlayerCraft("stone-axe", beforeAxe, craftedAxe);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.MediumRock &&
            target == ItemIds.Sticks &&
            CanCraftRecipe("stone-hammer") &&
            TryCraftInventoryRecipe(
                "stone-hammer", out var beforeHammer,
                out var craftedHammer))
        {
            SaveActivePlayerInventory(craftedHammer);
            _chatUi.AddMessage(
                "You fasten the medium rock to the sticks and create a stone hammer.",
                ChatMessageStyle.Action);
            CompletePlayerCraft(
                "stone-hammer", beforeHammer, craftedHammer);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.MediumRock &&
            target == ItemIds.MediumRock &&
            CanCraftRecipe("sharpened-rock") &&
            TryCraftInventoryRecipe(
                "sharpened-rock", out var beforeSharpening,
                out var sharpened))
        {
            SaveActivePlayerInventory(sharpened);
            _chatUi.AddMessage(
                "You strike the rocks together and create a sharp rock.",
                ChatMessageStyle.Action);
            CompletePlayerCraft(
                "sharpened-rock", beforeSharpening, sharpened);
            _activeInventorySlot = -1;
            return;
        }
        if ((source == ItemIds.LargeRock ||
             ItemCatalog.Get(source).HasTag(ItemTag.Hammer)) &&
            target is ItemIds.LargeRock or ItemIds.MediumRock)
        {
            var recipeId = target == ItemIds.LargeRock
                ? "medium-rock"
                : "small-rocks";
            if (!TryCraftInventoryRecipe(
                    recipeId, out var beforeBreaking,
                    out var broken))
            {
                ReportBlockedAction(
                    "break-rock-inventory-full",
                    "You need an empty inventory slot for the broken pieces.");
                return;
            }
            var hammerBlunted = false;
            if (source == ItemIds.StoneHammer &&
                Random.Shared.NextSingle() < .01f)
            {
                var hammerSlot = Array.FindIndex(
                    broken.ItemIds(), value =>
                        value == ItemIds.StoneHammer);
                hammerBlunted = hammerSlot >= 0 &&
                    broken.TryReplace(
                        hammerSlot, ItemIds.BluntStoneHammer);
            }
            SaveActivePlayerInventory(broken);
            _chatUi.AddMessage(
                target == ItemIds.LargeRock
                    ? "You split the large rock into two medium rocks."
                    : "You break the medium rock into two handfuls of pebbles.",
                ChatMessageStyle.Action);
            CompletePlayerCraft(
                recipeId, beforeBreaking, broken);
            if (hammerBlunted)
            {
                _chatUi.AddMessage(
                    "Your stone hammer becomes blunt. Use small rocks on it to sharpen it.",
                    ChatMessageStyle.Warning);
                AddBluntToolMonologue(ItemIds.StoneHammer);
            }
            _activeInventorySlot = -1;
            return;
        }
        _chatUi.AddMessage(
            $"You try to use {ItemCatalog.Get(source).Name} with " +
            $"{ItemCatalog.Get(target).Name}, but nothing happens.",
            ChatMessageStyle.Action);
        _activeInventorySlot = -1;
    }

    private bool TryCraftInventoryRecipe(
        string recipeId,
        out InventoryContainer before,
        out InventoryContainer after)
    {
        before = ActivePlayerInventory();
        var recipe = CraftingSkill.Recipes.First(value =>
            value.Id == recipeId);
        return CraftingService.TryCraftDetailed(
            recipe,
            CraftingSkill.LevelForExperience(
                _activePlayer?.CraftingExperience ?? 0),
            before, out after,
            HasRequiredCraftingStation(recipe)) ==
            CraftingService.CraftResult.Success;
    }

    private bool TrySharpenInventoryTool(
        int rocksSlot, int toolSlot,
        out InventoryContainer inventory)
    {
        inventory = ActivePlayerInventory();
        if (inventory[rocksSlot]?.ItemId != ItemIds.SmallRocks ||
            inventory[toolSlot] is not { } tool)
            return false;
        var sharpened = tool.ItemId switch
        {
            ItemIds.BluntStoneAxe => ItemIds.StoneAxe,
            ItemIds.BluntStoneHammer => ItemIds.StoneHammer,
            _ => null
        };
        if (sharpened is null ||
            !inventory.TryTake(rocksSlot, 1, out _))
            return false;
        return inventory.TryReplace(toolSlot, sharpened);
    }

    private void AddBluntToolMonologue(string toolId)
    {
        var toolName = ItemCatalog.Get(toolId).Name;
        var thought =
            $"My {toolName} has gone blunt. Maybe I should try using some small rocks to sharpen it.";
        _chatUi.AddMessage(thought, ChatMessageStyle.Monologue);
        ShowOverheadSpeech(thought);
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
