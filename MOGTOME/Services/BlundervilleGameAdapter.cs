using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using AethertekUI.Dalamud;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using MOGTOME.Models;

namespace MOGTOME.Services;

internal sealed record BlundervilleOffer(uint ShopId, uint ItemId, uint ReceiveCount, uint Price, string ShopName)
{
    internal ShopOfferGate Gate { get; init; } = new();
    internal ShopMissingKind MissingKind { get; init; }
}
internal enum BlundervilleRegistration { None, Owned, Missing, Unknown }

internal sealed unsafe class BlundervilleGameAdapter
{
    internal const uint MgfItemId = 41629;
    internal const uint Arena = 1165;
    internal const uint Square = 1197;
    internal const uint Saucer = 144;
    private readonly Plugin plugin;
    private Dictionary<string, HashSet<uint>> npcIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<BlundervilleOffer> Catalog { get; private set; } = [];
    public BlundervilleGameAdapter(Plugin plugin) { this.plugin = plugin; }

    public void LoadCatalog()
    {
        var offers = new List<BlundervilleOffer>();
        foreach (var shop in Plugin.DataManager.GetExcelSheet<SpecialShop>())
        foreach (var row in shop.Item)
        {
            var offer = ReadOffer(shop.RowId, shop.Name.ToString(),
                row.ReceiveItems.Select(r => (r.Item.RowId, r.ReceiveCount, r.ReceiveHq)),
                row.ItemCosts.Select(c => (c.ItemCost.RowId, c.CurrencyCost, (uint)c.CollectabilityCost, c.CostType)));
            if (offer != null) offers.Add(offer with { Gate = ShopOfferEligibility.ReadGate(shop, row) });
        }
        // Resolve identities in English once; object interactions and UI checks use IDs/client language.
        npcIds = ReadNpcIdentities(Plugin.DataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English)
            .Select(npc => (npc.RowId, npc.Singular.ToString())));
        var linkedShops = new HashSet<uint>();
        if (npcIds.TryGetValue("MGF trader", out var traderIds))
        foreach (var traderId in traderIds)
        {
            if (!Plugin.DataManager.GetExcelSheet<ENpcBase>().TryGetRow(traderId, out var trader)) continue;
            var events = new Queue<Lumina.Excel.RowRef>(trader.ENpcData);
            var visited = new HashSet<uint>();
            var mgfShopIds = offers.Select(o => o.ShopId).ToHashSet();
            while (events.TryDequeue(out var current) && visited.Count < 64)
            {
                if (current.RowId == 0 || !visited.Add(current.RowId)) continue;
                if (current.Is<SpecialShop>() && mgfShopIds.Contains(current.RowId)) linkedShops.Add(current.RowId);
                else if (current.Is<TopicSelect>())
                    foreach (var child in Plugin.DataManager.GetExcelSheet<TopicSelect>().GetRow(current.RowId).Shop) events.Enqueue(child);
                else if (current.Is<PreHandler>()) events.Enqueue(Plugin.DataManager.GetExcelSheet<PreHandler>().GetRow(current.RowId).Target);
                else if (current.Is<CustomTalk>())
                {
                    var talk = Plugin.DataManager.GetExcelSheet<CustomTalk>().GetRow(current.RowId);
                    if (talk.SpecialLinks.IsSubrow<CustomTalkNestHandlers>())
                        foreach (var child in Plugin.DataManager.GetSubrowExcelSheet<CustomTalkNestHandlers>().Flatten().Where(r => r.RowId == talk.SpecialLinks.RowId))
                            events.Enqueue(child.NestHandler);
                    else events.Enqueue(talk.SpecialLinks);
                    foreach (var script in talk.Script)
                        if (mgfShopIds.Contains(script.ScriptArg)) linkedShops.Add(script.ScriptArg);
                }
            }
        }
        var pvpItems = ShopMissingSelection.ReadPvpItems();
        Catalog = offers.Where(o => linkedShops.Contains(o.ShopId)).Distinct()
            .Select(o => o with { MissingKind = ShopMissingSelection.ReadKind(o.ItemId, pvpItems) }).ToArray();
        Plugin.Log.Information("[MOGTOME][BV] catalog offers={Offers}, MGF candidates={Candidates}, trader shops={Shops}, npc names={Names}, npc identities={Npcs}",
            Catalog.Count, offers.Count, linkedShops.Count, npcIds.Count, npcIds.Values.Sum(ids => ids.Count));
        var registrationStates = new List<BlundervilleRegistration>();
        foreach (var offer in Catalog)
        {
            var known = TryInventory(offer.ItemId, out var count, out _);
            var registration = Registration(offer.ItemId, known ? count : null);
            if (registration != BlundervilleRegistration.None) registrationStates.Add(registration);
            if (registration != BlundervilleRegistration.None && plugin.Configuration.Blunderville.PurchaseTargets.ContainsKey(offer.ItemId))
                Plugin.Log.Information("[MOGTOME][BV] selected collectible item={Item}; registration={Registration}; inventory={Inventory}",
                    offer.ItemId, registration, known ? count.ToString(CultureInfo.InvariantCulture) : "?");
        }
        Plugin.Log.Information("[MOGTOME][BV] collectible indicators: total={Total}, owned={Owned}, missing={Missing}, unknown={Unknown}",
            registrationStates.Count, registrationStates.Count(s => s == BlundervilleRegistration.Owned),
            registrationStates.Count(s => s == BlundervilleRegistration.Missing), registrationStates.Count(s => s == BlundervilleRegistration.Unknown));
    }

    internal static Dictionary<string, HashSet<uint>> ReadNpcIdentities(IEnumerable<(uint Id, string Name)> residents)
    {
        var identities = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in residents)
        {
            if (id == 0 || !(name.Equals("MGF trader", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Blunderville attendant", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Blunderville Registrar", StringComparison.OrdinalIgnoreCase))) continue;
            if (!identities.TryGetValue(name, out var ids)) identities[name] = ids = [];
            ids.Add(id);
        }
        return identities;
    }

    internal static BlundervilleOffer? ReadOffer(uint shopId, string shopName,
        IEnumerable<(uint ItemId, uint Count, bool Hq)> received,
        IEnumerable<(uint ItemId, uint Price, uint Collectability, byte CostType)> paid)
    {
        // Empty SpecialShop receive slots can have Count=1. Item identity determines whether a slot is populated.
        var receives = received.Where(r => r.ItemId != 0).ToArray();
        var costs = paid.Where(c => c.ItemId != 0 || c.Price != 0 || c.Collectability != 0).ToArray();
        if (receives.Length != 1 || receives[0].Count == 0 || receives[0].Hq ||
            costs.Length != 1 || costs[0].ItemId != MgfItemId || costs[0].Price == 0 ||
            costs[0].Collectability != 0 || costs[0].CostType != 0) return null;
        return new(shopId, receives[0].ItemId, receives[0].Count, costs[0].Price, shopName);
    }

    public static bool TryWallet(out uint balance)
    {
        balance = 0;
        var manager = CurrencyManager.Instance();
        if (manager == null || !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return false;
        balance = manager->GetItemCount(MgfItemId);
        return true;
    }

    public static BlundervilleRegistration Registration(uint itemId, int? inventoryCount)
    {
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return BlundervilleRegistration.None;
        var actionId = item.ItemAction.ValueNullable?.Action.RowId ?? 0;
        var state = ReadRegistration(actionId, inventoryCount, null);
        if (state != BlundervilleRegistration.Unknown || !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return state;
        // Same action categories and native unlock query as ADS's loot registrable check.
        var uiState = UIState.Instance();
        var exdItem = ExdModule.GetItemRowById(itemId);
        return uiState == null || exdItem == null ? state : ReadRegistration(actionId, inventoryCount,
            uiState->IsItemActionUnlocked(exdItem) switch { 1 => 1, 2 => 2, _ => (int?)null });
    }

    internal static BlundervilleRegistration ReadRegistration(uint actionId, int? inventoryCount, int? unlockStatus)
    {
        if (actionId is not (1322 or 853 or 20086 or 37312 or 25183 or 2633 or 1013 or 3357)) return BlundervilleRegistration.None;
        if (inventoryCount > 0 || unlockStatus == 1) return BlundervilleRegistration.Owned;
        return inventoryCount == 0 && unlockStatus == 2 ? BlundervilleRegistration.Missing : BlundervilleRegistration.Unknown;
    }

    internal static BlundervilleRegistration Ownership(uint itemId, int? inventoryCount, XaItemOwnershipState stored)
    {
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return BlundervilleRegistration.None;
        var registration = Registration(itemId, inventoryCount);
        var collectible = registration != BlundervilleRegistration.None || item.EquipSlotCategory.RowId != 0;
        return ReadOwnership(collectible, inventoryCount, registration, stored);
    }

    internal static BlundervilleRegistration ReadOwnership(bool collectible, int? inventoryCount,
        BlundervilleRegistration registration, XaItemOwnershipState stored)
    {
        if (!collectible) return BlundervilleRegistration.None;
        var absenceKnown = inventoryCount == 0 && registration is BlundervilleRegistration.None or BlundervilleRegistration.Missing;
        return XaItemOwnership.Resolve(inventoryCount > 0 || registration == BlundervilleRegistration.Owned, absenceKnown, stored) switch
        {
            XaItemOwnershipState.Owned => BlundervilleRegistration.Owned,
            XaItemOwnershipState.Missing => BlundervilleRegistration.Missing,
            _ => BlundervilleRegistration.Unknown,
        };
    }

    internal static BlundervilleRegistration SelectionState(uint itemId, int? inventoryCount, BlundervilleRegistration ownership)
        => ReadSelectionState(ownership, Registration(itemId, inventoryCount));

    internal static BlundervilleRegistration ReadSelectionState(BlundervilleRegistration ownership, BlundervilleRegistration registration)
    {
        if (ownership == BlundervilleRegistration.Owned || registration == BlundervilleRegistration.Owned)
            return BlundervilleRegistration.Owned;
        // This manual action targets unregistered collectibles. It is not a claim
        // that every storage source has been observed empty (required for gear).
        return registration == BlundervilleRegistration.Missing ? registration : ownership;
    }


    public static BlundervilleRole Role()
    {
        var self = Plugin.PlayerState.ContentId;
        if (self == 0 || Plugin.ObjectTable.LocalPlayer == null) return BlundervilleRole.Unknown;
        if (InfoProxyCrossRealm.IsCrossRealmParty())
            return BlundervilleProgress.Role(true, InfoProxyCrossRealm.IsLocalPlayerPartyLeader(), 0, self, 0);
        var groups = GroupManager.Instance();
        if (groups == null) return BlundervilleRole.Unknown;
        var group = groups->GetGroup();
        if (group == null || group->MemberCount > 8) return BlundervilleRole.Unknown;
        var count = group->MemberCount;
        if (count <= 1) return BlundervilleRole.Solo;
        if (group->PartyLeaderIndex >= count) return BlundervilleRole.Unknown;
        return BlundervilleProgress.Role(false, false, count, self, group->PartyMembers[(int)group->PartyLeaderIndex].ContentId);
    }

    // Carried inventory: the four ordinary bags. Retainers, saddlebag/storage and placed furniture are excluded.
    public static bool TryInventory(uint itemId, out int count, out int capacity)
    {
        count = capacity = 0;
        var manager = InventoryManager.Instance();
        if (manager == null || !Plugin.ClientState.IsLoggedIn) return false;
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var sheetItem)) return false;
        var stackSize = Math.Max(1, sheetItem.StackSize);
        for (var bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            var bag = manager->GetInventoryContainer((InventoryType)bagIndex);
            if (bag == null || !bag->IsLoaded || bag->Items == null || bag->Size is < 1 or > 100) return false;
            for (var index = 0; index < bag->Size; index++)
            {
                var slot = bag->GetInventorySlot(index);
                if (slot == null) return false;
                if (slot->ItemId == 0) capacity = checked(capacity + (int)stackSize);
                else if (slot->ItemId == itemId)
                {
                    count = checked(count + (int)slot->Quantity);
                    if ((slot->Flags & InventoryItem.ItemFlags.HighQuality) == 0)
                        capacity = checked(capacity + Math.Max(0, (int)stackSize - (int)slot->Quantity));
                }
            }
        }
        return true;
    }

    public IGameObject? FindNpc(string name)
    {
        if (!npcIds.TryGetValue(name, out var ids)) return null;
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? null : Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventNpc && ids.Contains(o.BaseId))
            .OrderBy(o => Vector3.Distance(player.Position, o.Position)).FirstOrDefault();
    }

    public bool ApproachAndInteract(IGameObject npc, ref bool ownsMovement)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        if (Vector3.Distance(player.Position, npc.Position) > 3)
        {
            if (!ownsMovement)
            {
                if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc()) return false;
                if (!plugin.VNavIPC.MoveTo(npc.Position)) throw new InvalidOperationException("vnavmesh movement rejected");
                ownsMovement = true;
            }
            return false;
        }
        if (ownsMovement) { plugin.VNavIPC.Stop(); ownsMovement = false; }
        Plugin.TargetManager.Target = npc;
        if (!GameHelpers.InteractWithObject(npc)) throw new InvalidOperationException("NPC interaction rejected");
        return true;
    }

    internal static bool SendGameCommand(string command)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread) return false;
        if (Plugin.CommandManager.ProcessCommand(command)) return true;
        var commandToken = command.Split(' ', 2)[0];
        // Do not dispatch a guessed game command when its current sheet has no matching alias.
        if (!Plugin.DataManager.GetExcelSheet<TextCommand>(ClientLanguage.English).Any(row =>
            row.Command.ToString() == commandToken || row.ShortCommand.ToString() == commandToken ||
            row.Alias.ToString() == commandToken || row.ShortAlias.ToString() == commandToken)) return false;
        var module = UIModule.Instance();
        if (module == null) return false;
        var text = Utf8String.FromSequence(Encoding.UTF8.GetBytes(command));
        try { module->ProcessChatBoxEntry(text, nint.Zero); return true; }
        finally { text->Dtor(true); }
    }

    internal static bool Visible(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible;
    }
    internal static bool Callback(string name, params object[] values) => GameHelpers.TryFireAddonCallback(name, true, values);
    internal static bool ContainsText(string name, string expected)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        if (addon == null || !addon->IsVisible || addon->AtkValues == null || string.IsNullOrWhiteSpace(expected)) return false;
        for (var i = 0; i < addon->AtkValuesCount; i++)
            if (addon->AtkValues[i].Type is AtkValueType.String or AtkValueType.ManagedString &&
                GameText.Normalize(addon->AtkValues[i].String.ToString()) == GameText.Normalize(expected)) return true;
        return false;
    }

    private bool TrySquareWarp(out Warp warp)
    {
        warp = default;
        if (!npcIds.TryGetValue("Blunderville attendant", out var attendants)) return false;
        foreach (var id in attendants)
        {
            if (!Plugin.DataManager.GetExcelSheet<ENpcBase>().TryGetRow(id, out var npc)) continue;
            foreach (var handler in npc.ENpcData)
                if (handler.Is<Warp>() && Plugin.DataManager.GetExcelSheet<Warp>().TryGetRow(handler.RowId, out warp) &&
                    warp.TerritoryType.RowId == Square) return true;
        }
        return false;
    }

    public bool SquareEntryPromptVisible()
    {
        if (!TrySquareWarp(out var warp)) return false;
        var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->IsVisible || addon->PromptText == null) return false;
        var expected = Plugin.SeStringEvaluator.Evaluate(warp.Question, language: Plugin.ClientState.ClientLanguage).ToString();
        return GameText.MatchesEvaluated(GameText.ReadVisibleText(addon->PromptText->NodeText.AsSpan()), expected);
    }

    public bool SquareEntryTalkVisible(bool denied)
    {
        if (!TrySquareWarp(out var warp)) return false;
        var row = denied ? warp.ConditionFailEvent.RowId : warp.ConditionSuccessEvent.RowId;
        if (row == 0 || !Plugin.DataManager.GetExcelSheet<DefaultTalk>().TryGetRow(row, out var talk)) return false;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("Talk").Address;
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null ||
            addon->UldManager.NodeListCount is < 1 or > 256) return false;
        foreach (var source in talk.Text)
        {
            var expected = Plugin.SeStringEvaluator.Evaluate(source, language: Plugin.ClientState.ClientLanguage).ToString();
            if (string.IsNullOrWhiteSpace(expected) || expected == "0") continue;
            for (var index = 0; index < addon->UldManager.NodeListCount; index++)
            {
                var node = addon->UldManager.NodeList[index];
                if (node == null || !node->IsVisible()) continue;
                var text = node->GetAsAtkTextNode();
                if (text != null && GameText.MatchesEvaluated(GameText.ReadVisibleText(text->NodeText.AsSpan()), expected)) return true;
            }
        }
        return false;
    }

    public static bool BlundervilleEntryVisible()
    {
        var names = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>()
            .Where(c => c.TerritoryType.RowId == Arena).Select(c => c.Name.ToString());
        return names.Any(name => ContainsText("ContentsFinderConfirm", name));
    }

    public static bool CancelBlundervilleQueue()
    {
        try
        {
            var finder = ContentsFinder.Instance();
            if (finder == null) return false;
            var queue = finder->GetQueueInfo();
            if (queue == null) return false;
            if (queue->QueueState is ContentsFinderQueueState.None or ContentsFinderQueueState.InContent) return true;
            bool Matches(ContentsId entry) => entry.ContentType == ContentsType.Regular &&
                Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(entry.Id, out var content) &&
                content.TerritoryType.RowId == Arena;
            var owned = Matches(queue->PoppedQueueEntry);
            foreach (var entry in queue->QueuedEntries) owned |= Matches(entry);
            if (!owned) return false;
            Plugin.Log.Information("[MOGTOME][BV] cancelling native Blunderville queue; state={State}", queue->QueueState);
            queue->CancelQueue();
            return true; // Dispatch only; finishing waits for the game's queue state to clear.
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MOGTOME][BV] native queue cancellation unavailable");
            return false;
        }
    }

    // Validate the visible callback index against AgentShop's independent item/bundle truth.
    internal static bool ShopReady(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        var agent = AgentShop.Instance();
        return addon != null && addon->IsVisible && addon->IsReady && agent != null && agent->IsAgentActive() && agent->IsAddonReady();
    }

    public static bool TryValidateOffer(BlundervilleOffer offer, out string addonName, out int callbackRow)
    {
        addonName = Visible("ShopExchangeCurrency") ? "ShopExchangeCurrency" : "ShopExchangeItem";
        callbackRow = -1;
        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonReady() ||
            agent->ShopName.ToString() != offer.ShopName || !Visible(addonName)) return false;
        var receives = agent->ItemReceiveSpan;
        if (receives.Length is < 1 or > 122) return false;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(addonName).Address;
        if (addon == null || addon->AtkValues == null) return false;
        if (addonName == "ShopExchangeCurrency")
        {
            if (addon->AtkValuesCount <= 87) return false;
            var count = Number(addon->AtkValues[4]);
            if (count is < 1 or > 122 || addon->AtkValuesCount < 1310 + count || Number(addon->AtkValues[3]) != receives.Length) return false;
            var icon = Number(addon->AtkValues[87]);
            var currency = Plugin.DataManager.GetExcelSheet<Item>().Where(i => i.Icon == icon).Take(2).ToArray();
            if (currency.Length != 1 || currency[0].RowId != MgfItemId) return false;
            var native = new (uint ItemId, uint Count)[receives.Length];
            for (var i = 0; i < native.Length; i++) native[i] = (receives[i].ItemId, receives[i].ItemCount);
            var rows = new (long ItemId, long Price, long Callback)[(int)count];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = (Number(addon->AtkValues[1066 + i]), Number(addon->AtkValues[456 + i]), Number(addon->AtkValues[1310 + i]));
            callbackRow = ReadCurrencyRow(native, rows, offer);
        }
        else
        {
            var costs = agent->ItemCostSpan;
            if (costs.Length == 0 || costs.Length % receives.Length != 0) return false;
            var stride = costs.Length / receives.Length;
            if (stride is < 1 or > 3) return false;
            for (var i = 0; i < receives.Length; i++)
            {
                if (receives[i].ItemId != offer.ItemId || receives[i].ItemCount != offer.ReceiveCount) continue;
                var populated = 0;
                var valid = true;
                for (var j = 0; j < stride; j++)
                {
                    var cost = costs[i * stride + j];
                    if (cost.ItemId == 0 && cost.ItemCount == 0) continue;
                    populated++;
                    valid &= cost.ItemId == MgfItemId && cost.ItemCount == offer.Price;
                }
                if (!valid || populated != 1) continue;
                if (callbackRow != -1) return false;
                callbackRow = i;
            }
        }
        return callbackRow >= 0;
    }

    internal static int ReadCurrencyRow((uint ItemId, uint Count)[] receives,
        (long ItemId, long Price, long Callback)[] rows, BlundervilleOffer offer)
    {
        if (receives.Length is < 1 or > 122 || rows.Length is < 1 or > 122 || rows.Length > receives.Length) return -1;
        var result = -1;
        var callbacks = new HashSet<long>();
        foreach (var row in rows)
        {
            if (row.Callback < 0 || row.Callback >= receives.Length || !callbacks.Add(row.Callback)) return -1;
            var receive = receives[(int)row.Callback];
            if (row.ItemId != receive.ItemId || row.Price is <= 0 or > uint.MaxValue) return -1;
            if (receive.ItemId != offer.ItemId) continue;
            if (result != -1 || receive.Count != offer.ReceiveCount || row.Price != offer.Price) return -1;
            result = (int)row.Callback;
        }
        return result;
    }

    internal static bool EnsureOfferCategory(BlundervilleOffer offer, ref uint selectedItem, out bool ready)
    {
        ready = false;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("ShopExchangeCurrency").Address;
        var agent = AgentShop.Instance();
        if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount <= 4 ||
            agent == null || !agent->IsAgentActive() || !agent->IsAddonReady() || agent->ShopName.ToString() != offer.ShopName ||
            agent->ItemReceive == null || agent->ItemReceiveCount is < 1 or > 122) return false;
        var matches = 0;
        foreach (var receive in agent->ItemReceiveSpan)
            if (receive.ItemId == offer.ItemId && receive.ItemCount == offer.ReceiveCount) matches++;
        if (matches != 1) return false;
        var count = Number(addon->AtkValues[4]);
        if (count is < 1 or > 122 || addon->AtkValuesCount < 1066 + count) return false;
        for (var i = 0; i < count; i++)
            if (Number(addon->AtkValues[1066 + i]) == offer.ItemId) { ready = true; return true; }
        if (selectedItem == offer.ItemId) return true; // Wait for the one owned category click.
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(offer.ItemId);
        var slot = item.EquipSlotCategory.Value;
        var nodeId = item.EquipSlotCategory.RowId == 0 ? 11u :
            slot.MainHand > 0 || slot.OffHand > 0 ? 8u :
            slot.Head > 0 || slot.Body > 0 || slot.Gloves > 0 || slot.Legs > 0 || slot.Feet > 0 ? 9u : 10u;
        // This addon uses Weapons/Armor/Accessories/Others radios 8..11.
        // Dispatch the actual registered click, then validate the resulting rows.
        var node = addon->GetNodeById(nodeId);
        var tab = node == null || !node->IsVisible() ? null : node->GetAsAtkComponentRadioButton();
        if (tab == null || !tab->IsEnabled) return false;
        selectedItem = offer.ItemId; // Consume before dispatch.
        Plugin.Log.Information("[MOGTOME][BV] selecting trader category node={Node}; item={Item}", nodeId, offer.ItemId);
        return ClickRegisteredButton(node);
    }

    internal static bool ClickRegisteredButton(AtkResNode* node)
    {
        if (node == null) return false;
        var registered = node->AtkEventManager.Event;
        while (registered != null && registered->State.EventType != AtkEventType.ButtonClick) registered = registered->NextEvent;
        if (registered == null)
        {
            registered = node->AtkEventManager.Event;
            while (registered != null && registered->State.EventType is not (AtkEventType.MouseClick or AtkEventType.MouseDown or AtkEventType.MouseUp))
                registered = registered->NextEvent;
        }
        if (registered == null || registered->Listener == null || registered->Listener->VirtualTable == null ||
            registered->Listener->VirtualTable->ReceiveEvent == null || registered->Param > int.MaxValue ||
            registered->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) return false;
        var click = *registered;
        var data = new AtkEventData();
        click.Listener->ReceiveEvent(click.State.EventType, (int)click.Param, &click, &data);
        return true;
    }

    internal static void LogOfferMismatch(BlundervilleOffer offer, string addonName)
    {
        var agent = AgentShop.Instance();
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(addonName).Address;
        Plugin.Log.Warning("[MOGTOME][BV] trader validation mismatch; addon={Addon}; values={Values}; agentActive={Active}; ready={Ready}; expectedShop={ExpectedShop}; nativeShop={NativeShop}; item={Item}; receive={Receive}; price={Price}; receives={Receives}; costs={Costs}",
            addonName, addon == null ? 0 : addon->AtkValuesCount, agent != null && agent->IsAgentActive(),
            agent != null && agent->IsAddonReady(), offer.ShopName, agent == null ? string.Empty : agent->ShopName.ToString(),
            offer.ItemId, offer.ReceiveCount, offer.Price, agent == null ? 0 : agent->ItemReceiveCount, agent == null ? 0 : agent->ItemCostCount);
        if (addon == null || addon->AtkValues == null || agent == null) return;
        foreach (var index in new[] { 3, 4, 87, 456, 1066, 1310 })
            if (index < addon->AtkValuesCount)
                Plugin.Log.Warning("[MOGTOME][BV] trader field index={Index}; type={Type}; number={Number}", index, addon->AtkValues[index].Type, Number(addon->AtkValues[index]));
        if (agent->ItemReceive == null || agent->ItemReceiveCount is < 1 or > 122) return;
        foreach (var entry in agent->ItemReceiveSpan)
            if (entry.ItemId == offer.ItemId)
                Plugin.Log.Warning("[MOGTOME][BV] trader receive item={Item}; count={Count}", entry.ItemId, entry.ItemCount);
    }

    private static long Number(AtkValue value) => value.Type switch
    { AtkValueType.UInt => value.UInt, AtkValueType.Int => value.Int, _ => -1 };

    internal static void LogPurchaseMismatch(BlundervilleOffer offer)
    {
        foreach (var name in new[] { "SelectYesno", "ShopExchangeCurrencyDialog", "ShopExchangeItemDialog" })
        {
            var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
            if (addon == null || !addon->IsVisible) continue;
            Plugin.Log.Warning("[MOGTOME][BV] purchase confirmation mismatch; addon={Addon}; expectedItem={Item}; quantity={Quantity}; price={Price}; values={Values}; ready={Ready}",
                name, offer.ItemId, offer.ReceiveCount, offer.Price, addon->AtkValuesCount, addon->IsReady);
            if (name == "SelectYesno")
            {
                var yesno = (AddonSelectYesno*)addon;
                if (yesno->PromptText != null)
                    Plugin.Log.Warning("[MOGTOME][BV] owned purchase prompt={Prompt}", GameText.ReadVisibleText(yesno->PromptText->NodeText.AsSpan()));
            }
            if (addon->AtkValues == null) continue;
            for (var i = 0; i < Math.Min(64, (int)addon->AtkValuesCount); i++)
                if (addon->AtkValues[i].Type is AtkValueType.Int or AtkValueType.UInt)
                    Plugin.Log.Warning("[MOGTOME][BV] confirmation field index={Index}; type={Type}; number={Number}", i, addon->AtkValues[i].Type, Number(addon->AtkValues[i]));
        }
    }

    public static bool ConfirmPurchase(BlundervilleOffer offer, string ownedAddon)
    {
        if (!TryValidateOffer(offer, out var currentAddon, out _) || currentAddon != ownedAddon) return false;
        if (Visible("SelectYesno"))
        {
            var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
            if (addon == null || addon->PromptText == null || addon->AtkValues == null || addon->AtkValuesCount < 16 ||
                addon->AtkValues[12].Type != AtkValueType.Int || !addon->CollectibleAtkValuesAvailable) return false;
            var payload = addon->CollectibleTypedAtkValues;
            if (payload->ItemId.Type != AtkValueType.UInt || payload->ItemId.UInt != offer.ItemId) return false;
            var prompt = GameText.ReadVisibleText(addon->PromptText->NodeText.AsSpan());
            var numbers = Regex.Matches(prompt, @"[0-9](?:[0-9,.\u00A0\u202F]*[0-9])?");
            var price = offer.Price.ToString(CultureInfo.InvariantCulture);
            if (numbers.Count != 1 || new string(numbers[0].Value.Where(char.IsDigit).ToArray()) != price) return false;
            return ClickSelectYesno(addon, yes: true);
        }
        var dialogName = ownedAddon == "ShopExchangeCurrency" ? "ShopExchangeCurrencyDialog" : "ShopExchangeItemDialog";
        if (!Visible(dialogName)) return false;
        // The item-exchange quantity dialog has no verified MGF quantity layout in this adapter.
        if (ownedAddon != "ShopExchangeCurrency") return false;
        // Revalidate the owned shop before accepting its quantity dialog. Each submission is one transaction.
        if (!TryValidateOffer(offer, out var addonName, out _) || addonName != ownedAddon) return false;
        var dialog = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(dialogName).Address;
        if (dialog == null || dialog->UldManager.NodeList == null) return false;
        if (ownedAddon == "ShopExchangeCurrency")
        {
            if (dialog->UldManager.NodeListCount <= 8 || dialog->UldManager.NodeList[8] == null) return false;
            var input = dialog->UldManager.NodeList[8]->GetAsAtkComponentNumericInput();
            if (input == null || input->Value != offer.ReceiveCount) return false;
        }
        var nodeId = ownedAddon == "ShopExchangeCurrency" ? 17u : 18u;
        var button = dialog->GetComponentButtonById(nodeId);
        if (button == null || !button->IsEnabled) return false;
        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null) return false;
        var registered = owner->AtkResNode.AtkEventManager.Event;
        while (registered != null && registered->State.EventType != AtkEventType.ButtonClick) registered = registered->NextEvent;
        if (registered == null || registered->Listener == null || registered->Listener->VirtualTable == null ||
            registered->Listener->VirtualTable->ReceiveEvent == null || registered->Param > int.MaxValue ||
            registered->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) return false;
        var click = *registered;
        var data = new AtkEventData();
        click.Listener->ReceiveEvent(click.State.EventType, (int)click.Param, &click, &data);
        return true;
    }

    public static void CloseOwnedShop(string addonName)
    {
        foreach (var name in new[] { "ShopExchangeCurrencyDialog", "ShopExchangeItemDialog", addonName })
            if (!string.IsNullOrEmpty(name) && Visible(name)) Callback(name, -1);
    }

    public static bool CancelOwnedPurchase(BlundervilleOffer offer)
    {
        var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->IsVisible) return true;
        if (addon->AtkValues == null || addon->AtkValuesCount < 16 || addon->AtkValues[12].Type != AtkValueType.Int ||
            !addon->CollectibleAtkValuesAvailable || addon->CollectibleTypedAtkValues->ItemId.Type != AtkValueType.UInt ||
            addon->CollectibleTypedAtkValues->ItemId.UInt != offer.ItemId) return false;
        if (!ClickSelectYesno(addon, yes: false)) return false;
        // An already accepted popup can outlive its closed shop. Re-fetch and
        // close only the same identified item popup after its native No response.
        var remaining = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (remaining != null && remaining->IsVisible && remaining->AtkValues != null && remaining->AtkValuesCount >= 16 &&
            remaining->AtkValues[12].Type == AtkValueType.Int && remaining->CollectibleAtkValuesAvailable &&
            remaining->CollectibleTypedAtkValues->ItemId.Type == AtkValueType.UInt &&
            remaining->CollectibleTypedAtkValues->ItemId.UInt == offer.ItemId)
        {
            remaining->Close(true);
            Plugin.Log.Information("[MOGTOME][BV] closed owned cancelled item popup={Item}", offer.ItemId);
        }
        return true;
    }


    private static bool ClickSelectYesno(AddonSelectYesno* addon, bool yes)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady) return false;
        var button = yes ? addon->YesButton : addon->NoButton;
        if (button == null || !button->IsEnabled || button->AtkComponentBase.OwnerNode == null) return false;
        var dispatched = ClickRegisteredButton(&button->AtkComponentBase.OwnerNode->AtkResNode);
        Plugin.Log.Information("[MOGTOME][BV] owned confirmation button={Button}; dispatched={Dispatched}", yes ? "Yes" : "No", dispatched);
        return dispatched;
    }

    public static bool ConfirmPartyLeave()
    {
        // Resolve exact static party-leave prompt rows from English, then compare the client's own sheet text.
        var ids = Plugin.DataManager.GetExcelSheet<Addon>(ClientLanguage.English)
            .Where(row => row.Text.ToString() is "Leave the party?" or "Leave party?").Select(row => row.RowId);
        foreach (var id in ids)
        {
            var expected = Plugin.SeStringEvaluator.EvaluateFromAddon(id, language: Plugin.ClientState.ClientLanguage).ToString();
            var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
            if (addon != null && addon->IsVisible && addon->PromptText != null &&
                GameText.MatchesEvaluated(GameText.ReadVisibleText(addon->PromptText->NodeText.AsSpan()), expected))
                return ClickSelectYesno(addon, yes: true);
        }
        return false;
    }
}
