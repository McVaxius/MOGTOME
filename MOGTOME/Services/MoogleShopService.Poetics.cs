using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed partial class MoogleShopService
{
    private bool poeticsFinishing, poeticsShopClosed, deliveryAdmitted, positionApproach;
    private int poeticsCloseStage; // Consumed close commands: shop=1, menu=2, Talk=4.
    private DateTime? deliveryIdleSince;
    private readonly HashSet<uint> rejectedPoeticsVendors = [];
    private bool poeticsVendorRecovery;

    private static unsafe bool TryPoeticsWallet(out int balance)
    {
        balance = 0;
        var inventory = InventoryManager.Instance();
        if (inventory == null || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded) return false;
        var value = inventory->GetTomestoneCount(PoeticsShopCatalog.CurrencyItemId);
        if (value > int.MaxValue) return false;
        balance = (int)value;
        return true;
    }

    private static unsafe bool TryPoeticsDestination(PoeticsShopVendor vendor, out string destination)
    {
        destination = string.Empty;
        var state = UIState.Instance();
        if (state == null || !Plugin.PlayerState.IsLoaded) throw new InvalidOperationException("Teleport unlock state unavailable");
        foreach (var aetheryte in Plugin.DataManager.GetExcelSheet<Aetheryte>().OrderBy(row => row.RowId))
            if (aetheryte.IsAetheryte && aetheryte.Territory.RowId == vendor.TerritoryId &&
                state->IsAetheryteUnlocked(aetheryte.RowId))
            {
                destination = aetheryte.PlaceName.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(destination)) return true;
            }
        return false;
    }

    private static BlundervilleOffer ExchangeOffer(MoogleShopOffer offer)
        => new(offer.ShopId, offer.ItemId, offer.ReceiveCount, offer.Costs[0].Count, offer.ShopName);

    private static bool CanReachPoeticsVendor(PoeticsShopVendor vendor)
        => vendor.TerritoryId == Plugin.ClientState.TerritoryType || TryPoeticsDestination(vendor, out _);

    private ShopOfferAvailability ReadPoeticsAvailability(MoogleShopOffer offer)
    {
        var availability = poetics.ReadAvailability(offer, ReadPoeticsVendorAvailability);
        if (poeticsVendor != null && rejectedPoeticsVendors.Contains(poeticsVendor.NpcId) && !poeticsVendorRecovery)
            SkipPoeticsVendor("Shop_EligibilityUnknown", rejectVendor: false);
        return availability;
    }

    private ShopOfferAvailability ReadPoeticsVendorAvailability(PoeticsShopVendor vendor)
    {
        if (rejectedPoeticsVendors.Contains(vendor.NpcId)) return ShopOfferAvailability.Unknown;
        try
        {
            var availability = PoeticsShopCatalog.ReadVendorAvailability(vendor);
            if (availability == ShopOfferAvailability.Unknown) RejectPoeticsVendor(vendor.NpcId, "Shop_EligibilityUnknown");
            if (availability != ShopOfferAvailability.Available) return availability;
            return CanReachPoeticsVendor(vendor) ? availability : ShopOfferAvailability.Locked;
        }
        catch (Exception ex)
        {
            RejectPoeticsVendor(vendor.NpcId, "BV_TravelUnavailable", ex);
            return ShopOfferAvailability.Unknown;
        }
    }

    private void RejectPoeticsVendor(uint npcId, string reason, Exception? error = null)
    {
        if (!rejectedPoeticsVendors.Add(npcId)) return;
        const string message = "[MOGTOME][Poetics] skipping vendor {Npc} for this run; reason={Reason}; marker={Marker}";
        if (error == null) Plugin.Log.Warning(message, npcId, reason, marker);
        else Plugin.Log.Warning(error, message, npcId, reason, marker);
    }

    private bool CanSkipPoeticsVendor()
        => PoeticsShopping.CanSkipVendor(pending != null || settling != null || pendingMove != null,
            sessionSettings?.PurchaseReviewRequired == true || plugin.Configuration.Blunderville.PurchaseReviewRequired,
            PurchaseDialogVisible(), poeticsFinishing);

    private bool SkipPoeticsVendor(string reason, bool rejectVendor = true)
    {
        if (!CanSkipPoeticsVendor()) { Fail("Shop_UncertainPurchase"); return false; }
        if (poeticsVendor != null && rejectVendor) RejectPoeticsVendor(poeticsVendor.NpcId, reason);
        if (!poeticsVendorRecovery)
        {
            poeticsVendorRecovery = true;
            travel = false; // Lifestream has no request-owner token; do not abort a possible foreign replacement.
            started = DateTime.UtcNow; // Reuse the owner's existing deadline for bounded cleanup/next vendor.
        }
        Status = Ui.M("BV_Shopping");
        return false;
    }

    private unsafe bool RecoverPoeticsVendor()
    {
        if (!ContinuePoetics()) return false;
        if (!CanSkipPoeticsVendor()) { Fail("Shop_UncertainPurchase"); return false; }
        if (movement)
        {
            if (!plugin.VNavIPC.Stop()) { Fail("BV_TravelUnavailable"); return false; }
            movement = false;
        }
        // Observe travel idle without issuing Lifestream's global Abort.
        if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return false;
        var currencyShop = BlundervilleGameAdapter.Visible("ShopExchangeCurrency");
        var itemShop = BlundervilleGameAdapter.Visible("ShopExchangeItem");
        var menu = BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString");
        var talk = BlundervilleGameAdapter.Visible("Talk");
        if ((currencyShop || itemShop || menu || talk) && !OwnsTrader())
        { Fail("BV_UiMismatch"); return false; }
        if (talk && (currencyShop || itemShop || menu)) { Fail("BV_UiMismatch"); return false; }
        if (currencyShop || itemShop)
        {
            var addon = currencyShop ? "ShopExchangeCurrency" : "ShopExchangeItem";
            if (currencyShop && itemShop || addon != ownedAddon)
            { Fail("BV_UiMismatch"); return false; }
            if ((poeticsCloseStage & 1) == 0)
            {
                BlundervilleGameAdapter.CloseOwnedShop(addon);
                poeticsCloseStage |= 1;
            }
            return false;
        }
        if (menu)
        {
            if ((poeticsCloseStage & 2) == 0)
            {
                if (!game.CancelExchangeMenu()) { Fail("BV_UiMismatch"); return false; }
                poeticsCloseStage |= 2;
            }
            return false;
        }
        if (talk)
        {
            if (ownedAddon != "Talk") { Fail("BV_UiMismatch"); return false; }
            if ((poeticsCloseStage & 4) == 0)
            {
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("Talk").Address;
                if (addon != null && addon->IsVisible) addon->Close(true);
                poeticsCloseStage |= 4;
            }
            return false;
        }
        // Observe all owned UI closed before another vendor (or delivery) can start.
        poeticsVendor = null; ownedPoeticsOffer = null; ownedAddon = string.Empty;
        npc = 0; lastMenu = string.Empty; categoryItem = 0;
        shopSeen = menuSeen = default; nextInteraction = default;
        positionApproach = travelSubmitted = closeSubmitted = false;
        poeticsCloseStage = 0; poeticsVendorRecovery = false;
        started = DateTime.UtcNow;
        return true;
    }

    private bool PurchaseDialogVisible()
        => BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog") ||
            poeticsMode && BlundervilleGameAdapter.Visible("ShopExchangeCurrencyDialog");

    private bool CancelPurchase(MoogleShopOffer offer)
        => poeticsMode
            ? BlundervilleGameAdapter.CancelOwnedPurchase(ExchangeOffer(offer), PoeticsShopCatalog.CurrencyItemId)
            : MoogleShopGameAdapter.CancelPurchase(offer);

    private bool PreparePoeticsOffer(MoogleShopOffer offer)
    {
        try
        {
            var exchange = ExchangeOffer(offer);
            if (BlundervilleGameAdapter.Visible("ShopExchangeCurrency"))
            {
                if (!BlundervilleGameAdapter.EnsureOfferCategory(exchange, ref categoryItem, out var ready))
                { return SkipPoeticsVendor("BV_UiMismatch"); }
                if (!ready) return false;
            }
            if (!BlundervilleGameAdapter.TryValidateOffer(exchange, out var addon, out purchaseRow, PoeticsShopCatalog.CurrencyItemId))
            { return SkipPoeticsVendor("BV_UiMismatch"); }
            ownedAddon = addon;
            ownedPoeticsOffer = offer;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MOGTOME][Poetics] vendor offer validation failed; no purchase submitted");
            return SkipPoeticsVendor("BV_UiMismatch");
        }
    }

    private unsafe void ConfirmPoeticsPurchase(MoogleShopOffer offer)
    {
        if (ownedAddon == "ShopExchangeItem" && BlundervilleGameAdapter.Visible("ShopExchangeItemDialog"))
        {
            confirmationSubmitted = true;
            if (!MoogleShopGameAdapter.Confirm(offer, purchaseRow)) Fail("Shop_UncertainPurchase");
            return;
        }
        var exchange = ExchangeOffer(offer);
        if (!BlundervilleGameAdapter.PreparePurchaseConfirmation(exchange, ownedAddon, PoeticsShopCatalog.CurrencyItemId,
            out var ready, out var dialog, out var proceed, out var equipmentWarning))
        { Fail("Shop_UncertainPurchase"); return; }
        if (proceed != null && !proceed->IsChecked)
        {
            if (!proceedSubmitted)
            {
                proceedSubmitted = true;
                if (!BlundervilleGameAdapter.CheckPurchaseProceed(dialog, proceed)) Fail("Shop_UncertainPurchase");
            }
            return;
        }
        if (!ready) return;
        if (equipmentWarning)
        {
            if (!equipmentWarningSubmitted)
            {
                equipmentWarningSubmitted = true;
                if (!BlundervilleGameAdapter.ConfirmPurchase(exchange, ownedAddon, PoeticsShopCatalog.CurrencyItemId, equipmentWarning: true))
                    Fail("Shop_UncertainPurchase");
            }
            return;
        }
        confirmationSubmitted = true;
        if (!(ownedAddon == "ShopExchangeItem" ? MoogleShopGameAdapter.Confirm(offer, purchaseRow)
            : BlundervilleGameAdapter.ConfirmPurchase(exchange, ownedAddon, PoeticsShopCatalog.CurrencyItemId)))
            Fail("Shop_UncertainPurchase");
    }

    private bool ReachPoeticsTrader(MoogleShopOffer offer)
    {
        try
        {
            var shopVisible = BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency");
            if ((shopVisible || PurchaseDialogVisible() || BlundervilleGameAdapter.Visible("SelectString") ||
                BlundervilleGameAdapter.Visible("SelectIconString") || BlundervilleGameAdapter.Visible("Talk")) && !OwnsTrader())
            { Fail("BV_UiMismatch"); return false; }
            if (ownedPoeticsOffer != null && ownedPoeticsOffer.ShopId != offer.ShopId)
            {
                if (!ClosePoeticsTrader()) return false;
                poeticsVendor = null;
            }
            if (poeticsVendor != null)
            {
                var currentVendor = poeticsVendor.MatchCurrent(poetics.Vendors(offer)
                    .Where(vendor => !rejectedPoeticsVendors.Contains(vendor.NpcId)));
                if (currentVendor == null) return SkipPoeticsVendor("Shop_EligibilityUnknown", rejectVendor: false);
                // Keep the current offer's labels even when a catalog refresh replaced its records.
                poeticsVendor = currentVendor;
            }
            if (poeticsVendor == null)
            {
                poeticsVendor = PoeticsShopVendor.SelectAvailable(poetics.Vendors(offer), Plugin.ClientState.TerritoryType,
                    ReadPoeticsVendorAvailability);
                if (poeticsVendor == null) return SkipPoeticsVendor("Shop_NoTrader");
                travelSubmitted = false;
            }
            var vendorAvailability = ReadPoeticsVendorAvailability(poeticsVendor);
            if (vendorAvailability != ShopOfferAvailability.Available)
                return SkipPoeticsVendor(vendorAvailability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown");
            if (Plugin.ClientState.TerritoryType != poeticsVendor.TerritoryId)
            {
                if (!travelSubmitted)
                {
                    if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc() ||
                        !TryPoeticsDestination(poeticsVendor, out var destination))
                    { return SkipPoeticsVendor("BV_TravelUnavailable"); }
                    travelSubmitted = true;
                    travel = Plugin.CommandManager.ProcessCommand("/li " + destination);
                    if (!travel) return SkipPoeticsVendor("BV_TravelUnavailable");
                }
                Status = Ui.M("Shop_Travel");
                return false;
            }
            if (travel && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return false;
            travel = false;
            Status = Ui.M("BV_Shopping");
            if (shopVisible)
            {
                if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
                var currencyShop = BlundervilleGameAdapter.Visible("ShopExchangeCurrency");
                if (currencyShop && BlundervilleGameAdapter.Visible("ShopExchangeItem"))
                { Fail("BV_UiMismatch"); return false; }
                ownedAddon = currencyShop ? "ShopExchangeCurrency" : "ShopExchangeItem";
                return BlundervilleGameAdapter.ShopReady(ownedAddon);
            }
            if (PurchaseDialogVisible()) { Fail("Shop_UncertainPurchase"); return false; }
            if (BlundervilleGameAdapter.Visible("Talk"))
            {
                if (BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString"))
                { Fail("BV_UiMismatch"); return false; }
                ownedAddon = "Talk";
                return SkipPoeticsVendor("BV_UiMismatch");
            }
            if (BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString"))
            {
                if (!OwnsTrader()) { Fail("BV_UiMismatch"); return false; }
                if (!MoogleShopGameAdapter.TryReadExchangeMenu(out var addon, out var names))
                    return SkipPoeticsVendor("BV_UiMismatch");
                var menu = addon + "\n" + string.Join('\n', names);
                if (menu == lastMenu) return false;
                var row = MoogleShopGameAdapter.FindExchangeRow(names, offer.ShopName, poeticsVendor.MenuLabels);
                if (row < 0) return SkipPoeticsVendor("BV_UiMismatch");
                lastMenu = menu;
                if (!BlundervilleGameAdapter.Callback(addon, row)) return SkipPoeticsVendor("BV_UiMismatch");
                return false;
            }
            if (DateTime.UtcNow < nextInteraction) return false;
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;
            var trader = Plugin.ObjectTable.FirstOrDefault(actor => actor.ObjectKind == ObjectKind.EventNpc &&
                actor.BaseId == poeticsVendor.NpcId);
            if (trader == null)
            {
                if (Vector3.Distance(player.Position, poeticsVendor.Position) <= 3)
                { return SkipPoeticsVendor("Shop_NoTrader"); }
                if (!movement)
                {
                    if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
                        return SkipPoeticsVendor("BV_TravelUnavailable");
                    movement = plugin.VNavIPC.MoveTo(poeticsVendor.Position);
                    if (!movement) return SkipPoeticsVendor("BV_TravelUnavailable");
                    positionApproach = true;
                }
                return false;
            }
            if (positionApproach)
            {
                if (movement) plugin.VNavIPC.Stop();
                positionApproach = movement = false;
            }
            if (game.Approach(trader, ref movement))
            {
                npc = trader.GameObjectId;
                nextInteraction = DateTime.UtcNow.AddSeconds(3);
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MOGTOME][Poetics] vendor travel or interaction failed; no purchase submitted");
            return SkipPoeticsVendor("BV_TravelUnavailable");
        }
    }

    private bool ClosePoeticsTrader()
    {
        var shopVisible = BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency");
        var menuVisible = BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString");
        var talkVisible = BlundervilleGameAdapter.Visible("Talk");
        if ((shopVisible || menuVisible || talkVisible || PurchaseDialogVisible()) && !OwnsTrader())
        { Fail("BV_UiMismatch"); return false; }
        if (talkVisible)
        {
            if (shopVisible || menuVisible || PurchaseDialogVisible() || poeticsFinishing)
            { Fail("BV_UiMismatch"); return false; }
            ownedAddon = "Talk";
            return SkipPoeticsVendor("BV_UiMismatch", rejectVendor: false);
        }
        if (shopVisible || PurchaseDialogVisible())
        {
            if (ownedPoeticsOffer == null || string.IsNullOrEmpty(ownedAddon) ||
                !BlundervilleGameAdapter.TryValidateOffer(ExchangeOffer(ownedPoeticsOffer), out var current, out _, PoeticsShopCatalog.CurrencyItemId) ||
                current != ownedAddon)
                return SkipPoeticsVendor("BV_UiMismatch");
            if ((poeticsCloseStage & 1) == 0)
            {
                if (!CancelPurchase(ownedPoeticsOffer)) return SkipPoeticsVendor("Shop_UncertainPurchase");
                BlundervilleGameAdapter.CloseOwnedShop(ownedAddon);
                poeticsCloseStage |= 1;
            }
            return false;
        }
        if (menuVisible)
        {
            if ((poeticsCloseStage & 2) == 0)
            {
                if (!game.CancelExchangeMenu()) return SkipPoeticsVendor("BV_UiMismatch");
                poeticsCloseStage |= 2;
            }
            return false;
        }
        if (movement) { plugin.VNavIPC.Stop(); movement = false; }
        positionApproach = false;
        poeticsCloseStage = 0;
        ownedPoeticsOffer = null; ownedAddon = string.Empty;
        npc = 0; lastMenu = string.Empty; categoryItem = 0;
        return true;
    }

    private bool TryDeliveryBusy(out bool busy)
    {
        busy = false;
        try
        {
            if (!AutoRetainerLoaded) return false;
            busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    private bool PurchasedPoeticsGearGone()
    {
        foreach (var item in targets.Keys)
            if (!BlundervilleGameAdapter.TryInventory(item, out var count, out _) || count != 0) return false;
        return true;
    }

    private bool ContinuePoetics()
    {
        if (!running) return false;
        if (Volatile.Read(ref stopRequested) != 0) { Stop(); return false; }
        if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != character ||
            !ReferenceEquals(Settings, sessionSettings) || Settings.BuyPoeticsGear != poeticsMode ||
            Settings.DeliverPoeticsGear != poeticsDeliver || Settings.PoeticsEndingLocation != poeticsEnding)
        { Fail("BV_SessionChanged"); return false; }
        if (!Ready) { deliveryIdleSince = null; return false; }
        return true;
    }

    private void FinishPoetics()
    {
        if (!ContinuePoetics()) return;
        poeticsFinishing = true;
        if (!poeticsShopClosed)
        {
            if (!ClosePoeticsTrader()) return;
            // Purchase travel is finished before AR owns any travel of its own.
            travel = travelSubmitted = false;
            poeticsShopClosed = true;
        }
        if (poeticsDeliver && !travelSubmitted)
        {
            if (!deliveryAdmitted)
            {
                if (!TryDeliveryBusy(out var before)) { Fail("Shop_EligibilityUnknown"); return; }
                if (before) { Fail("BV_BusyClient"); return; }
                plugin.YesAlreadyIPC.Unpause();
                if (!ContinuePoetics()) return;
                if (!Plugin.Framework.IsInFrameworkUpdateThread || !Plugin.CommandManager.ProcessCommand("/ays deliver") ||
                    !TryDeliveryBusy(out var after) || !after)
                { Fail("Shop_EligibilityUnknown"); return; }
                deliveryAdmitted = true;
                deliveryIdleSince = null;
                Status = Ui.M("Shop_PoeticsDelivering");
                return;
            }
            var busy = TryDeliveryBusy(out var currentBusy) ? currentBusy : (bool?)null;
            if (BlundervilleGameAdapter.Visible("GrandCompanySupplyList") || PurchaseDialogVisible() ||
                BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString") ||
                BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency") ||
                BlundervilleGameAdapter.Visible("Talk")) busy = null;
            if (!PoeticsShopping.DeliverySettled(deliveryAdmitted, busy, PurchasedPoeticsGearGone(),
                Volatile.Read(ref stopRequested) != 0, ReferenceEquals(Settings, sessionSettings), DateTime.UtcNow, ref deliveryIdleSince))
            {
                Status = Ui.M("Shop_PoeticsDelivering");
                return;
            }
        }
        if (poeticsEnding == BlundervilleEndingLocation.Stay)
        { if (ContinuePoetics()) CompleteNow(); return; }
        var rooms = new uint[] { 0, 178, 179, 177, 429, 843, 990, 1205 };
        var commands = new[] { 0, 2, 3, 1, 4, 6, 7, 8 };
        var selected = (int)poeticsEnding;
        if (selected < 1 || selected >= rooms.Length) { Fail("BV_TravelUnavailable"); return; }
        var travelBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        if (Plugin.ClientState.TerritoryType == rooms[selected] && !travelBusy)
        { travel = false; CompleteNow(); return; }
        Status = Ui.M("BV_EndingTravel");
        if (travelSubmitted) return;
        if (travelBusy) { Fail("BV_TravelUnavailable"); return; }
        if (!ContinuePoetics()) return;
        if (poeticsDeliver && (!TryDeliveryBusy(out var finalBusy) || finalBusy || !PurchasedPoeticsGearGone()))
        { deliveryIdleSince = null; return; }
        travelSubmitted = true;
        travel = Plugin.CommandManager.ProcessCommand("/li inn " + commands[selected]);
        if (!travel) Fail("BV_TravelUnavailable");
    }

    private void ResetPoeticsProgress()
    {
        poeticsFinishing = poeticsShopClosed = deliveryAdmitted = positionApproach = false;
        deliveryIdleSince = null; poeticsCloseStage = 0;
        poeticsVendorRecovery = false; rejectedPoeticsVendors.Clear();
    }
}

