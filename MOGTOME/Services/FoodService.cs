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
    private bool? lastFoodAvailability;
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
        log.Debug("[MOGTOME][FoodService] Subscribed to configuration changes");
    }

    private void OnConfigurationChanged(Configuration newConfig)
    {
        this.config = newConfig; // Update stored config
        log.Information($"[MOGTOME][Food] Configuration updated - FoodItemId: {config.FoodItemId}, FoodName: '{config.FoodItemName}', HQ={config.FoodUseHighQuality}");
    }

    public void Update()
    {
        if (config.FoodItemId <= 0) 
        {
            state.FoodAvailable = false;
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastFoodCheck).TotalSeconds < FoodCheckCooldown) 
        {
            return;
        }
        lastFoodCheck = now;

        // Enhanced condition checks with proper logging
        var inCombat = condition[26]; // Condition[26] = InCombat
        var boundByDuty = condition[34]; // Condition[34] = BoundByDuty
        
        // FrenRider pattern: Only block eating when in duty AND combat simultaneously
        if (boundByDuty && inCombat)
        {
            return;
        }

        if (inCombat) 
        {
            return;
        }

        try
        {
            // Check Well Fed buff timer
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) 
            {
                return;
            }

            // Check if player HP > 0 (alive)
            if (player.CurrentHp == 0) 
            {
                return;
            }

            var availableCount = GameHelpers.GetInventoryItemCount((uint)config.FoodItemId, config.FoodUseHighQuality);
            state.FoodAvailable = availableCount > 0;
            if (lastFoodAvailability != state.FoodAvailable)
            {
                lastFoodAvailability = state.FoodAvailable;
                var qualityLabel = config.FoodUseHighQuality ? "HQ" : "NQ";
                log.Information($"[MOGTOME][Food] {qualityLabel} availability changed for {config.FoodItemName}: count={availableCount}");
            }

            if (!state.FoodAvailable)
            {
                log.Warning($"[MOGTOME][Food] No {(config.FoodUseHighQuality ? "HQ" : "NQ")} {config.FoodItemName} found");
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

            if (wellFedRemaining < FoodRefreshThreshold)
            {
                ConsumeFood();
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Food] Update failed: {ex.Message}");
        }
    }

    private void ConsumeFood()
    {
        try
        {
            var qualityLabel = config.FoodUseHighQuality ? "HQ" : "NQ";
            log.Information($"[MOGTOME][Food] Consuming: {config.FoodItemName} [{qualityLabel}] (ID: {config.FoodItemId})");
            var result = GameHelpers.UseItem((uint)config.FoodItemId, config.FoodUseHighQuality);
            if (result)
            {
                log.Information($"[MOGTOME][Food] Successfully ate {config.FoodItemName} [{qualityLabel}]");
            }
            else
            {
                log.Warning($"[MOGTOME][Food] Failed to eat {config.FoodItemName} [{qualityLabel}] - UseItem returned false");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Food] ConsumeFood failed: {ex.Message}");
        }
    }
}
