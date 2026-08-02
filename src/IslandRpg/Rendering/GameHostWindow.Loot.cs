using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void DropEnemyLoot(EnemyState enemy)
    {
        var coordinate = new ChunkCoordinate(
            FloorDiv((int)MathF.Floor(enemy.Position.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(enemy.Position.Y), WorldChunk.Size),
            enemy.WorldLevel);
        if (!_worldChunks.TryGetValue(coordinate, out var gpu)) return;
        var loot = LootBagService.Roll(enemy, unchecked((int)_worldSeed));
        if (loot.Count == 0) return;
        var bag = LootBagService.Create(Guid.NewGuid(), enemy.Position, loot);
        gpu.Chunk.GroundObjects.Add(bag);
        QueueChunkSave(gpu.Chunk);
        _chatUi.AddMessage(
            $"The {EnemyDisplayName(enemy.Kind).ToLowerInvariant()} left a loot bag.",
            ChatMessageStyle.Action);
    }

    private void UpdateLootBags()
    {
        if (_emptyLootBagFadeStarts.Count == 0) return;
        foreach (var entry in _emptyLootBagFadeStarts.ToArray())
        {
            if (!LootBagService.FadeFinished(entry.Value, _clock)) continue;
            var location = FindGroundObjectLocation(entry.Key);
            if (location is not null)
            {
                location.Value.Chunk.GroundObjects.RemoveAt(location.Value.Index);
                QueueChunkSave(location.Value.Chunk);
            }
            _emptyLootBagFadeStarts.Remove(entry.Key);
        }
    }

    private float LootBagOpacity(WorldGroundObject value)
    {
        if (!LootBagService.IsLootBag(value.ItemId)) return 1f;
        return _emptyLootBagFadeStarts.TryGetValue(value.Id, out var startedAt)
            ? LootBagService.FadeOpacity(startedAt, _clock)
            : 1f;
    }
}
