using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Vector2? _caveEntranceLightWorld;
    private int _digTargetingSlot = -1;
    private Guid? _activeDigSiteId;
    private ChunkCoordinate _activeDigChunk;
    private int _lastDigStrike;

    private void BeginCaveDigTargeting(int shovelSlot)
    {
        if (_activeWorldLevel != (int)WorldLevel.Overworld ||
            !InventoryHasTagAt(shovelSlot, ItemTag.Shovel))
            return;
        _digTargetingSlot = shovelSlot;
        _gameCursorKind = GameCursorKind.Dig;
        Cursor = _digNativeCursor ?? _defaultNativeCursor ??
            OpenTK.Windowing.Common.Input.MouseCursor.Default;
        _chatUi.AddMessage(
            "Choose a clear patch of ground to excavate.",
            ChatMessageStyle.Action);
    }

    private bool TryTargetCaveDig(Vector2 target)
    {
        if (_digTargetingSlot < 0) return false;
        var shovelSlot = _digTargetingSlot;
        _digTargetingSlot = -1;
        UseDefaultGameCursor();
        QueueCaveDig(target, shovelSlot);
        return true;
    }

    private void CancelCaveDigTargeting()
    {
        _digTargetingSlot = -1;
        UseDefaultGameCursor();
    }

    private void CacheCaveEntranceLight(WorldChunk chunk)
    {
        if (chunk.Coordinate.Level != (int)WorldLevel.Underground ||
            _caveEntranceLightWorld is not null)
            return;
        foreach (var value in chunk.GroundObjects)
            if (CaveEntranceService.IsCaveShaft(value))
            {
                _caveEntranceLightWorld = new(value.X, value.Y);
                return;
            }
    }

    private void QueueCaveDig(Vector2 target, int shovelSlot)
    {
        if (!InventoryHasTagAt(shovelSlot, ItemTag.Shovel))
            return;
        var shovelItemId = _activePlayer?.Inventory?[shovelSlot];
        if (shovelItemId is null) return;
        target = new(
            MathF.Floor(target.X) + .5f,
            MathF.Floor(target.Y) + .5f);
        _worldActions.QueuePath(
            target, .82f, WorldActionType.DigCave,
            inventorySlot: shovelSlot,
            itemId: shovelItemId,
            clearTreeActions: true);
    }

    private void QueueContinueCaveDig(WorldGroundObject site)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        var shovelSlot = Array.FindIndex(
            inventory, item => item is not null &&
                ItemCatalog.Get(item).HasTag(ItemTag.Shovel));
        if (shovelSlot < 0)
        {
            ReportBlockedAction(
                "dig-without-shovel",
                "You need a shovel to continue digging.");
            return;
        }
        QueueCaveDig(new(site.X, site.Y), shovelSlot);
    }

    private void QueueRestoreExcavation(WorldGroundObject site) =>
        _worldActions.QueuePath(
            new(site.X, site.Y), .82f,
            WorldActionType.RestoreExcavation,
            groundObjectId: site.Id,
            clearTreeActions: true);

    private void QueueTakeCaveRope(WorldGroundObject entrance) =>
        _worldActions.QueuePath(
            new(entrance.X, entrance.Y), .82f,
            WorldActionType.TakeCaveRope,
            groundObjectId: entrance.Id,
            clearTreeActions: true);

    internal void TryDigCave(Vector2 target, int shovelSlot)
    {
        if (_activeWorldLevel != (int)WorldLevel.Overworld ||
            !InventoryHasTagAt(shovelSlot, ItemTag.Shovel))
            return;
        var tileX = (int)MathF.Floor(target.X);
        var tileY = (int)MathF.Floor(target.Y);
        if (!TryGetDropTerrain(tileX, tileY, out var gpu, out var reason))
        {
            ReportBlockedAction("cave-dig-blocked", reason);
            return;
        }
        var position = new Vector2(tileX + .5f, tileY + .5f);
        var existing = gpu.Chunk.GroundObjects.FirstOrDefault(value =>
            (value.X - position.X) * (value.X - position.X) +
            (value.Y - position.Y) * (value.Y - position.Y) < .5f);
        if (existing is not null)
        {
            if (CaveEntranceService.IsDigSite(existing))
            {
                BeginCaveDigging(existing);
                return;
            }
            ReportBlockedAction(
                "cave-dig-occupied",
                "There is already something on that patch of ground.");
            return;
        }
        if (gpu.Chunk.Trees.Any(value =>
                value.X == tileX && value.Y == tileY) ||
            gpu.Chunk.TreeInstances.Any(value =>
                value.X == tileX && value.Y == tileY &&
                value.State == TreeLifecycleState.Standing))
        {
            ReportBlockedAction(
                "cave-dig-occupied",
                "A tree is blocking that patch of ground.");
            return;
        }
        var tile = gpu.Chunk.Tiles[
            PositiveMod(tileY, WorldChunk.Size) * WorldChunk.Size +
            PositiveMod(tileX, WorldChunk.Size)];
        var terrain = DiggingSkill.Terrain(tile.Biome);
        var site = new WorldGroundObject(
            Guid.NewGuid(), ItemIds.DigSite,
            position.X, position.Y,
            Health: terrain.Health,
            MaxHealth: terrain.Health);
        gpu.Chunk.GroundObjects.Add(site);
        gpu.VegetationRenderItems = gpu.VegetationRenderItems
            .Where(value =>
                value.TileX != tileX || value.TileY != tileY)
            .ToArray();
        QueueChunkSave(gpu.Chunk);
        _chatUi.AddMessage(
            $"You begin excavating ({terrain.Health} health).",
            ChatMessageStyle.Action);
        BeginCaveDigging(site);
    }

    private void BeginCaveDigging(WorldGroundObject site)
    {
        if (_player is null) return;
        _activeDigSiteId = site.Id;
        _activeDigChunk = new(
            FloorDiv((int)MathF.Floor(site.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(site.Y), WorldChunk.Size),
            _activeWorldLevel);
        _lastDigStrike = 0;
        _player.DigAt(new(site.X, site.Y));
    }

    internal void UpdateCaveDigging()
    {
        if (_activeDigSiteId is null) return;
        if (_player is null || _player.Action != EntityAction.Dig)
        {
            _activeDigSiteId = null;
            return;
        }
        if (
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Dig), out var animation))
            return;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        // The male farmer's hoe contacts the ground on authored frame 6,
        // while the female farmer's hand reaches the ground on frame 7.
        // Resolve each complete excavation strike on its gender-specific
        // pose so sound, damage, XP, and feedback remain aligned.
        var authoredImpactFrame = _player.Gender == EntityGender.Female
            ? 6
            : 5;
        var impactFrame = Math.Clamp(
            authoredImpactFrame, 0, framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastDigStrike) return;
        _lastDigStrike = strike;

        var location = ActiveDigSiteLocation();
        if (location is null ||
            !CaveEntranceService.IsDigSite(location.Value.Object))
        {
            StopCaveDigging();
            return;
        }
        var site = location.Value.Object;
        var damage = Math.Min(
            site.Health,
            DiggingSkill.Damage(
                _activePlayer?.DiggingExperience ?? 0,
                PlayerInventory.BestShovel(
                    _activePlayer?.Inventory)?.DiggingPower ?? 1));
        var health = site.Health - damage;
        ShowEntityImpact(
            DigFeedbackKey(site.Id), damage, true);
        PlaySoundCue("digging-impact");
        _chatUi.AddMessage(
            $"You excavate for {damage} damage " +
            $"({health}/{site.MaxHealth}).",
            ChatMessageStyle.Damage);
        if (health > 0)
        {
            location.Value.Chunk.GroundObjects[location.Value.Index] =
                site with { Health = health };
            QueueChunkSave(location.Value.Chunk);
            AwardDiggingExperience(damage);
            return;
        }

        var tileX = (int)MathF.Floor(site.X);
        var tileY = (int)MathF.Floor(site.Y);
        var tile = location.Value.Chunk.Tiles[
            PositiveMod(tileY, WorldChunk.Size) * WorldChunk.Size +
            PositiveMod(tileX, WorldChunk.Size)];
        var terrain = DiggingSkill.Terrain(tile.Biome);
        var cave = CaveEntranceService.CaveBelow(
            _worldSeed, site.X, site.Y);
        var completed = site with
        {
            ItemId = cave
                ? ItemIds.CaveHole
                : ItemIds.ShallowHole,
            Health = 0
        };
        location.Value.Chunk.GroundObjects[location.Value.Index] =
            completed;
        if (cave)
            SynchronizeUndergroundShaft(completed);
        QueueChunkSave(location.Value.Chunk);
        AwardDiggingExperience(damage + site.MaxHealth / 5);
        AddExcavatedMaterial(
            terrain.RewardItemId, new(site.X, site.Y));
        _chatUi.AddMessage(
            cave
                ? "The completed excavation opens into a cave. A rope could secure the descent."
                : "The hole has a solid bottom. Nothing lies below.",
            ChatMessageStyle.Action);
        StopCaveDigging();
    }

    private void StopCaveDigging()
    {
        _activeDigSiteId = null;
        _player?.Stop();
    }

    internal void RestoreExcavation(Guid siteId)
    {
        if (FindGroundObjectLocation(siteId) is not { } location ||
            !CaveEntranceService.IsDigSite(location.Object))
            return;
        if (_activeDigSiteId == siteId)
            StopCaveDigging();
        location.Chunk.GroundObjects.RemoveAt(location.Index);
        RefreshExcavationVegetation(location.Chunk);
        QueueChunkSave(location.Chunk);
        _chatUi.AddMessage(
            "You restore the unfinished ground.",
            ChatMessageStyle.Action);
    }

    private void RefreshExcavationVegetation(WorldChunk chunk)
    {
        if (_worldChunks.TryGetValue(chunk.Coordinate, out var gpu))
            gpu.VegetationRenderItems =
                WorldVegetationRenderCache.Build(
                    chunk, gpu.RenderedHeights);
    }

    private (
        WorldChunk Chunk,
        int Index,
        WorldGroundObject Object)? ActiveDigSiteLocation()
    {
        if (_activeDigSiteId is not { } id ||
            !_worldChunks.TryGetValue(_activeDigChunk, out var gpu) ||
            !IsActiveWorldChunk(gpu))
            return null;
        var items = gpu.Chunk.GroundObjects;
        for (var index = 0; index < items.Count; index++)
            if (items[index].Id == id)
                return (gpu.Chunk, index, items[index]);
        return null;
    }

    private void AwardDiggingExperience(int amount)
    {
        if (_activePlayer is null || amount <= 0) return;
        var award = DiggingSkill.AwardExperience(
            _activePlayer.DiggingExperience, amount);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            DiggingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{award.Gained} Digging XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your Digging level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    private void AddExcavatedMaterial(
        string itemId, Vector2 excavation)
    {
        if (_activePlayer is null) return;
        var inventory = ActivePlayerInventory();
        if (!inventory.TryAdd(itemId))
        {
            if (TryFindGroundObjectDrop(
                    excavation, out var gpu, out var drop,
                    out _))
            {
                gpu.Chunk.GroundObjects.Add(new(
                    Guid.NewGuid(), itemId, drop.X, drop.Y));
                QueueChunkSave(gpu.Chunk);
            }
            _chatUi.AddMessage(
                $"Your inventory is full, so the " +
                $"{ItemCatalog.Get(itemId).Name} is left behind.",
                ChatMessageStyle.Warning);
            return;
        }
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
    }

    private void RenderDigSiteHealthBar(Vector4 scene)
    {
        if (ActiveDigSiteLocation() is not { } location ||
            location.Object.MaxHealth <= 0)
            return;
        var world = GroundObjectWorld(location.Object);
        var anchor = SpriteAnchor(world);
        DrawEntityFeedback(
            scene,
            (anchor.X - 20, anchor.Y - 40,
                anchor.X + 20, anchor.Y),
            location.Object.Health /
            (float)location.Object.MaxHealth,
            DigFeedbackKey(location.Object.Id),
            forceHealth: true);
    }

    private void QueueCaveEntry(WorldGroundObject entrance) =>
        _worldActions.QueuePath(
            new(entrance.X, entrance.Y), .72f,
            WorldActionType.EnterCave,
            groundObjectId: entrance.Id,
            clearTreeActions: true);

    internal void EnterCave(Guid entranceId)
    {
        var entrance = FindGroundObject(entranceId);
        if (entrance is null ||
            !CaveEntranceService.IsEntrance(entrance) ||
            _player is null || _activePlayer is null ||
            _activeWorld is null)
            return;
        var destinationLevel =
            _activeWorldLevel == (int)WorldLevel.Overworld
                ? (int)WorldLevel.Underground
                : (int)WorldLevel.Overworld;
        CancelWorldLevelWork(clearMinimap: true);
        _activeWorldLevel = destinationLevel;
        _caveEntranceLightWorld =
            destinationLevel == (int)WorldLevel.Underground
                ? new(entrance.X, entrance.Y)
                : null;
        _player.TeleportTo(new(entrance.X, entrance.Y));
        _saves.SaveWorldPlayer(
            _activeWorld.Id,
            new WorldPlayerState(
                _activePlayer.Id, entrance.X, entrance.Y,
                DateTime.UtcNow, destinationLevel,
                _fishingBoat?.Position.X,
                _fishingBoat?.Position.Y,
                _fishingBoat?.Facing.X ?? 1,
                _fishingBoat?.Facing.Y ?? 1,
                false));
        StreamWorld();
        _chatUi.AddMessage(
            destinationLevel == (int)WorldLevel.Underground
                ? "You climb down into the cave."
                : "You climb back into the daylight.",
            ChatMessageStyle.Action);
        if (destinationLevel == (int)WorldLevel.Underground)
            RecordQuestEvent(new(QuestEventType.EnterCave));
    }

    private void TryInstallCaveRope(Guid holeId, int ropeSlot)
    {
        if (_activeWorldLevel != (int)WorldLevel.Overworld ||
            _activePlayer is null ||
            !InventoryContainsAt(ropeSlot, ItemIds.Rope) ||
            FindGroundObjectLocation(holeId) is not { } location ||
            !CaveEntranceService.IsHole(location.Object))
            return;
        var inventory = ActivePlayerInventory();
        if (!inventory.TryTake(ropeSlot, 1, out _))
            return;

        var entrance = CaveEntranceService.InstallRope(location.Object);
        location.Chunk.GroundObjects[location.Index] = entrance;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Chunk);

        SynchronizeUndergroundShaft(entrance);
        _chatUi.AddMessage(
            "You secure the rope. The cave can now be entered.",
            ChatMessageStyle.Action);
    }

    internal void TakeCaveRope(Guid entranceId)
    {
        if (_activeWorldLevel != (int)WorldLevel.Overworld ||
            _activePlayer is null ||
            FindGroundObjectLocation(entranceId) is not { } location ||
            !CaveEntranceService.IsEntrance(location.Object))
            return;
        var inventory = ActivePlayerInventory();
        if (!inventory.TryAdd(ItemIds.Rope))
        {
            ReportBlockedAction(
                "take-rope-inventory-full",
                "Your inventory is too full to take the rope.");
            return;
        }

        var openShaft =
            location.Object with { ItemId = ItemIds.CaveHole };
        location.Chunk.GroundObjects[location.Index] = openShaft;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Chunk);
        SynchronizeUndergroundShaft(openShaft);
        _chatUi.AddMessage(
            "You recover the rope. The cave is no longer accessible.",
            ChatMessageStyle.Action);
    }

    private void SynchronizeUndergroundShaft(
        WorldGroundObject shaft)
    {
        var coordinate = new ChunkCoordinate(
            FloorDiv((int)MathF.Floor(shaft.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(shaft.Y), WorldChunk.Size),
            (int)WorldLevel.Underground);
        if (_worldChunks.TryGetValue(coordinate, out var loaded))
        {
            Upsert(loaded.Chunk, shaft);
            RefreshExcavationVegetation(loaded.Chunk);
            _worldStore?.Save(loaded.Chunk);
            return;
        }
        if (_worldStore is null) return;
        var underground = _worldStore.LoadOrGenerate(coordinate);
        Upsert(underground, shaft);
        _worldStore.Save(underground);

        static void Upsert(
            WorldChunk chunk,
            WorldGroundObject value)
        {
            var index = chunk.GroundObjects.FindIndex(
                existing => existing.Id == value.Id);
            if (index >= 0)
                chunk.GroundObjects[index] = value;
            else
                chunk.GroundObjects.Add(value);
        }
    }

    private void RemoveUndergroundShaft(WorldGroundObject shaft)
    {
        var coordinate = new ChunkCoordinate(
            FloorDiv((int)MathF.Floor(shaft.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(shaft.Y), WorldChunk.Size),
            (int)WorldLevel.Underground);
        if (_worldChunks.TryGetValue(coordinate, out var loaded))
        {
            loaded.Chunk.GroundObjects.RemoveAll(
                value => value.Id == shaft.Id);
            RefreshExcavationVegetation(loaded.Chunk);
            _worldStore?.Save(loaded.Chunk);
        }
        else if (_worldStore is not null)
        {
            var underground = _worldStore.LoadOrGenerate(coordinate);
            underground.GroundObjects.RemoveAll(
                value => value.Id == shaft.Id);
            _worldStore.Save(underground);
        }
        if (_caveEntranceLightWorld is { } light &&
            MathF.Abs(light.X - shaft.X) < .01f &&
            MathF.Abs(light.Y - shaft.Y) < .01f)
            _caveEntranceLightWorld = null;
    }

    private bool CanFillExcavation(
        WorldGroundObject hole,
        string materialItemId,
        out string requiredItemId)
    {
        requiredItemId = "";
        if (!CaveEntranceService.CanFill(hole) ||
            FindGroundObjectLocation(hole.Id) is not { } location)
            return false;
        var localX = PositiveMod(
            (int)MathF.Floor(hole.X), WorldChunk.Size);
        var localY = PositiveMod(
            (int)MathF.Floor(hole.Y), WorldChunk.Size);
        requiredItemId = DiggingSkill.Terrain(
            location.Chunk.Tiles[
                localY * WorldChunk.Size + localX].Biome).RewardItemId;
        return materialItemId == requiredItemId;
    }

    private void TryFillExcavation(
        Guid holeId,
        int materialSlot,
        string materialItemId)
    {
        var requiredItemId = "";
        if (_activePlayer is null ||
            !InventoryContainsAt(materialSlot, materialItemId) ||
            FindGroundObjectLocation(holeId) is not { } location ||
            !CanFillExcavation(
                location.Object, materialItemId, out requiredItemId))
        {
            if (!string.IsNullOrEmpty(requiredItemId))
                ReportBlockedAction(
                    "fill-hole-material",
                    $"This ground must be restored with " +
                    $"{ItemCatalog.Get(requiredItemId).Name}.");
            return;
        }
        var inventory = ActivePlayerInventory();
        if (!inventory.TryTake(materialSlot, 1, out _))
            return;

        var filled = location.Object;
        location.Chunk.GroundObjects.RemoveAt(location.Index);
        if (CaveEntranceService.IsHole(filled))
            RemoveUndergroundShaft(filled);
        RefreshExcavationVegetation(location.Chunk);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == materialSlot)
            _activeInventorySlot = -1;
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Chunk);
        _chatUi.AddMessage(
            $"You fill in the hole with " +
            $"{ItemCatalog.Get(materialItemId).Name}.",
            ChatMessageStyle.Action);
    }
}
