using System;
using System.Collections.Generic;

namespace MOGTOME.Models;

// Catalog construction supplies the actual GC-eligible item and its seal value.
internal sealed record PoeticsShopOffer(MoogleShopOffer Offer, uint SealValue);

internal static class PoeticsShopping
{
    internal static bool CanSkipVendor(bool purchaseInFlight, bool reviewRequired, bool purchaseDialogVisible, bool finishing)
        => !purchaseInFlight && !reviewRequired && !purchaseDialogVisible && !finishing;

    internal static ShopOfferAvailability ResolveVendorAvailability(ShopOfferAvailability offer,
        IEnumerable<ShopOfferAvailability> vendors)
    {
        if (offer == ShopOfferAvailability.Locked) return offer;
        var any = false;
        var unknown = false;
        foreach (var vendor in vendors)
        {
            any = true;
            if (vendor == ShopOfferAvailability.Available) return offer;
            if (vendor != ShopOfferAvailability.Locked) unknown = true;
        }
        return !any || unknown ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Locked;
    }

    internal static uint ResolveCurrency(byte shopCurrencyType, byte costType, uint rawItemId,
        IReadOnlyDictionary<uint, uint> tomestones)
    {
        if (shopCurrencyType is not (2 or 4 or 16)) return 0;
        return costType switch
        {
            0 => rawItemId,
            2 => tomestones.TryGetValue(rawItemId, out var item) ? item : 0,
            _ => 0,
        };
    }

    internal static bool DeliverySettled(bool admitted, bool? busy, bool purchasedGearGone,
        bool cancelled, bool sessionMatches, DateTime now, ref DateTime? idleSince)
    {
        if (!admitted || busy != false || !purchasedGearGone || cancelled || !sessionMatches)
        {
            idleSince = null;
            return false;
        }
        idleSince ??= now;
        return now - idleSince.Value >= TimeSpan.FromSeconds(10);
    }

    internal static MoogleShopOffer? Select(
        IEnumerable<PoeticsShopOffer> offers, uint currencyItemId, int wallet,
        Func<uint, int?> owned, Func<MoogleShopOffer, ShopOfferAvailability> availability,
        out bool inventoryKnown, out bool eligibilityKnown, Func<MoogleShopOffer, bool>? reachable = null)
    {
        inventoryKnown = wallet >= 0;
        eligibilityKnown = true;
        if (!inventoryKnown) return null;
        if (currencyItemId == 0) { eligibilityKnown = false; return null; }
        PoeticsShopOffer? best = null;
        var uncertainInventory = new List<PoeticsShopOffer>();
        foreach (var candidate in offers)
        {
            var offer = candidate.Offer;
            if (candidate.SealValue == 0 || offer.ReceiveCount != 1 || offer.TomestoneId != currencyItemId ||
                offer.Costs.Length != 1 || offer.Costs[0].ItemId != currencyItemId ||
                offer.Costs[0].Count == 0 || offer.Costs[0].Count > wallet)
                continue;
            var gate = availability(offer);
            // An unresolved vendor or unlock is skipped; it is never treated as permission to buy.
            if (gate != ShopOfferAvailability.Available) continue;
            if (reachable != null && !reachable(offer)) continue;
            var count = owned(offer.ItemId);
            if (!count.HasValue || count.Value < 0)
            {
                uncertainInventory.Add(candidate);
                continue;
            }
            if (count.Value != 0) continue;
            if (best == null || Better(candidate, best)) best = candidate;
        }
        foreach (var candidate in uncertainInventory)
        {
            if (best != null)
            {
                // Another shop offering the same purchase adds no better item or price.
                if (candidate.Offer.ItemId == best.Offer.ItemId && candidate.SealValue == best.SealValue &&
                    candidate.Offer.Costs[0].Count == best.Offer.Costs[0].Count)
                    continue;
                if (!Better(candidate, best)) continue;
            }
            inventoryKnown = false;
        }
        // Inventory uncertainty on an eligible offer still prevents an unverified choice or completion.
        return inventoryKnown && eligibilityKnown ? best?.Offer : null;
    }

    private static bool Better(PoeticsShopOffer candidate, PoeticsShopOffer current)
    {
        var cost = candidate.Offer.Costs[0].Count;
        var otherCost = current.Offer.Costs[0].Count;
        var value = (ulong)candidate.SealValue * otherCost;
        var otherValue = (ulong)current.SealValue * cost;
        if (value != otherValue) return value > otherValue;
        if (cost != otherCost) return cost < otherCost;
        if (candidate.Offer.ItemId != current.Offer.ItemId) return candidate.Offer.ItemId < current.Offer.ItemId;
        return candidate.Offer.ShopId < current.Offer.ShopId;
    }
}
