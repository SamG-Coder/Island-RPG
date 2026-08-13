using IslandRpg.Server;

try
{
    var options = ServerOptions.Parse(args);
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    await using var server = new DedicatedServer(options);
    await server.RunAsync(shutdown.Token);
    return 0;
}
catch (ShowHelpException)
{
    ServerOptions.PrintUsage(Console.Out);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Server failed: {exception.Message}");
    return 1;
}
