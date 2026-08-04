namespace IslandRpg.Gameplay;

using IslandRpg.Assets;
using OpenTK.Mathematics;

internal sealed record GateDefinition(
    string ItemId,
    string Name,
    string Architecture,
    int Tier,
    string GateGraphicName,
    short GateGraphicId,
    string GateShadowGraphicName,
    short GateShadowGraphicId,
    string OpenGateGraphicName,
    short OpenGateGraphicId,
    string OpenGateShadowGraphicName,
    short OpenGateShadowGraphicId,
    string? SideWallGraphicName,
    short SideWallGraphicId,
    string? SideWallShadowGraphicName,
    short SideWallShadowGraphicId,
    string ConstructionGraphicName,
    short ConstructionGraphicId,
    string? ConstructionShadowGraphicName,
    short ConstructionShadowGraphicId,
    int MaximumHealth,
    int RequiredLevel,
    int LogCost);

internal sealed record GateOrientationDefinition(
    string GateGraphicName, short GateGraphicId,
    string GateShadowGraphicName, short GateShadowGraphicId,
    string OpenGateGraphicName, short OpenGateGraphicId,
    string OpenGateShadowGraphicName, short OpenGateShadowGraphicId,
    string? SideWallGraphicName, short SideWallGraphicId,
    string? SideWallShadowGraphicName, short SideWallShadowGraphicId,
    string ConstructionGraphicName, short ConstructionGraphicId,
    string? ConstructionShadowGraphicName, short ConstructionShadowGraphicId);

internal static class GateCatalog
{
    internal sealed record OrientationGeometry(
        Vector2 CollisionRadius,
        Vector2 PlacementClearance,
        IReadOnlyList<Vector2> AnnexOffsets)
    {
        public Vector2 CollisionSize => CollisionRadius * 2;
        public Vector2 PlacementSize => PlacementClearance * 2;
    }

    private sealed record GateSource(
        string Architecture, string Suffix,
        short GateId, short SideWallId, short ConstructionId);

    private static readonly GateSource[] Stone =
    [
        new("Central European", "E", 2045, 2093, 3286),
        new("East Asian", "F", 2046, 2094, 3287),
        new("Middle Eastern", "M", 2047, 2095, 3288),
        new("Western European", "W", 2048, 2096, 3289),
        new("Expansion I", "X", 6775, 6791, 6798),
        new("Expansion II", "X", 7417, 7433, 7440),
        new("Expansion III", "X", 7775, 7791, 7798),
        new("Palisade expansion", "X", 8185, 0, 8179),
        new("Expansion IV", "X", 8775, 8791, 8798),
        new("Expansion V", "X", 9775, 9791, 9798),
        new("Expansion VI", "X", 10775, 10791, 10798)
    ];

    private static readonly GateSource[] Fortified =
    [
        new("Central European", "E", 2057, 2105, 3294),
        new("East Asian", "F", 2058, 2106, 3295),
        new("Middle Eastern", "M", 2059, 2107, 3296),
        new("Western European", "W", 2060, 2108, 3297),
        new("Expansion I", "X", 6779, 6795, 6802),
        new("Expansion II", "X", 7421, 7437, 7444),
        new("Expansion III", "X", 7779, 7795, 7802),
        new("Expansion IV", "X", 8779, 8795, 8802),
        new("Expansion V", "X", 9779, 9795, 9802),
        new("Expansion VI", "X", 10779, 10795, 10802)
    ];

    // Forgotten ships the palisade gate as a complete four-orientation
    // family. These are explicit DAT graphic ids, not offsets inferred from
    // the later stone-gate layout. A completed placement adds the authored
    // single palisade wall frame at each DAT endpoint.
    private static readonly GateOrientationDefinition[] WoodenFamily =
    [
        WoodenOrientation(
            "B", 8191, 8189, 8194, 8192,
            "SGAC1NN", 6506, "SGAC1N0", 6504,
            "GTBX2CNX", 8180),
        WoodenOrientation(
            "A", 8185, 8183, 8188, 8186,
            "SGBC1NN", 6527, "SGBC1N0", 6525,
            "GTAX2CNX", 8179),
        WoodenOrientation(
            "C", 8197, 8195, 8200, 8198,
            "SGCC1NN", 6548, "SGCC1N0", 6546,
            "GTCX2CNX", 8181),
        WoodenOrientation(
            "D", 8203, 8201, 8206, 8204,
            "SGDC1NN", 6569, "SGDC1N0", 6567,
            "GTDX2CNX", 8182)
    ];

    // Keep the first gate pass deliberately narrow. AoE names the palisade
    // graphics as tier 2 (GT*A2), but it is our tier-one wooden gate. Stone
    // and fortified families remain catalogued above for later re-enabling.
    public static readonly IReadOnlyList<GateDefinition> All =
        Stone.Where(value => value.GateId == 8185)
            .Select(value => Create(value, 2))
            .ToArray();

    private static readonly IReadOnlyDictionary<string, GateDefinition>
        ByItemId = All.ToDictionary(
            value => value.ItemId, StringComparer.OrdinalIgnoreCase);

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

    public static bool IsGate(string itemId) => ByItemId.ContainsKey(itemId);

    public static GateDefinition Get(string itemId) =>
        ByItemId.TryGetValue(itemId, out var value)
            ? value
            : throw new KeyNotFoundException($"Unknown gate: {itemId}");

    public static GateOrientationDefinition Orientation(
        GateDefinition gate, int rotation)
    {
        rotation = NormalizeRotation(rotation);
        if (gate.GateGraphicId == 8185)
            return WoodenFamily[rotation];
        var standard = gate.GateGraphicId < 3000;
        var closedOffset = standard
            ? new[] { 0, 72, 1560, 1656 }[rotation]
            : rotation * 32;
        var constructionOffset = standard
            ? new[] { 0, 16, 387, 483 }[rotation]
            : rotation * 32;
        var closedId = (short)(gate.GateGraphicId + closedOffset);
        var openId = (short)(closedId + (standard ? 24 : 8));
        var sideId = (short)(closedId + (standard ? 48 : 16));
        var shadowDelta = standard ? 8 : 2;
        var constructionId = (short)(gate.ConstructionGraphicId + constructionOffset);
        var constructionShadowId =
            (short)(constructionId - (standard ? 4 : 1));
        var family = (char)('A' + rotation);
        var suffix = gate.GateGraphicName[^1];
        var tier = gate.Tier;
        return new(
            $"GT{family}A{tier}NN{suffix}", closedId,
            $"GT{family}A{tier}N0{suffix}", (short)(closedId - shadowDelta),
            $"GT{family}B{tier}NN{suffix}", openId,
            $"GT{family}B{tier}N0{suffix}", (short)(openId - shadowDelta),
            sideId == 0 ? null : $"GT{family}C{tier}NN{suffix}", sideId,
            sideId == 0 ? null : $"GT{family}C{tier}N0{suffix}",
            sideId == 0 ? (short)0 : (short)(sideId - shadowDelta),
            $"GT{family}X{tier}CN{suffix}", constructionId,
            constructionShadowId == 0 ? null : $"GT{family}X{tier}C0{suffix}",
            constructionShadowId);
    }

    private static GateOrientationDefinition WoodenOrientation(
        string family,
        short closedId,
        short closedShadowId,
        short openId,
        short openShadowId,
        string sideName,
        short sideId,
        string sideShadowName,
        short sideShadowId,
        string constructionName,
        short constructionId) =>
        new(
            $"GT{family}A2NNX", closedId,
            $"GT{family}A2N0X", closedShadowId,
            $"GT{family}B2NNX", openId,
            $"GT{family}B2N0X", openShadowId,
            sideName, sideId, sideShadowName, sideShadowId,
            constructionName, constructionId,
            null, 0);

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

    public static IReadOnlyCollection<string> RequiredGraphics =>
        All.SelectMany(value => Enumerable.Range(0, 4)
            .Select(rotation => Orientation(value, rotation))
            .SelectMany(orientation => new[]
            {
                orientation.GateGraphicName,
                orientation.GateShadowGraphicName,
                orientation.OpenGateGraphicName,
                orientation.OpenGateShadowGraphicName,
                orientation.SideWallGraphicName,
                orientation.SideWallShadowGraphicName,
                orientation.ConstructionGraphicName,
                orientation.ConstructionShadowGraphicName
            }.Where(name => name is not null).Select(name => name!)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<CraftingRecipe> Recipes => All.Select(value =>
        new CraftingRecipe(
            $"build-{value.ItemId}", value.ItemId,
            CraftingCategory.Furniture, value.RequiredLevel,
            100 + value.RequiredLevel * 20,
            [new(ItemIds.Logs, value.LogCost)],
            ["Mark the gate footprint.", "Raise and secure the gate."],
            RequiredTools: [new(ItemTag.Hammer, "hammer")])).ToArray();

    private static GateDefinition Create(GateSource value, int tier)
    {
        var standard = value.GateId < 3000;
        var palisade = value.GateId == 8185;
        var gateShadowId = (short)(value.GateId - (standard ? 8 : 2));
        var openGateId = (short)(value.GateId +
            (standard ? 24 : palisade ? 3 : 8));
        var openGateShadowId = (short)(openGateId -
            (standard ? 8 : 2));
        var sideShadowId = value.SideWallId == 0
            ? (short)0
            : (short)(value.SideWallId - (standard ? 8 : 2));
        var constructionShadowId = palisade
            ? (short)0
            : (short)(value.ConstructionId - (standard ? 4 : 1));
        return new(
            $"gate_{value.GateId}",
            "Wooden gate",
            value.Architecture, tier,
            $"GTAA{tier}NN{value.Suffix}", value.GateId,
            $"GTAA{tier}N0{value.Suffix}", gateShadowId,
            $"GTAB{tier}NN{value.Suffix}", openGateId,
            $"GTAB{tier}N0{value.Suffix}", openGateShadowId,
            value.SideWallId == 0 ? null : $"GTAC{tier}NN{value.Suffix}",
            value.SideWallId,
            value.SideWallId == 0 ? null : $"GTAC{tier}N0{value.Suffix}",
            sideShadowId,
            $"GTAX{tier}CN{value.Suffix}", value.ConstructionId,
            constructionShadowId == 0 ? null : $"GTAX{tier}C0{value.Suffix}",
            constructionShadowId,
            350,
            1,
            4);
    }
}
