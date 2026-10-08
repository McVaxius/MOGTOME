using System.Text.Json;
using MOGTOME.Models;
using MOGTOME.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MOGTOME.Tests;

public sealed class MoogleShopTests
{
    [Fact]
    public void FixedQuantityConfirmationRequiresTheLiveRewardAndExactCurrencyAmounts()
    {
        var offer = new MoogleShopOffer(1770710, "Newest Irregular Tomestone Exchange", 39918, 1, 51750, [new(51750, 15)]);
        var values = new AtkValue[35];
        foreach (var index in new[] { 8, 9, 18, 19, 23, 24, 25, 29 }) values[index].Type = AtkValueType.UInt;
        values[8].UInt = 39918; values[18].UInt = 1; values[23].UInt = 51750; values[29].UInt = 15;
        Assert.True(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[18].UInt = 2;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[18].UInt = 1; values[29].UInt = 14;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[29].UInt = 15; values[24].UInt = 51752; values[30].Type = AtkValueType.UInt; values[30].UInt = 1;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        var both = offer with { Costs = [new(51750, 15), new(51752, 1)] };
        Assert.True(MoogleShopGameAdapter.DialogValuesMatch(both, values));
        values[9].UInt = 123;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(both, values));
        values[9].UInt = 0; values[8].UInt = 123;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(both, values));
        values[8].UInt = 39918;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(both, values.AsSpan(0, 34)));
    }

    [Fact]
    public void ExchangeMenusMatchTheLiveMoogleChoiceAndRejectOtherOrAmbiguousExchanges()
    {
        string[] labels = ["Exchange irregular tomestones."];
        Assert.Equal(0, MoogleShopGameAdapter.FindExchangeRow(
            ["Exchange irregular tomestones.", "Consult the Mogpendium.", "Cancel"], "Newest Irregular Tomestone Exchange", labels));
        Assert.Equal(1, MoogleShopGameAdapter.FindExchangeRow(
            ["Past Irregular Tomestone Exchange", "Newest Irregular Tomestone Exchange", "Cancel"], "Newest Irregular Tomestone Exchange", labels));
        Assert.Equal(-1, MoogleShopGameAdapter.FindExchangeRow(
            ["Exchange previous irregular tomestones.", "Cancel"], "Newest Irregular Tomestone Exchange", labels));
        Assert.Equal(-1, MoogleShopGameAdapter.FindExchangeRow(
            ["Exchange irregular tomestones.", "Newest Irregular Tomestone Exchange"], "Newest Irregular Tomestone Exchange", labels));
        Assert.Equal(-1, MoogleShopGameAdapter.FindExchangeRow(["", "Cancel"], "", [""]));
        Assert.Equal(0, MoogleShopGameAdapter.FindExchangeRow(["Échanger les mémoquartz atypiques.", "Annuler"],
            "Échange actuel", ["Échanger les mémoquartz atypiques."]));
        Assert.Equal(2, MoogleShopGameAdapter.FindExchangeRow(["Exchange irregular tomestones.", "Consult the Mogpendium.", "Cancel"], "", ["Cancel"]));
    }

    [Fact]
    public void FullStopCancelsAQueuedShopBuyBeforeServicesOrEngineExist()
    {
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Plugin).GetField("<IsMoogleShopActionQueued>k__BackingField", flags)!.SetValue(plugin, true);
        typeof(Plugin).GetField("moogleShopActionGeneration", flags)!.SetValue(plugin, 7);
        Assert.True(plugin.StopEngine());
        Assert.False(plugin.IsMoogleShopActionQueued);
        Assert.Equal(8, typeof(Plugin).GetField("moogleShopActionGeneration", flags)!.GetValue(plugin));
        Assert.False(plugin.IsEngineStartQueued);
        Assert.Null(plugin.Engine);
    }

    [Fact]
    public void ShopSettingsAreSeparatePerAccountAndDoNotChangeBlunderville()
    {
        var first = JsonSerializer.Deserialize<Configuration>("{\"DutyCounter\":42,\"Blunderville\":{\"RunLimit\":66}}")!;
        Assert.Equal(MoogleShopCity.Limsa, first.MoogleShop.City);
        Assert.Empty(first.MoogleShop.PurchaseTargets);
        Assert.False(first.MoogleShop.PurchaseReviewRequired);
        first.MoogleShop.City = MoogleShopCity.Gridania;
        first.MoogleShop.PurchaseTargets[123] = 2;
        first.MoogleShop.PurchaseReviewRequired = true;
        var loaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(first))!;
        Assert.Equal(MoogleShopCity.Gridania, loaded.MoogleShop.City);
        Assert.Equal(2, loaded.MoogleShop.PurchaseTargets[123]);
        Assert.True(loaded.MoogleShop.PurchaseReviewRequired);
        Assert.Equal(66, loaded.Blunderville.RunLimit);
        Assert.Empty(loaded.Blunderville.PurchaseTargets);
        Assert.Equal(42, loaded.DutyCounter);
        Assert.Empty(new Configuration().MoogleShop.PurchaseTargets);
    }

    [Fact]
    public void EventEligibilityDoesNotTreatAnUnrelatedFestivalAsCurrent()
    {
        Assert.True(MoogleShopMath.FestivalEligible(0, 0, []));
        Assert.False(MoogleShopMath.FestivalEligible(88, 0, [(119, 1)]));
        Assert.True(MoogleShopMath.FestivalEligible(88, 0, [(88, 2)]));
        Assert.False(MoogleShopMath.FestivalEligible(88, 1, [(88, 2)]));
        Assert.True(MoogleShopMath.FestivalEligible(88, 2, [(88, 2)]));
    }

    [Fact]
    public void CurrentOffersRetainEveryLiteralCurrencyIncludingRewardTokens()
    {
        var offer = MoogleShopMath.ReadOffer(1, "Current", [(100, 1, false), (0, 1, false)],
            [(51750, 100, 0, 0), (51752, 10, 0, 0), (0, 0, 0, 0)], new HashSet<uint> { 51750 });
        Assert.NotNull(offer);
        Assert.Equal(51750u, offer.TomestoneId);
        Assert.Equal(new[] { new MoogleShopCost(51750, 100), new MoogleShopCost(51752, 10) }, offer.Costs);
        Assert.Null(MoogleShopMath.ReadOffer(1, "Current", [(100, 1, true)], [(51750, 1, 0, 0)], new HashSet<uint> { 51750 }));
        Assert.Null(MoogleShopMath.ReadOffer(1, "Current", [(100, 1, false)], [(51750, 1, 0, 3)], new HashSet<uint> { 51750 }));
        Assert.Null(MoogleShopMath.ReadOffer(1, "Current", [(100, 1, false)], [(51750, 1, 0, 0), (51750, 1, 0, 0)], new HashSet<uint> { 51750 }));
    }

    [Theory]
    [InlineData(0, 3, 1, 0)]
    [InlineData(3, 3, 1, 0)]
    [InlineData(2, 3, 1, 0)]
    [InlineData(4, 3, 1, 1)]
    [InlineData(4, 3, 10, 1)]
    [InlineData(24, 3, 10, 3)]
    public void DeficitsRoundUpToExchangeBundlesWithoutBuyingSatisfiedTargets(int desired, int current, uint receive, long exchanges)
        => Assert.Equal(exchanges, MoogleShopMath.Transactions(desired, current, receive));

    [Fact]
    public void MergeUsesSameCurrencyAndReplansOnlyAfterConservedChanges()
    {
        TomestoneStack[] bags = [new(0, 0, 100, 800), new(1, 3, 100, 500), new(2, 2, 100, 999), new(3, 4, 200, 20)];
        var move = MoogleShopMath.PlanMove(bags, 100)!;
        Assert.Equal(199u, move.Transfer);
        Assert.Equal(2299, move.Total);
        Assert.False(MoogleShopMath.MoveVerified(move, bags));
        TomestoneStack[] confirmed = [new(0, 0, 100, 999), new(1, 3, 100, 301), bags[2], bags[3]];
        Assert.True(MoogleShopMath.MoveVerified(move, confirmed));
        Assert.Null(MoogleShopMath.PlanMove(confirmed, 100));
        Assert.False(MoogleShopMath.MoveVerified(move, [confirmed[0], new(1, 3, 100, 300), bags[2], bags[3]]));
        Assert.Null(MoogleShopMath.PlanMove(bags, 200));
        Assert.Null(MoogleShopMath.PlanMove([bags[0], bags[1] with { Mergeable = false }], 100));
    }

    [Fact]
    public void EmptySourceAndAllThreePartialStacksAreConfirmedSequentially()
    {
        TomestoneStack[] bags = [new(0, 0, 100, 200), new(0, 1, 100, 30), new(1, 2, 100, 40)];
        var first = MoogleShopMath.PlanMove(bags, 100)!;
        var after = new[] { new TomestoneStack(0, 0, 100, 230), bags[2] };
        Assert.True(MoogleShopMath.MoveVerified(first, after));
        var second = MoogleShopMath.PlanMove(after, 100)!;
        Assert.True(MoogleShopMath.MoveVerified(second, [new(0, 0, 100, 270)]));
        Assert.False(MoogleShopMath.MoveVerified(second, [new(0, 0, 100, 270), new(1, 2, 999, 1)]));
        Assert.False(MoogleShopMath.MoveVerified(second, [new(0, 0, 100, 270), new(1, 2, 999, 1, false)]));
    }

    [Fact]
    public void ReceiptRequiresExactRewardAndEveryCurrencyChange()
    {
        var offer = new MoogleShopOffer(1, "Current", 100, 1, 10, [new(10, 100), new(11, 10)]);
        var before = new Dictionary<uint, int> { [10] = 200, [11] = 15 };
        Assert.True(MoogleShopMath.ReceiptVerified(offer, 2, before, 3, new Dictionary<uint, int> { [10] = 100, [11] = 5 }));
        Assert.False(MoogleShopMath.ReceiptVerified(offer, 2, before, 3, new Dictionary<uint, int> { [10] = 100, [11] = 15 }));
        Assert.False(MoogleShopMath.ReceiptVerified(offer, 2, before, 2, new Dictionary<uint, int> { [10] = 100, [11] = 5 }));
        Assert.False(MoogleShopMath.ReceiptVerified(offer, 2, before, 3, new Dictionary<uint, int> { [10] = 100 }));
    }
}
