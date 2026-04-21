using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class RepairService
{
    private readonly IPluginLog log;
    private Configuration config; // Remove readonly to allow updates
    private readonly DutyState state;
    private readonly DutyAutomationService dutyAutomationService;
    private readonly ICondition condition;

    private DateTime lastRepairCheck = DateTime.MinValue;
    private const float RepairCheckCooldown = 5.0f;

    public RepairService(
        IPluginLog log, Configuration config, DutyState state,
        DutyAutomationService dutyAutomationService, ICondition condition)
    {
        this.log = log;
        this.config = config; // Still store initial config
        this.state = state;
        this.dutyAutomationService = dutyAutomationService;
        this.condition = condition;
    }

    // ADD EVENT HANDLER
    public void SubscribeToConfigChanges(ConfigManager configManager)
    {
        configManager.ConfigurationChanged += OnConfigurationChanged;
        log.Debug("[MOGTOME][RepairService] Subscribed to configuration changes");
    }

    private void OnConfigurationChanged(Configuration newConfig)
    {
        this.config = newConfig;
        log.Information($"[MOGTOME][RepairService] Configuration updated - RepairThreshold: {config.RepairThreshold}%");
    }

    public bool NeedsRepair(bool forceRefresh = false)
    {
        if (config.RepairThreshold < 0) return false;

        try
        {
            // Use Dalamud's condition check - only check periodically
            var now = DateTime.UtcNow;
            if (!forceRefresh && (now - lastRepairCheck).TotalSeconds < RepairCheckCooldown) 
            {
                // Return cached result during cooldown
                return state.NeedsRepair;
            }
            lastRepairCheck = now;

            // Check actual equipment durability
            bool needsRepair = CheckEquipmentDurability(config.RepairThreshold);
            
            // Update cached state
            state.NeedsRepair = needsRepair;
            
            if (needsRepair)
            {
                log.Debug($"[MOGTOME][Repair] Equipment needs repair (threshold: {config.RepairThreshold}%)");
            }
            
            return needsRepair;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Repair] NeedsRepair check failed: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool CheckEquipmentDurability(int repairThreshold)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return false;

            // Check equipped gear slots
            var equippedContainer = im->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedContainer == null) return false;

            for (var i = 0; i < equippedContainer->Size; i++)
            {
                var item = equippedContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                // Check condition (durability) - convert from 0-30000 to 0-100%
                var actualCondition = item->Condition / 300;
                if (actualCondition < repairThreshold)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME][Repair] CheckEquipmentDurability failed: {ex.Message}");
            return false;
        }
    }

    public void TrySelfRepair()
    {
        try
        {
            dutyAutomationService.RequestSelfRepair();
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Repair] Self-repair failed: {ex.Message}");
        }
    }

    public void TryNpcRepair()
    {
        try
        {
            dutyAutomationService.RequestNpcRepair();
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Repair] NPC repair failed: {ex.Message}");
        }
    }

    public void ReturnToInnIfNeeded()
    {
        dutyAutomationService.ReturnToInnIfNeeded();
    }

    public void AutoEquipIfEnabled()
    {
        // No-op: AutoDuty handles equipment management
    }
}
