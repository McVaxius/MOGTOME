using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MOGTOME.Models;

namespace MOGTOME;

[Serializable]
public class Configuration
{
    public int Version { get; set; } = 1;

    // --- Party Settings ---
    public bool IsPartyLeader { get; set; } = false;
    public bool IsCrossWorldParty { get; set; } = false;

    // --- Duty Settings ---
    public int DutyCounter { get; set; } = 0;
    public int PraetoriumThreshold { get; set; } = 99;
    public int MaxRuns { get; set; } = 9999;
    public string QuitCommand { get; set; } = "/ays m e";
    public bool TestingModeUnsynced { get; set; } = false;

    // --- Debug Settings ---
    public bool DebugModeEnabled { get; set; } = false;           // Controls UI visibility
    public bool ShowDebugRuns { get; set; } = false;              // Controls showing unsynced runs in stats

    // --- Food Settings ---
    public int FoodItemId { get; set; } = 0;
    public string FoodItemName { get; set; } = "";
    public bool FoodUseHighQuality { get; set; } = false;

    // --- Potion Settings ---
    public int PotionItemId { get; set; } = 0;
    public string PotionItemName { get; set; } = "";
    public bool PotionUseHighQuality { get; set; } = false;
    public int PotionTarget { get; set; } = 0;

    // --- Repair Settings (handled by duty automation backend) ---
    public int RepairThreshold { get; set; } = 25;
    public bool UseAdsSelfRepair { get; set; } = false;

    // --- Debug Settings ---
    public int DebugCounter { get; set; } = 0;
    public int BailoutTimeout { get; set; } = 1200;

    // --- Dependency Check ---
    public bool AutoDutyPathInstalled { get; set; } = false;
    public string PraetoriumPathFileName { get; set; } = "(1044) The Praetorium - W2W 20250716 phecda.json";
    public bool UseAdsExperimental { get; set; } = false;

    // --- Stats ---
    // Global stats (kept for compatibility)
    public float BestTimeEver { get; set; } = float.MaxValue;
    public string BestTimeDate { get; set; } = "";
    public string BestTimeParty { get; set; } = "";
    public float LongestRunEver { get; set; } = 0;
    public string LongestRunDate { get; set; } = "";
    public string LongestRunParty { get; set; } = "";
    public int MostDeathsSelf { get; set; } = 0;
    public int MostDeathsOthers { get; set; } = 0;
    public int MostDeathsAll { get; set; } = 0;
    public int TotalDeathsSelf { get; set; } = 0;
    public int TotalDeathsOthers { get; set; } = 0;
    public int TotalDeathsAll { get; set; } = 0;
    
    // Duty-specific stats
    public float PraeBestTime { get; set; } = float.MaxValue;
    public string PraeBestTimeDate { get; set; } = "";
    public string PraeBestTimeParty { get; set; } = "";
    public float PraeLongestRun { get; set; } = 0;
    public string PraeLongestRunDate { get; set; } = "";
    public string PraeLongestRunParty { get; set; } = "";
    public int PraeMostDeathsSelf { get; set; } = 0;
    public int PraeMostDeathsOthers { get; set; } = 0;
    public int PraeMostDeathsAll { get; set; } = 0;
    public int PraeTotalDeathsSelf { get; set; } = 0;
    public int PraeTotalDeathsOthers { get; set; } = 0;
    public int PraeTotalDeathsAll { get; set; } = 0;
    public int TotalPraes { get; set; } = 0;
    public int PraeMogtomesEarned { get; set; } = 0;
    
    public float DecuBestTime { get; set; } = float.MaxValue;
    public string DecuBestTimeDate { get; set; } = "";
    public string DecuBestTimeParty { get; set; } = "";
    public float DecuLongestRun { get; set; } = 0;
    public string DecuLongestRunDate { get; set; } = "";
    public string DecuLongestRunParty { get; set; } = "";
    public int DecuMostDeathsSelf { get; set; } = 0;
    public int DecuMostDeathsOthers { get; set; } = 0;
    public int DecuMostDeathsAll { get; set; } = 0;
    public int DecuTotalDeathsSelf { get; set; } = 0;
    public int DecuTotalDeathsOthers { get; set; } = 0;
    public int DecuTotalDeathsAll { get; set; } = 0;
    public int TotalDecus { get; set; } = 0;
    public int DecuMogtomesEarned { get; set; } = 0;
    
    // Daily Decumana stats
    public int DailyDecuRuns { get; set; } = 0;
    public float DailyDecuBestTime { get; set; } = float.MaxValue;
    public float DailyDecuLongestRun { get; set; } = 0;
    public int DailyDecuMogtomesEarned { get; set; } = 0;
    public int MaxDailyDecuRuns { get; set; } = 0;
    public int AllTimeMaxDailyDecu { get; set; } = 0; // All-time record
    public DateTime? LastDailyDecuReset { get; set; } = null;
    
    // --- Detailed Stats Settings ---
    public int MaxHistorySize { get; set; } = 1000;
    public bool EnableDetailedTracking { get; set; } = true;
    public bool RecordPartyMembers { get; set; } = true;
    
    public int TotalMogtomesEarned { get; set; } = 0;

    // --- UI Settings ---
    public bool IsConfigWindowMovable { get; set; } = true;
    public bool StatsKrangleNames { get; set; } = false;
    public bool KrangleNames { get; set; } = false;
    public int WarningPopupAcknowledgedVersion { get; set; } = 0;

    /// <summary>
    /// Save configuration to a JSON file (replaces Dalamud SQLite system)
    /// Used for per-account configuration storage
    /// </summary>
    public void SaveToFile(string filePath)
    {
        try
        {
            var json = JsonSerializer.Serialize(this, GetJsonOptions());
            File.WriteAllText(filePath, json);
            Plugin.Log.Debug($"[MOGTOME][Configuration] Saved to file: {filePath}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME][Configuration] Failed to save to file {filePath}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Load configuration from a JSON file
    /// Used for per-account configuration loading
    /// </summary>
    public static Configuration LoadFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Plugin.Log.Debug($"[MOGTOME][Configuration] File not found, creating new: {filePath}");
                return new Configuration();
            }

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<Configuration>(json, GetJsonOptions());
            
            if (config == null)
            {
                Plugin.Log.Warning($"[MOGTOME][Configuration] Failed to deserialize config from {filePath}, creating new");
                return new Configuration();
            }

            Plugin.Log.Debug($"[MOGTOME][Configuration] Loaded from file: {filePath}");
            return config;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME][Configuration] Failed to load from file {filePath}: {ex.Message}");
            return new Configuration();
        }
    }

    /// <summary>
    /// Get JSON serializer options for configuration
    /// </summary>
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Legacy Save method for backward compatibility
    /// This should no longer be used - use SaveToFile instead
    /// </summary>
    [Obsolete("Use SaveToFile with per-account file path instead")]
    public void Save()
    {
        Plugin.Log.Warning("[MOGTOME][Configuration] Legacy Save() called - this should use SaveToFile with per-account path");
        // Don't call Plugin.PluginInterface.SavePluginConfig(this) anymore
    }
}
