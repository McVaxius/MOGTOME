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
    private DateTime lastRepairCancelFailureLogUtc = DateTime.MinValue;
    private int repairCancelSequenceId = 0;
    private RepairCancelStage repairCancelStage = RepairCancelStage.None;
    private bool repairCancelRestoreAttemptLogged = false;
    private bool repairCancelDeclineAttemptLogged = false;
    private const float QueueCooldown = 10.0f;
    private const float RepairCancelLogCooldownSeconds = 15.0f;
    private const int RepairCancelCallbackDelayMs = 500;
    private const int RepairConfirmRestoreWaitMs = 1000;
    private const string ContentsFinderConfirmAddon = "ContentsFinderConfirm";
    private const string NotificationAddon = "_Notification";
    private const string NotificationFinderAddon = "_NotificationFinder";

    private enum RepairCancelStage
    {
        None,
        WaitingForConfirm,
        RestoreTimedOut,
        ClosingConfirm,
    }

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
        CancelRepairDutyPopSequence();
        log.Information("[MOGTOME][DutyQueue] MOGTOME queue resumed after repair");
    }

    public void ClearRepairQueuePauseOnStop()
    {
        if (state.AutoQueueDisabledForRepair)
            log.Information("[MOGTOME][DutyQueue] MOGTOME queue repair pause cleared on stop");

        state.AutoQueueDisabledForRepair = false;
        CancelRepairDutyPopSequence();
    }

    public bool IsRepairDutyPopCancelInFlight
        => repairCancelStage is RepairCancelStage.WaitingForConfirm or RepairCancelStage.ClosingConfirm;

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
        if (GameHelpers.IsAddonVisible(ContentsFinderConfirmAddon))
        {
            var now = DateTime.UtcNow;
            if ((now - lastCommenceClickTime).TotalSeconds > 2) // Rate limit to prevent spam
            {
                lastCommenceClickTime = now;
                log.Information("[MOGTOME][DutyQueue] Clicking Commence on ContentsFinderConfirm");
                
                // Fire commence callback - typically callback index 8 = Commence button
                GameHelpers.FireAddonCallback(ContentsFinderConfirmAddon, true, 8);
            }
        }
    }

    public bool CancelDutyPopForRepair()
    {
        var confirmVisible = GameHelpers.IsAddonVisible(ContentsFinderConfirmAddon);
        var minimizedFinderVisible = GameHelpers.IsAddonVisible(NotificationFinderAddon);

        switch (repairCancelStage)
        {
            case RepairCancelStage.WaitingForConfirm:
                if (confirmVisible)
                {
                    StartRepairConfirmCloseSequence();
                    return true;
                }

                return true;

            case RepairCancelStage.ClosingConfirm:
                return true;

            case RepairCancelStage.RestoreTimedOut:
                if (confirmVisible)
                {
                    StartRepairConfirmCloseSequence();
                    return true;
                }

                if (!minimizedFinderVisible)
                    CancelRepairDutyPopSequence();

                return false;
        }

        if (confirmVisible)
        {
            StartRepairCancelSequence(RepairCancelStage.ClosingConfirm);
            StartRepairConfirmCloseSequence();
            return true;
        }

        if (!minimizedFinderVisible)
            return false;

        StartRepairCancelSequence(RepairCancelStage.WaitingForConfirm);
        var sequenceId = repairCancelSequenceId;
        LogRepairCancelRestoreAttempt();
        if (!GameHelpers.TryFireAddonCallback(NotificationAddon, true, 0, 17))
            LogRepairCancelFailure("[MOGTOME][DutyQueue] Failed to restore minimized duty finder notification with _Notification true 0 17");
        GameHelpers.QueueFrameworkAction(
            "DutyQueue repair cancel",
            "wait for restored ContentsFinderConfirm",
            TimeSpan.FromMilliseconds(RepairConfirmRestoreWaitMs),
            () => CheckRestoredContentsFinderConfirm(sequenceId));
        return true;
    }

    private void StartRepairCancelSequence(RepairCancelStage stage)
    {
        repairCancelSequenceId++;
        repairCancelStage = stage;
        repairCancelRestoreAttemptLogged = false;
        repairCancelDeclineAttemptLogged = false;
    }

    private void StartRepairConfirmCloseSequence()
    {
        repairCancelStage = RepairCancelStage.ClosingConfirm;
        var sequenceId = repairCancelSequenceId;

        LogRepairCancelDeclineAttempt();
        if (!GameHelpers.IsAddonVisible(ContentsFinderConfirmAddon))
        {
            LogRepairCancelFailure("[MOGTOME][DutyQueue] ContentsFinderConfirm was not visible for repair cancel callback 9");
        }
        else if (!GameHelpers.TryFireAddonCallback(ContentsFinderConfirmAddon, true, 9))
        {
            LogRepairCancelFailure("[MOGTOME][DutyQueue] Failed to decline ContentsFinderConfirm with callback 9 while repair is active");
        }

        GameHelpers.QueueFrameworkAction(
            "DutyQueue repair cancel",
            "close declined ContentsFinderConfirm",
            TimeSpan.FromMilliseconds(RepairCancelCallbackDelayMs),
            () => FireRepairConfirmClose(sequenceId));
    }

    private void CheckRestoredContentsFinderConfirm(int sequenceId)
    {
        if (!IsCurrentRepairCancelSequence(sequenceId) || repairCancelStage != RepairCancelStage.WaitingForConfirm)
            return;

        if (GameHelpers.IsAddonVisible(ContentsFinderConfirmAddon))
        {
            StartRepairConfirmCloseSequence();
            return;
        }

        LogRepairCancelFailure("[MOGTOME][DutyQueue] ContentsFinderConfirm did not appear within 1s after _Notification true 0 17");
        repairCancelStage = RepairCancelStage.RestoreTimedOut;
    }

    private void FireRepairConfirmClose(int sequenceId)
    {
        try
        {
            if (!IsCurrentRepairCancelSequence(sequenceId))
                return;

            if (GameHelpers.IsAddonVisible(ContentsFinderConfirmAddon) &&
                !GameHelpers.TryFireAddonCallback(ContentsFinderConfirmAddon, true, 9))
            {
                LogRepairCancelFailure("[MOGTOME][DutyQueue] Failed to close ContentsFinderConfirm with callback 9 after repair cancel");
            }
        }
        finally
        {
            CompleteRepairCancelSequence(sequenceId);
        }
    }

    private bool IsCurrentRepairCancelSequence(int sequenceId)
        => repairCancelStage != RepairCancelStage.None && sequenceId == repairCancelSequenceId;

    private void CompleteRepairCancelSequence(int sequenceId)
    {
        if (sequenceId != repairCancelSequenceId)
            return;

        repairCancelStage = RepairCancelStage.None;
        repairCancelRestoreAttemptLogged = false;
        repairCancelDeclineAttemptLogged = false;
    }

    private void CancelRepairDutyPopSequence()
    {
        repairCancelSequenceId++;
        repairCancelStage = RepairCancelStage.None;
        repairCancelRestoreAttemptLogged = false;
        repairCancelDeclineAttemptLogged = false;
    }

    private void LogRepairCancelRestoreAttempt()
    {
        if (repairCancelRestoreAttemptLogged)
            return;

        repairCancelRestoreAttemptLogged = true;
        log.Warning("[MOGTOME][DutyQueue] Restoring minimized duty finder notification while repair is active");
    }

    private void LogRepairCancelDeclineAttempt()
    {
        if (repairCancelDeclineAttemptLogged)
            return;

        repairCancelDeclineAttemptLogged = true;
        log.Warning("[MOGTOME][DutyQueue] Declining ContentsFinderConfirm while repair is active");
    }

    private void LogRepairCancelFailure(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - lastRepairCancelFailureLogUtc).TotalSeconds < RepairCancelLogCooldownSeconds)
            return;

        lastRepairCancelFailureLogUtc = now;
        log.Warning(message);
    }
}
