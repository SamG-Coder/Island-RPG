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
                allowDragOutsideToGame: true),
            renderDragPreview: true);
    }

    private void RenderInventoryPanel(
        InventoryPanelState inventoryPanel,
        bool renderDragPreview)
    {
        var panel = inventoryPanel.Bounds;
        var inventory = inventoryPanel.Inventory;
        var count = PlayerInventory.Count(inventory);
        DrawPanelCaption("Bag", panel);
        DrawUiText(
            $"{count}/{PlayerInventory.Capacity}",
            new System.Numerics.Vector2(panel.X + panel.Z - 48, panel.Y + 13),
            count >= PlayerInventory.Capacity
                ? new(228, 135, 108, 255)
                : new(184, 177, 149, 255));

        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
        {
            var bounds = inventoryPanel.SlotBounds(slot);
            if (slot >= inventory.Length ||
                inventory[slot] is not { } itemId ||
                slot == inventoryPanel.DraggingSlot)
                continue;
            var itemUv = InventoryItemUv(itemId);
            var itemTexture = InventoryItemTexture(itemId);
            var itemFrame = InventoryItemFrame(itemId);
            var hasSprite = itemTexture != 0 && itemUv is not null;
            if (hasSprite)
                DrawUiSprite(
                    itemFrame,
                    itemTexture,
                    bounds,
                    uvRectangle: itemUv,
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
            if (itemTexture != 0 && itemUv is not null)
                DrawUiSprite(
                    itemFrame,
                    itemTexture,
                    dragBounds,
                    uvRectangle: itemUv,
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
        _inventoryContext.Open(
            pointer,
            ItemCatalog.Get(itemId).HasTag(ItemTag.Seed)
                ? ["Plant", "Drop", "Examine"]
                : ["Use", "Drop", "Examine"],
            SceneClientBounds());
    }

    private void SwapInventorySlots(int source, int target)
    {
        if (_activePlayer is null || source == target || target < 0)
            return;
        if (!PlayerInventory.TrySwap(
                _activePlayer.Inventory, source, target,
                out var inventory))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == source)
            _activeInventorySlot = target;
        else if (_activeInventorySlot == target)
            _activeInventorySlot = source;
        _saves.SavePlayer(_activePlayer);
    }

    private void ActivateInventorySlot(int slot)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is null)
            return;
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
        if (source == ItemIds.SmallRocks &&
            PlayerInventory.TrySharpenStoneTool(
                inventory, _activeInventorySlot, slot,
                out var sharpenedTool))
        {
            var toolName = ItemCatalog.Get(sharpenedTool[slot]!).Name;
            _activePlayer = _activePlayer! with
            {
                Inventory = sharpenedTool,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                $"You use the small rocks to sharpen the {toolName}.",
                ChatMessageStyle.Action);
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.SharpenedRock &&
            ItemCatalog.Get(target).HasTag(ItemTag.Log) &&
            CanCraftRecipe("plank") &&
            PlayerInventory.TryCarvePlank(
                inventory, _activeInventorySlot, slot,
                Random.Shared.NextSingle(), out var carvedPlank,
                out var sharpRockDestroyed))
        {
            _activePlayer = _activePlayer! with
            {
                Inventory = carvedPlank,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                sharpRockDestroyed
                    ? "You carve the log into a plank, but the sharp rock breaks."
                    : "You carve the log into a plank with the sharp rock.",
                ChatMessageStyle.Action);
            AwardCraftingExperience("plank");
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.SharpenedRock &&
            target == ItemIds.Sticks &&
            CanCraftRecipe("stone-axe") &&
            PlayerInventory.TryCraftStoneAxe(
                inventory, _activeInventorySlot, slot, out var craftedAxe))
        {
            _activePlayer = _activePlayer! with
            {
                Inventory = craftedAxe,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                "You fasten the sharp rock to the sticks and create a stone axe.",
                ChatMessageStyle.Action);
            AwardCraftingExperience("stone-axe");
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.MediumRock &&
            target == ItemIds.Sticks &&
            CanCraftRecipe("stone-hammer") &&
            PlayerInventory.TryCraftStoneHammer(
                inventory, _activeInventorySlot, slot,
                out var craftedHammer))
        {
            _activePlayer = _activePlayer! with
            {
                Inventory = craftedHammer,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                "You fasten the medium rock to the sticks and create a stone hammer.",
                ChatMessageStyle.Action);
            AwardCraftingExperience("stone-hammer");
            _activeInventorySlot = -1;
            return;
        }
        if (source == ItemIds.MediumRock &&
            target == ItemIds.MediumRock &&
            CanCraftRecipe("sharpened-rock") &&
            PlayerInventory.TrySharpenRock(
                inventory, _activeInventorySlot, slot, out var sharpened))
        {
            _activePlayer = _activePlayer! with
            {
                Inventory = sharpened,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                "You strike the rocks together and create a sharp rock.",
                ChatMessageStyle.Action);
            AwardCraftingExperience("sharpened-rock");
            _activeInventorySlot = -1;
            return;
        }
        if (source is ItemIds.LargeRock or ItemIds.StoneHammer &&
            target is ItemIds.LargeRock or ItemIds.MediumRock)
        {
            if (!PlayerInventory.TryBreakRock(
                    inventory, _activeInventorySlot, slot, out var broken))
            {
                ReportBlockedAction(
                    "break-rock-inventory-full",
                    "You need an empty inventory slot for the broken pieces.");
                return;
            }
            var afterUse = broken;
            var hammerBlunted = source == ItemIds.StoneHammer &&
                PlayerInventory.TryBluntStoneTool(
                    broken, ItemIds.StoneHammer,
                    Random.Shared.NextSingle(), out afterUse);
            _activePlayer = _activePlayer! with
            {
                Inventory = hammerBlunted ? afterUse : broken,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                target == ItemIds.LargeRock
                    ? "You split the large rock into two medium rocks."
                    : "You break the medium rock into two handfuls of pebbles.",
                ChatMessageStyle.Action);
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
        if (option == 0)
        {
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
                item.WoodcuttingPower > 0
                    ? $"{item.Examine} Woodcutting power: " +
                      $"{item.WoodcuttingPower}."
                    : item.Examine,
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
