using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly WorldHoverProbeGate _worldHoverProbeGate = new();

    private void PrepareGameCursors()
    {
        if (_useTestAssets)
        {
            Cursor = MouseCursor.Default;
            CursorState = CursorState.Normal;
            return;
        }

        var path = Path.Combine(
            _install, "resources", "_common", "drs", "interface", "51000.slp");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The installed interface assets do not contain the AoE cursor sheet.", path);

        var palette = JascPalette.Load(Age2PaletteResolver.Resolve(_install, path).Path);
        var cursorSheet = SlpDecoder.Decode(path, palette);
        if (cursorSheet.Frames.Count <= 17)
            throw new InvalidDataException(
                "The installed AoE cursor sheet does not contain the required game cursors.");

        _defaultNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.Default]);
        _attackNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.Attack]);
        _pickupNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.MineAndPickUp]);
        _mineNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.MineAndPickUp]);
        _climbDownNativeCursor =
            CreateNativeCursor(
                cursorSheet.Frames[GameCursorFrames.ClimbDown]);
        _climbUpNativeCursor =
            CreateNativeCursor(
                cursorSheet.Frames[GameCursorFrames.ClimbUp]);
        _exitBoatNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.ExitBoat]);
        _enterBoatNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.EnterBoat]);
        _digNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.Dig]);
        _dropNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.Drop]);
        _openStorageNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.OpenStorage]);
        _craftingStationNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.CraftingStation]);
        _cutNativeCursor = CreateNativeCursor(
            cursorSheet.Frames[GameCursorFrames.Cut]);
        Cursor = _defaultNativeCursor;
        CursorState = CursorState.Normal;
    }

    private void UseDefaultGameCursor()
    {
        _gameCursorKind = GameCursorKind.Default;
        Cursor = _defaultNativeCursor ?? MouseCursor.Default;
    }

    private static MouseCursor CreateNativeCursor(SpriteFrame frame)
    {
        var pixels = (byte[])frame.Rgba.Clone();
        // GLFW's Windows backend expects premultiplied RGB for translucent
        // custom-cursor pixels.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }

        return new MouseCursor(
            Math.Clamp(frame.HotspotX, 0, frame.Width - 1),
            Math.Clamp(frame.HotspotY, 0, frame.Height - 1),
            frame.Width,
            frame.Height,
            pixels);
    }

    private void UpdateNativeCursor()
    {
        if (_defaultNativeCursor is null || _cutNativeCursor is null) return;

        var pointerBlocked = IsPointerOverGameUi(MouseState.Position);
        if (!_worldHoverProbeGate.ShouldProbe(
                SceneMousePosition(),
                _camera,
                _zoom,
                pointerBlocked,
                _clock))
            return;
        var next = GameCursorKind.Default;
        MouseCursor cursor = _defaultNativeCursor;
        if (_digTargetingSlot >= 0 &&
            !pointerBlocked &&
            _digNativeCursor is not null)
        {
            next = GameCursorKind.Dig;
            cursor = _digNativeCursor;
        }
        else if ((IsWorldDropDragOutsideInventory() ||
             IsPlaceablePlacementActiveOverWorld()) &&
            _dropNativeCursor is not null)
        {
            next = GameCursorKind.DropItem;
            cursor = _dropNativeCursor;
        }
        else if (!pointerBlocked)
        {
            if (TryGetVillagerUnderMouse(
                    SceneMousePosition(), out _))
            {
                var inventory = _activePlayer?.Inventory ?? [];
                var givingItem =
                    (uint)_activeInventorySlot <
                    (uint)inventory.Length &&
                    inventory[_activeInventorySlot] is not null;
                if (givingItem && _dropNativeCursor is not null)
                {
                    next = GameCursorKind.DropItem;
                    cursor = _dropNativeCursor;
                }
                else if (_attackNativeCursor is not null)
                {
                    next = GameCursorKind.Attack;
                    cursor = _attackNativeCursor;
                }
            }
            else if (_fishingBoatDisembarkTargeting &&
                _exitBoatNativeCursor is not null)
            {
                next = GameCursorKind.ExitBoat;
                cursor = _exitBoatNativeCursor;
            }
            else if (!_fishingBoatBoarded &&
                     FishingBoatHitTest(SceneMousePosition()) &&
                     _enterBoatNativeCursor is not null)
            {
                next = GameCursorKind.EnterBoat;
                cursor = _enterBoatNativeCursor;
            }
            else if (TryGetGroundObjectUnderMouse(
                    SceneMousePosition(), out var groundObject, out _))
            {
                if (IsAttackableCombatTarget(groundObject) &&
                    _attackNativeCursor is not null)
                {
                    next = GameCursorKind.Attack;
                    cursor = _attackNativeCursor;
                }
                else if (CaveEntranceService.IsEntrance(groundObject) &&
                    _activeWorldLevel == (int)WorldLevel.Overworld &&
                    _climbDownNativeCursor is not null)
                {
                    next = GameCursorKind.ClimbDown;
                    cursor = _climbDownNativeCursor;
                }
                else if (CaveEntranceService.IsEntrance(groundObject) &&
                         _activeWorldLevel ==
                         (int)WorldLevel.Underground &&
                         _climbUpNativeCursor is not null)
                {
                    next = GameCursorKind.ClimbUp;
                    cursor = _climbUpNativeCursor;
                }
                else if (StorageContainerService.IsStorage(
                             groundObject.ItemId) &&
                         _openStorageNativeCursor is not null)
                {
                    next = GameCursorKind.OpenStorage;
                    cursor = _openStorageNativeCursor;
                }
                else if (CraftingStationService.IsStation(
                             groundObject.ItemId) &&
                         _craftingStationNativeCursor is not null)
                {
                    next = GameCursorKind.CraftingStation;
                    cursor = _craftingStationNativeCursor;
                }
                else if (!PlaceableObjectCatalog.IsPlaceable(
                             groundObject.ItemId) &&
                         _pickupNativeCursor is not null)
                {
                    next = GameCursorKind.PickUpItem;
                    cursor = _pickupNativeCursor;
                }
            }
            else if (TryGetMiningNodeUnderMouse(
                         SceneMousePosition(), out _, out _) &&
                     _mineNativeCursor is not null)
            {
                next = GameCursorKind.Mine;
                cursor = _mineNativeCursor;
            }
            else if (TryGetFishUnderMouse(
                         SceneMousePosition(), out _) &&
                     _pickupNativeCursor is not null)
            {
                next = GameCursorKind.PickUpItem;
                cursor = _pickupNativeCursor;
            }
            else if (TryGetGatherableVegetationUnderMouse(
                         SceneMousePosition(), out _, out _) &&
                     _pickupNativeCursor is not null)
            {
                next = GameCursorKind.PickUpItem;
                cursor = _pickupNativeCursor;
            }
            else if (TryGetTreeUnderMouse(SceneMousePosition(), out _))
            {
                next = GameCursorKind.CutTree;
                cursor = _cutNativeCursor;
            }
        }

        if (next == _gameCursorKind) return;
        _gameCursorKind = next;
        Cursor = cursor;
    }

    private static bool IsAttackableCombatTarget(
        WorldGroundObject groundObject) =>
        groundObject.ItemId == ItemIds.TrainingDummy;
}
