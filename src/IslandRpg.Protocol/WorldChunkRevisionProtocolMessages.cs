namespace IslandRpg.Protocol;

/// <summary>
/// Authoritative revision for one world chunk, including chunks that currently
/// contain no public objects. Clients use this token for optimistic world
/// actions such as dropping an item into an otherwise empty chunk.
/// </summary>
public readonly record struct WorldChunkRevisionState(
    int ChunkX,
    int ChunkY,
    short WorldLevel,
    uint Revision);

/// <summary>
/// A bounded reliable baseline of authoritative chunk revisions. Multiple
/// messages may be sent to cover a large saved world.
/// </summary>
public sealed record WorldChunkRevisionBatchMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<WorldChunkRevisionState> Chunks) : IProtocolMessage
{
    public ProtocolMessageKind Kind =>
        ProtocolMessageKind.WorldChunkRevisionBatch;
}

/// <summary>
/// Generated ground loot is never published as world objects. Late joiners
/// receive this list so locally generated sticks/rocks/seeds stay hidden
/// after someone else already picked them.
/// </summary>
public sealed record PickedProceduralGroundObjectsMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<Guid> ObjectIds) : IProtocolMessage
{
    public ProtocolMessageKind Kind =>
        ProtocolMessageKind.PickedProceduralGroundObjects;
}
