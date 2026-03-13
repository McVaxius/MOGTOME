using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class FoodService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
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
        this.config = config;
        this.state = state;
        this.condition = condition;
    }

    public void Update()
    {
        if (config.FoodItemId <= 0) return;

        var now = DateTime.UtcNow;
        if ((now - lastFoodCheck).TotalSeconds < FoodCheckCooldown) return;
        lastFoodCheck = now;

        // Don't eat food while in combat
        // Condition[26] = InCombat
        if (condition[26]) return;

        try
        {
            // Check Well Fed buff timer
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return;

            // Check if player HP > 0 (alive)
            if (player.CurrentHp == 0) return;

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
            log.Error($"[Food] Update failed: {ex.Message}");
        }
    }

    private void ConsumeFood()
    {
        try
        {
            if (config.EchoLevel < 4)
                log.Information($"[Food] Consuming: {config.FoodItemName} (ID: {config.FoodItemId})");

            // Use the food item via command
            Plugin.CommandManager.ProcessCommand($"/useitem {config.FoodItemId}");
        }
        catch (Exception ex)
        {
            log.Error($"[Food] ConsumeFood failed: {ex.Message}");
        }
    }
}
