using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed class ConsumableInventoryService
{
    private const float RefreshCooldownSeconds = 1.0f;

    private readonly IPluginLog log;
    private Configuration config;
    private readonly DutyState state;

    private DateTime lastRefreshUtc = DateTime.MinValue;
    private int? lastFoodExactCount;
    private int? lastPotionExactCount;
    private bool lastFoodMismatchLogged;
    private bool lastPotionMismatchLogged;

    public ConsumableInventoryService(IPluginLog log, Configuration config, DutyState state)
    {
        this.log = log;
        this.config = config;
        this.state = state;
    }

    public void SubscribeToConfigChanges(ConfigManager configManager)
    {
        configManager.ConfigurationChanged += OnConfigurationChanged;
        log.Debug("[MOGTOME][Consumables] Subscribed to configuration changes");
    }

    public void Refresh(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - lastRefreshUtc).TotalSeconds < RefreshCooldownSeconds)
            return;

        lastRefreshUtc = now;
        RefreshFoodState();
        RefreshPotionState();
    }

    private void OnConfigurationChanged(Configuration newConfig)
    {
        config = newConfig;
        lastFoodExactCount = null;
        lastPotionExactCount = null;
        lastFoodMismatchLogged = false;
        lastPotionMismatchLogged = false;
        lastRefreshUtc = DateTime.MinValue;
        Refresh(force: true);
    }

    private void RefreshFoodState()
    {
        if (config.FoodItemId <= 0)
        {
            state.FoodExactCount = 0;
            state.FoodAvailable = false;
            lastFoodExactCount = null;
            lastFoodMismatchLogged = false;
            return;
        }

        var qualityLabel = GetQualityLabel(config.FoodUseHighQuality);
        var alternateQualityLabel = GetQualityLabel(!config.FoodUseHighQuality);
        var itemName = GetConfiguredItemLabel(config.FoodItemName, config.FoodItemId);
        var exactCount = GameHelpers.GetInventoryItemCount((uint)config.FoodItemId, config.FoodUseHighQuality);

        state.FoodExactCount = exactCount;
        state.FoodAvailable = exactCount > 0;

        if (lastFoodExactCount != exactCount)
        {
            log.Information($"[MOGTOME][Food] Exact configured count for {itemName} [{qualityLabel}] is now {exactCount}.");
            lastFoodExactCount = exactCount;
        }

        if (exactCount > 0)
        {
            lastFoodMismatchLogged = false;
            return;
        }

        var alternateCount = GameHelpers.GetInventoryItemCount((uint)config.FoodItemId, !config.FoodUseHighQuality);
        if (alternateCount > 0 && !lastFoodMismatchLogged)
        {
            log.Warning($"[MOGTOME][Food] Configured {qualityLabel} {itemName} unavailable: exact {qualityLabel}=0, {alternateQualityLabel}={alternateCount}. No cross-quality fallback.");
            lastFoodMismatchLogged = true;
        }
        else if (alternateCount <= 0)
        {
            lastFoodMismatchLogged = false;
        }
    }

    private void RefreshPotionState()
    {
        if (config.PotionItemId <= 0)
        {
            state.PotionExactCount = 0;
            state.PotionsAvailable = false;
            lastPotionExactCount = null;
            lastPotionMismatchLogged = false;
            return;
        }

        var qualityLabel = GetQualityLabel(config.PotionUseHighQuality);
        var alternateQualityLabel = GetQualityLabel(!config.PotionUseHighQuality);
        var itemName = GetConfiguredItemLabel(config.PotionItemName, config.PotionItemId);
        var exactCount = GameHelpers.GetInventoryItemCount((uint)config.PotionItemId, config.PotionUseHighQuality);

        state.PotionExactCount = exactCount;
        state.PotionsAvailable = exactCount > 0;

        if (lastPotionExactCount != exactCount)
        {
            log.Information($"[MOGTOME][Potions] Exact configured count for {itemName} [{qualityLabel}] is now {exactCount}.");
            lastPotionExactCount = exactCount;
        }

        if (exactCount > 0)
        {
            lastPotionMismatchLogged = false;
            return;
        }

        var alternateCount = GameHelpers.GetInventoryItemCount((uint)config.PotionItemId, !config.PotionUseHighQuality);
        if (alternateCount > 0 && !lastPotionMismatchLogged)
        {
            log.Warning($"[MOGTOME][Potions] Configured {qualityLabel} {itemName} unavailable: exact {qualityLabel}=0, {alternateQualityLabel}={alternateCount}. No cross-quality fallback.");
            lastPotionMismatchLogged = true;
        }
        else if (alternateCount <= 0)
        {
            lastPotionMismatchLogged = false;
        }
    }

    private static string GetQualityLabel(bool highQuality)
        => highQuality ? "HQ" : "NQ";

    private static string GetConfiguredItemLabel(string configuredName, int itemId)
        => string.IsNullOrWhiteSpace(configuredName) ? $"Item {itemId}" : configuredName;
}
