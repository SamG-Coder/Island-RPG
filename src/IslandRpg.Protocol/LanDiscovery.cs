using System.Globalization;
using System.Text;

namespace IslandRpg.Protocol;

/// <summary>
/// Out-of-band LAN beacon. This is not a reliable game frame and does not
/// change <see cref="ProtocolConstants.CurrentVersion"/>.
/// </summary>
public readonly record struct LanDiscoveryBeacon(
    ushort GamePort,
    Guid WorldId,
    long WorldSeed,
    bool IslandStart,
    int PlayerCount,
    int MaximumClients,
    string Name)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name)
            ? IslandStart ? "Shore world" : "Open world"
            : Name.Trim();
}

public static class LanDiscovery
{
    public const ushort Port = 38_741;
    public const string Prefix = "IRPG1";
    public const int MaximumNameCharacters = 48;
    public const int MaximumDatagramBytes = 256;

    public static byte[] Encode(LanDiscoveryBeacon beacon)
    {
        var name = (beacon.Name ?? string.Empty)
            .Replace('|', '/')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (name.Length > MaximumNameCharacters)
            name = name[..MaximumNameCharacters];
        var line = string.Join('|',
            Prefix,
            beacon.GamePort.ToString(CultureInfo.InvariantCulture),
            beacon.WorldId.ToString("N"),
            beacon.WorldSeed.ToString(CultureInfo.InvariantCulture),
            beacon.IslandStart ? "1" : "0",
            Math.Max(0, beacon.PlayerCount)
                .ToString(CultureInfo.InvariantCulture),
            Math.Max(1, beacon.MaximumClients)
                .ToString(CultureInfo.InvariantCulture),
            name);
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > MaximumDatagramBytes)
            throw new ProtocolException("LAN discovery beacon is too large.");
        return bytes;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> data, out LanDiscoveryBeacon beacon)
    {
        beacon = default;
        if (data.Length is 0 or > MaximumDatagramBytes)
            return false;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 8 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            !ushort.TryParse(
                parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var port) ||
            port == 0 ||
            !Guid.TryParseExact(parts[2], "N", out var worldId) ||
            worldId == Guid.Empty ||
            !long.TryParse(
                parts[3], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var seed) ||
            !int.TryParse(
                parts[5], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var players) ||
            !int.TryParse(
                parts[6], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var maximum) ||
            maximum < 1)
            return false;
        var name = string.Join('|', parts.Skip(7)).Trim();
        if (name.Length > MaximumNameCharacters)
            name = name[..MaximumNameCharacters];
        beacon = new(
            port,
            worldId,
            seed,
            parts[4] == "1",
            Math.Max(0, players),
            maximum,
            name);
        return true;
    }
}
