using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenTK.Mathematics;

namespace IslandRpg.Assets;

internal sealed record GenieBuildingAnnex(
    short UnitId,
    Vector2 Offset);

internal sealed record GenieUnitGeometry(
    short UnitId,
    string InternalName,
    Vector2 CollisionRadius,
    Vector2 PlacementClearance,
    IReadOnlyList<GenieBuildingAnnex> Annexes)
{
    public Vector2 CollisionSize => CollisionRadius * 2;
    public Vector2 PlacementSize => PlacementClearance * 2;
}

internal static class GenieUnitMetadataReader
{
    // AoE2 HD stores four BuildingAnnex records immediately before this
    // fixed 26-byte BuildingUnit tail. Each annex is an int16 unit id and two
    // float offsets. This is DAT metadata, not information inferred from the
    // rendered SLP pixels.
    private const int HdBuildingTailBytes = 26;
    private const int AnnexCount = 4;
    private const int AnnexBytes = 10;

    // These offsets belong to the fixed UnitObject prefix used by the HD
    // edition. Variable attack, armour and damage arrays occur later, so the
    // collision and placement fields can be read without deserializing them.
    private const int UnitIdOffset = 2;
    private const int UnitClassOffset = 8;
    private const int CollisionRadiusXOffset = 26;
    private const int CollisionRadiusYOffset = 30;
    private const int PlacementClearanceXOffset = 61;
    private const int PlacementClearanceYOffset = 65;
    private const short GateUnitClass = 39;

    public static IReadOnlyDictionary<short, GenieUnitGeometry> ReadHdUnits(
        string datPath,
        IReadOnlyDictionary<short, string> requestedUnits)
    {
        if (requestedUnits.Count == 0)
            return new Dictionary<short, GenieUnitGeometry>();

        return ReadHdUnits(Decompress(datPath), requestedUnits);
    }

    internal static IReadOnlyDictionary<short, GenieUnitGeometry> ReadHdUnits(
        byte[] data,
        IReadOnlyDictionary<short, string> requestedUnits)
    {
        var result = new Dictionary<short, GenieUnitGeometry>();
        foreach (var requested in requestedUnits)
            if (TryReadNamedUnit(
                    data, requested.Key, requested.Value, out var geometry))
                result[requested.Key] = geometry;
        return result;
    }

    private static bool TryReadNamedUnit(
        byte[] data,
        short unitId,
        string internalName,
        out GenieUnitGeometry geometry)
    {
        geometry = null!;
        var name = Encoding.ASCII.GetBytes(internalName);
        var search = 0;
        while (search <= data.Length - name.Length)
        {
            var relative = data.AsSpan(search).IndexOf(name);
            if (relative < 0) return false;
            var nameOffset = search + relative;
            search = nameOffset + name.Length;

            // A graphic, effect or language record may contain the same
            // ASCII name. Accept it only when a structurally valid HD
            // UnitObject prefix with the requested id and gate class leads
            // directly to this length-prefixed unit name.
            if (!TryFindUnitStart(
                    data, nameOffset, name.Length + 1, unitId,
                    out var unitStart))
                continue;
            if (!TryFindNextUnitStart(
                    data, search, unitId, out var nextUnitStart))
                continue;

            var annexStart = nextUnitStart -
                HdBuildingTailBytes - AnnexCount * AnnexBytes;
            if (annexStart <= nameOffset ||
                !TryReadAnnexes(data, annexStart, out var annexes))
                continue;

            var radius = new Vector2(
                F32(data, unitStart + CollisionRadiusXOffset),
                F32(data, unitStart + CollisionRadiusYOffset));
            var clearance = new Vector2(
                F32(data, unitStart + PlacementClearanceXOffset),
                F32(data, unitStart + PlacementClearanceYOffset));
            if (!ValidSize(radius) || !ValidSize(clearance))
                continue;

            geometry = new(
                unitId, internalName, radius, clearance, annexes);
            return true;
        }
        return false;
    }

    private static bool TryFindUnitStart(
        byte[] data,
        int nameOffset,
        int storedNameLength,
        short unitId,
        out int unitStart)
    {
        var minimum = Math.Max(0, nameOffset - 768);
        for (var candidate = nameOffset - 2;
             candidate >= minimum;
             candidate--)
        {
            if (candidate + PlacementClearanceYOffset + 4 > data.Length ||
                U16(data, candidate) != storedNameLength ||
                I16(data, candidate + UnitIdOffset) != unitId ||
                I16(data, candidate + UnitClassOffset) != GateUnitClass)
                continue;
            var radius = new Vector2(
                F32(data, candidate + CollisionRadiusXOffset),
                F32(data, candidate + CollisionRadiusYOffset));
            var clearance = new Vector2(
                F32(data, candidate + PlacementClearanceXOffset),
                F32(data, candidate + PlacementClearanceYOffset));
            if (!ValidSize(radius) || !ValidSize(clearance)) continue;
            unitStart = candidate;
            return true;
        }
        unitStart = -1;
        return false;
    }

    private static bool TryFindNextUnitStart(
        byte[] data,
        int searchStart,
        short currentUnitId,
        out int nextUnitStart)
    {
        var maximum = Math.Min(
            data.Length - PlacementClearanceYOffset - 4,
            searchStart + 4096);
        for (var candidate = searchStart; candidate <= maximum; candidate++)
        {
            var nameLength = U16(data, candidate);
            if (nameLength is 0 or > 128 ||
                I16(data, candidate + UnitIdOffset) != currentUnitId + 1)
                continue;
            var radius = new Vector2(
                F32(data, candidate + CollisionRadiusXOffset),
                F32(data, candidate + CollisionRadiusYOffset));
            if (!ValidSize(radius)) continue;
            nextUnitStart = candidate;
            return true;
        }
        nextUnitStart = -1;
        return false;
    }

    private static bool TryReadAnnexes(
        byte[] data,
        int start,
        out IReadOnlyList<GenieBuildingAnnex> annexes)
    {
        var result = new GenieBuildingAnnex[AnnexCount];
        for (var index = 0; index < AnnexCount; index++)
        {
            var offset = start + index * AnnexBytes;
            if (offset < 0 || offset + AnnexBytes > data.Length)
            {
                annexes = [];
                return false;
            }
            var position = new Vector2(
                F32(data, offset + 2), F32(data, offset + 6));
            if (!float.IsFinite(position.X) ||
                !float.IsFinite(position.Y) ||
                MathF.Abs(position.X) > 32 ||
                MathF.Abs(position.Y) > 32)
            {
                annexes = [];
                return false;
            }
            result[index] = new(I16(data, offset), position);
        }
        annexes = result;
        return true;
    }

    private static bool ValidSize(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        value.X > 0 && value.Y > 0 && value.X <= 32 && value.Y <= 32;

    private static byte[] Decompress(string path)
    {
        using var input = File.OpenRead(path);
        using var deflate = new DeflateStream(
            input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static short I16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));

    private static float F32(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));
}
