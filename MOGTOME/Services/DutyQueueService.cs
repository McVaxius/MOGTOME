using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

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
    {
        if (state.IsInDuty) return;
        if (!state.IsPartyLeader) return;

        var now = DateTime.UtcNow;
        if ((now - lastQueueAttempt).TotalSeconds < QueueCooldown) return;
        lastQueueAttempt = now;

        // Condition[34] = BoundByDuty
        if (condition[34]) return;

        if (config.QueueMethod == 0)
        {
            QueueViaAutoDuty(isPraetorium);
        }
        else
        {
            QueueViaCallback(isPraetorium);
        }
    }

    private void QueueViaAutoDuty(bool isPraetorium)
    {
        var dutyName = isPraetorium ? "The Praetorium" : "The Porta Decumana";
        log.Information($"[DutyQueue] Queueing via AutoDuty: {dutyName}");
        autoDutyIPC.QueueDuty(dutyName);
    }

    private void QueueViaCallback(bool isPraetorium)
    {
        try
        {
            var dutyIndex = isPraetorium ? state.PraetoriumDutyIndex : DutyState.DecumanaDutyId;
            log.Information($"[DutyQueue] Queueing via callback method: index={dutyIndex}");

            // Open duty finder and select the duty
            commandManager.ProcessCommand("/dutyfinder");
        }
        catch (Exception ex)
        {
            log.Error($"[DutyQueue] Callback queue failed: {ex.Message}");
        }
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
}
