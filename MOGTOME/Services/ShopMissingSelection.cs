using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using MOGTOME.Models;

namespace MOGTOME.Services;

internal readonly record struct ShopMissingResult(int Added, int UnknownOwnership, int UnknownAcquisition, int UnknownEligibility);

internal static class ShopMissingSelection
{
    internal static ShopMissingResult Apply(Dictionary<uint, int> targets,
        IEnumerable<(uint ItemId, ShopMissingKind Kind, BlundervilleRegistration Ownership, ShopOfferAvailability Availability)> entries)
    {
        var added = 0; var unknownOwnership = 0; var unknownAcquisition = 0; var unknownEligibility = 0;
        foreach (var entry in entries.DistinctBy(e => e.ItemId))
        {
            if (entry.ItemId == 0 || entry.Kind == ShopMissingKind.None || entry.Availability == ShopOfferAvailability.Locked ||
                entry.Ownership == BlundervilleRegistration.Owned) continue;
            if (entry.Kind == ShopMissingKind.UnknownEquipment) { unknownAcquisition++; continue; }
            if (entry.Availability == ShopOfferAvailability.Unknown) { unknownEligibility++; continue; }
            if (entry.Ownership == BlundervilleRegistration.Unknown) { unknownOwnership++; continue; }
            if (entry.Ownership != BlundervilleRegistration.Missing || targets.GetValueOrDefault(entry.ItemId) >= 1) continue;
            targets[entry.ItemId] = 1;
            added++;
        }
        return new(added, unknownOwnership, unknownAcquisition, unknownEligibility);
    }

    internal static ShopMissingKind ReadKind(uint itemId, IReadOnlySet<uint> pvpItems)
    {
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return ShopMissingKind.None;
        var action = item.ItemAction.ValueNullable?.Action.RowId ?? 0;
        return Classify(action, item.EquipSlotCategory.RowId != 0, pvpItems.Contains(itemId), itemId);
    }

    internal static ShopMissingKind Classify(uint action, bool equippable, bool pvp, uint itemId = 0)
        => action is 1322 or 853 or 20086 or 37312 or 25183 or 2633 or 1013 or 3357 ? ShopMissingKind.Collectible
            : !equippable ? ShopMissingKind.None : pvp ? ShopMissingKind.PvpEquipment : itemId switch
            {
                // Current rewards verified 2026-10-10 against Garland Tools item acquisition records.
                // Mameshiba event reward; Skybuilders' Scrip garments; Archaeotania's Horn garments.
                24615 or 32794 or 30052 or 27936 or 27937 => ShopMissingKind.ShopEquipment,
                // These coats drop in Pharos Sirius (Hard) / Saint Mocianne's Arboretum.
                13162 or 13174 or 13186 => ShopMissingKind.None,
                _ => ShopMissingKind.UnknownEquipment,
            };

    internal static HashSet<uint> ReadPvpItems()
    {
        var items = new HashSet<uint>();
        // Literal currencies verified in Item/SpecialShop: Wolf Mark 25, Trophy Crystal 36656.
        // IsPvP is false even for several actual Wolf Mark rewards. No item-ID ranges or name matching.
        foreach (var shop in Plugin.DataManager.GetExcelSheet<SpecialShop>())
        foreach (var row in shop.Item)
            if (row.ItemCosts.Any(c => IsPvpCost(c.ItemCost.RowId, c.CurrencyCost, c.CostType)))
                foreach (var receive in row.ReceiveItems)
                    if (receive.Item.RowId != 0 && receive.ReceiveCount > 0) items.Add(receive.Item.RowId);
        return items;
    }

    internal static bool IsPvpCost(uint itemId, uint price, byte costType)
        => price > 0 && itemId is 25 or 36656 && costType == 0;
}
