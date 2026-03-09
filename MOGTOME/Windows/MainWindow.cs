using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using System;
using System.Numerics;

namespace MOGTOME.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly Vector4 greenColor = new(0, 1, 0, 1);
    private readonly Vector4 redColor = new(1, 0, 0, 1);
    private readonly Vector4 yellowColor = new(1, 1, 0, 1);
    private readonly Vector4 cyanColor = new(0, 1, 1, 1);

    public MainWindow(Plugin plugin, Configuration config) : base("M.O.G.T.O.M.E. - Main Interface##mogtome")
    {
        this.plugin = plugin;
        this.config = config;
        
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(800, 600)
        };

        RespectCloseHotkey = true;
    }

    public override void Draw()
    {
        try
        {
            DrawHeader();
            ImGui.Separator();
            DrawStatusSection();
            ImGui.Separator();
            DrawProgressSection();
            ImGui.Separator();
            DrawControlsSection();
            ImGui.Separator();
            DrawMaintenanceSection();
            ImGui.Separator();
            DrawDebugSection();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error drawing main window: {ex.Message}");
        }
    }

    private void DrawHeader()
    {
        ImGui.Text("Management Of Grand Tome Operations & Management Engine");
        ImGui.Spacing();
        
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable Automation", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.Text($"| Version: 1.0.0");
        
        ImGui.SameLine();
        if (ImGui.Button("Config"))
        {
            Service.PluginInterface.UiBuilder.OpenConfigUi();
        }
    }

    private void DrawStatusSection()
    {
        ImGui.Text("Current Status");
        ImGui.Spacing();

        // Player status
        var isLoggedIn = Service.ClientState.IsLoggedIn;
        var hasPlayer = Service.ClientState.LocalPlayer != null;
        var inDuty = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty];
        var betweenAreas = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas];

        DrawStatusRow("Player Logged In", isLoggedIn);
        DrawStatusRow("Player Available", hasPlayer);
        DrawStatusRow("In Duty", inDuty);
        DrawStatusRow("Between Areas", betweenAreas);

        ImGui.Spacing();

        // Current state
        var stateColor = config.CurrentState switch
        {
            FarmingState.Idle => yellowColor,
            FarmingState.Error => redColor,
            FarmingState.StuckRecovery => yellowColor,
            _ => greenColor
        };

        ImGui.TextColored(stateColor, $"Current State: {config.CurrentState}");
        
        if (config.CurrentState != FarmingState.Idle)
        {
            var timeSinceActivity = DateTime.Now - config.LastActivity;
            ImGui.Text($"Last Activity: {timeSinceActivity:mm\\:ss} ago");
        }
    }

    private void DrawStatusRow(string label, bool status)
    {
        var color = status ? greenColor : redColor;
        ImGui.TextColored(color, $"{label}: {(status ? "Yes" : "No")}");
    }

    private void DrawProgressSection()
    {
        ImGui.Text("Daily Progress");
        ImGui.Spacing();

        // Progress bar
        var progress = (float)config.DailyCounter / config.DailyTarget;
        var progressColor = progress >= 1.0f ? greenColor : cyanColor;
        
        ImGui.ProgressBar(progress, new Vector2(300, 20), $"{config.DailyCounter}/{config.DailyTarget}");
        
        ImGui.Spacing();

        // Progress details
        ImGui.Text($"Daily Target: {config.DailyTarget} duties");
        ImGui.Text($"Current Count: {config.DailyCounter}");
        ImGui.Text($"Current Duty: {config.CurrentDuty}");
        ImGui.Text($"Last Reset: {config.LastResetDate:yyyy-MM-dd}");

        if (config.DailyCounter >= config.DailyTarget)
        {
            ImGui.TextColored(greenColor, "Daily target completed!");
        }
        else
        {
            var remaining = config.DailyTarget - config.DailyCounter;
            ImGui.Text($"Remaining: {remaining} duties");
        }

        ImGui.Spacing();

        // Quick actions
        if (ImGui.Button("Reset Counter"))
        {
            config.DailyCounter = 0;
            config.Save();
            Service.Log.Info("Daily counter reset from main window");
        }

        ImGui.SameLine();
        if (ImGui.Button("Switch Duty"))
        {
            config.CurrentDuty = config.CurrentDuty == DutyType.Praetorium ? DutyType.PortaDecumana : DutyType.Praetorium;
            config.Save();
            Service.Log.Info($"Duty switched to {config.CurrentDuty}");
        }
    }

    private void DrawControlsSection()
    {
        ImGui.Text("Automation Controls");
        ImGui.Spacing();

        var canStart = config.CanStartAutomation();
        
        if (!canStart)
        {
            ImGui.TextColored(redColor, "Cannot start automation:");
            if (!config.Enabled) ImGui.Text("• Plugin is disabled");
            if (!Service.ClientState.IsLoggedIn) ImGui.Text("• Not logged in");
            if (Service.ClientState.LocalPlayer == null) ImGui.Text("• No local player");
            if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) ImGui.Text("• Currently in duty");
            if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]) ImGui.Text("• Between areas");
            ImGui.Spacing();
        }

        // Start/Stop button
        var isRunning = config.CurrentState != FarmingState.Idle && config.CurrentState != FarmingState.Error;
        var buttonText = isRunning ? "Stop Automation" : "Start Automation";
        var buttonColor = isRunning ? redColor : greenColor;
        
        ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
        if (ImGui.Button(buttonText, new Vector2(150, 30)))
        {
            if (isRunning)
            {
                // TODO: Implement stop automation
                Service.Chat.Print("Stop automation not yet implemented");
            }
            else
            {
                // TODO: Implement start automation
                Service.Chat.Print("Start automation not yet implemented");
            }
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();
        
        if (ImGui.Button("Status", new Vector2(80, 30)))
        {
            Service.Chat.Print($"Status: {config.CurrentState} | Counter: {config.DailyCounter}/{config.DailyTarget}");
        }

        ImGui.Spacing();

        // Quick commands
        ImGui.Text("Quick Commands:");
        if (ImGui.Button("Queue Duty"))
        {
            Service.Chat.Print("Queue duty not yet implemented");
        }

        ImGui.SameLine();
        if (ImGui.Button("Leave Duty"))
        {
            Service.Chat.Print("Leave duty not yet implemented");
        }

        ImGui.SameLine();
        if (ImGui.Button("Check Maintenance"))
        {
            Service.Chat.Print("Maintenance check not yet implemented");
        }
    }

    private void DrawMaintenanceSection()
    {
        ImGui.Text("Maintenance Status");
        ImGui.Spacing();

        // Repair status
        var repairStatus = "Unknown";
        var repairColor = yellowColor;
        
        // TODO: Implement actual repair checking
        ImGui.TextColored(repairColor, $"Repair Status: {repairStatus}");
        ImGui.Text($"Auto Repair: {(config.AutoRepair ? $"Enabled ({config.RepairThreshold}%)" : "Disabled")}");

        ImGui.Spacing();

        // Food status
        var foodStatus = "Unknown";
        var foodColor = yellowColor;
        
        // TODO: Implement actual food checking
        ImGui.TextColored(foodColor, $"Food Status: {foodStatus}");
        ImGui.Text($"Auto Food: {(config.AutoFood ? $"Enabled ({config.FoodItem})" : "Disabled")}");

        ImGui.Spacing();

        // Gear status
        ImGui.Text($"Auto Equip: {(config.AutoEquip ? "Enabled" : "Disabled")}");

        ImGui.Spacing();

        if (ImGui.Button("Force Maintenance Check"))
        {
            Service.Chat.Print("Maintenance check not yet implemented");
        }
    }

    private void DrawDebugSection()
    {
        if (!ImGui.CollapsingHeader("Debug Information")) return;

        ImGui.Spacing();
        
        // Debug toggle
        var debugMode = config.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debugMode))
        {
            config.DebugMode = debugMode;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Log Config"))
        {
            config.LogConfiguration();
        }

        ImGui.Spacing();

        // Party information
        ImGui.Text("Party Information:");
        ImGui.Text($"Is Party Leader: {config.IsPartyLeader}");
        ImGui.Text($"Party Size: {Service.PartyList.Length}");
        ImGui.Text($"Party Coordination: {(config.PartyCoordination ? "Enabled" : "Disabled")}");

        ImGui.Spacing();

        // Territory information
        if (Service.ClientState.TerritoryType != 0)
        {
            ImGui.Text($"Current Territory: {Service.ClientState.TerritoryType}");
            
            // Known territories
            var territoryName = Service.ClientState.TerritoryType switch
            {
                1044 => "The Praetorium",
                1048 => "Porta Decumana",
                177 => "Gridania Inn",
                178 => "Limsa Lominsa Inn",
                179 => "Ul'dah Inn",
                _ => $"Unknown Territory {Service.ClientState.TerritoryType}"
            };
            
            ImGui.Text($"Territory Name: {territoryName}");
        }

        ImGui.Spacing();

        // Performance information
        ImGui.Text("Performance Information:");
        ImGui.Text($"Update Interval: {config.UpdateInterval}ms");
        ImGui.Text($"Performance Mode: {(config.PerformanceMode ? "Enabled" : "Disabled")}");
        ImGui.Text($"Last Activity: {config.LastActivity:HH:mm:ss}");

        ImGui.Spacing();

        // Debug actions
        if (ImGui.Button("Test Stuck Detection"))
        {
            Service.Chat.Print("Stuck detection test not yet implemented");
        }

        ImGui.SameLine();
        if (ImGui.Button("Test Party Detection"))
        {
            Service.Chat.Print("Party detection test not yet implemented");
        }

        ImGui.SameLine();
        if (ImGui.Button("Force Error State"))
        {
            config.CurrentState = FarmingState.Error;
            Service.Chat.Print("Forced error state for testing");
        }
    }
}
