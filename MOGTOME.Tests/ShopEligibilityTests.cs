using System.Text.Json;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class ShopEligibilityTests
{
    [Fact]
    public void UngatedOffersDoNotRequireUnrelatedNativeData()
    {
        var gate = new ShopOfferGate();
        Assert.Equal(ShopOfferAvailability.Available, gate.Resolve(_ => throw new Exception(), _ => throw new Exception(),
            (_, _) => throw new Exception(), (_, _) => throw new Exception()));
    }

    [Theory]
    [InlineData(null, null, (int)ShopOfferAvailability.Unknown)]
    [InlineData(true, null, (int)ShopOfferAvailability.Unknown)]
    [InlineData(null, false, (int)ShopOfferAvailability.Locked)]
    [InlineData(false, null, (int)ShopOfferAvailability.Locked)]
    [InlineData(true, true, (int)ShopOfferAvailability.Available)]
    public void UnobservedPrerequisiteStaysUnknownWhileConfirmedLockWins(bool? quest, bool? achievement, int expected)
    {
        var gate = new ShopOfferGate(ShopQuest: 10, ItemQuest: 11, Achievement: 12);
        Assert.Equal((ShopOfferAvailability)expected, gate.Resolve(id => id == 10 ? true : quest, _ => achievement, (_, _) => null, (_, _) => null));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ContentUnlockAndCompletionRequirementsRemainDistinct(bool completed)
    {
        var gate = new ShopOfferGate(ContentFinderCondition: 123, ContentComplete: completed, Festival: 8, FestivalPhase: 2);
        Assert.Equal(ShopOfferAvailability.Available, gate.Resolve(_ => null, _ => null,
            (id, completion) => { Assert.Equal(123u, id); Assert.Equal(completed, completion); return true; },
            (id, phase) => { Assert.Equal(8u, id); Assert.Equal(2u, phase); return true; }));
        Assert.Equal(ShopOfferAvailability.Locked, gate.Resolve(_ => null, _ => null, (_, _) => true, (_, _) => false));
    }

    [Fact]
    public void HiddenTargetsRetainBothCurrenciesAndOnlyCarriedDeficitsReduceCost()
    {
        var settings = new MoogleShopSettings { HideOwned = true, PurchaseTargets = new() { [100] = 3, [101] = 1 } };
        var offers = new Dictionary<uint, MoogleShopOffer>
        {
            [100] = new(1, "Exchange", 100, 1, 25, [new(25, 10), new(26, 2)]),
            [101] = new(1, "Exchange", 101, 1, 25, [new(25, 50)]),
        };
        // Item 100 may be hidden as owned in storage; it still needs two carried copies.
        var total = MoogleShopMath.TotalCosts(settings.PurchaseTargets, offers, _ => 1, out var known);
        Assert.True(known);
        Assert.Equal(20ul, total[25]); Assert.Equal(4ul, total[26]);
        Assert.Equal(3, settings.PurchaseTargets[100]); Assert.Equal(1, settings.PurchaseTargets[101]);
    }

    [Fact]
    public void HiddenMgfTargetKeepsItsCostAndSatisfiedMissingOfferNeedsNoPrice()
    {
        var settings = new BlundervilleSettings { HideOwned = true, PurchaseTargets = new() { [100] = 3, [101] = 1 } };
        var offers = new Dictionary<uint, BlundervilleOffer> { [100] = new(1, 100, 1, 6, "Exchange") };
        var total = BlundervilleProgress.TotalMgf(settings.PurchaseTargets, offers, _ => 1, out var known);
        Assert.True(known); Assert.Equal(12ul, total);
        Assert.Equal(3, settings.PurchaseTargets[100]);
    }

    [Theory]
    [InlineData(null)] [InlineData(-1)]
    public void UnavailableInventoryCannotProduceKnownTotals(int? inventory)
    {
        var targets = new Dictionary<uint, int> { [100] = 1 };
        MoogleShopMath.TotalCosts(targets, new Dictionary<uint, MoogleShopOffer>(), _ => inventory, out var mogKnown);
        BlundervilleProgress.TotalMgf(targets, new Dictionary<uint, BlundervilleOffer>(), _ => inventory, out var bvKnown);
        Assert.False(mogKnown); Assert.False(bvKnown);
    }

    [Fact]
    public void MissingOffersAndNonIntegralMgfBundlesStayUncertain()
    {
        var targets = new Dictionary<uint, int> { [100] = 1 };
        MoogleShopMath.TotalCosts(targets, new Dictionary<uint, MoogleShopOffer>(), _ => 0, out var mogKnown);
        BlundervilleProgress.TotalMgf(targets, new Dictionary<uint, BlundervilleOffer> { [100] = new(1, 100, 2, 6, "Exchange") },
            _ => 0, out var bvKnown);
        Assert.False(mogKnown); Assert.False(bvKnown);
    }

    [Fact]
    public void EachShopDefaultsToShowingOwnedAndPersistsItsIndependentChoice()
    {
        var legacy = JsonSerializer.Deserialize<Configuration>("{\"Blunderville\":{},\"MoogleShop\":{}}")!;
        Assert.False(legacy.Blunderville.HideOwned); Assert.False(legacy.MoogleShop.HideOwned);
        legacy.Blunderville.HideOwned = true;
        legacy.Blunderville.PurchaseTargets[100] = 7;
        legacy.MoogleShop.PurchaseTargets[101] = 3;
        var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(legacy))!;
        Assert.True(restored.Blunderville.HideOwned); Assert.False(restored.MoogleShop.HideOwned);
        Assert.Equal(7, restored.Blunderville.PurchaseTargets[100]); Assert.Equal(3, restored.MoogleShop.PurchaseTargets[101]);
    }
}
