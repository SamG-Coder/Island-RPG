using System.Collections.Immutable;
using System.Numerics;

namespace IslandRpg.Simulation;

/// <summary>
/// Durable actor identity and gameplay. Live connection IDs, movement routes
/// and receipt caches are intentionally transient and rebuilt after restart.
/// </summary>
public sealed record AuthoritativeActorCheckpoint(
    PlayerIdentity Identity,
    string DisplayName,
    Vector2 Position,
    int WorldLevel,
    long LastProcessedCommandSequence,
    long? DisconnectedAtTick,
    PlayerGameplaySnapshot Gameplay,
    ImmutableArray<byte> ReconnectTokenHash,
    ImmutableArray<AuthoritativeCommandReceiptCheckpoint> CommandReceipts)
{
    public override string ToString() =>
        $"{DisplayName} ({Identity.PlayerId}/{Identity.ActorId}) [credential redacted]";
}

/// <summary>
/// Bounded durable tombstone for an already-processed gameplay command. Large
/// requester-only transaction deltas remain transient; the original outcome
/// is sufficient to prevent the mutation from executing again after restart.
/// </summary>
public sealed record AuthoritativeCommandReceiptCheckpoint(
    Guid CommandId,
    string PayloadFingerprint,
    IntentStatus Status,
    string? Error);

/// <summary>
/// One atomic, immutable image of all durable authoritative session state.
/// </summary>
public sealed record AuthoritativeSessionCheckpoint(
    SessionId SessionId,
    long Tick,
    long SnapshotSequence,
    ImmutableArray<AuthoritativeActorCheckpoint> Actors,
    AuthoritativeWorldTransactionsCheckpoint World);
