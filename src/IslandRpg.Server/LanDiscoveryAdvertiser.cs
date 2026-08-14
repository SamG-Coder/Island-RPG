using System.Net;
using System.Net.Sockets;
using IslandRpg.Protocol;

namespace IslandRpg.Server;

/// <summary>
/// Broadcasts a small UDP beacon so LAN clients can list this world.
/// Failures are ignored: hosting must not depend on discovery.
/// </summary>
internal sealed class LanDiscoveryAdvertiser : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1_500);
    private readonly Func<LanDiscoveryBeacon> _beacon;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _run;
    private int _disposed;

    public LanDiscoveryAdvertiser(Func<LanDiscoveryBeacon> beacon)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        _beacon = beacon;
        _run = Task.Run(() => RunAsync(_lifetime.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.EnableBroadcast = true;
            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            var target = new IPEndPoint(
                IPAddress.Broadcast, LanDiscovery.Port);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var payload = LanDiscovery.Encode(_beacon());
                    await socket.SendToAsync(
                        payload, SocketFlags.None, target, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                }

                try
                {
                    await Task.Delay(Interval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
    }
}
