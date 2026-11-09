using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed partial class MoogleShopService : IDisposable
{
    public const string BuildMarker = "mog-shop-0011";
    private readonly Plugin plugin;
    private readonly MoogleShopGameAdapter game;
    private readonly PoeticsShopCatalog poetics = new();
    private readonly string marker = $"{BuildMarker}/pid={Environment.ProcessId}/utc={DateTimeOffset.UtcNow:O}";
    private bool disposed, catalogLoaded, catalogAvailable, running, movement, travel, travelSubmitted, closeSubmitted, confirmationSubmitted;
    private int stopRequested;
    private ulong character, npc;
    private MoogleShopSettings? sessionSettings;
    private Dictionary<uint, int> targets = [];
    private IReadOnlyList<MoogleShopOffer> sessionCatalog = [];
    private MoogleShopOffer? pending, settling;
    private TomestoneMove? pendingMove;
    private int beforeItem, purchaseRow;
    private Dictionary<uint, int> beforeBalances = [];
    private DateTime updated, started, submitted, nextInteraction, shopSeen, menuSeen;
    private string lastMenu = string.Empty;
    private ulong balanceCharacter;
    private IReadOnlyList<MoogleTomestoneBalance> tomestoneBalances = [];
    private bool poeticsMode, poeticsDeliver, proceedSubmitted, equipmentWarningSubmitted, poeticsCatalogAvailable;
    private BlundervilleEndingLocation poeticsEnding;
    private string ownedAddon = "ShopExchangeItem";
    private MoogleShopOffer? ownedPoeticsOffer;
    private PoeticsShopVendor? poeticsVendor;
    private uint categoryItem;
    private bool AutoRetainerLoaded => Plugin.PluginInterface.InstalledPlugins.Any(installed =>
        installed.IsLoaded && string.Equals(installed.InternalName, "AutoRetainer", StringComparison.OrdinalIgnoreCase));
    internal bool PoeticsDeliveryAvailable => TryDeliveryBusy(out _);
    internal uint CurrentTomestoneId => game.CurrentTomestoneId;
    internal IReadOnlyList<MoogleTomestoneBalance> TomestoneBalances
        => balanceCharacter == Plugin.PlayerState.ContentId ? tomestoneBalances : [];
    internal uint CurrencyId { get; set; }
    internal IReadOnlyList<MoogleShopOffer> Catalog => game.Catalog;
    internal IReadOnlyList<MoogleShopOffer> SelectedCatalog => Catalog.Where(o => o.TomestoneId == CurrencyId).ToArray();
    public bool IsRunning => running;
    public UiText Status { get; private set; } = Ui.M("BV_Idle");
    public bool Ready => !disposed && plugin.CanSelectUiLanguage && plugin.Engine != null && Plugin.ClientState.IsLoggedIn &&
        Plugin.ObjectTable.LocalPlayer != null && !Plugin.Condition[ConditionFlag.LoggingOut] &&
        !Plugin.Condition[ConditionFlag.BetweenAreas] && !Plugin.Condition[ConditionFlag.BetweenAreas51];
    private MoogleShopSettings Settings => plugin.Configuration.MoogleShop;

    public MoogleShopService(Plugin plugin)
    {
        this.plugin = plugin;
        game = new(plugin);
        Plugin.Log.Information("[MOGTOME][Shop] startup {Marker}; passive load", marker);
    }

    public void RefreshCatalog()
    {
        if (!Ready || running) return;
        catalogLoaded = true; // A failed automatic inspection stays failed until an explicit Refresh/Open/Buy.
        catalogAvailable = false;
        poeticsCatalogAvailable = false;
        var previousStatus = Status;
        try
        {
            game.LoadCatalog();
            ShopOfferEligibility.RefreshAchievements(Catalog.Select(offer => offer.Gate));
            catalogAvailable = true;
            if (!Catalog.Any(o => o.TomestoneId == CurrencyId)) CurrencyId = Catalog.FirstOrDefault()?.TomestoneId ?? 0;
            foreach (var id in Catalog.SelectMany(o => o.Costs).Select(c => c.ItemId).Distinct())
            {
                var known = BlundervilleGameAdapter.TryBagInventory(id, out var balance, out _);
                Plugin.Log.Information("[MOGTOME][Shop] bag balance currency={Currency}; balance={Balance}; marker={Marker}",
                    id, known ? balance.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?", marker);
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][Shop] catalog unavailable"); Status = Ui.M("Shop_NoCatalog"); }
        if (Settings.BuyPoeticsGear)
        {
            try
            {
                poetics.LoadCatalog();
                poeticsCatalogAvailable = poetics.Offers.Count > 0;
                ShopOfferEligibility.RefreshAchievements(poetics.Offers.Select(candidate => candidate.Offer.Gate));
                if (!poeticsCatalogAvailable) Status = Ui.M("Shop_NoCatalog");
                else if (!catalogAvailable) Status = previousStatus;
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][Poetics] catalog unavailable"); Status = Ui.M("Shop_NoCatalog"); }
        }
    }

    internal (uint ItemId, int? Total) ReadCurrentTomestoneTotal()
    {
        if (!Ready) return (0, null);
        if (!catalogLoaded) RefreshCatalog();
        if (!catalogAvailable || CurrentTomestoneId == 0) return (0, null);
        RefreshTomestoneBalances(force: true);
        return (CurrentTomestoneId, TomestoneBalances.FirstOrDefault(balance => balance.ItemId == CurrentTomestoneId)?.Total);
    }

    private void RefreshTomestoneBalances(bool force = false)
    {
        if (!force && plugin.MainWindow?.IsOpen != true && plugin.ConfigWindow?.IsOpen != true
            && plugin.MoogleShopWindow?.IsOpen != true && plugin.StatsWindow?.IsOpen != true)
            return;
        if (!Ready || !catalogAvailable)
        {
            balanceCharacter = 0;
            tomestoneBalances = [];
            return;
        }
        var ids = Catalog.Select(offer => offer.TomestoneId).Distinct().OrderByDescending(id => id).Take(2).ToArray();
        try
        {
            var stored = plugin.XaDatabase.ReadStocks(ids);
            tomestoneBalances = ids.Select(id =>
            {
                if (!BlundervilleGameAdapter.TryLiveInventoryCounts(id, out var live, out _))
                    return MoogleTomestoneBalance.Unavailable(id);
                var locations = live.ToDictionary(pair => IPC.XaDatabaseIPC.ContainerName(pair.Key), pair => pair.Value);
                return MoogleTomestoneBalance.Read(id, locations, stored.GetValueOrDefault(id));
            }).ToArray();
            balanceCharacter = Plugin.PlayerState.ContentId;
        }
        catch
        {
            // Unreadable display/limit truth stays visible as unavailable. Never
            // substitute a stale carried count for the fresh farming decision.
            tomestoneBalances = ids.Select(MoogleTomestoneBalance.Unavailable).ToArray();
            balanceCharacter = Plugin.PlayerState.ContentId;
        }
    }

    public bool Buy()
    {
        if (plugin.KeyboundShopOwnsAutomation) return Reject("BV_Unavailable");
        if (Volatile.Read(ref stopRequested) != 0 || !Ready || running || plugin.IsEngineStartQueued || plugin.Engine.IsRunning ||
            plugin.Blunderville.IsRunning || plugin.IsBlundervilleActionQueued) return Reject("BV_Unavailable");
        var missingPlugins = GameHelpers.MissingXaDependencies();
        if (missingPlugins.Length > 0) return Reject("Plugin_RequiredXaPlugins", missingPlugins);
        if (Settings.PurchaseReviewRequired || plugin.Configuration.Blunderville.PurchaseReviewRequired) return Reject("Shop_ReviewRequired");
        poeticsMode = Settings.BuyPoeticsGear;
        poeticsDeliver = Settings.DeliverPoeticsGear;
        poeticsEnding = Settings.PoeticsEndingLocation;
        if (poeticsMode && poeticsDeliver && !PoeticsDeliveryAvailable) return Reject("Shop_EligibilityUnknown");
        if ((!poeticsMode && !Enum.IsDefined(Settings.City)) || (poeticsMode && !Enum.IsDefined(poeticsEnding)) ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InDutyQueue] ||
            Plugin.Condition[ConditionFlag.InCombat] || BlundervilleGameAdapter.Visible("ContentsFinderConfirm") ||
            BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("Talk") ||
            BlundervilleGameAdapter.Visible("ShopExchangeItemDialog") ||
            BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency"))
            return Reject("BV_BusyClient");
        RefreshCatalog();
        if (poeticsMode)
        {
            if (!poeticsCatalogAvailable) return Reject("Shop_NoCatalog");
            sessionCatalog = poetics.Offers.Select(candidate => candidate.Offer).ToArray();
            targets = []; // Reused below for this run's receipt-verified Poetics purchases.
        }
        else
        {
            if (!catalogAvailable) return Reject("Shop_NoCatalog");
            sessionCatalog = SelectedCatalog;
            targets = new(Settings.PurchaseTargets ?? []);
        }
        MoogleShopOffer? firstOffer = null;
        var storedStocks = plugin.XaDatabase.ReadStocks(targets.Keys);
        foreach (var target in targets.Where(t => !poeticsMode && t.Value > 0))
        {
            if (!plugin.XaDatabase.TryCount(target.Key, storedStocks.GetValueOrDefault(target.Key), out var count)) return Reject("BV_NoInventory");
            if (count < target.Value && sessionCatalog.Count(o => o.ItemId == target.Key) != 1) return Reject("Shop_NoCatalog");
            if (count < target.Value)
            {
                var offer = sessionCatalog.Single(o => o.ItemId == target.Key);
                var availability = ShopOfferEligibility.Read(offer.Gate);
                if (availability != ShopOfferAvailability.Available)
                    return Reject(availability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
                firstOffer ??= offer;
            }
        }
        var menuOpen = BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString");
        var existingTrader = menuOpen ? game.FindTrader() : null;
        // A manual retry may resume the preserved Moogle menu only after checking its NPC and exact exchange label.
        if (menuOpen && (poeticsMode || Plugin.ClientState.TerritoryType != new uint[] { 129, 130, 132 }[(int)Settings.City] ||
            existingTrader == null || Plugin.TargetManager.Target?.GameObjectId != existingTrader.GameObjectId ||
            firstOffer == null || !game.MatchesExchangeMenu(firstOffer))) return Reject("BV_BusyClient");
        sessionSettings = Settings;
        character = Plugin.PlayerState.ContentId;
        pending = settling = null; pendingMove = null;
        npc = existingTrader?.GameObjectId ?? 0; lastMenu = string.Empty; shopSeen = menuSeen = default;
        movement = travel = travelSubmitted = closeSubmitted = confirmationSubmitted = false;
        proceedSubmitted = equipmentWarningSubmitted = false;
        ownedAddon = poeticsMode ? string.Empty : "ShopExchangeItem";
        ownedPoeticsOffer = null; poeticsVendor = null; categoryItem = 0;
        ResetPoeticsProgress();
        started = DateTime.UtcNow; nextInteraction = default;
        plugin.YesAlreadyIPC.Pause();
        if (!plugin.YesAlreadyIPC.IsPaused) return Reject("BV_DialogControl");
        running = true;
        Status = Ui.M("BV_Shopping");
        Plugin.Log.Information("[MOGTOME][Shop] Buy accepted; city={City}; currency={Currency}; targets={Targets}; marker={Marker}",
            Settings.City, CurrencyId, targets.Count, marker);
        if (poeticsMode)
            Plugin.Log.Information("[MOGTOME][Poetics] skipping unresolved vendor shops for this run: {Shops}; marker={Marker}",
                string.Join(',', sessionCatalog.Where(offer => poetics.Vendors(offer).Count == 0)
                    .Select(offer => offer.ShopId).Distinct()), marker);
        return true;
    }

    public void Update()
    {
        if (Interlocked.Exchange(ref stopRequested, 0) != 0) { Stop(); return; }
        if (DateTime.UtcNow - updated < TimeSpan.FromMilliseconds(500)) return;
        updated = DateTime.UtcNow;
        if (!running)
        {
            if (!catalogLoaded && Ready) RefreshCatalog();
            RefreshTomestoneBalances();
            return;
        }
        RefreshTomestoneBalances();
        try
        {
            if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != character || !ReferenceEquals(Settings, sessionSettings) ||
                Settings.BuyPoeticsGear != poeticsMode || poeticsMode &&
                (Settings.DeliverPoeticsGear != poeticsDeliver || Settings.PoeticsEndingLocation != poeticsEnding))
            { Fail("BV_SessionChanged"); return; }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10))
            {
                if (poeticsMode && Ready && poeticsVendor != null && !poeticsVendorRecovery && CanSkipPoeticsVendor())
                { SkipPoeticsVendor("BV_Timeout"); return; }
                Fail("BV_Timeout"); return;
            }
            if (!Ready) { if (poeticsMode) deliveryIdleSince = null; return; }
            if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InDutyQueue] || Plugin.Condition[ConditionFlag.InCombat])
            { Fail("BV_BusyClient"); return; }
            if (poeticsMode && poeticsFinishing) { FinishPoetics(); return; }
            if (pendingMove != null) { VerifyMove(); return; }
            if (pending != null) { VerifyPurchase(); return; }
            if (settling != null)
            {
                if (!closeSubmitted)
                {
                    closeSubmitted = true;
                    if (!CancelPurchase(settling)) { Fail("BV_UiMismatch"); return; }
                }
                if (PurchaseDialogVisible() || !BlundervilleGameAdapter.ShopReady(ownedAddon))
                { if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch"); return; }
                settling = null; closeSubmitted = false;
            }
            if (poeticsMode && poeticsVendorRecovery && !RecoverPoeticsVendor()) return;
            var next = NextOffer();
            if (!running) return;
            if (poeticsMode && poeticsVendorRecovery) return;
            if (next == null && poeticsMode && poeticsVendor != null && ownedPoeticsOffer == null)
            { SkipPoeticsVendor("Shop_EligibilityUnknown", rejectVendor: false); return; }
            if (next == null) { Complete(); return; }
            TomestoneMove? merge = null;
            if (!poeticsMode)
            {
                if (!MoogleShopGameAdapter.TryStacks(out var stacks)) { Fail("BV_NoInventory"); return; }
                merge = MoogleShopMath.PlanMove(stacks, next.TomestoneId);
            }
            if (merge != null)
            {
                if (!CloseTraderForMerge()) return;
                SetHold(true);
                pendingMove = merge; submitted = DateTime.UtcNow;
                Status = Ui.M("Shop_Merging");
                if (!MoogleShopGameAdapter.Move(merge)) Fail("Shop_MergeUncertain");
                return;
            }
            if (!ReachTrader(next)) return;
            if (poeticsMode && !PreparePoeticsOffer(next)) return;
            if (!poeticsMode && !MoogleShopGameAdapter.ValidateOffer(next, out purchaseRow))
            {
                BlundervilleGameAdapter.LogOfferMismatch(new(next.ShopId, next.ItemId, next.ReceiveCount, next.Costs[0].Count, next.ShopName), "ShopExchangeItem");
                Fail("BV_UiMismatch"); return;
            }
            if (!BlundervilleGameAdapter.TryInventory(next.ItemId, out beforeItem, out var capacity) || !ReadBalances(next, out beforeBalances))
            { Fail("BV_NoInventory"); return; }
            if (!plugin.XaDatabase.TryCount(next.ItemId, out var ownedCount)) { Fail("BV_NoInventory"); return; }
            if (ownedCount >= (poeticsMode ? 1 : targets[next.ItemId])) return;
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(next.ItemId);
            var shortage = next.Costs.FirstOrDefault(c => beforeBalances[c.ItemId] < c.Count);
            if (shortage != null)
            {
                Plugin.Log.Warning("[MOGTOME][Shop] insufficient currency item={Item}; currency={Currency}; required={Required}; available={Available}; costs={Costs}; balances={Balances}; marker={Marker}",
                    next.ItemId, shortage.ItemId, shortage.Count, beforeBalances[shortage.ItemId],
                    string.Join(',', next.Costs.Select(c => $"{c.ItemId}:{c.Count}")),
                    string.Join(',', beforeBalances.Select(c => $"{c.Key}:{c.Value}")), marker);
                Fail("Shop_NoFundsItem", item.Name.ToString(),
                    Plugin.DataManager.GetExcelSheet<Item>().GetRow(shortage.ItemId).Name.ToString(),
                    shortage.Count, beforeBalances[shortage.ItemId]);
                return;
            }
            if (capacity < next.ReceiveCount || (item.IsUnique && (beforeItem > 0 || next.ReceiveCount > 1))) { Fail("BV_NoCapacity"); return; }
            if (!CheckEligibility(next)) return;
            SetHold(true);
            pending = next; submitted = DateTime.UtcNow; confirmationSubmitted = false;
            proceedSubmitted = equipmentWarningSubmitted = false;
            Plugin.Log.Information("[MOGTOME][Shop] purchase submitted item={Item}; before={Before}; receive={Receive}; row={Row}; costs={Costs}; balances={Balances}; marker={Marker}",
                next.ItemId, beforeItem, next.ReceiveCount, purchaseRow, string.Join(',', next.Costs.Select(c => $"{c.ItemId}:{c.Count}")),
                string.Join(',', beforeBalances.Select(c => $"{c.Key}:{c.Value}")), marker);
            if (!(ownedAddon == "ShopExchangeCurrency"
                ? BlundervilleGameAdapter.Callback(ownedAddon, 0, purchaseRow, 1, 0)
                : BlundervilleGameAdapter.Callback(ownedAddon, 0, purchaseRow, 1))) Fail("Shop_UncertainPurchase");
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "[MOGTOME][Shop] action failed; no resubmission"); Fail("BV_ActionFailed"); }
    }

    private MoogleShopOffer? NextOffer()
    {
        if (poeticsMode)
        {
            if (!TryPoeticsWallet(out var wallet)) { Fail("BV_NoInventory"); return null; }
            var candidates = poetics.Offers;
            var stocks = plugin.XaDatabase.ReadStocks(candidates.Select(candidate => candidate.Offer.ItemId));
            var next = PoeticsShopping.Select(candidates, PoeticsShopCatalog.CurrencyItemId, wallet,
                id => plugin.XaDatabase.TryCount(id, stocks.GetValueOrDefault(id), out var count) ? count : (int?)null,
                ReadPoeticsAvailability, out var inventoryKnown, out var eligibilityKnown);
            if (!inventoryKnown) Fail("BV_NoInventory");
            else if (!eligibilityKnown) Fail("Shop_EligibilityUnknown");
            return next;
        }
        var storedStocks = plugin.XaDatabase.ReadStocks(targets.Keys);
        foreach (var target in targets.Where(t => t.Value > 0))
        {
            if (!plugin.XaDatabase.TryCount(target.Key, storedStocks.GetValueOrDefault(target.Key), out var count)) { Fail("BV_NoInventory"); return null; }
            if (count >= target.Value) continue;
            var matches = sessionCatalog.Where(o => o.ItemId == target.Key).ToArray();
            if (matches.Length != 1) { Fail("Shop_NoCatalog"); return null; }
            if (!CheckEligibility(matches[0])) return null;
            return matches[0];
        }
        return null;
    }

    private bool CheckEligibility(MoogleShopOffer offer)
    {
        var availability = poeticsMode ? ReadPoeticsAvailability(offer) : ShopOfferEligibility.Read(offer.Gate);
        if (poeticsMode && poeticsVendorRecovery) return false;
        if (availability == ShopOfferAvailability.Available) return true;
        if (poeticsMode)
        {
            // An offer's gate changing is not evidence that every shop on its NPC failed.
            Status = Ui.M(availability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
            return false;
        }
        Fail(availability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
        return false;
    }

    private bool ReadBalances(MoogleShopOffer offer, out Dictionary<uint, int> balances)
    {
        balances = [];
        foreach (var cost in offer.Costs)
        {
            int count;
            if (poeticsMode && cost.ItemId == PoeticsShopCatalog.CurrencyItemId)
            {
                if (!TryPoeticsWallet(out count)) return false;
            }
            else if (!BlundervilleGameAdapter.TryBagInventory(cost.ItemId, out count, out _)) return false;
            balances[cost.ItemId] = count;
        }
        return true;
    }

    private bool ReachTrader(MoogleShopOffer offer)
    {
        if (poeticsMode) return ReachPoeticsTrader(offer);
        var territories = new uint[] { 129, 130, 132 };
        var destinations = new[] { "Limsa Lominsa", "Ul'dah", "Gridania" };
        if (Plugin.ClientState.TerritoryType != territories[(int)sessionSettings!.City])
        {
            if (!travelSubmitted)
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) { Fail("BV_TravelUnavailable"); return false; }
                travelSubmitted = true;
                travel = Plugin.CommandManager.ProcessCommand("/li " + destinations[(int)sessionSettings.City]);
                if (!travel) Fail("BV_TravelUnavailable");
                Status = Ui.M("Shop_Travel");
            }
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(180)) Fail("BV_Timeout");
            return false;
        }
        if (travel && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return false;
        travel = false;
        Status = Ui.M("BV_Shopping");
        if (BlundervilleGameAdapter.Visible("ShopExchangeItem"))
        {
            if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
            if (shopSeen == default) shopSeen = DateTime.UtcNow;
            if (!BlundervilleGameAdapter.ShopReady("ShopExchangeItem"))
            { if (DateTime.UtcNow - shopSeen > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch"); return false; }
            return true;
        }
        if (BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency"))
        { Fail("BV_UiMismatch"); return false; }
        if (BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString"))
        {
            if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
            if (menuSeen == default) menuSeen = DateTime.UtcNow;
            if (!MoogleShopGameAdapter.ExchangeMenuReady())
            { if (DateTime.UtcNow - menuSeen > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch"); return false; }
            var priorMenu = lastMenu;
            if (!game.SelectExchange(offer, ref lastMenu)) { Fail("BV_UiMismatch"); return false; }
            if (menuSeen == default || lastMenu != priorMenu) menuSeen = DateTime.UtcNow;
            if (DateTime.UtcNow - menuSeen > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
            return false;
        }
        menuSeen = default;
        if (BlundervilleGameAdapter.Visible("Talk")) { Fail("BV_UiMismatch"); return false; }
        if (DateTime.UtcNow < nextInteraction) return false;
        var trader = game.FindTrader();
        if (trader == null) { Fail("Shop_NoTrader"); return false; }
        if (game.Approach(trader, ref movement)) { npc = trader.GameObjectId; nextInteraction = DateTime.UtcNow.AddSeconds(3); }
        return false;
    }

    private bool OwnsTrader() => npc != 0 && Plugin.TargetManager.Target?.GameObjectId == npc;
    private bool CloseTraderForMerge()
    {
        if (BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("SelectString") ||
            BlundervilleGameAdapter.Visible("SelectIconString"))
        {
            if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
            if (!closeSubmitted)
            {
                closeSubmitted = true; submitted = DateTime.UtcNow;
                if (!game.CancelExchangeMenu()) { Fail("BV_UiMismatch"); return false; }
                BlundervilleGameAdapter.CloseOwnedShop("ShopExchangeItem");
            }
            if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
            return false;
        }
        if (BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog"))
        { Fail("BV_UiMismatch"); return false; }
        closeSubmitted = false; npc = 0; lastMenu = string.Empty; shopSeen = menuSeen = default;
        return true;
    }

    private void VerifyMove()
    {
        if (!MoogleShopGameAdapter.TryStacks(out var stacks)) { Fail("Shop_MergeUncertain"); return; }
        if (MoogleShopMath.MoveVerified(pendingMove!, stacks))
        {
            Plugin.Log.Information("[MOGTOME][Shop] merge verified currency={Currency}; total={Total}; marker={Marker}", pendingMove!.Source.ItemId, pendingMove.Total, marker);
            pendingMove = null; SetHold(false); Status = Ui.M("BV_Shopping"); return;
        }
        if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("Shop_MergeUncertain");
    }

    private void VerifyPurchase()
    {
        var offer = pending!;
        if (!BlundervilleGameAdapter.TryInventory(offer.ItemId, out var count, out var capacity) || !ReadBalances(offer, out var balances))
        { Fail("Shop_UncertainPurchase"); return; }
        if (MoogleShopMath.ReceiptVerified(offer, beforeItem, beforeBalances, count, balances))
        {
            Plugin.Log.Information("[MOGTOME][Shop] purchase verified item={Item}; count={Count}; costs={Costs}; marker={Marker}",
                offer.ItemId, count, string.Join(',', balances.Select(c => $"{c.Key}:{c.Value}")), marker);
            settling = offer; pending = null; submitted = DateTime.UtcNow; closeSubmitted = false;
            if (poeticsMode) targets[offer.ItemId] = 1;
            SetHold(false); return;
        }
        if (!confirmationSubmitted && PurchaseDialogVisible())
        {
            if (!OwnsTrader() || count != beforeItem || capacity < offer.ReceiveCount ||
                !balances.OrderBy(c => c.Key).SequenceEqual(beforeBalances.OrderBy(c => c.Key))) { Fail("Shop_UncertainPurchase"); return; }
            if (poeticsMode)
            {
                ConfirmPoeticsPurchase(offer);
                if (running && DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("Shop_UncertainPurchase");
                return;
            }
            confirmationSubmitted = true;
            if (!MoogleShopGameAdapter.Confirm(offer, purchaseRow))
            {
                MoogleShopGameAdapter.InspectConfirmation(purchaseRow);
                BlundervilleGameAdapter.LogPurchaseMismatch(new(offer.ShopId, offer.ItemId, offer.ReceiveCount, offer.Costs[0].Count, offer.ShopName));
                Fail("BV_UiMismatch"); return;
            }
        }
        if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("Shop_UncertainPurchase");
    }

    private void SetHold(bool value)
    {
        if (sessionSettings == null) return;
        sessionSettings.PurchaseReviewRequired = value;
        if (ReferenceEquals(sessionSettings, Settings)) plugin.ConfigManager.SaveCurrentAccount();
    }
    public void AcknowledgeReview()
    {
        if (!Ready || running || !Settings.PurchaseReviewRequired || PurchaseDialogVisible() ||
            !MoogleShopGameAdapter.TryStacks(out _)) return;
        Settings.PurchaseReviewRequired = false; plugin.ConfigManager.SaveCurrentAccount();
        if (!plugin.Configuration.Blunderville.PurchaseReviewRequired) plugin.YesAlreadyIPC.Unpause();
        Status = Ui.M("BV_Idle");
    }
    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Stop()
    {
        Interlocked.Exchange(ref stopRequested, 0);
        Cleanup(); Status = Ui.M("BV_Stopped");
    }
    private void Cleanup()
    {
        running = false;
        if ((pending != null || settling != null) && PurchaseDialogVisible()) SetHold(true);
        foreach (var cleanup in new System.Action[]
        {
            () => { if (movement) { movement = false; plugin.VNavIPC.Stop(); } },
            () => { if (travel) { travel = false; Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); } },
            () =>
            {
                if (!OwnsTrader()) return;
                var offer = pending ?? settling ?? ownedPoeticsOffer;
                if (offer != null && !CancelPurchase(offer)) { SetHold(true); return; }
                if (!string.IsNullOrEmpty(ownedAddon)) BlundervilleGameAdapter.CloseOwnedShop(ownedAddon);
                if (!game.CancelExchangeMenu()) Plugin.Log.Warning("[MOGTOME][Shop] owned menu could not be cancelled; no further interaction");
            },
        }) try { cleanup(); } catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][Shop] cleanup failed"); }
        if (sessionSettings != null && !PurchaseDialogVisible() &&
            !plugin.Configuration.Blunderville.PurchaseReviewRequired) plugin.YesAlreadyIPC.Unpause();
        pending = settling = null; pendingMove = null; npc = 0;
        ownedPoeticsOffer = null; poeticsVendor = null; ResetPoeticsProgress();
    }
    private bool Reject(string key, params object?[] arguments) { Status = Ui.M(key, arguments); Plugin.Log.Warning("[MOGTOME][Shop] rejected: {Reason}", Status.English); return false; }
    private void Fail(string key, params object?[] arguments) { Cleanup(); Status = Ui.M(key, arguments); Plugin.Log.Warning("[MOGTOME][Shop] stopped: {Reason}; marker={Marker}", Status.English, marker); }
    private void Complete()
    {
        if (poeticsMode) { FinishPoetics(); return; }
        CompleteNow();
    }
    private void CompleteNow()
    {
        Cleanup(); Status = Ui.M("BV_ShopDone"); Plugin.Log.Information("[MOGTOME][Shop] completed; marker={Marker}", marker);
    }
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}
