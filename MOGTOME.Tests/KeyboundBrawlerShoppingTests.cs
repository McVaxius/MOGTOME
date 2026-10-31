using System.Reflection;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Services.Keybound;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using NativeAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using AtkUnitBasePtr = Dalamud.Game.NativeWrapper.AtkUnitBasePtr;

namespace MOGTOME.Tests;

public sealed class KeyboundBrawlerShoppingTests
{
    private static readonly BlundervilleOffer Offer = new(1771021, 52450, 1, 910, "MGC exchange");

    [Fact]
    public void KeyboundStartupEnablesXaCutsceneSkippingBeforeRegistrationAndContinuesThroughTwoClears()
    {
        using var f = new ShopFixture();
        f.Settings.RunLimitEnabled = true; f.Settings.RunLimit = 2;
        f.Settings.WalletTargetEnabled = false; f.Settings.ShopWhenFinished = false;
        f.Settings.PurchaseReviewRequired = true;
        f.Plugin.StartKeyboundBrawler();
        Assert.False(f.Typer.IsRunning);
        Assert.Empty(f.Commands); // Rejected admission must not change XA settings.

        f.Settings.PurchaseReviewRequired = false;
        f.Plugin.StartKeyboundBrawler();
        Assert.True(f.Typer.IsRunning);
        Assert.True(f.Loop.IsActive);
        Assert.Equal(new[] { "/xa skipcutscenes on" }, f.Commands);
        Assert.Equal(0, f.Game.Registrations);
        f.Plugin.StartKeyboundBrawler();
        Assert.Single(f.Commands); // A duplicate Start does not repeat the startup action.

        void Tick(long now)
        {
            var frame = f.Game.Frame;
            f.Typer.Observe(true, false, frame.Territory == KeyboundEventLoop.Arena ? 65u : 23u,
                frame.Territory, frame.Transitioning);
            f.Typer.FinishIfLimitMet(0);
            f.Loop.Update(now);
            f.Typer.Update(0, now);
        }

        var started = Environment.TickCount64;
        for (var round = 0; round < 2; round++)
        {
            var now = started + round * 3000;
            f.Game.Frame = f.Game.Frame with { Entry = true };
            Tick(now);
            Assert.Equal(round + 1, f.Game.Registrations);
            f.Game.Frame = f.Game.Frame with { Entry = false, Queued = true, QueueMatches = true };
            Tick(now + 100);
            f.Game.Frame = f.Game.Frame with { Queued = false, Pop = true, PopMatches = true };
            Tick(now + 200);
            f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Arena, Pop = false };
            Tick(now + 300);
            f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true };
            Tick(now + 400); Tick(now + 2400);
            Assert.Equal(round + 1, f.Typer.CompletedRuns);
            Assert.Equal(round + 1, f.Game.Leaves);
            f.Game.Frame = f.Game.Frame with
            {
                Territory = KeyboundEventLoop.Lobby, Results = false, ResultsReady = false,
            };
            Tick(now + 2500);
            Assert.Equal(round == 0, f.Loop.IsActive);
        }
        Assert.Equal(2, f.Game.Commences);
        Assert.True(f.Typer.Finished);
        Assert.Single(f.Commands);

        f.Plugin.StartKeyboundBrawler();
        Assert.True(f.Typer.IsRunning);
        Assert.Equal(0, f.Typer.CompletedRuns);
        Assert.Equal(new[] { "/xa skipcutscenes on", "/xa skipcutscenes on" }, f.Commands);
    }

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
    public void InterimShoppingStaysWithoutChangingTheConfiguredFinalEnding()
    {
        var settings = new KeyboundBrawlerSettings { EndingLocation = BlundervilleEndingLocation.Limsa };
        var interim = new KeyboundShopSession(settings, "fixture", 123, BlundervilleEndingLocation.Stay);
        Assert.Equal(BlundervilleEndingLocation.Stay, interim.EndingLocation);
        Assert.Equal(BlundervilleEndingLocation.Limsa, settings.EndingLocation);
        Assert.Equal(BlundervilleEndingLocation.Limsa, new KeyboundShopSession(settings, "fixture", 123).EndingLocation);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void CapturedWalletTargetTakesPriorityOverRunCountShopping(bool runs, bool wallet, bool expected)
    {
        var settings = new KeyboundBrawlerSettings
        {
            RunLimitEnabled = runs, RunLimit = 3, WalletTargetEnabled = wallet, WalletTarget = 9999,
        };
        using var service = new KeyboundBrawlerService(new KeyboundTypingTests.FakeInput("a"));
        service.Observe(true, false, 23, KeyboundEventLoop.Lobby);
        Assert.True(service.Start(settings));
        settings.RunLimitEnabled = !runs;
        settings.WalletTargetEnabled = !wallet;
        Assert.Equal(expected, service.UsesRunCountMode);
        service.Stop();
        Assert.Equal(expected, service.ResumeAfterShopping(9500));
    }

    [Fact]
    public void ResumeKeepsRunsPhrasesStageSpeedsAndLimitsCapturedBeforeTheTrip()
    {
        var delays = new List<int>();
        var input = new KeyboundTypingTests.FakeInput("abc") { AutoEcho = true };
        using var service = new KeyboundBrawlerService(input, (minimum, _) => { delays.Add(minimum); return 0; });
        var settings = new KeyboundBrawlerSettings
        {
            RunLimitEnabled = true, RunLimit = 3, WalletTargetEnabled = false,
            Stage12TypingWpm = 80, Stage3TypingWpm = 40,
        };
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        Assert.True(service.Start(settings));
        service.Update(9500, 0); service.Update(9500, 1); service.Update(9500, 2); service.Update(9500, 3); service.Update(9500, 38);
        Assert.Equal(1, service.SubmittedPhrases);
        Assert.False(service.CanPauseForShopping);
        service.ObserveCompletion(KeyboundEventLoop.Arena);
        Assert.Equal(1, service.CompletedRuns);
        Assert.True(service.CanPauseForShopping);
        service.Observe(true, false, 23, KeyboundEventLoop.Lobby);
        // The shopping handoff can precede the first controller Update outside the old arena.
        service.Stop();
        settings.RunLimit = 1;
        settings.WalletTargetEnabled = true; settings.WalletTarget = 0;
        settings.Stage12TypingWpm = settings.Stage3TypingWpm = 300;
        Assert.True(service.ResumeAfterShopping(9500));
        Assert.Equal(1, service.CompletedRuns);
        Assert.Equal(1, service.SubmittedPhrases);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        service.ObserveArenaEntry(KeyboundEventLoop.Arena);
        delays.Clear();
        service.Update(0, 1000);
        Assert.Contains(60, delays); // Captured 80 WPM: 75 ms average, 60 ms minimum.
        delays.Clear();
        input.Configure = snapshot => snapshot.Stage = 3;
        service.Update(0, 1001);
        Assert.Contains(120, delays); // Captured 40 WPM remains the stage-three cadence.
        service.ObserveCompletion(KeyboundEventLoop.Arena);
        service.FinishIfLimitMet(0);
        Assert.Equal(2, service.CompletedRuns);
        Assert.True(service.IsRunning);
        service.Observe(true, false, 23, KeyboundEventLoop.Lobby);
        service.Update(0, 2000);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        service.ObserveArenaEntry(KeyboundEventLoop.Arena);
        service.ObserveCompletion(KeyboundEventLoop.Arena);
        service.FinishIfLimitMet(0);
        Assert.Equal(3, service.CompletedRuns);
        Assert.True(service.Finished);
        Assert.False(service.ResumeAfterShopping(9500));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StopOrUnavailableAdmissionCannotResumeAfterShopping(bool ready, bool busy)
    {
        using var service = new KeyboundBrawlerService(new KeyboundTypingTests.FakeInput("a"));
        service.Observe(true, false, 23, KeyboundEventLoop.Lobby);
        Assert.True(service.Start(new() { RunLimitEnabled = true, RunLimit = 3, WalletTargetEnabled = false }));
        service.Stop();
        service.Observe(ready, busy, 23, KeyboundEventLoop.Lobby);
        if (ready && !busy) service.RequestStop();
        Assert.False(service.ResumeAfterShopping(9500));
        Assert.False(service.IsRunning);
    }

    [Theory]
    [InlineData(9499u, true, false, false, false)]
    [InlineData(9500u, true, false, false, true)]
    [InlineData(9999u, true, false, false, true)]
    [InlineData(9500u, false, false, false, false)]
    [InlineData(9500u, true, true, false, false)]
    [InlineData(9500u, true, false, true, false)]
    public void AutomaticTripUsesTheCapturedModeAndThresholdBeforeAnotherRegistration(
        uint wallet, bool runs, bool walletTarget, bool finalRun, bool shops)
    {
        using var f = new ShopFixture();
        f.Settings.RunLimitEnabled = runs; f.Settings.RunLimit = finalRun ? 1 : 3;
        f.Settings.WalletTargetEnabled = walletTarget; f.Settings.WalletTarget = 9999;
        f.Settings.ShopWhenFinished = false;
        f.Settings.PurchaseTargets = new() { [Offer.ItemId] = 2 };
        f.ArmFarming();
        // Edits after Start cannot change which captured limit owns the session.
        f.Settings.RunLimitEnabled = !runs; f.Settings.WalletTargetEnabled = !walletTarget;
        if (finalRun)
        {
            f.Typer.Observe(true, false, 65, KeyboundEventLoop.Arena);
            f.Typer.ObserveArenaEntry(KeyboundEventLoop.Arena);
            f.Typer.ObserveCompletion(KeyboundEventLoop.Arena);
        }
        ShopFixture.Wallet = wallet;
        f.UpdateFarming();
        Assert.Equal(shops, f.Shop.Status.Key == "Plugin_RequiredXaPlugins"); // Buy was reached; this offline host has no loaded plugins.
        Assert.Equal(!shops && !finalRun, f.Typer.IsRunning);
        Assert.Equal(!shops && !finalRun, f.Loop.IsActive);
        Assert.Equal(0, f.Game.Registrations);
    }

    [Fact]
    public void SelectorSkipsExpensiveTargetsAndBuysRepeatedAffordableUnits()
    {
        using var f = new ShopFixture();
        var expensive = Offer with { ItemId = 52323, Price = 5000 };
        f.Offers(expensive, Offer);
        f.Settings.PurchaseTargets = new() { [expensive.ItemId] = 1, [Offer.ItemId] = 3 };
        var session = f.Session();
        uint wallet = 2000;
        Assert.True(f.Shop.HasAffordablePurchase(wallet));
        for (var owned = 0; owned < 2; owned++)
        {
            Assert.Same(Offer, f.Next(session, wallet));
            f.Counts[Offer.ItemId] = owned + 1;
            wallet -= Offer.Price;
        }
        Assert.Equal(180u, wallet);
        Assert.Null(f.Next(session, wallet));
        Assert.False(f.Shop.HasAffordablePurchase(wallet));
        f.Counts[Offer.ItemId] = 3;
        Assert.Null(f.Next(session, 4999));
        Assert.Same(expensive, f.Next(session, 5000));
    }

    [Fact]
    public void EmptyFulfilledAndUnaffordableTargetsNeverRequestAnotherTrip()
    {
        using var f = new ShopFixture();
        foreach (var targets in new Dictionary<uint, int>[]
        {
            new(), new() { [Offer.ItemId] = 0 }, new() { [Offer.ItemId] = 1 },
        })
        {
            f.Settings.PurchaseTargets = targets;
            Assert.False(f.Shop.HasAffordablePurchase(Offer.Price - 1));
            Assert.Null(f.Next(f.Session(), Offer.Price - 1));
        }
        f.Counts[Offer.ItemId] = 1;
        Assert.False(f.Shop.HasAffordablePurchase(9999));
        Assert.Null(f.Next(f.Session(), 9999));
    }

    [ThreadStatic] private static bool achievementComplete;
    [UnmanagedCallersOnly]
    private static unsafe byte BarterAchievement(NativeAchievement* _, uint id) => 0;
    [UnmanagedCallersOnly]
    private static unsafe byte CompletedAchievement(NativeAchievement* _, int id) => achievementComplete ? (byte)1 : (byte)0;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void UnknownEligibilityKeepsTheTripPendingUntilNativeAchievementsResolve(bool unlocked)
    {
        using var f = new ShopFixture();
        var native = new NativeAchievement();
        var originalInstance = NativeAchievement.Addresses.Instance.Value;
        var originalBarter = NativeAchievement.Addresses.IsItemBarterWarningAchievementComplete.Value;
        var originalComplete = NativeAchievement.Addresses.IsComplete.Value;
        NativeAchievement.Addresses.Instance.Value = (nint)(&native);
        NativeAchievement.Addresses.IsItemBarterWarningAchievementComplete.Value = (nint)(delegate* unmanaged<NativeAchievement*, uint, byte>)&BarterAchievement;
        NativeAchievement.Addresses.IsComplete.Value = (nint)(delegate* unmanaged<NativeAchievement*, int, byte>)&CompletedAchievement;
        try
        {
            var gated = Offer with { Gate = new(Achievement: 1) };
            f.BeginShop();
            Assert.False(f.CheckEligibility(gated));
            Assert.True(f.Shop.IsRunning);
            Assert.Equal("Shop_EligibilityUnknown", f.Shop.Status.Key);
            Assert.False(f.Shop.CompletedNormally);
            native.State = NativeAchievement.AchievementState.Loaded;
            achievementComplete = unlocked;
            Assert.Equal(unlocked, f.CheckEligibility(gated));
            Assert.Equal(unlocked, f.Shop.IsRunning);
            Assert.False(f.Shop.CompletedNormally);
            if (!unlocked) Assert.Equal("Shop_Locked", f.Shop.Status.Key);
        }
        finally
        {
            NativeAchievement.Addresses.Instance.Value = originalInstance;
            NativeAchievement.Addresses.IsItemBarterWarningAchievementComplete.Value = originalBarter;
            NativeAchievement.Addresses.IsComplete.Value = originalComplete;
        }
    }

    [Fact]
    public unsafe void SuccessfulShoppingRequiresClosedDialogsAndNoPendingStopBeforeResume()
    {
        using var f = new ShopFixture();
        f.BeginShop();
        f.Complete();
        Assert.True(f.Shop.CompletedNormally);
        Assert.True(f.Shop.CanResumeFarming);
        foreach (var addon in new[] { "SelectYesno", "ShopExchangeCurrencyDialog", "ShopExchangeItemDialog", "ShopExchangeCurrency", "ShopExchangeItem", "Talk", "SelectString", "SelectIconString" })
        {
            f.VisibleAddon = addon;
            Assert.False(f.Shop.CanResumeFarming);
        }
        f.VisibleAddon = null;
        Assert.True(f.Shop.CanResumeFarming);
        f.Shop.RequestStop();
        Assert.False(f.Shop.CanResumeFarming);
        f.Shop.Update();
        Assert.False(f.Shop.CompletedNormally);
        Assert.False(f.Shop.CanResumeFarming);
    }

    [Fact]
    public void FailedOwnedCleanupCannotMarkShoppingComplete()
    {
        using var f = new ShopFixture();
        f.BeginShop();
        f.SetShopField("npc", 99ul);
        f.SetShopField("ownedAddon", "ShopExchangeCurrency");
        f.VisibleAddon = "ShopExchangeCurrency";
        f.Complete();
        Assert.False(f.Shop.IsRunning);
        Assert.False(f.Shop.CompletedNormally);
        Assert.False(f.Shop.CanResumeFarming);
        Assert.Equal("BV_ActionFailed", f.Shop.Status.Key);
    }

    [Fact]
    public void SessionChangesAndUncertainReceiptsCannotCompleteOrResumeShopping()
    {
        using var f = new ShopFixture();
        f.BeginShop();
        f.Character = 456;
        f.Shop.Update();
        Assert.Equal("BV_SessionChanged", f.Shop.Status.Key);
        Assert.False(f.Shop.CompletedNormally);
        Assert.False(f.Shop.CanResumeFarming);
        f.Character = 123;
        f.BeginShop();
        var transaction = new KeyboundShopPurchase(f.Settings, Offer, 0, 1000);
        transaction.Submit(() => { }, () => true);
        f.SetShopField("pending", transaction);
        f.Character = 456; // Cleanup must retain the captured hold without saving into the new session.
        f.Shop.Stop();
        Assert.True(f.Settings.PurchaseReviewRequired);
        Assert.True(f.Shop.HoldAutomaticDialogs);
        Assert.False(f.Shop.CompletedNormally);
        Assert.False(f.Shop.CanResumeFarming);
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

    [Fact]
    public void ProceedAcknowledgementIsOneOwnedStepBeforeFinalConfirmationAndRetainsTheReceiptHold()
    {
        var settings = new KeyboundBrawlerSettings();
        var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
        var calls = new List<string>();
        Assert.False(transaction.AcknowledgeProceed(() => { calls.Add("early"); return true; }));
        Assert.True(transaction.Submit(() => { }, () => true));
        Assert.True(transaction.AcknowledgeProceed(() => { calls.Add("proceed"); return true; }));
        Assert.True(settings.PurchaseReviewRequired);
        Assert.False(transaction.ConfirmationSubmitted);
        Assert.False(transaction.AcknowledgeProceed(() => { calls.Add("duplicate"); return true; }));
        Assert.True(transaction.Confirm(() => { calls.Add("confirm"); return true; }));
        Assert.Equal(new[] { "proceed", "confirm" }, calls);
        Assert.True(settings.PurchaseReviewRequired);
        Assert.True(transaction.Verify(1, 90, () => { }));
        Assert.False(settings.PurchaseReviewRequired);
    }

    [ThreadStatic] private static nint warningCheckbox, warningOk;
    [ThreadStatic] private static int warningNotifications, warningAccepts;
    [ThreadStatic] private static bool checkedBeforeNotification;

    [UnmanagedCallersOnly]
    private static unsafe void SetWarningChecked(AtkComponentButton* button, byte value)
    {
        if (value != 0) button->Flags |= 1u << 18;
        else button->Flags &= ~(1u << 18);
    }

    [UnmanagedCallersOnly]
    private static unsafe void ReceiveWarning(AtkEventListener* listener, AtkEventType type, int param, AtkEvent* evt, AtkEventData* data)
    {
        if (param == 7)
        {
            warningNotifications++;
            checkedBeforeNotification = ((AtkComponentCheckBox*)warningCheckbox)->IsChecked;
            if (checkedBeforeNotification) ((AtkComponentButton*)warningOk)->OwnerNode->AtkResNode.NodeFlags |= NodeFlags.Enabled;
        }
        if (param == 8) warningAccepts++;
        evt->Param = 999; // The native callback may alter its copy, never the registration.
    }

    [Theory]
    [InlineData(AtkValueType.Int)]
    [InlineData(AtkValueType.UInt)]
    public unsafe void StandardEquipmentWarningChecksOwnershipThenProceedAndOkWithoutConsumingTheFollowingConfirmation(AtkValueType zeroType)
    {
        const string prompt = "You will never be able to equip this item.Proceed with the transaction?";
        var values = stackalloc AtkValue[13];
        new Span<AtkValue>(values, 13).Clear();
        values[0].Type = AtkValueType.String;
        values[8].Type = AtkValueType.String;
        values[12] = new AtkValue { Type = zeroType, UInt = 0 };
        var shop = new AtkUnitBase { Id = 42 };
        var receive = new AgentShop.ShopItem { ItemId = Offer.ItemId, ItemCount = Offer.ReceiveCount };
        var agent = new AgentShop { ItemReceive = &receive, ItemReceiveCount = 1, SelectedItemIndex = 0, SelectedItemStackSize = 1 };
        var checkboxOwner = new AtkComponentNode();
        checkboxOwner.AtkResNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        var checkbox = new AtkComponentCheckBox { OwnerNode = &checkboxOwner };
        var okOwner = new AtkComponentNode();
        okOwner.AtkResNode.NodeFlags = NodeFlags.Visible; // OK is gated until Proceed is checked.
        var ok = new AtkComponentButton { OwnerNode = &okOwner };
        var table = new AtkEventListener.AtkEventListenerVirtualTable { ReceiveEvent = &ReceiveWarning };
        var warning = new AddonSelectYesno
        {
            AtkUnitBase = new AtkUnitBase { AtkValues = values, AtkValuesCount = 13, BlockedParentId = 42,
                AtkEventListener = new AtkEventListener { VirtualTable = &table } },
            ConfirmCheckBox = &checkbox, YesButton = &ok,
        };
        var proceedEvent = new AtkEvent { Listener = &warning.AtkUnitBase.AtkEventListener, Param = 7,
            State = new AtkEventState { EventType = AtkEventType.ButtonClick } };
        var hoverEvent = new AtkEvent { NextEvent = &proceedEvent, State = new AtkEventState { EventType = AtkEventType.MouseOver } };
        checkboxOwner.AtkResNode.AtkEventManager.Event = &hoverEvent;
        var okEvent = new AtkEvent { Listener = &warning.AtkUnitBase.AtkEventListener, Param = 8,
            State = new AtkEventState { EventType = AtkEventType.ButtonClick } };
        okOwner.AtkResNode.AtkEventManager.Event = &okEvent;

        foreach (var invalidValue in new AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = -1 },
            new() { Type = AtkValueType.Int, Int = 1 },
            new() { Type = AtkValueType.UInt, UInt = 1 },
            new() { Type = AtkValueType.UInt, UInt = uint.MaxValue },
        })
        {
            values[12] = invalidValue;
            Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        }
        foreach (var invalidType in Enum.GetValues<AtkValueType>())
        {
            if (invalidType is AtkValueType.Int or AtkValueType.UInt) continue;
            values[12] = new AtkValue { Type = invalidType, UInt = 0 };
            Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        }
        values[12] = new AtkValue { Type = zeroType, UInt = 0 };

        Assert.True(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, "An unrelated question?", prompt));
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, null));
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer with { ItemId = 1 }, prompt, prompt));
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer with { ReceiveCount = 2 }, prompt, prompt));
        warning.BlockedParentId = 43;
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        warning.BlockedParentId = 42;
        agent.SelectedItemIndex = 1;
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        agent.SelectedItemIndex = 0;
        agent.SelectedItemStackSize = 2;
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        agent.SelectedItemStackSize = 1;
        warning.AtkValuesCount = 12;
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        warning.AtkValuesCount = 13;
        checkboxOwner.AtkResNode.NodeFlags = 0;
        Assert.False(BlundervilleGameAdapter.EquipmentWarningMatches(&warning, &shop, &agent, 0, Offer, prompt, prompt));
        checkboxOwner.AtkResNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;

        var setChecked = AtkComponentButton.Addresses.SetChecked.Value;
        AtkComponentButton.Addresses.SetChecked.Value = (nint)(delegate* unmanaged<AtkComponentButton*, byte, void>)&SetWarningChecked;
        warningCheckbox = (nint)(&checkbox); warningOk = (nint)(&ok);
        warningNotifications = warningAccepts = 0; checkedBeforeNotification = false;
        try
        {
            var settings = new KeyboundBrawlerSettings();
            var transaction = new KeyboundShopPurchase(settings, Offer, 0, 1000);
            var dialog = &warning.AtkUnitBase;
            var proceed = &checkbox;
            Assert.False(transaction.AcknowledgeEquipmentWarning(() => true));
            Assert.True(transaction.Submit(() => { }, () => true));
            Assert.False(BlundervilleGameAdapter.ClickAddonButton(dialog, warning.YesButton));
            Assert.True(transaction.AcknowledgeProceed(() => BlundervilleGameAdapter.CheckPurchaseProceed(dialog, proceed)));
            Assert.True(checkedBeforeNotification);
            Assert.True(checkbox.IsChecked);
            Assert.True(ok.IsEnabled);
            Assert.True(transaction.AcknowledgeEquipmentWarning(() => BlundervilleGameAdapter.ClickAddonButton(dialog, ((AddonSelectYesno*)dialog)->YesButton)));
            Assert.False(transaction.AcknowledgeProceed(() => throw new InvalidOperationException("Duplicate Proceed")));
            Assert.False(transaction.AcknowledgeEquipmentWarning(() => throw new InvalidOperationException("Duplicate warning OK")));
            Assert.False(transaction.ConfirmationSubmitted);
            Assert.True(settings.PurchaseReviewRequired);
            Assert.Equal(1, warningNotifications);
            Assert.Equal(1, warningAccepts);
            Assert.Equal(7u, proceedEvent.Param);
            Assert.Equal(8u, okEvent.Param);
            Assert.True(transaction.Confirm(() => true)); // A following native purchase confirmation is still admitted.
            Assert.False(transaction.Verify(0, 1000, () => { }));
            Assert.True(transaction.Verify(1, 90, () => { }));
            Assert.False(settings.PurchaseReviewRequired);

            var directSettings = new KeyboundBrawlerSettings();
            var direct = new KeyboundShopPurchase(directSettings, Offer, 0, 1000);
            direct.Submit(() => { }, () => true);
            direct.AcknowledgeEquipmentWarning(() => true);
            Assert.True(direct.Verify(1, 90, () => { })); // Warning OK can also complete the purchase directly.
            Assert.False(directSettings.PurchaseReviewRequired);
            Assert.False(direct.Confirm(() => throw new InvalidOperationException("Purchase already verified")));
        }
        finally { AtkComponentButton.Addresses.SetChecked.Value = setChecked; warningCheckbox = warningOk = 0; }
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

    [ThreadStatic] private static uint nativeWallet;
    [UnmanagedCallersOnly]
    private static unsafe uint ReadWallet(CurrencyManager* _, uint itemId) => nativeWallet;

    private sealed class ShopGame : IKeyboundLoopGame
    {
        internal KeyboundLoopFrame Frame = new(KeyboundEventLoop.Lobby, false, false, false, false, false, false, false, false, false);
        internal int Registrations, Commences, Leaves;
        public KeyboundLoopFrame Read() => Frame;
        public bool EnsureLobby() => true;
        public bool ApproachRegistrar() => true;
        public bool Register() { Registrations++; return true; }
        public bool Commence() { Commences++; return true; }
        public bool LeaveResults() { Leaves++; return true; }
        public void KeepActive() { }
        public void CancelQueue() { }
        public void StopMovement() { }
    }

    // DispatchProxy cannot preserve IDataManager's self-referential Excel row constraints.
    private sealed class EmptyDataManager : IDataManager
    {
        private readonly RawExcelSheet sheet = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        internal EmptyDataManager()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(RawExcelSheet).GetField("_rowIndexLookupArray", flags)!.SetValue(sheet, Array.Empty<int>());
            typeof(RawExcelSheet).GetField("_rowIndexLookupDict", flags)!.SetValue(sheet, FrozenDictionary<int, int>.Empty);
        }
        public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.English;
        public Lumina.GameData GameData => throw new NotSupportedException();
        public ExcelModule Excel => throw new NotSupportedException();
        public bool HasModifiedGameDataFiles => false;
        public ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelRow<T> => new(sheet);
        public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelSubrow<T> => throw new NotSupportedException();
        public Lumina.Data.FileResource? GetFile(string path) => null;
        public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => null;
        public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken) where T : Lumina.Data.FileResource
            => throw new NotSupportedException();
        public bool FileExists(string path) => false;
    }

    private sealed unsafe class ShopFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Dictionary<PropertyInfo, object?> originals = new();
        private readonly object? originalCatalog;
        private readonly nint originalCurrency, originalWallet;
        private readonly nint currencySlot, currency, addon;
        internal readonly Plugin Plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        internal readonly Configuration Configuration = new();
        internal readonly Dictionary<uint, int> Counts = new();
        internal readonly List<string> Commands = new();
        internal readonly ShopGame Game = new();
        internal readonly KeyboundBrawlerService Typer = new(new KeyboundTypingTests.FakeInput("a"), (_, _) => 0);
        internal readonly KeyboundEventLoop Loop;
        internal readonly KeyboundBrawlerShopService Shop;
        internal KeyboundBrawlerSettings Settings => Configuration.KeyboundBrawler;
        internal ulong Character = 123;
        internal string? VisibleAddon;
        internal static uint Wallet { set => nativeWallet = value; }

        internal ShopFixture()
        {
            var log = Fake<IPluginLog>();
            Service("Log", log);
            Service("ClientState", Fake<IClientState>((method, _) => method.Name switch
            {
                "get_IsLoggedIn" => true, "get_TerritoryType" => KeyboundEventLoop.Lobby,
                _ => Default(method.ReturnType),
            }));
            Service("PlayerState", Fake<IPlayerState>((method, _) => method.Name switch
            {
                "get_IsLoaded" => true, "get_ContentId" => Character, _ => Default(method.ReturnType),
            }));
            Service("ObjectTable", Fake<IObjectTable>((method, _) => method.Name == "get_LocalPlayer"
                ? Fake<IPlayerCharacter>() : Default(method.ReturnType)));
            Service("Condition", Fake<ICondition>());
            Service("Framework", Fake<IFramework>((method, _) => method.Name == "get_IsInFrameworkUpdateThread"
                ? true : Default(method.ReturnType)));
            Service("CommandManager", Fake<ICommandManager>((method, args) =>
            {
                if (method.Name != "ProcessCommand") return Default(method.ReturnType);
                Commands.Add((string)args![0]!);
                return true;
            }));
            var target = Fake<IGameObject>((method, _) => method.Name == "get_GameObjectId" ? 99ul : Default(method.ReturnType));
            Service("TargetManager", Fake<ITargetManager>((method, _) => method.Name == "get_Target" ? target : Default(method.ReturnType)));
            addon = (nint)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
            ((AtkUnitBase*)addon)->IsVisible = true;
            Service("GameGui", Fake<IGameGui>((method, args) => method.Name == "GetAddonByName"
                ? new AtkUnitBasePtr((string?)args![0] == VisibleAddon ? addon : 0) : Default(method.ReturnType)));
            Service("DataManager", new EmptyDataManager());
            var gate = Fake<ICallGateSubscriber<string, string>>((method, _) => method.Name == "InvokeFunc"
                ? JsonSerializer.Serialize(new
                {
                    version = 1, ready = true, character = new { contentId = Character },
                    rows = Counts.Select(pair => new { itemId = pair.Key, ownerContentId = Character, quantity = pair.Value,
                        source = "inventory", containerName = "Inventory 1" }),
                }) : Default(method.ReturnType));
            var host = Fake<IDalamudPluginInterface>((method, _) => method.Name switch
            {
                "GetIpcSubscriber" => gate, "get_InstalledPlugins" => Array.Empty<IExposedPlugin>(),
                _ => Default(method.ReturnType),
            });
            Service("PluginInterface", host);
            var config = (ConfigManager)RuntimeHelpers.GetUninitializedObject(typeof(ConfigManager));
            Field(config, "log", log);
            Field(config, "currentAccountId", "fixture");
            Field(config, "accounts", new Dictionary<string, AccountConfig> { ["fixture"] = new() { AccountId = "fixture", Settings = Configuration } });
            Field(Plugin, "<ConfigManager>k__BackingField", config);
            Field(Plugin, "accountInitialized", true);
            Field(Plugin, "<Engine>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(MogtomeEngine)));
            Field(Plugin, "<Blunderville>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(BlundervilleService)));
            Field(Plugin, "<MoogleShop>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(MoogleShopService)));
            Field(Plugin, "<XaDatabase>k__BackingField", new XaDatabaseIPC(host));
            Field(Plugin, "<YesAlreadyIPC>k__BackingField", new YesAlreadyIPC(log));
            Field(Plugin, "<KeyboundBrawler>k__BackingField", Typer);
            Loop = new(Game, Typer);
            Field(Plugin, "<KeyboundLoop>k__BackingField", Loop);
            Shop = new(Plugin);
            Field(Plugin, "<KeyboundShop>k__BackingField", Shop);
            originalCatalog = typeof(KeyboundBrawlerShop).GetField("catalog", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Offers(Offer);
            originalCurrency = CurrencyManager.Addresses.Instance.Value;
            originalWallet = CurrencyManager.Addresses.GetItemCount.Value;
            currencySlot = (nint)NativeMemory.AllocZeroed((nuint)sizeof(nint));
            currency = (nint)NativeMemory.AllocZeroed((nuint)sizeof(CurrencyManager));
            *(nint*)currencySlot = currency;
            CurrencyManager.Addresses.Instance.Value = currencySlot;
            CurrencyManager.Addresses.GetItemCount.Value = (nint)(delegate* unmanaged<CurrencyManager*, uint, uint>)&ReadWallet;
            nativeWallet = 9500;
        }
        internal void ArmFarming()
        {
            Field(Plugin, "keyboundSessionSettings", Settings);
            Field(Plugin, "keyboundSessionAccount", "fixture");
            Field(Plugin, "keyboundSessionCharacter", Character);
            Typer.Observe(true, false, 23, KeyboundEventLoop.Lobby);
            Assert.True(Typer.Start(Settings));
            Loop.Start(Environment.TickCount64);
        }
        internal void UpdateFarming() => typeof(Plugin).GetMethod("UpdateKeyboundBrawler", Fields)!.Invoke(Plugin, null);
        internal KeyboundShopSession Session() => new(Settings, "fixture", Character, BlundervilleEndingLocation.Stay);
        internal void BeginShop()
        {
            Field(Plugin, "<IsKeyboundShoppingTrip>k__BackingField", true);
            SetShopField("session", Session());
            SetShopField("running", true);
            SetShopField("started", DateTime.UtcNow);
        }
        internal void Offers(params BlundervilleOffer[] offers)
        {
            typeof(KeyboundBrawlerShop).GetField("catalog", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, offers);
            SetShopField("catalog", offers);
        }
        internal BlundervilleOffer? Next(KeyboundShopSession session, uint wallet)
        {
            object?[] args = [session, wallet, null];
            Assert.True((bool)typeof(KeyboundBrawlerShopService).GetMethod("TryNextOffer", Fields)!.Invoke(Shop, args)!);
            return (BlundervilleOffer?)args[2];
        }
        internal bool CheckEligibility(BlundervilleOffer offer)
            => (bool)typeof(KeyboundBrawlerShopService).GetMethod("CheckEligibility", Fields)!.Invoke(Shop, [offer])!;
        internal void Complete() => typeof(KeyboundBrawlerShopService).GetMethod("Complete", Fields)!.Invoke(Shop, null);
        internal void SetShopField(string name, object value) => Field(Shop, name, value);
        private static void Field(object target, string name, object value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
        private void Service(string name, object value)
        {
            var property = typeof(Plugin).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            originals.TryAdd(property, property.GetValue(null));
            property.SetValue(null, value);
        }
        public void Dispose()
        {
            Typer.Dispose();
            CurrencyManager.Addresses.Instance.Value = originalCurrency;
            CurrencyManager.Addresses.GetItemCount.Value = originalWallet;
            typeof(KeyboundBrawlerShop).GetField("catalog", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, originalCatalog);
            foreach (var (property, value) in originals) property.SetValue(null, value);
            NativeMemory.Free((void*)currencySlot);
            NativeMemory.Free((void*)currency);
            NativeMemory.Free((void*)addon);
        }
    }
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, ShoppingProxy>();
        ((ShoppingProxy)(object)proxy).InvokeHandler = handler ?? ((method, _) => Default(method.ReturnType));
        return proxy;
    }

    public class ShoppingProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? InvokeHandler { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => InvokeHandler?.Invoke(method!, args);
    }
}
