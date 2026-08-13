using System.Numerics;
using IslandRpg.Protocol;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

/// <summary>
/// Pure wire projection for cave commands. All validation and mutation remain
/// on the simulation thread in the authoritative world aggregate.
/// </summary>
internal static class CaveActionProtocolAdapter
{
    public static WorldGameplayIntent ToIntent(
        ActionCommandMessage command,
        CaveActionPayload action) => action switch
        {
            StartExcavationAction value => new StartExcavationIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                new Vector2(value.X, value.Y),
                value.WorldLevel,
                value.ShovelInventorySlot,
                value.ExpectedChunkRevision),
            WorkExcavationAction value => new WorkExcavationIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Excavation),
                value.ShovelInventorySlot),
            RestoreExcavationAction value => new RestoreExcavationIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Excavation)),
            InstallCaveRopeAction value => new InstallCaveRopeIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Shaft),
                value.RopeInventorySlot),
            TakeCaveRopeAction value => new TakeCaveRopeIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Entrance)),
            FillExcavationAction value => new FillExcavationIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Excavation),
                value.MaterialInventorySlot),
            TraverseCaveAction value => new TraverseCaveIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Entrance)),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    public static CaveActionResultMessage ToPrivateResult(
        ulong sequence,
        ulong tick,
        ActionCommandMessage command,
        CaveActionPayload action,
        IntentResult result)
    {
        var transition = result.WorldTransaction?.ActorTransition;
        var (damage, completed) = Outcome(action, result.WorldTransaction);
        var worldLevel = transition is null
            ? (short)0
            : ToWireWorldLevel(transition.Value.WorldLevel);
        return new CaveActionResultMessage(
            sequence,
            tick,
            command.CommandId,
            action.Action,
            result.Accepted,
            DedicatedServer.MapRejection(result.Status),
            result.Error ?? result.WorldTransaction?.Detail ?? string.Empty,
            result.ActorRevision,
            result.InventoryRevision,
            transition is not null,
            transition?.Position.X ?? 0,
            transition?.Position.Y ?? 0,
            worldLevel,
            damage,
            completed);
    }

    private static (int Damage, bool Completed) Outcome(
        CaveActionPayload action,
        WorldTransactionResult? transaction)
    {
        if (action.Action != CaveActionKind.WorkExcavation ||
            transaction is not { Accepted: true, CaveOutcome: { } outcome })
            return (0, false);
        return (outcome.Damage, outcome.Completed);
    }

    private static WorldObjectHandle Handle(WorldObjectReference value) => new(
        value.ObjectId,
        new WorldChunkKey(value.ChunkX, value.ChunkY, value.WorldLevel),
        value.ExpectedObjectRevision,
        value.ExpectedChunkRevision);

    private static short ToWireWorldLevel(int value)
    {
        if (value is < short.MinValue or > short.MaxValue)
            throw new InvalidOperationException(
                "The authoritative cave destination level is outside protocol bounds.");
        return (short)value;
    }
}
