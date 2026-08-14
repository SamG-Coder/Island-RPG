using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly ContextMenuControlState _fishContext = new();
    private WorldFish? _fishContextTarget;
    private Vector2 _fishContextWalkTarget;
    private string? _activeFishKey;
    private int _completedFishingCycles;
    private readonly int[] _fishItemTextures =
        new int[12];
    private readonly SpriteFrame?[] _fishItemFrames =
        new SpriteFrame?[12];
    private readonly SpriteFrame?[] _fishItemShadowFrames =
        new SpriteFrame?[12];

    private void InitializeFishing() =>
        _fishContext.Selected += HandleFishContextSelection;

    private void OpenFishContext(
        WorldFish fish, Vector2 walkTarget)
    {
        _fishContextTarget = fish;
        _fishContextWalkTarget = walkTarget;
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _vegetationContext.Close();
        _fishContext.Open(
            MouseState.Position,
            ["Fish", "Walk Here", "Examine"],
            SceneClientBounds(), 142);
    }

    private void HandleFishContextSelection(int option)
    {
        var fish = _fishContextTarget;
        _fishContextTarget = null;
        if (fish is null) return;
        switch (option)
        {
            case 0:
                QueueFishing(fish);
                break;
            case 1:
                if (IsNetworkWorld && _fishingBoatBoarded)
                {
                    var boat = LocalNetworkBoat();
                    if (boat is not null &&
                        FishingBoatTravel.IsNavigable(
                            InfiniteWorldGenerator.BiomeAt(
                                _worldSeed,
                                (int)MathF.Floor(_fishContextWalkTarget.X),
                                (int)MathF.Floor(_fishContextWalkTarget.Y))))
                    {
                        SendNetworkBoatAction(
                            BoatActionKind.Move,
                            boat.State.BoatId,
                            reference => new MoveBoatAction(
                                reference,
                                _fishContextWalkTarget.X,
                                _fishContextWalkTarget.Y));
                    }
                }
                else if (_fishingBoatBoarded)
                    QueueFishingBoatTravel(_fishContextWalkTarget);
                else
                    QueueWalk(_fishContextWalkTarget);
                break;
            case 2:
                var profile = WorldFishGenerator.Profile(fish.Species);
                _chatUi.AddMessage(
                    $"A school of {profile.DisplayName}. " +
                    $"{profile.Rarity}; found in " +
                    $"{profile.Habitat.ToLowerInvariant()}.",
                    ChatMessageStyle.Normal);
                break;
        }
    }

    private void PrepareFishingItemSprites()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images",
            "fish-items.png");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        const int cellSize = 32;
        for (var index = 0; index < _fishItemFrames.Length; index++)
        {
            var pixels = new byte[cellSize * cellSize * 4];
            var cellX = index % 4 * cellSize;
            var cellY = index / 4 * cellSize;
            for (var row = 0; row < cellSize; row++)
                Buffer.BlockCopy(
                    sheet.Data,
                    ((cellY + row) * sheet.Width + cellX) * 4,
                    pixels,
                    row * cellSize * 4,
                    cellSize * 4);
            var frame = new SpriteFrame(
                cellSize, cellSize, cellSize / 2, 28, pixels);
            _fishItemFrames[index] = frame;
            _fishItemShadowFrames[index] =
                ItemShadowGenerator.Create(frame);
            _fishItemTextures[index] = Upload(frame);
        }
    }

    private static string FishItemAtlasKey(int cell, bool shadow) =>
        shadow ? $"FISH_ITEM_SHADOW#{cell}" : $"FISH_ITEM#{cell}";

    private bool IsFishDepleted(WorldFish fish)
    {
        if (IsNetworkWorld)
            return _networkResourceHotPath.IsFishDepleted(
                fish.StableKey,
                _networkClient?.State.ResourceChunks);
        var chunk = FindFishChunk(fish.StableKey);
        return chunk is not null &&
               chunk.Chunk.FishRemaining.TryGetValue(
                   fish.StableKey, out var remaining) &&
               remaining <= 0;
    }

    private float FishingNetReach()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Fish), out var animation))
            return 1.5f;
        var reachPixels = animation.Graphic.Sprite.Frames.Max(frame =>
            Math.Max(
                frame.HotspotX,
                frame.Width - frame.HotspotX));
        return Math.Clamp(reachPixels / 48f, 1.1f, 2.4f);
    }

    private float FishingAnimationCycleSeconds()
    {
        const int authoredAngles = 5;
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Fish), out var animation))
            return 2.8f;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / authoredAngles);
        var baseSeconds = framesPerAngle * animation.SecondsPerFrame;
        var netPower = PlayerInventory.BestFishingNet(
            _activePlayer?.Inventory)?.FishingPower ?? 1;
        return FishingSkill.CycleSeconds(baseSeconds, netPower);
    }

    private void QueueFishing(WorldFish fish)
    {
        if (IsNetworkWorld)
        {
            QueueNetworkFishing(fish);
            return;
        }
        if (_activePlayer is null || IsFishDepleted(fish)) return;
        var net = PlayerInventory.BestFishingNet(
            _activePlayer.Inventory);
        if (net is null)
        {
            ReportBlockedAction(
                "fishing-without-net",
                "You need a fishing net to catch fish.");
            return;
        }
        var level = FishingSkill.LevelForExperience(
            _activePlayer.FishingExperience);
        var profile = FishingSkill.Profile(fish.Species);
        if (!FishingSkill.CanCatch(
                fish.Species, level, net.FishingPower))
        {
            if (level >= profile.RequiredLevel)
            {
                ReportBlockedAction(
                    $"fishing-net-{fish.Species}",
                    $"You need a stronger fishing net to catch " +
                    $"{WorldFishGenerator.Profile(fish.Species).DisplayName}.");
                return;
            }
            ReportBlockedAction(
                $"fishing-level-{fish.Species}",
                $"You need Fishing level {profile.RequiredLevel} to catch " +
                $"{WorldFishGenerator.Profile(fish.Species).DisplayName}.");
            return;
        }

        if (_fishingBoatBoarded)
            QueueFishingFromBoat(fish);
        else
            _worldActions.QueueFish(fish);
    }

    internal void BeginFishing(string fishKey, Vector2 target)
    {
        if (IsNetworkWorld)
        {
            var networkFish = FindFish(fishKey);
            if (networkFish is not null)
                QueueNetworkFishing(networkFish);
            return;
        }
        if (_player is null || _activePlayer is null) return;
        var fish = FindFish(fishKey);
        if (fish is null || IsFishDepleted(fish)) return;
        var net = PlayerInventory.BestFishingNet(
            _activePlayer.Inventory);
        var level = FishingSkill.LevelForExperience(
            _activePlayer.FishingExperience);
        if (net is null ||
            !FishingSkill.CanCatch(fish.Species, level, net.FishingPower))
        {
            ReportBlockedAction(
                "fishing-without-net",
                "You need a suitable fishing net to catch this fish.");
            return;
        }
        if (!ActivePlayerInventory().CanAdd(
                FishingSkill.Profile(fish.Species).ItemId))
        {
            ReportBlockedAction(
                "fishing-inventory-full",
                "Your inventory is too full to hold another catch.");
            return;
        }

        _activeTreeId = null;
        _activeTreeStickGatherId = null;
        _activeGroundPickupId = null;
        _activeFishKey = fishKey;
        _completedFishingCycles = 0;
        _player.FishAt(target);
    }

    internal void UpdateFishing()
    {
        if (IsNetworkWorld) return;
        if (_activeFishKey is null || _player is null ||
            _activePlayer is null)
            return;
        if (_player.Action != EntityAction.Fish)
        {
            _activeFishKey = null;
            return;
        }

        var fish = FindFish(_activeFishKey);
        if (fish is null || IsFishDepleted(fish))
        {
            StopFishing();
            return;
        }
        var net = PlayerInventory.BestFishingNet(
            _activePlayer.Inventory);
        var level = FishingSkill.LevelForExperience(
            _activePlayer.FishingExperience);
        if (net is null ||
            !FishingSkill.CanCatch(fish.Species, level, net.FishingPower))
        {
            ReportBlockedAction(
                "fishing-net-lost",
                "You can no longer catch this fish with your current net.");
            StopFishing();
            return;
        }

        var duration = FishingAnimationCycleSeconds();
        var completed = (int)(_player.ActionTime / duration);
        while (_completedFishingCycles < completed)
        {
            _completedFishingCycles++;
            if (!TryCompleteFishingCatch(fish))
            {
                StopFishing();
                return;
            }
        }
    }

    private bool TryCompleteFishingCatch(WorldFish fish)
    {
        if (_activePlayer is null) return false;
        var profile = FishingSkill.Profile(fish.Species);
        var netPower = PlayerInventory.BestFishingNet(
            _activePlayer.Inventory)?.FishingPower ?? 1;
        var level = FishingSkill.LevelForExperience(
            _activePlayer.FishingExperience);
        if (Random.Shared.NextSingle() >
            FishingSkill.CatchChance(fish.Species, level, netPower))
        {
            _entityFeedback.ShowLabel(
                FishFeedbackKey(fish.StableKey), "Miss", false, _clock);
            return true;
        }
        var inventory = ActivePlayerInventory();
        if (!inventory.TryAdd(profile.ItemId))
        {
            ReportBlockedAction(
                "fishing-inventory-full",
                "Your inventory is too full to hold another catch.");
            return false;
        }

        var award = FishingSkill.AwardExperience(
            _activePlayer.FishingExperience, fish.Species);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            FishingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        RecordQuestEvent(new(
            QuestEventType.CatchFish,
            profile.ItemId));

        var chunk = FindFishChunk(fish.StableKey);
        if (chunk is null) return false;
        var remaining = chunk.Chunk.FishRemaining.TryGetValue(
            fish.StableKey, out var current)
            ? current
            : profile.SchoolSize;
        remaining--;
        chunk.Chunk.FishRemaining[fish.StableKey] = remaining;
        QueueChunkSave(chunk.Chunk);
        var name = ItemCatalog.Get(profile.ItemId).Name;
        _entityFeedback.ShowLabel(
            FishFeedbackKey(fish.StableKey), "Caught", true, _clock);
        _chatUi.AddMessage(
            FishingSkill.InventoryMessage(name),
            ChatMessageStyle.Experience);
        _chatUi.AddMessage(
            FishingSkill.ExperienceMessage(award.Gained),
            ChatMessageStyle.Experience);

        if (award.LevelledUp)
            _chatUi.AddMessage(
                FishingSkill.LevelUpMessage(award.Level),
                ChatMessageStyle.LevelUp);
        if (remaining <= 0)
            _chatUi.AddMessage(
                "The fish school has been exhausted.",
                ChatMessageStyle.Normal);
        return remaining > 0;
    }

    private void StopFishing()
    {
        _activeFishKey = null;
        _player?.Stop();
        CenterFishingBoatRider();
    }

    private void RenderFishingFeedback(Vector4 scene)
    {
        if (_entityFeedback.LatestImpactTargetKey is not { } targetKey ||
            !targetKey.StartsWith("fish:", StringComparison.Ordinal))
            return;
        var stableKey = targetKey["fish:".Length..];
        var cached = _worldChunks.Values
            .Where(value =>
                value.Chunk.Coordinate.Level == _activeWorldLevel)
            .SelectMany(value => value.FishRenderItems)
            .FirstOrDefault(value =>
                value.Fish.StableKey.Equals(
                    stableKey, StringComparison.Ordinal));
        if (cached.Fish is null) return;
        var atlasKey = WorldFishAnimation.AtlasKey(cached.Fish, _clock);
        if (!_treeAtlas.TryGetValue(atlasKey, out var atlas)) return;
        DrawEntityFeedback(
            scene,
            SpriteBounds(atlas.Frame, cached.World),
            1,
            targetKey);
    }

    private WorldFish? FindFish(string stableKey) =>
        _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(chunk => chunk.Chunk.Fish)
            .FirstOrDefault(fish =>
                fish.StableKey.Equals(stableKey, StringComparison.Ordinal));

    private GpuWorldChunk? FindFishChunk(string stableKey) =>
        _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .FirstOrDefault(chunk =>
            chunk.Chunk.Fish.Any(fish =>
                fish.StableKey.Equals(stableKey, StringComparison.Ordinal)));

}
