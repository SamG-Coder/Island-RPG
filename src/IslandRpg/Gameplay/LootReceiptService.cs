using System.Text;

namespace IslandRpg.Gameplay;

internal readonly record struct LootReceiptItem(
    string ItemId,
    int Quantity);

/// <summary>
/// Builds compact, input-agnostic feedback for loot container withdrawals.
/// Both mouse input and the control pipe use the same receipt path.
/// </summary>
internal static class LootReceiptService
{
    public static string Summary(IReadOnlyList<LootReceiptItem> items)
    {
        var builder = new StringBuilder("Looted ");
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
                builder.Append(index == items.Count - 1 ? " and " : ", ");
            var item = items[index];
            builder.Append(item.Quantity)
                .Append('\u00d7')
                .Append(' ')
                .Append(ItemCatalog.Get(item.ItemId).Name);
        }
        return builder.Append('.').ToString();
    }

    public static string? DiscoveryHint(string itemId) => itemId switch
    {
        ItemIds.SlimeGel =>
            "Slime gel can replace rope in selected recipes.",
        ItemIds.SaltCrystals =>
            "Salt crystals can preserve cooked fish.",
        _ => null
    };
}
