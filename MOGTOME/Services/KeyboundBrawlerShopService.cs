using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;
using Action = System.Action;

namespace MOGTOME.Services;

/// <summary>Independent MGC purchases; no entry, solver or round callbacks.</summary>
public sealed class KeyboundBrawlerShopService : IDisposable
{
    private const uint TraderId = 1058753;
    private readonly Plugin plugin;
    private readonly BlundervilleGameAdapter interactions;
    private KeyboundShopSession? session;
    private KeyboundShopPurchase? pending;
    private BlundervilleOffer? settling;
    private BlundervilleOffer? ownedOffer;
    private IReadOnlyList<BlundervilleOffer> catalog = [];
    private bool disposed, running, movement, travel, innStarted, confirmationCleanup;
    private int stopRequested, purchaseRow;
    private ulong npc;
    private string ownedAddon = string.Empty;
    private uint selectedCategoryItem;
    private DateTime started, updated, submitted, nextInteraction, shopSeen, categoryRequested;

    public KeyboundBrawlerShopService(Plugin plugin)
    {
        this.plugin = plugin;
        interactions = new(plugin);
    }

    private KeyboundBrawlerSettings Settings => plugin.Configuration.KeyboundBrawler;
    public bool IsRunning => running;
    public bool Ready => !disposed && plugin.CanSelectUiLanguage && plugin.Engine != null &&
        Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.ContentId != 0 && Plugin.ObjectTable.LocalPlayer != null &&
        !Plugin.Condition[ConditionFlag.LoggingOut] && !Plugin.Condition[ConditionFlag.BetweenAreas] &&
        !Plugin.Condition[ConditionFlag.BetweenAreas51];
    public UiText Status { get; private set; } = Ui.M("BV_Idle");
    public bool HoldAutomaticDialogs => running || pending is { ReceiptAcknowledged: false } ||
        session?.Settings.PurchaseReviewRequired == true;

    public void RefreshCatalog()
    {
        if (!Ready || running) return;
        KeyboundBrawlerShop.RefreshCatalog();
        catalog = KeyboundBrawlerShop.Catalog;
        ShopOfferEligibility.RefreshAchievements(catalog.Select(offer => offer.Gate));
        Status = Ui.M(catalog.Count == 0 ? "KB_NoCatalog" : "BV_Idle");
    }

    public bool Buy()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Volatile.Read(ref stopRequested) != 0 || !Ready || running || plugin.IsEngineStartQueued ||
            plugin.Engine.IsRunning || plugin.Engine.IsStartupPending || plugin.MoogleShop.IsRunning ||
            plugin.IsMoogleShopActionQueued || plugin.Blunderville.IsRunning || plugin.IsBlundervilleActionQueued)
            return Reject("BV_Unavailable");
        if (Settings.PurchaseReviewRequired || session?.Settings.PurchaseReviewRequired == true ||
            plugin.Configuration.Blunderville.PurchaseReviewRequired || plugin.Configuration.MoogleShop.PurchaseReviewRequired)
            return Reject("Shop_ReviewRequired");
        var missingPlugins = GameHelpers.MissingXaDependencies();
        if (missingPlugins.Length > 0) return Reject("Plugin_RequiredXaPlugins", missingPlugins);
        if (BusyClient() || DialogVisible() || ShopVisible() || BlundervilleGameAdapter.Visible("Talk") ||
            BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString"))
            return Reject("BV_BusyClient");
        if (!Enum.IsDefined(Settings.EndingLocation)) return Reject("BV_TravelUnavailable");
        var account = plugin.ConfigManager.CurrentAccountId;
        if (string.IsNullOrEmpty(account) || account is "temporary" or "default") return Reject("KB_ProfileUnavailable");
        try
        {
            RefreshCatalog();
            if (catalog.Count == 0) return Reject("KB_NoCatalog");
            var candidate = new KeyboundShopSession(Settings, account, Plugin.PlayerState.ContentId);
            if (!TryNextOffer(candidate, out var next)) return false;
            if (next != null)
            {
                if (!KeyboundBrawlerShop.TryWallet(out var wallet)) return Reject("KB_NoWallet");
                if (wallet < next.Price) return Reject("KB_NoFunds");
                if (!BlundervilleGameAdapter.TryInventory(next.ItemId, out var count, out var capacity)) return Reject("BV_NoInventory");
                var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(next.ItemId);
                if (capacity < next.ReceiveCount || item.IsUnique && (count > 0 || next.ReceiveCount > 1)) return Reject("BV_NoCapacity");
                if (FindTrader() == null) return Reject("KB_TraderUnavailable");
            }
            session = candidate;
            pending = null; settling = null;
            npc = 0; ownedAddon = string.Empty; ownedOffer = null;
            movement = travel = innStarted = confirmationCleanup = false;
            selectedCategoryItem = 0;
            shopSeen = categoryRequested = nextInteraction = default;
            started = DateTime.UtcNow;
            plugin.YesAlreadyIPC.Pause();
            if (!plugin.YesAlreadyIPC.IsPaused) return Reject("BV_DialogControl");
            running = true;
            Status = Ui.M("BV_Shopping");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MOGTOME][KeyboundShop] Buy rejected");
            return Reject("BV_ActionFailed");
        }
    }

    public void Update()
    {
        if (Interlocked.Exchange(ref stopRequested, 0) != 0) { Stop(); return; }
        if (!running)
        {
            if (HoldAutomaticDialogs || plugin.CanSelectUiLanguage && Settings.PurchaseReviewRequired) plugin.YesAlreadyIPC.Pause();
            return;
        }
        if (!SessionMatches()) { Fail("BV_SessionChanged"); return; }
        if (DateTime.UtcNow - updated < TimeSpan.FromMilliseconds(500)) return;
        updated = DateTime.UtcNow;
        try
        {
            if (!Ready) return;
            if (BusyClient() || plugin.Engine.IsRunning || plugin.Engine.IsStartupPending ||
                plugin.MoogleShop.IsRunning || plugin.Blunderville.IsRunning)
            { Fail("BV_BusyClient"); return; }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) { Fail("BV_Timeout"); return; }
            if (innStarted) { UpdateEnding(); return; }
            if (!KeyboundBrawlerShop.TryWallet(out var wallet)) { Fail("KB_NoWallet"); return; }
            if (pending != null) { VerifyPurchase(wallet); return; }
            if (settling != null)
            {
                if (!OwnsTrader()) { Fail("BV_UiMismatch"); return; }
                if (!confirmationCleanup && DialogVisible())
                {
                    confirmationCleanup = true;
                    if (!BlundervilleGameAdapter.CancelOwnedPurchase(settling)) { Fail("BV_UiMismatch"); return; }
                }
                if (DialogVisible() || !BlundervilleGameAdapter.ShopReady(ownedAddon))
                { if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch"); return; }
                settling = null;
            }
            if (!TryNextOffer(session!, out var next)) return;
            if (next == null)
            {
                CloseShop();
                if (session!.EndingLocation == BlundervilleEndingLocation.Stay) Complete();
                else { innStarted = true; started = DateTime.UtcNow; Status = Ui.M("BV_EndingTravel"); UpdateEnding(); }
                return;
            }
            if (!ReachTrader(next)) return;
            if (!BlundervilleGameAdapter.TryValidateOffer(next, out var addon, out purchaseRow, KeyboundBrawlerShop.CurrencyItemId))
            { Fail("BV_UiMismatch"); return; }
            if (!BlundervilleGameAdapter.TryInventory(next.ItemId, out var before, out var capacity) ||
                !plugin.XaDatabase.TryCount(next.ItemId, out var total)) { Fail("BV_NoInventory"); return; }
            if (BlundervilleProgress.Deficit(session!.Targets[next.ItemId], total) < next.ReceiveCount) return;
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(next.ItemId);
            if (capacity < next.ReceiveCount || item.IsUnique && (before > 0 || next.ReceiveCount > 1)) { Fail("BV_NoCapacity"); return; }
            if (!KeyboundBrawlerShop.TryWallet(out wallet)) { Fail("KB_NoWallet"); return; }
            if (wallet < next.Price) { Fail("KB_NoFunds"); return; }
            if (!CheckEligibility(next)) return;
            ownedAddon = addon;
            ownedOffer = next;
            pending = new(session.Settings, next, before, wallet);
            submitted = DateTime.UtcNow;
            if (!pending.Submit(SaveSession, () => addon == "ShopExchangeCurrency"
                ? BlundervilleGameAdapter.Callback(addon, 0, purchaseRow, 1, 0)
                : BlundervilleGameAdapter.Callback(addon, 0, purchaseRow, 1)))
                Fail("Shop_UncertainPurchase");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MOGTOME][KeyboundShop] action failed; no automatic resubmission");
            Fail(pending != null ? "Shop_UncertainPurchase" : "BV_ActionFailed");
        }
    }

    private bool TryNextOffer(KeyboundShopSession candidate, out BlundervilleOffer? next)
    {
        next = null;
        var stocks = plugin.XaDatabase.ReadStocks(candidate.Targets.Keys);
        foreach (var target in candidate.Targets.Where(target => target.Value > 0))
        {
            if (!plugin.XaDatabase.TryCount(target.Key, stocks.GetValueOrDefault(target.Key), out var count))
            { Fail("BV_NoInventory"); return false; }
            if (BlundervilleProgress.Deficit(target.Value, count) == 0) continue;
            var offers = catalog.Where(offer => offer.ItemId == target.Key && offer.ShopId == KeyboundBrawlerShop.ShopId).ToArray();
            if (offers.Length != 1) { Fail("KB_NoCatalog"); return false; }
            next = offers[0];
            if (next.ReceiveCount == 0 || BlundervilleProgress.Deficit(target.Value, count) % next.ReceiveCount != 0)
            { Fail("BV_QuantityMismatch"); return false; }
            return CheckEligibility(next);
        }
        return true;
    }

    private bool CheckEligibility(BlundervilleOffer offer)
    {
        var availability = ShopOfferEligibility.Read(offer.Gate);
        if (availability == ShopOfferAvailability.Available) return true;
        Fail(availability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
        return false;
    }

    private static IGameObject? FindTrader()
    {
        if (!Plugin.DataManager.GetExcelSheet<ENpcBase>().TryGetRow(TraderId, out var row) ||
            !row.ENpcData.Any(handler => handler.Is<SpecialShop>() && handler.RowId == KeyboundBrawlerShop.ShopId)) return null;
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? null : Plugin.ObjectTable
            .Where(value => value.ObjectKind == ObjectKind.EventNpc && value.BaseId == TraderId)
            .OrderBy(value => System.Numerics.Vector3.Distance(player.Position, value.Position)).FirstOrDefault();
    }

    private bool ReachTrader(BlundervilleOffer offer)
    {
        if (DialogVisible() || BlundervilleGameAdapter.Visible("SelectString") ||
            BlundervilleGameAdapter.Visible("SelectIconString") || BlundervilleGameAdapter.Visible("Talk"))
        { Fail("KB_TraderUnavailable"); return false; }
        if (ShopVisible())
        {
            if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
            var addon = BlundervilleGameAdapter.Visible("ShopExchangeCurrency") ? "ShopExchangeCurrency" : "ShopExchangeItem";
            if (shopSeen == default) shopSeen = DateTime.UtcNow;
            if (!BlundervilleGameAdapter.ShopReady(addon))
            { if (DateTime.UtcNow - shopSeen > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch"); return false; }
            if (addon == "ShopExchangeCurrency")
            {
                if (!BlundervilleGameAdapter.EnsureOfferCategory(offer, ref selectedCategoryItem, out var ready))
                { Fail("BV_UiMismatch"); return false; }
                if (!ready)
                {
                    if (categoryRequested == default) categoryRequested = DateTime.UtcNow;
                    if (DateTime.UtcNow - categoryRequested > TimeSpan.FromSeconds(10)) Fail("BV_UiMismatch");
                    return false;
                }
                categoryRequested = default;
            }
            return true;
        }
        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(120)) { Fail("KB_TraderUnavailable"); return false; }
        if (DateTime.UtcNow < nextInteraction) return false;
        var trader = FindTrader();
        if (trader == null) { Fail("KB_TraderUnavailable"); return false; }
        if (interactions.ApproachAndInteract(trader, ref movement))
        {
            npc = trader.GameObjectId;
            nextInteraction = DateTime.UtcNow.AddSeconds(3);
        }
        return false;
    }

    private void VerifyPurchase(uint wallet)
    {
        var transaction = pending!;
        if (!BlundervilleGameAdapter.TryInventory(transaction.Offer.ItemId, out var count, out var capacity))
        { Fail("Shop_UncertainPurchase"); return; }
        if (transaction.Verify(count, wallet, SaveSession))
        {
            settling = transaction.Offer;
            pending = null;
            submitted = DateTime.UtcNow;
            confirmationCleanup = false;
            return;
        }
        if (DialogVisible() && !transaction.ConfirmationSubmitted)
        {
            if (!OwnsTrader() || count != transaction.BeforeCount || wallet != transaction.BeforeWallet ||
                capacity < transaction.Offer.ReceiveCount || !CheckEligibility(transaction.Offer))
            { Fail("Shop_UncertainPurchase"); return; }
            var accepted = transaction.Confirm(() => ownedAddon == "ShopExchangeItem"
                ? MoogleShopGameAdapter.Confirm(new(transaction.Offer.ShopId, transaction.Offer.ShopName,
                    transaction.Offer.ItemId, transaction.Offer.ReceiveCount, KeyboundBrawlerShop.CurrencyItemId,
                    [new(KeyboundBrawlerShop.CurrencyItemId, transaction.Offer.Price)]), purchaseRow)
                : BlundervilleGameAdapter.ConfirmPurchase(transaction.Offer, ownedAddon, KeyboundBrawlerShop.CurrencyItemId));
            if (!accepted) { Fail("Shop_UncertainPurchase"); return; }
        }
        if (DateTime.UtcNow - submitted > TimeSpan.FromSeconds(10)) Fail("Shop_UncertainPurchase");
    }

    private void UpdateEnding()
    {
        var rooms = new uint[] { 0, 178, 179, 177, 429, 843, 990, 1205 };
        var commands = new[] { 0, 2, 3, 1, 4, 6, 7, 8 };
        var selected = (int)session!.EndingLocation;
        if (selected < 1 || selected >= rooms.Length) { Fail("BV_TravelUnavailable"); return; }
        var busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        if (Plugin.ClientState.TerritoryType == rooms[selected] && !busy) { travel = false; Complete(); return; }
        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(180)) { Fail("BV_Timeout"); return; }
        if (travel) return;
        if (busy) { Fail("BV_TravelUnavailable"); return; }
        // One owned route; do not retry an idle route while its territory transition is pending.
        travel = true;
        if (!Plugin.CommandManager.ProcessCommand("/li inn " + commands[selected])) { travel = false; Fail("BV_TravelUnavailable"); }
    }

    public void AcknowledgePurchaseReview()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || !Ready || running || plugin.IsKeyboundShopActionQueued || !Settings.PurchaseReviewRequired ||
            DialogVisible() || ShopVisible() || !KeyboundBrawlerShop.TryWallet(out _)) return;
        foreach (var target in Settings.PurchaseTargets)
            if (!BlundervilleGameAdapter.TryInventory(target.Key, out _, out _)) return;
        Settings.PurchaseReviewRequired = false;
        try { plugin.ConfigManager.SaveCurrentAccount(); }
        catch { Settings.PurchaseReviewRequired = true; throw; }
        if (session != null && ReferenceEquals(session.Settings, Settings)) session = null;
        if (!plugin.Configuration.Blunderville.PurchaseReviewRequired && !plugin.Configuration.MoogleShop.PurchaseReviewRequired &&
            !plugin.Blunderville.IsRunning && !plugin.MoogleShop.IsRunning && plugin.Engine?.IsRunning != true)
            plugin.YesAlreadyIPC.Unpause();
        Status = Ui.M("BV_Idle");
    }

    private bool SessionMatches() => session != null && Plugin.ClientState.IsLoggedIn &&
        session.Matches(Settings, plugin.ConfigManager.CurrentAccountId, Plugin.PlayerState.ContentId);
    private bool OwnsTrader() => SessionMatches() && npc != 0 && Plugin.TargetManager.Target?.GameObjectId == npc;
    private static bool BusyClient() => Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InDutyQueue] ||
        Plugin.Condition[ConditionFlag.InCombat] || BlundervilleGameAdapter.Visible("ContentsFinderConfirm");
    private static bool DialogVisible() => BlundervilleGameAdapter.Visible("SelectYesno") ||
        BlundervilleGameAdapter.Visible("ShopExchangeCurrencyDialog") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog");
    private static bool ShopVisible() => BlundervilleGameAdapter.Visible("ShopExchangeCurrency") || BlundervilleGameAdapter.Visible("ShopExchangeItem");
    private void SaveSession()
    {
        if (!SessionMatches()) throw new InvalidOperationException("Purchase session changed before persistence");
        plugin.ConfigManager.SaveCurrentAccount();
    }
    private void CloseShop()
    {
        if (!OwnsTrader() || string.IsNullOrEmpty(ownedAddon)) return;
        var offer = pending?.Offer ?? settling;
        if (ownedOffer == null || !BlundervilleGameAdapter.TryValidateOffer(ownedOffer, out var current, out _, KeyboundBrawlerShop.CurrencyItemId) ||
            current != ownedAddon)
        {
            if (ShopVisible() || DialogVisible()) throw new InvalidOperationException("Owned shop identity changed before cleanup");
            ownedAddon = string.Empty;
            return;
        }
        if (offer != null && !BlundervilleGameAdapter.CancelOwnedPurchase(offer))
            throw new InvalidOperationException("Owned purchase could not be cancelled");
        BlundervilleGameAdapter.CloseOwnedShop(ownedAddon);
        ownedAddon = string.Empty;
        ownedOffer = null;
    }

    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Stop()
    {
        Interlocked.Exchange(ref stopRequested, 0);
        Cleanup();
        Status = Ui.M("BV_Stopped");
    }
    private void Cleanup()
    {
        running = false;
        if (session != null && (pending is { ReceiptAcknowledged: false } || settling != null && DialogVisible()))
        {
            session.Settings.PurchaseReviewRequired = true;
            if (SessionMatches())
                try { SaveSession(); } catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][KeyboundShop] purchase hold persistence failed"); }
        }
        foreach (var cleanup in new Action[]
        {
            () => { if (movement) { movement = false; plugin.VNavIPC.Stop(); } },
            () => { if (travel) { travel = false; Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); } },
            CloseShop,
        }) try { cleanup(); } catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][KeyboundShop] owned cleanup failed"); }
        pending = null; settling = null; ownedOffer = null; npc = 0; ownedAddon = string.Empty;
        movement = travel = innStarted = false;
        if (session?.Settings.PurchaseReviewRequired != true) session = null;
        if (!HoldAutomaticDialogs && !plugin.Configuration.Blunderville.PurchaseReviewRequired &&
            !plugin.Configuration.MoogleShop.PurchaseReviewRequired && !plugin.Blunderville.IsRunning &&
            !plugin.MoogleShop.IsRunning && plugin.Engine?.IsRunning != true) plugin.YesAlreadyIPC.Unpause();
    }
    private bool Reject(string key, params object?[] arguments) { Status = Ui.M(key, arguments); return false; }
    private void Fail(string key) { Cleanup(); Status = Ui.M(key); Plugin.Log.Warning("[MOGTOME][KeyboundShop] stopped: {Reason}", Status.English); }
    private void Complete() { Cleanup(); Status = Ui.M("BV_ShopDone"); }
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}

internal sealed class KeyboundShopSession
{
    internal KeyboundShopSession(KeyboundBrawlerSettings settings, string accountId, ulong characterId)
    {
        Settings = settings; AccountId = accountId; CharacterId = characterId;
        Targets = new(settings.PurchaseTargets);
        EndingLocation = settings.EndingLocation;
    }
    internal KeyboundBrawlerSettings Settings { get; }
    internal string AccountId { get; }
    internal ulong CharacterId { get; }
    internal Dictionary<uint, int> Targets { get; }
    internal BlundervilleEndingLocation EndingLocation { get; }
    internal bool Matches(KeyboundBrawlerSettings settings, string account, ulong character)
        => ReferenceEquals(settings, Settings) && account == AccountId && character == CharacterId;
}

internal sealed class KeyboundShopPurchase
{
    private readonly KeyboundBrawlerSettings settings;
    internal KeyboundShopPurchase(KeyboundBrawlerSettings settings, BlundervilleOffer offer, int beforeCount, uint beforeWallet)
    { this.settings = settings; Offer = offer; BeforeCount = beforeCount; BeforeWallet = beforeWallet; }
    internal BlundervilleOffer Offer { get; }
    internal int BeforeCount { get; }
    internal uint BeforeWallet { get; }
    internal bool Submitted { get; private set; }
    internal bool ConfirmationSubmitted { get; private set; }
    internal bool ReceiptAcknowledged { get; private set; }
    internal bool Submit(Action save, Func<bool> dispatch)
    {
        if (Submitted) return false;
        settings.PurchaseReviewRequired = true;
        save();
        Submitted = true;
        return dispatch();
    }
    internal bool Confirm(Func<bool> dispatch)
    {
        if (!Submitted || ConfirmationSubmitted || ReceiptAcknowledged) return false;
        ConfirmationSubmitted = true;
        return dispatch();
    }
    internal bool Verify(int count, uint wallet, Action save)
    {
        if (!Submitted || ReceiptAcknowledged || !BlundervilleProgress.PurchaseVerified(BeforeCount, BeforeWallet,
            Offer.ReceiveCount, Offer.Price, count, wallet)) return false;
        settings.PurchaseReviewRequired = false;
        save();
        ReceiptAcknowledged = true;
        return true;
    }
}
