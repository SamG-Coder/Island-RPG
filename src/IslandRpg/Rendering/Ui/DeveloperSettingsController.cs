using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class DeveloperSettingsController
{
    public const int ToolsHeaderIndex = 0;
    public const int PrimaryToolsIndex = 1;
    public const int WorldToolsIndex = 2;
    public const int SoundAuditionIndex = 3;
    public const int DiagnosticsHeaderIndex = 4;
    public const int NavigationBlocksIndex = 5;
    public const int UnlimitedZoomIndex = 6;
    public const int ZoomScaledLoadingIndex = 7;
    public const int UseTestAssetsIndex = 8;
    public const int ProgressionHeaderIndex = 9;
    public const int SkillStartIndex = 10;

    public static readonly SkillType[] Skills =
        Enum.GetValues<SkillType>();

    public bool TryUpdate(
        Vector2 pointer,
        ListControlState list,
        PlayerProfile? player,
        out PlayerProfile? updated)
    {
        updated = player;
        if (player is null) return false;

        foreach (var skill in Skills)
        {
            if (!list.VisibleIndices.Contains(
                    SkillStartIndex + (int)skill))
                continue;
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

    public static Vector4 MapToolBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(PrimaryToolsIndex), 0);
    }

    public static Vector4 ItemBankBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(PrimaryToolsIndex), 1);
    }

    public static Vector4 AdvanceTimeBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(WorldToolsIndex), 0);
    }

    public static Vector4 WorldLevelBounds(ListControlState list)
    {
        return RowColumnBounds(list.RowBounds(WorldToolsIndex), 1);
    }

    public static Vector4 NavigationBlocksBounds(
        ListControlState list) =>
        list.RowBounds(NavigationBlocksIndex);

    public static Vector4 UnlimitedZoomBounds(
        ListControlState list) =>
        list.RowBounds(UnlimitedZoomIndex);

    public static Vector4 ZoomScaledLoadingBounds(
        ListControlState list) =>
        list.RowBounds(ZoomScaledLoadingIndex);

    public static Vector4 UseTestAssetsBounds(
        ListControlState list) =>
        list.RowBounds(UseTestAssetsIndex);

    public static Vector4 SoundPreviousBounds(ListControlState list) =>
        SoundColumnBounds(list, 0);

    public static Vector4 SoundPlayBounds(ListControlState list) =>
        SoundColumnBounds(list, 1);

    public static Vector4 SoundNextBounds(ListControlState list) =>
        SoundColumnBounds(list, 2);

    public static Vector4 SkillRowBounds(
        ListControlState list, SkillType skill) =>
        list.RowBounds(SkillStartIndex + (int)skill);

    public static Vector4 MaxBounds(
        ListControlState list, SkillType skill)
    {
        var row = SkillRowBounds(list, skill);
        return new(row.X + row.Z - 66, row.Y + 10, 58, 30);
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

    private static Vector4 SoundColumnBounds(
        ListControlState list, int column)
    {
        var row = list.RowBounds(SoundAuditionIndex);
        const float labelWidth = 150;
        const float gap = 6;
        var width = (row.Z - labelWidth - gap * 2) / 3;
        return new(
            row.X + labelWidth + column * (width + gap),
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
