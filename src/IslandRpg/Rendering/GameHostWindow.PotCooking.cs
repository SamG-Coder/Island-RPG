using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private sealed record ActivePotCooking(
        Guid PotId,
        Vector2 Target);

    private ActivePotCooking? _activePotCooking;

    private void QueuePotCooking(WorldGroundObject pot)
    {
        if (_activePlayer is null) return;
        var level = CookingSkill.LevelForExperience(
            _activePlayer.CookingExperience);
        if (level < StewCookingService.RequiredLevel)
        {
            ReportBlockedAction(
                "stew-level",
                $"You need Cooking level " +
                $"{StewCookingService.RequiredLevel} to make stew.");
            return;
        }
        if (!StewCookingService.HasIngredients(
                _activePlayer.Inventory))
        {
            ReportBlockedAction(
                "stew-ingredients",
                "You need one raw fish and one handful of raw berries.");
            return;
        }
        if (!HasNearbyLitCampfire(pot))
        {
            ReportBlockedAction(
                "stew-fire",
                "Place the cooking pot close to a lit campfire.");
            return;
        }
        _worldActions.QueuePath(
            new Vector2(pot.X, pot.Y),
            .82f,
            WorldActionType.CookStew,
            groundObjectId: pot.Id,
            clearTreeActions: true);
    }

    internal void BeginPotCooking(Guid potId, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var pot = FindGroundObject(potId);
        if (pot is null ||
            pot.ItemId != ItemIds.CookingPot ||
            !HasNearbyLitCampfire(pot) ||
            !StewCookingService.HasIngredients(
                _activePlayer.Inventory))
            return;
        _activePotCooking = new(potId, target);
        _player.GatherAt(target);
    }

    internal void UpdatePotCooking()
    {
        if (_activePotCooking is not { } cooking ||
            _player is null ||
            _activePlayer is null)
            return;
        if (_player.Action != EntityAction.Gather)
        {
            _activePotCooking = null;
            return;
        }
        if (_player.ActionTime <
            CookingSkill.PlacementAnimationSeconds +
            CookingSkill.CookingSeconds)
            return;

        _activePotCooking = null;
        var pot = FindGroundObject(cooking.PotId);
        if (pot is null ||
            pot.ItemId != ItemIds.CookingPot ||
            !HasNearbyLitCampfire(pot))
        {
            ReportBlockedAction(
                "stew-interrupted",
                "The cooking pot is no longer beside a lit campfire.");
            _player.Stop();
            return;
        }
        var cookingInventory = PlayerInventory.Normalize(
            _activePlayer.Inventory);
        var fishItemId = cookingInventory.FirstOrDefault(itemId =>
            itemId is not null &&
            ItemCatalog.Get(itemId).HasTag(ItemTag.Fish) &&
            CookingSkill.TryProfile(itemId, out _));
        var berryItemId = cookingInventory.FirstOrDefault(itemId =>
            itemId is not null &&
            ItemCatalog.Get(itemId).HasTag(ItemTag.Berry) &&
            CookingSkill.TryProfile(itemId, out _));
        var cooked = EntityInteractionService.CookStew(
            cookingInventory,
            CookingSkill.LevelForExperience(
                _activePlayer.CookingExperience));
        if (!cooked.Succeeded)
        {
            ReportBlockedAction(
                "stew-ingredients",
                "You need one raw fish and one handful of raw berries.");
            _player.Stop();
            return;
        }
        var inventory = cooked.Inventory;

        var award = CookingSkill.AwardExperience(
            _activePlayer.CookingExperience,
            StewCookingService.Experience);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            CookingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You simmer the {ItemCatalog.Get(fishItemId!).Name} " +
            $"with {ItemCatalog.Get(berryItemId!).Name}.",
            ChatMessageStyle.Action);
        _chatUi.AddMessage(
            $"You make {ItemCatalog.Get(ItemIds.FishBerryStew).Name}.",
            ChatMessageStyle.Experience);
        _chatUi.AddMessage(
            CookingSkill.ExperienceMessage(award.Gained),
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                CookingSkill.LevelUpMessage(award.Level),
                ChatMessageStyle.LevelUp);
        _player.Stop();
    }

    private bool HasNearbyLitCampfire(WorldGroundObject pot)
    {
        var rangeSquared =
            StewCookingService.CampfireRange *
            StewCookingService.CampfireRange;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            var objects = gpu.Chunk.GroundObjects;
            for (var index = 0; index < objects.Count; index++)
            {
                var candidate = objects[index];
                if (CampfireService.State(
                        candidate, _worldGameSeconds) !=
                    CampfireState.Lit)
                    continue;
                var offset = new Vector2(
                    candidate.X - pot.X,
                    candidate.Y - pot.Y);
                if (offset.LengthSquared <= rangeSquared)
                    return true;
            }
        }
        return false;
    }
}
