using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class KeyboundBrawlerShoppingTests
{
    private static readonly BlundervilleOffer Offer = new(1771021, 52450, 1, 910, "MGC exchange");

    [Fact]
    public void ShoppingSnapshotsTargetsAndEndingAtAdmissionEvenWhenTheEditorChanges()
    {
        var settings = new KeyboundBrawlerSettings
        {
            PurchaseTargets = new() { [52450] = 2 },
            EndingLocation = BlundervilleEndingLocation.Limsa,
        };
        var session = new KeyboundShopSession(settings, "fixture-a", 123);
        settings.PurchaseTargets.Clear();
        settings.PurchaseTargets[52323] = 99;
        settings.EndingLocation = BlundervilleEndingLocation.Uldah;
        Assert.Equal(2, session.Targets[52450]);
        Assert.False(session.Targets.ContainsKey(52323));
        Assert.Equal(BlundervilleEndingLocation.Limsa, session.EndingLocation);
        Assert.True(session.Matches(settings, "fixture-a", 123));
        Assert.False(session.Matches(settings, "fixture-a", 456));
        Assert.False(session.Matches(settings, "fixture-b", 123));
        Assert.False(session.Matches(new KeyboundBrawlerSettings(), "fixture-a", 123));
    }

    [Fact]
    public void ReviewHoldIsPersistedBeforeDispatchAndARejectedCallbackCannotBeSubmittedAgain()
    {
        var settings = new KeyboundBrawlerSettings();
        var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
        var calls = new List<string>();
        Assert.False(transaction.Submit(() =>
        {
            Assert.True(settings.PurchaseReviewRequired);
            calls.Add("save");
        }, () =>
        {
            Assert.Equal(new[] { "save" }, calls);
            calls.Add("dispatch");
            return false;
        }));
        Assert.True(settings.PurchaseReviewRequired);
        Assert.False(transaction.Submit(() => calls.Add("second-save"), () => { calls.Add("second-dispatch"); return true; }));
        Assert.Equal(new[] { "save", "dispatch" }, calls);
    }

    [Fact]
    public void FailedPersistenceDoesNotDispatchAPurchase()
    {
        var settings = new KeyboundBrawlerSettings();
        var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
        var dispatched = false;
        Assert.Throws<IOException>(() =>
        {
            transaction.Submit(() => throw new IOException("Fixture save failure"), () => { dispatched = true; return true; });
        });
        Assert.False(dispatched);
        Assert.True(settings.PurchaseReviewRequired);
        Assert.False(transaction.Submitted);
    }

    [Fact]
    public void ConfirmationIsConsumedBeforeItsCallbackAndNeverReplayedOnFailure()
    {
        var transaction = new KeyboundShopPurchase(new(), Offer, 0, 1000);
        var confirmations = 0;
        Assert.False(transaction.Confirm(() => { confirmations++; return true; }));
        Assert.True(transaction.Submit(() => { }, () => true));
        Assert.False(transaction.Confirm(() => { confirmations++; return false; }));
        Assert.False(transaction.Confirm(() => { confirmations++; return true; }));
        Assert.Equal(1, confirmations);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(0, 90)]
    [InlineData(1, 89)]
    [InlineData(2, 90)]
    public void PartialOrAmbiguousReceiptsRetainThePurchaseHold(int count, uint wallet)
    {
        var settings = new KeyboundBrawlerSettings();
        var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
        transaction.Submit(() => { }, () => true);
        var saves = 0;
        Assert.False(transaction.Verify(count, wallet, () => saves++));
        Assert.True(settings.PurchaseReviewRequired);
        Assert.False(transaction.ReceiptAcknowledged);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void ExactItemAndMgcReceiptClearsAndSavesTheHoldOnce()
    {
        var settings = new KeyboundBrawlerSettings();
        var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
        transaction.Submit(() => { }, () => true);
        var saves = 0;
        Assert.True(transaction.Verify(1, 90, () => { Assert.False(settings.PurchaseReviewRequired); saves++; }));
        Assert.True(transaction.ReceiptAcknowledged);
        Assert.False(transaction.Verify(1, 90, () => saves++));
        Assert.False(transaction.Confirm(() => throw new InvalidOperationException("A verified purchase must not confirm again")));
        Assert.Equal(1, saves);
    }

    [Fact]
    public void UnverifiedMgcPurchasePersistsWithoutChangingOtherShopOrDutySettings()
    {
        var configuration = new Configuration { DutyCounter = 17, MaxRuns = 23 };
        var transaction = new KeyboundShopPurchase(configuration.KeyboundBrawler, Offer, 0, 1000);
        string? saved = null;
        transaction.Submit(() => saved = JsonSerializer.Serialize(configuration), () => true);
        var restored = JsonSerializer.Deserialize<Configuration>(saved!)!;
        Assert.True(restored.KeyboundBrawler.PurchaseReviewRequired);
        Assert.False(restored.Blunderville.PurchaseReviewRequired);
        Assert.False(restored.MoogleShop.PurchaseReviewRequired);
        Assert.Equal(17, restored.DutyCounter);
        Assert.Equal(23, restored.MaxRuns);
        Assert.False(new Configuration().KeyboundBrawler.PurchaseReviewRequired);
    }

    [Fact]
    public void AnotherModuleAndPluginDisposalCannotReleaseAnUnverifiedKeyboundDialogHold()
    {
        var property = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = property.GetValue(null);
        var requests = new HashSet<string>();
        var host = DispatchProxy.Create<IDalamudPluginInterface, ShoppingProxy>();
        ((ShoppingProxy)(object)host).InvokeHandler = (method, _) => method.Name == "GetOrCreateData" ? requests : null;
        property.SetValue(null, host);
        try
        {
            var hold = true;
            var ipc = new YesAlreadyIPC(DispatchProxy.Create<IPluginLog, ShoppingProxy>(), () => hold);
            ipc.Pause();
            Assert.Contains("MOGTOME", requests);
            ipc.Unpause();
            Assert.Contains("MOGTOME", requests);
            ipc.Dispose();
            Assert.Contains("MOGTOME", requests);
            hold = false;
            ipc.Unpause();
            Assert.DoesNotContain("MOGTOME", requests);
        }
        finally { property.SetValue(null, original); }
    }

    public class ShoppingProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? InvokeHandler { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => InvokeHandler?.Invoke(method!, args);
    }
}
