using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal sealed record SkillGuideEntry(int Level, string Description);

internal sealed record SkillGuideDefinition(
    SkillType Skill,
    string Name,
    IReadOnlyList<SkillGuideEntry> Entries);

internal static class SkillGuideService
{
    public static bool IsSupported(SkillType skill) =>
        skill is SkillType.Woodcutting or
            SkillType.Fishing or
            SkillType.Cooking or
            SkillType.Firemaking or
            SkillType.Mining;

    public static SkillGuideDefinition Definition(SkillType skill) =>
        skill switch
        {
            SkillType.Woodcutting => Woodcutting(),
            SkillType.Fishing => Fishing(),
            SkillType.Cooking => Cooking(),
            SkillType.Firemaking => Firemaking(),
            SkillType.Mining => Mining(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(skill), skill, "This skill has no level guide.")
        };

    private static SkillGuideDefinition Mining() =>
        new(
            SkillType.Mining,
            "Mining",
            Enumerable.Range(1, MiningSkill.MaximumLevel)
                .Select(level => new SkillGuideEntry(
                    level,
                    level == 1
                        ? $"Mine cave deposits • {MiningSkill.HitChance(level) * 100:0}% hit"
                        : $"Improved mining • {MiningSkill.HitChance(level) * 100:0}% hit"))
                .ToArray());

    private static SkillGuideDefinition Firemaking() =>
        new(
            SkillType.Firemaking,
            "Firemaking",
            Enumerable.Range(1, FiremakingSkill.MaximumLevel)
                .Select(level =>
                {
                    var hours =
                        FiremakingSkill.DurationGameSeconds(level) /
                        3600;
                    var radius =
                        FiremakingSkill.LightRadiusPixels(level);
                    var tier = FiremakingSkill.FlameTier(level);
                    var flameNote =
                        level == 1 || FiremakingSkill.FlameTier(level - 1) != tier
                            ? $" • Flame size {tier + 1}"
                            : "";
                    return new SkillGuideEntry(
                        level,
                        $"Fire lasts {hours:0.0} hours • " +
                        $"Light radius {radius:0} px{flameNote}");
                })
                .ToArray());

    private static SkillGuideDefinition Cooking()
    {
        var unlocks = CookingSkill.CookProfiles
            .GroupBy(profile => profile.RequiredLevel)
            .OrderBy(group => group.Key)
            .Select(group => new SkillGuideEntry(
                group.Key,
                string.Join(
                    " • ",
                    group.Select(profile =>
                        $"Cook {ItemCatalog.Get(profile.RawItemId).Name}"))))
            .ToArray();
        return new(
            SkillType.Cooking,
            "Cooking",
            unlocks);
    }

    private static SkillGuideDefinition Fishing()
    {
        var unlocks = FishingSkill.CatchProfiles
            .GroupBy(profile => profile.RequiredLevel)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    " • ",
                    group.Select(profile =>
                        $"Catch {WorldFishGenerator.Profile(profile.Species).DisplayName}")));
        return new(
            SkillType.Fishing,
            "Fishing",
            unlocks
                .OrderBy(pair => pair.Key)
                .Select(pair => new SkillGuideEntry(
                    pair.Key,
                    pair.Value))
                .ToArray());
    }

    private static SkillGuideDefinition Woodcutting() =>
        new(
            SkillType.Woodcutting,
            "Woodcutting",
            Enumerable.Range(1, WoodcuttingSkill.MaximumLevel)
                .Select(level => new SkillGuideEntry(
                    level,
                    level == 1
                        ? $"Cut all current trees • " +
                          $"{WoodcuttingSkill.HitChance(level) * 100:0}% hit • " +
                          $"{WoodcuttingSkill.MinimumDamage(level)}–" +
                          $"{WoodcuttingSkill.MaximumDamage(level)} base damage"
                        : $"Improved cutting • " +
                          $"{WoodcuttingSkill.HitChance(level) * 100:0}% hit • " +
                          $"{WoodcuttingSkill.MinimumDamage(level)}–" +
                          $"{WoodcuttingSkill.MaximumDamage(level)} base damage"))
                .ToArray());
}
