using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Guid? _activePlantedTreeId;
    private double _nextPlantedTreeStrikeAt;

    private int CountLivingPlantedTrees(string ownerId)
    {
        var count = 0;
        foreach (var gpu in _worldChunks.Values)
        foreach (var value in gpu.Chunk.GroundObjects)
        {
            if (IsNetworkWorld &&
                _networkKnownWorldObjectIds.Contains(value.Id))
                continue;
            if (PlantedTreeService.IsLiving(value) &&
                string.Equals(value.OwnerId, ownerId, StringComparison.Ordinal))
                count++;
        }
        if (!IsNetworkWorld) return count;
        foreach (var value in _networkWorldObjects.Values)
            if (PlantedTreeService.IsLiving(value) &&
                string.Equals(
                    PlantedTreeService.PlanterDisplayName(value),
                    _activePlayer?.Name,
                    StringComparison.Ordinal))
                count++;
        return count;
    }

    private void QueuePlantedTreeChop(WorldGroundObject tree)
    {
        if (!PlantedTreeService.IsLiving(tree)) return;
        var target = new Vector2(tree.X, tree.Y);
        _worldActions.QueuePath(
            target,
            TreeInteractionDistance(PlantedTreeService.TreeType(tree)),
            WorldActionType.CutPlantedTree,
            groundObjectId: tree.Id,
            clearTreeActions: true);
        if (IsNetworkWorld)
            SendNetworkWalkCommand(
                WorldActionReach.StandOff(
                    NetworkActionPosition,
                    target,
                    TreeInteractionDistance(PlantedTreeService.TreeType(tree))));
    }

    internal void TryStartPlantedTreeCutting(Guid treeId)
    {
        if (_player is null || _activePlayer is null) return;
        var tree = FindGroundObject(treeId);
        if (tree is null || !PlantedTreeService.IsLiving(tree))
        {
            ReportBlockedAction(
                "planted-tree-missing",
                "That planted tree is no longer there.");
            return;
        }

        if (EntityInteractionService.TryAutoSharpenStoneTool(
                _activePlayer.Inventory,
                ItemIds.BluntStoneAxe,
                out var sharpenedInventory))
        {
            _activePlayer = _activePlayer with
            {
                Inventory = sharpenedInventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                "You use small rocks to sharpen your blunt stone axe.",
                ChatMessageStyle.Action);
        }

        if (!PlayerInventory.HasAxe(_activePlayer.Inventory))
        {
            var hasBluntAxe =
                PlayerInventory.HasAnyAxe(_activePlayer.Inventory);
            ReportBlockedAction(
                hasBluntAxe ? "chop-with-blunt-axe" : "chop-without-axe",
                hasBluntAxe
                    ? "Your axe is too blunt. Use small rocks on it to sharpen it."
                    : "You need an axe to chop down this tree.");
            _player.Stop();
            return;
        }

        var logItem = PlantedTreeService.LogItemId(
            PlantedTreeService.TreeType(tree));
        if (!ActivePlayerInventory().CanAdd(logItem))
        {
            ReportBlockedAction(
                "chop-inventory-full",
                "Your inventory is full. You cannot begin woodcutting.");
            _player.Stop();
            return;
        }

        _activePlantedTreeId = tree.Id;
        _activeTreeId = tree.Id;
        _lastTreeStrike = 0;
        _nextPlantedTreeStrikeAt = 0;
        if (IsNetworkWorld)
            SendNetworkPresentSkill(EntityAction.Work);
        _player.WorkAt(new Vector2(tree.X, tree.Y));
        _chatUi.AddMessage(
            $"You begin cutting the {PlantedTreeService.DisplayName(PlantedTreeService.TreeType(tree))}.",
            ChatMessageStyle.Action);
    }

    internal void UpdateActivePlantedTreeCutting()
    {
        if (_player is null || _activePlantedTreeId is null ||
            _player.Action != EntityAction.Work)
            return;
        var tree = FindGroundObject(_activePlantedTreeId.Value);
        if (tree is null || !PlantedTreeService.IsLiving(tree))
        {
            _activePlantedTreeId = null;
            _activeTreeId = null;
            if (IsNetworkWorld)
                SendNetworkPresentSkill(EntityAction.Idle);
            _player.Stop();
            return;
        }

        if (IsNetworkWorld)
        {
            UpdateNetworkPlantedTreeStrike(tree);
            return;
        }

        if (!_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Work), out var animation))
            return;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(
            (int)MathF.Round((framesPerAngle - 1) * .43f),
            0,
            framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastTreeStrike) return;
        _lastTreeStrike = strike;
        ApplyLocalPlantedTreeStrike(tree);
    }

    private void UpdateNetworkPlantedTreeStrike(WorldGroundObject tree)
    {
        if (_clock < _nextPlantedTreeStrikeAt) return;
        if (!TryFindNetworkAxeSlot(out var toolSlot) ||
            !TryNetworkWorldObjectReference(tree.Id, out var reference))
        {
            _activePlantedTreeId = null;
            _activeTreeId = null;
            SendNetworkPresentSkill(EntityAction.Idle);
            _player?.Stop();
            return;
        }

        _nextPlantedTreeStrikeAt =
            _clock + PlantedTreeService.StrikeCadenceSeconds;
        SendNetworkAction(new StrikePlantedTreeAction(reference, toolSlot));
    }

    private void ApplyLocalPlantedTreeStrike(WorldGroundObject tree)
    {
        if (_player is null || _activePlayer is null) return;
        var location = FindGroundObjectLocation(tree.Id);
        if (location is null) return;

        if (EntityInteractionService.TryAutoSharpenStoneTool(
                _activePlayer.Inventory,
                ItemIds.BluntStoneAxe,
                out var automaticallySharpenedInventory))
        {
            _activePlayer = _activePlayer with
            {
                Inventory = automaticallySharpenedInventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            _chatUi.AddMessage(
                "You use small rocks to sharpen your blunt stone axe.",
                ChatMessageStyle.Action);
        }

        var axe = PlayerInventory.BestAxe(_activePlayer.Inventory);
        if (axe is null)
        {
            ReportBlockedAction(
                "chop-without-axe",
                "You need an axe to chop down this tree.");
            _activePlantedTreeId = null;
            _activeTreeId = null;
            _player.Stop();
            return;
        }

        if (axe.Id == ItemIds.StoneAxe &&
            EntityInteractionService.TryBluntStoneTool(
                _activePlayer.Inventory,
                axe.Id,
                Random.Shared.NextSingle(),
                out var bluntedInventory))
        {
            var sharpened =
                EntityInteractionService.TryAutoSharpenStoneTool(
                    bluntedInventory,
                    ItemIds.BluntStoneAxe,
                    out var resharpenedInventory);
            _activePlayer = _activePlayer with
            {
                Inventory = sharpened
                    ? resharpenedInventory
                    : bluntedInventory,
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            if (sharpened)
            {
                _chatUi.AddMessage(
                    "Your stone axe becomes blunt, so you sharpen it " +
                    "with small rocks and keep chopping.",
                    ChatMessageStyle.Action);
            }
            else
            {
                _chatUi.AddMessage(
                    "Your stone axe becomes blunt. Use small rocks on " +
                    "it to sharpen it.",
                    ChatMessageStyle.Warning);
                AddBluntToolMonologue(ItemIds.StoneAxe);
                _activePlantedTreeId = null;
                _activeTreeId = null;
                _player.Stop();
                return;
            }
        }

        var treeType = PlantedTreeService.TreeType(tree);
        var strikeResult = EntityInteractionService.StrikeResource(new(
            EntityResourceAction.Woodcut,
            _activePlayer.WoodcuttingExperience,
            tree.Health,
            tree.MaxHealth,
            axe.WoodcuttingPower,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle()));
        PlaySoundCue("woodcutting-impact");
        if (!strikeResult.Hit)
        {
            ShowEntityImpact(TreeFeedbackKey(tree.Id), 0, false);
            _chatUi.AddMessage(
                $"Woodcutting {strikeResult.Experience.Level}: you miss the tree.",
                ChatMessageStyle.Miss);
            return;
        }

        var next = PlantedTreeService.ApplyStrike(
            tree, strikeResult.Health, _worldGameSeconds);
        ReplaceGroundObject(location.Value.Chunk, tree, next);
        ShowEntityImpact(TreeFeedbackKey(tree.Id), strikeResult.Damage, true);
        _chatUi.AddMessage(
            $"You hit the {PlantedTreeService.DisplayName(treeType)} for {strikeResult.Damage} damage " +
            $"({next.Health}/{next.MaxHealth}).",
            ChatMessageStyle.Damage);
        AwardWoodcuttingExperience(strikeResult.Experience.Gained);

        var compacted = PlantedTreeService.IsCompacted(tree, _worldGameSeconds);
        if (next.Health > 0 &&
            compacted &&
            WoodcuttingSkill.GrantsSwingLog(
                strikeResult.Experience.Level,
                Random.Shared.NextSingle()))
        {
            AddWoodcuttingLog(treeType);
            _chatUi.AddMessage(
                "You cut away a usable piece of wood.",
                ChatMessageStyle.Action);
        }

        if (next.Health > 0)
            return;

        if (compacted)
            AddWoodcuttingLog(
                treeType,
                WoodcuttingSkill.FellingLogCount(tree.MaxHealth));
        _chatUi.AddMessage(
            $"The {PlantedTreeService.DisplayName(treeType)} falls.",
            ChatMessageStyle.Action);
        _activePlantedTreeId = null;
        _activeTreeId = null;
        _player.Stop();
    }

    private void ReplaceGroundObject(
        WorldChunk chunk,
        WorldGroundObject previous,
        WorldGroundObject next)
    {
        var index = chunk.GroundObjects.FindIndex(value => value.Id == previous.Id);
        if (index < 0) return;
        chunk.GroundObjects[index] = next;
        QueueChunkSave(chunk);
    }

    private void UpdateExpiredPlantedTrees()
    {
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            var changed = false;
            for (var index = gpu.Chunk.GroundObjects.Count - 1;
                 index >= 0;
                 index--)
            {
                var current = gpu.Chunk.GroundObjects[index];
                if (!PlantedTreeService.IsExpired(current, _worldGameSeconds))
                    continue;
                gpu.Chunk.GroundObjects.RemoveAt(index);
                changed = true;
                if (_activePlantedTreeId == current.Id)
                {
                    _activePlantedTreeId = null;
                    _activeTreeId = null;
                }
            }

            if (changed)
                QueueChunkSave(gpu.Chunk);
        }
    }

    private void RenderPlantedTreeOverlays(Vector4 scene)
    {
        foreach (var source in CollectVisibleGroundObjects(
                     [.. _worldChunks.Values.Where(IsChunkVisible)]))
        {
            var tree = source.Object;
            if (!PlantedTreeService.IsPlantedTree(tree))
                continue;
            if (!TryGroundObjectVisual(
                    tree, out var frame, out _, out _, out _))
                continue;
            var scale = PlantedTreeService.GrowthScale(
                tree, _worldGameSeconds);
            var opacity = PlantedTreeService.FadeOpacity(
                tree, _worldGameSeconds);
            if (opacity <= .01f)
                continue;
            var world = GroundObjectWorld(tree);
            var bounds = SpriteBounds(frame, world, renderScale: scale);
            DrawEntityFeedback(
                scene, bounds,
                tree.MaxHealth <= 0
                    ? 0
                    : tree.Health / (float)tree.MaxHealth,
                TreeFeedbackKey(tree.Id),
                forceHealth: true);
            DrawPlantedTreeTitle(scene, bounds, PlantedTreeService.Title(tree));
        }
    }

    private void DrawPlantedTreeTitle(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) bounds,
        string title)
    {
        var scale = scene.Z / ReferenceWidth;
        var width = Math.Max(MeasureUiText(title) + 10, 48);
        var label = new Vector4(
            scene.X + ((bounds.Left + bounds.Right) * .5f - width * .5f) *
            scale,
            scene.Y + (bounds.Top - 24) * scale,
            width * scale,
            Math.Max(12, 14 * scale));
        if (label.X + label.Z < scene.X || label.X > scene.X + scene.Z ||
            label.Y + label.W < scene.Y || label.Y > scene.Y + scene.W)
            return;
        DrawUiColor(label, new(.04f, .032f, .022f, .78f));
        DrawUiText(
            title,
            CenteredTextPosition(title, label),
            new FSColor(236, 224, 176, 255));
    }
}
