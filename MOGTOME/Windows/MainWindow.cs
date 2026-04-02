using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Numerics;
using System.IO;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.IPC;

namespace MOGTOME.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Vector2? pendingWindowPosition;
    private bool pendingPositionConditionReset;

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

    public void QueueResetToOrigin()
        => QueueWindowPosition(new Vector2(1f, 1f));

    public void QueueRandomVisibleJump()
        => QueueWindowPosition(GetRandomVisiblePosition());

    public override void PreDraw()
    {
        if (pendingWindowPosition.HasValue)
        {
            Position = pendingWindowPosition.Value;
            PositionCondition = ImGuiCond.Always;
            pendingWindowPosition = null;
            pendingPositionConditionReset = true;
        }
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var state = plugin.State;
        var engine = plugin.Engine;

        // Header
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "M.O.G.T.O.M.E.");
        ImGui.SameLine();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.TextDisabled($"v{version}");
        
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

        ImGui.SameLine(ImGui.GetWindowWidth() - 60);
        if (ImGui.SmallButton("Discord"))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = Plugin.DiscordUrl,
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Plugin.DiscordChannelHint);
        }
        
        ImGui.Separator();

        if (engine == null)
        {
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), "State: Initializing");
            ImGui.Text("Status: Waiting for account and engine initialization.");
            ImGui.Separator();

            if (ImGui.Button("Config", new Vector2(70, 30)))
                plugin.ConfigWindow.Toggle();

            ImGui.SameLine();
            if (ImGui.Button("Stats", new Vector2(60, 30)))
                plugin.StatsWindow.Toggle();

            ImGui.SameLine();
            if (ImGui.Button("Reset", new Vector2(60, 30)))
            {
                state.DutyCounter = 0;
                state.DecumanaCounter = 0;
                config.DutyCounter = 0;
                plugin.ConfigManager.SaveCurrentAccount();
            }

            FinalizePendingWindowPlacement();
            return;
        }

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
                _ = Task.Run(() => engine.Start());
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
            plugin.ConfigManager.SaveCurrentAccount();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stats", new Vector2(60, 30)))
        {
            plugin.StatsWindow.Toggle();
        }

        ImGui.SameLine();
        var krangleEnabled = plugin.Configuration.KrangleNames;
        var krangleText = krangleEnabled ? "Un-Krangle" : "Krangle";
        if (ImGui.Button(krangleText, new Vector2(80, 30)))
        {
            plugin.Configuration.KrangleNames = !krangleEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            KrangleService.ClearCache();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Obfuscate names with military/exercise words.\nUseful for screenshots.");
        }

        ImGui.Spacing();
        if (ImGui.Button("[WARNING TEXT]", new Vector2(140, 28)))
        {
            plugin.WarningTextWindow.Show(force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh Party State", new Vector2(150, 28)))
        {
            engine.RefreshPartyLeaderState();
        }

        ImGui.Separator();

        // Duty Info
        ImGui.Text("Duty Information");
        ImGui.Indent();
        ImGui.Text($"Counter: {state.DutyCounter} / {config.PraetoriumThreshold} Prae | {config.MaxRuns} Total");
        ImGui.Text($"Daily Decu: {state.DecumanaCounter} runs today");

        var currentDuty = state.DutyCounter < config.PraetoriumThreshold
            ? "The Praetorium"
            : "The Porta Decumana";
        ImGui.Text($"Current: {currentDuty}");

        // Reset countdown
        var (countdown, localTime) = plugin.DutyTrackerService.GetResetTimeDisplay();
        ImGui.Text($"Daily reset: {countdown} ({localTime})");

        if (config.TestingModeUnsynced)
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), "[TESTING MODE - Unsynced]");

        if (state.LastCompletionDuration > 0)
            ImGui.Text($"Last Clear: {state.LastCompletionDuration:F0}s");

        if (state.IsInDuty)
            ImGui.Text($"Time in Duty: {state.TimeInDuty:F0}s");

        ImGui.Unindent();
        ImGui.Separator();

        // Debug Section (only visible when debug mode is enabled via /mog debug)
        if (config.DebugModeEnabled)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "Debug Tools");
            ImGui.Indent();

            // Force Path Selection button
            if (ImGui.Button("FORCE PATH SELECTION", new Vector2(200, 25)))
            {
                plugin.AutoDutyPathService.ForcePathSelection(config.PraetoriumPathFileName);
            }
            if (ImGui.IsItemHovered())
            {
                var selectedPath = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName);
                ImGui.SetTooltip($"Force AutoDuty to select:\nMode: Looping\nDuty Mode: Regular\nDuty: The Praetorium (1044)\nPath: {selectedPath}\n\nLogs full AutoDuty structure to Dalamud log.");
            }
            ImGui.TextDisabled($"Result: {plugin.AutoDutyPathService.LastForceResult}");

            // Test Path Index Discovery button
            if (ImGui.Button("TEST PATH INDEX", new Vector2(200, 25)))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    // Test both methods and compare results
                    var correctIndex = plugin.AutoDutyPathService.FindPathIndexFromDictionaryPaths(autoDutyPlugin, "(1044) The Praetorium - W2W 20250716 phecda");
                    var fallbackIndex = plugin.AutoDutyPathService.FindPathIndexByName(autoDutyPlugin, "(1044) The Praetorium - W2W 20250716 phecda");
                    
                    Plugin.Log.Information($"[TEST] DictionaryPaths method result: {correctIndex}");
                    Plugin.Log.Information($"[TEST] PathSelectionsByPath method result: {fallbackIndex}");
                    
                    if (correctIndex != fallbackIndex)
                    {
                        Plugin.Log.Information($"[TEST] *** MISMATCH DETECTED *** DictionaryPaths={correctIndex}, PathSelectionsByPath={fallbackIndex}");
                    }
                    else if (correctIndex >= 0)
                    {
                        Plugin.Log.Information($"[TEST] Both methods agree: Index {correctIndex}");
                    }
                    else
                    {
                        Plugin.Log.Information($"[TEST] Both methods failed to find path");
                    }
                }
                else
                {
                    Plugin.Log.Error("[TEST] Could not find AutoDuty plugin");
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Test path index discovery methods.\nCompares DictionaryPaths vs PathSelectionsByPath.\nResults logged to Dalamud log.");
            }

            // First row: Core research buttons
            if (ImGui.SmallButton("Log AD Structure"))
            {
                plugin.AutoDutyPathService.LogAutoDutyStructure();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Dump AutoDuty plugin structure to Dalamud log for research.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Log Config"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var configObj = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Configuration");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, configObj);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Dump AutoDuty Configuration structure to Dalamud log.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Log Actions"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actions = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Actions");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, actions);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Dump AutoDuty Actions list (currently empty, but shows structure).");
            }

            // Second row: Manager and exploration buttons
            if (ImGui.SmallButton("Log Manager"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actionsManager = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "actions");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, actionsManager);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Dump AutoDuty ActionsManager (contains actionsList with command definitions).");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Explore Paths"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    // Try different approaches to find path data
                    plugin.AutoDutyPathService.ExplorePathData(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Explore different locations for path data (Configuration, MainWindow, etc.).");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Log Tuples"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actionsManager = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "actions");
                    var actionsList = AutoDutyPathService.GetMemberValue(actionsManager?.GetType(), actionsManager, "actionsList");
                    plugin.AutoDutyPathService.LogActionsListTuples(actionsList);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Iterate through actionsList and log each tuple's contents (command definitions).");
            }

            // Third row: Path data exploration
            if (ImGui.SmallButton("Path Selections"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var configObj = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Configuration");
                    var pathSelections = AutoDutyPathService.GetMemberValue(configObj?.GetType(), configObj, "PathSelectionsByPath");
                    plugin.AutoDutyPathService.LogPathSelections(pathSelections);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Explore PathSelectionsByPath dictionary (territory → path mappings).");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Check Current"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogCurrentSelection(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Check what path AutoDuty currently has selected.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Find Methods"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogAutoDutyMethods(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Find Save/Apply/Load methods in AutoDuty.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Config Fields"))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogConfigFields(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Find actual config field names for path selection.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Check Repair"))
            {
                var needsRepair = plugin.RepairService.NeedsRepair();
                var threshold = plugin.ConfigManager.GetActiveConfig().RepairThreshold;
                Plugin.Log.Information($"[DEBUG] Repair Check - Threshold: {threshold}%, Needs Repair: {needsRepair}");
                
                // Check actual equipment durability for detailed info
                try
                {
                    unsafe
                    {
                        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        if (im != null)
                        {
                            var equippedContainer = im->GetInventoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
                            if (equippedContainer != null)
                            {
                                var allItems = new List<string>();
                                var lowItems = new List<string>();
                                
                                for (var i = 0; i < equippedContainer->Size; i++)
                                {
                                    var item = equippedContainer->GetInventorySlot(i);
                                    if (item == null || item->ItemId == 0) continue;

                                    var itemName = $"Item {item->ItemId}";
                                    var actualCondition = item->Condition / 300; // Convert from 0-30000 to 0-100%
                                    allItems.Add($"{itemName}: {actualCondition}%");
                                    
                                    if (actualCondition < threshold)
                                    {
                                        lowItems.Add($"{itemName}: {actualCondition}%");
                                    }
                                }
                                
                                Plugin.Log.Information($"[DEBUG] Equipment durability check - Total items: {allItems.Count}");
                                Plugin.Log.Information($"[DEBUG] All items: {string.Join(", ", allItems)}");
                                
                                if (lowItems.Count > 0)
                                {
                                    Plugin.Log.Information($"[DEBUG] Items below {threshold}%: {string.Join(", ", lowItems)}");
                                }
                                else
                                {
                                    Plugin.Log.Information($"[DEBUG] All equipment above {threshold}% durability");
                                }
                            }
                            else
                            {
                                Plugin.Log.Error("[DEBUG] Failed to get equipped container");
                            }
                        }
                        else
                        {
                            Plugin.Log.Error("[DEBUG] Failed to get InventoryManager instance");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[DEBUG] Equipment check failed: {ex.Message}");
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Check equipment durability and repair status.");
            }

            ImGui.Spacing();

            // Unsynced testing mode checkbox (only visible in debug)
            var testMode = config.TestingModeUnsynced;
            if (ImGui.Checkbox("Testing Mode: Unsynced (no stats)", ref testMode))
            {
                config.TestingModeUnsynced = testMode;
                plugin.ConfigManager.SaveCurrentAccount();
            }
            if (testMode)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "WARNING: Running Unsynced without Level Sync.");
            }

            ImGui.Unindent();
            ImGui.Separator();
        }

        // Party Info
        ImGui.Text("Party");
        ImGui.Indent();
        ImGui.Text($"Leader (runtime): {(state.IsPartyLeader ? "Yes" : "No")}");
        ImGui.Text($"Cross-World: {(config.IsCrossWorldParty ? "Yes" : "No")}");
        ImGui.TextDisabled(config.IsCrossWorldParty
            ? "Source: manual cross-world setting"
            : "Source: same-world auto-detection");
        ImGui.Unindent();
        ImGui.Separator();

        // Subsystem Status
        ImGui.Text("Subsystems");
        ImGui.Indent();
        
        DrawStatusLine("Food", config.FoodItemId > 0 && state.FoodAvailable, FormatConsumableLabel(config.FoodItemName, config.FoodUseHighQuality));
        DrawStatusLine("Potions", config.PotionItemId > 0 && state.PotionsAvailable, FormatConsumableLabel(config.PotionItemName, config.PotionUseHighQuality));
        DrawStatusLine("YesAlready", plugin.YesAlreadyIPC.IsPaused, "Paused by MOGTOME");
        DrawStatusLine(
            "AutoDuty Path",
            plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName),
            plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName));
        
        ImGui.Unindent();
        ImGui.Separator();
        
        // Debug section (only visible when debug mode is enabled)
        if (config.DebugModeEnabled)
        {
            ImGui.Text("Debug Tools");
            ImGui.Indent();
            
            if (ImGui.Button("Log All Configuration"))
            {
                LogAllConfiguration(plugin);
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Log Config Path"))
            {
                Plugin.Log.Information($"[Config] Current account ID: {plugin.ConfigManager.CurrentAccountId}");
                Plugin.Log.Information("[Config] Use Config folder button in Stats window to open config folder");
            }
            
            ImGui.Unindent();
            ImGui.Separator();
        }
        DrawStatusLine("Queue", true, "AutoDuty");
        DrawStatusLine("Bailout", true, $"{config.BailoutTimeout}s");

        ImGui.Unindent();

        FinalizePendingWindowPlacement();
    }

    private void QueueWindowPosition(Vector2 position)
    {
        pendingWindowPosition = position;
    }

    private Vector2 GetRandomVisiblePosition()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var currentSize = Size ?? Vector2.Zero;
        var minimumSize = SizeConstraints?.MinimumSize ?? Vector2.Zero;
        var width = MathF.Max(currentSize.X, minimumSize.X);
        var height = MathF.Max(currentSize.Y, minimumSize.Y);
        var maxX = MathF.Max(1f, viewport.Size.X - width - 20f);
        var maxY = MathF.Max(1f, viewport.Size.Y - height - 20f);
        return new Vector2(1f + (Random.Shared.NextSingle() * maxX), 1f + (Random.Shared.NextSingle() * maxY));
    }

    private void FinalizePendingWindowPlacement()
    {
        if (!pendingPositionConditionReset)
            return;

        pendingPositionConditionReset = false;
        Position = null;
        PositionCondition = ImGuiCond.None;
    }

    private static void DrawStatusLine(string label, bool active, string detail)
    {
        var color = active
            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
            : new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
        
        ImGui.TextColored(color, $"{label}:");
        ImGui.SameLine();
        ImGui.Text(detail);
    }
    
    private static void LogAllConfiguration(Plugin plugin)
    {
        var config = plugin.Configuration;
        
        Plugin.Log.Information("=== MOGTOME COMPLETE CONFIGURATION DUMP ===");
        
        // Account & Character Info
        Plugin.Log.Information($"[Config] Account ID: {plugin.ConfigManager.CurrentAccountId}");
        
        // Food Settings
        Plugin.Log.Information($"[Config] Food Item ID: {config.FoodItemId}");
        Plugin.Log.Information($"[Config] Food Item Name: '{config.FoodItemName}'");
        Plugin.Log.Information($"[Config] Food HQ: {config.FoodUseHighQuality}");
        Plugin.Log.Information($"[Config] Food Available: {config.FoodItemId > 0}");
        
        // Potion Settings
        Plugin.Log.Information($"[Config] Potion Item ID: {config.PotionItemId}");
        Plugin.Log.Information($"[Config] Potion Item Name: '{config.PotionItemName}'");
        Plugin.Log.Information($"[Config] Potion HQ: {config.PotionUseHighQuality}");
        Plugin.Log.Information($"[Config] Potion Target: {config.PotionTarget}");
        
        // Engine Settings
        Plugin.Log.Information($"[Config] Debug Mode: {config.DebugModeEnabled}");
        Plugin.Log.Information($"[Config] Testing Mode Unsynced: {config.TestingModeUnsynced}");
        Plugin.Log.Information($"[Config] Bailout Timeout: {config.BailoutTimeout}s");
        Plugin.Log.Information($"[Config] Show Debug Runs: {config.ShowDebugRuns}");
        
        // Krangle Settings
        Plugin.Log.Information($"[Config] Krangle Names: {config.KrangleNames}");
        Plugin.Log.Information($"[Config] Stats Krangle Names: {config.StatsKrangleNames}");
        
        // Tracking Settings
        Plugin.Log.Information($"[Config] Enable Detailed Tracking: {config.EnableDetailedTracking}");
        
        Plugin.Log.Information("=== END CONFIGURATION DUMP ===");
    }

    private static string FormatConsumableLabel(string name, bool highQuality)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return $"{name} [{(highQuality ? "HQ" : "NQ")}]";
    }
}
