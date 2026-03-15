using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace MOGTOME;

[Serializable]
public class Configuration : IPluginConfiguration
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

    // --- Food Settings ---
    public int FoodItemId { get; set; } = 0;
    public string FoodItemName { get; set; } = "";

    // --- Potion Settings ---
    public int PotionItemId { get; set; } = 0;
    public string PotionItemName { get; set; } = "";
    public int PotionTarget { get; set; } = 0;

    // --- Repair Settings (handled by AutoDuty) ---
    public int RepairThreshold { get; set; } = 25;

    // --- Debug Settings ---
    public int DebugCounter { get; set; } = 0;
    public int BailoutTimeout { get; set; } = 1200;

    // --- Dependency Check ---
    public bool AutoDutyPathInstalled { get; set; } = false;

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
    
    public int TotalMogtomesEarned { get; set; } = 0;

    // --- UI Settings ---
    public bool IsConfigWindowMovable { get; set; } = true;
    public bool StatsKrangleNames { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
