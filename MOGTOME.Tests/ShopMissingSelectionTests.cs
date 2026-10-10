using AethertekUI.Dalamud;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class ShopMissingSelectionTests
{
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

    [Theory]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Unknown, (int)BlundervilleRegistration.Unknown)]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Missing)]
    [InlineData(null, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Unknown)]
    [InlineData(0, (int)BlundervilleRegistration.Unknown, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Unknown)]
    [InlineData(1, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Missing, (int)BlundervilleRegistration.Owned)]
    [InlineData(0, (int)BlundervilleRegistration.Owned, (int)XaItemOwnershipState.Unknown, (int)BlundervilleRegistration.Owned)]
    [InlineData(0, (int)BlundervilleRegistration.Missing, (int)XaItemOwnershipState.Owned, (int)BlundervilleRegistration.Owned)]
    public void ShopOwnershipRequiresNativeAndStorageAbsence(int? count, int registration, int storage, int expected)
        => Assert.Equal((BlundervilleRegistration)expected, BlundervilleGameAdapter.ReadOwnership(true, count,
            (BlundervilleRegistration)registration, (XaItemOwnershipState)storage));

    [Fact]
    public void NoncollectibleRowsHaveNoOwnershipIndicator()
        => Assert.Equal(BlundervilleRegistration.None, BlundervilleGameAdapter.ReadOwnership(false, 2,
            BlundervilleRegistration.None, XaItemOwnershipState.Owned));
}
