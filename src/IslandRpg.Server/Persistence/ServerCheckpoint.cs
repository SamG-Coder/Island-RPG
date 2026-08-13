using System.Text.Json.Serialization;

namespace IslandRpg.Server.Persistence;

/// <summary>
/// Durable state owned by one authoritative world server. Transient network
/// connections, queued input and movement routes are intentionally rebuilt on
/// restart; committed actor, inventory and world state is preserved together.
/// </summary>
public sealed record ServerCheckpoint(
    int SchemaVersion,
    long Revision,
    Guid WorldId,
    long WorldSeed,
    string BuildVersion,
    string ContentVersion,
    Guid SessionId,
    long Tick,
    long SnapshotSequence,
    IReadOnlyList<ServerActorCheckpoint> Actors,
    IReadOnlyList<ServerWorldObjectCheckpoint> WorldObjects,
    IReadOnlyList<ServerChunkRevisionCheckpoint> ChunkRevisions)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ServerActorCheckpoint(
    Guid PlayerId,
    Guid ActorId,
    string DisplayName,
    float X,
    float Y,
    int WorldLevel,
    long LastProcessedCommandSequence,
    long? DisconnectedAtTick,
    uint ActorRevision,
    int Health,
    float Hunger,
    float WellFedSeconds,
    int CraftingExperience,
    int CookingExperience,
    uint InventoryRevision,
    IReadOnlyList<ServerInventorySlotCheckpoint> Inventory,
    byte[] ReconnectTokenHash,
    IReadOnlyList<ServerCommandReceiptCheckpoint> CommandReceipts)
{
    // Keep credentials out of accidental structured-log interpolation.
    public override string ToString() =>
        $"{DisplayName} ({PlayerId:N}/{ActorId:N}) [credential redacted]";
}

public sealed record ServerCommandReceiptCheckpoint(
    Guid CommandId,
    string PayloadFingerprint,
    IslandRpg.Simulation.IntentStatus Status,
    string? Error);

public sealed record ServerInventorySlotCheckpoint(
    int Slot,
    string? ItemId,
    int Quantity);

public sealed record ServerWorldObjectCheckpoint(
    Guid ObjectId,
    string DefinitionId,
    float X,
    float Y,
    int ChunkX,
    int ChunkY,
    int WorldLevel,
    uint ObjectRevision,
    uint ContainerRevision,
    int Rotation,
    int Health,
    int MaximumHealth,
    string? OwnerId,
    string? GroupOwnerId,
    bool HasContainer,
    string? FuelItemId,
    double LitUntilGameSeconds,
    int FiremakingLevel,
    IslandRpg.Simulation.WorldGateAccessState GateState,
    bool AllowsDeposit,
    IReadOnlyList<ServerContainerSlotCheckpoint> Container);

public sealed record ServerContainerSlotCheckpoint(
    int Slot,
    string? ItemId,
    int Quantity,
    string? OwnerId);

public sealed record ServerChunkRevisionCheckpoint(
    int X,
    int Y,
    int WorldLevel,
    uint Revision);

public sealed record ServerCheckpointLoadResult(
    ServerCheckpoint Checkpoint,
    bool RecoveredFromBackup);

[JsonSerializable(typeof(ServerCheckpoint))]
internal sealed partial class ServerCheckpointJsonContext : JsonSerializerContext;
