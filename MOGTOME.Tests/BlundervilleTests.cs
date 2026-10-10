using System.Text.Json;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class BlundervilleTests
{
    [Fact]
    public void CollectibleOwnershipUsesInventoryOrRegistrationAndDoesNotGuessMissingTruth()
    {
        foreach (var action in new uint[] { 1322, 853, 20086, 37312, 25183, 2633, 1013, 3357 })
        {
            Assert.Equal(BlundervilleRegistration.Owned, BlundervilleGameAdapter.ReadRegistration(action, 1, 3));
            Assert.Equal(BlundervilleRegistration.Owned, BlundervilleGameAdapter.ReadRegistration(action, null, 1));
            Assert.Equal(BlundervilleRegistration.Missing, BlundervilleGameAdapter.ReadRegistration(action, 0, 2));
            Assert.Equal(BlundervilleRegistration.Unknown, BlundervilleGameAdapter.ReadRegistration(action, null, 2));
            Assert.Equal(BlundervilleRegistration.Unknown, BlundervilleGameAdapter.ReadRegistration(action, 0, 3));
            Assert.Equal(BlundervilleRegistration.Unknown, BlundervilleGameAdapter.ReadRegistration(action, 0, null));
        }
        Assert.Equal(BlundervilleRegistration.None, BlundervilleGameAdapter.ReadRegistration(0, 1, 1));
    }

    [Fact]
    public void CurrencyRowsUseGlobalCallbackIndexesWithinASortedVisibleCategory()
    {
        var offer = new BlundervilleOffer(1770724, 41463, 1, 410, "");
        (uint ItemId, uint Count)[] receives = [(41463, 1), (41564, 1), (41491, 1)];
        Assert.Equal(0, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41491, 750, 2), (41463, 410, 0)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41564, 410, 1)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41564, 410, 0)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41463, 409, 0)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41463, 410, 0), (41463, 410, 0)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow(receives, [(41463, 410, 3)], offer));
        Assert.Equal(-1, BlundervilleGameAdapter.ReadCurrencyRow([(41463, 2)], [(41463, 410, 0)], offer));
    }

    [Fact]
    public void RegistrationDoesNotConsumeCommenceAndEachCycleAcceptsEntryOnce()
    {
        var progress = new BlundervilleProgress();
        progress.SubmitRegistration();
        Assert.True(progress.RegistrationSubmitted);
        Assert.False(progress.CommenceSubmitted);
        Assert.True(progress.TrySubmitCommence());
        Assert.False(progress.TrySubmitCommence());
        progress.Enter();
        progress.ObserveElimination();
        progress.SubmitExit();
        Assert.True(progress.Return());
        progress.SubmitRegistration();
        Assert.True(progress.TrySubmitCommence());
        Assert.False(progress.TrySubmitCommence());
        Assert.Equal(1, progress.Cycles);
    }

    [Fact]
    public void ConfirmedRegistrationRejectionPreservesDeadlineAndDoesNotRearmAcceptedEntry()
    {
        var started = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var progress = new BlundervilleProgress();
        Assert.False(progress.ObserveAreaChangeRejection(false, started));
        progress.SubmitRegistration(started);
        Assert.False(progress.ObserveAreaChangeRejection(true, started.AddSeconds(1)));
        Assert.True(progress.RegistrationSubmitted);
        Assert.True(progress.ObserveAreaChangeRejection(false, started.AddSeconds(1)));
        Assert.False(progress.RegistrationSubmitted);
        Assert.False(progress.ObserveAreaChangeRejection(false, started.AddSeconds(2)));
        progress.SubmitRegistration(started.AddSeconds(3));
        Assert.Equal(started, progress.RegistrationStartedAt);
        Assert.True(progress.ObserveAreaChangeRejection(false, started.AddSeconds(89)));
        Assert.False(progress.RegistrationTimedOut(started.AddSeconds(90)));
        Assert.True(progress.RegistrationTimedOut(started.AddSeconds(90.001)));
        progress.SubmitRegistration(started.AddSeconds(91));
        Assert.False(progress.ObserveAreaChangeRejection(false, started.AddSeconds(91)));
        progress.ResetEntry(); // Stop/next cycle must discard the old registration ownership.
        Assert.Null(progress.RegistrationStartedAt);
        Assert.False(progress.ObserveAreaChangeRejection(false, started.AddSeconds(92)));
        progress.SubmitRegistration(started.AddSeconds(93));
        Assert.False(progress.RegistrationTimedOut(started.AddSeconds(94)));
        Assert.True(progress.TrySubmitCommence());
        Assert.False(progress.ObserveAreaChangeRejection(false, started.AddSeconds(94)));
        progress.Enter();
        Assert.False(progress.ObserveAreaChangeRejection(false, started.AddSeconds(95)));
        Assert.Equal(0, progress.Cycles);
    }

    [Fact]
    public void NpcIdentityCatalogRetainsEveryMatchingSpawnInsteadOfOnlyTheLastResident()
    {
        var identities = BlundervilleGameAdapter.ReadNpcIdentities([
            (1009541, "Blunderville attendant"), (1009542, "Blunderville attendant"),
            (1009543, "BLUNDERVILLE ATTENDANT"), (1009541, "Blunderville attendant"),
            (1046441, "MGF trader"), (1046442, "Blunderville Registrar"), (1, "Unrelated NPC"),
            (0, "Blunderville attendant")]);
        Assert.Equal(3, identities.Count);
        var attendants = identities["blunderville attendant"];
        Assert.Equal(3, attendants.Count);
        Assert.Contains(1009541u, attendants);
        Assert.Contains(1009542u, attendants);
        Assert.Contains(1009543u, attendants);
        Assert.DoesNotContain(1046441u, attendants);
        Assert.Contains(1046441u, identities["MGF trader"]);
    }

    [Fact]
    public void TraderOffersIgnoreEmptyReceiveSlotsWithDefaultQuantityAndRejectAmbiguousRewards()
    {
        (uint ItemId, uint Count, bool Hq)[] receives = [(41463, 1, false), (0, 1, false)];
        (uint ItemId, uint Price, uint Collectability, byte CostType)[] costs = [(41629, 410, 0, 0), (0, 0, 0, 0)];
        var offer = BlundervilleGameAdapter.ReadOffer(1770724, "", receives, costs);
        Assert.NotNull(offer);
        Assert.Equal(41463u, offer.ItemId);
        Assert.Equal(1u, offer.ReceiveCount);
        Assert.Equal(410u, offer.Price);
        Assert.Null(BlundervilleGameAdapter.ReadOffer(1770724, "", [(41463, 1, false), (41560, 1, false)], costs));
        Assert.Null(BlundervilleGameAdapter.ReadOffer(1770724, "", [(41463, 0, false)], costs));
        Assert.Null(BlundervilleGameAdapter.ReadOffer(1770724, "", [(41463, 1, true)], costs));
        Assert.Null(BlundervilleGameAdapter.ReadOffer(1770724, "", receives, [(41629, 410, 0, 0), (29, 1, 0, 0)]));
        Assert.Null(BlundervilleGameAdapter.ReadOffer(1770724, "", receives, [(41629, 410, 0, 3)]));
    }

    [Fact]
    public void DefaultsAndAccountSettingsDoNotAlterOrdinaryDutySettings()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"DutyCounter\":42,\"MaxRuns\":99}")!;
        var settings = config.Blunderville;
        Assert.True(settings.RunLimitEnabled);
        Assert.Equal(1, settings.RunLimit);
        Assert.False(settings.WalletTargetEnabled);
        Assert.Empty(settings.PurchaseTargets);
        Assert.False(settings.ShopWhenFinished);
        Assert.Equal(BlundervilleEndingLocation.Stay, settings.EndingLocation);
        Assert.Equal(BlundervilleReloadScenario.None, settings.ReloadScenario);
        settings.PurchaseTargets[123] = 3;
        settings.WalletTargetEnabled = true;
        settings.PurchaseReviewRequired = true;
        var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
        Assert.Equal(42, restored.DutyCounter);
        Assert.Equal(99, restored.MaxRuns);
        Assert.Equal(3, restored.Blunderville.PurchaseTargets[123]);
        Assert.True(restored.Blunderville.PurchaseReviewRequired);
        Assert.Empty(new Configuration().Blunderville.PurchaseTargets);
    }

    [Fact]
    public void RolesAreRecomputedFromActualSameWorldAndCrossWorldLeaders()
    {
        Assert.Equal(BlundervilleRole.Solo, BlundervilleProgress.Role(false, false, 0, 10, 0));
        Assert.Equal(BlundervilleRole.Leader, BlundervilleProgress.Role(false, false, 4, 10, 10));
        Assert.Equal(BlundervilleRole.Member, BlundervilleProgress.Role(false, false, 4, 10, 20));
        Assert.Equal(BlundervilleRole.Leader, BlundervilleProgress.Role(true, true, 0, 10, 20));
        Assert.Equal(BlundervilleRole.Member, BlundervilleProgress.Role(true, false, 0, 10, 10));
        Assert.Equal(BlundervilleRole.Unknown, BlundervilleProgress.Role(false, false, 4, 10, 0));
        Assert.Equal(BlundervilleRole.Unknown, BlundervilleProgress.Role(true, true, 4, 0, 10));
    }

    [Fact]
    public void OnlyConfirmedEliminationAndReturnCountsOnce()
    {
        var progress = new BlundervilleProgress();
        progress.ObserveElimination();
        progress.SubmitExit();
        Assert.False(progress.Return());
        progress.Enter();
        Assert.False(progress.Return()); // Entering then leaving alone is not elimination evidence.
        progress.Enter();
        progress.ObserveElimination();
        Assert.False(progress.Return()); // Exit still must be confirmed.
        progress.Enter();
        progress.Enter();
        progress.ObserveElimination();
        progress.ObserveElimination();
        progress.SubmitExit();
        progress.SubmitExit();
        Assert.True(progress.Return());
        Assert.False(progress.Return());
        Assert.Equal(1, progress.Cycles);
        progress.Enter();
        progress.SubmitExit(); // Early exit submission is not elimination evidence.
        progress.ObserveElimination();
        Assert.False(progress.Return());
        Assert.Equal(1, progress.Cycles);
    }

    [Fact]
    public void RunAndWalletLimitsAreIndependentAndEitherMayComplete()
    {
        var progress = new BlundervilleProgress();
        var settings = new BlundervilleSettings { RunLimit = 1, WalletTarget = 100 };
        Assert.True(BlundervilleProgress.HasLimit(settings));
        Assert.False(progress.Finished(settings, 100)); // Wallet limit is disabled.
        settings.RunLimitEnabled = false;
        Assert.False(BlundervilleProgress.HasLimit(settings));
        settings.WalletTargetEnabled = true;
        Assert.True(BlundervilleProgress.HasLimit(settings));
        Assert.True(progress.Finished(settings, 100)); // Already met outside arena; zero cycles needed.
        Assert.False(progress.Finished(settings, 99));
        settings.RunLimitEnabled = true;
        Assert.True(progress.Finished(settings, 101));
        progress.Enter(); progress.ObserveElimination(); progress.SubmitExit(); progress.Return();
        Assert.True(progress.Finished(settings, 0));
        settings.WalletTargetEnabled = false;
        Assert.True(progress.Finished(settings, 0));
        settings.RunLimit = -1;
        settings.WalletTargetEnabled = true;
        settings.WalletTarget = -1;
        Assert.False(BlundervilleProgress.HasLimit(settings));
        Assert.False(progress.Finished(settings, 0));
    }

    [Fact]
    public void DeficitsAndPurchaseVerificationRequireBothInventoryAndCurrency()
    {
        Assert.Equal(1, BlundervilleProgress.Deficit(6, 5));
        Assert.Equal(0, BlundervilleProgress.Deficit(6, 6));
        Assert.Equal(0, BlundervilleProgress.Deficit(6, 7));
        Assert.Equal(0, BlundervilleProgress.Deficit(0, 7));
        Assert.True(BlundervilleProgress.PurchaseVerified(5, 100, 1, 50, 6, 50));
        Assert.False(BlundervilleProgress.PurchaseVerified(5, 100, 1, 50, 6, 100));
        Assert.False(BlundervilleProgress.PurchaseVerified(5, 100, 1, 50, 5, 50));
        Assert.False(BlundervilleProgress.PurchaseVerified(5, 100, 1, 50, 7, 50));
        Assert.False(BlundervilleProgress.PurchaseVerified(5, 100, 1, 50, 6, 49));
        Assert.False(BlundervilleProgress.PurchaseVerified(int.MaxValue, uint.MaxValue, 1, uint.MaxValue, int.MinValue, 0));
    }

    [Fact]
    public void ReloadWaitsForReadinessConsumesBeforeCleanupAndNeverRearms()
    {
        var reload = new BlundervilleReloadAttempt();
        reload.Capture(BlundervilleReloadScenario.Start);
        Assert.Equal(BlundervilleReloadScenario.None, reload.Consume(false));
        Assert.Equal(BlundervilleReloadScenario.Start, reload.Consume(true));
        // A reentrant cleanup/update cannot consume the same dispatch again.
        Assert.Equal(BlundervilleReloadScenario.None, reload.Consume(true));
        var invalid = new BlundervilleReloadAttempt();
        invalid.Capture((BlundervilleReloadScenario)999);
        Assert.Equal(BlundervilleReloadScenario.None, invalid.Consume(true));
        reload.Capture(BlundervilleReloadScenario.Buy);
        Assert.Equal(BlundervilleReloadScenario.None, reload.Consume(true));
    }

    [Fact]
    public void StopAndSelectionReplacementCancelPendingDispatchIncludingBeforeReadiness()
    {
        foreach (var cancelBeforeCapture in new[] { true, false })
        {
            var reload = new BlundervilleReloadAttempt();
            if (cancelBeforeCapture) reload.Cancel();
            reload.Capture(BlundervilleReloadScenario.Buy);
            reload.Cancel();
            reload.Capture(BlundervilleReloadScenario.Start);
            Assert.Equal(BlundervilleReloadScenario.None, reload.Consume(true));
        }
        // A distinct plugin load can consume the retained setting once.
        var nextLoad = new BlundervilleReloadAttempt();
        nextLoad.Capture(BlundervilleReloadScenario.Buy);
        Assert.Equal(BlundervilleReloadScenario.Buy, nextLoad.Consume(true));
        Assert.False(nextLoad.IsCancelled);
        nextLoad.Cancel(); // A Stop or selection callback during cleanup cancels the consumed action too.
        Assert.True(nextLoad.IsCancelled);
        Assert.Equal(BlundervilleReloadScenario.None, nextLoad.Consume(true));
    }

    [Fact]
    public void NewControlsAndStatusesExistInAllCatalogs()
    {
        var original = Ui.Language;
        try
        {
            foreach (var language in Enum.GetValues<UiLanguage>())
            {
                Ui.SetLanguage(language);
                foreach (var key in new[] { "BV_AutoShop", "BV_ReviewedPurchase", "BV_MemberWait", "BV_NoInventory", "BV_InnStay", "BV_InnTuliyollal", "BV_ReloadHelp",
                    "BV_ShopTargets", "BV_Item", "BV_NoCatalog", "BV_EntryLocked",
                    "BV_Wanted", "BV_OnHand", "BV_UnitMgf", "BV_NeededMgf", "BV_RegistrationHelp", "BV_Registered", "BV_NotRegistered", "BV_PlannedPurchase", "BV_RegistrationUnknown" })
                {
                    Assert.False(string.IsNullOrWhiteSpace(Ui.T(key)));
                    Assert.NotEqual(key, Ui.T(key));
                }
                Assert.Contains("7", Ui.T("BV_Progress", 7, 100));
                Assert.Contains("3", Ui.T("BV_ItemCost", 50, 1, 3));
                Assert.Contains("123", Ui.T("BV_TotalMgf", 123));
            }
        }
        finally { Ui.SetLanguage(original); }
    }
}
