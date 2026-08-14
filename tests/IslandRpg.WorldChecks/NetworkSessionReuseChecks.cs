using IslandRpg.Gameplay;
using IslandRpg.Persistence;

internal static class NetworkSessionReuseChecks
{
    public static void Run()
    {
        DifferentLocalPlayersDoNotShareReconnect();
        SameLocalPlayerReusesHostedSession();
        RepositoryKeepsSessionsPerPlayer();
        LegacySessionIsClaimedOnlyByMatchingPlayer();
        Console.WriteLine(
            "Network session reuse checks passed: selected characters join independently.");
    }

    private static void DifferentLocalPlayersDoNotShareReconnect()
    {
        var alice = Session("alice", "Alice");
        Assert(
            NetworkSessionReuse.CanReconnect(
                alice, "alice", "127.0.0.1", 38_740, alice.WorldId) &&
            !NetworkSessionReuse.CanReconnect(
                alice, "bob", "127.0.0.1", 38_740, alice.WorldId),
            "a second selected character must not reuse the first player's reconnect token");
    }

    private static void SameLocalPlayerReusesHostedSession()
    {
        var world = Guid.NewGuid();
        var session = Session("alice", "Alice", world);
        Assert(
            NetworkSessionReuse.CanReconnect(
                session, "alice", "127.0.0.1", 38_740, world) &&
            !NetworkSessionReuse.CanReconnect(
                session, "alice", "127.0.0.1", 38_740, Guid.NewGuid()),
            "the same selected character may reconnect only to the same hosted world");
    }

    private static void RepositoryKeepsSessionsPerPlayer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "IslandRpg",
            "NetworkSessionReuseChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var saves = new GameSaveRepository(root);
            var alice = saves.CreatePlayer(
                "Alice", EntityGender.Female, 2, 1);
            var bob = saves.CreatePlayer(
                "Bob", EntityGender.Male, 2, 3);
            var world = Guid.NewGuid();
            saves.SaveNetworkSession(new NetworkSessionRecord(
                "127.0.0.1",
                38_740,
                world,
                Guid.NewGuid(),
                "alice-token",
                alice.Name,
                alice.Gender,
                alice.TeamColor,
                LocalPlayerId: alice.Id));
            saves.SaveNetworkSession(new NetworkSessionRecord(
                "127.0.0.1",
                38_740,
                world,
                Guid.NewGuid(),
                "bob-token",
                bob.Name,
                bob.Gender,
                bob.TeamColor,
                LocalPlayerId: bob.Id));

            var loadedAlice = saves.LoadNetworkSession(alice.Id);
            var loadedBob = saves.LoadNetworkSession(bob.Id);
            Assert(
                loadedAlice?.ReconnectToken == "alice-token" &&
                loadedBob?.ReconnectToken == "bob-token" &&
                NetworkSessionReuse.CanReconnect(
                    loadedAlice, alice.Id, "127.0.0.1", 38_740, world) &&
                !NetworkSessionReuse.CanReconnect(
                    loadedAlice, bob.Id, "127.0.0.1", 38_740, world),
                "each selected character must keep its own hosted reconnect token");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void LegacySessionIsClaimedOnlyByMatchingPlayer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "IslandRpg",
            "NetworkSessionReuseChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var saves = new GameSaveRepository(root);
            var alice = saves.CreatePlayer(
                "Alice", EntityGender.Female, 2, 1);
            var bob = saves.CreatePlayer(
                "Bob", EntityGender.Male, 2, 3);
            var world = Guid.NewGuid();
            File.WriteAllText(
                saves.NetworkSessionPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new NetworkSessionRecord(
                        "127.0.0.1",
                        38_740,
                        world,
                        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        "legacy-token",
                        "Alice",
                        EntityGender.Female,
                        1)));

            var bobClaim = saves.LoadNetworkSession(bob.Id);
            var aliceClaim = saves.LoadNetworkSession(alice.Id);
            Assert(
                bobClaim is null &&
                aliceClaim?.ReconnectToken == "legacy-token" &&
                aliceClaim.LocalPlayerId == alice.Id &&
                !File.Exists(saves.NetworkSessionPath),
                "a leftover global session may be claimed only by the matching selected character");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NetworkSessionRecord Session(
        string localPlayerId,
        string name,
        Guid worldId = default) =>
        new(
            "127.0.0.1",
            38_740,
            worldId == Guid.Empty ? Guid.Parse(
                "11111111-1111-1111-1111-111111111111") : worldId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            $"{localPlayerId}-token",
            name,
            EntityGender.Male,
            1,
            LocalPlayerId: localPlayerId);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
