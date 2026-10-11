using System.Text.Json;
using MOGTOME.Models;
using MOGTOME.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MOGTOME.Tests;

public sealed class MoogleShopTests
{
    [Fact]
    public void StorageTotalsSuppressSatisfiedPurchasesAndLiveArmouryReceiptCompletesTheRemainingDeficit()
    {
        var stored = new AethertekUI.Dalamud.XaItemStock(new Dictionary<string, int>
        {
            ["Inventory 1"] = 1, ["Armoury - Head"] = 0,
            ["Saddlebag 1"] = 1, ["Saddlebag 2"] = 2,
            ["Premium Saddlebag 1"] = 3, ["Premium Saddlebag 2"] = 4,
        }, 3); // Retainer, armoire and dresser copies.
        var live = new Dictionary<string, int>
        {
            [MOGTOME.IPC.XaDatabaseIPC.ContainerName(InventoryType.Inventory1)] = 0,
            [MOGTOME.IPC.XaDatabaseIPC.ContainerName(InventoryType.ArmoryHead)] = 0,
        };
        var count = stored.CountWithLiveInventory(live);
        Assert.Equal(13, count); // The emptied bag replaces its recorded copy; inaccessible bags remain.
        Assert.Equal(0, MoogleShopMath.Transactions(13, count, 1));
        Assert.Equal(0, BlundervilleProgress.Deficit(13, count));
        Assert.Equal(1, MoogleShopMath.Transactions(14, count, 1));
        live["Armoury - Head"] = 1;
        Assert.Equal(14, stored.CountWithLiveInventory(live));
        Assert.Equal(0, MoogleShopMath.Transactions(14, stored.CountWithLiveInventory(live), 1));
        Assert.Equal(0, BlundervilleProgress.Deficit(14, stored.CountWithLiveInventory(live)));
        var offer = new MoogleShopOffer(1770710, "Current", 27936, 1, 51750, [new(51750, 30)]);
        Assert.True(MoogleShopMath.ReceiptVerified(offer, 0, new Dictionary<uint, int> { [51750] = 69 },
            1, new Dictionary<uint, int> { [51750] = 39 }));
    }

    [Theory]
    [InlineData(24615u, InventoryType.ArmoryNeck)]
    [InlineData(27936u, InventoryType.ArmoryHead)]
    [InlineData(27937u, InventoryType.ArmoryBody)]
    public unsafe void ReceiptRecognizesPurchasedGearAutomaticallyPlacedInTheArmoury(uint itemId, InventoryType gearContainer)
    {
        var containers = stackalloc InventoryContainer[BlundervilleGameAdapter.LiveInventoryTypes.Length];
        var items = stackalloc InventoryItem[BlundervilleGameAdapter.LiveInventoryTypes.Length];
        Span<nint> pointers = stackalloc nint[BlundervilleGameAdapter.LiveInventoryTypes.Length];
        for (var index = 0; index < pointers.Length; index++)
        {
            items[index] = default;
            containers[index] = new InventoryContainer { Items = &items[index], Size = 1, IsLoaded = true };
            pointers[index] = (nint)(&containers[index]);
        }
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, itemId, 1, out var before, out var beforeCapacity));
        Assert.Equal(0, before.Values.Sum());
        Assert.Equal(4, beforeCapacity);
        items[Array.IndexOf(BlundervilleGameAdapter.LiveInventoryTypes, gearContainer)] = new InventoryItem { ItemId = itemId, Quantity = 1 };
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, itemId, 1, out var after, out var afterCapacity));
        Assert.Equal(1, after[gearContainer]);
        Assert.Equal(beforeCapacity, afterCapacity);
        var offer = new MoogleShopOffer(1, "Current", itemId, 1, 51750, [new(51750, 30)]);
        Assert.True(MoogleShopMath.ReceiptVerified(offer, before.Values.Sum(), new Dictionary<uint, int> { [51750] = 100 },
            after.Values.Sum(), new Dictionary<uint, int> { [51750] = 70 }));
    }

    [Fact]
    public unsafe void NativeInventoryIncludesAllGearAndBothSaddlebagSidesWithoutStorageCapacity()
    {
        InventoryType[] expected =
        [
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.EquippedItems,
            InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
            InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
            InventoryType.SaddleBag1, InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
        ];
        Assert.Equal(expected, BlundervilleGameAdapter.LiveInventoryTypes);
        var containers = stackalloc InventoryContainer[expected.Length];
        var items = stackalloc InventoryItem[expected.Length * 35];
        new Span<InventoryItem>(items, expected.Length * 35).Clear();
        Span<nint> pointers = stackalloc nint[expected.Length];
        for (var index = 0; index < pointers.Length; index++)
        {
            items[index * 35] = new InventoryItem { ItemId = 100, Quantity = index + 1 };
            var saddlebag = expected[index] is InventoryType.SaddleBag1 or InventoryType.SaddleBag2 or
                InventoryType.PremiumSaddleBag1 or InventoryType.PremiumSaddleBag2;
            containers[index] = new InventoryContainer { Items = &items[index * 35], Size = saddlebag ? 35 : 2, IsLoaded = true };
            pointers[index] = (nint)(&containers[index]);
        }
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 99, out var counts, out var capacity, 1, 1, true, true));
        Assert.Equal(expected.Length, counts.Count);
        for (var index = 0; index < expected.Length; index++) Assert.Equal(index + 1, counts[expected[index]]);
        Assert.Equal(4 * 198 - 10, capacity);
        containers[Array.IndexOf(expected, InventoryType.SaddleBag1)].IsLoaded = false;
        containers[Array.IndexOf(expected, InventoryType.SaddleBag2)].IsLoaded = false;
        containers[Array.IndexOf(expected, InventoryType.PremiumSaddleBag1)].IsLoaded = false;
        containers[Array.IndexOf(expected, InventoryType.PremiumSaddleBag2)].IsLoaded = false;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 99, out counts, out capacity));
        Assert.DoesNotContain(InventoryType.SaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.SaddleBag2, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag2, counts.Keys);
        Assert.Equal(4 * 198 - 10, capacity);
        var head = Array.IndexOf(expected, InventoryType.ArmoryHead);
        containers[head].Items = null;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 99, out counts, out capacity));
        Assert.DoesNotContain(InventoryType.ArmoryHead, counts.Keys);
        Assert.Equal(4 * 198 - 10, capacity);
        containers[head].Items = &items[head * 35];
        containers[head].Size = 0;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 99, out counts, out capacity));
        Assert.DoesNotContain(InventoryType.ArmoryHead, counts.Keys);
        Assert.Equal(4 * 198 - 10, capacity);
        containers[0].IsLoaded = false;
        Assert.False(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 99, out _, out _));
    }

    [Fact]
    public unsafe void UnreadableSaddlebagPairsCannotReplaceRecordedCopiesWithLiveZeroes()
    {
        var containers = stackalloc InventoryContainer[BlundervilleGameAdapter.LiveInventoryTypes.Length];
        var items = stackalloc InventoryItem[BlundervilleGameAdapter.LiveInventoryTypes.Length * 35];
        new Span<InventoryItem>(items, BlundervilleGameAdapter.LiveInventoryTypes.Length * 35).Clear();
        Span<nint> pointers = stackalloc nint[BlundervilleGameAdapter.LiveInventoryTypes.Length];
        for (var index = 0; index < pointers.Length; index++)
        {
            containers[index] = new InventoryContainer { Items = &items[index * 35], Size = 35, IsLoaded = true };
            pointers[index] = (nint)(&containers[index]);
        }
        var left = Array.IndexOf(BlundervilleGameAdapter.LiveInventoryTypes, InventoryType.SaddleBag1);
        var right = Array.IndexOf(BlundervilleGameAdapter.LiveInventoryTypes, InventoryType.SaddleBag2);
        var premiumLeft = Array.IndexOf(BlundervilleGameAdapter.LiveInventoryTypes, InventoryType.PremiumSaddleBag1);
        var premiumRight = Array.IndexOf(BlundervilleGameAdapter.LiveInventoryTypes, InventoryType.PremiumSaddleBag2);
        containers[left].IsLoaded = false;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out var counts, out _, 1, 1));
        Assert.DoesNotContain(InventoryType.SaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.SaddleBag2, counts.Keys);
        containers[left].IsLoaded = true;
        containers[right].IsLoaded = false;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 1));
        Assert.DoesNotContain(InventoryType.SaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.SaddleBag2, counts.Keys);
        containers[right].IsLoaded = true;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 2, true, true));
        Assert.DoesNotContain(InventoryType.SaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.SaddleBag2, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag2, counts.Keys);
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 1, true, false));
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag2, counts.Keys);
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 1, false, true));
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag2, counts.Keys);
        containers[premiumLeft].IsLoaded = containers[premiumRight].IsLoaded = false;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 1, true, true));
        Assert.Equal(0, counts[InventoryType.PremiumSaddleBag1]);
        Assert.Equal(0, counts[InventoryType.PremiumSaddleBag2]);
        containers[premiumRight].Size = 34;
        Assert.True(BlundervilleGameAdapter.ReadLiveInventoryCounts(pointers, 100, 1, out counts, out _, 1, 1, true, true));
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag1, counts.Keys);
        Assert.DoesNotContain(InventoryType.PremiumSaddleBag2, counts.Keys);
    }

    [Fact]
    public void OneItemPromptMayOmitQuantityButStillRequiresItsRewardAndEveryCost()
    {
        var offer = new MoogleShopOffer(1770710, "Newest Irregular Tomestone Exchange", 30107, 1, 51750, [new(51750, 50)]);
        var values = new AtkValue[35];
        foreach (var index in new[] { 8, 9, 18, 19, 23, 24, 25, 29 }) values[index].Type = AtkValueType.UInt;
        values[8].UInt = 30107; values[23].UInt = 51750; values[29].UInt = 50;
        Assert.True(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer with { ReceiveCount = 2 }, values));
        values[18].UInt = 2;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[18].UInt = 0; values[8].UInt = 30108;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[8].UInt = 30107; values[29].UInt = 49;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
        values[29].UInt = 50; values[24].UInt = 51752; values[30].Type = AtkValueType.UInt; values[30].UInt = 1;
        Assert.False(MoogleShopGameAdapter.DialogValuesMatch(offer, values));
    }

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
