using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed partial class MoogleShopService
{
    private bool poeticsFinishing, poeticsShopClosed, deliveryAdmitted, positionApproach;
    private int poeticsCloseStage;
    private DateTime? deliveryIdleSince;

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

    private bool HasPoeticsVendor(MoogleShopOffer offer)
        => poetics.Vendors(offer).Count == 0 || poetics.Vendors(offer).Any(CanReachPoeticsVendor);

    private static bool CanReachPoeticsVendor(PoeticsShopVendor vendor)
        => vendor.TerritoryId == Plugin.ClientState.TerritoryType || TryPoeticsDestination(vendor, out _);

    private ShopOfferAvailability ReadPoeticsAvailability(MoogleShopOffer offer)
        => poetics.ReadAvailability(offer, CanReachPoeticsVendor);

    private bool PurchaseDialogVisible()
        => BlundervilleGameAdapter.Visible("SelectYesno") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog") ||
            poeticsMode && BlundervilleGameAdapter.Visible("ShopExchangeCurrencyDialog");

    private bool CancelPurchase(MoogleShopOffer offer)
        => poeticsMode
            ? BlundervilleGameAdapter.CancelOwnedPurchase(ExchangeOffer(offer), PoeticsShopCatalog.CurrencyItemId)
            : MoogleShopGameAdapter.CancelPurchase(offer);

    private bool PreparePoeticsOffer(MoogleShopOffer offer)
    {
        var exchange = ExchangeOffer(offer);
        if (BlundervilleGameAdapter.Visible("ShopExchangeCurrency"))
        {
            if (!BlundervilleGameAdapter.EnsureOfferCategory(exchange, ref categoryItem, out var ready))
            { Fail("BV_UiMismatch"); return false; }
            if (!ready) return false;
        }
        if (!BlundervilleGameAdapter.TryValidateOffer(exchange, out var addon, out purchaseRow, PoeticsShopCatalog.CurrencyItemId))
        { Fail("BV_UiMismatch"); return false; }
        ownedAddon = addon;
        ownedPoeticsOffer = offer;
        return true;
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
            var currentVendor = poeticsVendor.MatchCurrent(poetics.Vendors(offer));
            if (currentVendor == null) { Fail("Shop_EligibilityUnknown"); return false; }
            // Keep the current offer's labels even when a catalog refresh replaced its records.
            poeticsVendor = currentVendor;
        }
        if (poeticsVendor == null)
        {
            poeticsVendor = poetics.Vendors(offer)
                .Where(vendor => CanReachPoeticsVendor(vendor) &&
                    PoeticsShopCatalog.ReadVendorAvailability(vendor) == ShopOfferAvailability.Available)
                .OrderBy(vendor => vendor.TerritoryId == Plugin.ClientState.TerritoryType ? 0 : 1)
                .ThenBy(vendor => vendor.NpcId).ThenBy(vendor => vendor.TerritoryId).FirstOrDefault();
            if (poeticsVendor == null) { Fail("Shop_NoTrader"); return false; }
            travelSubmitted = false;
        }
        var vendorAvailability = PoeticsShopCatalog.ReadVendorAvailability(poeticsVendor);
        if (vendorAvailability != ShopOfferAvailability.Available)
        { Fail(vendorAvailability == ShopOfferAvailability.Locked ? "Shop_Locked" : "Shop_EligibilityUnknown"); return false; }
        if (Plugin.ClientState.TerritoryType != poeticsVendor.TerritoryId)
        {
            if (!travelSubmitted)
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc() ||
                    !TryPoeticsDestination(poeticsVendor, out var destination))
                { Fail("BV_TravelUnavailable"); return false; }
                travelSubmitted = true;
                travel = Plugin.CommandManager.ProcessCommand("/li " + destination);
                if (!travel) Fail("BV_TravelUnavailable");
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
            return BlundervilleGameAdapter.ShopReady(BlundervilleGameAdapter.Visible("ShopExchangeCurrency")
                ? "ShopExchangeCurrency" : "ShopExchangeItem");
        }
        if (PurchaseDialogVisible() || BlundervilleGameAdapter.Visible("Talk")) { Fail("BV_UiMismatch"); return false; }
        if (BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString"))
        {
            if (!OwnsTrader() || !MoogleShopGameAdapter.TryReadExchangeMenu(out var addon, out var names))
            { Fail("BV_UiMismatch"); return false; }
            var menu = addon + "\n" + string.Join('\n', names);
            if (menu == lastMenu) return false;
            var row = MoogleShopGameAdapter.FindExchangeRow(names, offer.ShopName, poeticsVendor.MenuLabels);
            if (row < 0) { Fail("BV_UiMismatch"); return false; }
            lastMenu = menu;
            if (!BlundervilleGameAdapter.Callback(addon, row)) Fail("BV_UiMismatch");
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
            { Fail("Shop_NoTrader"); return false; }
            if (!movement)
            {
                if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc()) return false;
                movement = plugin.VNavIPC.MoveTo(poeticsVendor.Position);
                if (!movement) { Fail("BV_TravelUnavailable"); return false; }
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

    private bool ClosePoeticsTrader()
    {
        var shopVisible = BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeCurrency");
        var menuVisible = BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString");
        if ((shopVisible || menuVisible || PurchaseDialogVisible()) && !OwnsTrader())
        { Fail("BV_UiMismatch"); return false; }
        if (shopVisible || PurchaseDialogVisible())
        {
            if (ownedPoeticsOffer == null || string.IsNullOrEmpty(ownedAddon) ||
                !BlundervilleGameAdapter.TryValidateOffer(ExchangeOffer(ownedPoeticsOffer), out var current, out _, PoeticsShopCatalog.CurrencyItemId) ||
                current != ownedAddon)
            { Fail("BV_UiMismatch"); return false; }
            if (poeticsCloseStage == 0)
            {
                if (!CancelPurchase(ownedPoeticsOffer)) { Fail("Shop_UncertainPurchase"); return false; }
                BlundervilleGameAdapter.CloseOwnedShop(ownedAddon);
                poeticsCloseStage = 1;
            }
            return false;
        }
        if (menuVisible)
        {
            if (poeticsCloseStage != 2)
            {
                if (!game.CancelExchangeMenu()) { Fail("BV_UiMismatch"); return false; }
                poeticsCloseStage = 2;
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
    }
}

