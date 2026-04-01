using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Services;

public class DutyQueueService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly AutomatonIPC automatonIPC;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;

    private DateTime lastQueueAttempt = DateTime.MinValue;
    private DateTime lastCommenceClickTime = DateTime.MinValue;
    private DateTime lastDeclineClickTime = DateTime.MinValue;
    private const float QueueCooldown = 10.0f;

    public DutyQueueService(
        IPluginLog log, Configuration config, DutyState state,
        AutoDutyIPC autoDutyIPC, AutomatonIPC automatonIPC,
        ICommandManager commandManager, ICondition condition)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.autoDutyIPC = autoDutyIPC;
        this.automatonIPC = automatonIPC;
        this.commandManager = commandManager;
        this.condition = condition;
    }

    public void TryQueue(bool isPraetorium)
        => TryQueueInternal(isPraetorium, ignoreCooldown: false);

    public void ForceQueue(bool isPraetorium)
        => TryQueueInternal(isPraetorium, ignoreCooldown: true);

    private void TryQueueInternal(bool isPraetorium, bool ignoreCooldown)
    {
        if (state.IsInDuty) return;
        if (!state.IsPartyLeader) return;

        var now = DateTime.UtcNow;
        if (!ignoreCooldown && (now - lastQueueAttempt).TotalSeconds < QueueCooldown) return;
        lastQueueAttempt = now;

        // Condition[34] = BoundByDuty
        if (condition[34]) return;

        var dutyName = isPraetorium ? "The Praetorium" : "The Porta Decumana";
        log.Information(ignoreCooldown
            ? $"[DutyQueue] Force-queueing via AutoDuty: {dutyName}"
            : $"[DutyQueue] Queueing via AutoDuty: {dutyName}");
        autoDutyIPC.QueueDuty(dutyName);
    }

    public void DisableAutoQueueForRepair()
    {
        if (!state.AutoQueueDisabledForRepair)
        {
            automatonIPC.DisableAutoQueue();
            state.AutoQueueDisabledForRepair = true;
            log.Information("[DutyQueue] AutoQueue disabled for repair");
        }
    }

    public void EnableAutoQueueAfterRepair()
    {
        if (state.AutoQueueDisabledForRepair)
        {
            automatonIPC.EnableAutoQueue();
            state.AutoQueueDisabledForRepair = false;
            log.Information("[DutyQueue] AutoQueue re-enabled after repair");
        }
    }

    /// <summary>
    /// Auto-accept duty pop for non-leaders.
    /// All party members should accept unless engaged in repair.
    /// Uses FrenRider's ContentsFinderConfirm approach.
    /// </summary>
    public void AutoAcceptDuty()
    {
        // Only auto-accept if not the party leader
        if (state.IsPartyLeader) return;

        // Don't accept if in the middle of repair
        if (state.AutoQueueDisabledForRepair) return;

        // Handle ContentsFinderConfirm popup (duty commence dialog) like FrenRider
        if (GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
        {
            var now = DateTime.UtcNow;
            if ((now - lastCommenceClickTime).TotalSeconds > 2) // Rate limit to prevent spam
            {
                lastCommenceClickTime = now;
                log.Information("[DutyQueue] Clicking Commence on ContentsFinderConfirm (non-leader)");
                
                // Fire commence callback - typically callback index 8 = Commence button
                GameHelpers.FireAddonCallback("ContentsFinderConfirm", true, 8);
            }
        }
    }

    public bool CancelDutyPopForRepair()
    {
        if (!GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
            return false;

        var now = DateTime.UtcNow;
        if ((now - lastDeclineClickTime).TotalSeconds <= 2)
            return false;

        lastDeclineClickTime = now;
        log.Warning("[DutyQueue] Declining ContentsFinderConfirm while repair is active");
        GameHelpers.FireAddonCallback("ContentsFinderConfirm", true, -2);
        return true;
    }
}
