namespace IslandRpg.Rendering;

using IslandRpg.Assets;

internal sealed record SpriteAtlasSource(
    string Key,
    string? Alias,
    SpriteFrame Frame,
    string Group);

internal sealed record SpriteAtlasPlacement(
    SpriteAtlasSource Source,
    int X,
    int Y);

internal sealed record SpriteAtlasPageLayout(
    int Width,
    int Height,
    IReadOnlyList<SpriteAtlasPlacement> Placements);

/// <summary>
/// Packs related sprites into bounded, tightly-sized pages. Group boundaries
/// keep construction, world scenery and item/effect assets independently
/// pageable while shelf packing avoids a permanently allocated 8K square.
/// </summary>
internal static class SpriteAtlasPacker
{
    public static IReadOnlyList<SpriteAtlasPageLayout> Pack(
        IEnumerable<SpriteAtlasSource> sources,
        int maximumPageSize = 4096,
        int padding = 1)
    {
        if (maximumPageSize <= padding * 2)
            throw new ArgumentOutOfRangeException(nameof(maximumPageSize));

        // AoE data can expose the same atlas key through more than one
        // graphic definition. The former monolithic atlas resolved those
        // collisions by keeping the last loaded definition. Preserve that
        // rule before height sorting; otherwise sorting can pair a key with
        // a differently-sized shadow frame and stretch its rendered quad.
        var uniqueSources = sources
            .Select((source, index) => (Source: source, Index: index))
            .GroupBy(
                value => value.Source.Key,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(value => value.Index).Source)
            .ToArray();

        var result = new List<SpriteAtlasPageLayout>();
        foreach (var group in uniqueSources.GroupBy(
                     value => value.Group,
                     StringComparer.OrdinalIgnoreCase))
            PackGroup(group, maximumPageSize, padding, result);
        return result;
    }

    private static void PackGroup(
        IEnumerable<SpriteAtlasSource> sources,
        int maximumPageSize,
        int padding,
        List<SpriteAtlasPageLayout> result)
    {
        var page = new List<SpriteAtlasPlacement>();
        var x = padding;
        var y = padding;
        var rowHeight = 0;
        var usedWidth = padding;

        // Tallest-first shelf packing is deterministic and substantially
        // reduces empty row tails compared with asset-load order.
        foreach (var source in sources
                     .OrderByDescending(value => value.Frame.Height)
                     .ThenByDescending(value => value.Frame.Width)
                     .ThenBy(value => value.Key, StringComparer.Ordinal))
        {
            if (source.Frame.Width + padding * 2 > maximumPageSize ||
                source.Frame.Height + padding * 2 > maximumPageSize)
                throw new InvalidOperationException(
                    $"Atlas sprite {source.Key} is larger than the " +
                    $"{maximumPageSize}px page limit.");

            if (x + source.Frame.Width + padding > maximumPageSize)
            {
                x = padding;
                y += rowHeight + padding;
                rowHeight = 0;
            }
            if (y + source.Frame.Height + padding > maximumPageSize)
            {
                FinishPage();
                x = padding;
                y = padding;
                rowHeight = 0;
                usedWidth = padding;
            }

            page.Add(new(source, x, y));
            x += source.Frame.Width + padding;
            usedWidth = Math.Max(usedWidth, x);
            rowHeight = Math.Max(rowHeight, source.Frame.Height);
        }
        FinishPage();

        void FinishPage()
        {
            if (page.Count == 0) return;
            result.Add(new(
                Math.Min(maximumPageSize, usedWidth + padding),
                Math.Min(maximumPageSize, y + rowHeight + padding),
                page.ToArray()));
            page.Clear();
        }
    }
}
