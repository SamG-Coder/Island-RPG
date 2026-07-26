using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class DeveloperSettingsController
{
    private static readonly int[] Multipliers = [1, 10, 100];
    private int _multiplierIndex;

    public int ExperienceMultiplier => Multipliers[_multiplierIndex];
    public int ExperienceGrant => 100 * ExperienceMultiplier;

    public bool TryUpdate(
        Vector2 pointer,
        Vector4 panel,
        PlayerProfile? player,
        out PlayerProfile? updated)
    {
        updated = player;
        if (MultiplierBounds(panel).Contains(pointer))
        {
            _multiplierIndex =
                (_multiplierIndex + 1) % Multipliers.Length;
            return false;
        }
        if (player is null) return false;

        foreach (var skill in Enum.GetValues<SkillType>())
        {
            if (GrantBounds(panel, skill).Contains(pointer))
            {
                updated = SetExperience(
                    player,
                    skill,
                    SkillService.AwardExperience(
                        Experience(player, skill),
                        ExperienceGrant).Experience);
                return true;
            }
            if (MaxBounds(panel, skill).Contains(pointer))
            {
                updated = SetExperience(
                    player, skill, MaximumExperience());
                return true;
            }
        }
        return false;
    }

    public static int Level(PlayerProfile? player, SkillType skill) =>
        SkillService.LevelForExperience(Experience(player, skill));

    public static int Experience(
        PlayerProfile? player, SkillType skill) =>
        skill switch
        {
            SkillType.Woodcutting =>
                player?.WoodcuttingExperience ?? 0,
            SkillType.Farming =>
                player?.FarmingExperience ?? 0,
            SkillType.Crafting =>
                player?.CraftingExperience ?? 0,
            _ => player?.FishingExperience ?? 0
        };

    public static int ExperienceToNextLevel(
        PlayerProfile? player, SkillType skill) =>
        SkillService.ExperienceToNextLevel(Experience(player, skill));

    public static Vector4 MultiplierBounds(Vector4 panel)
    {
        var content = SettingsMenuState.ContentBounds(panel);
        return new(content.X + 16, content.Y + 14, 150, 34);
    }

    public static Vector4 MapToolBounds(Vector4 panel)
    {
        var content = SettingsMenuState.ContentBounds(panel);
        return new(content.X + 182, content.Y + 14, 150, 34);
    }

    public static Vector4 SkillRowBounds(
        Vector4 panel, SkillType skill)
    {
        var content = SettingsMenuState.ContentBounds(panel);
        return new(
            content.X + 16,
            content.Y + 58 + (int)skill * 72,
            content.Z - 32,
            62);
    }

    public static Vector4 GrantBounds(
        Vector4 panel, SkillType skill)
    {
        var row = SkillRowBounds(panel, skill);
        return new(row.X + row.Z - 196, row.Y + 15, 98, 32);
    }

    public static Vector4 MaxBounds(
        Vector4 panel, SkillType skill)
    {
        var row = SkillRowBounds(panel, skill);
        return new(row.X + row.Z - 90, row.Y + 15, 82, 32);
    }

    private static int MaximumExperience() =>
        SkillService.ExperienceForLevel(SkillService.MaximumLevel);

    private static PlayerProfile SetExperience(
        PlayerProfile player, SkillType skill, int experience)
    {
        var updated = skill switch
        {
            SkillType.Woodcutting => player with
            {
                WoodcuttingExperience = experience
            },
            SkillType.Farming => player with
            {
                FarmingExperience = experience
            },
            SkillType.Crafting => player with
            {
                CraftingExperience = experience
            },
            _ => player with
            {
                FishingExperience = experience
            }
        };
        return updated with { UpdatedUtc = DateTime.UtcNow };
    }
}
