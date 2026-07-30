using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly ContextMenuControlState _miningContext = new();
    private string? _miningContextKey;
    private Vector2 _miningContextWalkTarget;
    private string? _activeMiningKey;
    private int _lastMiningStrike;

    private void InitializeMining() =>
        _miningContext.Selected += HandleMiningContextSelection;

    private bool TryGetMiningNodeUnderMouse(
        Vector2 mouse, out WorldVegetation node, out string stableKey)
    {
        node = null!;
        stableKey = "";
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            for (var index = gpu.VegetationRenderItems.Length - 1;
                 index >= 0; index--)
            {
                var cached = gpu.VegetationRenderItems[index];
                if (cached.VegetationIndex < 0 ||
                    !MiningNodeCatalog.TryGet(
                        gpu.Chunk.Vegetation[cached.VegetationIndex], out _) ||
                    !_treeAtlas.TryGetValue(cached.AtlasKey, out var entry))
                    continue;
                var bounds = SpriteBounds(entry.Frame, cached.World);
                var scale = Math.Max(SpritePixelScale(), .001f);
                if (!SpriteHitTesting.Contains(
                        entry.Frame, bounds, mouse, scale,
                        SpriteHitTesting.SizeAwareTolerance(entry.Frame)) ||
                    !WorldHoverSelection.Prefer(cached.World.Y, ref selectedDepth))
                    continue;
                node = gpu.Chunk.Vegetation[cached.VegetationIndex];
                stableKey = cached.StableKey;
            }
        }
        return node is not null;
    }

    private void OpenMiningContext(
        WorldVegetation node, string stableKey, Vector2 walkTarget)
    {
        if (!MiningNodeCatalog.TryGet(node, out _)) return;
        _miningContextKey = stableKey;
        _miningContextWalkTarget = walkTarget;
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _miningContext.Open(
            MouseState.Position,
            ["Mine", "Walk Here", "Examine"],
            SceneClientBounds(), 174);
    }

    private void HandleMiningContextSelection(int option)
    {
        var key = _miningContextKey;
        _miningContextKey = null;
        if (key is null) return;
        if (option == 0) QueueMining(key);
        else if (option == 1) QueueWalk(_miningContextWalkTarget);
        else if (FindMiningNode(key) is { Definition: var definition })
            _chatUi.AddMessage(
                $"A {definition.DisplayName}. A pickaxe can break it down.",
                ChatMessageStyle.Normal);
    }

    private void QueueMining(string stableKey)
    {
        var found = FindMiningNode(stableKey);
        if (found is null) return;
        if (PlayerInventory.BestPickaxe(_activePlayer?.Inventory) is null)
        {
            ReportBlockedAction(
                "mining-pickaxe", "You need a pickaxe to mine this.");
            return;
        }
        if (found.Value.Definition.RewardItemId is not null &&
            PlayerInventory.IsFull(_activePlayer?.Inventory))
        {
            ReportBlockedAction(
                "mining-inventory", "Your inventory is too full to mine this.");
            return;
        }
        _worldActions.QueueMining(
            found.Value.Node, stableKey);
    }

    internal void BeginMining(string stableKey, Vector2 target)
    {
        if (_player is null || FindMiningNode(stableKey) is null) return;
        _activeMiningKey = stableKey;
        _lastMiningStrike = 0;
        _player.MineAt(target);
    }

    internal void UpdateMining()
    {
        if (_activeMiningKey is null || _player is null ||
            _activePlayer is null) return;
        if (_player.Action != EntityAction.Mine)
        {
            _activeMiningKey = null;
            return;
        }
        if (!_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Mine),
                out var animation))
            return;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        // The AoE mining animation first contacts the node on authored
        // frame 10 (zero-based frame 9). Resolve sound, roll, damage, XP,
        // depletion, and feedback together on that pose.
        var impactFrame = Math.Clamp(9, 0, framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastMiningStrike) return;
        _lastMiningStrike = strike;

        var found = FindMiningNode(_activeMiningKey);
        var pickaxe = PlayerInventory.BestPickaxe(_activePlayer.Inventory);
        if (found is null || pickaxe is null)
        {
            StopMining();
            return;
        }
        var value = found.Value;
        var state = value.Gpu.Chunk.MiningStates.FirstOrDefault(candidate =>
            candidate.StableKey.Equals(_activeMiningKey,
                StringComparison.Ordinal));
        var health = state?.Health ?? value.Definition.MaximumHealth;
        var roll = MiningSkill.Roll(
            _activePlayer.MiningExperience,
            Random.Shared.NextSingle(), Random.Shared.NextSingle(),
            pickaxe.MiningPower);
        PlaySoundCue("mining-impact");
        if (!roll.Hit) return;

        var damage = Math.Min(health, roll.Damage);
        health -= damage;
        var experience = MiningSkill.AwardExperience(
            _activePlayer.MiningExperience,
            damage + (health == 0
                ? value.Definition.CompletionExperience : 0));
        AwardAdventureExperience(experience.Gained);
        var inventory = _activePlayer.Inventory;
        if (health == 0 &&
            value.Definition.RewardItemId is { } reward)
            PlayerInventory.TryAdd(inventory, reward, out inventory);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            MiningExperience = experience.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        value.Gpu.Chunk.MiningStates.RemoveAll(candidate =>
            candidate.StableKey.Equals(_activeMiningKey,
                StringComparison.Ordinal));
        value.Gpu.Chunk.MiningStates.Add(new(
            _activeMiningKey, health, value.Definition.MaximumHealth));
        QueueChunkSave(value.Gpu.Chunk);
        _saves.SavePlayer(_activePlayer);
        if (health != 0) return;

        value.Gpu.VegetationRenderItems = WorldVegetationRenderCache.Build(
            value.Gpu.Chunk, value.Gpu.RenderedHeights);
        _chatUi.AddMessage(
            value.Definition.RewardItemId is null
                ? $"You break apart the {value.Definition.DisplayName}."
                : $"You mine some {ItemCatalog.Get(value.Definition.RewardItemId).Caption.ToLowerInvariant()}.",
            ChatMessageStyle.Action);
        StopMining();
    }

    private void RenderMiningHealthBars(Vector4 scene)
    {
        if (_activeWorldLevel != (int)WorldLevel.Underground)
            return;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu) ||
                _activeMiningKey is null &&
                gpu.Chunk.MiningStates.Count == 0)
                continue;
            foreach (var cached in gpu.VegetationRenderItems)
            {
                if (cached.VegetationIndex < 0) continue;
                var node = gpu.Chunk.Vegetation[cached.VegetationIndex];
                if (!MiningNodeCatalog.TryGet(node, out var definition))
                    continue;
                var active = cached.StableKey.Equals(
                    _activeMiningKey, StringComparison.Ordinal);
                var state = gpu.Chunk.MiningStates.Find(candidate =>
                    candidate.StableKey.Equals(
                        cached.StableKey, StringComparison.Ordinal));
                if (!active && state is null ||
                    state is { Health: <= 0 })
                    continue;
                if (!_treeAtlas.TryGetValue(
                        cached.AtlasKey, out var entry))
                    continue;
                DrawWorldHealthBar(
                    scene,
                    SpriteBounds(entry.Frame, cached.World),
                    (state?.Health ?? definition.MaximumHealth) /
                    (float)(state?.MaxHealth ??
                            definition.MaximumHealth));
            }
        }
    }

    private void StopMining()
    {
        _activeMiningKey = null;
        _player?.Stop();
    }

    private (
        WorldVegetation Node,
        MiningNodeDefinition Definition,
        GpuWorldChunk Gpu)? FindMiningNode(string stableKey)
    {
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            foreach (var cached in gpu.VegetationRenderItems)
            {
                if (cached.VegetationIndex < 0 ||
                    !cached.StableKey.Equals(
                        stableKey, StringComparison.Ordinal))
                    continue;
                var node = gpu.Chunk.Vegetation[cached.VegetationIndex];
                if (MiningNodeCatalog.TryGet(node, out var definition))
                    return (node, definition, gpu);
            }
        }
        return null;
    }
}
