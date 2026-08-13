namespace IslandRpg.Gameplay;

using IslandRpg.Assets;
using OpenTK.Mathematics;

internal static class GateGeometryCatalog
{
    internal sealed record OrientationGeometry(
        Vector2 CollisionRadius,
        Vector2 PlacementClearance,
        IReadOnlyList<Vector2> AnnexOffsets)
    {
        public Vector2 CollisionSize => CollisionRadius * 2;
        public Vector2 PlacementSize => PlacementClearance * 2;
    }

    private static IReadOnlyDictionary<(int Tier, int Rotation),
        OrientationGeometry> _geometry = FallbackGeometry();

    public static void LoadGeometry(string datPath)
    {
        if (!File.Exists(datPath)) return;
        var requests = new Dictionary<short, string>
        {
            [487] = "GTAX2", [488] = "GTAX3",
            [490] = "GTBX2", [491] = "GTBX3",
            [665] = "GTCX2", [666] = "GTCX3",
            [673] = "GTDX2", [674] = "GTDX3"
        };
        var parsed = GenieUnitMetadataReader.ReadHdUnits(datPath, requests);
        var result = FallbackGeometry().ToDictionary();
        foreach (var request in requests)
        {
            if (!parsed.TryGetValue(request.Key, out var value)) continue;
            var tier = request.Value[^1] - '0';
            var rotation = request.Value[2] - 'A';
            result[(tier, rotation)] = new(
                value.CollisionRadius,
                value.PlacementClearance,
                value.Annexes.Select(annex => annex.Offset)
                    .Where(offset => offset.LengthSquared > .0001f)
                    .ToArray());
        }
        _geometry = result;
    }

    public static OrientationGeometry Geometry(
        GateDefinition gate, int rotation) =>
        _geometry.TryGetValue((gate.Tier, NormalizeRotation(rotation)),
            out var value)
            ? value
            : FallbackGeometry()[(gate.Tier, NormalizeRotation(rotation))];

    private static int NormalizeRotation(int rotation) =>
        rotation < 0 ? 0 : rotation % 4;

    private static IReadOnlyDictionary<(int Tier, int Rotation),
        OrientationGeometry> FallbackGeometry()
    {
        var result = new Dictionary<(int, int), OrientationGeometry>();
        foreach (var tier in new[] { 2, 3 })
        {
            result[(tier, 0)] = new(
                new(2, .5f), new(2, .5f),
                [new(-1.5f, 0), new(1.5f, 0)]);
            result[(tier, 1)] = new(
                new(.5f, 2), new(.5f, 2),
                [new(0, 1.5f), new(0, -1.5f)]);
            result[(tier, 2)] = new(
                Vector2.One, new(2, 2), []);
            result[(tier, 3)] = new(
                Vector2.One, new(2, 2), []);
        }
        return result;
    }
}
