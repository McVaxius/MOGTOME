using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class RepairService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;

    private DateTime lastRepairCheck = DateTime.MinValue;
    private const float RepairCheckCooldown = 5.0f;

    public RepairService(
        IPluginLog log, Configuration config, DutyState state,
        ICommandManager commandManager, ICondition condition)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.commandManager = commandManager;
        this.condition = condition;
    }

    public bool NeedsRepair()
    {
        if (config.RepairThreshold < 0) return false;

        try
        {
            // Use Dalamud's condition check - only check periodically
            var now = DateTime.UtcNow;
            if ((now - lastRepairCheck).TotalSeconds < RepairCheckCooldown) return state.NeedsRepair;
            lastRepairCheck = now;

            // We'll check repair status via the game's repair check
            // NeedsRepair is set by the engine when it detects low gear condition
            return state.NeedsRepair;
        }
        catch (Exception ex)
        {
            log.Error($"[Repair] NeedsRepair check failed: {ex.Message}");
            return false;
        }
    }

    public void TrySelfRepair()
    {
        try
        {
            log.Information("[Repair] Requesting repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
        }
        catch (Exception ex)
        {
            log.Error($"[Repair] Self-repair failed: {ex.Message}");
        }
    }

    public void TryNpcRepair()
    {
        try
        {
            log.Information("[Repair] Attempting NPC repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
        }
        catch (Exception ex)
        {
            log.Error($"[Repair] NPC repair failed: {ex.Message}");
        }
    }

    public void AutoEquipIfEnabled()
    {
        // No-op: AutoDuty handles equipment management
    }
}
