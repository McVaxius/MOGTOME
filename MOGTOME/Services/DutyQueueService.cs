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
    private readonly DutyAutomationService dutyAutomationService;
    private readonly AutomatonIPC automatonIPC;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;

    private DateTime lastQueueAttempt = DateTime.MinValue;
    private DateTime lastCommenceClickTime = DateTime.MinValue;
    private DateTime lastDeclineClickTime = DateTime.MinValue;
    private const float QueueCooldown = 10.0f;

    public DutyQueueService(
        IPluginLog log, Configuration config, DutyState state,
        DutyAutomationService dutyAutomationService, AutomatonIPC automatonIPC,
        ICommandManager commandManager, ICondition condition)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.dutyAutomationService = dutyAutomationService;
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
            ? $"[MOGTOME][DutyQueue] Force-queueing via {dutyAutomationService.ActiveBackendDisplayName}: {dutyName}"
            : $"[MOGTOME][DutyQueue] Queueing via {dutyAutomationService.ActiveBackendDisplayName}: {dutyName}");
        dutyAutomationService.QueueDuty(isPraetorium);
    }

    public void DisableAutoQueueForRepair()
    {
        if (!state.AutoQueueDisabledForRepair)
        {
            automatonIPC.DisableAutoQueue();
            state.AutoQueueDisabledForRepair = true;
            log.Information("[MOGTOME][DutyQueue] AutoQueue disabled for repair");
        }
    }

    public void ApplyAutoQueuePolicyAtStart()
        => ApplyAutoQueuePolicyForCurrentRole("startup");

    public void ApplyAutoQueuePolicyForCurrentRole(string reason)
    {
        if (state.AutoQueueDisabledForRepair)
        {
            automatonIPC.DisableAutoQueue();
            log.Information($"[MOGTOME][DutyQueue] AutoQueue kept disabled while repair is active ({reason})");
            return;
        }

        if (state.IsPartyLeader)
        {
            automatonIPC.EnableAutoQueue();
            log.Information($"[MOGTOME][DutyQueue] AutoQueue enabled for leader ({reason})");
        }
        else
        {
            automatonIPC.DisableAutoQueue();
            log.Information($"[MOGTOME][DutyQueue] AutoQueue disabled for non-leader ({reason})");
        }
    }

    public void RestoreAutoQueueAfterRepair()
    {
        if (!state.AutoQueueDisabledForRepair)
            return;

        state.AutoQueueDisabledForRepair = false;
        ApplyAutoQueuePolicyForCurrentRole("repair complete");
    }

    public void EnsureAutoQueueDisabledOnStop()
    {
        state.AutoQueueDisabledForRepair = false;
        automatonIPC.DisableAutoQueue();
        log.Information("[MOGTOME][DutyQueue] Ensured AutoQueue is disabled on stop");
    }

    /// <summary>
    /// Auto-accept duty pop for any party member that should accept.
    /// Repair flow owns the popup when repair is active.
    /// Uses the known ContentsFinderConfirm Commence callback.
    /// </summary>
    public void AutoAcceptDuty()
    {
        // Don't accept if in the middle of repair
        if (state.AutoQueueDisabledForRepair) return;

        // Handle ContentsFinderConfirm popup (duty commence dialog) like FrenRider
        if (GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
        {
            var now = DateTime.UtcNow;
            if ((now - lastCommenceClickTime).TotalSeconds > 2) // Rate limit to prevent spam
            {
                lastCommenceClickTime = now;
                log.Information("[MOGTOME][DutyQueue] Clicking Commence on ContentsFinderConfirm");
                
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
        log.Warning("[MOGTOME][DutyQueue] Declining ContentsFinderConfirm while repair is active");
        GameHelpers.FireAddonCallback("ContentsFinderConfirm", true, -2);
        return true;
    }
}
