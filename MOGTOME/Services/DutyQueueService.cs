using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Services;

public class DutyQueueService
{
    private readonly IPluginLog log;
    private readonly DutyState state;
    private readonly DutyAutomationService dutyAutomationService;
    private readonly ICondition condition;

    private DateTime lastQueueAttempt = DateTime.MinValue;
    private DateTime lastCommenceClickTime = DateTime.MinValue;
    private DateTime lastDeclineClickTime = DateTime.MinValue;
    private const float QueueCooldown = 10.0f;

    public DutyQueueService(
        IPluginLog log, DutyState state,
        DutyAutomationService dutyAutomationService, ICondition condition)
    {
        this.log = log;
        this.state = state;
        this.dutyAutomationService = dutyAutomationService;
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
        var queuePath = dutyAutomationService.UseAdsExperimental
            ? "MOGTOME ContentsFinder flow"
            : dutyAutomationService.ActiveBackendDisplayName;
        log.Information(ignoreCooldown
            ? $"[MOGTOME][DutyQueue] Force-queueing via {queuePath}: {dutyName}"
            : $"[MOGTOME][DutyQueue] Queueing via {queuePath}: {dutyName}");
        dutyAutomationService.QueueDuty(isPraetorium);
    }

    public void PauseQueueForRepair()
    {
        if (!state.AutoQueueDisabledForRepair)
        {
            state.AutoQueueDisabledForRepair = true;
            log.Information("[MOGTOME][DutyQueue] MOGTOME queue paused for repair");
        }
    }

    public void ResumeQueueAfterRepair()
    {
        if (!state.AutoQueueDisabledForRepair)
            return;

        state.AutoQueueDisabledForRepair = false;
        log.Information("[MOGTOME][DutyQueue] MOGTOME queue resumed after repair");
    }

    public void ClearRepairQueuePauseOnStop()
    {
        if (state.AutoQueueDisabledForRepair)
            log.Information("[MOGTOME][DutyQueue] MOGTOME queue repair pause cleared on stop");

        state.AutoQueueDisabledForRepair = false;
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
