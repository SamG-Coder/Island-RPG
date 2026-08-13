using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

/// <summary>
/// Pure projection boundary between untrusted resource wire claims and the
/// simulation's typed, revision-checked transactions.
/// </summary>
internal static class ResourceActionProtocolAdapter
{
    public static ResourceGameplayIntent ToIntent(
        ActionCommandMessage command,
        ResourceActionPayload action) => action.Action switch
        {
            ResourceActionKind.GatherTreeStick => new GatherTreeStickIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                action.Resource),
            ResourceActionKind.CutTree => new StrikeTreeIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                action.Resource,
                action.ToolInventorySlot),
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                "The resource action is not supported by this authority.")
        };

    public static ResourceActionResultMessage ToPrivateResult(
        ulong sequence,
        ulong tick,
        ActionCommandMessage command,
        ResourceActionPayload action,
        IntentResult result)
    {
        var transaction = result.ResourceTransaction;
        var rewards = transaction?.Rewards.IsDefaultOrEmpty is false
            ? transaction.Rewards.Select(static value =>
                new ResourceItemRewardState(
                    value.ItemId, value.Quantity)).ToArray()
            : [];
        return new ResourceActionResultMessage(
            sequence,
            tick,
            command.CommandId,
            result.Accepted,
            DedicatedServer.MapRejection(result.Status),
            result.Error ?? transaction?.Detail ?? string.Empty,
            result.ActorRevision,
            result.InventoryRevision,
            action.Action,
            action.Resource,
            rewards,
            transaction?.Hit ?? false,
            transaction?.Damage ?? 0,
            transaction?.ToolWorn ?? false);
    }

    public static ResourceNodeDeltaBatchMessage? ToPublicDelta(
        ulong sequence,
        ulong tick,
        ResourceTransactionResult result)
    {
        if (!result.Accepted ||
            result.NodeDelta is not { } node ||
            result.ChunkDelta is not { } chunk)
            return null;
        if (node.Previous.Id != node.Current.Id ||
            node.Previous.Chunk != node.Current.Chunk ||
            node.Current.Chunk != chunk.Chunk ||
            node.Current.NodeRevision <= node.Previous.NodeRevision ||
            chunk.CurrentRevision <= chunk.PreviousRevision)
            throw new InvalidOperationException(
                "The committed resource transaction has an invalid revision chain.");

        return new ResourceNodeDeltaBatchMessage(
            sequence,
            tick,
            [new ResourceNodeDelta(
                ResourceNodeDeltaKind.Upsert,
                new ResourceNodeReference(
                    node.Previous.Id,
                    node.Previous.Chunk,
                    node.Previous.NodeRevision,
                    chunk.PreviousRevision),
                node.Current.NodeRevision,
                chunk.CurrentRevision,
                node.Current)]);
    }

    public static ResourceChunkBaselineMessage ToBaseline(
        ulong sequence,
        ulong tick,
        ResourceChunkSparseState chunk) => new(
        sequence,
        tick,
        chunk.Chunk,
        chunk.ResourceChunkRevision,
        chunk.Nodes,
        []);
}
