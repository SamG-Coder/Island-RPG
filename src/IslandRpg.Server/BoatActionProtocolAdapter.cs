using System.Numerics;
using IslandRpg.Protocol;
using IslandRpg.Simulation;
using ProtocolBoatReference = IslandRpg.Protocol.BoatReference;
using SimulationBoatReference = IslandRpg.Simulation.BoatReference;

namespace IslandRpg.Server;

/// <summary>
/// Pure projection boundary for untrusted boat commands. Position, ownership,
/// occupancy, routes, and network identity always come from Simulation.
/// </summary>
internal static class BoatActionProtocolAdapter
{
    public static BoatGameplayIntent ToIntent(
        ActionCommandMessage command,
        BoatActionPayload action)
    {
        var reference = new SimulationBoatReference(
            new BoatId(action.Boat.BoatId),
            action.Boat.ExpectedRevision);
        return action switch
        {
            BoardBoatAction => new BoardBoatIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                reference),
            MoveBoatAction move => new MoveBoatIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                reference,
                new Vector2(move.TargetX, move.TargetY)),
            StopBoatAction => new StopBoatIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                reference),
            DisembarkBoatAction disembark => new DisembarkBoatIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                reference,
                new Vector2(disembark.TargetX, disembark.TargetY)),
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                "The boat action is not supported by this authority.")
        };
    }

    public static BoatActionResultMessage ToPrivateResult(
        ulong sequence,
        ulong tick,
        ActionCommandMessage command,
        BoatActionPayload action,
        IntentResult result)
    {
        var transaction = result.BoatTransaction;
        var transition = transaction?.ActorTransition;
        return new BoatActionResultMessage(
            sequence,
            tick,
            command.CommandId,
            action.Action,
            action.Boat,
            result.Accepted,
            DedicatedServer.MapRejection(result.Status),
            result.Error ?? transaction?.Detail ?? string.Empty,
            result.ActorRevision,
            result.InventoryRevision,
            transaction?.BoatDelta?.Current?.Revision ??
                transaction?.BoatDelta?.Previous?.Revision ??
                action.Boat.ExpectedRevision,
            transition is not null,
            transition?.Position.X ?? 0,
            transition?.Position.Y ?? 0,
            checked((short)(transition?.WorldLevel ?? 0)));
    }

    public static BoatBaselineMessage ToBaseline(
        ulong sequence,
        ulong tick,
        IReadOnlyList<AuthoritativeBoatSnapshot> boats) => new(
        sequence,
        tick,
        boats.Select(ToState).ToArray());

    public static BoatDeltaBatchMessage? ToPublicDelta(
        ulong sequence,
        ulong tick,
        BoatStateDelta? delta)
    {
        if (delta is null) return null;
        var previous = delta.Previous;
        var current = delta.Current;
        var id = current?.BoatId ?? previous?.BoatId ??
            throw new InvalidOperationException("Boat delta omitted both states.");
        var expectedRevision = previous?.Revision ?? 0;
        var currentRevision = current?.Revision ?? checked(expectedRevision + 1);
        if (currentRevision <= expectedRevision)
            throw new InvalidOperationException(
                "A public boat delta must advance its semantic revision.");
        return new BoatDeltaBatchMessage(
            sequence,
            tick,
            [new BoatDelta(
                current is null ? BoatDeltaKind.Remove : BoatDeltaKind.Upsert,
                new ProtocolBoatReference(id.Value, expectedRevision),
                currentRevision,
                current is null ? null : ToState(current))]);
    }

    public static BoatState ToState(AuthoritativeBoatSnapshot value) => new(
        value.BoatId.Value,
        value.NetworkEntityId,
        value.Revision,
        value.OwnerPlayerId.Value,
        value.GroupId ?? string.Empty,
        value.OccupantPlayerId?.Value ?? Guid.Empty,
        value.OccupantActorId is { } actor
            ? DedicatedServer.StableNetworkId(actor.Value)
            : 0,
        value.Position.X,
        value.Position.Y,
        value.Facing.X,
        value.Facing.Y,
        checked((short)value.WorldLevel),
        value.Destination is not null);
}
