using MOGTOME.Models;
using MOGTOME.Services;
using System.Numerics;
using System.Text.Json;

namespace MOGTOME.Tests;

public sealed class PoeticsShoppingTests
{
    [Fact]
    public void LegacyProfilesKeepPoeticsDisabledAndItsInnIndependentOfOtherModes()
    {
        var config = JsonSerializer.Deserialize<Configuration>("""
            {"MoogleShop":{"City":1,"HideOwned":true,"PurchaseTargets":{"100":2}},
             "Blunderville":{"EndingLocation":3},"KeyboundBrawler":{"EndingLocation":5}}
            """)!;
        Assert.False(config.MoogleShop.BuyPoeticsGear);
        Assert.False(config.MoogleShop.DeliverPoeticsGear);
        Assert.Equal(BlundervilleEndingLocation.Stay, config.MoogleShop.PoeticsEndingLocation);
        config.MoogleShop.BuyPoeticsGear = config.MoogleShop.DeliverPoeticsGear = true;
        config.MoogleShop.PoeticsEndingLocation = BlundervilleEndingLocation.Tuliyollal;
        var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
        Assert.True(restored.MoogleShop.BuyPoeticsGear);
        Assert.True(restored.MoogleShop.DeliverPoeticsGear);
        Assert.Equal(BlundervilleEndingLocation.Tuliyollal, restored.MoogleShop.PoeticsEndingLocation);
        Assert.Equal(MoogleShopCity.Uldah, restored.MoogleShop.City);
        Assert.True(restored.MoogleShop.HideOwned);
        Assert.Equal(2, restored.MoogleShop.PurchaseTargets[100]);
        Assert.Equal(BlundervilleEndingLocation.Limsa, restored.Blunderville.EndingLocation);
        Assert.Equal(BlundervilleEndingLocation.Crystarium, restored.KeyboundBrawler.EndingLocation);
    }

    [Theory]
    [InlineData(2, 2, 1u, 28u)]
    [InlineData(4, 2, 1u, 28u)]
    [InlineData(16, 2, 1u, 28u)]
    [InlineData(16, 0, 1u, 1u)]
    [InlineData(16, 0, 28u, 28u)]
    [InlineData(2, 2, 28u, 0u)]
    [InlineData(5, 2, 1u, 0u)]
    [InlineData(16, 1, 1u, 0u)]
    [InlineData(2, 2, 9u, 0u)]
    public void ActualCostTypeDistinguishesTomestoneSlotsFromLiteralItems(
        byte shopType, byte costType, uint rawItemId, uint expected)
    {
        var tomestones = new Dictionary<uint, uint> { [1] = 28, [3] = 900003 };
        Assert.Equal(expected, PoeticsShopping.ResolveCurrency(shopType, costType, rawItemId, tomestones));
    }

    // Fabricated currency, shop and item identities; no game sheets or native state.
    private const uint Currency = 900001;
    private const uint OtherCurrency = 900002;

    [Fact]
    public void AReselectedOfferCannotKeepTheFirstTravelVendorsUnrelatedNpc()
    {
        var previous = new PoeticsShopVendor(910001, 920001, new(1, 2, 3), ["Fixture first exchange"]);
        var next = new PoeticsShopVendor(910002, 920001, new(1, 2, 3), ["Fixture next exchange"]);
        Assert.Null(previous.MatchCurrent([next]));
    }

    [Fact]
    public void CatalogRefreshReusesTheVendorLocationAndRefreshesItsCurrentOfferMenuLabels()
    {
        var previous = new PoeticsShopVendor(910001, 920001, new(1, 2, 3), ["Fixture old exchange"]);
        var current = new PoeticsShopVendor(910001, 920001, new(1, 2, 3), ["Fixture current exchange"]);
        Assert.Same(current, previous.MatchCurrent([current]));
        Assert.Equal("Fixture current exchange", previous.MatchCurrent([current])!.MenuLabels.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSameNpcAtAnotherTerritoryOrPlacementDoesNotValidateACachedRoute(bool differentTerritory)
    {
        var previous = new PoeticsShopVendor(910001, 920001, new(1, 2, 3), ["Fixture exchange"]);
        var current = differentTerritory
            ? previous with { TerritoryId = 920002 }
            : previous with { Position = new Vector3(4, 5, 6) };
        Assert.Null(previous.MatchCurrent([current]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailableVendorsPreferTheCurrentTerritoryThenNpcIdentity(bool reverse)
    {
        const uint here = 920001;
        var remote = Vendor(910001, 920002);
        var localHigherId = Vendor(910003, here);
        var localLowerId = Vendor(910002, here);
        var unknown = Vendor(910000, here);
        var locked = Vendor(910004, here);
        PoeticsShopVendor[] candidates = [remote, localHigherId, unknown, locked, localLowerId];
        ShopOfferAvailability Availability(PoeticsShopVendor vendor)
            => vendor == unknown ? ShopOfferAvailability.Unknown
                : vendor == locked ? ShopOfferAvailability.Locked : ShopOfferAvailability.Available;

        Assert.Same(localLowerId, PoeticsShopVendor.SelectAvailable(reverse ? candidates.Reverse() : candidates,
            here, Availability));
        Assert.Same(remote, PoeticsShopVendor.SelectAvailable(candidates, here, vendor =>
            vendor.TerritoryId == here ? ShopOfferAvailability.Locked : ShopOfferAvailability.Available));
    }

    [Fact]
    public void APrepurchaseVendorFailureUsesAnotherVendorForTheSameBestOffer()
    {
        const uint here = 920001;
        var vendorA = Vendor(910010, here);
        var vendorB = Vendor(910001, 920002);
        var lowerValueVendor = Vendor(910000, here);
        var best = Offer(100, 10, 1000, 10);
        var lowerValue = Offer(101, 10, 100, 20);
        PoeticsShopOffer[] offers = [best, lowerValue];
        var vendors = new Dictionary<uint, PoeticsShopVendor[]>
        {
            [best.Offer.ShopId] = [vendorA, vendorB],
            [lowerValue.Offer.ShopId] = [lowerValueVendor],
        };
        var failedNpcs = new HashSet<uint>();
        ShopOfferAvailability Availability(PoeticsShopVendor vendor)
            => failedNpcs.Contains(vendor.NpcId) ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available;
        (MoogleShopOffer? Offer, PoeticsShopVendor? Vendor) Plan()
        {
            var selected = PoeticsShopping.Select(offers, Currency, 100, _ => 0,
                offer => PoeticsShopping.ResolveVendorAvailability(ShopOfferAvailability.Available,
                    vendors[offer.ShopId].Select(Availability)), out var inventoryKnown, out var eligibilityKnown);
            Assert.True(inventoryKnown);
            Assert.True(eligibilityKnown);
            return (selected, selected == null ? null : PoeticsShopVendor.SelectAvailable(vendors[selected.ShopId], here, Availability));
        }

        var first = Plan();
        Assert.Same(best.Offer, first.Offer);
        Assert.Same(vendorA, first.Vendor);
        Assert.True(PoeticsShopping.CanSkipVendor(false, false, false, false));
        Assert.True(failedNpcs.Add(vendorA.NpcId));
        var second = Plan();
        Assert.Same(first.Offer, second.Offer);
        Assert.Same(vendorB, second.Vendor);
        Assert.Single(failedNpcs);
        Assert.DoesNotContain(vendorB.NpcId, failedNpcs);
    }

    [Fact]
    public void AnUnknownVendorCanBecomeAvailableWithoutDiscardingAKnownAlternative()
    {
        const uint here = 920001;
        var local = Vendor(910001, here);
        var remote = Vendor(910002, 920002);
        var localAvailability = ShopOfferAvailability.Unknown;
        ShopOfferAvailability Availability(PoeticsShopVendor vendor)
            => vendor == local ? localAvailability : ShopOfferAvailability.Available;

        Assert.Null(PoeticsShopVendor.SelectAvailable([local], here, Availability));
        Assert.Same(remote, PoeticsShopVendor.SelectAvailable([local, remote], here, Availability));
        Assert.Equal(ShopOfferAvailability.Available, PoeticsShopping.ResolveVendorAvailability(
            ShopOfferAvailability.Available, new[] { local, remote }.Select(Availability)));
        localAvailability = ShopOfferAvailability.Available;
        Assert.Same(local, PoeticsShopVendor.SelectAvailable([remote, local], here, Availability));
    }

    [Fact]
    public void ExhaustingAllVendorsTerminatesAfterEachNpcWasTriedOnce()
    {
        const uint here = 920001;
        PoeticsShopVendor[] candidates = [Vendor(910003, 920002), Vendor(910002, here), Vendor(910001, here)];
        var failedNpcs = new HashSet<uint>();
        var attempted = new List<uint>();
        PoeticsShopVendor? NextVendor() => PoeticsShopVendor.SelectAvailable(
            candidates.Where(vendor => !failedNpcs.Contains(vendor.NpcId)), here, _ => ShopOfferAvailability.Available);

        for (var attempt = 0; attempt < candidates.Length; attempt++)
        {
            var next = NextVendor();
            Assert.NotNull(next);
            attempted.Add(next!.NpcId);
            Assert.True(failedNpcs.Add(next.NpcId));
        }
        Assert.Equal(new uint[] { 910001, 910002, 910003 }, attempted);
        Assert.Null(NextVendor());
        var selected = PoeticsShopping.Select([Offer(100, 10, 100)], Currency, 100,
            _ => throw new Exception("Exhausted vendor stock must not be queried"), _ => ShopOfferAvailability.Available,
            out var inventoryKnown, out var eligibilityKnown, _ => NextVendor() != null);
        Assert.Null(selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Fact]
    public void ANewRunsFailureSetDoesNotInheritThePreviousRunsExhaustion()
    {
        const uint here = 920001;
        var local = Vendor(910001, here);
        var remote = Vendor(910002, 920002);
        PoeticsShopVendor[] candidates = [remote, local];
        var firstRunFailures = candidates.Select(vendor => vendor.NpcId).ToHashSet();
        PoeticsShopVendor? Plan(HashSet<uint> failures) => PoeticsShopVendor.SelectAvailable(
            candidates.Where(vendor => !failures.Contains(vendor.NpcId)), here, _ => ShopOfferAvailability.Available);

        Assert.Null(Plan(firstRunFailures));
        var nextRunFailures = new HashSet<uint>();
        Assert.Same(local, Plan(nextRunFailures));
        Assert.Empty(nextRunFailures);
        Assert.Equal(2, firstRunFailures.Count);
    }

    [Fact]
    public void VendorRecoveryReopensOnlyAfterThePurchaseIsFullySettledAndBeforeFinishing()
    {
        Assert.True(PoeticsShopping.CanSkipVendor(false, false, false, false));
        Assert.False(PoeticsShopping.CanSkipVendor(true, true, true, false));
        Assert.False(PoeticsShopping.CanSkipVendor(true, false, false, false));
        Assert.True(PoeticsShopping.CanSkipVendor(false, false, false, false));
        Assert.False(PoeticsShopping.CanSkipVendor(false, false, false, true));
    }

    [Theory]
    [InlineData("submitted purchase", true, false, false, false)]
    [InlineData("pending receipt", true, false, false, false)]
    [InlineData("settling purchase", true, false, false, false)]
    [InlineData("pending inventory move", true, false, false, false)]
    [InlineData("purchase review", false, true, false, false)]
    [InlineData("purchase dialog", false, false, true, false)]
    [InlineData("delivery or return finishing", false, false, false, true)]
    [InlineData("overlapping owned work", true, true, true, true)]
    public void OwnedTransactionStatesCannotBeReinterpretedAsPrepurchaseVendorFailures(
        string state, bool purchaseInFlight, bool reviewRequired, bool purchaseDialogVisible, bool finishing)
        => Assert.False(PoeticsShopping.CanSkipVendor(purchaseInFlight, reviewRequired, purchaseDialogVisible, finishing), state);

    [Theory]
    [InlineData("Available", "Available", "Available")]
    [InlineData("Available", "Locked", "Locked")]
    [InlineData("Available", "Unknown", "Unknown")]
    [InlineData("Unknown", "Available", "Unknown")]
    [InlineData("Unknown", "Locked", "Locked")]
    [InlineData("Unknown", "Unknown", "Unknown")]
    [InlineData("Locked", "Available", "Locked")]
    [InlineData("Locked", "Locked", "Locked")]
    [InlineData("Locked", "Unknown", "Locked")]
    public void VendorUnlocksDoNotReplaceTheOriginalShopAndItemGates(
        string offer, string vendor, string expected)
        => Assert.Equal(Enum.Parse<ShopOfferAvailability>(expected), PoeticsShopping.ResolveVendorAvailability(
            Enum.Parse<ShopOfferAvailability>(offer), [Enum.Parse<ShopOfferAvailability>(vendor)]));

    [Theory]
    [InlineData("Available", "Unknown")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("Locked", "Locked")]
    public void MissingVendorEvidenceIsUnknownUnlessTheOfferIsAlreadyProvenLocked(
        string offer, string expected)
        => Assert.Equal(Enum.Parse<ShopOfferAvailability>(expected), PoeticsShopping.ResolveVendorAvailability(
            Enum.Parse<ShopOfferAvailability>(offer), []));

    [Fact]
    public void AnAvailableAlternativeVendorIsNotBlockedByAnotherVendorsUnknownStatus()
    {
        ShopOfferAvailability[] vendors = [ShopOfferAvailability.Unknown, ShopOfferAvailability.Locked,
            ShopOfferAvailability.Available];
        Assert.Equal(ShopOfferAvailability.Available,
            PoeticsShopping.ResolveVendorAvailability(ShopOfferAvailability.Available, vendors));
        Assert.Equal(ShopOfferAvailability.Available,
            PoeticsShopping.ResolveVendorAvailability(ShopOfferAvailability.Available, vendors.Reverse()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnresolvedVendorIsSkippedBeforeConsideringKnownAvailablePurchases(bool includeKnownAlternative)
    {
        var unresolved = Offer(100, 10, 1000);
        var alternative = Offer(101, 10, 100);
        var selected = PoeticsShopping.Select(includeKnownAlternative ? [unresolved, alternative] : [unresolved],
            Currency, 100, id => id == unresolved.Offer.ItemId
                ? throw new Exception("Unresolved vendor stock must not be queried") : 0,
            offer => PoeticsShopping.ResolveVendorAvailability(ShopOfferAvailability.Available,
                offer.ItemId == unresolved.Offer.ItemId ? [] : [ShopOfferAvailability.Available]),
            out var inventoryKnown, out var eligibilityKnown, _ => true);
        Assert.Same(includeKnownAlternative ? alternative.Offer : null, selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Fact]
    public void AProvenVendorLockSkipsStockBeforeRankingTheNextAvailablePurchase()
    {
        var locked = Offer(100, 10, 1000);
        var available = Offer(101, 20, 100);
        var selected = PoeticsShopping.Select([locked, available], Currency, 100,
            id => id == locked.Offer.ItemId ? throw new Exception("Locked stock must not be queried") : 0,
            offer => PoeticsShopping.ResolveVendorAvailability(ShopOfferAvailability.Available,
                [offer.ItemId == locked.Offer.ItemId ? ShopOfferAvailability.Locked : ShopOfferAvailability.Available]),
            out var inventoryKnown, out var eligibilityKnown, _ => true);
        Assert.Same(available.Offer, selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAvailableUnknownStockCanBlockAfterTheKnownBestPurchaseIsOwned(bool unknownInventory)
    {
        var best = Offer(100, 10, 1000);
        var unknown = Offer(101, 10, 100);
        int? Stock(uint id, bool purchased) => id == best.Offer.ItemId ? purchased ? 1 : 0 : unknownInventory ? null : 0;
        ShopOfferAvailability Availability(MoogleShopOffer offer)
            => offer.ItemId == unknown.Offer.ItemId && !unknownInventory
                ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available;

        var selected = PoeticsShopping.Select([unknown, best], Currency, 100, id => Stock(id, false), Availability,
            out var inventoryKnown, out var eligibilityKnown);
        Assert.Same(best.Offer, selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);

        selected = PoeticsShopping.Select([best, unknown], Currency, 100, id => Stock(id, true), Availability,
            out inventoryKnown, out eligibilityKnown);
        Assert.Null(selected);
        Assert.Equal(!unknownInventory, inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACheaperAvailableUnknownStockContenderBlocksButAnUnknownUnlockIsSkipped(bool unknownInventory)
    {
        var known = Offer(100, 20, 200);
        var cheaper = Offer(101, 10, 100);
        var selected = PoeticsShopping.Select([known, cheaper], Currency, 100,
            id => id == cheaper.Offer.ItemId && unknownInventory ? null : 0,
            offer => offer.ItemId == cheaper.Offer.ItemId && !unknownInventory
                ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available,
            out var inventoryKnown, out var eligibilityKnown);
        Assert.Same(unknownInventory ? null : known.Offer, selected);
        Assert.Equal(!unknownInventory, inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnknownDuplicateShopDoesNotBlockTheSamePurchaseFromAProvenVendor(bool reverse)
    {
        var unresolved = Offer(100, 10, 100, 1);
        var known = Offer(100, 10, 100, 2);
        var selected = PoeticsShopping.Select(reverse ? [known, unresolved] : [unresolved, known], Currency, 100,
            _ => 0, offer => offer.ShopId == unresolved.Offer.ShopId
                ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available,
            out var inventoryKnown, out var eligibilityKnown);
        Assert.Same(known.Offer, selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Fact]
    public void UnreachableBestOfferDoesNotBlockTheBestReachablePurchase()
    {
        var unavailable = Offer(100, 10, 1000, 10);
        var reachable = Offer(101, 20, 100, 20);
        var selected = PoeticsShopping.Select([unavailable, reachable], Currency, 100,
            id => id == unavailable.Offer.ItemId ? throw new Exception("Unreachable stock must not be queried") : 0,
            _ => ShopOfferAvailability.Available, out var inventoryKnown, out var eligibilityKnown,
            offer => offer.ShopId == reachable.Offer.ShopId);
        Assert.Same(reachable.Offer, selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    [Fact]
    public void NoReachableOfferIsKnownCompletionWithoutInventingTravelOrStock()
    {
        var selected = PoeticsShopping.Select([Offer(100, 10, 100)], Currency, 100,
            _ => throw new Exception("Unreachable stock must not be queried"), _ => ShopOfferAvailability.Available,
            out var inventoryKnown, out var eligibilityKnown, _ => false);
        Assert.Null(selected);
        Assert.True(inventoryKnown);
        Assert.True(eligibilityKnown);
    }

    private static PoeticsShopVendor Vendor(uint npc, uint territory)
        => new(npc, territory, new(1, 2, 3), ["Fixture exchange"]);

    private static PoeticsShopOffer Offer(uint item, uint cost, uint seals, uint shop = 10)
        => new(new(shop, "Fixture exchange", item, 1, Currency, [new(Currency, cost)]), seals);

    private static (MoogleShopOffer? Offer, bool InventoryKnown, bool EligibilityKnown) Select(
        IEnumerable<PoeticsShopOffer> offers, int wallet = 100,
        Func<uint, int?>? owned = null, Func<ShopOfferGate, ShopOfferAvailability>? availability = null,
        uint currency = Currency)
    {
        var selected = PoeticsShopping.Select(offers, currency, wallet, owned ?? (_ => 0),
            offer => availability?.Invoke(offer.Gate) ?? ShopOfferAvailability.Available,
            out var inventoryKnown, out var eligibilityKnown);
        return (selected, inventoryKnown, eligibilityKnown);
    }

    [Fact]
    public void BestSealReturnPerCostWinsOverLargestAbsoluteSealValue()
    {
        var largest = Offer(100, 90, 900);
        var bestRatio = Offer(101, 50, 550);
        var result = Select([largest, bestRatio]);

        Assert.Same(bestRatio.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Fact]
    public void FractionalRatiosAreComparedWithoutIntegerTruncation()
    {
        var bestRatio = Offer(100, 3, 5);
        var lowerCost = Offer(101, 2, 3);

        Assert.Same(bestRatio.Offer, Select([lowerCost, bestRatio]).Offer);
    }

    [Theory]
    [InlineData(100, 101u)]
    [InlineData(99, 102u)]
    [InlineData(24, 0u)]
    public void EverySelectionUsesTheCurrentWalletAndAcceptsAnExactCost(int wallet, uint expected)
    {
        PoeticsShopOffer[] offers = [Offer(100, 101, 10000), Offer(101, 100, 500), Offer(102, 25, 10)];
        var result = Select(offers, wallet);

        Assert.Equal(expected, result.Offer?.ItemId ?? 0);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Theory]
    [InlineData(0, 100u)]
    [InlineData(1, 101u)]
    [InlineData(50, 101u)]
    public void OnlyKnownZeroOwnershipCanBePurchased(int count, uint expected)
    {
        var result = Select([Offer(100, 10, 1000), Offer(101, 10, 100)], owned: id => id == 100 ? count : 0);

        Assert.Equal(expected, result.Offer?.ItemId);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void UnknownOrNegativeOwnershipCannotProduceABestChoiceOrKnownCompletion(int? count)
    {
        var unknown = Offer(100, 10, 1000);
        var known = Offer(101, 10, 100);
        foreach (var offers in new[] { new[] { unknown, known }, new[] { known, unknown } })
        {
            var result = Select(offers, owned: id => id == 100 ? count : 0);
            Assert.Null(result.Offer);
            Assert.False(result.InventoryKnown);
            Assert.True(result.EligibilityKnown);
        }
    }

    [Fact]
    public void UnaffordableUnknownStockDoesNotBlockAnAffordableKnownPurchase()
    {
        var unavailable = Offer(100, 101, 10000);
        var available = Offer(101, 25, 100);
        var result = Select([unavailable, available], owned: id => id == 100 ? null : 0);

        Assert.Same(available.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Fact]
    public void ConfirmedLockedOffersAreSkippedWithoutDowngradingAValidUnlockedChoice()
    {
        var locked = Offer(100, 10, 1000);
        locked = locked with { Offer = locked.Offer with { Gate = new(ShopQuest: 10) } };
        var unlocked = Offer(101, 10, 100);
        var result = Select([locked, unlocked], availability: gate =>
            gate.ShopQuest == 10 ? ShopOfferAvailability.Locked : ShopOfferAvailability.Available);

        Assert.Same(unlocked.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Fact]
    public void ConfirmedLockedOffersDoNotRequireOwnershipData()
    {
        var locked = Offer(100, 10, 1000);
        locked = locked with { Offer = locked.Offer with { Gate = new(ShopQuest: 10) } };
        var unlocked = Offer(101, 10, 100);
        var result = Select([locked, unlocked], owned: id => id == 100 ? null : 0,
            availability: gate => gate.ShopQuest == 10 ? ShopOfferAvailability.Locked : ShopOfferAvailability.Available);

        Assert.Same(unlocked.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownEligibilityNeverAuthorizesAPurchaseOrBlocksKnownAvailableAlternatives(bool includeAvailable)
    {
        var unknown = Offer(100, 10, 1000);
        unknown = unknown with { Offer = unknown.Offer with { Gate = new(ShopQuest: 10) } };
        var available = Offer(101, 10, 100);
        var result = Select(includeAvailable ? [unknown, available] : [unknown],
            availability: gate => gate.ShopQuest == 10 ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available);

        Assert.Same(includeAvailable ? available.Offer : null, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Fact]
    public void AlreadyOwnedItemsDoNotNeedTheirUnlockStateToRankMissingGear()
    {
        var owned = Offer(100, 10, 1000);
        owned = owned with { Offer = owned.Offer with { Gate = new(ShopQuest: 10) } };
        var missing = Offer(101, 10, 100);
        var result = Select([owned, missing], owned: id => id == 100 ? 1 : 0,
            availability: gate => gate.ShopQuest == 10 ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available);

        Assert.Same(missing.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Theory]
    [InlineData("mixed-currency")]
    [InlineData("duplicate-cost")]
    [InlineData("foreign-cost")]
    [InlineData("foreign-offer")]
    [InlineData("no-cost")]
    [InlineData("free")]
    [InlineData("bundle")]
    [InlineData("no-receive")]
    [InlineData("no-seals")]
    public void UnsupportedOfferShapesCannotOutrankAnEligibleSingleCurrencyGearPurchase(string kind)
    {
        var invalid = Offer(100, 10, 1000);
        invalid = kind switch
        {
            "mixed-currency" => invalid with { Offer = invalid.Offer with { Costs = [new(Currency, 1), new(OtherCurrency, 1)] } },
            "duplicate-cost" => invalid with { Offer = invalid.Offer with { Costs = [new(Currency, 1), new(Currency, 1)] } },
            "foreign-cost" => invalid with { Offer = invalid.Offer with { Costs = [new(OtherCurrency, 1)] } },
            "foreign-offer" => invalid with { Offer = invalid.Offer with { TomestoneId = OtherCurrency } },
            "no-cost" => invalid with { Offer = invalid.Offer with { Costs = [] } },
            "free" => invalid with { Offer = invalid.Offer with { Costs = [new(Currency, 0)] } },
            "bundle" => invalid with { Offer = invalid.Offer with { ReceiveCount = 2 } },
            "no-receive" => invalid with { Offer = invalid.Offer with { ReceiveCount = 0 } },
            "no-seals" => invalid with { SealValue = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var valid = Offer(101, 10, 1);
        var result = Select([invalid, valid], owned: id => id == 100 ? null : 0);

        Assert.Same(valid.Offer, result.Offer);
        Assert.True(result.InventoryKnown);
        Assert.True(result.EligibilityKnown);
    }

    [Fact]
    public void UnknownWalletOrCurrencyCannotLookLikeKnownCompletion()
    {
        var wallet = Select([Offer(100, 10, 100)], wallet: -1);
        Assert.Null(wallet.Offer);
        Assert.False(wallet.InventoryKnown);

        var currency = Select([Offer(100, 10, 100)], currency: 0);
        Assert.Null(currency.Offer);
        Assert.True(currency.InventoryKnown);
        Assert.False(currency.EligibilityKnown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualValueRatiosPreferTheLowerCurrencyCost(bool reverse)
    {
        var cheaper = Offer(101, 10, 100);
        var dearer = Offer(100, 20, 200);

        Assert.Same(cheaper.Offer, Select(reverse ? [cheaper, dearer] : [dearer, cheaper]).Offer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualRatioAndCostBreakTiesByItemThenShopIdentity(bool reverse)
    {
        PoeticsShopOffer[] offers = [Offer(101, 10, 100, 1), Offer(100, 10, 100, 3), Offer(100, 10, 100, 2)];

        Assert.Same(offers[2].Offer, Select(reverse ? offers.Reverse() : offers).Offer);
    }

    [Fact]
    public void LargeValuesUseOverflowSafeCrossProducts()
    {
        var lowerRatio = Offer(100, int.MaxValue, uint.MaxValue);
        var higherRatio = Offer(101, int.MaxValue - 1, uint.MaxValue - 1);
        // Both products exceed UInt32; reducing them to UInt32 reverses the comparison.
        foreach (var offers in new[] { new[] { lowerRatio, higherRatio }, new[] { higherRatio, lowerRatio } })
            Assert.Same(higherRatio.Offer, Select(offers, int.MaxValue).Offer);
    }

    [Fact]
    public void VerifiedPurchasesReselectFromCurrentWalletAndOwnershipUntilNoneRemain()
    {
        PoeticsShopOffer[] offers = [Offer(100, 30, 300), Offer(101, 35, 280), Offer(102, 10, 50)];
        var stock = new Dictionary<uint, int>();
        var wallet = 100;
        foreach (var expected in offers)
        {
            var selected = Select(offers, wallet, id => stock.GetValueOrDefault(id));
            Assert.Same(expected.Offer, selected.Offer);
            Assert.True(selected.InventoryKnown);
            Assert.True(selected.EligibilityKnown);
            var beforeItem = stock.GetValueOrDefault(expected.Offer.ItemId);
            var before = new Dictionary<uint, int> { [Currency] = wallet };
            wallet -= checked((int)expected.Offer.Costs.Single().Count);
            stock[expected.Offer.ItemId] = beforeItem + 1;
            Assert.True(MoogleShopMath.ReceiptVerified(expected.Offer, beforeItem, before,
                stock[expected.Offer.ItemId], new Dictionary<uint, int> { [Currency] = wallet }));
        }

        var completed = Select(offers, wallet, id => stock.GetValueOrDefault(id));
        Assert.Null(completed.Offer);
        Assert.True(completed.InventoryKnown);
        Assert.True(completed.EligibilityKnown);
        Assert.Equal(25, wallet);
        Assert.Equal(3, stock.Count);
    }

    [Theory]
    [InlineData(9.99, false)]
    [InlineData(10, true)]
    [InlineData(10.01, true)]
    public void AdmittedDeliveryRequiresTenContinuousIdleSeconds(double seconds, bool expected)
    {
        var start = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        DateTime? idle = null;
        Assert.False(PoeticsShopping.DeliverySettled(true, false, true, false, true, start, ref idle));
        Assert.Equal(expected, PoeticsShopping.DeliverySettled(true, false, true, false, true,
            start.AddSeconds(seconds), ref idle));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void BusyOrUnreadableAutoRetainerRestartsTheEntireIdleAllowance(bool? observedBusy)
    {
        var start = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        DateTime? idle = null;
        Assert.False(PoeticsShopping.DeliverySettled(true, false, true, false, true, start, ref idle));
        Assert.False(PoeticsShopping.DeliverySettled(true, observedBusy, true, false, true, start.AddSeconds(9), ref idle));
        Assert.Null(idle);
        Assert.False(PoeticsShopping.DeliverySettled(true, false, true, false, true, start.AddSeconds(10), ref idle));
        Assert.False(PoeticsShopping.DeliverySettled(true, false, true, false, true, start.AddSeconds(19.99), ref idle));
        Assert.True(PoeticsShopping.DeliverySettled(true, false, true, false, true, start.AddSeconds(20), ref idle));
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    public void RejectedIncompleteCancelledOrChangedSessionCannotAuthorizeReturn(
        bool admitted, bool gearGone, bool cancelled, bool sessionMatches)
    {
        var start = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        DateTime? idle = start;
        Assert.False(PoeticsShopping.DeliverySettled(admitted, false, gearGone, cancelled,
            sessionMatches, start.AddSeconds(100), ref idle));
        Assert.Null(idle);
        Assert.False(PoeticsShopping.DeliverySettled(true, false, true, false, true, start.AddSeconds(101), ref idle));
    }
}
