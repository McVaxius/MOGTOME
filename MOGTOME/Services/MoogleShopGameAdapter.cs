using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MOGTOME.Models;

namespace MOGTOME.Services;

internal sealed unsafe class MoogleShopGameAdapter
{
    private readonly BlundervilleGameAdapter interactions;
    private readonly HashSet<uint> npcIds = [];
    private readonly HashSet<string> exchangeMenus = [];
    private readonly HashSet<string> cancelMenus = [];
    private readonly HashSet<uint> tomestones = [];
    internal IReadOnlyList<MoogleShopOffer> Catalog { get; private set; } = [];
    internal MoogleShopGameAdapter(Plugin plugin) => interactions = new(plugin);

    internal void LoadCatalog()
    {
        Catalog = [];
        tomestones.Clear(); npcIds.Clear(); exchangeMenus.Clear(); cancelMenus.Clear();
        foreach (var text in Plugin.DataManager.GetExcelSheet<Addon>(ClientLanguage.English))
            if (GameText.Normalize(text.Text.ToString()) == "Cancel")
                cancelMenus.Add(Plugin.DataManager.GetExcelSheet<Addon>().GetRow(text.RowId).Text.ToString());
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English))
            if (item.StackSize == 999 && item.Name.ToString().StartsWith("Irregular Tomestone", StringComparison.OrdinalIgnoreCase))
                tomestones.Add(item.RowId);
        foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English))
        {
            if (!npc.Singular.ToString().Equals("itinerant moogle", StringComparison.OrdinalIgnoreCase) ||
                !Plugin.DataManager.GetExcelSheet<ENpcBase>().TryGetRow(npc.RowId, out var data)) continue;
            foreach (var handler in data.ENpcData)
            {
                if (!handler.Is<CustomTalk>()) continue;
                var talk = Plugin.DataManager.GetExcelSheet<CustomTalk>(ClientLanguage.English).GetRow(handler.RowId);
                if (!talk.Name.ToString().StartsWith("CtsEtcVoyagerMoogle", StringComparison.Ordinal)) continue;
                npcIds.Add(npc.RowId);
                exchangeMenus.Add(Plugin.DataManager.GetExcelSheet<CustomTalk>().GetRow(handler.RowId).MainOption.ToString());
                var script = talk.Name.ToString();
                var suffix = script[^5..];
                var sheetName = $"custom/{suffix[..3]}/{script}";
                var englishMenu = Plugin.DataManager.GameData.Excel.GetSheet<Lumina.Excel.RawRow>(Lumina.Data.Language.English, sheetName);
                var language = Plugin.ClientState.ClientLanguage switch
                {
                    ClientLanguage.Japanese => Lumina.Data.Language.Japanese,
                    ClientLanguage.German => Lumina.Data.Language.German,
                    ClientLanguage.French => Lumina.Data.Language.French,
                    _ => Lumina.Data.Language.English,
                };
                var clientMenu = Plugin.DataManager.GameData.Excel.GetSheet<Lumina.Excel.RawRow>(language, sheetName);
                // Resolve the current translated script option through its native column metadata.
                foreach (var row in englishMenu)
                    if (row.ReadColumn(1).ToString() == "Exchange irregular tomestones.")
                        exchangeMenus.Add(clientMenu.GetRow(row.RowId).ReadColumn(1).ToString() ?? string.Empty);
            }
        }
        var festivals = new List<(uint Id, uint Phase)>();
        var main = GameMain.Instance();
        if (main == null) throw new InvalidOperationException("Festival state unavailable");
        foreach (var festival in main->ActiveFestivals) festivals.Add((festival.Id, festival.Phase));
        var uiPlayer = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (uiPlayer == null) throw new InvalidOperationException("Player festival state unavailable");
        for (var index = 0; index < uiPlayer->ActiveFestivalIds.Length; index++)
            festivals.Add((uiPlayer->ActiveFestivalIds[index], uiPlayer->ActiveFestivalPhases[index]));
        var english = Plugin.DataManager.GetExcelSheet<SpecialShop>(ClientLanguage.English);
        // These names are the game's current-exchange identities, independent of event item IDs.
        // A festival-specific current exchange supersedes its off-season version with the same name.
        var eligible = english.Where(s => s.Name.ToString() is "Newest Irregular Tomestone Exchange" or "Irregular Tomestone Exchange (Seasonal)")
            .Where(s => s.UseCurrencyType == 2 && MoogleShopMath.FestivalEligible(s.RequiredFestival.RowId, s.RequiredFestivalPhase, festivals))
            .GroupBy(s => s.Name.ToString())
            .SelectMany(g => g.Any(s => s.RequiredFestival.RowId != 0) ? g.Where(s => s.RequiredFestival.RowId != 0) : g);
        var offers = new List<MoogleShopOffer>();
        foreach (var identity in eligible)
        {
            var shop = Plugin.DataManager.GetExcelSheet<SpecialShop>().GetRow(identity.RowId);
            foreach (var row in shop.Item)
            {
                var offer = MoogleShopMath.ReadOffer(shop.RowId, shop.Name.ToString(),
                    row.ReceiveItems.Select(r => (r.Item.RowId, r.ReceiveCount, r.ReceiveHq)),
                    row.ItemCosts.Select(c => (c.ItemCost.RowId, c.CurrencyCost, (uint)c.CollectabilityCost, c.CostType)), tomestones);
                if (offer != null) offers.Add(offer with { Gate = ShopOfferEligibility.ReadGate(shop, row) });
            }
        }
        var pvpItems = ShopMissingSelection.ReadPvpItems();
        Catalog = offers.Select(o => o with { MissingKind = ShopMissingSelection.ReadKind(o.ItemId, pvpItems) }).ToArray();
        Plugin.Log.Information("[MOGTOME][Shop] catalog offers={Offers}; shops={Shops}; tomestones={Currencies}; traders={Traders}; cancelLabels={CancelLabels}; exchangeLabels={ExchangeLabels}; festivals={Festivals}",
            Catalog.Count, string.Join(',', Catalog.Select(o => o.ShopId).Distinct()),
            string.Join(',', Catalog.Select(o => o.TomestoneId).Distinct()), npcIds.Count, cancelMenus.Count, string.Join(" | ", exchangeMenus),
            string.Join(',', festivals.Where(f => f.Id != 0).Select(f => $"{f.Id}:{f.Phase}")));
    }

    internal IGameObject? FindTrader()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? null : Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventNpc && npcIds.Contains(o.BaseId))
            .OrderBy(o => System.Numerics.Vector3.Distance(player.Position, o.Position)).FirstOrDefault();
    }
    internal bool Approach(IGameObject npc, ref bool movement) => interactions.ApproachAndInteract(npc, ref movement);

    internal bool SelectExchange(MoogleShopOffer offer, ref string lastMenu)
    {
        if (!TryReadExchangeMenu(out var addonName, out var names)) return false;
        var menu = addonName + "\n" + string.Join('\n', names);
        if (menu == lastMenu) return true;
        var row = FindExchangeRow(names, offer.ShopName, exchangeMenus);
        if (row < 0)
        {
            Plugin.Log.Warning("[MOGTOME][Shop] exchange menu mismatch; addon={Addon}; shop={Shop}; labels={Labels}; choices={Choices}",
                addonName, offer.ShopName, string.Join(" | ", exchangeMenus), string.Join(" | ", names));
            return false;
        }
        lastMenu = menu;
        Plugin.Log.Information("[MOGTOME][Shop] exchange menu selected; addon={Addon}; row={Row}; label={Label}", addonName, row, names[row]);
        return BlundervilleGameAdapter.Callback(addonName, row);
    }

    internal bool MatchesExchangeMenu(MoogleShopOffer offer)
        => TryReadExchangeMenu(out _, out var names) && FindExchangeRow(names, offer.ShopName, exchangeMenus) >= 0;

    internal static bool ExchangeMenuReady() => TryReadExchangeMenu(out _, out _);

    internal static int FindExchangeRow(IReadOnlyList<string> names, string shopName, IReadOnlyCollection<string> menuLabels)
    {
        var matches = names.Select((name, index) => (name, index))
            .Where(n => !string.IsNullOrWhiteSpace(n.name) && (GameText.Normalize(n.name) == GameText.Normalize(shopName) ||
                menuLabels.Any(m => !string.IsNullOrWhiteSpace(m) && GameText.Normalize(m) == GameText.Normalize(n.name)))).ToArray();
        return matches.Length == 1 ? matches[0].index : -1;
    }

    private static bool TryReadExchangeMenu(out string addonName, out List<string> names)
    {
        names = [];
        addonName = BlundervilleGameAdapter.Visible("SelectIconString") ? "SelectIconString" : "SelectString";
        if (BlundervilleGameAdapter.Visible("SelectIconString") && BlundervilleGameAdapter.Visible("SelectString")) return false;
        if (addonName == "SelectIconString")
        {
            var addon = (AddonSelectIconString*)Plugin.GameGui.GetAddonByName(addonName).Address;
            if (addon == null || !addon->IsVisible || !addon->IsReady || addon->PopupMenu.PopupMenu.EntryNames == null ||
                addon->PopupMenu.PopupMenu.EntryCount is < 1 or > 32) return false;
            for (var index = 0; index < addon->PopupMenu.PopupMenu.EntryCount; index++)
                names.Add(addon->PopupMenu.PopupMenu.EntryNames[index].ToString());
        }
        else
        {
            var addon = (AddonSelectString*)Plugin.GameGui.GetAddonByName(addonName).Address;
            if (addon == null || !addon->IsVisible || !addon->IsReady || addon->PopupMenu.EntryNames == null ||
                addon->PopupMenu.EntryCount is < 1 or > 32) return false;
            for (var index = 0; index < addon->PopupMenu.EntryCount; index++) names.Add(addon->PopupMenu.EntryNames[index].ToString());
        }
        return true;
    }

    internal bool CancelExchangeMenu()
    {
        if (!BlundervilleGameAdapter.Visible("SelectString") && !BlundervilleGameAdapter.Visible("SelectIconString")) return true;
        if (!TryReadExchangeMenu(out var addonName, out var names)) return false;
        var row = FindExchangeRow(names, string.Empty, cancelMenus);
        return row >= 0 && BlundervilleGameAdapter.Callback(addonName, row);
    }

    internal static bool TryStacks(out IReadOnlyList<TomestoneStack> stacks)
    {
        var result = new List<TomestoneStack>(); stacks = result;
        var inventory = InventoryManager.Instance();
        if (inventory == null || !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return false;
        for (var bagId = 0; bagId < 4; bagId++)
        {
            var bag = inventory->GetInventoryContainer((InventoryType)bagId);
            if (bag == null || !bag->IsLoaded || bag->Items == null || bag->Size is < 1 or > 100) return false;
            for (var index = 0; index < bag->Size; index++)
            {
                var slot = bag->GetInventorySlot(index);
                if (slot == null) return false;
                if (slot->ItemId == 0) continue;
                // Currency consolidation never touches HQ, collectible or symbolic slots.
                result.Add(new(bagId, (ushort)index, slot->ItemId, checked((uint)slot->Quantity), slot->Flags == 0 && !slot->IsSymbolic));
            }
        }
        return true;
    }

    internal static bool Move(TomestoneMove move)
    {
        if (BlundervilleGameAdapter.Visible("ShopExchangeItem") || BlundervilleGameAdapter.Visible("ShopExchangeItemDialog") ||
            BlundervilleGameAdapter.Visible("SelectString") || BlundervilleGameAdapter.Visible("SelectIconString") ||
            BlundervilleGameAdapter.Visible("SelectYesno") ||
            !TryStacks(out var stacks) || !stacks.Contains(move.Source) || !stacks.Contains(move.Destination) ||
            stacks.Where(s => s.ItemId == move.Source.ItemId).Sum(s => (long)s.Quantity) != move.Total) return false;
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;
        // Native merge technique also used by Automaton's AutoMerge. Submit once; confirm bags before replanning.
        var result = inventory->MoveItemSlot((InventoryType)move.Source.Bag, move.Source.Slot,
            (InventoryType)move.Destination.Bag, move.Destination.Slot, true);
        Plugin.Log.Information("[MOGTOME][Shop] merge submitted currency={Currency}; from={FromBag}:{FromSlot}; to={ToBag}:{ToSlot}; transfer={Quantity}; nativeResult={Result}",
            move.Source.ItemId, move.Source.Bag, move.Source.Slot, move.Destination.Bag, move.Destination.Slot, move.Transfer, result);
        return true;
    }

    internal static bool ValidateOffer(MoogleShopOffer offer, out int row)
    {
        row = -1;
        var agent = AgentShop.Instance();
        if (!BlundervilleGameAdapter.ShopReady("ShopExchangeItem") || agent == null || agent->ShopName.ToString() != offer.ShopName ||
            agent->ItemReceive == null || agent->ItemCost == null || agent->ItemReceiveCount is < 1 or > 122 ||
            agent->ItemCostCount != agent->ItemReceiveCount * 3) return false;
        var receives = agent->ItemReceiveSpan;
        var costs = agent->ItemCostSpan;
        for (var index = 0; index < receives.Length; index++)
        {
            if (receives[index].ItemId != offer.ItemId || receives[index].ItemCount != offer.ReceiveCount) continue;
            var paid = new List<MoogleShopCost>();
            for (var cost = 0; cost < 3; cost++)
            {
                var entry = costs[index * 3 + cost];
                if (entry.ItemId != 0 || entry.ItemCount != 0) paid.Add(new(entry.ItemId, entry.ItemCount));
            }
            if (paid.Count != offer.Costs.Length || !paid.OrderBy(c => c.ItemId).SequenceEqual(offer.Costs.OrderBy(c => c.ItemId))) continue;
            if (row != -1) return false;
            row = index;
        }
        return row >= 0;
    }

    internal static bool Confirm(MoogleShopOffer offer, int expectedRow)
    {
        if (!ValidateOffer(offer, out var row) || row != expectedRow) return false;
        if (BlundervilleGameAdapter.Visible("SelectYesno"))
        {
            // Reuse the validated collectible identity/prompt path only for single-currency single-item prompts.
            if (offer.Costs.Length != 1 || offer.ReceiveCount != 1) return false;
            var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 16 || addon->AtkValues[12].Type != AtkValueType.Int ||
                !addon->CollectibleAtkValuesAvailable || addon->CollectibleTypedAtkValues->ItemId.Type != AtkValueType.UInt ||
                addon->CollectibleTypedAtkValues->ItemId.UInt != offer.ItemId || addon->YesButton == null ||
                !addon->YesButton->IsEnabled || addon->YesButton->AtkComponentBase.OwnerNode == null || addon->PromptText == null) return false;
            var prompt = GameText.ReadVisibleText(addon->PromptText->NodeText.AsSpan());
            var numbers = System.Text.RegularExpressions.Regex.Matches(prompt, @"[0-9](?:[0-9,.\u00A0\u202F]*[0-9])?");
            if (numbers.Count != 1 || new string(numbers[0].Value.Where(char.IsDigit).ToArray()) != offer.Costs[0].Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
            return BlundervilleGameAdapter.ClickRegisteredButton(&addon->YesButton->AtkComponentBase.OwnerNode->AtkResNode);
        }
        var dialog = (AddonShopExchangeItemDialog*)Plugin.GameGui.GetAddonByName("ShopExchangeItemDialog").Address;
        var agent = AgentShop.Instance();
        if (dialog == null || !dialog->IsVisible || !dialog->IsReady || agent == null || agent->SelectedItemIndex != row ||
            dialog->UldManager.NodeList == null || dialog->ExchangeButton == null || !dialog->ExchangeButton->IsEnabled ||
            dialog->ExchangeButton->AtkComponentBase.OwnerNode == null || dialog->AtkValues == null ||
            !DialogValuesMatch(offer, new ReadOnlySpan<AtkValue>(dialog->AtkValues, dialog->AtkValuesCount))) return false;
        var inputs = 0;
        for (var index = 0; index < dialog->UldManager.NodeListCount; index++)
        {
            var node = dialog->UldManager.NodeList[index];
            if (node == null) continue;
            var input = node->GetAsAtkComponentNumericInput();
            if (input == null) continue;
            if (input->Value != 1) return false; // One exchange transaction, never an edited quantity.
            inputs++;
        }
        // The live item-exchange dialog has no quantity editor for a fixed exchange.
        // Its displayed reward and every active currency amount remain authoritative.
        if (inputs > 1) return false;
        return BlundervilleGameAdapter.ClickRegisteredButton(&dialog->ExchangeButton->AtkComponentBase.OwnerNode->AtkResNode);
    }

    internal static bool DialogValuesMatch(MoogleShopOffer offer, ReadOnlySpan<AtkValue> values)
    {
        // Current native 35-value layout, validated against the live item/quantity/cost prompt.
        if (values.Length != 35 || values[8].Type != AtkValueType.UInt || values[8].UInt != offer.ItemId ||
            values[9].Type != AtkValueType.UInt || values[9].UInt != 0 ||
            values[18].Type != AtkValueType.UInt || values[18].UInt != offer.ReceiveCount ||
            values[19].Type != AtkValueType.UInt || values[19].UInt != 0) return false;
        var costs = new List<MoogleShopCost>();
        for (var slot = 0; slot < 3; slot++)
        {
            var id = values[23 + slot];
            var amount = values[29 + slot];
            if (id.Type != AtkValueType.UInt) return false;
            if (id.UInt == 0)
            {
                if ((amount.Type == AtkValueType.UInt && amount.UInt != 0) ||
                    (amount.Type == AtkValueType.Int && amount.Int != 0)) return false;
                continue;
            }
            if (amount.Type != AtkValueType.UInt || amount.UInt == 0) return false;
            costs.Add(new(id.UInt, amount.UInt));
        }
        return costs.OrderBy(c => c.ItemId).SequenceEqual(offer.Costs.OrderBy(c => c.ItemId));
    }

    internal static void InspectConfirmation(int expectedRow)
    {
        var dialog = (AddonShopExchangeItemDialog*)Plugin.GameGui.GetAddonByName("ShopExchangeItemDialog").Address;
        var agent = AgentShop.Instance();
        if (dialog == null || !dialog->IsVisible || agent == null) return;
        Plugin.Log.Warning("[MOGTOME][Shop] confirmation state expectedRow={Expected}; selectedRow={Selected}; stackSize={Stack}; ready={Ready}; button={Button}; enabled={Enabled}; nodes={Nodes}",
            expectedRow, agent->SelectedItemIndex, agent->SelectedItemStackSize, dialog->IsReady,
            dialog->ExchangeButton != null, dialog->ExchangeButton != null && dialog->ExchangeButton->IsEnabled, dialog->UldManager.NodeListCount);
        if (dialog->UldManager.NodeList == null || dialog->UldManager.NodeListCount > 256) return;
        var inputs = 0;
        for (var index = 0; index < dialog->UldManager.NodeListCount; index++)
        {
            var node = dialog->UldManager.NodeList[index];
            if (node == null) continue;
            var input = node->GetAsAtkComponentNumericInput();
            if (input == null) continue;
            inputs++;
            Plugin.Log.Warning("[MOGTOME][Shop] quantity input node={Node}; value={Value}; visible={Visible}", node->NodeId, input->Value, node->IsVisible());
        }
        Plugin.Log.Warning("[MOGTOME][Shop] confirmation numeric inputs={Inputs}", inputs);
    }

    internal static bool CancelPurchase(MoogleShopOffer offer)
        => BlundervilleGameAdapter.CancelOwnedPurchase(new(offer.ShopId, offer.ItemId, offer.ReceiveCount, offer.Costs[0].Count, offer.ShopName));
}
