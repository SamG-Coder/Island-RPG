using IslandRpg.NetworkingChecks;

var checks = new CheckRunner();

// Production protocol and authoritative-session checks are registered in
// separate files as those projects land. Keeping the runner independent from
// rendering makes this suite suitable for CI and dedicated-server builds.
checks.Add("check runner executes deterministic assertions", () =>
{
    CheckAssert.True(true, "the networking check runner must execute checks");
    CheckAssert.False(false, "false assertions must remain false");
    CheckAssert.Equal(2, 1 + 1, "basic equality assertions must be deterministic");
    CheckAssert.SequenceEqual(
        new byte[] { 1, 2, 3 },
        new byte[] { 1, 2, 3 },
        "byte sequence assertions must preserve order");
});

AuthoritativeSessionChecks.Register(checks);
AuthoritativeNavigationChecks.Register(checks);
ProtocolChecks.Register(checks);
UdpSnapshotTransportChecks.Register(checks);
NetworkGameClientStateChecks.Register(checks);
ClientWorldStateChecks.Register(checks);
ItemContainerChecks.Register(checks);
WorldRuleChecks.Register(checks);
WorldTransactionChecks.Register(checks);
SessionWorldTransactionChecks.Register(checks);
ServerWorldActionAdapterChecks.Register(checks);
ServerCheckpointChecks.Register(checks);
LoopbackChecks.Register(checks);
ServerRestartLoopbackChecks.Register(checks);

return await checks.RunAsync(CancellationToken.None);
