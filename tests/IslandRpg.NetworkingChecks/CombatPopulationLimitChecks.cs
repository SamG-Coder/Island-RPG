using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class CombatPopulationLimitChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "combat authority persistence and baseline share the 512 enemy cap",
            AuthorityPersistenceAndBaselineShareCap);
        checks.Add(
            "declared world population fits one reliable snapshot boundary",
            DeclaredWorldPopulationFitsReliableSnapshot);
    }

    private static void AuthorityPersistenceAndBaselineShareCap()
    {
        CheckAssert.Equal(
            ProtocolLimits.MaxEnemiesPerBatch,
            CombatPopulationLimits.MaximumEnemies,
            "wire and authoritative population bounds must be one invariant");
        CheckAssert.Equal(
            CombatPopulationLimits.MaximumEnemies,
            ServerCheckpointStore.MaximumEnemies,
            "persistence must not admit a population the wire cannot baseline");
        CheckAssert.Throws<ArgumentOutOfRangeException>(
            () => new AuthoritativeCombatTransactions(
                41,
                options: new AuthoritativeCombatOptions
                {
                    MaximumEnemies =
                        CombatPopulationLimits.MaximumEnemies + 1
                }),
            "authority options must reject a population above one baseline");

        var authority = new AuthoritativeCombatTransactions(41);
        for (var index = 0;
             index < CombatPopulationLimits.MaximumEnemies;
             index++)
            authority.Seed(new AuthoritativeEnemySeed(
                new EnemyId(Id(index + 1)),
                EnemyKind.WaterSlime,
                Vector2.Zero));

        var enemies = authority.CaptureEnemies();
        CheckAssert.Equal(
            CombatPopulationLimits.MaximumEnemies,
            enemies.Length,
            "the exact authoritative boundary must be accepted");
        CheckAssert.Throws<ArgumentException>(
            () => authority.Seed(new AuthoritativeEnemySeed(
                new EnemyId(Id(enemies.Length + 1)),
                EnemyKind.WaterSlime,
                Vector2.Zero)),
            "the first enemy above the authoritative boundary must reject");

        var baseline = CombatActionProtocolAdapter.ToBaseline(1, 0, enemies);
        ReliableProtocolCodec.Encode(baseline);
        CheckAssert.Equal(
            CombatPopulationLimits.MaximumEnemies,
            baseline.Enemies.Count,
            "the exact authority population must fit one reliable baseline");

        var worldId = Guid.Parse(
            "044140e7-2a9b-533e-ae26-9eb68ffb14ab");
        var options = new ServerOptions(
            System.Net.IPAddress.Loopback,
            0,
            worldId,
            41,
            "combat-cap",
            "base",
            1);
        var checkpoint = new AuthoritativeSessionCheckpoint(
            new SessionId(worldId),
            0,
            0,
            ImmutableArray<AuthoritativeActorCheckpoint>.Empty,
            new AuthoritativeWorldTransactionsCheckpoint([], []),
            Combat: authority.CaptureCheckpoint());
        var durable = ServerCheckpointMapper.ToDurable(
            checkpoint, options, revision: 1);
        ServerCheckpointStore.Validate(durable, worldId);

        var combat = durable.Combat!;
        var tooMany = durable with
        {
            Combat = combat with
            {
                NextSpawnOrdinal = checked(combat.NextSpawnOrdinal + 1),
                Enemies = combat.Enemies.Append(combat.Enemies[0] with
                {
                    EnemyId = Id(
                        CombatPopulationLimits.MaximumEnemies + 1),
                    SpawnOrdinal = combat.NextSpawnOrdinal
                }).ToArray()
            }
        };
        CheckAssert.Throws<InvalidDataException>(
            () => ServerCheckpointStore.Validate(tooMany, worldId),
            "persistence must reject the first population a client cannot baseline");
    }

    private static void DeclaredWorldPopulationFitsReliableSnapshot()
    {
        CheckAssert.Equal(
            NetworkPopulationLimits.MaximumActors,
            ServerCheckpointStore.MaximumActors,
            "persistence and transport must share the actor membership bound");
        CheckAssert.Equal(
            NetworkPopulationLimits.MaximumBoats,
            ProtocolLimits.MaxBoatsPerBatch,
            "boat baselines must share the snapshot membership bound");
        CheckAssert.Equal(
            NetworkPopulationLimits.MaximumBoats,
            ServerCheckpointStore.MaximumBoats,
            "persistence and transport must share the boat membership bound");
        CheckAssert.Equal(
            NetworkPopulationLimits.MaximumSnapshotEntities,
            ProtocolLimits.MaxSnapshotEntities,
            "the wire snapshot bound must cover every declared membership cap");
        CheckAssert.Equal(
            NetworkPopulationLimits.MaximumActors +
            NetworkPopulationLimits.MaximumBoats +
            NetworkPopulationLimits.MaximumEnemies,
            ProtocolLimits.MaxSnapshotEntities,
            "the aggregate snapshot bound must not omit an entity class");

        var entities = Enumerable.Range(
                0, ProtocolLimits.MaxSnapshotEntities)
            .Select(static index => new EntitySnapshot(
                checked((ulong)index + 1),
                index < NetworkPopulationLimits.MaximumActors
                    ? NetworkEntityKind.Player
                    : index < NetworkPopulationLimits.MaximumActors +
                              NetworkPopulationLimits.MaximumBoats
                        ? NetworkEntityKind.Boat
                        : NetworkEntityKind.Enemy,
                0,
                0,
                index,
                0,
                0,
                0,
                NetworkEntityState.None,
                1))
            .ToArray();
        var message = new EntitySnapshotMessage(
            1,
            10,
            new SnapshotMetadata(
                0, 1, 0, 0, 10, 0, SnapshotFlags.Keyframe),
            entities);
        var frame = ReliableProtocolCodec.Encode(message);
        const int snapshotMetadataWireSize =
            sizeof(ulong) + sizeof(ushort) + sizeof(ushort) + sizeof(uint) +
            sizeof(ulong) + sizeof(ulong) + sizeof(byte);
        var expectedFrameBytes = ProtocolConstants.ReliableHeaderSize +
            snapshotMetadataWireSize + sizeof(ushort) +
            entities.Length * EntitySnapshot.WireSize;
        CheckAssert.Equal(expectedFrameBytes, frame.Length,
            "the exact declared population must retain its fixed wire budget");
        CheckAssert.True(frame.Length <= ProtocolConstants.MaxReliableFrameBytes,
            "the exact declared population must fit one reliable frame");

        var decoded = (EntitySnapshotMessage)ReliableProtocolCodec.Decode(frame);
        CheckAssert.Equal(ProtocolLimits.MaxSnapshotEntities,
            decoded.Entities.Count,
            "the exact declared population must decode without truncation");
        var reconstructor = new EntitySnapshotReconstructor();
        CheckAssert.True(reconstructor.TryReconstruct(decoded, out var complete),
            "the client must accept the exact declared membership boundary");
        CheckAssert.Equal(ProtocolLimits.MaxSnapshotEntities,
            complete.Snapshot.Entities.Count,
            "client reconstruction must preserve every boundary entity");
    }

    private static Guid Id(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        bytes[15] = 0x7d;
        return new Guid(bytes);
    }
}
