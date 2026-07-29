using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float CookingCollectionRange = 1.35f;

    private sealed record ActiveCooking(
        Guid CampfireId,
        int InventorySlot,
        string RawItemId,
        Vector2 Target,
        double? ReadyAt = null);

    private ActiveCooking? _activeCooking;

    private bool TrySelectedRawCookingItem(
        out int slot, out string itemId)
    {
        slot = _activeInventorySlot;
        itemId = "";
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } selected ||
            !CookingSkill.TryProfile(selected, out _))
            return false;
        itemId = selected;
        return true;
    }

    private bool CanCookOnCampfire(
        WorldGroundObject campfire,
        string itemId,
        out string reason,
        bool allowActive = false)
    {
        if (CampfireService.State(
                campfire, _worldGameSeconds) != CampfireState.Lit)
        {
            reason = "The campfire must be lit before you can cook.";
            return false;
        }
        if (!CookingSkill.TryProfile(itemId, out var profile))
        {
            reason = "That item cannot be cooked on this fire.";
            return false;
        }
        var level = CookingSkill.LevelForExperience(
            _activePlayer?.CookingExperience ?? 0);
        if (level < profile.RequiredLevel)
        {
            reason =
                $"You need Cooking level {profile.RequiredLevel} " +
                $"to cook {ItemCatalog.Get(itemId).Name}.";
            return false;
        }
        if (!allowActive && _activeCooking is not null)
        {
            reason = "You are already cooking something.";
            return false;
        }
        reason = "";
        return true;
    }

    private void QueueCampfireCooking(
        WorldGroundObject campfire,
        int inventorySlot,
        string itemId)
    {
        if (_activePlayer is null ||
            !InventoryContainsAt(inventorySlot, itemId))
            return;
        if (!CanCookOnCampfire(campfire, itemId, out var reason))
        {
            ReportBlockedAction("campfire-cooking-blocked", reason);
            return;
        }
        _worldActions.QueuePath(
            new Vector2(campfire.X, campfire.Y),
            .72f,
            WorldActionType.CookOnCampfire,
            groundObjectId: campfire.Id,
            inventorySlot: inventorySlot,
            itemId: itemId,
            clearTreeActions: true);
    }

    internal void BeginCampfireCooking(
        Guid campfireId,
        int inventorySlot,
        string itemId,
        Vector2 target)
    {
        if (_player is null ||
            _activePlayer is null ||
            !InventoryContainsAt(inventorySlot, itemId))
            return;
        var campfire = FindGroundObject(campfireId);
        var reason = string.Empty;
        if (campfire is null ||
            !CanCookOnCampfire(campfire, itemId, out reason))
        {
            ReportBlockedAction(
                "campfire-cooking-blocked",
                campfire is null
                    ? "That campfire is no longer available."
                    : reason);
            return;
        }
        _activeGroundPickupId = null;
        _activeCampfireFuelPickupId = null;
        _activeCooking = new(
            campfireId, inventorySlot, itemId, target);
        _player.GatherAt(target);
    }

    internal void UpdateCooking()
    {
        if (_activeCooking is not { } cooking ||
            _activePlayer is null)
            return;
        if (cooking.ReadyAt is null)
        {
            if (_player is null ||
                _player.Action != EntityAction.Gather)
            {
                _activeCooking = null;
                return;
            }
            if (_player.ActionTime <
                CookingSkill.PlacementAnimationSeconds)
                return;
            var campfire = FindGroundObject(cooking.CampfireId);
            var reason = string.Empty;
            if (campfire is null ||
                !CanCookOnCampfire(
                    campfire, cooking.RawItemId, out reason,
                    allowActive: true) ||
                !InventoryContainsAt(
                    cooking.InventorySlot, cooking.RawItemId) ||
                !PlayerInventory.TryRemove(
                    _activePlayer.Inventory,
                    cooking.InventorySlot,
                    out var inventory))
            {
                ReportBlockedAction(
                    "campfire-cooking-interrupted",
                    campfire is null
                        ? "The campfire is no longer available."
                        : reason);
                _activeCooking = null;
                _player.Stop();
                return;
            }
            _activePlayer = _activePlayer with
            {
                Inventory = inventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            if (_activeInventorySlot == cooking.InventorySlot)
                _activeInventorySlot = -1;
            _activeCooking = cooking with
            {
                ReadyAt = _clock + CookingSkill.CookingSeconds
            };
            _chatUi.AddMessage(
                $"You place the " +
                $"{ItemCatalog.Get(cooking.RawItemId).Name} over the fire.",
                ChatMessageStyle.Action);
            _player.Stop();
            return;
        }

        if (_clock < cooking.ReadyAt.Value) return;
        _activeCooking = null;
        CompleteCooking(cooking);
    }

    private void CompleteCooking(ActiveCooking cooking)
    {
        if (_activePlayer is null) return;
        var campfireLocation =
            FindGroundObjectLocation(cooking.CampfireId);
        if (campfireLocation is null ||
            CampfireService.State(
                campfireLocation.Value.Object,
                _worldGameSeconds) != CampfireState.Lit)
        {
            ReturnInterruptedCookingItem(cooking, campfireLocation);
            return;
        }

        var level = CookingSkill.LevelForExperience(
            _activePlayer.CookingExperience);
        var result = CookingSkill.Roll(
            cooking.RawItemId,
            level,
            Random.Shared.NextSingle());
        var closeEnoughToCollect =
            IsPlayerNearCampfire(campfireLocation.Value.Object);
        var inventory = PlayerInventory.Normalize(
            _activePlayer.Inventory);
        var addedToInventory = false;
        if (closeEnoughToCollect &&
            PlayerInventory.TryAddAtPreferredSlot(
                _activePlayer.Inventory,
                result.ItemId,
                cooking.InventorySlot,
                out var updatedInventory))
        {
            inventory = updatedInventory;
            addedToInventory = true;
        }
        if (!addedToInventory)
        {
            DropCookingResult(
                campfireLocation.Value.Chunk,
                campfireLocation.Value.Object,
                result.ItemId,
                closeEnoughToCollect
                    ? null
                    : $"You are too far away, so the {ItemCatalog.Get(result.ItemId).Name} remains beside the fire.");
        }

        var award = CookingSkill.AwardExperience(
            _activePlayer.CookingExperience,
            result.Experience);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            CookingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);

        var resultName = ItemCatalog.Get(result.ItemId).Name;
        _chatUi.AddMessage(
            result.Burnt
                ? $"The {ItemCatalog.Get(cooking.RawItemId).Name} burns."
                : $"You successfully cook the " +
                  $"{ItemCatalog.Get(cooking.RawItemId).Name}.",
            result.Burnt
                ? ChatMessageStyle.Warning
                : ChatMessageStyle.Action);
        if (addedToInventory)
            _chatUi.AddMessage(
                $"You add {resultName} to your inventory.",
                ChatMessageStyle.Experience);
        if (award.Gained > 0)
            _chatUi.AddMessage(
                CookingSkill.ExperienceMessage(award.Gained),
                ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                CookingSkill.LevelUpMessage(award.Level),
                ChatMessageStyle.LevelUp);
    }

    private void ReturnInterruptedCookingItem(
        ActiveCooking cooking,
        (WorldChunk Chunk, int Index, WorldGroundObject Object)? location)
    {
        if (_activePlayer is null) return;
        var closeEnoughToCollect =
            location is not null &&
            IsPlayerNearCampfire(location.Value.Object);
        if (closeEnoughToCollect &&
            PlayerInventory.TryAddAtPreferredSlot(
                _activePlayer.Inventory,
                cooking.RawItemId,
                cooking.InventorySlot,
                out var inventory))
        {
            _activePlayer = _activePlayer with
            {
                Inventory = inventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
        }
        else if (location is not null)
            DropCookingResult(
                location.Value.Chunk,
                location.Value.Object,
                cooking.RawItemId,
                closeEnoughToCollect
                    ? null
                    : $"You are too far away, so the {ItemCatalog.Get(cooking.RawItemId).Name} remains beside the fire.");
        ReportBlockedAction(
            "campfire-cooking-fire-out",
            "The fire goes out before the food finishes cooking.");
    }

    private void DropCookingResult(
        WorldChunk chunk,
        WorldGroundObject campfire,
        string itemId,
        string? message = null)
    {
        var origin = new Vector2(campfire.X, campfire.Y);
        WorldChunk targetChunk;
        Vector2 dropPosition;
        if (TryFindGroundObjectDrop(
                origin, out var dropGpu, out var clearPosition, out _))
        {
            targetChunk = dropGpu.Chunk;
            dropPosition = clearPosition;
        }
        else
        {
            const float fallbackRadius = .38f;
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            targetChunk = chunk;
            dropPosition = origin + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * fallbackRadius;
        }

        targetChunk.GroundObjects.Add(new(
            Guid.NewGuid(),
            itemId,
            dropPosition.X,
            dropPosition.Y));
        QueueChunkSave(targetChunk);
        _chatUi.AddMessage(
            message ??
            $"Your inventory is full, so the " +
            $"{ItemCatalog.Get(itemId).Name} falls beside the fire.",
            ChatMessageStyle.Warning);
    }

    private bool IsPlayerNearCampfire(WorldGroundObject campfire)
    {
        if (_player is null) return false;
        var offset =
            _player.Position - new Vector2(campfire.X, campfire.Y);
        return offset.LengthSquared <=
               CookingCollectionRange * CookingCollectionRange;
    }
}
