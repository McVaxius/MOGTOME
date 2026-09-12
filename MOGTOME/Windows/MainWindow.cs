using MOGTOME.Localization;
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
        : base(Ui.T("Window_MOGTOMEStatus") + "###MogtomeMain", ImGuiWindowFlags.None)
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
        WindowName = Ui.T("Window_MOGTOMEStatus") + "###MogtomeMain";
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
        plugin.ConsumableInventoryService.Refresh();

        // Header
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "M.O.G.T.O.M.E.");
        ImGui.SameLine();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        UiLayout.TextDisabled(Ui.T("Main_V", version));
        
        // Ko-fi donation button in upper right
        ImGui.SameLine(ImGui.GetWindowWidth() - 120);
        if (UiLayout.SmallButton(Ui.L("Main_KoFi")))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/mcvaxius",
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Main_SupportDevelopmentOnKoFi"));
        }

        ImGui.SameLine(ImGui.GetWindowWidth() - 60);
        if (UiLayout.SmallButton("Discord"))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = Plugin.DiscordUrl,
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Plugin.DiscordChannelHint);
        }
        
        ImGui.Separator();

        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        var language = (int)Ui.Language;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Ui.L("Language_Label"), ref language, ["English", "Français", "Deutsch", "日本語"], 4))
            plugin.ConfigManager.SetUiLanguage((UiLanguage)language);
        ImGui.EndDisabled();
        if (!plugin.CanSelectUiLanguage)
            UiLayout.TextDisabled(Ui.T("Language_WaitForProfile"));
        ImGui.Separator();

        if (engine == null)
        {
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), Ui.T("Main_StateInitializing"));
            ImGui.Text(Ui.T("Main_StatusWaitingForAccountAndEngineInitialization"));
            ImGui.Separator();

            if (UiLayout.Button(Ui.L("Main_Config"), new Vector2(70, 30)))
                plugin.ConfigWindow.Toggle();

            ImGui.SameLine();
            if (UiLayout.Button(Ui.L("Main_Stats"), new Vector2(60, 30)))
                plugin.StatsWindow.Toggle();

            ImGui.SameLine();
            if (UiLayout.Button(Ui.L("Main_Reset"), new Vector2(60, 30)))
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

        ImGui.TextColored(statusColor, Ui.T("Main_State", Ui.EnumLabel(engine.CurrentState)));
        ImGui.TextWrapped(Ui.T("Main_Status", engine.Status));
        ImGui.Separator();

        // Controls Row 1
        if (!engine.IsRunning && !plugin.IsEngineStartQueued)
        {
            if (UiLayout.Button(Ui.L("Main_Start"), new Vector2(80, 30)))
            {
                plugin.QueueEngineStart("main window", notifyChat: false);
            }
        }
        else
        {
            if (UiLayout.Button(Ui.L("Main_Stop"), new Vector2(80, 30)))
            {
                plugin.StopEngine();
            }
        }

        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Main_Config"), new Vector2(70, 30)))
        {
            plugin.ConfigWindow.Toggle();
        }

        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Main_Reset"), new Vector2(60, 30)))
        {
            state.DutyCounter = 0;
            state.DecumanaCounter = 0;
            config.DutyCounter = 0;
            plugin.ConfigManager.SaveCurrentAccount();
        }

        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Main_Stats"), new Vector2(60, 30)))
        {
            plugin.StatsWindow.Toggle();
        }

        ImGui.SameLine();
        var krangleEnabled = plugin.Configuration.KrangleNames;
        var krangleText = krangleEnabled ? Ui.T("Main_UnKrangle") : Ui.T("Main_Krangle");
        if (UiLayout.Button(krangleText + "###Krangle", new Vector2(80, 30)))
        {
            plugin.Configuration.KrangleNames = !krangleEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            KrangleService.ClearCache();
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Main_ObfuscateNamesWithMilitaryExerciseWordsUseful"));
        }

        ImGui.Spacing();
        var stopNextLabel = engine.StopAfterNextSuccessfulRunArmed
            ? Ui.T("Main_CancelStopAfterNext")
            : Ui.T("Main_StopAfterNextSuccess");
        if (UiLayout.Button(stopNextLabel + "###StopNext", new Vector2(190, 28)))
            engine.ToggleStopAfterNextSuccessfulRun();
        UiLayout.TextDisabled(engine.StopAfterNextSuccessfulRunArmed
            ? Ui.T("Main_ArmedStopsAfterASuccessfulRunIs")
            : Ui.T("Main_RuntimeOnlyAbortedRunsDoNotConsume"));

        ImGui.Spacing();
        if (UiLayout.Button(Ui.L("Main_HELP"), new Vector2(140, 28)))
        {
            plugin.WarningTextWindow.Show(force: true);
        }

        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Config_RefreshPartyState"), new Vector2(150, 28)))
        {
            engine.RefreshPartyLeaderState();
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Config_OneTimeSameWorldLeaderDetectionUse"));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1.0f, 1.0f), Ui.T("Main_MainWindowSettings"));
        var useAdsExperimental = config.UseAdsExperimental;
        if (ImGui.Checkbox(Ui.L("Main_AIDutySolverADS"), ref useAdsExperimental))
        {
            ToggleAdsExperimental(useAdsExperimental);
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Main_DutyBackendSelectorEnablingADSImmediatelySends"));
        }
        ImGui.SameLine();
        plugin.ConfigWindow.DrawCombatRotationSelector("Main");
        UiLayout.TextDisabled(config.UseAdsExperimental
            ? Ui.T("Main_ADSDutyBackendActiveADSAlsoHandles")
            : Ui.T("Main_AutoDutyDutyBackendActiveADSIsOptional"));

        ImGui.Separator();

        // Duty Info
        ImGui.Text(Ui.T("Main_DutyInformation"));
        ImGui.Indent();
        ImGui.Text(Ui.T("Main_CounterPraeDailyPraetoriumLimit", state.DutyCounter, config.PraetoriumThreshold, config.MaxRuns));
        ImGui.Text(Ui.T("Main_DailyDecuRunsToday", state.DecumanaCounter));

        var currentDuty = state.DutyCounter < config.PraetoriumThreshold
            ? Ui.Duty(1044).Render()
            : Ui.Duty(1048).Render();
        ImGui.Text(Ui.T("Main_Current", currentDuty));

        // Reset countdown
        var (countdown, localTime) = plugin.DutyTrackerService.GetResetTimeDisplay();
        ImGui.Text(Ui.T("Main_DailyReset", countdown, localTime));

        if (config.TestingModeUnsynced)
            ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), Ui.T("Main_TESTINGMODEUnsynced"));

        if (state.LastCompletionDuration > 0)
            ImGui.Text(Ui.T("Main_LastClearS", state.LastCompletionDuration));

        if (state.IsInDuty)
            ImGui.Text(Ui.T("Main_TimeInDutyS", state.TimeInDuty));

        if (plugin.DeathTrackingService.IsActive)
        {
            var deaths = plugin.DeathTrackingService.CurrentSnapshot;
            ImGui.Text(Ui.T("Main_DeathsThisRunSelfOthersAll", deaths.SelfDeathCount, deaths.OtherDeathCount, deaths.TotalDeathCount));
        }

        ImGui.Unindent();
        ImGui.Separator();

        // Debug Section (only visible when debug mode is enabled via /mog debug)
        if (config.DebugModeEnabled && !config.UseAdsExperimental)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), Ui.T("Main_DebugTools"));
            ImGui.Indent();

            // Force Path Selection button
            if (UiLayout.Button(Ui.L("Main_FORCEPATHSELECTION"), new Vector2(200, 25)))
            {
                plugin.AutoDutyPathService.ForcePathSelection(config.PraetoriumPathFileName);
            }
            if (ImGui.IsItemHovered())
            {
                var selectedPath = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName);
                UiLayout.SetTooltip(Ui.T("Main_ForceAutoDutyToSelectModeLoopingDuty", selectedPath));
            }
            UiLayout.TextDisabled(Ui.T("Main_Result", plugin.AutoDutyPathService.ForceResult));

            // Test Path Index Discovery button
            if (UiLayout.Button(Ui.L("Main_TESTPATHINDEX"), new Vector2(200, 25)))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var selectedPathFileName = plugin.AutoDutyPathService.ResolvePraetoriumPathFileName(config.PraetoriumPathFileName);
                    var selectedPathName = Path.GetFileNameWithoutExtension(selectedPathFileName) ?? selectedPathFileName;

                    // Test both methods and compare results
                    var correctIndex = plugin.AutoDutyPathService.FindPathIndexFromDictionaryPaths(autoDutyPlugin, selectedPathName);
                    var fallbackIndex = plugin.AutoDutyPathService.FindPathIndexByName(autoDutyPlugin, selectedPathName);
                    
                    Plugin.Log.Information($"[TEST] Selected path target: {selectedPathFileName}");
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
                UiLayout.SetTooltip(Ui.T("Main_TestPathIndexDiscoveryMethodsComparesDictionaryPaths"));
            }

            // First row: Core research buttons
            if (UiLayout.SmallButton(Ui.L("Main_LogADStructure")))
            {
                plugin.AutoDutyPathService.LogAutoDutyStructure();
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyPluginStructureToDalamudLog"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_LogConfig")))
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
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyConfigurationStructureToDalamudLog"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_LogActions")))
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
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyActionsListCurrentlyEmptyBut"));
            }

            // Second row: Manager and exploration buttons
            if (UiLayout.SmallButton(Ui.L("Main_LogManager")))
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
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyActionsManagerContainsActionsListWithCommand"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_ExplorePaths")))
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
                UiLayout.SetTooltip(Ui.T("Main_ExploreDifferentLocationsForPathDataConfiguration"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_LogTuples")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actionsManager = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "actions");
                    var actionsList = actionsManager == null
                        ? null
                        : AutoDutyPathService.GetMemberValue(actionsManager.GetType(), actionsManager, "actionsList");
                    plugin.AutoDutyPathService.LogActionsListTuples(actionsList);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_IterateThroughActionsListAndLogEachTuple"));
            }

            // Third row: Path data exploration
            if (UiLayout.SmallButton(Ui.L("Main_PathSelections")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var configObj = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Configuration");
                    var pathSelections = configObj == null
                        ? null
                        : AutoDutyPathService.GetMemberValue(configObj.GetType(), configObj, "PathSelectionsByPath");
                    plugin.AutoDutyPathService.LogPathSelections(pathSelections, plugin.Configuration.PraetoriumPathFileName);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_ExplorePathSelectionsByPathDictionaryTerritoryPathMappings"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_CheckCurrent")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogCurrentSelection(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_CheckWhatPathAutoDutyCurrentlyHasSelected"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_FindMethods")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogAutoDutyMethods(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_FindSaveApplyLoadMethodsInAutoDuty"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_ConfigFields")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogConfigFields(autoDutyPlugin, plugin.Configuration.PraetoriumPathFileName);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_FindActualConfigFieldNamesForPath"));
            }

            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.L("Main_CheckRepair")))
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

                                    var itemName = Ui.T("Main_Item", item->ItemId);
                                    var actualCondition = item->Condition / 300; // Convert from 0-30000 to 0-100%
                                    allItems.Add(string.Create(Ui.Culture, $"{itemName}: {actualCondition}%"));
                                    
                                    if (actualCondition < threshold)
                                    {
                                        lowItems.Add(string.Create(Ui.Culture, $"{itemName}: {actualCondition}%"));
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
                UiLayout.SetTooltip(Ui.T("Main_CheckEquipmentDurabilityAndRepairStatus"));
            }

            ImGui.Spacing();

            // Unsynced testing mode checkbox (only visible in debug)
            var testMode = config.TestingModeUnsynced;
            if (ImGui.Checkbox(Ui.L("Main_TestingModeUnsyncedNoStats"), ref testMode))
            {
                config.TestingModeUnsynced = testMode;
                plugin.ConfigManager.SaveCurrentAccount();
            }
            if (testMode)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), Ui.T("Main_WARNINGRunningUnsyncedWithoutLevelSync"));
            }

            ImGui.Unindent();
            ImGui.Separator();
        }
        else if (config.DebugModeEnabled && config.UseAdsExperimental)
        {
            var praeSelection = plugin.DutyAutomationService.GetPraetoriumSelectionInfo();

            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), Ui.T("Main_DebugTools"));
            ImGui.Indent();

            if (UiLayout.Button(Ui.L("Main_TESTPRAEINDEX"), new Vector2(200, 25)))
            {
                plugin.DutyAutomationService.LogPraetoriumSelectionInfo();
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_LogsPraetoriumCallbackIndexAndMissingOptional"));
            }

            ImGui.Text(Ui.T("Main_PraetoriumCallback", praeSelection.CallbackCommand));
            ImGui.Text(Ui.T("Main_MissingOptionalUnlocks", praeSelection.MissingUnlockCount));

            foreach (var unlock in praeSelection.Unlocks)
            {
                var color = unlock.IsUnlocked
                    ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f)
                    : new Vector4(1.0f, 0.45f, 0.45f, 1.0f);
                var status = unlock.IsUnlocked ? Ui.T("Main_Unlocked") : Ui.T("Main_Missing");
                ImGui.TextColored(color, string.Create(Ui.Culture, $"{Ui.Duty(unlock.TerritoryTypeId).Render()}: {status}"));
                if (ImGui.IsItemHovered())
                {
                    UiLayout.SetTooltip(Ui.T("Main_QuestIDs", unlock.QuestSummary));
                }
            }

            UiLayout.TextDisabled(Ui.T("Main_ADSModeActiveAutoDutyReflectionPathDebug"));
            ImGui.Unindent();
            ImGui.Separator();
        }

        // Party Info
        ImGui.Text(Ui.T("Config_Party"));
        ImGui.Indent();
        ImGui.Text(Ui.T("Main_LeaderRuntime", (state.IsPartyLeader ? Ui.T("Config_Yes") : Ui.T("Config_No"))));
        ImGui.Text(Ui.T("Main_CrossWorld", (config.IsCrossWorldParty ? Ui.T("Config_Yes") : Ui.T("Config_No"))));
        UiLayout.TextDisabled(config.IsCrossWorldParty
            ? Ui.T("Main_SourceConfiguredCrossWorldRole")
            : Ui.T("Config_SourceConfiguredRoleRefreshPartyStateOnly"));
        ImGui.Unindent();
        ImGui.Separator();

        // Subsystem Status
        ImGui.Text(Ui.T("Main_Subsystems"));
        ImGui.Indent();
        
        DrawStatusLine(Ui.T("Config_Food"), config.FoodItemId > 0 && state.FoodAvailable, FormatConsumableLabel(config.FoodItemId > 0 ? Ui.Item((uint)config.FoodItemId).Render() : string.Empty, config.FoodUseHighQuality, state.FoodExactCount));
        DrawStatusLine(Ui.T("Config_Potions"), config.PotionItemId > 0 && state.PotionsAvailable, FormatConsumableLabel(config.PotionItemId > 0 ? Ui.Item((uint)config.PotionItemId).Render() : string.Empty, config.PotionUseHighQuality, state.PotionExactCount));
        DrawStatusLine("YesAlready", plugin.YesAlreadyIPC.IsPaused, Ui.T("Main_PausedByMOGTOME"));
        DrawStatusLine(
            Ui.T("Main_DutyBackend"),
            plugin.DutyAutomationService.GetSubsystemHealthy(),
            plugin.DutyAutomationService.GetSubsystemStatusText().Render());
        
        ImGui.Unindent();
        ImGui.Separator();
        
        // Debug section (only visible when debug mode is enabled)
        if (config.DebugModeEnabled)
        {
            ImGui.Text(Ui.T("Main_DebugTools"));
            ImGui.Indent();
            
            if (UiLayout.Button(Ui.L("Main_LogAllConfiguration")))
            {
                LogAllConfiguration(plugin);
            }
            
            ImGui.SameLine();
            if (UiLayout.Button(Ui.L("Main_LogConfigPath")))
            {
                Plugin.Log.Information($"[Config] Current account ID: {plugin.ConfigManager.CurrentAccountId}");
                Plugin.Log.Information("[Config] Use Config folder button in Stats window to open config folder");
            }
            
            ImGui.Unindent();
            ImGui.Separator();
        }
        DrawStatusLine(Ui.T("Main_QueueRole"), true, plugin.DutyAutomationService.GetQueueStatusText().Render());
        if (config.UseAdsExperimental)
            DrawStatusLine(Ui.T("Main_ADSRuntime"), true, plugin.DutyAutomationService.GetAdsRuntimeStatusText().Render());
        DrawStatusLine(Ui.T("Main_Bailout"), true, Ui.T("Main_S", config.BailoutTimeout));

        ImGui.Unindent();

        FinalizePendingWindowPlacement();
    }

    private void ToggleAdsExperimental(bool enable)
    {
        plugin.Configuration.UseAdsExperimental = enable;
        plugin.ConfigManager.SaveCurrentAccount();
        plugin.ConfigManager.NotifyConfigurationChanged(force: true);

        if (!enable)
            return;

        _ = Task.Run(async () => await plugin.DutyAutomationService.EnsureAutoDutyDisabledForAdsAsync("ADS toggle"));
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
        
        ImGui.TextColored(color, string.Create(Ui.Culture, $"{label}:"));
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

    private static string FormatConsumableLabel(string name, bool highQuality, int exactCount)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Ui.T("Main_NotConfigured");

        return Ui.T("Main_X", name, (highQuality ? Ui.T("Main_HQ") : Ui.T("Main_NQ")), Math.Max(0, exactCount));
    }
}
