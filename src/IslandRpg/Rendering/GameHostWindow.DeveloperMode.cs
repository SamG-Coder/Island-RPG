using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool _godMode;
    private bool _noClipMode;

    private void HandleChatSubmission(string message)
    {
        var text = message.Trim();
        if (!text.StartsWith('/'))
        {
            ShowOverheadSpeech(message);
            return;
        }
        if (!ChatCommandRegistry.TryParse(text, out var command))
        {
            CommandMessage("Unknown command. Type /help.", warning: true);
            return;
        }
        if (command.Definition.RequiresDeveloperMode &&
            !_settingsMenu.DeveloperModeEnabled)
        {
            CommandMessage(
                $"{command.Definition.Name} requires developer mode.",
                warning: true);
            return;
        }
        ExecuteChatCommand(command);
    }

    private void ExecuteChatCommand(ParsedChatCommand command)
    {
        var args = command.Arguments;
        switch (command.Definition.Name)
        {
            case "/help":
                foreach (var item in ChatCommandRegistry.Visible(
                             _settingsMenu.DeveloperModeEnabled))
                    CommandMessage($"{item.Usage} - {item.Description}");
                break;
            case "/die":
                ForcePlayerDefeat("You surrender to death.");
                break;
            case "/stuck":
                TeleportToSafeSpawn();
                break;
            case "/where":
                if (_player is not null)
                    CommandMessage(
                        $"Position {_player.Position.X:0.##}, " +
                        $"{_player.Position.Y:0.##}; " +
                        WorldLevelMapPresentation.LevelName(
                            _activeWorldLevel) + ".");
                break;
            case "/clear":
                _chatUi.ClearMessages();
                break;
            case "/seed":
                CommandMessage($"World seed: {_worldSeed}.");
                break;
            case "/imahacker":
                EnableDeveloperModeFromCommand();
                break;
            case "/respawn":
                RespawnPlayer(force: true);
                break;
            case "/heal":
                HealFromCommand();
                break;
            case "/feed":
                FeedFromCommand();
                break;
            case "/god":
                _godMode = !_godMode;
                CommandMessage($"God mode {OnOff(_godMode)}.");
                break;
            case "/noclip":
                _noClipMode = !_noClipMode;
                CommandMessage($"Pathing collision {OnOff(!_noClipMode)}.");
                break;
            case "/teleport":
                TeleportFromCommand(args);
                break;
            case "/surface":
                SetWorldLevelFromCommand((int)WorldLevel.Overworld);
                break;
            case "/underground":
                SetWorldLevelFromCommand((int)WorldLevel.Underground);
                break;
            case "/time":
                SetTimeFromCommand(args);
                break;
            case "/give":
                GiveFromCommand(args);
                break;
            case "/xp":
                SetSkillFromCommand(args, setLevel: false);
                break;
            case "/level":
                SetSkillFromCommand(args, setLevel: true);
                break;
            case "/damage":
                if (TryInt(args, 0, 1, int.MaxValue, out var damage))
                    ApplyPlayerDamage(damage, "Developer damage");
                else
                    Usage(command.Definition);
                break;
            case "/spawn":
            case "/killall":
                CommandMessage(
                    "Hostile creature entities are not implemented yet.",
                    warning: true);
                break;
            case "/debug":
                CommandMessage(
                    $"Dev on; god {OnOff(_godMode)}; noclip " +
                    $"{OnOff(_noClipMode)}; level " +
                    $"{WorldLevelMapPresentation.LevelName(_activeWorldLevel)}.");
                break;
        }
    }

    private void UpdateCommandHints()
    {
        var items = _chatUi.Input.Focused
            ? ChatCommandRegistry.Filter(
                _chatUi.InputText,
                _settingsMenu.DeveloperModeEnabled)
            : [];
        _commandHints.UpdateItems(items, _chatUi.Input.Bounds);
    }

    private void CompleteCommandHint(ChatCommandDefinition command)
    {
        _chatUi.SetInputText(
            command.Usage.Contains('<') || command.Usage.Contains('[')
                ? command.Name + " "
                : command.Name);
        UpdateCommandHints();
    }

    private void RenderCommandHints()
    {
        if (!_commandHints.Visible) return;
        DrawUiColor(_commandHints.Bounds, new(.030f, .027f, .021f, .99f));
        DrawPanelOutline(
            _commandHints.Bounds, 2, new(.45f, .35f, .15f, 1));
        for (var rowIndex = 0;
             rowIndex < _commandHints.VisibleCount;
             rowIndex++)
        {
            var item = _commandHints.ItemAtVisibleRow(rowIndex);
            var row = _commandHints.RowBounds(rowIndex);
            var selected = _commandHints.IsSelectedVisibleRow(rowIndex);
            var hovered = row.Contains(MouseState.Position);
            if (selected || hovered)
                DrawUiColor(
                    row,
                    selected
                        ? new(.16f, .125f, .045f, .99f)
                        : new(.09f, .075f, .042f, .99f));
            if (rowIndex > 0)
                DrawUiColor(
                    new(row.X + 8, row.Y, row.Z - 16, 1),
                    new(.18f, .15f, .09f, 1));
            DrawUiText(
                item.Usage,
                new(row.X + 10, row.Y + 5),
                selected
                    ? new FSColor(246, 224, 166, 255)
                    : new FSColor(220, 207, 166, 255));
            DrawUiText(
                item.Description,
                new(row.X + 10, row.Y + 23),
                new FSColor(153, 147, 127, 255));
        }
        if (_commandHints.CanScroll)
        {
            DrawUiColor(
                _commandHints.ScrollTrackBounds,
                new(.035f, .032f, .027f, .98f));
            DrawUiColor(
                _commandHints.ScrollThumbBounds,
                new(.30f, .27f, .18f, 1));
            DrawPanelOutline(
                _commandHints.ScrollTrackBounds,
                0,
                new(.18f, .15f, .09f, 1));
        }
    }

    private void EnableDeveloperModeFromCommand()
    {
        var enabled = _settingsMenu.DeveloperModeEnabled;
        _settingsMenu.EnableDeveloperMode();
        CommandMessage(enabled
            ? "Developer mode is already enabled."
            : "Developer mode enabled. Open Pause > Settings > Dev.");
    }

    private void TeleportToSafeSpawn()
    {
        if (_player is null) return;
        CancelMeleeCombat();
        CancelWorldLevelWork(clearMinimap: true);
        _activeWorldLevel = (int)WorldLevel.Overworld;
        _caveEntranceLightWorld = null;
        _player.TeleportTo(FindPlayableSpawn());
        FollowPlayer();
        StreamWorld();
        SaveActivePlayerState();
        CommandMessage("Moved to the safe world spawn.");
    }

    private void HealFromCommand()
    {
        if (_activePlayer is null) return;
        _activePlayer = _activePlayer with
        {
            Health = AdventureService.MaximumHealth(
                _activePlayer.AdventureExperience),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        CommandMessage("Health restored.");
    }

    private void FeedFromCommand()
    {
        if (_activePlayer is null) return;
        _activePlayer = _activePlayer with
        {
            Hunger = SurvivalService.MaximumHunger,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        CommandMessage("Hunger restored.");
    }

    private void TeleportFromCommand(string[] args)
    {
        if (_player is null ||
            !TryFloat(args, 0, out var x) ||
            !TryFloat(args, 1, out var y))
        {
            Usage(ChatCommandRegistry.All.First(c => c.Name == "/teleport"));
            return;
        }
        CancelWorldLevelWork(clearMinimap: true);
        _player.TeleportTo(new(x, y));
        FollowPlayer();
        StreamWorld();
        SaveActivePlayerState();
        CommandMessage($"Teleported to {x:0.##}, {y:0.##}.");
    }

    private void SetWorldLevelFromCommand(int level)
    {
        if (_player is null) return;
        CancelWorldLevelWork(clearMinimap: true);
        _activeWorldLevel = level;
        _caveEntranceLightWorld = null;
        StreamWorld();
        SaveActivePlayerState();
        CommandMessage(
            $"Entered {WorldLevelMapPresentation.LevelName(level)}.");
    }

    private void SetTimeFromCommand(string[] args)
    {
        if (!TryFloat(args, 0, out var hour) ||
            hour is < 0 or >= 24)
        {
            Usage(ChatCommandRegistry.All.First(c => c.Name == "/time"));
            return;
        }
        const double daySeconds = 24 * 60 * 60;
        _worldGameSeconds =
            Math.Floor(_worldGameSeconds / daySeconds) * daySeconds +
            hour * 60 * 60;
        SaveActivePlayerState();
        CommandMessage($"Time set to {hour:00.##}:00.");
    }

    private void GiveFromCommand(string[] args)
    {
        if (_activePlayer is null || args.Length == 0)
        {
            Usage(ChatCommandRegistry.All.First(c => c.Name == "/give"));
            return;
        }
        var item = ItemCatalog.All.FirstOrDefault(value =>
            value.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        var amount = 1;
        if (item is null ||
            (args.Length > 1 &&
             !int.TryParse(args[1], out amount)) ||
            amount is < 1 or > PlayerInventory.Capacity)
        {
            Usage(ChatCommandRegistry.All.First(c => c.Name == "/give"));
            return;
        }
        var inventory = _activePlayer.Inventory;
        var added = 0;
        while (added < amount &&
               PlayerInventory.TryAdd(inventory, item.Id, out inventory))
            added++;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        CommandMessage($"Added {added} x {item.Id}.");
    }

    private void SetSkillFromCommand(string[] args, bool setLevel)
    {
        if (_activePlayer is null || args.Length < 2 ||
            !Enum.TryParse<SkillType>(args[0], true, out var skill) ||
            !int.TryParse(args[1], out var value) || value < 0 ||
            (setLevel && value < 1) ||
            (setLevel && value > (skill == SkillType.Adventure
                ? AdventureService.MaximumLevel
                : SkillService.MaximumLevel)))
        {
            Usage(ChatCommandRegistry.All.First(c =>
                c.Name == (setLevel ? "/level" : "/xp")));
            return;
        }
        var current = PlayerSkillExperience(_activePlayer, skill);
        var experience = setLevel
            ? skill == SkillType.Adventure
                ? AdventureService.ExperienceForLevel(value)
                : SkillService.ExperienceForLevel(value)
            : skill == SkillType.Adventure
                ? Math.Min(
                    AdventureService.ExperienceForLevel(
                        AdventureService.MaximumLevel),
                    current + value)
                : SkillService.AwardExperience(current, value).Experience;
        _activePlayer = SetPlayerSkillExperience(
            _activePlayer, skill, experience);
        _saves.SavePlayer(_activePlayer);
        CommandMessage(
            $"{skill} is now " +
            $"{(setLevel ? "level " + value : experience + " XP")}.");
    }

    private static IslandRpg.Persistence.PlayerProfile SetPlayerSkillExperience(
        IslandRpg.Persistence.PlayerProfile player,
        SkillType skill,
        int experience) =>
        (skill switch
        {
            SkillType.Woodcutting => player with { WoodcuttingExperience = experience },
            SkillType.Farming => player with { FarmingExperience = experience },
            SkillType.Crafting => player with { CraftingExperience = experience },
            SkillType.Fishing => player with { FishingExperience = experience },
            SkillType.Cooking => player with { CookingExperience = experience },
            SkillType.Firemaking => player with { FiremakingExperience = experience },
            SkillType.Digging => player with { DiggingExperience = experience },
            SkillType.Mining => player with { MiningExperience = experience },
            SkillType.Adventure => player with { AdventureExperience = experience },
            SkillType.Attack => player with { AttackExperience = experience },
            SkillType.Strength => player with { StrengthExperience = experience },
            _ => player with { DefenceExperience = experience }
        }) with { UpdatedUtc = DateTime.UtcNow };

    private static int PlayerSkillExperience(
        IslandRpg.Persistence.PlayerProfile player,
        SkillType skill) =>
        skill switch
        {
            SkillType.Woodcutting => player.WoodcuttingExperience,
            SkillType.Farming => player.FarmingExperience,
            SkillType.Crafting => player.CraftingExperience,
            SkillType.Fishing => player.FishingExperience,
            SkillType.Cooking => player.CookingExperience,
            SkillType.Firemaking => player.FiremakingExperience,
            SkillType.Digging => player.DiggingExperience,
            SkillType.Mining => player.MiningExperience,
            SkillType.Adventure => player.AdventureExperience,
            SkillType.Attack => player.AttackExperience,
            SkillType.Strength => player.StrengthExperience,
            _ => player.DefenceExperience
        };

    private void CommandMessage(string text, bool warning = false) =>
        _chatUi.AddMessage(
            text,
            warning ? ChatMessageStyle.Warning : ChatMessageStyle.Action);

    private void Usage(ChatCommandDefinition command) =>
        CommandMessage($"Usage: {command.Usage}", warning: true);

    private static bool TryFloat(
        string[] args, int index, out float value)
    {
        value = 0;
        return index < args.Length &&
               float.TryParse(
                   args[index],
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryInt(
        string[] args,
        int index,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return index < args.Length &&
               int.TryParse(args[index], out value) &&
               value >= minimum && value <= maximum;
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
