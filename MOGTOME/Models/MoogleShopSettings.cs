using System;
using System.Collections.Generic;
using System.Linq;

namespace MOGTOME.Models;

public enum MoogleShopCity { Limsa, Uldah, Gridania }

public sealed class MoogleShopSettings
{
    public MoogleShopCity City { get; set; }
    public Dictionary<uint, int> PurchaseTargets { get; set; } = [];
    public bool PurchaseReviewRequired { get; set; }
    public bool HideOwned { get; set; }
}

internal sealed record MoogleShopCost(uint ItemId, uint Count);
internal sealed record MoogleShopOffer(uint ShopId, string ShopName, uint ItemId, uint ReceiveCount,
    uint TomestoneId, MoogleShopCost[] Costs)
{
    internal ShopOfferGate Gate { get; init; } = new();
}

internal sealed record TomestoneStack(int Bag, ushort Slot, uint ItemId, uint Quantity, bool Mergeable = true);
internal sealed record TomestoneMove(TomestoneStack Source, TomestoneStack Destination, uint Transfer, long Total);

internal static class MoogleShopMath
{
    internal static long Transactions(int desired, int current, uint receive)
        => receive == 0 ? 0 : (Math.Max(0L, (long)desired - current) + receive - 1) / receive;

    internal static Dictionary<uint, ulong> TotalCosts(IReadOnlyDictionary<uint, int> targets,
        IReadOnlyDictionary<uint, MoogleShopOffer> offers, Func<uint, int?> inventory, out bool known)
    {
        var costs = new Dictionary<uint, ulong>();
        known = true;
        foreach (var target in targets.Where(target => target.Value > 0))
        {
            var count = inventory(target.Key);
            if (!count.HasValue || count.Value < 0) { known = false; continue; }
            if (count.Value >= target.Value) continue;
            if (!offers.TryGetValue(target.Key, out var offer) || offer.ReceiveCount == 0) { known = false; continue; }
            var transactions = Transactions(target.Value, count.Value, offer.ReceiveCount);
            try
            {
                foreach (var cost in offer.Costs)
                    costs[cost.ItemId] = checked(costs.GetValueOrDefault(cost.ItemId) + (ulong)transactions * cost.Count);
            }
            catch (OverflowException) { known = false; }
        }
        return costs;
    }

    internal static bool FestivalEligible(uint required, uint phase, IEnumerable<(uint Id, uint Phase)> active)
        => required == 0 || active.Any(f => f.Id == required && (phase == 0 || f.Phase == phase));

    internal static MoogleShopOffer? ReadOffer(uint shop, string name,
        IEnumerable<(uint ItemId, uint Count, bool Hq)> rewards,
        IEnumerable<(uint ItemId, uint Count, uint Collectability, byte Type)> costs, ISet<uint> tomestones)
    {
        var received = rewards.Where(r => r.ItemId != 0).ToArray();
        var paid = costs.Where(c => c.ItemId != 0 || c.Count != 0 || c.Collectability != 0).ToArray();
        if (received.Length != 1 || received[0].Count == 0 || received[0].Hq || paid.Length is < 1 or > 3 ||
            paid.Any(c => c.ItemId == 0 || c.Count == 0 || c.Collectability != 0 || c.Type != 0) ||
            paid.Select(c => c.ItemId).Distinct().Count() != paid.Length) return null;
        var tokens = paid.Where(c => tomestones.Contains(c.ItemId)).ToArray();
        if (tokens.Length != 1) return null;
        return new(shop, name, received[0].ItemId, received[0].Count, tokens[0].ItemId,
            paid.Select(c => new MoogleShopCost(c.ItemId, c.Count)).ToArray());
    }

    internal static TomestoneMove? PlanMove(IReadOnlyList<TomestoneStack> stacks, uint currency)
    {
        var partial = stacks.Where(s => s.Mergeable && s.ItemId == currency && s.Quantity is > 0 and < 999)
            .OrderByDescending(s => s.Quantity).ThenBy(s => s.Bag).ThenBy(s => s.Slot).ToArray();
        if (partial.Length < 2) return null;
        var destination = partial[0];
        var source = partial[^1];
        return new(source, destination, Math.Min(source.Quantity, 999 - destination.Quantity),
            stacks.Where(s => s.ItemId == currency).Sum(s => (long)s.Quantity));
    }

    internal static bool MoveVerified(TomestoneMove move, IReadOnlyList<TomestoneStack> stacks)
    {
        var source = stacks.SingleOrDefault(s => s.Bag == move.Source.Bag && s.Slot == move.Source.Slot);
        var destination = stacks.SingleOrDefault(s => s.Bag == move.Destination.Bag && s.Slot == move.Destination.Slot);
        var remainder = move.Source.Quantity - move.Transfer;
        return (remainder == 0 ? source == null : source?.ItemId == move.Source.ItemId && source.Quantity == remainder) &&
            destination?.ItemId == move.Source.ItemId && destination.Quantity == move.Destination.Quantity + move.Transfer &&
            stacks.Where(s => s.ItemId == move.Source.ItemId).Sum(s => (long)s.Quantity) == move.Total;
    }

    internal static bool ReceiptVerified(MoogleShopOffer offer, int beforeItem, IReadOnlyDictionary<uint, int> before,
        int item, IReadOnlyDictionary<uint, int> balances)
        => (long)item == (long)beforeItem + offer.ReceiveCount && offer.Costs.All(c =>
            before.TryGetValue(c.ItemId, out var old) && balances.TryGetValue(c.ItemId, out var now) && (long)now + c.Count == old);
}
