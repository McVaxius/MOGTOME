using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MOGTOME.Services.Keybound;

internal sealed unsafe class KeyboundLoopGame(Plugin plugin) : IKeyboundLoopGame
{
    private readonly BlundervilleGameAdapter interactions = new(plugin);
    private bool movement, travel, gateSubmitted;
    private ulong gate;
    private long nextInteraction;
    private const uint Saucer = 144, Registrar = 1058751, Gatekeeper = 1058754, SquareWarp = 131633;
    private static bool Visible(string name) => BlundervilleGameAdapter.Visible(name);
    private static bool Matches(ContentsId entry) => entry.ContentType == ContentsType.Regular && entry.Id == KeyboundEventLoop.Duty;
    public KeyboundLoopFrame Read()
    {
        var finder = ContentsFinder.Instance();
        var queue = finder == null ? null : finder->GetQueueInfo();
        var owned = false;
        if (queue != null)
        {
            // Every registered entry must identify KBB; never accept/cancel a mixed queue.
            var count = 0; var allMatch = true;
            foreach (var entry in queue->QueuedEntries)
            { if (entry.Id == 0) continue; count++; allMatch &= Matches(entry); }
            owned = count > 0 && allMatch;
        }
        var popped = queue != null && Matches(queue->PoppedQueueEntry);
        var territory = Plugin.ClientState.TerritoryType;
        var entryDialog = Addon("KTGEnterDialogue");
        var results = Addon("KTGResult");
        return new(territory, Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51],
            Plugin.Condition[ConditionFlag.InDutyQueue], owned, Visible("ContentsFinderConfirm"), popped,
            Visible("KTGEnterDialogue"), results != null, results != null && results->AtkValuesCount >= 78 && FindButton(results, 17853, 0x30) != null,
            Plugin.Condition[ConditionFlag.BoundByDuty] && territory != KeyboundEventLoop.Arena, BlundervilleGameAdapter.Role(), entryDialog != null);
    }
    public bool EnsureLobby()
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (territory == KeyboundEventLoop.Lobby) { travel = gateSubmitted = false; gate = 0; return true; }
        if (territory != Saucer)
        {
            if (!travel)
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return false;
                var aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>().FirstOrDefault(row => row.IsAetheryte && row.Territory.RowId == Saucer);
                if (aetheryte.RowId == 0) throw new InvalidOperationException("Gold Saucer travel unavailable");
                travel = true;
                if (!Plugin.CommandManager.ProcessCommand("/li " + aetheryte.PlaceName.Value.Name.ToString()))
                    throw new InvalidOperationException("Gold Saucer route rejected");
            }
            return false;
        }
        if (travel && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return false;
        travel = false;
        if (Visible("SelectYesno"))
        {
            var dialog = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
            var warp = Plugin.DataManager.GetExcelSheet<Warp>().GetRow(SquareWarp);
            if (gate == 0 || Plugin.TargetManager.Target?.GameObjectId != gate || dialog == null || dialog->PromptText == null ||
                !GameText.MatchesEvaluated(GameText.ReadVisibleText(dialog->PromptText->NodeText.AsSpan()), warp.Question.ToString()))
                throw new InvalidOperationException("Keybound Square prompt changed");
            if (!gateSubmitted)
            {
                gateSubmitted = true;
                if (!GameHelpers.ClickYesIfVisible()) throw new InvalidOperationException("Square entry rejected");
            }
            return false;
        }
        if (!gateSubmitted) Approach(Gatekeeper);
        return false;
    }
    public bool ApproachRegistrar()
    {
        if (BlundervilleGameAdapter.Role() == Models.BlundervilleRole.Member) return false;
        return Approach(Registrar);
    }
    private bool Approach(uint baseId)
    {
        if (Visible("SelectString") || Visible("SelectIconString") || Visible("Talk") || Visible("SelectYesno"))
            throw new InvalidOperationException("Unexpected dialog before KBB interaction");
        if (Environment.TickCount64 < nextInteraction) return false;
        var npc = Plugin.ObjectTable.FirstOrDefault(value => value.ObjectKind == ObjectKind.EventNpc && value.BaseId == baseId);
        if (npc == null) return false;
        if (!interactions.ApproachAndInteract(npc, ref movement)) return false;
        if (baseId == Gatekeeper) gate = npc.GameObjectId;
        nextInteraction = Environment.TickCount64 + 3000;
        return true;
    }
    public bool Register() => SubmitRegistration(Plugin.ClientState.TerritoryType,
        Addon("KTGEnterDialogue") != null, BlundervilleGameAdapter.Role(), GameHelpers.TryFireAddonCallback);

    internal static bool SubmitRegistration(uint territory, bool entryReady, Models.BlundervilleRole role,
        Func<string, bool, object[], bool> callback)
        // Observed registration capture: KTGEnterDialogue, update visibility 1, one Int value 0.
        // This dedicated ready dialog identifies the action even when it was open before Start or
        // the game cleared the NPC target. Party-role admission remains owned by the event loop.
        => territory == KeyboundEventLoop.Lobby && entryReady &&
            role is Models.BlundervilleRole.Solo or Models.BlundervilleRole.Leader &&
            callback("KTGEnterDialogue", true, [0]);
    public bool Commence()
    {
        var finder = ContentsFinder.Instance(); var queue = finder == null ? null : finder->GetQueueInfo();
        return queue != null && Matches(queue->PoppedQueueEntry) && BlundervilleGameAdapter.Callback("ContentsFinderConfirm", 8);
    }
    public bool LeaveResults() => Plugin.ClientState.TerritoryType == KeyboundEventLoop.Arena && Click("KTGResult", 17853, 0x30);
    public void KeepActive() => KeyboundActivity.Pulse();
    public void CancelQueue()
    {
        var frame = Read();
        if (frame.PartyRole == Models.BlundervilleRole.Member) return;
        if (frame.Pop && frame.PopMatches) BlundervilleGameAdapter.Callback("ContentsFinderConfirm", 9);
        else if (frame.Queued && frame.QueueMatches) ContentsFinder.Instance()->GetQueueInfo()->CancelQueue();
    }
    public void StopMovement()
    {
        if (movement) { movement = false; plugin.VNavIPC.Stop(); }
        if (travel) { travel = false; Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
    }
    private static AtkUnitBase* Addon(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }
    private static bool Click(string name, uint label, uint nodeId = 0)
    {
        var addon = Addon(name); var button = addon == null ? null : FindButton(addon, label, nodeId);
        return button != null && BlundervilleGameAdapter.ClickRegisteredButton(&button->OwnerNode->AtkResNode);
    }
    private static AtkComponentButton* FindButton(AtkUnitBase* addon, uint label, uint nodeId)
    {
        var expected = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(label).Text.ToString();
        AtkComponentButton* match = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible() || nodeId != 0 && node->NodeId != nodeId || (ushort)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.Button) continue;
            var button = (AtkComponentButton*)component;
            if (button->OwnerNode == null || !button->IsEnabled || button->ButtonTextNode == null ||
                !GameText.MatchesEvaluated(GameText.ReadVisibleText(button->ButtonTextNode->NodeText.AsSpan()), expected)) continue;
            if (match != null) return null; // An ambiguous label is not an action route.
            match = button;
        }
        return match;
    }
}
