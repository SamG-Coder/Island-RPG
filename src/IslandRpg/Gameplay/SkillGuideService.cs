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
        Enum.IsDefined(skill);

    public static SkillGuideDefinition Definition(SkillType skill) =>
        skill switch
        {
            SkillType.Woodcutting => Woodcutting(),
            SkillType.Farming => Farming(),
            SkillType.Crafting => Crafting(),
            SkillType.Fishing => Fishing(),
            SkillType.Cooking => Cooking(),
            SkillType.Firemaking => Firemaking(),
            SkillType.Digging => Digging(),
            SkillType.Mining => Mining(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(skill), skill, "This skill has no level guide.")
        };

    private static SkillGuideDefinition Farming() =>
        new(
            SkillType.Farming,
            "Farming",
            [
                new(
                    1,
                    "Plant gathered tree seeds \u2022 " +
                    "Forage wild and tropical berry bushes"),
                new(
                    9,
                    "Use a bronze sickle for faster harvesting " +
                    "and bonus berries")
            ]);

    private static SkillGuideDefinition Crafting()
    {
        var entries = CraftingSkill.Recipes
            .OrderBy(recipe => recipe.RequiredLevel)
            .ThenBy(recipe =>
                ItemCatalog.Get(recipe.ResultItemId).Name)
            .Select(recipe => new SkillGuideEntry(
                recipe.RequiredLevel,
                $"Craft {ItemCatalog.Get(recipe.ResultItemId).Name}"))
            .ToArray();
        return new(
            SkillType.Crafting,
            "Crafting",
            entries);
    }

    private static SkillGuideDefinition Digging() =>
        new(
            SkillType.Digging,
            "Digging",
            Enumerable.Range(1, DiggingSkill.MaximumLevel)
                .Select(level =>
                {
                    var experience =
                        DiggingSkill.ExperienceForLevel(level);
                    return new SkillGuideEntry(
                        level,
                        level == 1
                            ? $"Excavate clear non-water ground \u2022 " +
                              $"{DiggingSkill.Damage(experience)} damage"
                            : $"Improved excavation \u2022 " +
                              $"{DiggingSkill.Damage(experience)} damage");
                })
                .ToArray());

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
                    var charcoalNote = level == 1
                        ? " \u2022 Burn spent log fuel into charcoal"
                        : "";
                    return new SkillGuideEntry(
                        level,
                        $"Fire lasts {hours:0.0} hours • " +
                        $"Light radius {radius:0} px" +
                        $"{flameNote}{charcoalNote}");
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
                        $"Cook {ItemCatalog.Get(profile.RawItemId).Name}")) +
                (group.Key == StewCookingService.RequiredLevel
                    ? " \u2022 Cook fish and berry stew in a pot beside a lit fire"
                    : "")))
            .OrderBy(entry => entry.Level)
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
