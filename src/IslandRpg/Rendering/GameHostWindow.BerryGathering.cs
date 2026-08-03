using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const double BerryBushCooldownSeconds = 12 * 60;
    private const int BerryFarmingExperience = 18;
    private string? _activeBerryVegetationKey;
    private ItemDefinition? _activeBerrySickle;

    private void QueueBerryGather(string stableKey)
    {
        var located = FindVegetation(stableKey);
        if (located is not { } target ||
            target.Vegetation.Kind != WorldVegetationKind.BerryBush)
            return;
        if (!VegetationReady(target.Gpu.Chunk, stableKey))
        {
            ReportBlockedAction(
                "berry-bush-recovering",
                "This bush needs time to grow more berries.");
            return;
        }
        if (_activePlayer is null)
        {
            ReportBlockedAction(
                "berry-inventory-full",
                "Your inventory is too full to pick berries.");
            return;
        }
        _worldActions.QueueBerryBush(target.Vegetation, stableKey);
    }

    internal void BeginBerryGather(string stableKey, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var located = FindVegetation(stableKey);
        if (located is null ||
            located.Value.Vegetation.Kind != WorldVegetationKind.BerryBush ||
            !VegetationReady(located.Value.Gpu.Chunk, stableKey))
            return;
        _activeBerryVegetationKey = stableKey;
        _activeBerrySickle = PlayerInventory.BestSickle(
            _activePlayer.Inventory);
        _player.GatherAt(target);
    }

    internal void UpdateBerryGathering()
    {
        if (_activeBerryVegetationKey is null ||
            _player is null || _activePlayer is null)
            return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeBerryVegetationKey = null;
            _activeBerrySickle = null;
            return;
        }
        var sickle = _activeBerrySickle;
        if (_player.ActionTime <
            FarmingSkill.GatherSeconds(sickle))
            return;

        var key = _activeBerryVegetationKey;
        _activeBerryVegetationKey = null;
        _activeBerrySickle = null;
        var located = FindVegetation(key);
        if (located is not { } target ||
            target.Vegetation.Kind != WorldVegetationKind.BerryBush ||
            !VegetationReady(target.Gpu.Chunk, key))
        {
            _player.Stop();
            return;
        }

        var itemId = target.Vegetation.GraphicName.Equals(
            "FORAGM_NN", StringComparison.OrdinalIgnoreCase)
            ? ItemIds.TropicalBerries
            : ItemIds.WildBerries;
        var farmingLevel = FarmingSkill.LevelForExperience(
            _activePlayer.FarmingExperience);
        var requested = Random.Shared.Next(1, 4) +
                        FarmingSkill.BonusBerryCount(
                            farmingLevel, sickle,
                            Random.Shared.NextSingle()) +
                        FarmingSkill.GatheringBasketBonus(
                            _activePlayer.Inventory);
        var inventory = ActivePlayerInventory();
        var gathered = inventory.AddUpTo(itemId, requested);
        if (gathered == 0)
        {
            ReportBlockedAction(
                "berry-inventory-full",
                "Your inventory is too full to pick berries.");
            _player.Stop();
            return;
        }

        var award = FarmingSkill.AwardExperience(
            _activePlayer.FarmingExperience,
            BerryFarmingExperience * gathered);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            FarmingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        SetVegetationCooldown(
            target.Gpu.Chunk, key, BerryBushCooldownSeconds);
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(target.Gpu.Chunk);
        _chatUi.AddMessage(
            $"You pick {gathered} " +
            $"{ItemCatalog.Get(itemId).Name}.",
            ChatMessageStyle.Action);
        if (gathered < requested)
            _chatUi.AddMessage(
                "You leave some berries behind because your inventory is full.",
                ChatMessageStyle.Warning);
        _chatUi.AddMessage(
            FarmingSkill.ExperienceMessage(award.Gained),
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                FarmingSkill.LevelUpMessage(award.Level),
                ChatMessageStyle.LevelUp);
        _player.Stop();
    }
}
