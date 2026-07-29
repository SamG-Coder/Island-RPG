using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class DeveloperSettingsController
{
    private static readonly int[] Multipliers = [1, 10, 100];
    public static readonly SkillType[] Skills =
        Enum.GetValues<SkillType>();
    private int _multiplierIndex;

    public int ExperienceMultiplier => Multipliers[_multiplierIndex];
    public int ExperienceGrant => 100 * ExperienceMultiplier;

    public bool TryUpdate(
        Vector2 pointer,
        ListControlState list,
        PlayerProfile? player,
        out PlayerProfile? updated)
    {
        updated = player;
        if (MultiplierBounds(list).Contains(pointer))
        {
            _multiplierIndex =
                (_multiplierIndex + 1) % Multipliers.Length;
            return false;
        }
        if (player is null) return false;

        foreach (var skill in Skills)
        {
            if (!list.VisibleIndices.Contains(3 + (int)skill))
                continue;
            if (GrantBounds(list, skill).Contains(pointer))
            {
                updated = SetExperience(
                    player,
                    skill,
                    SkillService.AwardExperience(
                        Experience(player, skill),
                        ExperienceGrant).Experience);
                return true;
            }
            if (MaxBounds(list, skill).Contains(pointer))
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
            SkillType.Fishing =>
                player?.FishingExperience ?? 0,
            SkillType.Cooking =>
                player?.CookingExperience ?? 0,
            SkillType.Firemaking =>
                player?.FiremakingExperience ?? 0,
            SkillType.Digging => player?.DiggingExperience ?? 0,
            _ => player?.MiningExperience ?? 0
        };

    public static int ExperienceToNextLevel(
        PlayerProfile? player, SkillType skill) =>
        SkillService.ExperienceToNextLevel(Experience(player, skill));

    public static Vector4 MultiplierBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(0), 0);
    }

    public static Vector4 MapToolBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(0), 1);
    }

    public static Vector4 AdvanceTimeBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(1), 0);
    }

    public static Vector4 WorldLevelBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(1), 1);
    }

    public static Vector4 ItemBankBounds(ListControlState list)
    {
        var row = list.RowBounds(2);
        return new(row.X, row.Y + 7, row.Z, row.W - 14);
    }

    public static Vector4 NavigationBlocksBounds(
        ListControlState list)
    {
        var row = list.RowBounds(3);
        return new(row.X, row.Y + 7, row.Z, row.W - 14);
    }

    public static Vector4 SkillRowBounds(
        ListControlState list, SkillType skill) =>
        list.RowBounds(4 + (int)skill);

    public static Vector4 GrantBounds(
        ListControlState list, SkillType skill)
    {
        var row = SkillRowBounds(list, skill);
        return new(row.X + row.Z - 196, row.Y + 15, 98, 32);
    }

    public static Vector4 MaxBounds(
        ListControlState list, SkillType skill)
    {
        var row = SkillRowBounds(list, skill);
        return new(row.X + row.Z - 90, row.Y + 15, 82, 32);
    }

    private static Vector4 RowColumnBounds(Vector4 row, int column)
    {
        const float gap = 10;
        var width = (row.Z - gap) * .5f;
        return new(
            row.X + column * (width + gap),
            row.Y + 7,
            width,
            row.W - 14);
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
            SkillType.Fishing => player with
            {
                FishingExperience = experience
            },
            SkillType.Cooking => player with
            {
                CookingExperience = experience
            },
            SkillType.Firemaking => player with
            {
                FiremakingExperience = experience
            },
            SkillType.Digging => player with
            {
                DiggingExperience = experience
            },
            _ => player with
            {
                MiningExperience = experience
            }
        };
        return updated with { UpdatedUtc = DateTime.UtcNow };
    }
}
