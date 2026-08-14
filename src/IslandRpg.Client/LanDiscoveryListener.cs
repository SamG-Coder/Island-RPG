using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IslandRpg.Protocol;

namespace IslandRpg.Client;

public readonly record struct DiscoveredLanServer(
    string Host,
    LanDiscoveryBeacon Beacon,
    long LastSeenTimestamp);

/// <summary>
/// Listens for LAN world beacons. A failed bind leaves the list empty so
/// the join screen still works with a typed address.
/// </summary>
public sealed class LanDiscoveryListener : IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(4);

    private readonly ConcurrentDictionary<string, DiscoveredLanServer> _servers = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Socket? _socket;
    private readonly Task _run;
    private int _disposed;

    public LanDiscoveryListener()
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, LanDiscovery.Port));
        }
        catch (Exception)
        {
            socket?.Dispose();
            socket = null;
        }

        _socket = socket;
        _run = socket is null
            ? Task.CompletedTask
            : Task.Run(() => ReceiveAsync(socket, _lifetime.Token));
    }

    public IReadOnlyList<DiscoveredLanServer> Snapshot()
    {
        var cutoff = Stopwatch.GetTimestamp() -
                     (long)(StaleAfter.TotalSeconds * Stopwatch.Frequency);
        var result = new List<DiscoveredLanServer>();
        foreach (var pair in _servers)
        {
            if (pair.Value.LastSeenTimestamp < cutoff)
            {
                _servers.TryRemove(pair.Key, out _);
                continue;
            }

            result.Add(pair.Value);
        }

        return result
            .OrderBy(value => value.Beacon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Host, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Key(string host, int port) =>
        $"{host.Trim()}:{port}";

    private async Task ReceiveAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[LanDiscovery.MaximumDatagramBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, remote, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!LanDiscovery.TryDecode(
                        buffer.AsSpan(0, received.ReceivedBytes),
                        out var beacon))
                    continue;
                var host = HostFrom(received.RemoteEndPoint);
                if (string.IsNullOrWhiteSpace(host)) continue;
                _servers[Key(host, beacon.GamePort)] = new(
                    host, beacon, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception)
        {
        }
    }

    private static string HostFrom(EndPoint? endpoint)
    {
        if (endpoint is not IPEndPoint ip)
            return "";
        var address = ip.Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any))
            return "127.0.0.1";
        return address.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            _socket?.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _run.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
    }
}
