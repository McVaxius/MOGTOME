using AethertekUI.Dalamud;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class ShopMissingSelectionTests
{
    [Fact]
    public void BlundervilleKitsAndEveryTraderEquipmentSelectMissingWithoutChangingOwnedOrLargerTargets()
    {
        uint[] kits = [41377, 41378]; // Blunderful and Blunderous; AdditionalData kit IDs 119 and 120.
        uint[] garments = [41560, 41561, 41562, 41563]; // Knit cap, parka, bottoms and shoes.
        var targets = new Dictionary<uint, int> { [41561] = 4, [999] = 2 };
        var entries = kits.Select(id => (id, ShopMissingSelection.Classify(29459, false, false, id),
                BlundervilleGameAdapter.ReadRegistration(29459, 0, 2), ShopOfferAvailability.Available))
            .Concat(garments.Select(id => (id, ShopMissingSelection.Classify(0, true, false, id, verifiedShopEquipment: true),
                BlundervilleRegistration.Missing, ShopOfferAvailability.Available)))
            .Append((900000u, ShopMissingSelection.Classify(0, true, false, 900000, verifiedShopEquipment: true),
                BlundervilleRegistration.Owned, ShopOfferAvailability.Available)).ToArray();
        Assert.Equal(new ShopMissingResult(5, 0, 0, 0), ShopMissingSelection.Apply(targets, entries));
        Assert.All(kits, id => Assert.Equal(1, targets[id]));
        Assert.All(garments.Where(id => id != 41561), id => Assert.Equal(1, targets[id]));
        Assert.Equal(4, targets[41561]); Assert.Equal(2, targets[999]);
        Assert.False(targets.ContainsKey(900000));
        Assert.Equal(0, ShopMissingSelection.Apply(targets, entries).Added);
        Assert.Equal(ShopMissingKind.UnknownEquipment, ShopMissingSelection.Classify(0, true, false, 41560));
        Assert.Equal(BlundervilleRegistration.Owned, BlundervilleGameAdapter.ReadRegistration(29459, 0, 1));
        Assert.Equal(BlundervilleRegistration.Owned, BlundervilleGameAdapter.ReadRegistration(29459, 1, 2));
        Assert.Equal(BlundervilleRegistration.Unknown, BlundervilleGameAdapter.ReadRegistration(29459, null, null));
    }

    [Fact]
    public void NativeUnregisteredCollectiblesAreSelectableWithoutStorageAbsenceEvidence()
    {
        foreach (var action in new uint[] { 1322, 853, 20086, 37312, 25183, 2633, 1013, 3357, 29459 })
        {
            var native = BlundervilleGameAdapter.ReadRegistration(action, 0, 2);
            var ownership = BlundervilleGameAdapter.ReadOwnership(true, 0, native, XaItemOwnershipState.Unknown);
            Assert.Equal(BlundervilleRegistration.Unknown, ownership);
            var selection = BlundervilleGameAdapter.ReadSelectionState(ownership, native);
            var targets = new Dictionary<uint, int>();
            var result = ShopMissingSelection.Apply(targets,
                [(100u, ShopMissingKind.Collectible, selection, ShopOfferAvailability.Available)]);
            Assert.Equal(1, result.Added);
            Assert.Equal(1, targets[100]);
            Assert.Equal(0, result.UnknownOwnership);
        }
    }

    [Fact]
    public void InvalidCarriedCountCannotConfirmMissingRegistration()
        => Assert.Equal(BlundervilleRegistration.Unknown, BlundervilleGameAdapter.ReadRegistration(1322, -1, 2));

    [Theory]
    [InlineData((int)BlundervilleRegistration.Owned, (int)BlundervilleRegistration.Missing, (int)BlundervilleRegistration.Owned)]
    [InlineData((int)BlundervilleRegistration.Unknown, (int)BlundervilleRegistration.Owned, (int)BlundervilleRegistration.Owned)]
    [InlineData((int)BlundervilleRegistration.Unknown, (int)BlundervilleRegistration.Unknown, (int)BlundervilleRegistration.Unknown)]
    [InlineData((int)BlundervilleRegistration.Unknown, (int)BlundervilleRegistration.None, (int)BlundervilleRegistration.Unknown)]
    [InlineData((int)BlundervilleRegistration.None, (int)BlundervilleRegistration.None, (int)BlundervilleRegistration.None)]
    public void ManualSelectionHonorsOwnedCopiesAndUnknownNativeRegistration(int ownership, int registration, int expected)
        => Assert.Equal((BlundervilleRegistration)expected, BlundervilleGameAdapter.ReadSelectionState(
            (BlundervilleRegistration)ownership, (BlundervilleRegistration)registration));

    [Fact]
    public void SelectionPreservesLargerAndUnrelatedTargetsAndIsIdempotent()
    {
        var targets = new Dictionary<uint, int> { [100] = 7, [999] = 4 };
        var entries = new[]
        {
            (100u, ShopMissingKind.Collectible, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
            (101u, ShopMissingKind.PvpEquipment, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
            (102u, ShopMissingKind.Collectible, BlundervilleRegistration.Owned, ShopOfferAvailability.Available),
            (103u, ShopMissingKind.UnknownEquipment, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
            (104u, ShopMissingKind.Collectible, BlundervilleRegistration.Unknown, ShopOfferAvailability.Available),
            (105u, ShopMissingKind.Collectible, BlundervilleRegistration.Missing, ShopOfferAvailability.Unknown),
            (106u, ShopMissingKind.Collectible, BlundervilleRegistration.Missing, ShopOfferAvailability.Locked),
            (107u, ShopMissingKind.None, BlundervilleRegistration.Missing, ShopOfferAvailability.Available),
        };
        var result = ShopMissingSelection.Apply(targets, entries.Concat(entries));
        Assert.Equal(new ShopMissingResult(1, 1, 1, 1), result);
        Assert.Equal(7, targets[100]); Assert.Equal(1, targets[101]); Assert.Equal(4, targets[999]);
        Assert.Equal(3, targets.Count);
        Assert.Equal(0, ShopMissingSelection.Apply(targets, entries).Added);
    }

    [Theory]
    [InlineData(1322, false, false, (int)ShopMissingKind.Collectible)]
    [InlineData(3357, false, false, (int)ShopMissingKind.Collectible)]
    [InlineData(0, false, true, (int)ShopMissingKind.None)]
    [InlineData(0, true, true, (int)ShopMissingKind.PvpEquipment)]
    [InlineData(0, true, false, (int)ShopMissingKind.UnknownEquipment)]
    public void UnverifiedGearSourcesAreExcludedAndVerifiedPvpGearIncluded(int action, bool gear, bool pvp, int expected)
        => Assert.Equal((ShopMissingKind)expected, ShopMissingSelection.Classify((uint)action, gear, pvp));

    [Theory]
    [InlineData(25, 4000, 0, true)]
    [InlineData(36656, 1500, 0, true)]
    [InlineData(25, 4000, 1, false)]
    [InlineData(25, 0, 0, false)]
    [InlineData(29, 4000, 0, false)]
    public void OnlyActualLiteralPvpCurrencyCostsProveSource(int currency, int cost, int costType, bool expected)
        => Assert.Equal(expected, ShopMissingSelection.IsPvpCost((uint)currency, (uint)cost, (byte)costType));

    [Fact]
    public void AstronomyIAndIIGearIsSelectableWhileDungeonCoatsAndUnverifiedGearAreExcluded()
    {
        // Astronomy I: SpecialShop 1770710. Astronomy II: SpecialShop 1769929.
        // Both catalogs retain their verified event/crafted/scrip/FATE equipment;
        // all seven Pharos Sirius (Hard)/Arboretum coats stay manual-only.
        uint[] garments = [24615, 32794, 30052, 27936, 27937, 2650, 2993, 12986];
        uint[] dungeonCoats = [13162, 13174, 13186, 13168, 13180, 13192, 13198];
        var entries = garments.Concat(dungeonCoats).Append(900000u).Select(id =>
            (id, ShopMissingSelection.Classify(0, true, false, id),
                BlundervilleRegistration.Missing, ShopOfferAvailability.Available)).ToArray();
        var targets = new Dictionary<uint, int> { [32794] = 4, [999] = 2 };
        Assert.Equal(new ShopMissingResult(7, 0, 1, 0), ShopMissingSelection.Apply(targets, entries));
        Assert.Equal(4, targets[32794]);
        Assert.Equal(2, targets[999]);
        Assert.All(garments.Where(id => id != 32794), id => Assert.Equal(1, targets[id]));
        Assert.All(dungeonCoats.Append(900000u), id => Assert.False(targets.ContainsKey(id)));
        Assert.Equal(0, ShopMissingSelection.Apply(targets, entries).Added);

        // Source proof alone cannot establish absence, even for these verified garments.
        var unknownEntries = garments.Select(id => (id, ShopMissingSelection.Classify(0, true, false, id),
            BlundervilleRegistration.Unknown, ShopOfferAvailability.Available));
        var unknownTargets = new Dictionary<uint, int>();
        Assert.Equal(new ShopMissingResult(0, 8, 0, 0), ShopMissingSelection.Apply(unknownTargets, unknownEntries));
        Assert.Empty(unknownTargets);
    }

    [Theory]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Unknown, (int)BlundervilleRegistration.Unknown)]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Missing)]
    [InlineData(null, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Missing)]
    [InlineData(0, (int)BlundervilleRegistration.Unknown, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Missing)]
    [InlineData(1, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Owned)]
    [InlineData(0, (int)BlundervilleRegistration.Owned, (int)XaItemOwnershipState.Unknown, (int)BlundervilleRegistration.Owned)]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Owned, (int)BlundervilleRegistration.Owned)]
    public void ShopOwnershipUsesRecordedCopiesAndAssumesUnrecordedItemsMissing(int? count, int registration, int storage, int expected)
        => Assert.Equal((BlundervilleRegistration)expected, BlundervilleGameAdapter.ReadOwnership(true, count,
            (BlundervilleRegistration)registration, (XaItemOwnershipState)storage));

    [Fact]
    public void EmptyOrUnavailableStorageSelectsEligibleMissingGearOnceAndPreservesTargets()
    {
        foreach (var query in new Func<string, string>[] { _ => "{}", _ => throw new InvalidOperationException() })
        {
            var ids = new uint[] { 24615, 32794, 30052, 27936, 27937 };
            var storage = XaItemOwnership.QueryStates(query, query, ids, 42, DateTimeOffset.UtcNow);
            var targets = new Dictionary<uint, int> { [24615] = 3, [999] = 4 };
            var entries = ids.Select(id => (id, ShopMissingSelection.Classify(0, true, false, id),
                BlundervilleGameAdapter.ReadOwnership(true, 0, BlundervilleRegistration.None, storage[id]), ShopOfferAvailability.Available));
            Assert.Equal(new ShopMissingResult(4, 0, 0, 0), ShopMissingSelection.Apply(targets, entries));
            Assert.Equal(3, targets[24615]); Assert.Equal(4, targets[999]);
            Assert.Equal(1, targets[27937]);
            Assert.Equal(0, ShopMissingSelection.Apply(targets, entries).Added);
        }
    }

    [Fact]
    public void NoncollectibleRowsHaveNoOwnershipIndicator()
        => Assert.Equal(BlundervilleRegistration.None, BlundervilleGameAdapter.ReadOwnership(false, 2,
            BlundervilleRegistration.None, XaItemOwnershipState.Owned));
}
