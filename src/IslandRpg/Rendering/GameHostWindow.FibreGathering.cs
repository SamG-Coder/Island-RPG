using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const double FibreShrubCooldownSeconds = 5 * 60;
    private readonly ContextMenuControlState _vegetationContext = new();
    private string? _vegetationContextKey;
    private Vector2 _vegetationContextWalkTarget;
    private bool _vegetationContextBerries;
    private string? _activeFibreVegetationKey;

    private void InitializeFibreGathering() =>
        _vegetationContext.Selected +=
            HandleVegetationContextSelection;

    private bool TryGetGatherableVegetationUnderMouse(
        Vector2 mouse,
        out WorldVegetation vegetation,
        out string stableKey)
    {
        vegetation = null!;
        stableKey = "";
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
        for (var index = gpu.VegetationRenderItems.Length - 1;
             index >= 0;
             index--)
        {
            var cached = gpu.VegetationRenderItems[index];
            if (cached.VegetationIndex < 0) continue;
            var candidate =
                gpu.Chunk.Vegetation[cached.VegetationIndex];
            if ((!cached.CanGatherFibre && !cached.CanGatherBerries) ||
                !_treeAtlas.TryGetValue(
                    cached.AtlasKey, out var entry))
                continue;
            var bounds = SpriteBounds(entry.Frame, cached.World);
            if (mouse.X < bounds.Left || mouse.X >= bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y >= bounds.Bottom)
                continue;
            var scale = Math.Max(SpritePixelScale(), .001f);
            var x = (int)((mouse.X - bounds.Left) / scale);
            var y = (int)((mouse.Y - bounds.Top) / scale);
            if ((uint)x >= (uint)entry.Frame.Width ||
                (uint)y >= (uint)entry.Frame.Height ||
                entry.Frame.Rgba[
                    (y * entry.Frame.Width + x) * 4 + 3] <= 24)
                continue;
            if (!WorldHoverSelection.Prefer(
                    cached.World.Y, ref selectedDepth))
                continue;
            vegetation = candidate;
            stableKey = cached.StableKey;
        }
        }
        return vegetation is not null;
    }

    private void OpenVegetationContext(
        WorldVegetation vegetation,
        string stableKey,
        Vector2 walkTarget)
    {
        _vegetationContextKey = stableKey;
        _vegetationContextBerries =
            vegetation.Kind == WorldVegetationKind.BerryBush;
        _vegetationContextWalkTarget = walkTarget;
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _fishContext.Close();
        _vegetationContext.Open(
            MouseState.Position,
            [_vegetationContextBerries ? "Pick berries" : "Gather fibres",
             "Walk Here", "Examine"],
            SceneClientBounds(), 154);
    }

    private void HandleVegetationContextSelection(int option)
    {
        var key = _vegetationContextKey;
        var berries = _vegetationContextBerries;
        _vegetationContextKey = null;
        if (key is null) return;
        switch (option)
        {
            case 0:
                if (berries) QueueBerryGather(key);
                else QueueFibreGather(key);
                break;
            case 1:
                QueueWalk(_vegetationContextWalkTarget);
                break;
            case 2:
                _chatUi.AddMessage(
                    berries
                        ? "A wild berry bush ready to forage."
                        : "A fibrous green shrub. Its stems can be stripped " +
                          "and woven.",
                    ChatMessageStyle.Normal);
                break;
        }
    }

    private void QueueFibreGather(string stableKey)
    {
        if (IsNetworkWorld)
        {
            QueueNetworkVegetationAction(
                stableKey, ResourceActionKind.GatherFibre);
            return;
        }
        var located = FindVegetation(stableKey);
        if (located is not { } target) return;
        if (!VegetationReady(target.Gpu.Chunk, stableKey))
        {
            ReportBlockedAction(
                "fibre-shrub-recovering",
                "This shrub needs time to grow more usable fibres.");
            return;
        }
        if (_activePlayer is null ||
            !ActivePlayerInventory().CanAdd(ItemIds.PlantFibres))
        {
            ReportBlockedAction(
                "fibre-inventory-full",
                "Your inventory is too full to gather fibres.");
            return;
        }
        _worldActions.QueueFibreShrub(
            target.Vegetation, stableKey);
    }

    internal void BeginFibreGather(
        string stableKey, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var located = FindVegetation(stableKey);
        if (located is null ||
            !VegetationReady(located.Value.Gpu.Chunk, stableKey))
            return;
        _activeFibreVegetationKey = stableKey;
        _player.GatherAt(target);
    }

    internal void UpdateFibreGathering()
    {
        if (_activeFibreVegetationKey is null ||
            _player is null || _activePlayer is null)
            return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeFibreVegetationKey = null;
            return;
        }
        if (_player.ActionTime < GroundItemActionSeconds) return;

        var key = _activeFibreVegetationKey;
        _activeFibreVegetationKey = null;
        var located = FindVegetation(key);
        if (located is not { } target ||
            !VegetationReady(target.Gpu.Chunk, key))
        {
            _player.Stop();
            return;
        }

        var requested = Random.Shared.Next(1, 3) +
                        FarmingSkill.GatheringBasketBonus(
                            _activePlayer.Inventory);
        var inventory = ActivePlayerInventory();
        var gathered = inventory.AddUpTo(
            ItemIds.PlantFibres, requested);
        if (gathered == 0)
        {
            ReportBlockedAction(
                "fibre-inventory-full",
                "Your inventory is too full to gather fibres.");
            _player.Stop();
            return;
        }

        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        AwardAdventureExperience(gathered * 2);
        SetVegetationCooldown(
            target.Gpu.Chunk, key, FibreShrubCooldownSeconds);
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(target.Gpu.Chunk);
        RecordQuestEvent(new(
            QuestEventType.GatherItem,
            ItemIds.PlantFibres,
            gathered));
        _chatUi.AddMessage(
            gathered == 1
                ? "You gather some plant fibres."
                : "You gather two bundles of plant fibres.",
            ChatMessageStyle.Action);
        if (gathered < requested)
            _chatUi.AddMessage(
                "You leave some fibres behind because your inventory is full.",
                ChatMessageStyle.Warning);
        _player.Stop();
    }

    private bool VegetationReady(
        WorldChunk chunk, string stableKey) =>
        chunk.VegetationFibreStates.FirstOrDefault(state =>
            state.StableKey.Equals(
                stableKey, StringComparison.Ordinal))
        is not { } state ||
        state.ReadyAtGameSeconds <= _worldGameSeconds;

    private void SetVegetationCooldown(
        WorldChunk chunk, string stableKey, double cooldownSeconds)
    {
        chunk.VegetationFibreStates.RemoveAll(state =>
            state.StableKey.Equals(
                stableKey, StringComparison.Ordinal));
        chunk.VegetationFibreStates.Add(new(
            stableKey,
            _worldGameSeconds + cooldownSeconds));
    }

    private (
        WorldVegetation Vegetation,
        GpuWorldChunk Gpu)? FindVegetation(string stableKey)
    {
        foreach (var gpu in _worldChunks.Values)
        {
        if (!IsActiveWorldChunk(gpu)) continue;
        for (var index = 0;
             index < gpu.VegetationRenderItems.Length;
             index++)
            if (gpu.VegetationRenderItems[index].StableKey.Equals(
                    stableKey, StringComparison.Ordinal))
                return (
                    gpu.Chunk.Vegetation[
                        gpu.VegetationRenderItems[index].VegetationIndex],
                    gpu);
        }
        return null;
    }
}
