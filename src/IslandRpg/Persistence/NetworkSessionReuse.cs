namespace IslandRpg.Persistence;

/// <summary>
/// A reconnect token belongs to one local character. Sharing the last
/// session across every join makes a second client take over the first
/// player instead of entering as the selected adventurer.
/// </summary>
internal static class NetworkSessionReuse
{
    public static bool CanReconnect(
        NetworkSessionRecord? session,
        string? localPlayerId,
        string host,
        int port,
        Guid worldId)
    {
        if (session is null ||
            string.IsNullOrWhiteSpace(localPlayerId) ||
            session.PlayerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(session.ReconnectToken) ||
            session.Port != port)
            return false;
        if (!string.Equals(
                NormalizeHost(session.Host),
                NormalizeHost(host),
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (worldId != Guid.Empty &&
            session.WorldId != Guid.Empty &&
            session.WorldId != worldId)
            return false;
        if (!string.IsNullOrWhiteSpace(session.LocalPlayerId))
            return string.Equals(
                session.LocalPlayerId,
                localPlayerId,
                StringComparison.Ordinal);
        return false;
    }

    public static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (host is "*" or "" or "0.0.0.0" or "::")
            return "127.0.0.1";
        return host;
    }
}
