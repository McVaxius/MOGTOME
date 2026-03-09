using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace MOGTOME.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly List<string> foodItems = new()
    {
        "Orange Juice", "Baked Eggplant", "Tea", "Coffee", "Water", "Milk", "Apple Juice", "Grape Juice"
    };

    private readonly List<string> rotationPlugins = new()
    {
        "None", "RSR", "BossMod", "Wrath", "UCombo"
    };

    public ConfigWindow(Plugin plugin, Configuration config) : base("M.O.G.T.O.M.E. - Configuration##mogtome_config")
    {
        this.plugin = plugin;
        this.config = config;
        
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(800, 800)
        };

        RespectCloseHotkey = true;
    }

    public override void Draw()
    {
        try
        {
            DrawGeneralSettings();
            ImGui.Separator();
            DrawDutySettings();
            ImGui.Separator();
            DrawMaintenanceSettings();
            ImGui.Separator();
            DrawAutomationSettings();
            ImGui.Separator();
            DrawPartySettings();
            ImGui.Separator();
            DrawRotationSettings();
            ImGui.Separator();
            DrawUISettings();
            ImGui.Separator();
            DrawAdvancedSettings();
            ImGui.Spacing();
            DrawActionButtons();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error drawing config window: {ex.Message}");
        }
    }

    private void DrawGeneralSettings()
    {
        if (!ImGui.CollapsingHeader("General Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable Plugin", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Enable or disable the entire plugin functionality.");

        ImGui.Spacing();

        var debugMode = config.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debugMode))
        {
            config.DebugMode = debugMode;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Enable debug logging and additional debug information.");

        ImGui.Spacing();

        // Log level
        var logLevel = (int)config.LogLevel;
        if (ImGui.Combo("Log Level", ref logLevel, Enum.GetNames(typeof(LogLevel)), Enum.GetNames(typeof(LogLevel)).Length))
        {
            config.LogLevel = (LogLevel)logLevel;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Set the minimum level of messages to log.");

        ImGui.Spacing();

        // Update interval
        var updateInterval = config.UpdateInterval;
        if (ImGui.SliderInt("Update Interval (ms)", ref updateInterval, 500, 5000))
        {
            config.UpdateInterval = updateInterval;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("How often the plugin updates its internal state (in milliseconds).");
    }

    private void DrawDutySettings()
    {
        if (!ImGui.CollapsingHeader("Duty Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Daily target
        var dailyTarget = config.DailyTarget;
        if (ImGui.SliderInt("Daily Target", ref dailyTarget, 1, 999))
        {
            config.DailyTarget = dailyTarget;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("How many duties to complete before switching to Porta Decumana.");

        ImGui.Spacing();

        // Current duty type
        var currentDuty = (int)config.CurrentDuty;
        if (ImGui.Combo("Current Duty", ref currentDuty, new[] { "Praetorium", "Porta Decumana" }, 2))
        {
            config.CurrentDuty = (DutyType)currentDuty;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Which duty to farm. Praetorium for first 99 runs, then Porta Decumana.");

        ImGui.Spacing();

        // Queue delay
        var queueDelay = config.QueueDelay;
        if (ImGui.SliderInt("Queue Delay (seconds)", ref queueDelay, 1, 60))
        {
            config.QueueDelay = queueDelay;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Delay between queue attempts.");

        ImGui.Spacing();

        // Leave duty settings
        var leaveDuty = config.LeaveDutyAfterComplete;
        if (ImGui.Checkbox("Leave Duty After Complete", ref leaveDuty))
        {
            config.LeaveDutyAfterComplete = leaveDuty;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Automatically leave duty after completion.");

        if (leaveDuty)
        {
            ImGui.Indent();
            var leaveDelay = config.LeaveDutyDelay;
            if (ImGui.SliderInt("Leave Delay (seconds)", ref leaveDelay, 1, 60))
            {
                config.LeaveDutyDelay = leaveDelay;
                config.Save();
            }
            ImGui.Unindent();
        }
    }

    private void DrawMaintenanceSettings()
    {
        if (!ImGui.CollapsingHeader("Maintenance Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Auto repair
        var autoRepair = config.AutoRepair;
        if (ImGui.Checkbox("Auto Repair", ref autoRepair))
        {
            config.AutoRepair = autoRepair;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Automatically repair gear when durability falls below threshold.");

        if (autoRepair)
        {
            ImGui.Indent();
            var repairThreshold = config.RepairThreshold;
            if (ImGui.SliderInt("Repair Threshold (%)", ref repairThreshold, 1, 99))
            {
                config.RepairThreshold = repairThreshold;
                config.Save();
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();

        // Auto food
        var autoFood = config.AutoFood;
        if (ImGui.Checkbox("Auto Food", ref autoFood))
        {
            config.AutoFood = autoFood;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Automatically eat food when buff expires.");

        if (autoFood)
        {
            ImGui.Indent();
            
            // Food item selection
            var currentFoodIndex = foodItems.IndexOf(config.FoodItem);
            if (currentFoodIndex == -1) currentFoodIndex = 0;

            if (ImGui.Combo("Food Item", ref currentFoodIndex, foodItems.ToArray(), foodItems.Count))
            {
                config.FoodItem = foodItems[currentFoodIndex];
                config.Save();
            }

            ImGui.SameLine();
            HelpMarker("Select which food item to consume.");

            ImGui.Unindent();
        }

        ImGui.Spacing();

        // Auto equip
        var autoEquip = config.AutoEquip;
        if (ImGui.Checkbox("Auto Equip", ref autoEquip))
        {
            config.AutoEquip = autoEquip;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Automatically equip recommended gear (useful for level-synced content).");
    }

    private void DrawAutomationSettings()
    {
        if (!ImGui.CollapsingHeader("Automation Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Stuck detection
        var stuckDetection = config.StuckDetection;
        if (ImGui.Checkbox("Stuck Detection", ref stuckDetection))
        {
            config.StuckDetection = stuckDetection;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Detect when stuck and attempt recovery.");

        if (stuckDetection)
        {
            ImGui.Indent();
            var stuckTimeout = config.StuckTimeout;
            if (ImGui.SliderInt("Stuck Timeout (seconds)", ref stuckTimeout, 10, 300))
            {
                config.StuckTimeout = stuckTimeout;
                config.Save();
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();

        // Performance mode
        var performanceMode = config.PerformanceMode;
        if (ImGui.Checkbox("Performance Mode", ref performanceMode))
        {
            config.PerformanceMode = performanceMode;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Enable performance optimizations (reduced UI updates, etc.).");
    }

    private void DrawPartySettings()
    {
        if (!ImGui.CollapsingHeader("Party Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Party coordination
        var partyCoordination = config.PartyCoordination;
        if (ImGui.Checkbox("Party Coordination", ref partyCoordination))
        {
            config.PartyCoordination = partyCoordination;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Coordinate actions with party members.");

        ImGui.Spacing();

        // Party leader status
        ImGui.Text($"Party Leader Status: {(config.IsPartyLeader ? "Leader" : "Follower")}");
        
        if (ImGui.Button("Detect Party Role"))
        {
            if (Service.ClientState.IsLoggedIn && Service.ClientState.LocalPlayer != null)
            {
                var playerName = Service.ClientState.LocalPlayer.Name.ToString();
                var partyList = Service.PartyList;
                
                if (partyList.Length > 0)
                {
                    var leader = partyList[0];
                    config.IsPartyLeader = leader.Name.ToString() == playerName;
                    config.Save();
                    Service.Chat.Print($"Detected as {(config.IsPartyLeader ? "Party Leader" : "Party Follower")}");
                }
                else
                {
                    Service.Chat.Print("Not in a party");
                }
            }
            else
            {
                Service.Chat.Print("Not logged in");
            }
        }

        ImGui.Spacing();

        // Whitelist (placeholder for future implementation)
        ImGui.Text("Whitelist Settings (Future Feature):");
        ImGui.TextDisabled("Auto-accept party invites from whitelisted players");
        ImGui.TextDisabled("Coming in a future update");
    }

    private void DrawRotationSettings()
    {
        if (!ImGui.CollapsingHeader("Rotation Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Rotation plugin
        var rotationIndex = rotationPlugins.IndexOf(config.RotationPlugin.ToString());
        if (rotationIndex == -1) rotationIndex = 1; // Default to RSR

        if (ImGui.Combo("Rotation Plugin", ref rotationIndex, rotationPlugins.ToArray(), rotationPlugins.Count))
        {
            if (Enum.TryParse<RotationPlugin>(rotationPlugins[rotationIndex], out var rotation))
            {
                config.RotationPlugin = rotation;
                config.Save();
            }
        }

        ImGui.SameLine();
        HelpMarker("Select which rotation plugin to use for combat automation.");

        ImGui.Spacing();

        // BossMod preset (only if BossMod is selected)
        if (config.RotationPlugin == RotationPlugin.BossMod)
        {
            ImGui.Indent();
            
            var preset = config.BossModPreset;
            if (ImGui.InputText("BossMod Preset", ref preset, 256))
            {
                config.BossModPreset = preset;
                config.Save();
            }

            ImGui.SameLine();
            HelpMarker("BossMod preset to use for combat automation.");

            ImGui.Unindent();
        }
    }

    private void DrawUISettings()
    {
        if (!ImGui.CollapsingHeader("UI Settings", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        // Show main window
        var showMainWindow = config.ShowMainWindow;
        if (ImGui.Checkbox("Show Main Window", ref showMainWindow))
        {
            config.ShowMainWindow = showMainWindow;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Show or hide the main interface window.");

        ImGui.Spacing();

        // Show progress window
        var showProgressWindow = config.ShowProgressWindow;
        if (ImGui.Checkbox("Show Progress Window", ref showProgressWindow))
        {
            config.ShowProgressWindow = showProgressWindow;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Show or hide the progress tracking window.");

        ImGui.Spacing();

        // Show debug window
        var showDebugWindow = config.ShowDebugWindow;
        if (ImGui.Checkbox("Show Debug Window", ref showDebugWindow))
        {
            config.ShowDebugWindow = showDebugWindow;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Show or hide the debug information window.");
    }

    private void DrawAdvancedSettings()
    {
        if (!ImGui.CollapsingHeader("Advanced Settings")) return;

        ImGui.Spacing();

        // Multi-client
        var enableMultiClient = config.EnableMultiClient;
        if (ImGui.Checkbox("Enable Multi-Client", ref enableMultiClient))
        {
            config.EnableMultiClient = enableMultiClient;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Enable multi-client coordination features.");

        if (enableMultiClient)
        {
            ImGui.Indent();
            var clientId = config.ClientId;
            if (ImGui.SliderInt("Client ID", ref clientId, 1, 8))
            {
                config.ClientId = clientId;
                config.Save();
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();

        // Statistics
        var enableStatistics = config.EnableStatistics;
        if (ImGui.Checkbox("Enable Statistics", ref enableStatistics))
        {
            config.EnableStatistics = enableStatistics;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Collect and track statistics about farming performance.");

        ImGui.Spacing();

        // Logging
        var enableLogging = config.EnableLogging;
        if (ImGui.Checkbox("Enable Logging", ref enableLogging))
        {
            config.EnableLogging = enableLogging;
            config.Save();
        }

        ImGui.SameLine();
        HelpMarker("Enable detailed logging of all plugin activities.");
    }

    private void DrawActionButtons()
    {
        ImGui.Separator();
        ImGui.Spacing();

        // Save button
        if (ImGui.Button("Save Configuration", new Vector2(150, 30)))
        {
            config.Save();
            Service.Chat.Print("Configuration saved");
        }

        ImGui.SameLine();

        // Reset button
        if (ImGui.Button("Reset to Defaults", new Vector2(150, 30)))
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reset all settings to default values");
            
            // TODO: Implement reset to defaults
            Service.Chat.Print("Reset to defaults not yet implemented");
        }

        ImGui.SameLine();

        // Export/Import buttons
        if (ImGui.Button("Export Config", new Vector2(120, 30)))
        {
            // TODO: Implement config export
            Service.Chat.Print("Export configuration not yet implemented");
        }

        ImGui.SameLine();

        if (ImGui.Button("Import Config", new Vector2(120, 30)))
        {
            // TODO: Implement config import
            Service.Chat.Print("Import configuration not yet implemented");
        }

        ImGui.Spacing();

        // Information
        ImGui.Text($"Configuration Version: {config.Version}");
        ImGui.Text($"Last Reset: {config.LastResetDate:yyyy-MM-dd}");
        ImGui.Text($"Daily Counter: {config.DailyCounter}/{config.DailyTarget}");
    }

    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
