using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    [Fact]
    public unsafe void StandardEquipmentWarningChecksOwnershipThenProceedAndOkWithoutConsumingTheFollowingConfirmation()
    {
        const string prompt = "You will never be able to equip this item.Proceed with the transaction?";
        var values = stackalloc AtkValue[13];
        new Span<AtkValue>(values, 13).Clear();
        values[0].Type = AtkValueType.String;
        values[8].Type = AtkValueType.String;
        values[12] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
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

    public class ShoppingProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? InvokeHandler { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => InvokeHandler?.Invoke(method!, args);
    }
}
