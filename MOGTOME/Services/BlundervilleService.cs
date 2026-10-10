using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed class BlundervilleService : IDisposable
{
    public const string BuildMarker = "devhub-I518-select-missing-20261010-01";
    private enum Stage { Idle, Farming, Finishing, LeavingParty, Shopping, Ending }
    private readonly Plugin plugin;
    private readonly BlundervilleGameAdapter game;
    private readonly BlundervilleReloadAttempt reload = new();
    private BlundervilleProgress progress = new();
    private Stage stage;
    private ulong character;
    private BlundervilleSettings? sessionSettings;
    private BlundervilleSettings? reloadSettings;
    private bool catalogLoaded;
    private bool ownsMovement;
    private bool ownsTravel;
    private bool queueParticipation;
    private bool squareEntrySubmitted;
    private bool squareMenuSubmitted;
    private bool squareTalkSubmitted;
    private bool spectatorSubmitted;
    private bool exitSubmitted;
    private bool leaveSubmitted;
    private bool leaveConfirmed;
    private bool finishMustLeave;
    private bool queueCancelRejected;
    private bool innStarted;
    private ulong interactionNpc;
    private DateTime lastUpdate;
    private DateTime stepStarted;
    private DateTime nextInteraction;
    private BlundervilleOffer? pendingPurchase;
    private BlundervilleOffer? settlingPurchase;
    private DateTime purchaseVerifiedAt;
    private bool settlingCleanupSubmitted;
    private string ownedShopAddon = string.Empty;
    private uint selectedCategoryItem;
    private DateTime categoryRequestedAt;
    private DateTime shopVisibleAt;
    private bool purchaseConfirmed;
    private bool uncertainPurchase;
    private bool walletTargetReached;
    private bool disposed;
    private int stopRequested;
    private int beforeCount;
    private uint beforeWallet;
    private DateTime submittedAt;
    private readonly string loadMarker = $"{BuildMarker}/pid={Environment.ProcessId}/utc={DateTimeOffset.UtcNow:O}";
    internal IReadOnlyList<BlundervilleOffer> Catalog => game.Catalog;
    public bool IsRunning => stage != Stage.Idle;
    public int SessionCycles => progress.Cycles;
    public UiText Status { get; private set; } = Ui.M("BV_Idle");
    public bool Ready => !disposed && plugin.CanSelectUiLanguage && plugin.Engine != null &&
        Plugin.ObjectTable.LocalPlayer != null && !Plugin.Condition[ConditionFlag.BetweenAreas] &&
        !Plugin.Condition[ConditionFlag.BetweenAreas51] && !Plugin.Condition[ConditionFlag.LoggingOut];

    public BlundervilleService(Plugin plugin)
    {
        this.plugin = plugin;
        game = new(plugin);
        Plugin.Log.Information("[MOGTOME][BV][Reload] startup {Marker}; dll={Dll}", loadMarker, typeof(Plugin).Assembly.Location);
    }

    private BlundervilleSettings Settings => plugin.Configuration.Blunderville;
    public void ReloadSelectionChanged()
    {
        reload.Cancel();
        Plugin.Log.Information("[MOGTOME][BV][Reload] pending cancelled {Marker}; selection applies next load", loadMarker);
    }

    public bool Start()
    {
        return Request(Stage.Farming);
    }
    public bool Buy() => Request(Stage.Shopping);
    private bool Request(Stage requested)
    {
        if (Volatile.Read(ref stopRequested) != 0) return Reject("BV_Stopped");
        if (!Ready || IsRunning || plugin.Engine.IsRunning || plugin.IsEngineStartQueued || plugin.MoogleShop?.IsRunning == true || plugin.IsMoogleShopActionQueued)
            return Reject("BV_Unavailable");
        var inArena = Plugin.ClientState.TerritoryType == BlundervilleGameAdapter.Arena;
        if ((inArena && requested != Stage.Farming) ||
            (Plugin.Condition[ConditionFlag.BoundByDuty] && !inArena) || Plugin.Condition[ConditionFlag.InDutyQueue] ||
            BlundervilleGameAdapter.Visible("ContentsFinderConfirm") || BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            Plugin.Log.Warning("[MOGTOME][BV] busy client; territory={Territory}; duty={Duty}; queued={Queued}; pop={Pop}; yesno={Yesno}; marker={Marker}",
                Plugin.ClientState.TerritoryType, Plugin.Condition[ConditionFlag.BoundByDuty], Plugin.Condition[ConditionFlag.InDutyQueue],
                BlundervilleGameAdapter.Visible("ContentsFinderConfirm"), BlundervilleGameAdapter.Visible("SelectYesno"), loadMarker);
            return Reject("BV_BusyClient");
        }
        if (requested == Stage.Farming && !BlundervilleProgress.HasLimit(Settings)) return Reject("BV_NeedLimit");
        if ((requested == Stage.Shopping || Settings.ShopWhenFinished) &&
            (uncertainPurchase || Settings.PurchaseReviewRequired)) return Reject("BV_UncertainPurchase");
        if (plugin.Configuration.MoogleShop.PurchaseReviewRequired) return Reject("Shop_ReviewRequired");
        if (!catalogLoaded) LoadCatalog();
        if (!BlundervilleGameAdapter.TryWallet(out var wallet)) return Reject("BV_NoWallet");
        if (BlundervilleGameAdapter.Role() == BlundervilleRole.Unknown) return Reject("BV_NoRole");
        reload.Cancel();
        character = Plugin.PlayerState.ContentId;
        sessionSettings = Settings;
        if (requested == Stage.Farming)
        {
            progress = new();
            if (inArena) progress.Enter();
        }
        walletTargetReached = false;
        finishMustLeave = queueCancelRejected = false;
        progress.ResetEntry();
        spectatorSubmitted = exitSubmitted = leaveSubmitted = leaveConfirmed = innStarted = false;
        selectedCategoryItem = 0;
        categoryRequestedAt = shopVisibleAt = default;
        queueParticipation = requested == Stage.Farming;
        plugin.YesAlreadyIPC.Pause();
        if (!plugin.YesAlreadyIPC.IsPaused) return Reject("BV_DialogControl");
        SetStage(requested, requested == Stage.Farming ? "BV_Farming" : "BV_Shopping");
        Plugin.Log.Information("[MOGTOME][BV] accepted {Action}; marker={Marker}; cycles={Cycles}; wallet={Wallet}; currentArena={CurrentArena}", requested, loadMarker, progress.Cycles, wallet, inArena);
        return true;
    }

    private bool Reject(string key) { Status = Ui.M(key); Plugin.Log.Warning("[MOGTOME][BV] rejected: {Reason}", Status.English); return false; }
    private void SetStage(Stage value, string key)
    {
        stage = value;
        stepStarted = DateTime.UtcNow;
        interactionNpc = 0;
        nextInteraction = default;
        Status = Ui.M(key);
        Plugin.Log.Information("[MOGTOME][BV] {Status}; marker={Marker}", Status.English, loadMarker);
    }
    private void LoadCatalog()
    {
        catalogLoaded = true;
        try { game.LoadCatalog(); ShopOfferEligibility.RefreshAchievements(Catalog.Select(offer => offer.Gate)); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][BV] catalog unavailable"); Status = Ui.M("BV_NoCatalog"); }
    }


    public void Update()
    {
        if (Interlocked.Exchange(ref stopRequested, 0) != 0) { Stop(); return; }
        if (DateTime.UtcNow - lastUpdate < TimeSpan.FromMilliseconds(500)) return;
        lastUpdate = DateTime.UtcNow;
        try
        {
            if (plugin.CanSelectUiLanguage)
            {
                if (!catalogLoaded) LoadCatalog();
                if (reloadSettings == null)
                {
                    reloadSettings = Settings;
                    reload.Capture(Settings.ReloadScenario); // Snapshot the first registered account for this plugin load.
                }
                else if (!ReferenceEquals(reloadSettings, Settings)) reload.Cancel();
                var scenario = reload.Consume(Ready);
                if (scenario != BlundervilleReloadScenario.None)
                {
                    Plugin.Log.Information("[MOGTOME][BV][Reload] consumed {Marker}; scenario={Scenario}", loadMarker, scenario);
                    // Refuse to disturb another owner's activity during startup cleanup.
                    if (!IsRunning && !plugin.Engine.IsRunning && !plugin.IsEngineStartQueued && plugin.MoogleShop?.IsRunning != true && !plugin.IsMoogleShopActionQueued)
                    {
                        StopCore(false);
                        var accepted = !reload.IsCancelled && Ready && (scenario == BlundervilleReloadScenario.Start ? Start() : Buy());
                        Plugin.Log.Information("[MOGTOME][BV][Reload] dispatched {Marker}; accepted={Accepted}", loadMarker, accepted);
                    }
                    else Plugin.Log.Warning("[MOGTOME][BV][Reload] blocked {Marker}; automation is already active", loadMarker);
                }
            }
            if (!IsRunning) return;
            if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != character || !ReferenceEquals(Settings, sessionSettings))
            { Fail("BV_SessionChanged"); return; }
            if (!Ready) return;
            if (!BlundervilleGameAdapter.TryWallet(out var wallet)) { Fail("BV_NoWallet"); return; }
            switch (stage)
            {
                case Stage.Farming: UpdateFarming(wallet); break;
                case Stage.Finishing: UpdateFinishing(); break;
                case Stage.LeavingParty: UpdateLeavingParty(); break;
                case Stage.Shopping: UpdateShopping(wallet); break;
                case Stage.Ending: UpdateEnding(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MOGTOME][BV] action failed; no automatic resubmission");
            Fail("BV_ActionFailed");
        }
    }

    internal void HandleRegistrationChatMessage(string message)
    {
        if (disposed || stage != Stage.Farming || Volatile.Read(ref stopRequested) != 0 ||
            !progress.RegistrationSubmitted || progress.InArena || !Ready ||
            Plugin.ClientState.TerritoryType == BlundervilleGameAdapter.Arena ||
            Plugin.PlayerState.ContentId != character || !ReferenceEquals(Settings, sessionSettings)) return;
        if (!GameText.MatchesLogMessage(message, 7461)) return;
        if (progress.ObserveAreaChangeRejection(Plugin.Condition[ConditionFlag.InDutyQueue] ||
            BlundervilleGameAdapter.Visible("ContentsFinderConfirm"), DateTime.UtcNow))
        {
            Status = Ui.M("BV_RegistrationRetry");
            Plugin.Log.Information("[MOGTOME][BV] confirmed LogMessage7461; retry within original entry deadline; marker={Marker}", loadMarker);
        }
    }

    private void UpdateFarming(uint wallet)
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (territory == BlundervilleGameAdapter.Arena)
        {
            if (!progress.InArena)
                Plugin.Log.Information("[MOGTOME][BV] entered arena; cycles={Cycles}; wallet={Wallet}; marker={Marker}", progress.Cycles, wallet, loadMarker);
            progress.Enter();
            walletTargetReached |= BlundervilleProgress.WalletMet(Settings, wallet);
            Status = Ui.M("BV_ArenaIdle");
            if (BlundervilleGameAdapter.Visible("FGSSpectatorMenu"))
            {
                progress.ObserveElimination();
                Status = Ui.M("BV_Exiting");
                if (!spectatorSubmitted)
                {
                    spectatorSubmitted = true;
                    Plugin.Log.Information("[MOGTOME][BV] spectator exit requested; wallet={Wallet}; marker={Marker}", wallet, loadMarker);
                    if (!BlundervilleGameAdapter.Callback("FGSSpectatorMenu", 3)) Fail("BV_UiMismatch");
                }
            }
            if (spectatorSubmitted && !exitSubmitted && BlundervilleGameAdapter.Visible("FGSExitDialog"))
            {
                exitSubmitted = true;
                Plugin.Log.Information("[MOGTOME][BV] confirming eliminated arena exit; marker={Marker}", loadMarker);
                if (!BlundervilleGameAdapter.Callback("FGSExitDialog", 0)) { Fail("BV_UiMismatch"); return; }
                progress.SubmitExit();
            }
            return; // Even a met wallet target waits for elimination and normal exit.
        }
        if (progress.InArena)
        {
            if (!progress.Return()) { Fail("BV_UnconfirmedExit"); return; }
            Plugin.Log.Information("[MOGTOME][BV] confirmed elimination/return; cycles={Cycles}; wallet={Wallet}; marker={Marker}", progress.Cycles, wallet, loadMarker);
            spectatorSubmitted = exitSubmitted = false;
            stepStarted = DateTime.UtcNow;
            interactionNpc = 0;
        }
        var role = BlundervilleGameAdapter.Role();
        if (role == BlundervilleRole.Unknown) { Fail("BV_NoRole"); return; }
        if (walletTargetReached || progress.Finished(Settings, wallet))
        {
            finishMustLeave = role == BlundervilleRole.Member && (walletTargetReached || BlundervilleProgress.WalletMet(Settings, wallet));
            SetStage(Stage.Finishing, "BV_FarmDone");
            CancelQueueParticipation();
            CancelMovementAndTravel();
            UpdateFinishing();
            return;
        }
        if (BlundervilleGameAdapter.Visible("ContentsFinderConfirm"))
        {
            if (!BlundervilleGameAdapter.BlundervilleEntryVisible()) { Fail("BV_UiMismatch"); return; }
            if (progress.TrySubmitCommence())
            {
                Plugin.Log.Information("[MOGTOME][BV] accepting Blunderville entry; role={Role}; marker={Marker}", role, loadMarker);
                if (!BlundervilleGameAdapter.Callback("ContentsFinderConfirm", 8)) Fail("BV_UiMismatch");
            }
            Status = Ui.M("BV_WaitEntry");
            return;
        }
        if (!Plugin.Condition[ConditionFlag.InDutyQueue] && progress.RegistrationTimedOut(DateTime.UtcNow))
        {
            Fail("BV_Timeout");
            return;
        }
        if (role == BlundervilleRole.Member)
        {
            CancelMovementAndTravel();
            // Members never travel or interact with a registrar.
            if (BlundervilleGameAdapter.Visible("FGSEnterDialog") && !progress.RegistrationSubmitted)
            {
                progress.SubmitRegistration();
                if (!BlundervilleGameAdapter.Callback("FGSEnterDialog", 0)) Fail("BV_UiMismatch");
            }
            Status = Ui.M("BV_MemberWait");
            return;
        }
        if (Plugin.Condition[ConditionFlag.InDutyQueue] || progress.RegistrationSubmitted || progress.CommenceSubmitted)
        {
            Status = Ui.M("BV_WaitEntry");
            return;
        }
        if (!EnsureSquare()) return;
        if (BlundervilleGameAdapter.Visible("FGSEnterDialog"))
        {
            if (interactionNpc == 0 || Plugin.TargetManager.Target?.GameObjectId != interactionNpc) { Fail("BV_UiMismatch"); return; }
            // Leadership and targets were freshly checked above before registration.
            progress.SubmitRegistration();
            stepStarted = progress.RegistrationStartedAt!.Value;
            Plugin.Log.Information("[MOGTOME][BV] registering Blunderville; role={Role}; wallet={Wallet}; marker={Marker}", role, wallet, loadMarker);
            if (!BlundervilleGameAdapter.Callback("FGSEnterDialog", 0)) Fail("BV_UiMismatch");
            return;
        }
        Interact("Blunderville Registrar", "BV_Registrar");
    }

    private bool EnsureSquare()
    {
        if (Plugin.ClientState.TerritoryType == BlundervilleGameAdapter.Square)
        {
            if (squareEntrySubmitted || squareMenuSubmitted || squareTalkSubmitted) interactionNpc = 0;
            squareEntrySubmitted = squareMenuSubmitted = squareTalkSubmitted = false;
            ownsTravel = false;
            return true;
        }
        if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(120)) { Fail("BV_Timeout"); return false; }
        if (Plugin.ClientState.TerritoryType != BlundervilleGameAdapter.Saucer)
        {
            if (!ownsTravel)
            {
                var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().FirstOrDefault(a => a.IsAetheryte && a.Territory.RowId == BlundervilleGameAdapter.Saucer);
                if (aetheryte.RowId == 0 || !StartTravel(aetheryte.PlaceName.Value.Name.ToString())) { Fail("BV_TravelUnavailable"); return false; }
            }
            Status = Ui.M("BV_TravelSquare");
            return false;
        }
        if (ownsTravel && IsTravelBusy()) return false;
        ownsTravel = false;
        var owned = interactionNpc != 0 && Plugin.TargetManager.Target?.GameObjectId == interactionNpc;
        if (owned && game.SquareEntryTalkVisible(denied: true)) { Fail("BV_EntryLocked"); return false; }
        if (BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            if (!owned || !game.SquareEntryPromptVisible()) { Fail("BV_UiMismatch"); return false; }
            if (!squareEntrySubmitted)
            {
                squareEntrySubmitted = true; // Consume before callback; Stop cancels future registration.
                Plugin.Log.Information("[MOGTOME][BV] confirming owned Square entry; marker={Marker}", loadMarker);
                if (!GameHelpers.ClickYesIfVisible()) Fail("BV_UiMismatch");
            }
            return false;
        }
        if (squareEntrySubmitted) return false;
        if (BlundervilleGameAdapter.Visible("Talk"))
        {
            if (!owned || !game.SquareEntryTalkVisible(denied: false)) { Fail("BV_UiMismatch"); return false; }
            if (!squareTalkSubmitted)
            {
                squareTalkSubmitted = true;
                if (!BlundervilleGameAdapter.Callback("Talk", 0)) Fail("BV_UiMismatch");
            }
            return false;
        }
        if (BlundervilleGameAdapter.Visible("SelectString"))
        {
            if (!owned) { Fail("BV_UiMismatch"); return false; }
            if (!squareMenuSubmitted)
            {
                squareMenuSubmitted = true;
                if (!BlundervilleGameAdapter.Callback("SelectString", 0)) Fail("BV_UiMismatch");
            }
            return false;
        }
        if (squareMenuSubmitted || squareTalkSubmitted) return false;
        Interact("Blunderville attendant", "BV_TravelSquare");
        return false;
    }

    private void Interact(string npcName, string statusKey)
    {
        Status = Ui.M(statusKey);
        if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(120)) { Fail("BV_Timeout"); return; }
        if (DateTime.UtcNow < nextInteraction) return;
        var npc = game.FindNpc(npcName);
        if (npc == null)
        {
            Plugin.Log.Warning("[MOGTOME][BV] NPC unavailable: {Npc}; territory={Territory}; marker={Marker}",
                npcName, Plugin.ClientState.TerritoryType, loadMarker);
            Fail("BV_NpcUnavailable");
            return;
        }
        if (game.ApproachAndInteract(npc, ref ownsMovement))
        {
            interactionNpc = npc.GameObjectId;
            nextInteraction = DateTime.UtcNow.AddSeconds(3);
        }
    }

    private void UpdateFinishing()
    {
        if (queueCancelRejected) { Fail("BV_QueueCancelFailed"); return; }
        if (Plugin.Condition[ConditionFlag.InDutyQueue] || BlundervilleGameAdapter.Visible("ContentsFinderConfirm"))
        {
            if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(15)) Fail("BV_QueueCancelFailed");
            return;
        }
        if (finishMustLeave) SetStage(Stage.LeavingParty, "BV_LeavingParty");
        else FinishFarming();
    }

    private void UpdateLeavingParty()
    {
        if (Plugin.Condition[ConditionFlag.InDutyQueue] || BlundervilleGameAdapter.Visible("ContentsFinderConfirm"))
        {
            if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(15)) Fail("BV_QueueCancelFailed");
            return;
        }
        var role = BlundervilleGameAdapter.Role();
        if (role == BlundervilleRole.Solo) { FinishFarming(); return; }
        if (role != BlundervilleRole.Member) { Fail("BV_NoRole"); return; }
        if (!leaveSubmitted)
        {
            leaveSubmitted = true;
            if (!BlundervilleGameAdapter.SendGameCommand("/leave")) { Fail("BV_ActionFailed"); return; }
        }
        if (!leaveConfirmed && BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            if (!BlundervilleGameAdapter.ConfirmPartyLeave()) { Fail("BV_UiMismatch"); return; }
            leaveConfirmed = true;
        }
        if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(20)) Fail("BV_PartyLeaveFailed");
    }

    private void FinishFarming()
    {
        Plugin.Log.Information("[MOGTOME][BV] farming completed; cycles={Cycles}; shop={Shop}; marker={Marker}", progress.Cycles, Settings.ShopWhenFinished, loadMarker);
        if (Settings.ShopWhenFinished && (uncertainPurchase || Settings.PurchaseReviewRequired)) Fail("BV_UncertainPurchase");
        else if (Settings.ShopWhenFinished) SetStage(Stage.Shopping, "BV_Shopping");
        else Complete("BV_FarmDone");
    }

    private void UpdateShopping(uint wallet)
    {
        if (settlingPurchase != null)
        {
            if (!settlingCleanupSubmitted && BlundervilleGameAdapter.Visible("SelectYesno"))
            {
                settlingCleanupSubmitted = true;
                if (!BlundervilleGameAdapter.CancelOwnedPurchase(settlingPurchase)) { Fail("BV_UiMismatch"); return; }
            }
            if (BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("ShopExchangeCurrencyDialog") ||
                BlundervilleGameAdapter.Visible("ShopExchangeItemDialog") || !BlundervilleGameAdapter.ShopReady(ownedShopAddon))
            {
                if (DateTime.UtcNow - purchaseVerifiedAt > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
                return;
            }
            settlingPurchase = null;
        }
        if (pendingPurchase != null)
        {
            var purchase = pendingPurchase;
            if (!BlundervilleGameAdapter.TryInventory(purchase.ItemId, out var count, out _)) { Fail("BV_NoInventory"); return; }
            if (BlundervilleProgress.PurchaseVerified(beforeCount, beforeWallet, purchase.ReceiveCount, purchase.Price, count, wallet))
            {
                Plugin.Log.Information("[MOGTOME][BV] purchase verified item={Item} received={Quantity} spent={Price}; marker={Marker}", purchase.ItemId, purchase.ReceiveCount, purchase.Price, loadMarker);
                pendingPurchase = null;
                settlingPurchase = purchase;
                purchaseVerifiedAt = DateTime.UtcNow;
                settlingCleanupSubmitted = false;
                Settings.PurchaseReviewRequired = false;
                plugin.ConfigManager.SaveCurrentAccount();
                purchaseConfirmed = false;
                stepStarted = DateTime.UtcNow;
                return;
            }
            if (!purchaseConfirmed && (BlundervilleGameAdapter.Visible("SelectYesno") ||
                BlundervilleGameAdapter.Visible("ShopExchangeCurrencyDialog") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog")))
            {
                if (Plugin.TargetManager.Target?.GameObjectId != interactionNpc ||
                    !BlundervilleGameAdapter.TryWallet(out var liveWallet) || liveWallet != beforeWallet || count != beforeCount ||
                    !BlundervilleGameAdapter.TryInventory(purchase.ItemId, out _, out var capacity) || capacity < purchase.ReceiveCount)
                { Fail("BV_UncertainPurchase"); return; }
                purchaseConfirmed = true; // Consume before dispatch: never click twice on uncertainty.
                if (!BlundervilleGameAdapter.ConfirmPurchase(purchase, ownedShopAddon))
                {
                    BlundervilleGameAdapter.LogPurchaseMismatch(purchase);
                    Fail("BV_UiMismatch");
                    return;
                }
            }
            if (DateTime.UtcNow - submittedAt > TimeSpan.FromSeconds(10)) Fail("BV_UncertainPurchase");
            return;
        }
        var targets = Settings.PurchaseTargets;
        if (targets == null) { Fail("BV_NoCatalog"); return; }
        BlundervilleOffer? next = null;
        foreach (var target in targets.Where(t => t.Value > 0))
        {
            if (!BlundervilleGameAdapter.TryInventory(target.Key, out var count, out _)) { Fail("BV_NoInventory"); return; }
            if (BlundervilleProgress.Deficit(target.Value, count) == 0) continue;
            var offers = Catalog.Where(o => o.ItemId == target.Key).ToArray();
            if (offers.Length != 1) { Fail("BV_NoCatalog"); return; }
            next = offers[0];
            if (next.ReceiveCount == 0 || BlundervilleProgress.Deficit(target.Value, count) % next.ReceiveCount != 0)
            { Fail("BV_QuantityMismatch"); return; }
            break;
        }
        if (next == null)
        {
            CloseShop();
            if (Settings.EndingLocation == BlundervilleEndingLocation.Stay) Complete("BV_ShopDone");
            else SetStage(Stage.Ending, "BV_EndingTravel");
            return;
        }
        if (!CheckEligibility(next)) return;
        if (!EnsureSquare()) return;
        if (Plugin.Condition[ConditionFlag.InDutyQueue]) { Fail("BV_BusyClient"); return; }
        if (!BlundervilleGameAdapter.Visible("ShopExchangeCurrency") && !BlundervilleGameAdapter.Visible("ShopExchangeItem"))
        {
            if (BlundervilleGameAdapter.Visible("SelectString") && interactionNpc != 0 &&
                Plugin.TargetManager.Target?.GameObjectId == interactionNpc)
            {
                // MGF trader's direct exchange option; the resulting shop must pass exact validation.
                if (!BlundervilleGameAdapter.Callback("SelectString", 0)) Fail("BV_UiMismatch");
                return;
            }
            Interact("MGF trader", "BV_Shopping");
            return;
        }
        if (interactionNpc == 0 || Plugin.TargetManager.Target?.GameObjectId != interactionNpc ||
            BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            Plugin.Log.Warning("[MOGTOME][BV] trader ownership mismatch; interacted={Interacted}; targetOwned={Owned}; yesno={Yesno}; marker={Marker}",
                interactionNpc != 0, interactionNpc != 0 && Plugin.TargetManager.Target?.GameObjectId == interactionNpc,
                BlundervilleGameAdapter.Visible("SelectYesno"), loadMarker);
            Fail("BV_UiMismatch");
            return;
        }
        // Own the trader window before validation, so early failures also close it.
        ownedShopAddon = BlundervilleGameAdapter.Visible("ShopExchangeCurrency") ? "ShopExchangeCurrency" : "ShopExchangeItem";
        if (shopVisibleAt == default) shopVisibleAt = DateTime.UtcNow;
        if (!BlundervilleGameAdapter.ShopReady(ownedShopAddon))
        {
            if (DateTime.UtcNow - shopVisibleAt > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
            return;
        }
        if (ownedShopAddon == "ShopExchangeCurrency")
        {
            if (!BlundervilleGameAdapter.EnsureOfferCategory(next, ref selectedCategoryItem, out var categoryReady))
            {
                BlundervilleGameAdapter.LogOfferMismatch(next, ownedShopAddon);
                Fail("BV_UiMismatch");
                return;
            }
            if (!categoryReady)
            {
                if (categoryRequestedAt == default) categoryRequestedAt = DateTime.UtcNow;
                if (DateTime.UtcNow - categoryRequestedAt > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
                return;
            }
            categoryRequestedAt = default;
        }
        if (!BlundervilleGameAdapter.TryValidateOffer(next, out var addon, out var row))
        {
            BlundervilleGameAdapter.LogOfferMismatch(next, ownedShopAddon);
            Fail("BV_UiMismatch");
            return;
        }
        if (!BlundervilleGameAdapter.TryInventory(next.ItemId, out beforeCount, out var available)) { Fail("BV_NoInventory"); return; }
        if (BlundervilleProgress.Deficit(targets[next.ItemId], beforeCount) < next.ReceiveCount) return;
        if (!BlundervilleGameAdapter.TryWallet(out beforeWallet)) { Fail("BV_NoWallet"); return; }
        if (beforeWallet < next.Price)
        {
            Plugin.Log.Warning("[MOGTOME][BV] insufficient MGF; item={Item}; required={Price}; wallet={Wallet}; marker={Marker}", next.ItemId, next.Price, beforeWallet, loadMarker);
            Fail("BV_NoFunds");
            return;
        }
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(next.ItemId);
        if (available < next.ReceiveCount || (item.IsUnique && beforeCount > 0)) { Fail("BV_NoCapacity"); return; }
        if (!CheckEligibility(next)) return;
        Settings.PurchaseReviewRequired = true;
        plugin.ConfigManager.SaveCurrentAccount(); // Retain the hold across unload/reload until verified or explicitly reviewed.
        pendingPurchase = next; // Record ownership BEFORE submission, including rejected/uncertain callback results.
        ownedShopAddon = addon;
        submittedAt = DateTime.UtcNow;
        purchaseConfirmed = false;
        Plugin.Log.Information("[MOGTOME][BV] purchase dispatch item={Item}; quantity={Quantity}; cost={Price}; countBefore={Count}; walletBefore={Wallet}; marker={Marker}", next.ItemId, next.ReceiveCount, next.Price, beforeCount, beforeWallet, loadMarker);
        var accepted = addon == "ShopExchangeCurrency"
            ? BlundervilleGameAdapter.Callback(addon, 0, row, 1, 0)
            : BlundervilleGameAdapter.Callback(addon, 0, row, 1);
        if (!accepted) Fail("BV_UncertainPurchase");
    }

    private bool CheckEligibility(BlundervilleOffer offer)
    {
        var availability = ShopOfferEligibility.Read(offer.Gate);
        if (availability == ShopOfferAvailability.Available) return true;
        Fail(availability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
        return false;
    }

    private static bool IsTravelBusy() => Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
    private bool StartTravel(string destination)
    {
        if (IsTravelBusy()) return false;
        ownsTravel = Plugin.CommandManager.ProcessCommand("/li " + destination);
        return ownsTravel;
    }

    private void UpdateEnding()
    {
        var rooms = new uint[] { 0, 178, 179, 177, 429, 843, 990, 1205 };
        // Lifestream's local inn command uses its sorted inn list, including Kugane.
        var innCommands = new int[] { 0, 2, 3, 1, 4, 6, 7, 8 };
        var selected = (int)Settings.EndingLocation;
        if (selected < 1 || selected >= rooms.Length) { Fail("BV_TravelUnavailable"); return; }
        if (DateTime.UtcNow - stepStarted > TimeSpan.FromSeconds(180)) { Fail("BV_Timeout"); return; }
        if (Plugin.ClientState.TerritoryType == rooms[selected] && !IsTravelBusy())
        {
            ownsTravel = false;
            Complete("BV_ShopDone");
            return;
        }
        if (!innStarted)
        {
            innStarted = true; // One owned native route; never request repairs or resubmit it.
            Plugin.Log.Information("[MOGTOME][BV] ending inn dispatched; selected={Selected}; room={Room}; marker={Marker}",
                selected, rooms[selected], loadMarker);
            if (!StartTravel("inn " + innCommands[selected])) Fail("BV_TravelUnavailable");
            return;
        }
        // The route can become idle after confirming entry but before the territory changes.
        // Wait for the selected room under the existing timeout without submitting again.
    }

    private void CancelQueueParticipation()
    {
        if (!queueParticipation) return;
        queueParticipation = false;
        if (BlundervilleGameAdapter.BlundervilleEntryVisible()) queueCancelRejected |= !BlundervilleGameAdapter.Callback("ContentsFinderConfirm", 9);
        else if (Plugin.Condition[ConditionFlag.InDutyQueue]) queueCancelRejected |= !BlundervilleGameAdapter.CancelBlundervilleQueue();
        if (BlundervilleGameAdapter.Visible("FGSEnterDialog")) queueCancelRejected |= !BlundervilleGameAdapter.Callback("FGSEnterDialog", -2);
    }
    private void CancelMovementAndTravel()
    {
        if (interactionNpc != 0 && Plugin.TargetManager.Target?.GameObjectId == interactionNpc &&
            Plugin.ClientState.TerritoryType == BlundervilleGameAdapter.Saucer && game.SquareEntryPromptVisible())
            BlundervilleGameAdapter.Callback("SelectYesno", 1, 0);
        squareEntrySubmitted = squareMenuSubmitted = squareTalkSubmitted = false;
        if (ownsMovement) { ownsMovement = false; plugin.VNavIPC.Stop(); }
        if (ownsTravel)
        {
            ownsTravel = false;
            Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction();
        }
    }
    private void CloseShop()
    {
        if (interactionNpc != 0 && Plugin.TargetManager.Target?.GameObjectId == interactionNpc)
        {
            var purchase = pendingPurchase ?? settlingPurchase;
            if (purchase != null && !BlundervilleGameAdapter.CancelOwnedPurchase(purchase))
                throw new InvalidOperationException("Owned purchase dialog could not be safely cancelled; automatic dialog acceptance remains paused");
            BlundervilleGameAdapter.CloseOwnedShop(ownedShopAddon);
        }
        ownedShopAddon = string.Empty;
    }

    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Stop() { if (disposed) return; Interlocked.Exchange(ref stopRequested, 0); reload.Cancel(); StopCore(true); }
    public void AcknowledgePurchaseReview()
    {
        if (!Ready || IsRunning || !Settings.PurchaseReviewRequired || BlundervilleGameAdapter.Visible("SelectYesno")) return;
        foreach (var target in Settings.PurchaseTargets)
            if (!BlundervilleGameAdapter.TryInventory(target.Key, out _, out _)) return;
        if (!BlundervilleGameAdapter.TryWallet(out _)) return;
        Settings.PurchaseReviewRequired = uncertainPurchase = false;
        plugin.YesAlreadyIPC.Unpause();
        plugin.ConfigManager.SaveCurrentAccount();
        Status = Ui.M("BV_Idle");
        Plugin.Log.Information("[MOGTOME][BV] operator reviewed uncertain purchase; hold cleared; marker={Marker}", loadMarker);
    }
    private void StopCore(bool manual)
    {
        if (pendingPurchase != null) uncertainPurchase = true;
        stage = Stage.Idle; // Stop future callbacks even when individual cleanup actions fail.
        progress.ResetEntry();
        foreach (var cleanup in new System.Action[] { CancelQueueParticipation, CancelMovementAndTravel, CloseShop })
            try { cleanup(); } catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][BV] cleanup failed"); }
        if ((pendingPurchase != null || settlingPurchase != null) && ReferenceEquals(Settings, sessionSettings) &&
            BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            Settings.PurchaseReviewRequired = uncertainPurchase = true;
            plugin.ConfigManager.SaveCurrentAccount();
        }
        // An unreadable pending confirmation must not fall through to automatic acceptance after Stop.
        if ((!Settings.PurchaseReviewRequired && !plugin.Configuration.MoogleShop.PurchaseReviewRequired) ||
            (!BlundervilleGameAdapter.Visible("SelectYesno") && !BlundervilleGameAdapter.Visible("ShopExchangeItemDialog"))) plugin.YesAlreadyIPC.Unpause();
        pendingPurchase = null;
        settlingPurchase = null;
        settlingCleanupSubmitted = false;
        if (manual) Status = Ui.M(queueCancelRejected ? "BV_QueueCancelFailed" : "BV_Stopped");
        Plugin.Log.Information("[MOGTOME][BV] stopped; manual={Manual}; uncertainPurchase={Uncertain}; marker={Marker}", manual, uncertainPurchase, loadMarker);
    }
    private void Fail(string key)
    {
        StopCore(false);
        Status = Ui.M(key);
        Plugin.Log.Warning("[MOGTOME][BV] failed: {Status}; marker={Marker}", Status.English, loadMarker);
        Plugin.ChatGui.Print(Status.RenderGame());
    }
    private void Complete(string key)
    {
        StopCore(false);
        Status = Ui.M(key);
        Plugin.Log.Information("[MOGTOME][BV] completed: {Status}; marker={Marker}", Status.English, loadMarker);
    }
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}
