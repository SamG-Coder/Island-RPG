using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private GameControlPipe? _gameControlPipe;
    private readonly ChatHistoryReader _controlChatHistory = new();

    private void ProcessGameControlPipe()
    {
        if (_gameControlPipe is null) return;
        while (_gameControlPipe.TryDequeue(out var request))
        {
            try
            {
                using var document = JsonDocument.Parse(request.Json);
                var root = document.RootElement;
                var command = root.GetProperty("command").GetString();
                LogControlPipeCommand(command, root);
                switch (command)
                {
                    case "new_game":
                        var characterName = root.GetProperty(
                            "character").GetString();
                        var worldName = root.GetProperty("world").GetString();
                        if (string.IsNullOrWhiteSpace(characterName) ||
                            string.IsNullOrWhiteSpace(worldName))
                        {
                            request.Complete(Error("name_required"));
                            break;
                        }
                        var genderText = root.TryGetProperty(
                            "gender", out var genderElement)
                            ? genderElement.GetString()
                            : "male";
                        var gender = string.Equals(
                            genderText, "female",
                            StringComparison.OrdinalIgnoreCase)
                            ? EntityGender.Female
                            : EntityGender.Male;
                        var newSeed = root.TryGetProperty(
                            "seed", out var seedElement)
                            ? seedElement.ValueKind == JsonValueKind.Number
                                ? seedElement.GetInt64()
                                : SeedFromText(seedElement.GetString() ?? "")
                            : Random.Shared.NextInt64();
                        var npcCount = root.TryGetProperty(
                            "npcCount", out var npcCountElement) &&
                            npcCountElement.ValueKind == JsonValueKind.Number
                                ? Math.Clamp(
                                    npcCountElement.GetInt32(),
                                    0,
                                    VillagerSimulation.MaximumPopulation)
                                : 0;
                        var newPlayer = _saves.CreatePlayer(
                            characterName, gender, skinTone: 2, teamColor: 0);
                        _selectedPlayer = newPlayer;
                        _worldSeed = newSeed;
                        var newSpawn = FindPlayableSpawn();
                        CompleteNewWorldCreation(
                            new(
                                worldName, newSeed, newSpawn,
                                newPlayer, Population: npcCount),
                            [], false, "", [], "");
                        request.Complete(ControlSnapshot("new_game_started"));
                        break;
                    case "load_latest":
                        var world = _saves.ListWorlds()
                            .OrderByDescending(value => value.UpdatedUtc)
                            .FirstOrDefault();
                        if (world is null)
                        {
                            request.Complete(Error("no_ai_world"));
                            break;
                        }
                        var player = _saves.ListPlayers().FirstOrDefault(value =>
                            value.Id == world.LastPlayerId) ??
                            _saves.ListPlayers().FirstOrDefault();
                        EnterWorld(world, player);
                        request.Complete(ControlSnapshot("world_loaded"));
                        break;
                    case "approach":
                        if (_player is null || _villagers.Count == 0)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var requestedActor = root.TryGetProperty(
                            "actor", out var actorElement)
                            ? actorElement.GetString()
                            : null;
                        var target = _villagers.FirstOrDefault(value =>
                            value.Health > 0 &&
                            (string.IsNullOrWhiteSpace(requestedActor) ||
                             value.Id.Equals(requestedActor,
                                 StringComparison.OrdinalIgnoreCase) ||
                             value.Name.Equals(requestedActor,
                                 StringComparison.OrdinalIgnoreCase))) ??
                            _villagers.First(value => value.Health > 0);
                        _player.TeleportTo(new(
                            target.PositionX - 1,
                            target.PositionY));
                        _player.Stop();
                        request.Complete(ControlSnapshot("approached"));
                        break;
                    case "chat":
                        if (_player is null)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var text = root.GetProperty("text").GetString() ?? "";
                        HandleChatSubmission(text);
                        request.Complete(ControlSnapshot("chat_submitted"));
                        break;
                    case "chat_history":
                        request.Complete(ControlChatHistory(root));
                        break;
                    case "walk":
                        if (_player is null)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var x = root.GetProperty("x").GetSingle();
                        var y = root.GetProperty("y").GetSingle();
                        if (!float.IsFinite(x) || !float.IsFinite(y))
                        {
                            request.Complete(Error("invalid_position"));
                            break;
                        }
                        QueueWalk(new(x, y));
                        request.Complete(ControlSnapshot("walk_queued"));
                        break;
                    case "stop_player":
                        _player?.Stop();
                        request.Complete(ControlSnapshot("player_stopped"));
                        break;
                    case "combat_style":
                        if (_activePlayer is null)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var styleText = root.TryGetProperty(
                            "style", out var styleElement)
                            ? styleElement.GetString()
                            : null;
                        if (!ControlCombatCommands.TryParseStance(
                                styleText, out var stance))
                        {
                            request.Complete(Error("invalid_combat_style"));
                            break;
                        }
                        _activePlayer = _activePlayer with
                        {
                            CombatStance = stance,
                            UpdatedUtc = DateTime.UtcNow
                        };
                        _saves.SavePlayer(_activePlayer);
                        request.Complete(ControlSnapshot(
                            "combat_style_changed"));
                        break;
                    case "act":
                        if (!TryQueueControlAction(root, out var actionError))
                        {
                            request.Complete(Error(actionError));
                            break;
                        }
                        request.Complete(ControlSnapshot("action_queued"));
                        break;
                    case "screenshot":
                        request.Complete(ControlScreenshot());
                        break;
                    case "skip_cinematic":
                        SkipOpeningCinematic();
                        request.Complete(ControlSnapshot("cinematic_skipped"));
                        break;
                    case "continue":
                        if (_modalScreen.Active !=
                            ModalScreenKind.QuestComplete)
                        {
                            request.Complete(Error(
                                "quest_complete_not_open"));
                            break;
                        }
                        CloseQuestWindow();
                        request.Complete(ControlSnapshot(
                            "quest_complete_dismissed"));
                        break;
                    case "nearby":
                        request.Complete(ControlNearby(root));
                        break;
                    case "world":
                        request.Complete(ControlWorld());
                        break;
                    case "inventory":
                        request.Complete(ControlInventory());
                        break;
                    case "recipes":
                        request.Complete(ControlRecipes());
                        break;
                    case "craft":
                        request.Complete(ControlCraft(root));
                        break;
                    case "use":
                        request.Complete(ControlUseInventory(root));
                        break;
                    case "drop":
                        request.Complete(ControlDropInventory(root));
                        break;
                    case "events":
                        request.Complete(JsonSerializer.Serialize(new
                        {
                            ok = true,
                            eventType = "events",
                            events = _gameControlPipe.DrainPublished()
                        }));
                        break;
                    case "help":
                        request.Complete(JsonSerializer.Serialize(new
                        {
                            ok = true,
                            eventType = "help",
                            commands = new object[]
                            {
                                new { command = "state" },
                                new
                                {
                                    command = "new_game",
                                    arguments =
                                        "character, world, gender?, seed?, npcCount?"
                                },
                                new { command = "screenshot" },
                                new { command = "skip_cinematic" },
                                new
                                {
                                    command = "combat_style",
                                    arguments =
                                        "style: accurate|aggressive|defensive"
                                },
                                new
                                {
                                    command = "continue",
                                    description =
                                        "Dismiss the quest-complete popup"
                                },
                                new { command = "nearby", arguments = "radius?" },
                                new
                                {
                                    command = "world",
                                    description =
                                        "Report world, level, position and loaded objects"
                                },
                                new { command = "inventory" },
                                new { command = "recipes" },
                                new
                                {
                                    command = "craft",
                                    arguments = "recipe"
                                },
                                new
                                {
                                    command = "use",
                                    arguments =
                                        "slot/item, withSlot/withItem/with?, x/y?"
                                },
                                new
                                {
                                    command = "drop",
                                    arguments = "slot/item, x/y?"
                                },
                                new { command = "walk", arguments = "x, y" },
                                new
                                {
                                    command = "act",
                                    arguments = "action, x/y or actor, slot?",
                                    actions = new[]
                                    {
                                        "cut_tree", "gather_sticks", "pickup",
                                        "gather_fibres", "gather_berries",
                                        "fuel_campfire", "light_campfire",
                                        "take_campfire_fuel",
                                        "dig", "continue_dig", "restore_dig",
                                        "install_cave_rope", "enter_cave",
                                        "take_cave_rope", "fill_hole",
                                        "fish", "cook", "mine",
                                        "attack_villager", "attack_enemy",
                                        "give_item"
                                    }
                                },
                                new { command = "stop_player" },
                                new { command = "approach", arguments = "actor?" },
                                new { command = "chat", arguments = "text" },
                                new
                                {
                                    command = "chat_history",
                                    arguments =
                                        "scope: all, last10, unread/not_read",
                                    description =
                                        "Read non-debug game and conversation messages"
                                },
                                new { command = "events" },
                                new { command = "load_latest" },
                                new { command = "stop" }
                            }
                        }));
                        break;
                    case "state":
                        request.Complete(ControlSnapshot("state"));
                        break;
                    case "stop":
                        request.Complete(ControlSnapshot("stopping"));
                        Close();
                        break;
                    default:
                        request.Complete(Error("unknown_command"));
                        break;
                }
            }
            catch (Exception exception)
            {
                request.Complete(Error(exception.Message));
            }
        }
    }

    private string ControlInventory()
    {
        if (_activePlayer is null) return Error("world_not_loaded");
        var inventory = ActivePlayerInventory();
        var items = inventory.ItemIds();
        var quantities = inventory.Quantities();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "inventory",
            activeSlot = _activeInventorySlot,
            capacity = inventory.Capacity,
            slots = items.Select((itemId, slot) => new
            {
                slot,
                itemId,
                name = itemId is null ? null : ItemCatalog.Get(itemId).Name,
                quantity = quantities[slot]
            })
        });
    }

    private string ControlChatHistory(JsonElement root)
    {
        var requestedScope = root.TryGetProperty(
            "scope", out var scopeElement)
            ? scopeElement.GetString()?.Trim().ToLowerInvariant()
            : "last10";
        if (!ChatHistoryReader.TryParseScope(
                requestedScope, out var scope))
            return Error("invalid_chat_history_scope");
        var result = _controlChatHistory.Read(_chatUi.Messages, scope);
        var messages = result.Messages.Select(message => new
        {
            message.Sequence,
            text = message.Text,
            style = message.Style.ToString()
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "chat_history",
            scope = scope.ToString(),
            unreadAfter = result.ReadThroughSequence,
            messages
        });
    }

    private string ControlRecipes()
    {
        if (_activePlayer is null) return Error("world_not_loaded");
        RefreshNearbyCraftingStations();
        var level = CraftingSkill.LevelForExperience(
            _activePlayer.CraftingExperience);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "recipes",
            craftingLevel = level,
            recipes = CraftingSkill.Recipes.Select(recipe => new
            {
                recipe.Id,
                recipe.ResultItemId,
                result = ItemCatalog.Get(recipe.ResultItemId).Name,
                recipe.RequiredLevel,
                availability = RecipeAvailabilityFor(recipe).ToString(),
                ingredients = recipe.Ingredients.Select(ingredient => new
                {
                    ingredient.ItemId,
                    ingredient.Count,
                    ingredient.AlternativeItemIds
                }),
                recipe.RequiredStationItemId
            })
        });
    }

    private string ControlCraft(JsonElement root)
    {
        if (_activePlayer is null) return Error("world_not_loaded");
        if (!root.TryGetProperty("recipe", out var recipeElement) ||
            string.IsNullOrWhiteSpace(recipeElement.GetString()))
            return Error("recipe_required");
        var requested = recipeElement.GetString()!;
        var recipe = CraftingSkill.Recipes.FirstOrDefault(candidate =>
            candidate.Id.Equals(requested,
                StringComparison.OrdinalIgnoreCase) ||
            candidate.ResultItemId.Equals(requested,
                StringComparison.OrdinalIgnoreCase));
        if (recipe is null) return Error("recipe_not_found");

        RefreshNearbyCraftingStations();
        var availability = RecipeAvailabilityFor(recipe);
        if (availability != RecipeAvailability.Ready)
            return Error($"craft_{availability.ToString().ToLowerInvariant()}");
        var before = PlayerInventory.Normalize(
            _activePlayer.Inventory).ToArray();
        TryCraftRecipe(recipe);
        if (PlayerInventory.Normalize(
                _activePlayer.Inventory).SequenceEqual(before))
            return Error("craft_failed");
        return ControlSnapshot($"crafted:{recipe.Id}");
    }

    private string ControlUseInventory(JsonElement root)
    {
        if (_activePlayer is null) return Error("world_not_loaded");
        if (!TryResolveControlInventorySlot(
                root, "slot", "item", -1, out var sourceSlot,
                out var sourceError))
            return Error(sourceError);
        var inventory = PlayerInventory.Normalize(
            _activePlayer.Inventory);
        var sourceItem = inventory[sourceSlot]!;

        if (TryResolveOptionalControlInventorySlot(
                root, sourceSlot, out var targetSlot, out var targetError))
        {
            _activeInventorySlot = -1;
            ActivateInventorySlot(sourceSlot);
            ActivateInventorySlot(targetSlot);
            return ControlSnapshot("inventory_items_used_together");
        }
        if (targetError is not null) return Error(targetError);

        if (root.TryGetProperty("x", out var xElement) &&
            root.TryGetProperty("y", out var yElement))
        {
            var target = new Vector2(
                xElement.GetSingle(), yElement.GetSingle());
            if (!float.IsFinite(target.X) || !float.IsFinite(target.Y))
                return Error("invalid_position");
            QueueGroundObjectDrop(new(
                sourceSlot, sourceItem, target, Valid: true));
            return ControlSnapshot("inventory_use_queued");
        }

        var item = ItemCatalog.Get(sourceItem);
        if (SurvivalService.TryFoodEffect(sourceItem, out _))
            EatInventoryItem(sourceSlot, sourceItem);
        else if (item.HasTag(ItemTag.Shovel))
            BeginCaveDigTargeting(sourceSlot);
        else if (item.HasTag(ItemTag.Seed))
            TryPlantSeed(sourceSlot, sourceItem);
        else
        {
            _activeInventorySlot = -1;
            ActivateInventorySlot(sourceSlot);
        }
        return ControlSnapshot("inventory_item_used");
    }

    private string ControlDropInventory(JsonElement root)
    {
        if (_activePlayer is null) return Error("world_not_loaded");
        if (!TryResolveControlInventorySlot(
                root, "slot", "item", -1, out var slot,
                out var slotError))
            return Error(slotError);
        var itemId = PlayerInventory.Normalize(
            _activePlayer.Inventory)[slot]!;
        if (!PlayerInventory.CanDrop(itemId))
            return Error("item_not_droppable");

        if (root.TryGetProperty("x", out var xElement) &&
            root.TryGetProperty("y", out var yElement))
        {
            var target = new Vector2(
                xElement.GetSingle(), yElement.GetSingle());
            if (!float.IsFinite(target.X) || !float.IsFinite(target.Y))
                return Error("invalid_position");
            QueueGroundObjectDrop(new(slot, itemId, target, Valid: true));
        }
        else
            TryDropGroundObject(slot, itemId);
        return ControlSnapshot("inventory_drop_queued");
    }

    private bool TryResolveOptionalControlInventorySlot(
        JsonElement root, int excludedSlot, out int slot,
        out string? error)
    {
        slot = -1;
        error = null;
        if (root.TryGetProperty("withSlot", out _) ||
            root.TryGetProperty("withItem", out _))
        {
            var found = TryResolveControlInventorySlot(
                root, "withSlot", "withItem", excludedSlot,
                out slot, out var requiredError);
            error = found ? null : requiredError;
            return found;
        }
        if (!root.TryGetProperty("with", out var withElement)) return false;
        if (withElement.ValueKind == JsonValueKind.Number)
        {
            var inventory = _activePlayer?.Inventory ?? [];
            slot = withElement.GetInt32();
            if ((uint)slot >= (uint)inventory.Length ||
                slot == excludedSlot || inventory[slot] is null)
            {
                error = "with_item_not_found";
                slot = -1;
                return false;
            }
            return true;
        }
        var itemId = withElement.GetString();
        slot = FindControlInventoryItem(itemId, excludedSlot);
        if (slot >= 0) return true;
        error = "with_item_not_found";
        return false;
    }

    private bool TryResolveControlInventorySlot(
        JsonElement root, string slotProperty, string itemProperty,
        int excludedSlot, out int slot, out string error)
    {
        slot = -1;
        error = "item_required";
        var inventory = _activePlayer?.Inventory ?? [];
        if (root.TryGetProperty(slotProperty, out var slotElement))
        {
            slot = slotElement.GetInt32();
            if ((uint)slot < (uint)inventory.Length &&
                slot != excludedSlot && inventory[slot] is not null)
                return true;
            slot = -1;
            error = "item_not_found";
            return false;
        }
        if (!root.TryGetProperty(itemProperty, out var itemElement))
            return false;
        slot = FindControlInventoryItem(
            itemElement.GetString(), excludedSlot);
        if (slot >= 0) return true;
        error = "item_not_found";
        return false;
    }

    private int FindControlInventoryItem(string? requested, int excludedSlot)
    {
        if (string.IsNullOrWhiteSpace(requested)) return -1;
        var inventory = _activePlayer?.Inventory ?? [];
        for (var slot = 0; slot < inventory.Length; slot++)
        {
            if (slot == excludedSlot || inventory[slot] is not { } itemId)
                continue;
            var item = ItemCatalog.Get(itemId);
            if (itemId.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
                return slot;
        }
        return -1;
    }

    private string ControlNearby(JsonElement root)
    {
        if (_player is null) return Error("world_not_loaded");
        var radius = root.TryGetProperty("radius", out var radiusElement)
            ? Math.Clamp(radiusElement.GetSingle(), 1, 100)
            : 24;
        var origin = _player.Position;
        var radiusSquared = radius * radius;
        var chunks = _worldChunks.Values
            .Where(value =>
                value.Chunk.Coordinate.Level == _activeWorldLevel)
            .Select(value => value.Chunk)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "nearby",
            origin = new { origin.X, origin.Y },
            radius,
            trees = chunks.SelectMany(value => value.Trees)
                .Where(value => Vector2.DistanceSquared(
                    new(value.X + .5f, value.Y + .5f), origin) <=
                    radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    new(value.X + .5f, value.Y + .5f), origin))
                .Take(32)
                .Select(value => new
                {
                    x = value.X,
                    y = value.Y,
                    value.GraphicName,
                    distance = Vector2.Distance(
                        new(value.X + .5f, value.Y + .5f), origin)
                }),
            groundObjects = chunks.SelectMany(value => value.GroundObjects)
                .Where(value => Vector2.DistanceSquared(
                    new(value.X, value.Y), origin) <= radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    new(value.X, value.Y), origin))
                .Take(32)
                .Select(value => new
                {
                    value.Id,
                    value.ItemId,
                    value.X,
                    value.Y,
                    state = ControlGroundObjectState(value),
                    actions = ControlGroundObjectActions(value),
                    distance = Vector2.Distance(
                        new(value.X, value.Y), origin)
                }),
            vegetation = _worldChunks.Values
                .Where(value =>
                    value.Chunk.Coordinate.Level == _activeWorldLevel)
                .SelectMany(value => value.VegetationRenderItems
                    .Where(item => item.VegetationIndex >= 0 &&
                        (item.CanGatherFibre || item.CanGatherBerries))
                    .Select(item => new
                    {
                        Vegetation = value.Chunk.Vegetation[
                            item.VegetationIndex],
                        item.StableKey,
                        item.CanGatherFibre,
                        item.CanGatherBerries
                    }))
                .Where(value => Vector2.DistanceSquared(
                    new(value.Vegetation.X, value.Vegetation.Y), origin) <=
                    radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    new(value.Vegetation.X, value.Vegetation.Y), origin))
                .Take(32)
                .Select(value => new
                {
                    value.StableKey,
                    x = value.Vegetation.X,
                    y = value.Vegetation.Y,
                    kind = value.Vegetation.Kind.ToString(),
                    value.CanGatherFibre,
                    value.CanGatherBerries,
                    distance = Vector2.Distance(
                        new(value.Vegetation.X, value.Vegetation.Y), origin)
                }),
            fish = _worldChunks.Values
                .Where(value =>
                    value.Chunk.Coordinate.Level == _activeWorldLevel)
                .SelectMany(value => value.FishRenderItems)
                .Where(value => Vector2.DistanceSquared(
                    value.World, origin) <= radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    value.World, origin))
                .Take(32)
                .Select(value => new
                {
                    key = value.Fish.StableKey,
                    species = value.Fish.Species.ToString(),
                    x = value.World.X,
                    y = value.World.Y,
                    action = "fish",
                    distance = Vector2.Distance(value.World, origin)
                }),
            miningNodes = _worldChunks.Values
                .Where(value =>
                    value.Chunk.Coordinate.Level == _activeWorldLevel)
                .SelectMany(value => value.VegetationRenderItems)
                .Where(value => value.VegetationIndex >= 0 &&
                    FindMiningNode(value.StableKey) is not null)
                .Where(value => Vector2.DistanceSquared(
                    value.World, origin) <= radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    value.World, origin))
                .Take(32)
                .Select(value => new
                {
                    key = value.StableKey,
                    x = value.World.X,
                    y = value.World.Y,
                    action = "mine",
                    distance = Vector2.Distance(value.World, origin)
                }),
            villagers = _villagers
                .Where(value => value.Health > 0 &&
                    Vector2.DistanceSquared(
                        new(value.PositionX, value.PositionY), origin) <=
                    radiusSquared)
                .Select(value => new
                {
                    value.Id,
                    value.Name,
                    x = value.PositionX,
                    y = value.PositionY,
                    distance = Vector2.Distance(
                        new(value.PositionX, value.PositionY), origin)
                }),
            enemies = _enemies
                .Where(value => value.Alive &&
                    value.WorldLevel == _activeWorldLevel &&
                    Vector2.DistanceSquared(value.Position, origin) <=
                    radiusSquared)
                .OrderBy(value => Vector2.DistanceSquared(
                    value.Position, origin))
                .Select(value => new
                {
                    id = value.Id,
                    kind = value.Kind.ToString(),
                    x = value.Position.X,
                    y = value.Position.Y,
                    value.Health,
                    value.MaximumHealth,
                    behavior = value.Behavior.ToString(),
                    value.TargetId,
                    action = "attack_enemy",
                    distance = Vector2.Distance(value.Position, origin)
                })
        });
    }

    private string ControlWorld()
    {
        if (_player is null || _activeWorld is null)
            return Error("world_not_loaded");
        var chunks = _worldChunks.Values
            .Where(value =>
                value.Chunk.Coordinate.Level == _activeWorldLevel)
            .Select(value => value.Chunk)
            .ToArray();
        var position = _player.Position;
        var biome = _activeWorldLevel == (int)WorldLevel.Overworld
            ? InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(position.X),
                (int)MathF.Floor(position.Y)).ToString()
            : "Underground";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "world",
            world = new
            {
                _activeWorld.Id,
                _activeWorld.Name,
                seed = _worldSeed,
                level = _activeWorldLevel,
                levelName = WorldLevelMapPresentation.LevelName(
                    _activeWorldLevel),
                biome,
                gameSeconds = _worldGameSeconds,
                position = new { position.X, position.Y },
                loadedChunks = chunks.Length,
                loadedTrees = chunks.Sum(value => value.Trees.Length),
                loadedGroundObjects = chunks.Sum(
                    value => value.GroundObjects.Count),
                activeDigSiteId = _activeDigSiteId,
                caveEntranceLight = _caveEntranceLightWorld is { } light
                    ? new { light.X, light.Y }
                    : null
            }
        });
    }

    private string ControlGroundObjectState(WorldGroundObject value)
    {
        if (CampfireService.IsCampfire(value))
            return CampfireService.State(
                value, _worldGameSeconds).ToString();
        if (CaveEntranceService.IsDigSite(value))
            return $"Digging:{value.Health}/{value.MaxHealth}";
        if (CaveEntranceService.IsEntrance(value)) return "RopedEntrance";
        if (CaveEntranceService.IsHole(value)) return "OpenCaveShaft";
        if (CaveEntranceService.IsShallowHole(value)) return "ShallowHole";
        return "GroundObject";
    }

    private string[] ControlGroundObjectActions(WorldGroundObject value)
    {
        if (CampfireService.IsCampfire(value))
            return CampfireService.State(value, _worldGameSeconds) switch
            {
                CampfireState.Empty => ["fuel_campfire"],
                CampfireState.Fueled =>
                    ["light_campfire", "take_campfire_fuel"],
                CampfireState.Lit => [],
                _ => []
            };
        if (CaveEntranceService.IsDigSite(value))
            return ["continue_dig", "restore_dig"];
        if (CaveEntranceService.IsEntrance(value))
            return ["enter_cave", "take_cave_rope"];
        if (CaveEntranceService.IsHole(value))
            return ["install_cave_rope", "fill_hole"];
        if (CaveEntranceService.IsShallowHole(value))
            return ["fill_hole"];
        return ["pickup"];
    }

    private bool TryQueueControlAction(
        JsonElement root, out string error)
    {
        error = "invalid_action";
        if (_player is null || _activePlayer is null)
        {
            error = "world_not_loaded";
            return false;
        }
        var action = root.GetProperty("action").GetString();
        switch (action)
        {
            case "fuel_campfire":
                if (!TryResolveControlGroundObject(
                        root, CampfireService.IsCampfire,
                        out var fuelCampfire))
                {
                    error = "campfire_not_found";
                    return false;
                }
                if (!TryResolveControlInventorySlotByTag(
                        root, ItemTag.Log, out var fuelSlot,
                        out var fuelItemId))
                {
                    error = "campfire_fuel_not_found";
                    return false;
                }
                if (!CampfireService.CanAddFuel(
                        fuelCampfire, fuelItemId, _worldGameSeconds))
                {
                    error = "campfire_cannot_accept_fuel";
                    return false;
                }
                QueueGroundObjectDrop(new(
                    fuelSlot, fuelItemId,
                    new(fuelCampfire.X, fuelCampfire.Y),
                    Valid: true,
                    TargetObjectId: fuelCampfire.Id));
                return true;
            case "light_campfire":
                if (!TryResolveControlGroundObject(
                        root, CampfireService.IsCampfire,
                        out var lightCampfire))
                {
                    error = "campfire_not_found";
                    return false;
                }
                if (!CampfireService.CanLight(
                        lightCampfire, _activePlayer.Inventory ?? [],
                        _worldGameSeconds))
                {
                    error = "campfire_light_requirements";
                    return false;
                }
                QueueCampfireLight(lightCampfire);
                return true;
            case "take_campfire_fuel":
                if (!TryResolveControlGroundObject(
                        root,
                        value => CampfireService.IsCampfire(value) &&
                            CampfireService.CanRemoveFuel(
                                value, _worldGameSeconds),
                        out var fueledCampfire))
                {
                    error = "fueled_campfire_not_found";
                    return false;
                }
                QueueCampfireFuelPickup(fueledCampfire);
                return true;
            case "dig":
                if (_activeWorldLevel != (int)WorldLevel.Overworld ||
                    !TryControlPosition(root, out var digPosition))
                {
                    error = "invalid_dig_position";
                    return false;
                }
                if (!TryResolveControlInventorySlotByTag(
                        root, ItemTag.Shovel, out var shovelSlot, out _))
                {
                    error = "shovel_not_found";
                    return false;
                }
                QueueCaveDig(digPosition, shovelSlot);
                return true;
            case "continue_dig":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.IsDigSite,
                        out var digSite))
                {
                    error = "dig_site_not_found";
                    return false;
                }
                QueueContinueCaveDig(digSite);
                return true;
            case "restore_dig":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.IsDigSite,
                        out var restoreSite))
                {
                    error = "dig_site_not_found";
                    return false;
                }
                QueueRestoreExcavation(restoreSite);
                return true;
            case "install_cave_rope":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.IsHole,
                        out var caveHole))
                {
                    error = "cave_hole_not_found";
                    return false;
                }
                if (!TryResolveControlInventorySlot(
                        root, "slot", "item", -1,
                        out var ropeSlot, out _) ||
                    _activePlayer.Inventory?[ropeSlot] != ItemIds.Rope)
                {
                    ropeSlot = Array.FindIndex(
                        _activePlayer.Inventory ?? [],
                        value => value == ItemIds.Rope);
                }
                if (ropeSlot < 0)
                {
                    error = "rope_not_found";
                    return false;
                }
                QueueGroundObjectDrop(new(
                    ropeSlot, ItemIds.Rope,
                    new(caveHole.X, caveHole.Y), true, caveHole.Id));
                return true;
            case "enter_cave":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.IsEntrance,
                        out var entrance))
                {
                    error = "cave_entrance_not_found";
                    return false;
                }
                QueueCaveEntry(entrance);
                return true;
            case "take_cave_rope":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.IsEntrance,
                        out var ropedEntrance))
                {
                    error = "cave_entrance_not_found";
                    return false;
                }
                QueueTakeCaveRope(ropedEntrance);
                return true;
            case "fill_hole":
                if (!TryResolveControlGroundObject(
                        root, CaveEntranceService.CanFill,
                        out var hole))
                {
                    error = "hole_not_found";
                    return false;
                }
                if (!TryFindControlFillMaterial(
                        hole, out var fillSlot, out var fillItem))
                {
                    error = "fill_material_not_found";
                    return false;
                }
                QueueGroundObjectDrop(new(
                    fillSlot, fillItem, new(hole.X, hole.Y),
                    true, hole.Id));
                return true;
            case "fish":
                var fishKey = root.TryGetProperty(
                    "key", out var fishKeyElement)
                    ? fishKeyElement.GetString()
                    : null;
                var fish = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.FishRenderItems)
                    .Where(value => !IsFishDepleted(value.Fish) &&
                        (string.IsNullOrWhiteSpace(fishKey) ||
                         value.Fish.StableKey.Equals(
                             fishKey, StringComparison.Ordinal)))
                    .OrderBy(value => Vector2.DistanceSquared(
                        value.World, _player.Position))
                    .Select(value => value.Fish)
                    .FirstOrDefault();
                if (fish is null)
                {
                    error = "fish_not_found";
                    return false;
                }
                if (PlayerInventory.BestFishingNet(
                        _activePlayer.Inventory) is null)
                {
                    error = "fishing_net_not_found";
                    return false;
                }
                QueueFishing(fish);
                return true;
            case "cook":
                if (!TryResolveControlGroundObject(
                        root,
                        value => CampfireService.State(
                            value, _worldGameSeconds) == CampfireState.Lit,
                        out var cookingFire))
                {
                    error = "lit_campfire_not_found";
                    return false;
                }
                if (!TryResolveControlCookable(
                        root, out var rawSlot, out var rawItem))
                {
                    error = "cookable_item_not_found";
                    return false;
                }
                if (!CanCookOnCampfire(
                        cookingFire, rawItem, out var cookingReason))
                {
                    error = $"cannot_cook:{cookingReason}";
                    return false;
                }
                QueueCampfireCooking(cookingFire, rawSlot, rawItem);
                return true;
            case "mine":
                var miningKey = root.TryGetProperty(
                    "key", out var miningKeyElement)
                    ? miningKeyElement.GetString()
                    : null;
                var miningNode = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.VegetationRenderItems)
                    .Where(value => value.VegetationIndex >= 0 &&
                        (string.IsNullOrWhiteSpace(miningKey) ||
                         value.StableKey.Equals(
                             miningKey, StringComparison.Ordinal)))
                    .OrderBy(value => Vector2.DistanceSquared(
                        value.World, _player.Position))
                    .FirstOrDefault(value =>
                        FindMiningNode(value.StableKey) is not null);
                if (miningNode is null)
                {
                    error = "mining_node_not_found";
                    return false;
                }
                if (PlayerInventory.BestPickaxe(
                        _activePlayer.Inventory) is null)
                {
                    error = "pickaxe_not_found";
                    return false;
                }
                QueueMining(miningNode.StableKey);
                return true;
            case "cut_tree":
            case "gather_sticks":
                if (!TryControlPosition(root, out var treePosition))
                {
                    error = "invalid_position";
                    return false;
                }
                var tree = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.Chunk.Trees)
                    .Where(value =>
                        value.X == (int)MathF.Floor(treePosition.X) &&
                        value.Y == (int)MathF.Floor(treePosition.Y))
                    .FirstOrDefault();
                if (tree is null)
                {
                    error = "tree_not_found";
                    return false;
                }
                _worldActions.QueueTree(
                    tree,
                    action == "cut_tree"
                        ? WorldActionType.CutTree
                        : WorldActionType.GatherTreeSticks);
                return true;
            case "pickup":
                if (!TryControlPosition(root, out var pickupPosition))
                {
                    error = "invalid_position";
                    return false;
                }
                var groundObject = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.Chunk.GroundObjects)
                    .Where(value =>
                        Vector2.DistanceSquared(
                            new(value.X, value.Y), pickupPosition) <= .75f * .75f)
                    .OrderBy(value => Vector2.DistanceSquared(
                        new(value.X, value.Y), pickupPosition))
                    .FirstOrDefault();
                if (groundObject is null)
                {
                    error = "ground_object_not_found";
                    return false;
                }
                _worldActions.QueueGroundObjectPickup(groundObject);
                return true;
            case "gather_fibres":
            case "gather_berries":
                var wantsBerries = action == "gather_berries";
                var requestedKey = root.TryGetProperty(
                    "key", out var keyElement)
                    ? keyElement.GetString()
                    : null;
                Vector2? requestedVegetationPosition =
                    TryControlPosition(root, out var vegetationPosition)
                        ? vegetationPosition
                        : _player?.Position;
                var vegetationItem = ControlTargetSelection.Vegetation(
                    _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.VegetationRenderItems
                        .Where(item => item.VegetationIndex >= 0)
                        .Select(item => new ControlVegetationTarget(
                            item,
                            new(
                                value.Chunk.Vegetation[
                                    item.VegetationIndex].X,
                                value.Chunk.Vegetation[
                                    item.VegetationIndex].Y)))),
                    wantsBerries,
                    requestedKey,
                    requestedVegetationPosition,
                    requireNearbyPosition:
                        string.IsNullOrWhiteSpace(requestedKey) &&
                        TryControlPosition(root, out _));
                if (vegetationItem is null)
                {
                    error = "vegetation_not_found";
                    return false;
                }
                if (wantsBerries)
                    QueueBerryGather(vegetationItem.StableKey);
                else
                    QueueFibreGather(vegetationItem.StableKey);
                return true;
            case "attack_villager":
            case "give_item":
                var actor = root.TryGetProperty("actor", out var actorElement)
                    ? actorElement.GetString()
                    : null;
                var villager = _villagers.FirstOrDefault(value =>
                    value.Health > 0 &&
                    (value.Id.Equals(actor,
                         StringComparison.OrdinalIgnoreCase) ||
                     value.Name.Equals(actor,
                         StringComparison.OrdinalIgnoreCase)));
                if (villager is null)
                {
                    error = "villager_not_found";
                    return false;
                }
                if (action == "attack_villager")
                {
                    _worldActions.QueueVillagerAttack(villager);
                    return true;
                }
                var slot = root.TryGetProperty("slot", out var slotElement)
                    ? slotElement.GetInt32()
                    : -1;
                var inventory = PlayerInventory.Normalize(
                    _activePlayer.Inventory);
                if (slot < 0 || slot >= inventory.Length ||
                    inventory[slot] is not { } itemId)
                {
                    error = "invalid_inventory_slot";
                    return false;
                }
                _worldActions.QueueVillagerGift(
                    villager, slot, itemId);
                return true;
            case "attack_enemy":
                var requestedEnemy = root.TryGetProperty(
                    "actor", out var enemyElement)
                    ? enemyElement.GetString()
                    : null;
                var enemy = ControlTargetSelection.Enemy(
                    _enemies.Where(value =>
                        value.Alive &&
                        value.WorldLevel == _activeWorldLevel),
                    requestedEnemy,
                    _player?.Position);
                if (enemy is null)
                {
                    error = "enemy_not_found";
                    return false;
                }
                _worldActions.QueueEnemyAttack(enemy);
                return true;
            default:
                return false;
        }
    }

    private bool TryResolveControlInventorySlotByTag(
        JsonElement root, ItemTag tag, out int slot, out string itemId)
    {
        slot = -1;
        itemId = "";
        if (root.TryGetProperty("slot", out var slotElement))
        {
            var requested = slotElement.GetInt32();
            var inventory = _activePlayer?.Inventory ?? [];
            if ((uint)requested < (uint)inventory.Length &&
                inventory[requested] is { } requestedItem &&
                ItemCatalog.Get(requestedItem).HasTag(tag))
            {
                slot = requested;
                itemId = requestedItem;
                return true;
            }
            return false;
        }
        var values = _activePlayer?.Inventory ?? [];
        for (var index = 0; index < values.Length; index++)
            if (values[index] is { } candidate &&
                ItemCatalog.Get(candidate).HasTag(tag))
            {
                slot = index;
                itemId = candidate;
                return true;
            }
        return false;
    }

    private bool TryFindControlFillMaterial(
        WorldGroundObject hole, out int slot, out string itemId)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        for (var index = 0; index < inventory.Length; index++)
            if (inventory[index] is { } candidate &&
                CanFillExcavation(hole, candidate, out _))
            {
                slot = index;
                itemId = candidate;
                return true;
            }
        slot = -1;
        itemId = "";
        return false;
    }

    private bool TryResolveControlCookable(
        JsonElement root, out int slot, out string itemId)
    {
        if (TryResolveControlInventorySlot(
                root, "slot", "item", -1,
                out slot, out _) &&
            _activePlayer?.Inventory?[slot] is { } requested &&
            CookingSkill.TryProfile(requested, out _))
        {
            itemId = requested;
            return true;
        }
        var inventory = _activePlayer?.Inventory ?? [];
        for (var index = 0; index < inventory.Length; index++)
            if (inventory[index] is { } candidate &&
                CookingSkill.TryProfile(candidate, out _))
            {
                slot = index;
                itemId = candidate;
                return true;
            }
        slot = -1;
        itemId = "";
        return false;
    }

    private bool TryResolveControlGroundObject(
        JsonElement root,
        Func<WorldGroundObject, bool> predicate,
        out WorldGroundObject value)
    {
        var candidates = _worldChunks.Values
            .Where(chunk =>
                chunk.Chunk.Coordinate.Level == _activeWorldLevel)
            .SelectMany(chunk => chunk.Chunk.GroundObjects)
            .Where(predicate);
        if (root.TryGetProperty("id", out var idElement))
        {
            if (!Guid.TryParse(idElement.GetString(), out var id))
            {
                value = null!;
                return false;
            }
            candidates = candidates.Where(candidate => candidate.Id == id);
        }
        else if (TryControlPosition(root, out var position))
            candidates = candidates
                .Where(candidate => Vector2.DistanceSquared(
                    new(candidate.X, candidate.Y), position) <= 1f)
                .OrderBy(candidate => Vector2.DistanceSquared(
                    new(candidate.X, candidate.Y), position));
        else if (_player is { } player)
            candidates = candidates.OrderBy(candidate =>
                Vector2.DistanceSquared(
                    new(candidate.X, candidate.Y), player.Position));
        value = candidates.FirstOrDefault()!;
        return value is not null;
    }

    private static bool TryControlPosition(
        JsonElement root, out Vector2 position)
    {
        position = default;
        if (!root.TryGetProperty("x", out var xElement) ||
            !root.TryGetProperty("y", out var yElement))
            return false;
        var x = xElement.GetSingle();
        var y = yElement.GetSingle();
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        position = new(x, y);
        return true;
    }

    private string ControlScreenshot()
    {
        if (FramebufferSize.X <= 0 || FramebufferSize.Y <= 0)
            return Error("framebuffer_unavailable");
        var pixels = new byte[FramebufferSize.X * FramebufferSize.Y * 4];
        GL.ReadBuffer(ReadBufferMode.Front);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(
            0, 0, FramebufferSize.X, FramebufferSize.Y,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        var directory = Path.Combine(
            _saves.Root, "ControlPipe", "Screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"island-rpg-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
        PngScreenshotWriter.Write(
            path, pixels, FramebufferSize.X, FramebufferSize.Y,
            flipVertically: true);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "screenshot",
            path,
            width = FramebufferSize.X,
            height = FramebufferSize.Y
        });
    }

    private void LogControlPipeCommand(
        string? command, JsonElement root)
    {
        var detail = command == "chat" &&
                     root.TryGetProperty("text", out var text)
            ? $" \"{text.GetString()}\""
            : "";
        _chatUi.AddMessage(
            $"Codex: {command ?? "unknown"}{detail}",
            ChatMessageStyle.Debug);
    }

    private string ControlSnapshot(string eventType) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            eventType,
            screen = _screen.ToString(),
            gameSeconds = _worldGameSeconds,
            aiBusy = _npcAiSpeechTask is { IsCompleted: false },
            dialogueBusy = _npcAiDialogueTask is { IsCompleted: false },
            conversationFloorBusy = ConversationFloorBusy,
            world = _activeWorld is null ? null : new
            {
                _activeWorld.Id,
                _activeWorld.Name,
                seed = _worldSeed,
                level = _activeWorldLevel,
                levelName = WorldLevelMapPresentation.LevelName(
                    _activeWorldLevel),
                loadedChunks = _worldChunks.Count(value =>
                    value.Key.Level == _activeWorldLevel)
            },
            quest = _activePlayer is null
                ? null
                : QuestService.ActiveQuest(_activePlayer.Quests) is { } active
                    ? new
                    {
                        active.Definition.Id,
                        active.Definition.Title,
                        objectives = active.Definition.Objectives.Select(
                            objective => new
                            {
                                objective.Id,
                                objective.Description,
                                objective.Required,
                                current = active.Progress.ObjectiveCounts?
                                    .GetValueOrDefault(objective.Id) ?? 0
                            })
                    }
                    : null,
            ui = new
            {
                modal = _modalScreen.Active.ToString(),
                inputBlocked = CinematicActive ||
                    _modalScreen.CapturesAllInput,
                simulationPaused = _modalScreen.PausesSimulation,
                cinematic = CinematicActive,
                crafting = _craftingWindowOpen,
                skillGuide = _skillGuideWindow.Visible,
                itemContainer = _itemContainerWindow.Visible,
                developerMap = _developerMap.IsOpen,
                blockers = ControlUiBlockers()
            },
            actionQueue = new
            {
                pathPending = _pendingPathTask is not null,
                queuedAction = _queuedAction?.Type.ToString(),
                playerAction = _player?.Action.ToString(),
                moving = _player?.Action == EntityAction.Move,
                combatTarget = _combatEnemyId is { } enemyTarget
                    ? $"enemy:{enemyTarget:N}"
                    : _combatVillagerId is { } villagerTarget
                        ? $"villager:{villagerTarget}"
                        : _combatTargetId is { } objectTarget
                            ? $"object:{objectTarget:N}"
                            : null,
                readyForAction = _pendingPathTask is null &&
                    _queuedAction is null &&
                    _player?.Action is null or EntityAction.Idle
            },
            player = _activePlayer is null ? null : new
            {
                _activePlayer.Id,
                _activePlayer.Name,
                _activePlayer.Health,
                maximumHealth = AdventureService.MaximumHealth(
                    _activePlayer.AdventureExperience),
                _activePlayer.Hunger,
                combatStyle = _activePlayer.CombatStance.ToString(),
                _activePlayer.AttackExperience,
                _activePlayer.StrengthExperience,
                _activePlayer.DefenceExperience,
                combatEnemyId = _combatEnemyId,
                position = _player is null ? null : new
                {
                    _player.Position.X,
                    _player.Position.Y
                },
                inventory = _activePlayer.Inventory?
                    .Where(value => value is not null),
                inventoryStacks = _activePlayer.Inventory?
                    .Select((itemId, slot) => new
                    {
                        itemId,
                        quantity = _activePlayer.InventoryQuantities?
                            .ElementAtOrDefault(slot) ??
                            (itemId is null ? 0 : 1)
                    })
                    .Where(value => value.itemId is not null)
            },
            enemies = _enemies
                .Where(enemy => enemy.WorldLevel == _activeWorldLevel)
                .Select(enemy => new
                {
                    enemy.Id,
                    kind = enemy.Kind.ToString(),
                    enemy.Health,
                    enemy.MaximumHealth,
                    enemy.PowerLevel,
                    alive = enemy.Alive,
                    behavior = enemy.Behavior.ToString(),
                    enemy.TargetId,
                    position = new
                    {
                        enemy.Position.X,
                        enemy.Position.Y
                    }
                }),
            enemySpawners = _enemySpawners
                .Where(spawner =>
                    spawner.WorldLevel == _activeWorldLevel)
                .Select(spawner => new
                {
                    spawner.Id,
                    spawner.Wave,
                    spawner.MaximumAlive,
                    spawner.RecoveryUntil,
                    living = _enemies.Count(enemy =>
                        enemy.SpawnerId == spawner.Id && enemy.Alive),
                    position = new
                    {
                        spawner.Position.X,
                        spawner.Position.Y
                    }
                }),
            villagers = _villagers.Select(villager => new
            {
                villager.Id,
                villager.Name,
                villager.Health,
                villager.Hunger,
                villager.Energy,
                villager.Activity,
                villager.ActivityUntilGameSeconds,
                villager.NextDecisionGameSeconds,
                villager.Action,
                position = new { X = villager.PositionX, Y = villager.PositionY },
                inventory = villager.Inventory
                    .Where(value => value is not null),
                villager.LastDeliberation,
                villager.Promises,
                villager.ActionPlan,
                conversation = villager.ConversationHistory?.TakeLast(6)
            })
        });

    private string[] ControlUiBlockers()
    {
        var blockers = new List<string>(6);
        if (CinematicActive) blockers.Add("opening_cinematic");
        if (_modalScreen.IsOpen)
            blockers.Add($"modal:{_modalScreen.Active}");
        if (_craftingWindowOpen) blockers.Add("crafting");
        if (_skillGuideWindow.Visible) blockers.Add("skill_guide");
        if (_itemContainerWindow.Visible) blockers.Add("item_container");
        if (_developerMap.IsOpen) blockers.Add("developer_map");
        return blockers.ToArray();
    }

    private static string Error(string error) =>
        JsonSerializer.Serialize(new { ok = false, error });
}

internal static class ControlTargetSelection
{
    private const float CoordinateTolerance = 1f;

    public static WorldVegetationRenderItem? Vegetation(
        IEnumerable<ControlVegetationTarget> candidates,
        bool wantsBerries,
        string? stableKey,
        Vector2? position,
        bool requireNearbyPosition)
    {
        var eligible = candidates.Where(value =>
            (wantsBerries
                ? value.Item.CanGatherBerries
                : value.Item.CanGatherFibre));
        if (!string.IsNullOrWhiteSpace(stableKey))
            return eligible.FirstOrDefault(value =>
                value.Item.StableKey.Equals(
                    stableKey, StringComparison.OrdinalIgnoreCase)).Item;
        if (position is not { } origin)
            return eligible.FirstOrDefault().Item;
        if (requireNearbyPosition)
            eligible = eligible.Where(value =>
                Vector2.DistanceSquared(value.Position, origin) <=
                CoordinateTolerance * CoordinateTolerance);
        return eligible.OrderBy(value =>
                Vector2.DistanceSquared(value.Position, origin))
            .FirstOrDefault().Item;
    }

    public static EnemyState? Enemy(
        IEnumerable<EnemyState> candidates,
        string? actor,
        Vector2? origin)
    {
        if (!string.IsNullOrWhiteSpace(actor))
        {
            if (!Guid.TryParse(actor, out var id)) return null;
            return candidates.FirstOrDefault(value => value.Id == id);
        }
        return origin is { } position
            ? candidates.OrderBy(value => Vector2.DistanceSquared(
                    value.Position, position))
                .FirstOrDefault()
            : candidates.FirstOrDefault();
    }
}

internal static class ControlCombatCommands
{
    public static bool TryParseStance(
        string? value, out MeleeCombatStance stance) =>
        Enum.TryParse(value, true, out stance) &&
        Enum.IsDefined(stance);
}

internal readonly record struct ControlVegetationTarget(
    WorldVegetationRenderItem Item,
    Vector2 Position);

internal sealed class GameControlPipe : IDisposable
{
    internal sealed class Request(string json)
    {
        private readonly TaskCompletionSource<string> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Json { get; } = json;
        public Task<string> Response => _completion.Task;
        public void Complete(string response) =>
            _completion.TrySetResult(response);
    }

    private readonly string _name;
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<string> _published = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    public GameControlPipe(string name)
    {
        _name = name;
        _server = Task.Run(ServeAsync);
    }

    public bool TryDequeue(out Request request) =>
        _requests.TryDequeue(out request!);

    public void Publish(object message) =>
        _published.Enqueue(JsonSerializer.Serialize(message));

    public JsonElement[] DrainPublished()
    {
        var result = new List<JsonElement>();
        while (_published.TryDequeue(out var message))
        {
            using var document = JsonDocument.Parse(message);
            result.Add(document.RootElement.Clone());
        }
        return result.ToArray();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true)
                    { AutoFlush = true };
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line is null) continue;
                var request = new Request(line);
                _requests.Enqueue(request);
                var response = await request.Response.WaitAsync(
                    TimeSpan.FromSeconds(30), _stop.Token);
                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // A stalled main loop must not terminate the control server.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _server.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
