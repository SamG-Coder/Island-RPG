using System.Numerics;
using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class PlantedTreeService
{
    public const int MaximumLivingTreesPerPlanter = 12;
    public const double GrowthGameSeconds = 8 * 60 * 60;
    public const double CompactGameSeconds = 10 * 60;
    public const double FadeGameSeconds = 2 * 60 * 60;
    public const float ShrubScale = .32f;
    public const float CompactPeakScale = 1.06f;
    public const double StrikeCadenceSeconds = 1.05;
    public const int MaximumPlanterNameLength = 40;
    public const char FuelSeparator = '|';

    public static bool TryTreeType(string seedItemId, out string treeType)
    {
        treeType = seedItemId switch
        {
            ItemIds.TreeSeeds => "TREEA_NN",
            ItemIds.PalmSeeds => "FPAL_NN",
            ItemIds.PineSeeds => "FPIN_NN",
            ItemIds.OakSeeds => "FOAK_NN",
            ItemIds.JungleTreeSeeds => "FJUN_NN",
            ItemIds.SnowTreeSeeds => "FSNO_NN",
            ItemIds.BambooSeeds => "FBAM_NN",
            ItemIds.CactusSeeds => "FCAC_NN",
            _ => ""
        };
        return treeType.Length > 0;
    }

    public static string? TreeTypeForSeed(string seedItemId) =>
        TryTreeType(seedItemId, out var treeType) ? treeType : null;

    public static Vector2 TileCenter(Vector2 position) =>
        CropService.TileCenter(position);

    public static bool IsTileCenter(Vector2 position) =>
        CropService.IsTileCenter(position);

    public static WorldGroundObject Plant(
        Guid objectId,
        string seedItemId,
        float x,
        float y,
        double gameSeconds,
        string planterDisplayName,
        string? ownerId = null)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException(
                "A stable planted-tree identity is required.",
                nameof(objectId));
        if (!float.IsFinite(x) || !float.IsFinite(y))
            throw new ArgumentOutOfRangeException(
                nameof(x), "Planted tree coordinates must be finite.");
        if (!double.IsFinite(gameSeconds) || gameSeconds < 0 ||
            gameSeconds > double.MaxValue - GrowthGameSeconds -
            CompactGameSeconds - FadeGameSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(gameSeconds),
                "Planting time must be a finite non-negative value.");
        if (!TryTreeType(seedItemId, out var treeType))
            throw new ArgumentException(
                "The item is not a tree seed.", nameof(seedItemId));

        var maximumHealth = MaximumHealth(treeType);
        return new(
            objectId,
            ItemIds.PlantedTree,
            x,
            y,
            FuelItemId: ComposeFuel(treeType, planterDisplayName),
            LitUntilGameSeconds: gameSeconds,
            Health: maximumHealth,
            MaxHealth: maximumHealth,
            OwnerId: ownerId);
    }

    public static WorldGroundObject Plant(
        string seedItemId,
        float x,
        float y,
        double gameSeconds,
        string planterDisplayName,
        string? ownerId = null) =>
        Plant(
            Guid.NewGuid(),
            seedItemId,
            x,
            y,
            gameSeconds,
            planterDisplayName,
            ownerId);

    public static bool IsPlantedTreeItem(string itemId) =>
        itemId == ItemIds.PlantedTree;

    public static bool IsPlantedTree(WorldGroundObject value) =>
        IsPlantedTreeItem(value.ItemId) &&
        TryParseFuel(value.FuelItemId, out var treeType, out _) &&
        IsKnownTreeType(treeType) &&
        value.MaxHealth > 0 &&
        value.Health >= 0 &&
        value.Health <= value.MaxHealth &&
        double.IsFinite(value.LitUntilGameSeconds) &&
        value.LitUntilGameSeconds >= 0;

    public static bool IsLiving(WorldGroundObject value) =>
        IsPlantedTree(value) && value.Health > 0;

    public static bool IsFelled(WorldGroundObject value) =>
        IsPlantedTree(value) && value.Health <= 0;

    public static bool IsCompacted(
        WorldGroundObject value, double gameSeconds) =>
        IsLiving(value) &&
        ValidGameSeconds(gameSeconds) &&
        gameSeconds >= value.LitUntilGameSeconds +
            GrowthGameSeconds + CompactGameSeconds;

    public static bool IsExpired(
        WorldGroundObject value, double gameSeconds) =>
        IsFelled(value) &&
        ValidGameSeconds(gameSeconds) &&
        gameSeconds >= value.LitUntilGameSeconds + FadeGameSeconds;

    public static float GrowthScale(
        WorldGroundObject value, double gameSeconds)
    {
        if (!IsPlantedTree(value) || !ValidGameSeconds(gameSeconds))
            return 1;
        if (IsFelled(value))
            return 1;

        var age = gameSeconds - value.LitUntilGameSeconds;
        if (age <= 0)
            return ShrubScale;
        if (age < GrowthGameSeconds)
        {
            var progress = (float)(age / GrowthGameSeconds);
            var eased = 1f - (1f - progress) * (1f - progress);
            return ShrubScale + (1f - ShrubScale) * eased;
        }

        var compactAge = age - GrowthGameSeconds;
        if (compactAge < CompactGameSeconds)
        {
            var t = (float)(compactAge / CompactGameSeconds);
            return 1f + (CompactPeakScale - 1f) * MathF.Sin(t * MathF.PI);
        }

        return 1;
    }

    public static float FadeOpacity(
        WorldGroundObject value, double gameSeconds)
    {
        if (!IsFelled(value) || !ValidGameSeconds(gameSeconds))
            return 1;
        var elapsed = gameSeconds - value.LitUntilGameSeconds;
        if (elapsed <= 0)
            return 1;
        if (elapsed >= FadeGameSeconds)
            return 0;
        return (float)(1 - elapsed / FadeGameSeconds);
    }

    public static string TreeType(WorldGroundObject value) =>
        TryParseFuel(value.FuelItemId, out var treeType, out _)
            ? treeType
            : "TREEA_NN";

    public static string PlanterDisplayName(WorldGroundObject value) =>
        TryParseFuel(value.FuelItemId, out _, out var name) &&
        name.Length > 0
            ? name
            : "a settler";

    public static string Title(WorldGroundObject value) =>
        $"Planted by {PlanterDisplayName(value)}";

    public static string Examine(WorldGroundObject value)
    {
        var tree = DisplayName(TreeType(value));
        var planter = PlanterDisplayName(value);
        if (IsFelled(value))
            return $"The remains of {Article(tree)} planted by {planter}.";
        return $"{char.ToUpperInvariant(Article(tree)[0])}{Article(tree)[1..]} planted by {planter}.";
    }

    public static int MaximumHealth(string treeType)
    {
        if (Starts(treeType, "FPAL"))
            return 75;
        if (Starts(treeType, "FPIN"))
            return 125;
        if (Starts(treeType, "FOAK"))
            return 150;
        if (Starts(treeType, "FJUN"))
            return 175;
        if (Starts(treeType, "FSNO"))
            return 135;
        if (Starts(treeType, "FBAM"))
            return 80;
        if (Starts(treeType, "FCAC"))
            return 65;
        if (Starts(treeType, "TREE") && treeType.Length > 4)
        {
            int[] healthByVariant =
                [100, 125, 90, 150, 110, 175, 95, 135, 105, 160, 120, 145];
            var variant = char.ToUpperInvariant(treeType[4]) - 'A';
            if ((uint)variant < (uint)healthByVariant.Length)
                return healthByVariant[variant];
        }

        return 100;
    }

    public static string LogItemId(string treeType)
    {
        if (Starts(treeType, "FOAK"))
            return ItemIds.OakLogs;
        if (Starts(treeType, "FPIN"))
            return ItemIds.PineLogs;
        if (Starts(treeType, "FPAL"))
            return ItemIds.PalmLogs;
        if (Starts(treeType, "FBAM"))
            return ItemIds.Bamboo;
        return ItemIds.Logs;
    }

    public static string DisplayName(string treeType)
    {
        if (Starts(treeType, "FPAL"))
            return "palm";
        if (Starts(treeType, "FPIN"))
            return "pine";
        if (Starts(treeType, "FOAK"))
            return "oak";
        if (Starts(treeType, "FBAM"))
            return "bamboo";
        if (Starts(treeType, "FCAC"))
            return "cactus";
        if (Starts(treeType, "FJUN"))
            return "jungle tree";
        if (Starts(treeType, "FSNO"))
            return "snow tree";
        return "tree";
    }

    public static string SeedItemId(string treeType)
    {
        if (Starts(treeType, "FPAL"))
            return ItemIds.PalmSeeds;
        if (Starts(treeType, "FPIN"))
            return ItemIds.PineSeeds;
        if (Starts(treeType, "FOAK"))
            return ItemIds.OakSeeds;
        if (Starts(treeType, "FJUN"))
            return ItemIds.JungleTreeSeeds;
        if (Starts(treeType, "FSNO"))
            return ItemIds.SnowTreeSeeds;
        if (Starts(treeType, "FBAM"))
            return ItemIds.BambooSeeds;
        if (Starts(treeType, "FCAC"))
            return ItemIds.CactusSeeds;
        return ItemIds.TreeSeeds;
    }

    public static WorldGroundObject ApplyStrike(
        WorldGroundObject value, int remainingHealth, double gameSeconds)
    {
        if (!IsLiving(value))
            throw new ArgumentException(
                "Only a living planted tree can take a woodcutting strike.",
                nameof(value));
        if (!ValidGameSeconds(gameSeconds))
            throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        remainingHealth = Math.Clamp(remainingHealth, 0, value.MaxHealth);
        return remainingHealth == 0
            ? value with
            {
                Health = 0,
                LitUntilGameSeconds = gameSeconds
            }
            : value with { Health = remainingHealth };
    }

    public static int CountLiving(
        IEnumerable<WorldGroundObject> values, string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(ownerId))
            return 0;
        var count = 0;
        foreach (var value in values)
            if (IsLiving(value) &&
                string.Equals(
                    value.OwnerId, ownerId, StringComparison.Ordinal))
                count++;
        return count;
    }

    public static bool HasValidPersistentState(WorldGroundObject value) =>
        !IsPlantedTreeItem(value.ItemId) || IsPlantedTree(value);

    public static string ComposeFuel(
        string treeType, string? planterDisplayName)
    {
        if (!IsKnownTreeType(treeType))
            throw new ArgumentException(
                "The tree type is not recognized.", nameof(treeType));
        var name = SanitizePlanterName(planterDisplayName);
        return name.Length == 0 ? treeType : $"{treeType}{FuelSeparator}{name}";
    }

    public static bool TryParseFuel(
        string? fuel, out string treeType, out string planterDisplayName)
    {
        treeType = "";
        planterDisplayName = "";
        if (string.IsNullOrWhiteSpace(fuel))
            return false;
        var separator = fuel.IndexOf(FuelSeparator);
        if (separator < 0)
        {
            if (!IsKnownTreeType(fuel))
                return false;
            treeType = fuel;
            return true;
        }

        var type = fuel[..separator];
        if (!IsKnownTreeType(type))
            return false;
        treeType = type;
        planterDisplayName = SanitizePlanterName(fuel[(separator + 1)..]);
        return true;
    }

    public static string SanitizePlanterName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return "";
        Span<char> buffer = stackalloc char[Math.Min(
            trimmed.Length, MaximumPlanterNameLength)];
        var written = 0;
        foreach (var character in trimmed)
        {
            if (character == FuelSeparator ||
                char.IsControl(character))
                continue;
            if (written == buffer.Length)
                break;
            buffer[written++] = character;
        }

        return written == 0 ? "" : new string(buffer[..written]);
    }

    public static bool IsKnownTreeType(string treeType) =>
        treeType is "TREEA_NN" or "FPAL_NN" or "FPIN_NN" or "FOAK_NN" or
            "FJUN_NN" or "FSNO_NN" or "FBAM_NN" or "FCAC_NN" ||
        treeType.Length == 8 &&
        Starts(treeType, "TREE") &&
        treeType.EndsWith("_NN", StringComparison.Ordinal) &&
        treeType[4] is >= 'A' and <= 'L';

    private static string Article(string noun) =>
        noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0]))
            ? $"an {noun}"
            : $"a {noun}";

    private static bool Starts(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool ValidGameSeconds(double value) =>
        double.IsFinite(value) && value >= 0;
}
