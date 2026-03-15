using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("MOGTOME - Status##MogtomeMain", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(700, 1200),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var state = plugin.State;
        var engine = plugin.Engine;

        // Header
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "M.O.G.T.O.M.E.");
        ImGui.SameLine();
        ImGui.TextDisabled("v0.0.0.3");
        
        // Ko-fi donation button in upper right
        ImGui.SameLine(ImGui.GetWindowWidth() - 120);
        if (ImGui.SmallButton("\u2661 Ko-fi \u2661"))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/mcvaxius",
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Support development on Ko-fi");
        }
        
        ImGui.Separator();

        // Engine Status
        var statusColor = engine.CurrentState switch
        {
            EngineState.Idle => new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
            EngineState.InDuty => new Vector4(0.0f, 1.0f, 0.0f, 1.0f),
            EngineState.Queueing => new Vector4(1.0f, 1.0f, 0.0f, 1.0f),
            EngineState.RepairingOutside => new Vector4(1.0f, 0.5f, 0.0f, 1.0f),
            EngineState.Stopping => new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
            _ => new Vector4(0.7f, 0.7f, 1.0f, 1.0f),
        };

        ImGui.TextColored(statusColor, $"State: {engine.CurrentState}");
        ImGui.Text($"Status: {engine.StatusMessage}");
        ImGui.Separator();

        // Controls Row 1
        if (!engine.IsRunning)
        {
            if (ImGui.Button("Start", new Vector2(80, 30)))
            {
                engine.Start();
            }
        }
        else
        {
            if (ImGui.Button("Stop", new Vector2(80, 30)))
            {
                engine.Stop();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Config", new Vector2(70, 30)))
        {
            plugin.ConfigWindow.Toggle();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset", new Vector2(60, 30)))
        {
            state.DutyCounter = 0;
            state.DecumanaCounter = 0;
            config.DutyCounter = 0;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stats", new Vector2(60, 30)))
        {
            plugin.StatsWindow.Toggle();
        }

        ImGui.Separator();

        // Duty Info
        ImGui.Text("Duty Information");
        ImGui.Indent();
        ImGui.Text($"Counter: {state.DutyCounter} / {config.PraetoriumThreshold} Prae | {config.MaxRuns} Total");

        var currentDuty = state.DutyCounter < config.PraetoriumThreshold
            ? "The Praetorium"
            : "The Porta Decumana";
        ImGui.Text($"Current: {currentDuty}");

        if (config.TestingModeUnsynced)
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), "[TESTING MODE - Unsynced]");

        if (state.LastCompletionDuration > 0)
            ImGui.Text($"Last Clear: {state.LastCompletionDuration:F0}s");

        if (state.IsInDuty)
            ImGui.Text($"Time in Duty: {state.TimeInDuty:F0}s");

        ImGui.Unindent();
        ImGui.Separator();

        // Party Info
        ImGui.Text("Party");
        ImGui.Indent();
        ImGui.Text($"Leader: {(state.IsPartyLeader ? "Yes" : "No")}");
        ImGui.Text($"Cross-World: {(config.IsCrossWorldParty ? "Yes" : "No")}");
        ImGui.Unindent();
        ImGui.Separator();

        // Subsystem Status
        ImGui.Text("Subsystems");
        ImGui.Indent();

        DrawStatusLine("Food", config.FoodItemId > 0, config.FoodItemName);
        DrawStatusLine("Potions", config.PotionItemId > 0 && state.PotionsAvailable, config.PotionItemName);
        DrawStatusLine("YesAlready", plugin.YesAlreadyIPC.IsPaused, "Paused by MOGTOME");
        DrawStatusLine("AutoDuty Path", plugin.AutoDutyPathService.PathExists(), "Praetorium W2W");
        DrawStatusLine("Queue", true, "AutoDuty");
        DrawStatusLine("Bailout", true, $"{config.BailoutTimeout}s");

        ImGui.Unindent();
    }

    private static void DrawStatusLine(string label, bool active, string detail)
    {
        var color = active
            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
        var icon = active ? "[ON]" : "[OFF]";

        ImGui.TextColored(color, $"{icon} {label}");
        if (!string.IsNullOrEmpty(detail))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"- {detail}");
        }
    }
}
