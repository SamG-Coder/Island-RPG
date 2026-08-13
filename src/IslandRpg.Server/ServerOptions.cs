using System.Net;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

public sealed record ServerOptions(
    IPAddress ListenAddress,
    ushort ListenPort,
    Guid WorldId,
    long WorldSeed,
    string BuildVersion,
    string ContentVersion,
    int MaximumClients)
{
    public const ushort DefaultPort = 38_740;

    /// <summary>
    /// Trusted host bootstrap used by scenarios and tests. Network clients
    /// cannot provide these values; production servers begin with an empty bag.
    /// </summary>
    public IReadOnlyList<InitialInventoryItem> StartingInventory { get; init; } =
        Array.Empty<InitialInventoryItem>();

    public float StartingHunger { get; init; } = 100f;

    public static ServerOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var listenAddress = IPAddress.Loopback;
        var listenPort = DefaultPort;
        var worldId = Guid.NewGuid();
        var worldSeed = Random.Shared.NextInt64();
        var buildVersion = "0.3.0";
        var contentVersion = "base";
        var maximumClients = 64;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            var value = index + 1 < args.Length ? args[index + 1] : null;
            switch (option)
            {
                case "--listen":
                    value = RequireValue(option, value);
                    ParseEndpoint(value, out listenAddress, out listenPort);
                    index++;
                    break;
                case "--world-id":
                    value = RequireValue(option, value);
                    if (!Guid.TryParse(value, out worldId) || worldId == Guid.Empty)
                    {
                        throw new ArgumentException("--world-id must be a non-empty GUID.");
                    }

                    index++;
                    break;
                case "--world-seed":
                    value = RequireValue(option, value);
                    if (!long.TryParse(value, out worldSeed))
                    {
                        throw new ArgumentException("--world-seed must be a signed 64-bit integer.");
                    }

                    index++;
                    break;
                case "--build-version":
                    buildVersion = RequireValue(option, value);
                    index++;
                    break;
                case "--content-version":
                    contentVersion = RequireValue(option, value);
                    index++;
                    break;
                case "--max-clients":
                    value = RequireValue(option, value);
                    if (!int.TryParse(value, out maximumClients) || maximumClients is < 1 or > 1_024)
                    {
                        throw new ArgumentException("--max-clients must be between 1 and 1024.");
                    }

                    index++;
                    break;
                case "--help":
                case "-h":
                    throw new ShowHelpException();
                default:
                    throw new ArgumentException($"Unknown server option '{option}'.");
            }
        }

        ValidateVersion(buildVersion, "--build-version");
        ValidateVersion(contentVersion, "--content-version");
        return new ServerOptions(
            listenAddress,
            listenPort,
            worldId,
            worldSeed,
            buildVersion,
            contentVersion,
            maximumClients);
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Island RPG dedicated server");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  IslandRpg.Server [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --listen <address:port>   Listen endpoint (default 127.0.0.1:38740)");
        writer.WriteLine("  --world-id <guid>         Persistent world identity (default random)");
        writer.WriteLine("  --world-seed <long>       Deterministic world seed (default random)");
        writer.WriteLine("  --build-version <value>   Required client build (default 0.3.0)");
        writer.WriteLine("  --content-version <value> Required content version (default base)");
        writer.WriteLine("  --max-clients <count>     Concurrent connections, 1-1024 (default 64)");
        writer.WriteLine("  -h, --help                Show this help");
    }

    private static string RequireValue(string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return value;
    }

    private static void ParseEndpoint(string value, out IPAddress address, out ushort port)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !ushort.TryParse(value[(separator + 1)..], out port) || port == 0)
        {
            throw new ArgumentException("--listen must use address:port with a non-zero port.");
        }

        var host = value[..separator].Trim('[', ']');
        if (host == "*")
        {
            address = IPAddress.Any;
            return;
        }

        if (!IPAddress.TryParse(host, out var parsed))
        {
            throw new ArgumentException("--listen currently requires a numeric IPv4 or IPv6 address.");
        }

        address = parsed;
    }

    private static void ValidateVersion(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{option} must contain 1-64 printable characters.");
        }
    }
}

internal sealed class ShowHelpException : Exception;
