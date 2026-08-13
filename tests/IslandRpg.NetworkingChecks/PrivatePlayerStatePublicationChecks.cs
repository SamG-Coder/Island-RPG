using IslandRpg.Protocol;
using IslandRpg.Server;

namespace IslandRpg.NetworkingChecks;

internal static class PrivatePlayerStatePublicationChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "private player publication never rewinds either revision section",
            NeverRewindsEitherRevisionSection);
    }

    private static void NeverRewindsEitherRevisionSection()
    {
        var highWater = new PrivatePlayerStateHighWater();
        var baseline = highWater.Project(State(
            1, 10, 20,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory));
        CheckAssert.True(
            baseline!.Flags.HasFlag(PlayerStateFlags.Baseline),
            "the first private publication must remain a reconnect-safe baseline");
        ReliableProtocolCodec.Encode(baseline);
        highWater.Observe(baseline);

        // Autonomous combat advances actor state while a command outcome
        // captured at actor revision 10 is still waiting to publish.
        var autonomous = highWater.Project(State(
            2, 11, 20,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory));
        CheckAssert.Equal(PlayerStateFlags.Actor, autonomous!.Flags,
            "unchanged inventory must be omitted from autonomous publication");
        CheckAssert.Equal(10u, autonomous.BaselineActorRevision,
            "autonomous actor state must depend on the queued actor revision");
        CheckAssert.Equal(20u, autonomous.BaselineInventoryRevision,
            "omitted inventory must retain the queued inventory revision");
        ReliableProtocolCodec.Encode(autonomous);
        highWater.Observe(autonomous);

        var olderCommandResult = highWater.Project(State(
            3, 10, 21,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory));
        CheckAssert.Equal(PlayerStateFlags.Inventory,
            olderCommandResult!.Flags,
            "an older command actor section must be stripped, not rewound");
        CheckAssert.Equal(11u, olderCommandResult.ActorRevision,
            "inventory-only publication must retain current actor high-water");
        CheckAssert.Equal(11u, olderCommandResult.BaselineActorRevision,
            "omitted actor state must be internally self-consistent");
        CheckAssert.Equal(20u, olderCommandResult.BaselineInventoryRevision,
            "new inventory must rebase to the exact queued inventory revision");
        CheckAssert.Equal(21u, olderCommandResult.InventoryRevision,
            "the command's newer inventory must still reach the client");
        ReliableProtocolCodec.Encode(olderCommandResult);
        highWater.Observe(olderCommandResult);

        CheckAssert.True(highWater.Project(State(
                4, 10, 20,
                PlayerStateFlags.Baseline |
                PlayerStateFlags.Actor |
                PlayerStateFlags.Inventory)) is null,
            "a snapshot stale in both independent sections must be suppressed");
        CheckAssert.Equal(11u, highWater.ActorRevision,
            "the actor high-water must remain monotonic");
        CheckAssert.Equal(21u, highWater.InventoryRevision,
            "the inventory high-water must remain monotonic");
    }

    private static PlayerStateMessage State(
        ulong sequence,
        uint actorRevision,
        uint inventoryRevision,
        PlayerStateFlags flags) => new(
        sequence,
        100 + sequence,
        Guid.Parse("d1790b1f-762d-5d5e-bb7f-da0a48cdb323"),
        101,
        flags,
        0,
        0,
        actorRevision,
        inventoryRevision,
        100,
        100,
        0,
        0,
        0,
        Enumerable.Range(0, ProtocolLimits.PlayerInventorySlots)
            .Select(static slot => new InventorySlotState(
                slot, string.Empty, 0))
            .ToArray());
}
