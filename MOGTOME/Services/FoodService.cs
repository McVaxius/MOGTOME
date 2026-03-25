using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MOGTOME.Services;

public class FoodService
{
    private readonly IPluginLog log;
    private Configuration config; // Remove readonly to allow updates
    private readonly DutyState state;
    private readonly ICondition condition;

    // Well Fed status ID = 48
    private const uint WellFedStatusId = 48;
    private const float FoodRefreshThreshold = 90.0f;

    private DateTime lastFoodCheck = DateTime.MinValue;
    private const float FoodCheckCooldown = 5.0f;

    public FoodService(IPluginLog log, Configuration config, DutyState state, ICondition condition)
    {
        this.log = log;
        this.config = config; // Still store initial config
        this.state = state;
        this.condition = condition;
    }

    // ADD EVENT HANDLER
    public void SubscribeToConfigChanges(ConfigManager configManager)
    {
        configManager.ConfigurationChanged += OnConfigurationChanged;
        log.Debug("[FoodService] Subscribed to configuration changes");
    }

    private void OnConfigurationChanged(Configuration newConfig)
    {
        this.config = newConfig; // Update stored config
        log.Information($"[Food] Configuration updated - FoodItemId: {config.FoodItemId}, FoodName: '{config.FoodItemName}'");
    }

    public void Update()
    {
        log.Information($"[Food] Update - FoodItemId: {config.FoodItemId}, FoodName: '{config.FoodItemName}'");
        
        if (config.FoodItemId <= 0) 
        {
            log.Information("[Food] No food configured");
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastFoodCheck).TotalSeconds < FoodCheckCooldown) 
        {
            log.Debug($"[Food] Cooldown: {(now - lastFoodCheck).TotalSeconds:F1}s");
            return;
        }
        lastFoodCheck = now;

        // Enhanced condition checks with proper logging
        var inCombat = condition[26]; // Condition[26] = InCombat
        var boundByDuty = condition[34]; // Condition[34] = BoundByDuty
        
        log.Information($"[Food] Conditions - InCombat: {inCombat}, BoundByDuty: {boundByDuty}");
        
        // FrenRider pattern: Only block eating when in duty AND combat simultaneously
        if (boundByDuty && inCombat)
        {
            log.Information("[Food] Skipping - In duty + combat");
            return;
        }

        if (inCombat) 
        {
            log.Information("[Food] Skipping - In combat");
            return;
        }

        try
        {
            // Check Well Fed buff timer
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) 
            {
                log.Information("[Food] No player object");
                return;
            }

            // Check if player HP > 0 (alive)
            if (player.CurrentHp == 0) 
            {
                log.Information("[Food] Player dead");
                return;
            }

            float wellFedRemaining = 0;
            foreach (var status in player.StatusList)
            {
                if (status.StatusId == WellFedStatusId)
                {
                    wellFedRemaining = status.RemainingTime;
                    break;
                }
            }

            log.Information($"[Food] Well Fed: {wellFedRemaining:F1}s (threshold: {FoodRefreshThreshold}s)");
            
            if (wellFedRemaining < FoodRefreshThreshold)
            {
                log.Information($"[Food] Need to eat - {config.FoodItemName}");
                ConsumeFood();
            }
            else
            {
                log.Information("[Food] Well Fed sufficient");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Food] Update failed: {ex.Message}");
        }
    }

    private unsafe int GetInventoryItemCount(uint itemId)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) 
            {
                log.Error("[Food] InventoryManager.Instance() null");
                return 0;
            }
            var count = im->GetInventoryItemCount(itemId) + im->GetInventoryItemCount(itemId, true);
            log.Debug($"[Food] Inventory count for {itemId}: {count}");
            return count;
        }
        catch (Exception ex)
        {
            log.Error($"[Food] GetInventoryItemCount({itemId}) failed: {ex.Message}");
            return 0;
        }
    }

    private void ConsumeFood()
    {
        try
        {
            log.Information($"[Food] Consuming: {config.FoodItemName} (ID: {config.FoodItemId})");
            var result = GameHelpers.UseItem((uint)config.FoodItemId);
            if (result)
            {
                log.Information($"[Food] Successfully ate {config.FoodItemName}");
            }
            else
            {
                log.Warning($"[Food] Failed to eat {config.FoodItemName} - UseItem returned false");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Food] ConsumeFood failed: {ex.Message}");
        }
    }
}
