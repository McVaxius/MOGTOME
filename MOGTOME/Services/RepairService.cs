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
        if (config.RepairMaterialId <= 0) return;

        try
        {
            log.Information("[Repair] Attempting self-repair");
            commandManager.ProcessCommand("/generalaction \"Repair\"");
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
            // AutoDuty handles NPC repair when configured
            commandManager.ProcessCommand("/ad repair");
        }
        catch (Exception ex)
        {
            log.Error($"[Repair] NPC repair failed: {ex.Message}");
        }
    }

    public void AutoEquipIfEnabled()
    {
        if (!config.AutoEquipRecommended) return;

        try
        {
            commandManager.ProcessCommand("/equiprecommended");
            commandManager.ProcessCommand("/updategearset");
            log.Debug("[Repair] Auto-equipped recommended gear");
        }
        catch (Exception ex)
        {
            log.Error($"[Repair] Auto-equip failed: {ex.Message}");
        }
    }
}
