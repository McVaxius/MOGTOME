using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace MOGTOME;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Core Settings
    public bool Enabled { get; set; } = true;
    public bool DebugMode { get; set; } = false;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    // Duty Farming Settings
    public int DailyTarget { get; set; } = 99;
    public int DailyCounter { get; set; } = 0;
    public DateTime LastResetDate { get; set; } = DateTime.Today;
    public DutyType CurrentDuty { get; set; } = DutyType.Praetorium;

    // Maintenance Settings
    public bool AutoRepair { get; set; } = true;
    public int RepairThreshold { get; set; } = 25;
    public bool AutoFood { get; set; } = true;
    public string FoodItem { get; set; } = "Orange Juice";
    public int FoodItemId { get; set; } = 4745;
    public bool AutoEquip { get; set; } = false;

    // Automation Settings
    public bool StuckDetection { get; set; } = true;
    public int StuckTimeout { get; set; } = 30;
    public int QueueDelay { get; set; } = 5;
    public bool LeaveDutyAfterComplete { get; set; } = true;
    public int LeaveDutyDelay { get; set; } = 10;

    // Party Settings
    public bool IsPartyLeader { get; set; } = false;
    public bool PartyCoordination { get; set; } = true;
    public List<string> WhitelistNames { get; set; } = new();

    // Rotation Settings
    public RotationPlugin RotationPlugin { get; set; } = RotationPlugin.RSR;
    public string BossModPreset { get; set; } = "AutoDuty";

    // UI Settings
    public bool ShowMainWindow { get; set; } = true;
    public bool IsMainWindowVisible { get; set; } = true;
    public bool IsConfigWindowVisible { get; set; } = false;

    // Performance Settings
    public int UpdateInterval { get; set; } = 1500; // milliseconds
    public bool PerformanceMode { get; set; } = false;

    // Advanced Settings
    public bool EnableMultiClient { get; set; } = false;
    public int ClientId { get; set; } = 1;
    public bool EnableStatistics { get; set; } = true;
    public bool EnableLogging { get; set; } = true;

    // Runtime State (not saved)
    [NonSerialized]
    private FarmingState currentState = FarmingState.Idle;
    
    [NonSerialized]
    private DateTime lastActivity = DateTime.Now;

    public FarmingState CurrentState
    {
        get => currentState;
        set
        {
            if (currentState != value)
            {
                var oldState = currentState;
                currentState = value;
                lastActivity = DateTime.Now;
                Service.Log.Debug($"State changed: {oldState} -> {currentState}");
            }
        }
    }

    public DateTime LastActivity => lastActivity;

    public void Initialize()
    {
        // Check for daily reset
        if (LastResetDate < DateTime.Today)
        {
            DailyCounter = 0;
            LastResetDate = DateTime.Today;
            Service.Log.Info("Daily counter reset - new day detected");
        }

        // Validate settings
        DailyTarget = Math.Max(1, Math.Min(999, DailyTarget));
        RepairThreshold = Math.Max(1, Math.Min(99, RepairThreshold));
        StuckTimeout = Math.Max(10, Math.Min(300, StuckTimeout));
        QueueDelay = Math.Max(1, Math.Min(60, QueueDelay));
        LeaveDutyDelay = Math.Max(1, Math.Min(60, LeaveDutyDelay));
        UpdateInterval = Math.Max(500, Math.Min(5000, UpdateInterval));

        // Detect party leader status
        if (Service.ClientState.IsLoggedIn && Service.ClientState.LocalPlayer != null)
        {
            var playerName = Service.ClientState.LocalPlayer.Name.ToString();
            var partyList = Service.PartyList;
            
            if (partyList.Length > 0)
            {
                var leader = partyList[0];
                IsPartyLeader = leader.Name.ToString() == playerName;
            }
        }
    }

    public void Save()
    {
        try
        {
            Service.PluginInterface.SavePluginConfig(this);
            Service.Log.Debug("Configuration saved successfully");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Failed to save configuration: {ex.Message}");
        }
    }

    public void Reset()
    {
        DailyCounter = 0;
        LastResetDate = DateTime.Today;
        CurrentState = FarmingState.Idle;
        lastActivity = DateTime.Now;
        Save();
        Service.Log.Info("Configuration reset to defaults");
    }

    public bool CanStartAutomation()
    {
        return Enabled && 
               Service.ClientState.IsLoggedIn && 
               Service.ClientState.LocalPlayer != null &&
               !Service.Condition[ConditionFlag.BoundByDuty] &&
               !Service.Condition[ConditionFlag.BetweenAreas] &&
               !Service.Condition[ConditionFlag.OccupiedInCutSceneEvent];
    }

    public void LogConfiguration()
    {
        Service.Log.Info("=== M.O.G.T.O.M.E. Configuration ===");
        Service.Log.Info($"Enabled: {Enabled}");
        Service.Log.Info($"Daily Target: {DailyTarget}");
        Service.Log.Info($"Daily Counter: {DailyCounter}");
        Service.Log.Info($"Current Duty: {CurrentDuty}");
        Service.Log.Info($"Auto Repair: {AutoRepair} (threshold: {RepairThreshold}%)");
        Service.Log.Info($"Auto Food: {AutoFood} (item: {FoodItem})");
        Service.Log.Info($"Stuck Detection: {StuckDetection} (timeout: {StuckTimeout}s)");
        Service.Log.Info($"Party Leader: {IsPartyLeader}");
        Service.Log.Info($"Rotation Plugin: {RotationPlugin}");
        Service.Log.Info($"Debug Mode: {DebugMode}");
        Service.Log.Info($"================================");
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    None
}

public enum DutyType
{
    Praetorium,
    PortaDecumana
}

public enum FarmingState
{
    Idle,
    PreDutyCheck,
    Queueing,
    InDuty,
    Completing,
    PostDuty,
    StuckRecovery,
    Error
}

public enum RotationPlugin
{
    None,
    RSR,
    BossMod,
    Wrath,
    UCombo
}
