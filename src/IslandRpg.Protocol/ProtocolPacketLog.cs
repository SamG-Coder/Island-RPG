namespace IslandRpg.Protocol;

/// <summary>
/// Optional line log of reliable and UDP packets. Set ISLANDRPG_PACKET_LOG to
/// a file path. Used to see what join actually dumps onto the client.
/// </summary>
public static class ProtocolPacketLog
{
    private static readonly string? Path =
        Environment.GetEnvironmentVariable("ISLANDRPG_PACKET_LOG");
    private static readonly object Sync = new();
    private static readonly long Started = Environment.TickCount64;

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Path);

    public static void Write(
        string direction,
        IProtocolMessage message,
        int bytes)
    {
        if (!IsEnabled || Path is null) return;
        WriteRaw(
            $"{Ms()} {direction} {message.Kind} seq={message.Sequence} " +
            $"tick={message.Tick} bytes={bytes} {Describe(message)}");
    }

    public static void WriteUdp(string direction, EntitySnapshotMessage snapshot, int bytes) =>
        Write(direction, snapshot, bytes);

    public static void WriteRaw(string line)
    {
        if (!IsEnabled || Path is null) return;
        lock (Sync)
        {
            File.AppendAllText(Path, line + Environment.NewLine);
        }
    }

    private static string Ms() =>
        (Environment.TickCount64 - Started).ToString();

    private static string Describe(IProtocolMessage message) => message switch
    {
        WorldObjectStateMessage => "objects=1",
        WorldObjectDeltaBatchMessage batch => $"deltas={batch.Deltas.Count}",
        WorldChunkRevisionBatchMessage batch => $"chunks={batch.Chunks.Count}",
        EntitySnapshotMessage snapshot =>
            $"entities={snapshot.Entities.Count} flags={snapshot.Metadata.Flags}",
        EnemyBaselineMessage baseline => $"enemies={baseline.Enemies.Count}",
        EnemyDeltaBatchMessage batch => $"deltas={batch.Deltas.Count}",
        BoatBaselineMessage baseline => $"boats={baseline.Boats.Count}",
        BoatDeltaBatchMessage batch => $"deltas={batch.Deltas.Count}",
        ResourceChunkBaselineMessage baseline =>
            $"nodes={baseline.Nodes.Count} tombs={baseline.Tombstones.Count}",
        ResourceNodeDeltaBatchMessage batch => $"deltas={batch.Deltas.Count}",
        CombatEventBatchMessage batch => $"events={batch.Events.Count}",
        PlayerStateMessage state =>
            $"flags={state.Flags} slots={state.InventorySlots.Count}",
        SocialStateMessage social =>
            $"friends={social.Friends.Count} ignored={social.Ignored.Count} " +
            $"trade={social.OpenTradeId != Guid.Empty}",
        HandshakeAcceptedMessage accepted =>
            $"world={accepted.WorldId:N} seed={accepted.WorldSeed} " +
            $"spawn={accepted.SpawnX:0.##},{accepted.SpawnY:0.##} " +
            $"level={accepted.SpawnWorldLevel}",
        HandshakeRequestMessage request =>
            $"player={request.PlayerName} world={request.RequestedWorldId:N}",
        _ => ""
    };
}