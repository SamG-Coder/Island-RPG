using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FontStashSharp;
using IslandRpg.Boats;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const string NewHostedWorldId = "new";

    private enum MultiplayerWizardStep
    {
        Character,
        Mode,
        Host,
        Join
    }

    private readonly TextBoxControlState _multiplayerEndpointTextBox =
        new("127.0.0.1:38740") { MaximumLength = 64 };
    private readonly TextBoxControlState _multiplayerSeedTextBox =
        new(Random.Shared.NextInt64().ToString());
    private readonly ListControlState _hostedWorldList = new();
    private readonly ListControlState _joinServerList = new();
    private readonly List<JoinServerChoice> _joinServerChoices = [];
    private readonly TextBoxControlState _serverNameTextBox =
        new("") { MaximumLength = 32 };
    private readonly List<HostedWorldChoice> _hostedWorldChoices = [];
    private bool _joinEditorOpen;
    private string? _joinEditingId;
    private LanDiscoveryListener? _lanDiscovery;
    private bool _multiplayerIslandStart;
    private string? _multiplayerStatus;
    private Process? _hostedServerProcess;
    private bool _multiplayerBusy;
    private MultiplayerWizardStep _multiplayerStep =
        MultiplayerWizardStep.Character;
    private FrontendPage _characterCreateReturnPage = FrontendPage.Main;

    private bool IsHostingWorld =>
        _hostedServerProcess is { HasExited: false };

    private Vector4 MultiplayerPanel() => _multiplayerStep switch
    {
        MultiplayerWizardStep.Character => FrontendPanel(720, 600),
        MultiplayerWizardStep.Mode => FrontendPanel(720, 540),
        MultiplayerWizardStep.Host => FrontendPanel(760, 640),
        MultiplayerWizardStep.Join => FrontendPanel(760, 640),
        _ => FrontendPanel(720, 600)
    };

    private bool IsMultiplayerCharacterStep =>
        _frontendPage == FrontendPage.Multiplayer &&
        _multiplayerStep == MultiplayerWizardStep.Character;

    private bool IsNewHostedWorldSelected =>
        _hostedWorldList.SelectedId is null or NewHostedWorldId;

    private void OpenMultiplayerPage()
    {
        var settings = _saves.LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.LastMultiplayerEndpoint))
            _multiplayerEndpointTextBox.SetText(
                SanitizeJoinEndpoint(settings.LastMultiplayerEndpoint));
        else
            _multiplayerEndpointTextBox.SetText("127.0.0.1:38740");
        RefreshHostedWorldChoices();
        _hostedWorldList.SelectedId = NewHostedWorldId;
        _multiplayerStatus = null;
        _frontendError = null;
        _multiplayerStep = MultiplayerWizardStep.Character;
        _characterCreateReturnPage = FrontendPage.Multiplayer;
        _joinEditorOpen = false;
        _joinEditingId = null;
        StopLanDiscovery();
        _frontendPage = FrontendPage.Multiplayer;
        BlurTextBoxes();
    }

    private void OpenCharacterCreateFromMultiplayer()
    {
        _characterCreateReturnPage = FrontendPage.Multiplayer;
        _playerNameTextBox.SetText("");
        _frontendPage = FrontendPage.CharacterCreate;
        FocusTextBoxAtEnd(_playerNameTextBox);
    }

    private void RefreshHostedWorldChoices()
    {
        _hostedWorldChoices.Clear();
        _hostedWorldChoices.Add(HostedWorldChoice.NewWorld);
        var saveRoot = HostedWorldsRoot();
        Directory.CreateDirectory(saveRoot);
        var seen = new HashSet<Guid>();
        var worldsRoot = Path.Combine(saveRoot, "Worlds");
        if (Directory.Exists(worldsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(worldsRoot))
            {
                var name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name, "N", out var worldId))
                    continue;
                var checkpoint = TryLoadHostedCheckpoint(saveRoot, worldId);
                if (checkpoint is null) continue;
                seen.Add(worldId);
                var written = File.GetLastWriteTimeUtc(
                    Path.Combine(directory, "server-checkpoint.json"));
                _hostedWorldChoices.Add(new(
                    worldId.ToString("N"),
                    worldId,
                    checkpoint.WorldSeed,
                    checkpoint.IslandStart,
                    false,
                    null,
                    null,
                    written));
            }
        }

        var last = LoadHostedWorld(saveRoot);
        if (last is not null && seen.Add(last.WorldId))
            _hostedWorldChoices.Add(new(
                last.WorldId.ToString("N"),
                last.WorldId,
                last.Seed,
                last.IslandStart,
                false,
                last.SpawnX,
                last.SpawnY,
                DateTime.UtcNow));
    }

    private void LayoutHostedWorldList()
    {
        var list = HostedWorldListBounds();
        _hostedWorldList.Layout(
            list,
            _hostedWorldChoices.Select(value => value.Id).ToArray(),
            rowHeight: 44,
            rowGap: 6,
            deleteWidth: 72,
            actionGap: 6);
        _hostedWorldList.SelectedId ??= NewHostedWorldId;
    }

    private void RenderMultiplayerMenu()
    {
        var panel = MultiplayerPanel();
        DrawAoEPanelBorder(panel);
        switch (_multiplayerStep)
        {
            case MultiplayerWizardStep.Character:
                RenderMultiplayerCharacterStep(panel);
                break;
            case MultiplayerWizardStep.Mode:
                RenderMultiplayerModeStep(panel);
                break;
            case MultiplayerWizardStep.Host:
                RenderMultiplayerHostStep(panel);
                break;
            case MultiplayerWizardStep.Join:
                EnsureLanDiscovery();
                RenderMultiplayerJoinStep(panel);
                break;
        }

        if (_multiplayerStep != MultiplayerWizardStep.Join)
            StopLanDiscovery();
    }

    private void RenderMultiplayerChrome(
        Vector4 panel, string title, string subtitle)
    {
        var header = new Vector4(
            panel.X + 18, panel.Y + 18, panel.Z - 36, 108);
        DrawUiColor(header, new(.052f, .044f, .027f, 1));
        DrawPanelOutline(header, 0, new(.34f, .27f, .13f, 1));
        DrawPanelOutline(header, 1, new(.10f, .085f, .052f, 1));
        DrawCenteredMenuTitle(
            title,
            new(header.X, header.Y + 13, header.Z, 44),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            subtitle,
            new(header.X + 24, header.Y + 63, header.Z - 48, 22),
            new(180, 158, 107, 255));
        DrawUiColor(
            new(header.X + 142, header.Y + 91, header.Z - 284, 1),
            new(.46f, .34f, .13f, 1));
        RenderMultiplayerStepTrail(panel);
    }

    private void RenderMultiplayerStepTrail(Vector4 panel)
    {
        var current = _multiplayerStep switch
        {
            MultiplayerWizardStep.Character => 0,
            MultiplayerWizardStep.Mode => 1,
            _ => 2
        };
        string[] labels = ["1  CHARACTER", "2  PLAY", "3  CONNECT"];
        var trail = new Vector4(
            panel.X + 44, panel.Y + 136, panel.Z - 88, 28);
        var width = trail.Z / labels.Length;
        for (var index = 0; index < labels.Length; index++)
        {
            var cell = new Vector4(
                trail.X + index * width, trail.Y, width - 8, trail.W);
            var active = index == current;
            var done = index < current;
            DrawUiColor(
                cell,
                active
                    ? new(.155f, .12f, .055f, 1)
                    : done
                        ? new(.08f, .068f, .040f, 1)
                        : new(.038f, .034f, .026f, 1));
            DrawPanelOutline(
                cell, 0,
                active
                    ? new(.57f, .42f, .14f, 1)
                    : done
                        ? new(.36f, .28f, .12f, 1)
                        : new(.20f, .17f, .10f, 1));
            DrawCenteredUiText(
                labels[index],
                cell,
                active
                    ? new(241, 222, 162, 255)
                    : done
                        ? new(186, 166, 112, 255)
                        : new(122, 114, 94, 255));
        }
    }

    private void RenderMultiplayerAdventurerChip(Vector4 bounds)
    {
        DrawUiColor(bounds, new(.038f, .035f, .026f, 1));
        DrawPanelOutline(bounds, 0, new(.23f, .19f, .11f, 1));
        if (_selectedPlayer is null)
        {
            DrawCenteredUiText(
                "NO ADVENTURER SELECTED",
                bounds,
                new(220, 104, 82, 255));
            return;
        }

        var team = TeamColor(_selectedPlayer.TeamColor);
        DrawUiColor(
            new(bounds.X + 2, bounds.Y + 2, 4, bounds.W - 4),
            new(team.X, team.Y, team.Z, 1));
        var level = AdventureService.LevelForExperience(
            _selectedPlayer.AdventureExperience);
        DrawCenteredUiText(
            $"{_selectedPlayer.Name.ToUpperInvariant()}   •   " +
            $"{_selectedPlayer.Gender.ToString().ToUpperInvariant()}   •   " +
            $"ADVENTURE {level}",
            bounds,
            new(199, 184, 142, 255));
    }

    private void RenderMultiplayerCharacterStep(Vector4 panel)
    {
        RenderMultiplayerChrome(
            panel, "MULTIPLAYER", "CHOOSE YOUR ADVENTURER");
        var players = _saves.ListPlayers().ToArray();
        LayoutCharacterList(players);
        DrawUiText(
            "ADVENTURERS",
            new(panel.X + 44, panel.Y + 176),
            new FSColor(199, 184, 142, 255));
        var count = players.Length == 1
            ? "1 CHARACTER"
            : $"{players.Length} CHARACTERS";
        var size = _chatFont?.MeasureString(count) ??
                   System.Numerics.Vector2.Zero;
        DrawUiText(
            count,
            new(panel.X + panel.Z - 44 - size.X, panel.Y + 176),
            new FSColor(130, 124, 106, 255));
        if (players.Length == 0)
            RenderEmptyCharacterSelection(panel);
        else
            RenderCharacterRows(players);
        RenderListScrollbar(_characterList);
        DrawUiColor(
            new(panel.X + 44, panel.Y + panel.W - 106, panel.Z - 104, 1),
            new(.25f, .20f, .11f, 1));
        DrawMainMenuButton(
            NewCharacterButtonBounds(), "NEW CHARACTER");
        DrawMainMenuButton(
            CharacterSelectionBackButtonBounds(), "Back", quiet: true);
        if (_selectedPlayer is not null)
            DrawMainMenuButton(
                ContinueCharacterButtonBounds(),
                "CONTINUE",
                primary: true);
        RenderMultiplayerStatus(panel);
    }

    private void RenderMultiplayerModeStep(Vector4 panel)
    {
        RenderMultiplayerChrome(
            panel, "MULTIPLAYER", "HOW WILL YOU PLAY");
        RenderMultiplayerAdventurerChip(MultiplayerChipBounds());
        RenderMultiplayerModeCard(
            MultiplayerHostCardBounds(),
            "HOST A WORLD",
            "Start a dedicated world on this PC. Friends join with your LAN address.");
        RenderMultiplayerModeCard(
            MultiplayerJoinCardBounds(),
            "JOIN A FRIEND",
            "Connect to a host:port. Use 127.0.0.1 on this machine.");
        DrawMainMenuButton(MultiplayerBackBounds(), "Back", quiet: true);
        RenderMultiplayerStatus(panel);
    }

    private void RenderMultiplayerModeCard(
        Vector4 bounds, string title, string detail)
    {
        var hovered = bounds.Contains(MouseState.Position);
        DrawUiColor(
            bounds,
            hovered
                ? new(.12f, .096f, .050f, 1)
                : new(.047f, .042f, .032f, .96f));
        DrawPanelOutline(
            bounds, 0,
            hovered
                ? new(.65f, .48f, .17f, 1)
                : new(.28f, .23f, .14f, 1));
        DrawPanelOutline(bounds, 1, new(.045f, .040f, .029f, 1));
        DrawCenteredUiText(
            title,
            new(bounds.X + 16, bounds.Y + 22, bounds.Z - 32, 28),
            new(241, 222, 162, 255));
        DrawWrappedCenteredUiText(
            detail,
            new(bounds.X + 22, bounds.Y + 58, bounds.Z - 44, 88),
            new(154, 142, 112, 255));
    }

    private void DrawWrappedCenteredUiText(
        string text, Vector4 bounds, FSColor color)
    {
        var lines = WrapUiText(text, bounds.Z).ToArray();
        const float lineHeight = 20;
        var total = lines.Length * lineHeight;
        var y = bounds.Y + MathF.Max(0, (bounds.W - total) * .5f);
        foreach (var line in lines)
        {
            DrawCenteredUiText(
                line,
                new(bounds.X, y, bounds.Z, lineHeight),
                color);
            y += lineHeight;
        }
    }

    private IEnumerable<string> WrapUiText(string text, float maximumWidth)
    {
        var line = "";
        foreach (var word in text.Split(
                     ' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && MeasureUiText(candidate) > maximumWidth)
            {
                yield return line;
                line = word;
            }
            else
                line = candidate;
        }

        if (line.Length > 0)
            yield return line;
    }

    private void RenderMultiplayerHostStep(Vector4 panel)
    {
        RefreshHostedWorldChoices();
        LayoutHostedWorldList();
        RenderMultiplayerChrome(
            panel, "HOST WORLD", "CHOOSE OR CREATE A WORLD");
        RenderMultiplayerAdventurerChip(MultiplayerChipBounds());
        var hostBox = HostBoxBounds();
        DrawUiColor(hostBox, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(hostBox, 0, new(.28f, .23f, .14f, 1));
        DrawUiText(
            "SAVED WORLDS",
            new(hostBox.X + 18, hostBox.Y + 12),
            new(218, 202, 158, 255));
        RenderHostedWorldRows();
        RenderListScrollbar(_hostedWorldList);

        var seedBounds = HostSeedBounds();
        if (IsNewHostedWorldSelected)
        {
            _multiplayerSeedTextBox.Bounds = seedBounds;
            DrawUiText(
                "SEED",
                new(seedBounds.X, seedBounds.Y - 16),
                new(204, 190, 150, 255));
            DrawTextField(_multiplayerSeedTextBox);
            DrawMenuButton(HostRandomSeedBounds(), "Random");
            DrawMenuButton(
                HostIslandBounds(),
                _multiplayerIslandStart ? "Shore start: On" : "Shore start: Off");
        }
        else if (SelectedHostedWorld() is { } selected)
        {
            DrawUiText(
                selected.IslandStart
                    ? $"Resume shore world  •  seed {selected.Seed}"
                    : $"Resume open world  •  seed {selected.Seed}",
                new(seedBounds.X, seedBounds.Y + 8),
                new(180, 158, 107, 255));
        }

        DrawMainMenuButton(MultiplayerBackBounds(), "Back", quiet: true);
        DrawMainMenuButton(
            HostStartButtonBounds(),
            IsHostingWorld
                ? "HOSTING…"
                : IsNewHostedWorldSelected ? "HOST NEW WORLD" : "HOST THIS WORLD",
            primary: true);
        RenderMultiplayerStatus(panel);
    }

    private void RenderMultiplayerJoinStep(Vector4 panel)
    {
        RefreshJoinServerChoices();
        LayoutJoinServerList();
        RenderMultiplayerChrome(
            panel, "JOIN WORLD", "SAVED SERVERS AND LAN WORLDS");
        RenderMultiplayerAdventurerChip(MultiplayerChipBounds());
        var joinBox = JoinBoxBounds();
        DrawUiColor(joinBox, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(joinBox, 0, new(.28f, .23f, .14f, 1));
        DrawUiText(
            "SERVER LIST",
            new(joinBox.X + 18, joinBox.Y + 12),
            new(218, 202, 158, 255));
        if (_joinServerChoices.Count == 0)
        {
            var empty = JoinServerListBounds();
            DrawCenteredUiText(
                "No saved or LAN servers yet",
                new(empty.X, empty.Y + 28, empty.Z, 22),
                new(145, 138, 117, 255));
        }
        else
            RenderJoinServerRows();
        RenderListScrollbar(_joinServerList);

        if (_joinEditorOpen)
            RenderJoinServerEditor();
        else
        {
            var endpointBounds = JoinEndpointBounds();
            _multiplayerEndpointTextBox.Bounds = endpointBounds;
            DrawUiText(
                "DIRECT CONNECT",
                new(endpointBounds.X, endpointBounds.Y - 16),
                new(204, 190, 150, 255));
            DrawTextField(_multiplayerEndpointTextBox);
            DrawMenuButton(JoinAddServerBounds(), "Add server");
        }

        DrawMainMenuButton(MultiplayerBackBounds(), "Back", quiet: true);
        DrawMainMenuButton(JoinButtonBounds(), "JOIN WORLD", primary: true);
        RenderMultiplayerStatus(panel);
    }

    private void RenderJoinServerEditor()
    {
        var name = JoinServerNameBounds();
        var address = JoinEndpointBounds();
        _serverNameTextBox.Bounds = name;
        _multiplayerEndpointTextBox.Bounds = address;
        DrawUiText(
            "SERVER NAME",
            new(name.X, name.Y - 16),
            new(204, 190, 150, 255));
        DrawTextField(_serverNameTextBox);
        DrawUiText(
            "HOST:PORT",
            new(address.X, address.Y - 16),
            new(204, 190, 150, 255));
        DrawTextField(_multiplayerEndpointTextBox);
        DrawMenuButton(JoinSaveServerBounds(), "Save");
        DrawMenuButton(JoinCancelEditBounds(), "Cancel");
    }

    private void RenderJoinServerRows()
    {
        foreach (var index in _joinServerList.VisibleIndices)
        {
            if ((uint)index >= (uint)_joinServerChoices.Count) continue;
            var choice = _joinServerChoices[index];
            var row = _joinServerList.RowBounds(index);
            var action = _joinServerList.DeleteBounds(index);
            var selected = _joinServerList.SelectedId == choice.Id;
            var hovered = row.Contains(MouseState.Position);
            DrawUiColor(
                row,
                selected
                    ? new(.155f, .12f, .055f, 1)
                    : hovered
                        ? new(.10f, .086f, .052f, 1)
                        : new(.060f, .055f, .041f, 1));
            DrawPanelOutline(
                row, 0,
                selected
                    ? new(.57f, .42f, .14f, 1)
                    : new(.23f, .19f, .11f, 1));
            DrawUiText(
                choice.Title,
                new(row.X + 12, row.Y + 6),
                new FSColor(232, 217, 166, 255));
            DrawUiText(
                choice.Details,
                new(row.X + 12, row.Y + 24),
                new FSColor(142, 136, 116, 255));
            var pending = !choice.IsLan &&
                          _joinServerList.IsDeletePending(choice.Id);
            DrawMenuButton(
                action,
                choice.IsLan ? "SAVE" : pending ? "CONFIRM" : "DELETE");
        }
    }

    private void RenderMultiplayerStatus(Vector4 panel)
    {
        var status = _frontendError ?? _multiplayerStatus;
        if (string.IsNullOrWhiteSpace(status)) return;
        DrawCenteredUiText(
            status,
            new(panel.X + 160, panel.Y + panel.W - 94, panel.Z - 320, 22),
            _frontendError is null
                ? new(199, 184, 142, 255)
                : new(220, 104, 82, 255));
    }

    private void RenderHostedWorldRows()
    {
        foreach (var index in _hostedWorldList.VisibleIndices)
        {
            if ((uint)index >= (uint)_hostedWorldChoices.Count) continue;
            var choice = _hostedWorldChoices[index];
            var row = _hostedWorldList.RowBounds(index);
            var delete = _hostedWorldList.DeleteBounds(index);
            var selected = _hostedWorldList.SelectedId == choice.Id;
            var hovered = row.Contains(MouseState.Position);
            DrawUiColor(
                row,
                selected
                    ? new(.155f, .12f, .055f, 1)
                    : hovered
                        ? new(.10f, .086f, .052f, 1)
                        : new(.060f, .055f, .041f, 1));
            DrawPanelOutline(
                row, 0,
                selected
                    ? new(.57f, .42f, .14f, 1)
                    : new(.23f, .19f, .11f, 1));
            DrawUiText(
                choice.Title,
                new(row.X + 12, row.Y + 6),
                new FSColor(232, 217, 166, 255));
            DrawUiText(
                choice.Details,
                new(row.X + 12, row.Y + 24),
                new FSColor(142, 136, 116, 255));
            if (choice.IsNew) continue;
            var pending = _hostedWorldList.IsDeletePending(choice.Id);
            DrawMenuButton(delete, pending ? "CONFIRM" : "DELETE");
        }
    }

    private void UpdateMultiplayerClick(Vector2 pointer)
    {
        if (_multiplayerBusy) return;
        switch (_multiplayerStep)
        {
            case MultiplayerWizardStep.Character:
                UpdateMultiplayerCharacterClick(pointer);
                return;
            case MultiplayerWizardStep.Mode:
                UpdateMultiplayerModeClick(pointer);
                return;
            case MultiplayerWizardStep.Host:
                UpdateMultiplayerHostClick(pointer);
                return;
            case MultiplayerWizardStep.Join:
                UpdateMultiplayerJoinClick(pointer);
                return;
        }
    }

    private void UpdateMultiplayerCharacterClick(Vector2 pointer)
    {
        var players = _saves.ListPlayers().ToArray();
        LayoutCharacterList(players);
        if (_characterList.TryHit(pointer, out var index, out var delete) &&
            (uint)index < (uint)players.Length)
        {
            var player = players[index];
            if (!delete)
            {
                _selectedPlayer = player;
                _characterList.SelectedId = player.Id;
                _characterList.ClearDeleteApproval();
                _frontendError = null;
                return;
            }
            if (!_characterList.ApproveDelete(player.Id))
                return;
            var deletingSelected = _selectedPlayer?.Id == player.Id;
            _saves.DeletePlayer(player.Id);
            var remaining = _saves.ListPlayers();
            if (deletingSelected) _selectedPlayer = remaining.FirstOrDefault();
            if (remaining.Count == 0)
                OpenCharacterCreateFromMultiplayer();
            return;
        }

        if (NewCharacterButtonBounds().Contains(pointer))
            OpenCharacterCreateFromMultiplayer();
        else if (_selectedPlayer is not null &&
                 ContinueCharacterButtonBounds().Contains(pointer))
        {
            _multiplayerStep = MultiplayerWizardStep.Mode;
            _frontendError = null;
            BlurTextBoxes();
        }
        else if (CharacterSelectionBackButtonBounds().Contains(pointer))
        {
            _frontendPage = FrontendPage.Main;
            _characterCreateReturnPage = FrontendPage.Main;
            BlurTextBoxes();
        }
    }

    private void UpdateMultiplayerModeClick(Vector2 pointer)
    {
        if (MultiplayerHostCardBounds().Contains(pointer))
        {
            _multiplayerStep = MultiplayerWizardStep.Host;
            _frontendError = null;
            BlurTextBoxes();
        }
        else if (MultiplayerJoinCardBounds().Contains(pointer))
        {
            _multiplayerStep = MultiplayerWizardStep.Join;
            _frontendError = null;
            BlurTextBoxes();
        }
        else if (MultiplayerBackBounds().Contains(pointer))
        {
            _multiplayerStep = MultiplayerWizardStep.Character;
            BlurTextBoxes();
        }
    }

    private void UpdateMultiplayerHostClick(Vector2 pointer)
    {
        RefreshHostedWorldChoices();
        LayoutHostedWorldList();
        if (_hostedWorldList.TryHit(pointer, out var index, out var delete) &&
            (uint)index < (uint)_hostedWorldChoices.Count)
        {
            var choice = _hostedWorldChoices[index];
            if (delete && !choice.IsNew)
            {
                if (_hostedWorldList.ApproveDelete(choice.Id))
                    DeleteHostedWorld(choice.WorldId);
                return;
            }

            _hostedWorldList.SelectedId = choice.Id;
            _hostedWorldList.ClearDeleteApproval();
            _frontendError = null;
            return;
        }

        if (IsNewHostedWorldSelected &&
            HostSeedBounds().Contains(pointer))
            FocusTextBox(
                _multiplayerSeedTextBox, HostSeedBounds(), pointer);
        else if (IsNewHostedWorldSelected &&
                 HostRandomSeedBounds().Contains(pointer))
        {
            _multiplayerSeedTextBox.SetText(
                Random.Shared.NextInt64().ToString());
            FocusTextBoxAtEnd(_multiplayerSeedTextBox);
        }
        else if (IsNewHostedWorldSelected &&
                 HostIslandBounds().Contains(pointer))
            _multiplayerIslandStart = !_multiplayerIslandStart;
        else if (HostStartButtonBounds().Contains(pointer))
            _ = HostMultiplayerWorldAsync();
        else if (MultiplayerBackBounds().Contains(pointer))
        {
            _multiplayerStep = MultiplayerWizardStep.Mode;
            BlurTextBoxes();
        }
        else
            BlurTextBoxes();
    }

    private void UpdateMultiplayerJoinClick(Vector2 pointer)
    {
        RefreshJoinServerChoices();
        LayoutJoinServerList();
        if (_joinServerList.TryHit(pointer, out var index, out var action) &&
            (uint)index < (uint)_joinServerChoices.Count)
        {
            var choice = _joinServerChoices[index];
            if (action)
            {
                if (choice.IsLan)
                {
                    _saves.UpsertSavedServer(
                        choice.Title, choice.Host, choice.Port);
                    _multiplayerStatus = $"Saved {choice.Title}.";
                    _frontendError = null;
                    return;
                }

                if (_joinServerList.ApproveDelete(choice.Id))
                {
                    _saves.RemoveSavedServer(choice.Id);
                    _joinServerList.SelectedId = null;
                }
                return;
            }

            _joinServerList.SelectedId = choice.Id;
            _joinServerList.ClearDeleteApproval();
            _multiplayerEndpointTextBox.SetText($"{choice.Host}:{choice.Port}");
            _frontendError = null;
            return;
        }

        if (_joinEditorOpen)
        {
            if (JoinServerNameBounds().Contains(pointer))
                FocusTextBox(
                    _serverNameTextBox, JoinServerNameBounds(), pointer);
            else if (JoinEndpointBounds().Contains(pointer))
                FocusTextBox(
                    _multiplayerEndpointTextBox, JoinEndpointBounds(), pointer);
            else if (JoinSaveServerBounds().Contains(pointer))
                SaveJoinServerFromEditor();
            else if (JoinCancelEditBounds().Contains(pointer))
            {
                _joinEditorOpen = false;
                _joinEditingId = null;
                BlurTextBoxes();
            }
            else if (JoinButtonBounds().Contains(pointer))
                JoinMultiplayerWorld();
            else if (MultiplayerBackBounds().Contains(pointer))
                LeaveJoinStep();
            else
                BlurTextBoxes();
            return;
        }

        if (JoinEndpointBounds().Contains(pointer))
            FocusTextBox(
                _multiplayerEndpointTextBox, JoinEndpointBounds(), pointer);
        else if (JoinAddServerBounds().Contains(pointer))
            OpenJoinServerEditor(null);
        else if (JoinButtonBounds().Contains(pointer))
            JoinMultiplayerWorld();
        else if (MultiplayerBackBounds().Contains(pointer))
            LeaveJoinStep();
        else
            BlurTextBoxes();
    }

    private void LeaveJoinStep()
    {
        _joinEditorOpen = false;
        _joinEditingId = null;
        _multiplayerStep = MultiplayerWizardStep.Mode;
        StopLanDiscovery();
        BlurTextBoxes();
    }

    private void OpenJoinServerEditor(JoinServerChoice? selected)
    {
        _joinEditorOpen = true;
        _joinEditingId = selected?.IsLan == false ? selected.Id : null;
        _serverNameTextBox.SetText(selected?.Title ?? "");
        if (selected is not null)
            _multiplayerEndpointTextBox.SetText(
                $"{selected.Host}:{selected.Port}");
        FocusTextBoxAtEnd(_serverNameTextBox);
        _frontendError = null;
    }

    private void SaveJoinServerFromEditor()
    {
        try
        {
            var launch = NetworkLaunchOptions.Parse(
                _multiplayerEndpointTextBox.Text, "join");
            var name = _serverNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"{launch.Host}:{launch.Port}";
            if (_joinEditingId is { } id)
            {
                var servers = _saves.LoadSavedServers()
                    .Select(value =>
                        value.Id == id
                            ? value with
                            {
                                Name = name,
                                Host = launch.Host,
                                Port = launch.Port
                            }
                            : value)
                    .ToArray();
                _saves.SaveSavedServers(servers);
            }
            else
                _saves.UpsertSavedServer(name, launch.Host, launch.Port);
            _multiplayerEndpointTextBox.SetText(
                $"{launch.Host}:{launch.Port}");
            _joinEditorOpen = false;
            _joinEditingId = null;
            _multiplayerStatus = $"Saved {name}.";
            _frontendError = null;
            BlurTextBoxes();
        }
        catch (Exception exception)
        {
            _frontendError = exception.Message;
        }
    }

    private void RefreshJoinServerChoices()
    {
        _joinServerChoices.Clear();
        var saved = _saves.LoadSavedServers();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in saved)
        {
            var key = LanDiscoveryListener.Key(server.Host, server.Port);
            known.Add(key);
            _joinServerChoices.Add(new(
                server.Id,
                server.Name,
                $"{server.Host}:{server.Port}   •   SAVED",
                server.Host,
                server.Port,
                false));
        }

        foreach (var found in _lanDiscovery?.Snapshot() ?? [])
        {
            var key = LanDiscoveryListener.Key(found.Host, found.Beacon.GamePort);
            if (!known.Add(key)) continue;
            var players =
                $"{found.Beacon.PlayerCount}/{found.Beacon.MaximumClients}";
            _joinServerChoices.Add(new(
                $"lan:{key}:{found.Beacon.WorldId:N}",
                found.Beacon.DisplayName,
                $"{found.Host}:{found.Beacon.GamePort}   •   LAN   •   {players}",
                found.Host,
                found.Beacon.GamePort,
                true));
        }

        if (_joinServerList.SelectedId is { } selected &&
            _joinServerChoices.All(value => value.Id != selected))
            _joinServerList.SelectedId = null;
    }

    private void LayoutJoinServerList()
    {
        _joinServerList.Layout(
            JoinServerListBounds(),
            _joinServerChoices.Select(value => value.Id).ToArray(),
            rowHeight: 44,
            rowGap: 6,
            deleteWidth: 72,
            actionGap: 6);
    }

    private void EnsureLanDiscovery() =>
        _lanDiscovery ??= new LanDiscoveryListener();

    private void StopLanDiscovery()
    {
        _lanDiscovery?.Dispose();
        _lanDiscovery = null;
    }

    private sealed record JoinServerChoice(
        string Id,
        string Title,
        string Details,
        string Host,
        int Port,
        bool IsLan);

    private async Task HostMultiplayerWorldAsync()
    {
        if (_multiplayerBusy) return;
        if (_selectedPlayer is null)
        {
            _multiplayerStep = MultiplayerWizardStep.Character;
            _frontendError = "Choose a character first.";
            return;
        }

        RefreshHostedWorldChoices();
        var selected = SelectedHostedWorld();
        var createNew = selected is null || selected.IsNew;
        _multiplayerBusy = true;
        _frontendError = null;
        _multiplayerStatus = createNew
            ? "Creating hosted world…"
            : "Opening hosted world…";
        var islandStart = _multiplayerIslandStart;
        var seedText = _multiplayerSeedTextBox.Text.Trim();
        var resume = selected;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!long.TryParse(seedText, out var seed))
                    seed = Random.Shared.NextInt64();
                var saveRoot = HostedWorldsRoot();
                Directory.CreateDirectory(saveRoot);
                Guid worldId;
                bool useIslandStart;
                System.Numerics.Vector2 spawn;
                if (createNew || resume is null)
                {
                    worldId = Guid.NewGuid();
                    useIslandStart = islandStart;
                    spawn = BoatTravelRules.FindPlayableLandSpawn(seed);
                }
                else
                {
                    worldId = resume.WorldId;
                    seed = resume.Seed;
                    useIslandStart = resume.IslandStart;
                    spawn = resume.SpawnX is { } x && resume.SpawnY is { } y
                        ? new System.Numerics.Vector2(x, y)
                        : BoatTravelRules.FindPlayableLandSpawn(seed);
                }

                SaveHostedWorld(
                    saveRoot, worldId, seed, spawn, useIslandStart);
                await StopHostedServerAsync().ConfigureAwait(false);
                var endpoint = await StartHostedServerAsync(
                    worldId, seed, saveRoot, spawn, useIslandStart)
                    .ConfigureAwait(false);
                var lan = GuessLanAddress();
                _networkEvents.Enqueue(() =>
                {
                    RefreshHostedWorldChoices();
                    _hostedWorldList.SelectedId = worldId.ToString("N");
                    _multiplayerStatus =
                        $"Hosting on {lan}:{endpoint.Port}. Connecting…";
                    _multiplayerEndpointTextBox.SetText($"{lan}:{endpoint.Port}");
                    BeginJoin("127.0.0.1", endpoint.Port, worldId);
                    _multiplayerBusy = false;
                });
            }
            catch (Exception exception)
            {
                await StopHostedServerAsync().ConfigureAwait(false);
                _networkEvents.Enqueue(() =>
                {
                    _frontendError = $"Could not host: {exception.Message}";
                    _multiplayerStatus = null;
                    _multiplayerBusy = false;
                });
            }
        });
    }

    private void JoinMultiplayerWorld()
    {
        if (_multiplayerBusy) return;
        if (_selectedPlayer is null)
        {
            _multiplayerStep = MultiplayerWizardStep.Character;
            _frontendError = "Choose a character first.";
            return;
        }

        try
        {
            var selected = _joinServerChoices.FirstOrDefault(value =>
                value.Id == _joinServerList.SelectedId);
            var launch = selected is null
                ? NetworkLaunchOptions.Parse(
                    _multiplayerEndpointTextBox.Text,
                    _selectedPlayer.Name)
                : new NetworkLaunchOptions(
                    selected.Host,
                    selected.Port,
                    _selectedPlayer.Name,
                    Guid.Empty);
            _multiplayerEndpointTextBox.SetText($"{launch.Host}:{launch.Port}");
            _frontendError = null;
            _multiplayerStatus = $"Connecting to {launch.Host}:{launch.Port}…";
            BeginJoin(launch.Host, launch.Port, Guid.Empty);
        }
        catch (Exception exception)
        {
            _frontendError = exception.Message;
        }
    }

    private void BeginJoin(string host, int port, Guid worldId)
    {
        if (_selectedPlayer is null) return;
        host = NetworkLaunchOptions.NormalizeConnectHost(host);
        var localPlayerId = _selectedPlayer.Id;
        var session = _saves.LoadNetworkSession(localPlayerId);
        var reconnects = NetworkSessionReuse.CanReconnect(
            session, localPlayerId, host, port, worldId);
        _networkLaunch = new NetworkLaunchOptions(
            host,
            port,
            _selectedPlayer.Name,
            worldId,
            Guid.NewGuid(),
            reconnects ? session!.PlayerId : Guid.Empty,
            reconnects ? session!.ReconnectToken : "",
            _selectedPlayer.Gender,
            _selectedPlayer.TeamColor,
            localPlayerId);
        _networkConnectStarted = false;
        _networkWorldEntered = false;
        BeginNetworkConnection();
    }

    private static string SanitizeJoinEndpoint(string endpoint)
    {
        try
        {
            var launch = NetworkLaunchOptions.Parse(endpoint, "join");
            return $"{launch.Host}:{launch.Port}";
        }
        catch
        {
            return "127.0.0.1:38740";
        }
    }

    private async Task<IPEndPoint> StartHostedServerAsync(
        Guid worldId,
        long seed,
        string saveRoot,
        System.Numerics.Vector2 spawn,
        bool islandStart)
    {
        _ = spawn;
        var launch = ResolveDedicatedServerLaunch();
        Exception? last = null;
        for (var port = ServerOptions.DefaultPort;
             port < ServerOptions.DefaultPort + 10;
             port++)
        {
            var process = StartDedicatedServerProcess(
                launch, (ushort)port, worldId, seed, saveRoot, islandStart);
            try
            {
                var endpoint = await WaitForDedicatedServerListenAsync(
                    process, (ushort)port).ConfigureAwait(false);
                _hostedServerProcess = process;
                return endpoint;
            }
            catch (Exception exception)
            {
                last = exception;
                TerminateDedicatedServerProcess(process);
            }
        }

        throw last ?? new InvalidOperationException(
            "The dedicated world process did not bind a port.");
    }

    private async Task StopHostedServerAsync()
    {
        var process = _hostedServerProcess;
        _hostedServerProcess = null;
        if (process is null) return;
        await Task.Run(() => TerminateDedicatedServerProcess(process))
            .ConfigureAwait(false);
    }

    private readonly record struct DedicatedServerLaunch(
        string FileName,
        string ArgumentPrefix,
        string WorkingDirectory);

    private static DedicatedServerLaunch ResolveDedicatedServerLaunch()
    {
        var baseDir = AppContext.BaseDirectory;
        var windowsExe = Path.Combine(baseDir, "IslandRpg.Server.exe");
        if (OperatingSystem.IsWindows() && File.Exists(windowsExe))
            return new(windowsExe, "", baseDir);

        var unixHost = Path.Combine(baseDir, "IslandRpg.Server");
        if (!OperatingSystem.IsWindows() && File.Exists(unixHost))
            return new(unixHost, "", baseDir);

        var dll = Path.Combine(baseDir, "IslandRpg.Server.dll");
        if (File.Exists(dll))
            return new("dotnet", $"\"{dll}\" ", baseDir);

        throw new FileNotFoundException(
            "IslandRpg.Server was not found next to the game. Host starts a separate world process.");
    }

    private static Process StartDedicatedServerProcess(
        DedicatedServerLaunch launch,
        ushort port,
        Guid worldId,
        long seed,
        string saveRoot,
        bool islandStart)
    {
        var arguments =
            $"{launch.ArgumentPrefix}--listen 0.0.0.0:{port} " +
            $"--world-id {worldId:D} --world-seed {seed} " +
            $"--save-root \"{saveRoot}\" --max-clients 16";
        if (islandStart)
            arguments += " --island-start";

        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            Arguments = arguments,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(start);
        if (process is null)
            throw new InvalidOperationException(
                "The dedicated world process did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<IPEndPoint> WaitForDedicatedServerListenAsync(
        Process process,
        ushort port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    "The dedicated world process exited before it started listening.");
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                var finished = await Task.WhenAny(
                    connect, Task.Delay(200)).ConfigureAwait(false);
                if (finished == connect)
                {
                    await connect.ConfigureAwait(false);
                    if (client.Connected)
                        return new IPEndPoint(IPAddress.Loopback, port);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The dedicated world process did not start listening.");
    }

    private static void TerminateDedicatedServerProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(4000);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private HostedWorldChoice? SelectedHostedWorld()
    {
        var id = _hostedWorldList.SelectedId;
        return _hostedWorldChoices.FirstOrDefault(value => value.Id == id);
    }

    private void DeleteHostedWorld(Guid worldId)
    {
        var saveRoot = HostedWorldsRoot();
        var directory = Path.Combine(saveRoot, "Worlds", worldId.ToString("N"));
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var last = LoadHostedWorld(saveRoot);
        if (last?.WorldId == worldId)
        {
            var path = Path.Combine(saveRoot, "host.json");
            if (File.Exists(path)) File.Delete(path);
        }
        RefreshHostedWorldChoices();
        _hostedWorldList.SelectedId = NewHostedWorldId;
    }

    private sealed record HostedWorldChoice(
        string Id,
        Guid WorldId,
        long Seed,
        bool IslandStart,
        bool IsNew,
        float? SpawnX,
        float? SpawnY,
        DateTime UpdatedUtc)
    {
        public static HostedWorldChoice NewWorld { get; } = new(
            NewHostedWorldId, Guid.Empty, 0, false, true, null, null,
            DateTime.MinValue);

        public string Title => IsNew
            ? "NEW WORLD"
            : IslandStart ? "SHORE WORLD" : "OPEN WORLD";

        public string Details => IsNew
            ? "Fresh seed and shore-start setting"
            : $"SEED {Seed}   •   {UpdatedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    private sealed record HostedWorldRecord(
        Guid WorldId,
        long Seed,
        bool IslandStart,
        float? SpawnX = null,
        float? SpawnY = null);

    private string HostedWorldsRoot() =>
        Path.Combine(_saves.Root, "NetworkWorlds");

    private static ServerCheckpoint? TryLoadHostedCheckpoint(
        string saveRoot, Guid? worldId)
    {
        if (worldId is not { } id || id == Guid.Empty)
            return null;
        try
        {
            return new ServerCheckpointStore(saveRoot).Load(id)?.Checkpoint;
        }
        catch
        {
            return null;
        }
    }

    private static HostedWorldRecord? LoadHostedWorld(string saveRoot)
    {
        var path = Path.Combine(saveRoot, "host.json");
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HostedWorldRecord>(
                File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveHostedWorld(
        string saveRoot,
        Guid worldId,
        long seed,
        System.Numerics.Vector2 spawn,
        bool islandStart)
    {
        var path = Path.Combine(saveRoot, "host.json");
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                new HostedWorldRecord(
                    worldId,
                    seed,
                    islandStart,
                    spawn.X,
                    spawn.Y)));
    }

    private Vector4 MultiplayerChipBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + 172, panel.Z - 88, 30);
    }

    private Vector4 MultiplayerHostCardBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + 216, 300, 168);
    }

    private Vector4 MultiplayerJoinCardBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + panel.Z - 344, panel.Y + 216, 300, 168);
    }

    private Vector4 MultiplayerBackBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + panel.W - 56, 108, 40);
    }

    private Vector4 HostBoxBounds()
    {
        var panel = MultiplayerPanel();
        return new(
            panel.X + 44,
            panel.Y + 212,
            panel.Z - 88,
            panel.W - 280);
    }

    private Vector4 HostedWorldListBounds()
    {
        var host = HostBoxBounds();
        return new(host.X + 18, host.Y + 40, host.Z - 36, host.W - 148);
    }

    private Vector4 HostSeedBounds()
    {
        var host = HostBoxBounds();
        return new(host.X + 18, host.Y + host.W - 92, 280, 32);
    }

    private Vector4 HostRandomSeedBounds()
    {
        var seed = HostSeedBounds();
        return new(seed.X + 292, seed.Y, 92, 32);
    }

    private Vector4 HostIslandBounds()
    {
        var seed = HostSeedBounds();
        return new(seed.X + 396, seed.Y, 188, 32);
    }

    private Vector4 HostStartButtonBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + panel.Z - 232, panel.Y + panel.W - 56, 188, 40);
    }

    private Vector4 JoinBoxBounds()
    {
        var panel = MultiplayerPanel();
        return new(
            panel.X + 44,
            panel.Y + 212,
            panel.Z - 88,
            panel.W - 410);
    }

    private Vector4 JoinServerListBounds()
    {
        var join = JoinBoxBounds();
        return new(join.X + 18, join.Y + 40, join.Z - 36, join.W - 52);
    }

    private Vector4 JoinEndpointBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + panel.W - 148, 430, 32);
    }

    private Vector4 JoinServerNameBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + panel.W - 196, 280, 32);
    }

    private Vector4 JoinAddServerBounds()
    {
        var endpoint = JoinEndpointBounds();
        return new(endpoint.X + endpoint.Z + 12, endpoint.Y, 132, 32);
    }

    private Vector4 JoinSaveServerBounds()
    {
        var name = JoinServerNameBounds();
        return new(name.X + 292, name.Y, 88, 32);
    }

    private Vector4 JoinCancelEditBounds()
    {
        var name = JoinServerNameBounds();
        return new(name.X + 388, name.Y, 88, 32);
    }

    private Vector4 JoinButtonBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + panel.Z - 216, panel.Y + panel.W - 56, 172, 40);
    }

    private static string GuessLanAddress()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is
                        NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily ==
                            AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                        return address.Address.ToString();
                }
            }
        }
        catch
        {
        }

        return "127.0.0.1";
    }
}
