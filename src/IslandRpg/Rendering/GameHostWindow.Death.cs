using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void UpdateDeathOverlay()
    {
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        var clicked = leftDown && !_deathLeftWasDown;
        _deathLeftWasDown = leftDown;
        if (clicked && DeathRespawnButton().Contains(MouseState.Position))
            RespawnPlayer();
    }

    private void RespawnPlayer(bool force = false)
    {
        if ((!_playerDefeated && !force) || _activePlayer is null ||
            _activeWorld is null || _player is null)
            return;

        CancelMeleeCombat();
        CancelWorldLevelWork(clearMinimap: true);
        var spawn = FindPlayableSpawn();
        var maximumHealth = AdventureService.MaximumHealth(
            _activePlayer.AdventureExperience);
        var recovery = PlayerDeathService.Recover(maximumHealth);
        _activePlayer = _activePlayer with
        {
            Health = recovery.Health,
            Hunger = recovery.Hunger,
            WellFedSeconds = recovery.WellFedSeconds,
            UpdatedUtc = DateTime.UtcNow
        };
        _activeWorldLevel = (int)WorldLevel.Overworld;
        _caveEntranceLightWorld = null;
        _starvationElapsed = 0;
        _playerDefeated = false;
        _modalScreen.Close(ModalScreenKind.Death);
        _player.TeleportTo(spawn);
        _gameLeftWasDown =
            MouseState.IsButtonDown(MouseButton.Left);
        _gameRightWasDown =
            MouseState.IsButtonDown(MouseButton.Right);
        FollowPlayer();
        StreamWorld();

        _saves.SavePlayer(_activePlayer);
        _saves.SaveWorldPlayer(
            _activeWorld.Id,
            new(
                _activePlayer.Id,
                spawn.X,
                spawn.Y,
                DateTime.UtcNow,
                _activeWorldLevel));
        _chatUi.AddMessage(
            "You awaken at a safe place, weakened but carrying your belongings.",
            ChatMessageStyle.Action);
    }

    private void RenderDeathOverlay()
    {
        var panel = DeathPanel();
        DrawUiColor(panel, new(.17f, .018f, .014f, .985f));
        DrawPanelOutline(panel, 0, new(.035f, .008f, .006f, 1));
        DrawPanelOutline(panel, 2, new(.48f, .075f, .045f, 1));
        DrawPanelOutline(panel, 5, new(.15f, .018f, .012f, 1));
        DrawCenteredUiText(
            "YOU HAVE DIED",
            new(panel.X, panel.Y + 38, panel.Z, 46),
            new FSColor(255, 184, 155, 255));
        DrawSmallCenteredUiText(
            _deathMessage,
            new(panel.X + 34, panel.Y + 105, panel.Z - 68, 48),
            new FSColor(221, 190, 171, 255));
        DrawSmallCenteredUiText(
            "Your belongings remain with you.",
            new(panel.X + 34, panel.Y + 164, panel.Z - 68, 30),
            new FSColor(170, 145, 131, 255));
        DrawMenuButton(
            DeathRespawnButton(),
            "Respawn",
            new Vector3(.68f, .08f, .045f));
    }

    private void RenderDeathMarkers()
    {
        for (var index = 0; index < _playerDeaths.Count; index++)
        {
            if (_playerDefeated && index == 0) continue;
            var marker = _playerDeaths[index];
            if (marker.WorldLevel != _activeWorldLevel ||
                !_skeletonAnimations.TryGetValue(
                    marker.Gender, out var animation) ||
                animation.Graphic.Sprite.Frames.Count == 0)
                continue;

            var frameIndex = animation.Graphic.Sprite.Frames.Count - 1;
            var terrain = SamplePlayerTerrain(
                marker.PositionX, marker.PositionY);
            var world = IsometricTerrainProjection.Project(
                marker.PositionX,
                marker.PositionY,
                terrain.Height);
            DrawSprite(
                animation.Graphic.Sprite.Frames[frameIndex],
                animation.Textures[frameIndex],
                world);
        }
    }

    private Vector4 DeathPanel() => FrontendPanel(400, 330);

    private Vector4 DeathRespawnButton()
    {
        var panel = DeathPanel();
        return new(
            panel.X + 48,
            panel.Y + panel.W - 82,
            panel.Z - 96,
            48);
    }
}
