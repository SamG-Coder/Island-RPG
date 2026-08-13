using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ServerWorldActionAdapterChecks
{
    private static readonly Guid CommandId = Guid.Parse(
        "50000000-0000-0000-0000-000000000001");
    private static readonly Guid ObjectId = Guid.Parse(
        "60000000-0000-0000-0000-000000000001");
    private static readonly WorldObjectReference ObjectReference = new(
        ObjectId, 2, -3, 0, 4, 8);

    public static void Register(CheckRunner checks)
    {
        checks.Add("server maps every world action to a session intent",
            MapsEveryWorldAction);
        checks.Add("server maps every cave action and typed outcome exactly",
            MapsEveryCaveActionAndOutcome);
        checks.Add("server projects revisioned public world deltas without private slots",
            ProjectsPublicDeltasWithoutPrivateState);
        checks.Add("server projects player and container state only as private messages",
            ProjectsPrivateState);
        checks.Add("server maps every world transaction rejection",
            MapsEveryWorldRejection);
    }

    private static void MapsEveryWorldAction()
    {
        IActionCommandPayload[] payloads =
        [
            new PickUpWorldObjectAction(ObjectReference),
            new DropInventoryItemAction(3, 2, 65, -95, 0, 8),
            new PlaceInventoryWorldObjectAction(
                "cooking_pot", 3, 65, -95, 0, 0, 8),
            new PlantCropAction(3, 65.5f, -94.5f, 0, 8),
            new HarvestCropAction(ObjectReference),
            new OpenContainerAction(ObjectReference),
            new ContainerTransferAction(
                ObjectReference, 7, ContainerTransferDirection.Withdraw,
                4, 1, 2),
            new AddCampfireFuelAction(ObjectReference, 5),
            new TakeCampfireFuelAction(ObjectReference),
            new LightCampfireAction(ObjectReference),
            new CookOnCampfireAction(ObjectReference, 5),
            new PlaceConstructionAction(
                "wooden_wall", 6, 65, -95, 0, 2, 8),
            new BuildConstructionAction(ObjectReference),
            new DemolishWorldObjectAction(ObjectReference),
        ];
        Type[] expectedTypes =
        [
            typeof(PickUpWorldObjectIntent),
            typeof(DropInventoryItemIntent),
            typeof(PlaceInventoryWorldObjectIntent),
            typeof(PlantCropIntent),
            typeof(HarvestCropIntent),
            typeof(OpenWorldContainerIntent),
            typeof(TransferWorldContainerIntent),
            typeof(AddCampfireFuelIntent),
            typeof(TakeCampfireFuelIntent),
            typeof(LightCampfireIntent),
            typeof(CookOnCampfireIntent),
            typeof(PlaceConstructionIntent),
            typeof(BuildConstructionIntent),
            typeof(DemolishWorldObjectIntent),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var command = Command(payloads[index]);
            CheckAssert.True(
                WorldActionProtocolAdapter.TryToWorldIntent(
                    command, out var intent),
                $"{payloads[index].Kind} should be recognized as a world action");
            CheckAssert.Equal(
                expectedTypes[index], intent!.GetType(),
                $"{payloads[index].Kind} should map to its exact session intent");
            CheckAssert.Equal(CommandId, intent.CommandId,
                "the stable command identity must survive projection");
            CheckAssert.Equal(2u, intent.ExpectedInventoryRevision,
                "the inventory optimistic lock must survive projection");
            CheckAssert.Equal(3u, intent.ExpectedActorRevision,
                "the actor optimistic lock must survive projection");
        }

        var transfer = (TransferWorldContainerIntent)ToIntent(payloads[6]);
        CheckAssert.Equal(7u, transfer.Container.ExpectedContainerRevision,
            "container transfer must carry its private revision lock");
        CheckAssert.Equal(WorldContainerTransferDirection.Withdraw,
            transfer.Direction, "container direction must map exactly");
        CheckAssert.False(
            WorldActionProtocolAdapter.TryToWorldIntent(
                Command(new InventorySwapAction(0, 1)), out var nonWorld),
            "ordinary inventory actions must remain on their existing path");
        CheckAssert.True(nonWorld is null,
            "a rejected non-world projection must not return an intent");
    }

    private static void MapsEveryCaveActionAndOutcome()
    {
        CaveActionPayload[] payloads =
        [
            new StartExcavationAction(65, -95, 0, 4, 8),
            new WorkExcavationAction(ObjectReference, 4),
            new RestoreExcavationAction(ObjectReference),
            new InstallCaveRopeAction(ObjectReference, 5),
            new TakeCaveRopeAction(ObjectReference),
            new FillExcavationAction(ObjectReference, 6),
            new TraverseCaveAction(ObjectReference),
        ];
        Type[] expectedTypes =
        [
            typeof(StartExcavationIntent),
            typeof(WorkExcavationIntent),
            typeof(RestoreExcavationIntent),
            typeof(InstallCaveRopeIntent),
            typeof(TakeCaveRopeIntent),
            typeof(FillExcavationIntent),
            typeof(TraverseCaveIntent),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var intent = CaveActionProtocolAdapter.ToIntent(
                Command(payloads[index]), payloads[index]);
            CheckAssert.Equal(expectedTypes[index], intent.GetType(),
                $"{payloads[index].Action} must map to its exact session intent");
            CheckAssert.Equal(CommandId, intent.CommandId,
                "cave command correlation must survive projection");
            CheckAssert.Equal(2u, intent.ExpectedInventoryRevision,
                "cave inventory optimistic locks must survive projection");
            CheckAssert.Equal(3u, intent.ExpectedActorRevision,
                "cave actor optimistic locks must survive projection");
        }

        var workTransaction = Result() with
        {
            Detail = "excavation_strike:7",
            CaveOutcome = new CaveActionOutcome(7, false),
        };
        var work = CaveActionProtocolAdapter.ToPrivateResult(
            30, 40, Command(payloads[1]), payloads[1],
            Intent(workTransaction));
        CheckAssert.True(work.Accepted,
            "accepted authoritative cave work must remain accepted");
        CheckAssert.Equal(7, work.Damage,
            "the requester must receive typed strike damage");
        CheckAssert.False(work.Completed,
            "an incomplete strike must not claim discovery");
        CheckAssert.False(work.Transitioned,
            "ordinary cave work cannot move the actor");

        var traversalTransaction = Result() with
        {
            Detail = "cave_traversed",
            ActorTransition = new WorldActorTransition(
                new Vector2(65, -95), -1),
        };
        var traversal = CaveActionProtocolAdapter.ToPrivateResult(
            31, 41, Command(payloads[6]), payloads[6],
            Intent(traversalTransaction));
        CheckAssert.True(traversal.Transitioned,
            "accepted traversal must publish its authoritative destination");
        CheckAssert.Equal(-1, (int)traversal.WorldLevel,
            "the server must choose the destination world level");
        CheckAssert.Equal(65f, traversal.X,
            "the transition must retain its exact X coordinate");
        CheckAssert.Equal(-95f, traversal.Y,
            "the transition must retain its exact Y coordinate");
    }

    private static void ProjectsPublicDeltasWithoutPrivateState()
    {
        var result = Result();
        var message = WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
            10, 20, result);
        CheckAssert.True(message is not null,
            "an accepted object mutation must produce a public batch");
        var delta = message!.Deltas.Single();
        CheckAssert.Equal(WorldObjectDeltaKind.Upsert, delta.Kind,
            "an added object should project as an upsert");
        CheckAssert.Equal(ObjectId, delta.Reference.ObjectId,
            "the stable object identity must survive projection");
        CheckAssert.Equal(0u, delta.Reference.ExpectedObjectRevision,
            "the wire reference must carry the previous object revision");
        CheckAssert.Equal(8u, delta.Reference.ExpectedChunkRevision,
            "the wire reference must carry the previous chunk revision");
        CheckAssert.Equal(9u, delta.CurrentChunkRevision,
            "the delta must carry the current chunk revision");
        CheckAssert.Equal(1u, delta.State!.Value.ObjectRevision,
            "the public state must carry the current object revision");
        CheckAssert.Equal(9u, delta.State.Value.ChunkRevision,
            "the public state must carry the current chunk revision");

        var encoded = ReliableProtocolCodec.Encode(message);
        CheckAssert.False(encoded.AsSpan().IndexOf("slime_gel"u8) >= 0,
            "a public world broadcast must never contain container slots");
        CheckAssert.False(encoded.AsSpan().IndexOf("private-owner"u8) >= 0,
            "a public world broadcast must never contain item ownership");

        CheckAssert.True(
            WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                11, 21, result with
                {
                    Status = WorldTransactionStatus.OutOfRange,
                    ObjectDeltas = [],
                    ChunkDeltas = [],
                }) is null,
            "a rejected transaction must not produce public changes");

        var secondId = Guid.Parse(
            "60000000-0000-0000-0000-000000000002");
        var first = result.ObjectDeltas[0];
        var secondState = first.Object! with { ObjectId = secondId };
        var sameChunk = result with
        {
            ObjectDeltas =
            [
                first,
                new WorldObjectTransactionDelta(
                    WorldObjectChangeKind.Added,
                    secondId,
                    first.Chunk,
                    0,
                    1,
                    secondState),
            ],
        };
        var multi = WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
            12, 22, sameChunk)!;
        CheckAssert.Equal(2, multi.Deltas.Count,
            "one atomic chunk revision may contain multiple object changes");
        CheckAssert.True(multi.Deltas.All(value =>
                value.Reference.ExpectedChunkRevision == 8 &&
                value.CurrentChunkRevision == 9),
            "same-chunk changes must share the transaction chunk transition");
    }

    private static void ProjectsPrivateState()
    {
        var command = Command(new ContainerTransferAction(
            ObjectReference with
            {
                ExpectedObjectRevision = 0,
                ExpectedChunkRevision = 8,
            },
            0,
            ContainerTransferDirection.Deposit,
            0,
            0,
            1));
        var result = Result();
        var player = WorldActionProtocolAdapter.ToPrivatePlayerState(
            12,
            22,
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            77,
            command,
            result);
        CheckAssert.True(player is not null,
            "changed requester gameplay must produce private state");
        CheckAssert.True(player!.Flags.HasFlag(PlayerStateFlags.Actor),
            "actor changes must be named in the delta");
        CheckAssert.True(player.Flags.HasFlag(PlayerStateFlags.Inventory),
            "inventory changes must be named in the delta");
        CheckAssert.Equal(2u, player.BaselineInventoryRevision,
            "the private delta must depend on the command inventory revision");
        CheckAssert.Equal(28, player.InventorySlots.Count,
            "the legal full-slot delta must include the bounded player inventory");
        CheckAssert.Equal(137, player.MaximumHealth,
            "world actions must preserve the authoritative combat maximum");
        CheckAssert.Equal(31, player.AttackExperience,
            "world actions must preserve attack progression");
        CheckAssert.Equal(32, player.StrengthExperience,
            "world actions must preserve strength progression");
        CheckAssert.Equal(33, player.DefenceExperience,
            "world actions must preserve defence progression");
        CheckAssert.Equal(CombatStance.Defensive, player.CombatStance,
            "world actions must preserve the authoritative stance");
        CheckAssert.Equal(CombatLifeState.Dead, player.LifeState,
            "world actions must preserve the authoritative life state");
        CheckAssert.Equal(444ul, player.RespawnTick,
            "world actions must preserve the authoritative respawn tick");
        CheckAssert.True(
            player.CombatStatusFlags.HasFlag(
                IslandRpg.Protocol.CombatStatusFlags.Slowed),
            "world actions must preserve active authoritative statuses");

        var container = WorldActionProtocolAdapter.ToPrivateContainerBaseline(
            13, 23, command, result);
        CheckAssert.True(container is not null,
            "an accepted container result must produce a requester baseline");
        CheckAssert.Equal(ObjectId, container!.Container.ObjectId,
            "the private baseline must identify the same stable object");
        CheckAssert.Equal(1u, container.Container.ExpectedObjectRevision,
            "the private baseline must carry the current container revision");
        CheckAssert.Equal(9u, container.Container.ExpectedChunkRevision,
            "the private baseline must carry the current chunk revision");
        CheckAssert.Equal("slime_gel", container.Slots[0].ItemId,
            "only the private baseline may expose container contents");
        CheckAssert.True(container.IsBaseline,
            "server container responses should be self-contained baselines");
        _ = ReliableProtocolCodec.Encode(container);

        CheckAssert.True(
            WorldActionProtocolAdapter.ToPrivateContainerBaseline(
                14, 24, command,
                result with
                {
                    Status = WorldTransactionStatus.AccessDenied,
                    Container = null,
                }) is null,
            "a rejected access attempt must not disclose private contents");
    }

    private static void MapsEveryWorldRejection()
    {
        foreach (var status in Enum.GetValues<WorldTransactionStatus>())
        {
            var mapped = WorldActionProtocolAdapter.MapRejection(status);
            CheckAssert.Equal(
                status == WorldTransactionStatus.Accepted,
                mapped == CommandRejectionCode.None,
                $"{status} must map to a valid accepted/rejected wire result");
        }

        CheckAssert.Equal(CommandRejectionCode.OutOfOrder,
            WorldActionProtocolAdapter.MapRejection(
                WorldTransactionStatus.StaleContainerRevision),
            "stale revisions should tell clients to refresh and retry");
        CheckAssert.Equal(CommandRejectionCode.NotAuthorized,
            WorldActionProtocolAdapter.MapRejection(
                WorldTransactionStatus.AccessDenied),
            "access denial must not be reported as an impossible world state");
        CheckAssert.Equal(CommandRejectionCode.Impossible,
            WorldActionProtocolAdapter.MapRejection(
                WorldTransactionStatus.OutOfRange),
            "valid actions that fail world rules should be impossible");
    }

    private static WorldGameplayIntent ToIntent(IActionCommandPayload payload)
    {
        _ = WorldActionProtocolAdapter.TryToWorldIntent(
            Command(payload), out var intent);
        return intent!;
    }

    private static ActionCommandMessage Command(IActionCommandPayload payload) =>
        new(1, 2, CommandId, 3, 2, payload);

    private static IntentResult Intent(WorldTransactionResult transaction) =>
        new(IntentStatus.Accepted, 1, null)
        {
            CommandId = CommandId,
            ActorRevision = transaction.ActorRevision,
            InventoryRevision = transaction.InventoryRevision,
            Gameplay = transaction.Gameplay!.Value,
            WorldTransaction = transaction,
        };

    private static WorldTransactionResult Result()
    {
        var chunk = new WorldChunkKey(2, -3, 0);
        var state = new AuthoritativeWorldObjectSnapshot(
            ObjectId,
            "storage_chest",
            new Vector2(65, -95),
            chunk,
            1,
            1,
            0,
            10,
            20,
            "private-owner",
            null,
            true,
            null,
            0,
            1,
            WorldGateAccessState.None);
        var slots = Enumerable.Range(0, 4)
            .Select(static slot => new WorldContainerSlotSnapshot(
                slot,
                slot == 0 ? "slime_gel" : null,
                slot == 0 ? 2 : 0,
                slot == 0 ? "private-owner" : null))
            .ToImmutableArray();
        var inventory = Enumerable.Range(0, 28)
            .Select(static slot => new InventorySlotSnapshot(
                slot,
                slot == 0 ? "logs" : null,
                slot == 0 ? 1 : 0))
            .ToImmutableArray();
        var gameplay = new PlayerGameplaySnapshot(
            4,
            90,
            75,
            0,
            10,
            20,
            new PlayerInventorySnapshot(3, inventory),
            MaximumHealth: 137,
            AttackExperience: 31,
            StrengthExperience: 32,
            DefenceExperience: 33,
            CombatStance: MeleeCombatStance.Defensive,
            LifeState: ActorLifeState.Dead,
            RespawnAvailableTick: 444,
            CombatStatus: new SlimeVictimStatus(SlowedUntil: 10));
        return new WorldTransactionResult(
            CommandId,
            WorldTransactionStatus.Accepted,
            4,
            3,
            [new(
                WorldObjectChangeKind.Added,
                ObjectId,
                chunk,
                0,
                1,
                state)],
            [new(chunk, 8, 9)],
            gameplay,
            new WorldContainerSnapshot(
                ObjectId, chunk, 9, 1, 1, "storage_chest", true, slots));
    }
}
