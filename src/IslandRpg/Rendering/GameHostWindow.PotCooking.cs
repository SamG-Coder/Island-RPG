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
            WorldActionReach.CookStew,
            WorldActionType.CookStew,
            groundObjectId: pot.Id,
            clearTreeActions: true);
        if (IsNetworkWorld)
            SendNetworkWalk(
                WorldActionReach.StandOff(
                    NetworkActionPosition,
                    new Vector2(pot.X, pot.Y),
                    WorldActionReach.CookStew));
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
        if (IsNetworkWorld)
        {
            var seconds = CookingSkill.PlacementAnimationSeconds +
                          (float)CookingSkill.CookingSeconds;
            SendNetworkPresentSkill(EntityAction.Gather, seconds);
            _networkWorldActionCommitAt = _clock + seconds;
        }
        _player.GatherAt(target);
        if (IsNetworkWorld)
            _player.RestartActionTime();
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
        var potSeconds = CookingSkill.PlacementAnimationSeconds +
                         CookingSkill.CookingSeconds;
        if (!NetworkResourceWindupReady(
                _player.ActionTime, potSeconds, _clock,
                _networkWorldActionCommitAt))
            return;

        _activePotCooking = null;
        if (IsNetworkWorld)
        {
            var networkPot = FindGroundObject(cooking.PotId);
            if (networkPot is null)
            {
                _player.Stop();
                return;
            }
            QueueNetworkObjectAction(
                NetworkWorldActionKind.CookStew, networkPot);
            _player.Stop();
            return;
        }
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
        var cookingInventory = ActivePlayerInventory();
        if (CookingSkill.LevelForExperience(
                _activePlayer.CookingExperience) <
            StewCookingService.RequiredLevel ||
            !StewCookingService.TryPrepare(
                cookingInventory, out var inventory,
                out var fishItemId, out var berryItemId))
        {
            ReportBlockedAction(
                "stew-ingredients",
                "You need one raw fish and one handful of raw berries.");
            _player.Stop();
            return;
        }
        var award = CookingSkill.AwardExperience(
            _activePlayer.CookingExperience,
            StewCookingService.Experience);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
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
        => IsNearLitCampfire(
            new(pot.X, pot.Y),
            _activeWorldLevel,
            StewCookingService.CampfireRange);
}
