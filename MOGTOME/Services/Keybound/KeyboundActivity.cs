using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MOGTOME.Services.Keybound;

/// <summary>KBB has a separate 60-second input-agent idle counter; ordinary AFK timers alone are insufficient.</summary>
internal static unsafe class KeyboundActivity
{
    private static bool reportedMatch, reportedMismatch;
    private static int samples;
    private static long sampleAt;
    // Published native layouts: AtmoOmen/FFXIVClientStructs.Expansion, AgentKTGInput and AgentKTGHud.
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct KtgInput
    {
        [FieldOffset(0)] internal AgentInterface Agent;
        [FieldOffset(40)] internal KtgHud* Hud;
        [FieldOffset(80)] internal float IdleSeconds;
        [FieldOffset(84)] internal float KeyHoldSeconds;
    }
    [StructLayout(LayoutKind.Explicit, Size = 752)]
    private struct KtgHud
    {
        [FieldOffset(0)] internal AgentInterface Agent;
        [FieldOffset(640)] internal uint InputAddonId;
        [FieldOffset(673)] internal bool IsShown;
        [FieldOffset(674)] internal bool HasDirector;
    }
    // Published InstanceContentKeyboundBrawler layout, including its native-read tail fields.
    [StructLayout(LayoutKind.Explicit, Size = 8184)]
    private struct KtgDirector
    {
        [FieldOffset(8176)] internal int GamePhase;
        [FieldOffset(8180)] internal int PhaseImageIndex;
    }
    internal static bool Matches(uint territory, bool hudShown, bool hasDirector, bool hudMatches,
        uint expectedAddon, uint actualAddon, float idle)
        => territory == KeyboundEventLoop.Arena && hudShown && hasDirector && hudMatches && expectedAddon != 0 &&
           expectedAddon == actualAddon && float.IsFinite(idle) && idle >= 0 && idle <= 120;

    internal static int StageFromImageIndex(int index) => index is >= 0 and <= 2 ? index + 1 : 0;

    internal static int ReadStage(uint addonId)
    {
        var module = AgentModule.Instance();
        if (module == null) return 0;
        var input = (KtgInput*)module->GetAgentByInternalId((AgentId)398);
        var hud = (KtgHud*)module->GetAgentByInternalId((AgentId)397);
        if (input == null || hud == null || !Matches(Plugin.ClientState.TerritoryType,
            hud->IsShown, hud->HasDirector, input->Hud == hud, hud->InputAddonId, addonId, input->IdleSeconds)) return 0;
        var framework = EventFramework.Instance();
        var director = framework == null ? null : framework->GetInstanceContentDirector();
        if (director == null || (byte)director->InstanceContentType != 23) return 0;
        var keybound = (KtgDirector*)director;
        // Expansion revision 433c6def4d14f9e09c4ef8c9a8c4366453fc64eb: active phases 4–6,
        // phase images 1620–1622 at index 0–2. Read the director, not the input agent's stale cache.
        return keybound->GamePhase is >= 4 and <= 6 ? StageFromImageIndex(keybound->PhaseImageIndex) : 0;
    }

    internal static void BeginSession()
    {
        reportedMatch = reportedMismatch = false;
        samples = 0;
        sampleAt = 0;
    }

    internal static void Pulse()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ClientState.TerritoryType != KeyboundEventLoop.Arena) return;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("KTGInput").Address;
        var module = AgentModule.Instance();
        if (module == null || addon == null || !addon->IsVisible || !addon->IsReady) return;
        var input = (KtgInput*)module->GetAgentByInternalId((AgentId)398);
        var hud = (KtgHud*)module->GetAgentByInternalId((AgentId)397);
        if (input == null || hud == null) return;
        if (!Matches(Plugin.ClientState.TerritoryType,
            hud->IsShown, hud->HasDirector, input->Hud == hud,
            hud->InputAddonId, addon->Id, input->IdleSeconds))
        {
            if (!reportedMismatch)
            {
                reportedMismatch = true;
                Plugin.Log?.Warning("[MOGTOME][KBB] Activity guard blocked: shown={Shown}, director={Director}, sameHud={SameHud}, sameAddon={SameAddon}, idle={Idle}",
                    hud->IsShown, hud->HasDirector, input->Hud == hud, hud->InputAddonId == addon->Id, input->IdleSeconds);
            }
            return;
        }
        if (!reportedMatch)
        {
            reportedMatch = true;
            Plugin.Log?.Information("[MOGTOME][KBB] Activity guard matched; refreshing the native idle float each owned arena update.");
        }
        var timers = InputTimerModule.Instance();
        var now = Environment.TickCount64;
        if (timers != null && samples < 2 && now >= sampleAt)
        {
            samples++;
            sampleAt = now + 30_000;
            Plugin.Log?.Information("[MOGTOME][KBB] Activity sample {Sample}/2 BEFORE reset: idle={Idle}, keyHold={KeyHold}, moduleTerritory={Territory}, input={Input}, content={Content}, afk={Afk}",
                samples, input->IdleSeconds, input->KeyHoldSeconds, timers->TerritoryTypeId,
                timers->InputTimer, timers->ContentInputTimer, timers->AfkTimer);
        }
        // Match upstream's eight-byte clear at 0x50, expressed as its two published float fields.
        // The current client territory and matched HUD/addon already scope this operation to KBB.
        input->IdleSeconds = input->KeyHoldSeconds = 0;
        ResetInputTimers(timers);
    }

    internal static void ResetInputTimers(InputTimerModule* timers)
    {
        if (timers == null) return;
        // The module's cached territory is not an additional admission condition in upstream.
        // Skipping here leaves posted-key gameplay exposed to the general duty inactivity check.
        timers->ContentInputTimer = timers->InputTimer = 0;
        timers->AfkTimer = 0;
        ResetTimers(timers->ControllerInputTimers);
        ResetTimers(timers->MouseInputTimers);
    }

    // Upstream 1.4.3 also identified named controller/mouse counters. Keep these writes inside the
    // same matched arena/HUD/addon guard; preserve history and unknown fields.
    internal static void ResetTimers(Span<InputTimerModule.InputTimerData> timers)
    {
        for (var i = 0; i < timers.Length; i++)
            timers[i].Timer = 0;
    }
}
