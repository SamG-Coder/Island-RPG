using IslandRpg.Server;

namespace IslandRpg.NetworkingChecks;

internal static class OrderedPublicationsChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("publications wait for the earliest command receipt",
            EarliestBlockedTicketPreservesCommitOrder);
        checks.Add("discarded requester publication unblocks autonomous work",
            NoOpReleaseUnblocksFollowingPublication);
    }

    private static void EarliestBlockedTicketPreservesCommitOrder()
    {
        var publications = new OrderedPublications();
        var observed = new List<string>();
        var command = publications.Reserve();
        publications.Publish(() => observed.Add("autonomous"));

        CheckAssert.Equal(0, observed.Count,
            "a ready autonomous publication must not overtake a committed command");
        CheckAssert.Equal(2, publications.PendingCount,
            "both commit-order positions must remain retained while the head waits");

        publications.Release(command, () => observed.Add("command"));
        CheckAssert.SequenceEqual(
            new[] { "command", "autonomous" }, observed,
            "releasing the requester receipt must drain contiguous commits in order");
        CheckAssert.Equal(0, publications.PendingCount,
            "a completed publication prefix must not remain retained");
    }

    private static void NoOpReleaseUnblocksFollowingPublication()
    {
        var publications = new OrderedPublications();
        var observed = new List<string>();
        var disconnectedRequester = publications.Reserve();
        publications.Publish(() => observed.Add("next"));

        publications.Release(disconnectedRequester);

        CheckAssert.SequenceEqual(new[] { "next" }, observed,
            "queue failure or disconnect must discard its mutation boundary without deadlock");
        CheckAssert.Equal(0, publications.PendingCount,
            "discarding the head must drain and release later ready work");
    }
}
