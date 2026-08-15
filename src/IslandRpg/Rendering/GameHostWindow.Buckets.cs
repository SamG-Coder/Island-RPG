using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private int _bucketFillSlot = -1;
    private int _activeBucketFillSlot = -1;
    private Vector2 _activeBucketFillTarget;

    private void BeginBucketFillTargeting(int slot)
    {
        if (_activePlayer is null) return;
        var inventory = _activePlayer.Inventory ?? [];
        if ((uint)slot >= (uint)inventory.Length ||
            inventory[slot] is not { } itemId ||
            !BucketService.IsEmpty(itemId))
            return;
        _bucketFillSlot = slot;
        _gameCursorKind = GameCursorKind.Dig;
        Cursor = _digNativeCursor ?? _defaultNativeCursor ??
            OpenTK.Windowing.Common.Input.MouseCursor.Default;
        _chatUi.AddMessage(
            "Choose a water source to fill the bucket.",
            ChatMessageStyle.Action);
    }

    private void CancelBucketFillTargeting()
    {
        _bucketFillSlot = -1;
        UseDefaultGameCursor();
    }

    private bool TryTargetBucketFill(Vector2 target)
    {
        if (_bucketFillSlot < 0) return false;
        var slot = _bucketFillSlot;
        _bucketFillSlot = -1;
        UseDefaultGameCursor();
        var tile = new Vector2(
            MathF.Floor(target.X) + .5f,
            MathF.Floor(target.Y) + .5f);
        var kind = BucketService.ClassifyAt(
            _worldSeed,
            _activeWorldLevel,
            (int)MathF.Floor(tile.X),
            (int)MathF.Floor(tile.Y));
        if (kind == BucketWaterKind.None)
        {
            ReportBlockedAction(
                "bucket-not-water",
                "There is no water there to fill the bucket.");
            return true;
        }

        QueueBucketFill(tile, slot);
        return true;
    }

    private void QueueBucketFill(Vector2 target, int slot)
    {
        if (_activePlayer?.Inventory?[slot] is not { } itemId ||
            !BucketService.IsEmpty(itemId))
            return;
        _worldActions.QueuePath(
            target,
            WorldActionReach.FillBucket,
            WorldActionType.FillBucket,
            inventorySlot: slot,
            itemId: itemId,
            clearTreeActions: true);
        if (IsNetworkWorld)
            SendNetworkWalkCommand(
                WorldActionReach.StandOff(
                    NetworkActionPosition,
                    target,
                    WorldActionReach.FillBucket));
    }

    internal void BeginBucketFill(Vector2 target, int slot)
    {
        if (_player is null || _activePlayer is null) return;
        if (_activePlayer.Inventory?[slot] is not { } itemId ||
            !BucketService.IsEmpty(itemId))
        {
            ReportBlockedAction(
                "bucket-missing",
                "You need an empty bucket to fill.");
            return;
        }

        var kind = BucketService.ClassifyAt(
            _worldSeed,
            _activeWorldLevel,
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        if (kind == BucketWaterKind.None)
        {
            ReportBlockedAction(
                "bucket-not-water",
                "There is no water there to fill the bucket.");
            _player.Stop();
            return;
        }

        _activeBucketFillSlot = slot;
        _activeBucketFillTarget = new Vector2(
            MathF.Floor(target.X) + .5f,
            MathF.Floor(target.Y) + .5f);
        if (IsNetworkWorld)
        {
            SendNetworkPresentSkill(EntityAction.Gather);
            _networkWorldActionCommitAt = _clock + GroundItemActionSeconds;
        }
        _player.GatherAt(_activeBucketFillTarget);
        if (IsNetworkWorld)
            _player.RestartActionTime();
    }

    internal void UpdateBucketFill()
    {
        if (_player is null || _activeBucketFillSlot < 0) return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeBucketFillSlot = -1;
            return;
        }

        if (!NetworkResourceWindupReady(
                _player.ActionTime, GroundItemActionSeconds, _clock,
                _networkWorldActionCommitAt) &&
            _entityAnimations.ContainsKey(
                (_player.Gender, EntityAction.Gather)))
            return;

        var slot = _activeBucketFillSlot;
        var target = _activeBucketFillTarget;
        _activeBucketFillSlot = -1;
        CommitBucketFill(slot, target);
        _player.Stop();
    }

    private void CommitBucketFill(int slot, Vector2 target)
    {
        if (_activePlayer is null) return;
        if (IsNetworkWorld)
        {
            SendNetworkAction(new FillBucketAction(
                slot,
                target.X,
                target.Y,
                checked((short)_activeWorldLevel)));
            return;
        }

        var inventory = ActivePlayerInventory();
        if (inventory[slot]?.ItemId is not { } itemId ||
            !BucketService.IsEmpty(itemId))
            return;
        var kind = BucketService.ClassifyAt(
            _worldSeed,
            _activeWorldLevel,
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y));
        if (kind == BucketWaterKind.None)
        {
            ReportBlockedAction(
                "bucket-not-water",
                "There is no water there to fill the bucket.");
            return;
        }

        if (!inventory.TryReplace(slot, BucketService.FilledItemId(kind)))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You fill the bucket with {BucketService.DisplayName(kind)}.",
            ChatMessageStyle.Action);
    }

    private void EmptyBucket(int slot)
    {
        if (_activePlayer is null) return;
        if (IsNetworkWorld)
        {
            SendNetworkAction(new EmptyBucketAction(slot));
            return;
        }

        var inventory = ActivePlayerInventory();
        if (inventory[slot]?.ItemId is not { } itemId ||
            !BucketService.IsFilled(itemId))
            return;
        if (!inventory.TryReplace(slot, ItemIds.Bucket))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            "You empty the bucket.",
            ChatMessageStyle.Action);
    }
}
