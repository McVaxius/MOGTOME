using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MOGTOME.Services.Keybound;

/// <summary>KBB has a separate 60-second input-agent idle counter; ordinary AFK timers alone are insufficient.</summary>
internal static unsafe class KeyboundActivity
{
    // Published native layouts: AtmoOmen/FFXIVClientStructs.Expansion, AgentKTGInput and AgentKTGHud.
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct KtgInput
    {
        [FieldOffset(0)] internal AgentInterface Agent;
        [FieldOffset(40)] internal KtgHud* Hud;
        [FieldOffset(80)] internal float IdleSeconds;
    }
    [StructLayout(LayoutKind.Explicit, Size = 752)]
    private struct KtgHud
    {
        [FieldOffset(0)] internal AgentInterface Agent;
        [FieldOffset(640)] internal uint InputAddonId;
    }
    internal static bool Matches(uint territory, bool inputActive, bool hudActive, bool hudMatches,
        uint expectedAddon, uint actualAddon, float idle)
        => territory == KeyboundEventLoop.Arena && inputActive && hudActive && hudMatches && expectedAddon != 0 &&
           expectedAddon == actualAddon && float.IsFinite(idle) && idle >= 0 && idle <= 120;

    internal static void Pulse()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != KeyboundEventLoop.Arena) return;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("KTGInput").Address;
        var module = AgentModule.Instance();
        if (module == null || addon == null || !addon->IsVisible || !addon->IsReady) return;
        var input = (KtgInput*)module->GetAgentByInternalId((AgentId)398);
        var hud = (KtgHud*)module->GetAgentByInternalId((AgentId)397);
        if (input == null || hud == null || !Matches(Plugin.ClientState.TerritoryType,
            input->Agent.IsAgentActive(), hud->Agent.IsAgentActive(), input->Hud == hud,
            hud->InputAddonId, addon->Id, input->IdleSeconds)) return;
        input->IdleSeconds = 0;
        var timers = InputTimerModule.Instance();
        if (timers == null || timers->TerritoryTypeId != KeyboundEventLoop.Arena) return;
        timers->ContentInputTimer = timers->InputTimer = 0;
        if (timers->AfkTimer >= 0) timers->AfkTimer = 0;
    }
}
