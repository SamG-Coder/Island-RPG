using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void OpenDeveloperMap()
    {
        if (!_settingsMenu.DeveloperModeEnabled || _player is null)
            return;
        _developerMap.Open();
        _pauseMenu.SetPaused(false);
        _atlasOpen = true;
        StartAtlasAtCamera();
    }

    private void CloseDeveloperMap()
    {
        _developerMap.Close();
        CloseWorldAtlasSession();
    }

    private void TeleportFromDeveloperMap(Vector2 pointer)
    {
        if (!_developerMap.IsOpen || _player is null)
            return;
        var destination = DeveloperMapWindow.ResolveDestination(
            pointer,
            new(ReferenceWidth * .5f, ReferenceHeight * .5f),
            _atlasCenterIso,
            AtlasPixelsPerTile(),
            _worldSeed,
            _player.Position,
            _activeWorldLevel);

        CancelWorldLevelWork(clearMinimap: true);
        _activeTreeId = null;
        _activeTreeStickGatherId = null;
        _activeGroundPickupId = null;
        _activeGroundDrop = null;
        _player.TeleportTo(destination);

        _developerMap.Close();
        FollowPlayer();
        StreamWorld();
        SaveActivePlayerState();
        _chatUi.AddMessage(
            $"Teleported to {destination.X:0}, {destination.Y:0}.",
            ChatMessageStyle.Action);
    }

    private void RenderDeveloperMapOverlay()
    {
        var densityLayer =
            _activeWorldLevel == (int)WorldLevel.Overworld &&
            _developerMap.Layer == WorldAtlasLayer.TreeDensity;
        var title = new Vector4(
            ReferenceWidth * .5f - 310, 18, 620,
            densityLayer ? 102 : 82);
        DrawUiColor(title, new(.035f, .031f, .023f, .94f));
        DrawPanelOutline(title, 2, new(.48f, .38f, .18f, 1));
        DrawCenteredUiText(
            $"DEVELOPER MAP - " +
            WorldLevelMapPresentation.LevelName(_activeWorldLevel),
            new(title.X, title.Y + 5, title.Z, 25),
            new(232, 217, 166, 255));
        DrawCenteredUiText(
            densityLayer
                ? "TREE DENSITY • T: terrain • drag/pan • wheel/zoom • double-click/teleport"
                : "T: tree density • drag/pan • wheel/zoom • double-click/teleport • Esc/close",
            new(title.X + 8, title.Y + 32, title.Z - 16, 20),
            new(183, 173, 143, 255));
        if (densityLayer)
            DrawCenteredUiText(
                "Dark: none  •  Green: moderate  •  Yellow: densest",
                new(title.X + 8, title.Y + 54, title.Z - 16, 18),
                new(194, 185, 151, 255));
        var cacheMiB = _atlasTextures.Bytes /
                       (1024d * 1024d);
        DrawUiText(
            $"Tiles {_atlasDone}/{_atlasTotal}  •  " +
            $"jobs {_atlasGeneration.ActiveCount}  •  " +
            $"active {_atlasGeneration.OldestActiveMilliseconds:0} ms  •  " +
            $"cache {cacheMiB:0.0} MiB  •  " +
            $"last {_lastAtlasGenerationMilliseconds:0} ms",
            new(title.X + 10, title.Y + title.W - 18),
            new(155, 148, 124, 255));
    }
}
