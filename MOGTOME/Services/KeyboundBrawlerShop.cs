using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MOGTOME.Services;

internal static class KeyboundBrawlerShop
{
    // Verified against current SpecialShop/Item/ENpcBase sheets on 2026-10-22.
    // The MGC trader (1058753) links directly to this shop; its festival gate still applies.
    internal const uint ShopId = 1771021;
    internal const uint CurrencyItemId = 52322;
    internal const uint CofferItemId = 52450;

    private static IReadOnlyList<BlundervilleOffer>? catalog;
    internal static IReadOnlyList<BlundervilleOffer> Catalog => catalog ??= LoadCatalog();
    internal static void RefreshCatalog() => catalog = LoadCatalog();

    // Only the nineteen verified equipment/unlock rewards participate in Select Missing.
    // Future native offers remain visible and manually selectable until their ownership rules are reviewed.
    internal static bool IsSelectMissingReward(uint itemId) => itemId is
        52323 or 52324 or 52325 or 52326 or 52327 or
        52328 or 52329 or 52330 or 52331 or 52332 or 52647 or
        52287 or 52261 or 52283 or 52354 or 52355 or 52356 or 51997 or 52291;

    internal static string CategoryKey(uint itemId) => itemId switch
    {
        52323 or 52324 or 52325 or 52326 or 52327 or
        52328 or 52329 or 52330 or 52331 or 52332 or 52647 => "KB_CategoryGlamour",
        52287 => "KB_CategoryFashionAccessory",
        52261 => "KB_CategoryMount",
        52283 => "KB_CategoryMinion",
        52354 or 52355 or 52356 => "KB_CategoryOrchestrion",
        51997 => "KB_CategoryFramerKit",
        52291 => "KB_CategoryEmote",
        CofferItemId => "KB_CategoryCoffer",
        _ => "KB_CategoryOther",
    };

    internal static unsafe bool TryWallet(out uint balance)
    {
        balance = 0;
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return false;
        var manager = CurrencyManager.Instance();
        if (manager == null) return false;
        balance = manager->GetItemCount(CurrencyItemId);
        return true;
    }

    private static IReadOnlyList<BlundervilleOffer> LoadCatalog()
    {
        try
        {
            if (!Plugin.DataManager.GetExcelSheet<SpecialShop>().TryGetRow(ShopId, out var shop)) return [];
            var offers = new List<BlundervilleOffer>();
            var pvpItems = ShopMissingSelection.ReadPvpItems();
            foreach (var row in shop.Item)
            {
                var offer = ReadOffer(shop.RowId, shop.Name.ToString(),
                    row.ReceiveItems.Select(r => (r.Item.RowId, r.ReceiveCount, r.ReceiveHq)),
                    row.ItemCosts.Select(c => (c.ItemCost.RowId, c.CurrencyCost, (uint)c.CollectabilityCost, c.CostType)));
                if (offer == null) continue;
                offers.Add(offer with
                {
                    Gate = ShopOfferEligibility.ReadGate(shop, row),
                    MissingKind = ShopMissingSelection.ReadKind(offer.ItemId, pvpItems, verifiedShopEquipment: true),
                });
            }
            return offers.Distinct().ToArray();
        }
        catch
        {
            // Unavailable native data never becomes a guessed reward catalog.
            return [];
        }
    }

    internal static BlundervilleOffer? ReadOffer(uint shopId, string shopName,
        IEnumerable<(uint ItemId, uint Count, bool Hq)> received,
        IEnumerable<(uint ItemId, uint Price, uint Collectability, byte CostType)> paid)
    {
        var receives = received.Where(r => r.ItemId != 0).ToArray();
        var costs = paid.Where(c => c.ItemId != 0 || c.Price != 0 || c.Collectability != 0).ToArray();
        if (shopId != ShopId || receives.Length != 1 || receives[0].Count == 0 || receives[0].Hq ||
            costs.Length != 1 || costs[0].ItemId != CurrencyItemId || costs[0].Price == 0 ||
            costs[0].Collectability != 0 || costs[0].CostType != 0) return null;
        return new(shopId, receives[0].ItemId, receives[0].Count, costs[0].Price, shopName);
    }
}
