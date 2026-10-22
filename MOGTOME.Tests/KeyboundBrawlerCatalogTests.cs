using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class KeyboundBrawlerCatalogTests
{
    // Item actions/equipment and the complete reward list verified against the current game sheets.
    private static readonly (uint Id, uint Action, bool Equipment, string Category)[] Rewards =
    [
        (52323, 0, true, "KB_CategoryGlamour"),
        (52324, 0, true, "KB_CategoryGlamour"),
        (52325, 0, true, "KB_CategoryGlamour"),
        (52326, 0, true, "KB_CategoryGlamour"),
        (52327, 0, true, "KB_CategoryGlamour"),
        (52328, 0, true, "KB_CategoryGlamour"),
        (52329, 0, true, "KB_CategoryGlamour"),
        (52330, 0, true, "KB_CategoryGlamour"),
        (52331, 0, true, "KB_CategoryGlamour"),
        (52332, 0, true, "KB_CategoryGlamour"),
        (52647, 0, true, "KB_CategoryGlamour"),
        (52287, 20086, false, "KB_CategoryFashionAccessory"),
        (52261, 1322, false, "KB_CategoryMount"),
        (52283, 853, false, "KB_CategoryMinion"),
        (52354, 25183, false, "KB_CategoryOrchestrion"),
        (52355, 25183, false, "KB_CategoryOrchestrion"),
        (52356, 25183, false, "KB_CategoryOrchestrion"),
        (51997, 29459, false, "KB_CategoryFramerKit"),
        (52291, 2633, false, "KB_CategoryEmote"),
    ];

    public static IEnumerable<object[]> VerifiedRewards => Rewards.Select(r =>
        new object[] { r.Id, r.Action, r.Equipment, r.Category });

    [Theory]
    [MemberData(nameof(VerifiedRewards))]
    public void EveryVerifiedRewardHasItsActualCategoryAndAnEligibleNativeOwnershipKind(
        uint itemId, uint action, bool equipment, string category)
    {
        Assert.True(KeyboundBrawlerShop.IsSelectMissingReward(itemId));
        Assert.Equal(category, KeyboundBrawlerShop.CategoryKey(itemId));
        Assert.Equal(equipment ? ShopMissingKind.ShopEquipment : ShopMissingKind.Collectible,
            ShopMissingSelection.Classify(action, equipment, false, itemId, verifiedShopEquipment: true));
    }

    [Fact]
    public void SelectingAllNineteenMissingRewardsKeepsLargerAndManualCofferTargets()
    {
        var targets = new Dictionary<uint, int> { [52287] = 4, [KeyboundBrawlerShop.CofferItemId] = 3 };
        var entries = Rewards.Select(r => (r.Id,
                ShopMissingSelection.Classify(r.Action, r.Equipment, false, r.Id, verifiedShopEquipment: true),
                BlundervilleRegistration.Missing, ShopOfferAvailability.Available))
            .Append((KeyboundBrawlerShop.CofferItemId, ShopMissingKind.None,
                BlundervilleRegistration.Missing, ShopOfferAvailability.Available))
            .Where(r => KeyboundBrawlerShop.IsSelectMissingReward(r.Item1)).ToArray();

        Assert.Equal(19, entries.Length);
        Assert.Equal(new ShopMissingResult(18, 0, 0, 0), ShopMissingSelection.Apply(targets, entries));
        Assert.All(Rewards.Where(r => r.Id != 52287), r => Assert.Equal(1, targets[r.Id]));
        Assert.Equal(4, targets[52287]);
        Assert.Equal(3, targets[KeyboundBrawlerShop.CofferItemId]);
        Assert.Equal(20, targets.Count);
        Assert.Equal(0, ShopMissingSelection.Apply(targets, entries).Added);

        var coffer = KeyboundBrawlerShop.ReadOffer(KeyboundBrawlerShop.ShopId, "", [(52450, 1, false)],
            [(KeyboundBrawlerShop.CurrencyItemId, 910, 0, 0)]);
        Assert.NotNull(coffer);
        Assert.Equal("KB_CategoryCoffer", KeyboundBrawlerShop.CategoryKey(coffer.ItemId));
        Assert.Equal(1820ul, BlundervilleProgress.TotalMgf(
            new Dictionary<uint, int> { [coffer.ItemId] = targets[coffer.ItemId] },
            new Dictionary<uint, BlundervilleOffer> { [coffer.ItemId] = coffer }, _ => 1, out var known));
        Assert.True(known);
    }

    [Fact]
    public void MissingSelectionSkipsOwnedUnknownAndLockedRewards()
    {
        var targets = new Dictionary<uint, int>();
        var entries = new[]
        {
            (52323u, ShopMissingKind.ShopEquipment, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
            (52324u, ShopMissingKind.ShopEquipment, BlundervilleRegistration.Owned, ShopOfferAvailability.Available),
            (52325u, ShopMissingKind.ShopEquipment, BlundervilleRegistration.Unknown, ShopOfferAvailability.Available),
            (52326u, ShopMissingKind.ShopEquipment, BlundervilleRegistration.Missing, ShopOfferAvailability.Unknown),
            (52327u, ShopMissingKind.ShopEquipment, BlundervilleRegistration.Missing, ShopOfferAvailability.Locked),
            (52450u, ShopMissingKind.None, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
            (999999u, ShopMissingKind.Collectible, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
        }.Where(r => KeyboundBrawlerShop.IsSelectMissingReward(r.Item1));

        Assert.Equal(new ShopMissingResult(1, 1, 0, 1), ShopMissingSelection.Apply(targets, entries));
        Assert.Equal(1, targets[52323]);
        Assert.Single(targets);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(52322u)]
    [InlineData(52450u)]
    [InlineData(52333u)]
    [InlineData(999999u)]
    public void CurrencyCofferAndUnverifiedFutureRewardsNeverEnterSelectMissing(uint itemId)
        => Assert.False(KeyboundBrawlerShop.IsSelectMissingReward(itemId));

    [Fact]
    public void FutureNativeOffersRemainAvailableForManualTargets()
    {
        const uint futureItem = 999999;
        var offer = KeyboundBrawlerShop.ReadOffer(KeyboundBrawlerShop.ShopId, "", [(futureItem, 2, false)],
            [(KeyboundBrawlerShop.CurrencyItemId, 123, 0, 0)]);
        Assert.NotNull(offer);
        Assert.Equal(2u, offer.ReceiveCount);
        Assert.Equal(123u, offer.Price);
        Assert.Equal("KB_CategoryOther", KeyboundBrawlerShop.CategoryKey(futureItem));
        Assert.False(KeyboundBrawlerShop.IsSelectMissingReward(futureItem));
    }
}
