using System;
using MOGTOME.Localization;
using MOGTOME.Models;

namespace MOGTOME.Services.Keybound;

internal readonly record struct KeyboundLoopFrame(uint Territory, bool Transitioning, bool Queued,
    bool QueueMatches, bool Pop, bool PopMatches, bool Entry, bool Results, bool ResultsReady, bool OtherDuty,
    BlundervilleRole PartyRole = BlundervilleRole.Solo, bool EntryReady = true);

internal interface IKeyboundLoopGame
{
    KeyboundLoopFrame Read();
    bool EnsureLobby();
    bool ApproachRegistrar();
    bool Register();
    bool Commence();
    bool LeaveResults();
    void KeepActive();
    void CancelQueue();
    void StopMovement();
}

/// <summary>One owned registration/result action at a time, with observable transitions before repeats.</summary>
internal sealed class KeyboundEventLoop(IKeyboundLoopGame game, KeyboundBrawlerService typer)
{
    internal const uint Arena = 1359, Lobby = 1360, Duty = 1095;
    private bool active, entered, registered, commenced, resultSubmitted, resultsSeen;
    private long stageStarted, heartbeatAt;
    private KeyboundPartyRole partyRole;
    internal UiText Status { get; private set; } = Ui.M("BV_Idle");
    internal bool IsActive => active;
    internal void Start(long now, KeyboundPartyRole role = KeyboundPartyRole.Leader)
    {
        partyRole = role;
        active = true; entered = registered = commenced = resultSubmitted = resultsSeen = false;
        stageStarted = now; heartbeatAt = now;
        Status = Ui.M("KB_Queueing");
    }
    internal void Update(long now)
    {
        typer.SuppressGameplay = false;
        if (!active) return;
        try
        {
            if (typer.StopRequested || !typer.CanContinueSession || !typer.IsRunning && !typer.Finished) { Stop(); return; }
            var state = game.Read();
            if (state.Transitioning) return;
            if (state.OtherDuty || state.Queued && !state.QueueMatches || state.Pop && !state.PopMatches)
            { Fail("KB_AutomationBusy"); return; }
            if (state.Territory == Arena)
            {
                if (!entered) { entered = true; stageStarted = now; }
                if (now - stageStarted > 1_800_000) { Fail("BV_Timeout"); return; }
                if (now >= heartbeatAt)
                { game.KeepActive(); heartbeatAt = now + 30_000; }
                if (state.Results)
                {
                    if (!resultsSeen) { resultsSeen = true; stageStarted = now; }
                    typer.SuppressGameplay = true;
                    Status = Ui.M("KB_Returning");
                    if (state.ResultsReady && !resultSubmitted)
                    {
                        typer.ObserveCompletion(Arena);
                        resultSubmitted = true; // Consume before native dispatch.
                        stageStarted = now;
                        game.StopMovement();
                        if (!game.LeaveResults()) Fail("BV_UiMismatch");
                    }
                    if (now - stageStarted > 30_000) Fail("BV_Timeout");
                }
                else Status = typer.Status;
                return;
            }
            if (entered)
            {
                // A return alone cannot prove a finished game or earn a run count.
                if (!resultSubmitted) { Fail("BV_UiMismatch"); return; }
                entered = registered = commenced = resultSubmitted = resultsSeen = false;
                stageStarted = now;
            }
            if (typer.Finished) { active = false; game.StopMovement(); return; }
            if (now - stageStarted > (registered || state.Queued || partyRole == KeyboundPartyRole.Member ? 600_000 : 180_000)) { Fail("BV_Timeout"); return; }
            var roleMatches = partyRole == KeyboundPartyRole.Member
                ? state.PartyRole == BlundervilleRole.Member
                : state.PartyRole is BlundervilleRole.Solo or BlundervilleRole.Leader;
            if (!roleMatches)
            { Status = Ui.M("KB_WaitPartyRole"); return; }
            if (state.Pop)
            {
                if (!commenced)
                {
                    commenced = true;
                    game.StopMovement();
                    if (!game.Commence()) Fail("BV_UiMismatch");
                }
                Status = Ui.M("BV_WaitEntry"); return;
            }
            if (state.Queued || registered || commenced) { Status = Ui.M("BV_WaitEntry"); return; }
            // Only the leader registers in the Square. Members can receive the matching group pop
            // from their current territory; waiting must never start travel or touch the registrar.
            if (partyRole == KeyboundPartyRole.Member)
            { Status = Ui.M("BV_MemberWait"); return; }
            Status = Ui.M("KB_Queueing");
            if (!game.EnsureLobby()) return;
            if (state.Entry)
            {
                if (!state.EntryReady) return; // Visible during setup is not a rejected registration.
                registered = true; stageStarted = now;
                game.StopMovement();
                if (!game.Register()) Fail("BV_UiMismatch");
            }
            else game.ApproachRegistrar();
        }
        catch (Exception ex)
        {
            Fail("BV_ActionFailed");
            Plugin.Log?.Warning(ex, "[MOGTOME][KBB] event loop failed");
        }
    }
    private void Fail(string key) { Stop(); typer.StopWithReason(key); Status = Ui.M(key); }
    internal void Stop()
    {
        active = false;
        typer.SuppressGameplay = false;
        try { if (partyRole == KeyboundPartyRole.Leader && (registered || commenced)) game.CancelQueue(); }
        catch (Exception ex) { Plugin.Log?.Warning(ex, "[MOGTOME][KBB] owned queue cancellation failed"); }
        try { game.StopMovement(); }
        catch (Exception ex) { Plugin.Log?.Warning(ex, "[MOGTOME][KBB] owned movement cleanup failed"); }
        registered = commenced = false;
    }
}
