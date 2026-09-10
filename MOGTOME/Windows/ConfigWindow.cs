using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MOGTOME.Models;

namespace MOGTOME.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog Log;
    private Vector2? pendingWindowPosition;
    private bool pendingPositionConditionReset;

    // Food/Pot search state
    private string foodSearch = "";
    private string potionSearch = "";
    private List<(uint Id, string Name)> foodItems = new();
    private List<(uint Id, string Name)> potionItems = new();
    private bool itemsLoaded = false;

    // Dependency check cache
    private DateTime lastDepCheck = DateTime.MinValue;
    private bool depRsr, depBmr, depVbm, depWrath, depVnav, depYesAlready, depTextAdv, depXaSlave, depAutoDuty, depAds, depLifestream;
    private bool depCustomRes, depChillframes, depKrangler, depDps, depTtsl;
    private bool depTwistOfFayteInstalled, depTwistOfFayteEnabled;
    private bool allDepsGreen = false;
    private const int SetupWizardVersion = 1;
    private int setupWizardStep;
    private string setupWizardAccountId = string.Empty;
    private bool setupWizardAutoSelectPending = true;

    // Plugin repo URLs for clipboard
    private static readonly Dictionary<string, string> PluginRepos = new()
    {
        { "RSR", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json" },
        { "BMR", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json" },
        { "VBM", "https://puni.sh/api/repository/veyn" },
        { "vnavmesh", "https://raw.githubusercontent.com/awgil/ffxiv_plugin_distribution/master/pluginmaster.json" },
        { "Lifestream", "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json" },
        { "TextAdvance", "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json" },
        { "XASlave", "https://aethertek.io/x.json" },
        { "AutoDuty", "https://puni.sh/api/repository/erdelf" },
        { "ADS", "https://aethertek.io/x.json" },
        { "CustomResolution", "https://raw.githubusercontent.com/0x0ade/CustomResolution/main/pluginmaster.json" },
    };

    public ConfigWindow(Plugin plugin, IPluginLog log)
        : base("MOGTOME - Configuration##MogtomeConfig", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        this.Log = log;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 600),
            MaximumSize = new Vector2(750, 1000),
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

    private void EnsureItemsLoaded()
    {
        if (itemsLoaded) return;
        itemsLoaded = true;

        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (itemSheet == null) return;

            foreach (var item in itemSheet)
            {
                if (item.RowId == 0) continue;
                var name = item.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var catId = item.ItemUICategory.RowId;

                // Category 44 = Medicine (potions like Gemdraught)
                // Category 46 = Meal (food that gives Well Fed buff)
                if (catId == 44 || catId == 46)
                {
                    // Medicine (potions) = category 44
                    if (catId == 44)
                        potionItems.Add((item.RowId, name));
                    // Meals (food) = category 46
                    if (catId == 46)
                        foodItems.Add((item.RowId, name));
                }
            }

            Plugin.Log.Information($"[ConfigWindow] Loaded {foodItems.Count} food items, {potionItems.Count} potion items from Lumina");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[ConfigWindow] Failed to load items: {ex.Message}");
        }
    }

    public override void Draw()
    {
        EnsureItemsLoaded();
        var config = plugin.Configuration;
        var changed = false;

        // Check dependencies periodically
        var now = DateTime.UtcNow;
        if ((now - lastDepCheck).TotalSeconds > 5)
        {
            CheckDependencies();
            lastDepCheck = now;
        }

        DrawCombatRotationSelector("ConfigHeader");
        ImGui.Separator();

        if (ImGui.BeginTabBar("ConfigTabs"))
        {
            var currentAccountId = plugin.ConfigManager.CurrentAccountId;
            if (!string.Equals(setupWizardAccountId, currentAccountId, StringComparison.Ordinal))
            {
                setupWizardAccountId = currentAccountId;
                setupWizardStep = 0;
                setupWizardAutoSelectPending = true;
            }

            var wizardIncomplete = config.SetupWizardCompletedVersion < SetupWizardVersion;
            if (ImGui.BeginTabItem(
                    "Setup Wizard",
                    wizardIncomplete && setupWizardAutoSelectPending
                        ? ImGuiTabItemFlags.SetSelected
                        : ImGuiTabItemFlags.None))
            {
                setupWizardAutoSelectPending = false;
                changed |= DrawSetupWizardTab(config);
                ImGui.EndTabItem();
            }

            // Dependency Check tab - force user here if not all green
            var depColor = allDepsGreen ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, depColor);
            var depOpen = ImGui.BeginTabItem("Dependency Check");
            ImGui.PopStyleColor();
            if (depOpen)
            {
                DrawDependencyCheckTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Party"))
            {
                changed |= DrawPartyTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Duty"))
            {
                changed |= DrawDutyTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Food & Pots"))
            {
                changed |= DrawFoodPotTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Repair"))
            {
                changed |= DrawRepairTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Advanced"))
            {
                changed |= DrawAdvancedTab(config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (changed)
        {
            plugin.ConfigManager.SaveCurrentAccount();
            plugin.ConfigManager.NotifyConfigurationChanged(force: true);
        }

        FinalizePendingWindowPlacement();
    }

    private void CheckDependencies()
    {
        try
        {
            var installed = Plugin.PluginInterface.InstalledPlugins;
            depRsr = false;
            depBmr = false;
            depVbm = false;
            depWrath = false;
            depVnav = false;
            depYesAlready = false;
            depTextAdv = false;
            depXaSlave = false;
            depAutoDuty = false;
            depAds = false;
            depLifestream = false;
            depCustomRes = false;
            depChillframes = false;
            depKrangler = false;
            depDps = false;
            depTtsl = false;
            depTwistOfFayteInstalled = false;
            depTwistOfFayteEnabled = false;

            var twistOfFayteStatus = plugin.ConflictPluginService.GetTwistOfFayteStatus();
            depTwistOfFayteInstalled = twistOfFayteStatus.IsInstalled;
            depTwistOfFayteEnabled = twistOfFayteStatus.IsLoaded;

            foreach (var p in installed)
            {
                if (!p.IsLoaded) continue;
                switch (p.InternalName)
                {
                    case "RotationSolver":
                    case "RotationSolverReborn": depRsr = true; break;
                    case "BossModReborn": depBmr = true; break;
                    case "BossMod": depVbm = true; break;
                    case "vnavmesh": depVnav = true; break;
                    case "YesAlready": depYesAlready = true; break;
                    case "TextAdvance": depTextAdv = true; break;
                    case "AutoDuty": depAutoDuty = true; break;
                    case "ADS": depAds = true; break;
                    case "Lifestream": depLifestream = true; break;
                    case "ChillFrames": depChillframes = true; break;
                    case "Krangler": depKrangler = true; break;
                    case "DPS": depDps = true; break;
                    case "TTSL": depTtsl = true; break;
                }

                if (!depWrath &&
                    (p.InternalName.Contains("WrathCombo", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.Contains("Wrath Combo", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.Contains("WrathCombo", StringComparison.OrdinalIgnoreCase)))
                {
                    depWrath = true;
                }

                if (!depXaSlave &&
                    (IsXaSlavePlugin(p.InternalName) || IsXaSlavePlugin(p.Name)))
                {
                    depXaSlave = true;
                }

                if (!depLifestream &&
                    (p.Name.Contains("Lifestream", StringComparison.OrdinalIgnoreCase) ||
                     p.InternalName.Contains("Lifestream", StringComparison.OrdinalIgnoreCase)))
                {
                    depLifestream = true;
                }
                
                // Check for CustomResolution with random suffix
                if (p.InternalName.StartsWith("CustomResolution")) depCustomRes = true;
            }

            var pathOk = plugin.AutoDutyPathService.PathExists(plugin.Configuration.PraetoriumPathFileName);
            var useAdsExperimental = plugin.Configuration.UseAdsExperimental;
            var backendReady = useAdsExperimental
                ? depAds
                : depAutoDuty && pathOk;
            var providerReady = plugin.Configuration.CombatProvider switch
            {
                CombatProvider.Bmr => depBmr,
                CombatProvider.Vbm => depVbm,
                CombatProvider.Rsr => depRsr && (depBmr || depVbm),
                CombatProvider.Wrath => depWrath,
                _ => false,
            };
            allDepsGreen = providerReady && depVnav && depYesAlready && depXaSlave && backendReady;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[ConfigWindow] Dependency check failed: {ex.Message}");
        }
    }

    private void DrawDependencyCheckTab(Configuration config)
    {
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Backend Mode");
        var useAdsExperimental = config.UseAdsExperimental;
        if (ImGui.Checkbox("Use ADS (primary backend)", ref useAdsExperimental))
        {
            ToggleAdsExperimental(useAdsExperimental);
            config = plugin.Configuration;
        }
        ImGui.TextDisabled(config.UseAdsExperimental
            ? "ADS handles duty automation and inn return. AutoDuty is an alternative backend."
            : "AutoDuty is the alternative duty backend. ADS is required only when ADS mode is selected or /mog inn is requested.");
        ImGui.Spacing();

        DrawCombatRotationSelector("Dependencies");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Required Plugins");
        if (!allDepsGreen)
        {
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Setup requirements are incomplete. The wizard is advisory and does not block Start.");
        }
        ImGui.Separator();

        switch (config.CombatProvider)
        {
            case CombatProvider.Rsr:
                DrawDepLine("RSR (RotationSolverReborn)", depRsr, depRsr ? "Installed" : "NOT FOUND", "RSR");
                DrawDepLine("BossMod passive support (BMR or VBM)", depBmr || depVbm,
                    depBmr ? "BMR loaded" : depVbm ? "VBM loaded" : "NOT FOUND", "BMR");
                break;
            case CombatProvider.Bmr:
                DrawDepLine("BossModReborn (BMR)", depBmr, depBmr ? "Installed" : "NOT FOUND", "BMR");
                break;
            case CombatProvider.Vbm:
                DrawDepLine("BossMod (VBM)", depVbm, depVbm ? "Installed" : "NOT FOUND", "VBM");
                break;
            case CombatProvider.Wrath:
                DrawDepLine("Wrath Combo", depWrath, depWrath ? "Installed" : "NOT FOUND", null);
                break;
        }

        DrawRemainingDependencies(config);
    }

    public void DrawCombatRotationSelector(string id)
    {
        if ((DateTime.UtcNow - lastDepCheck).TotalSeconds > 5)
        {
            CheckDependencies();
            lastDepCheck = DateTime.UtcNow;
        }
        var config = plugin.Configuration;
        ImGui.PushID(id);
        ImGui.BeginDisabled(plugin.Engine?.IsRunning == true || plugin.Engine?.IsStartupPending == true);
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Combat rotation", config.CombatProvider.ToString().ToUpperInvariant()))
        {
            foreach (var provider in Enum.GetValues<CombatProvider>())
            {
                var selected = config.CombatProvider == provider;
                if (ImGui.Selectable(provider.ToString().ToUpperInvariant(), selected))
                {
                    config.CombatProvider = provider;
                    plugin.ConfigManager.SaveCurrentAccount();
                    plugin.ConfigManager.NotifyConfigurationChanged(force: true);
                    lastDepCheck = DateTime.MinValue;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.TextWrapped(config.CombatProvider switch
        {
            CombatProvider.Rsr => "RSR handles attacks; the loaded BossMod variant uses passive - tank/melee/ranged automatically.",
            CombatProvider.Bmr => "BMR handles attacks and movement with FRENRIDER - TANK/MELEE/RANGED, or your manual preset.",
            CombatProvider.Vbm => "VBM handles attacks and movement with FRENRIDER - TANK/MELEE/RANGED, or your manual preset.",
            _ => "Wrath handles attacks using its current settings.",
        });

        if (config.CombatProvider is CombatProvider.Bmr or CombatProvider.Vbm)
        {
            var manualPreset = config.UseManualBossModPreset;
            if (ImGui.Checkbox("Use manual BossMod preset", ref manualPreset))
            {
                config.UseManualBossModPreset = manualPreset;
                plugin.ConfigManager.SaveCurrentAccount();
            }

            if (manualPreset)
            {
                var presetName = config.ManualBossModPresetName;
                if (ImGui.InputText("Preset Name", ref presetName, 128))
                {
                    config.ManualBossModPresetName = presetName;
                    plugin.ConfigManager.SaveCurrentAccount();
                }
                if (string.IsNullOrWhiteSpace(config.ManualBossModPresetName))
                    ImGui.TextWrapped("Enter an existing preset name before starting.");
            }
            else
            {
                ImGui.TextWrapped("MOGTOME selects its packaged active preset by current role at each duty start.");
            }
        }
        ImGui.EndDisabled();
        if (depBmr && depVbm)
            ImGui.TextWrapped("Both BossMod variants are loaded. Start disables VBM and reloads BMR; VBM selection changes to BMR, while RSR stays selected.");
        ImGui.PopID();
    }

    private void DrawRemainingDependencies(Configuration config)
    {
        // VNAV
        DrawDepLine("vnavmesh", depVnav, depVnav ? "Installed" : "NOT FOUND", "vnavmesh");

        // XA Slave
        DrawDepLine("XA Slave", depXaSlave, depXaSlave ? "Installed" : "NOT FOUND", "XASlave");
        ImGui.TextDisabled("MOGTOME runs /xa skipcutscenes on before every manual start.");

        DrawDepLine("YesAlready", depYesAlready, depYesAlready ? "Installed" : "NOT FOUND", null);

        if (config.UseAdsExperimental)
        {
            DrawDepLine("ADS", depAds, depAds ? "Installed" : "NOT FOUND", "ADS");
        }
        else
        {
            DrawDepLine("AutoDuty", depAutoDuty, depAutoDuty ? "Installed" : "NOT FOUND", "AutoDuty");
            DrawDepLineOptional("ADS", depAds, "Optional. Enables /mog inn delegation.");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Optional Plugins");
        ImGui.Separator();

        DrawDepLineOptional("Lifestream", depLifestream, "Optional. Not required by MOGTOME.");
        DrawDepLineOptional("TextAdvance", depTextAdv, "Optional. Not required by MOGTOME.");
        DrawDepLineOptional("Krangler", depKrangler, "Recommended. Appearance/nameplate randomizer.");
        DrawDepLineOptional("CustomResolution (1pp by 0x0ade)", depCustomRes, "Experimental and not recommended. Low Spec Helper.",
		"Maybe Crashy with 2+ clients");
        DrawDepLineOptional("ChillFrames", depChillframes, "Will cause some issues sometimes.");
        DrawDepLineOptional(
            "DPS (Dhog Potato System)",
            depDps,
            "Experimental and not recommended. Low Spec Helper.",
            "May have pathing issues.");
        DrawDepLineOptional("Thick Thighs Save Lives", depTtsl, "Experimental and not recommended. Remote HUD + Control.",
            "Very new plugin unknown issues.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Conflicting Plugins");
        ImGui.Separator();

        DrawConflictPluginLine(
            "Twist of Fayte",
            depTwistOfFayteInstalled,
            depTwistOfFayteEnabled,
            () =>
            {
                _ = plugin.ConflictPluginService.EnsureTwistOfFayteDisabledAsync("Dependency Check", showPopup: false);
                lastDepCheck = DateTime.MinValue;
            });

        ImGui.Spacing();
        if (!config.UseAdsExperimental)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "AutoDuty Path");
            ImGui.Separator();

            var pathDisplayName = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName);
            var pathExists = plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);
            ImGui.TextColored(
                pathExists ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1),
                pathExists ? $"Praetorium path: INSTALLED ({pathDisplayName})" : $"Praetorium path: NOT FOUND ({pathDisplayName})");

            if (ImGui.Button("Install Bundled Praetorium Paths"))
            {
                _ = Task.Run(async () => await plugin.AutoDutyPathService.EnsurePathExists());
            }
            ImGui.TextDisabled("This copies the bundled W2W Praetorium path files from MOGTOME's data folder into AutoDuty's paths folder.");
            ImGui.TextDisabled("ADS is optional in AutoDuty mode; /mog inn needs ADS when explicitly requested.");
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "ADS Mode Notes");
            ImGui.Separator();
            ImGui.TextWrapped("ADS mode disables AutoDuty immediately and again on Start. Queueing uses ADS ownership plus direct duty finder registration.");
            ImGui.TextWrapped("Repair/inn/leave switch to /ads npcrepair, /ads selfrepair, /ads enterinn, and /ads leave.");
            ImGui.TextWrapped("The checkbox changes the duty backend. ADS is required only while ADS mode is selected.");
            ImGui.Spacing();
        }
    }

    private static void DrawDepLine(string name, bool ok, string detail, string? repoKey)
    {
        var color = ok ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
        var icon = ok ? "[OK]" : "[!!]";
        ImGui.TextColored(color, $"{icon} {name}");
        ImGui.SameLine();
        ImGui.TextDisabled($"- {detail}");

        if (!ok && repoKey != null && PluginRepos.TryGetValue(repoKey, out var repo) && !string.IsNullOrEmpty(repo))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy Repo##{name}"))
            {
                ImGui.SetClipboardText(repo);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Copy repo URL to clipboard, then add to\nDalamud Settings > Experimental > Custom Plugin Repositories");
            }
        }
    }

    private static void DrawDepLineColor(string name, Vector4 color, string detail)
    {
        ImGui.TextColored(color, $"[!!] {name}");
        ImGui.SameLine();
        ImGui.TextColored(color, $"- {detail}");
    }

    private static bool IsXaSlavePlugin(string? name)
        => string.Equals(NormalizePluginName(name), "XASlave", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePluginName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return new string(name.Where(char.IsLetterOrDigit).ToArray());
    }

    private static void DrawDepLineOptional(string name, bool exists, string? missingDetail = null, string? installedWarning = null)
    {
        var color = exists ? new Vector4(0, 1, 0, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
        var icon = exists ? "[OK]" : "[--]";
        ImGui.TextColored(color, $"{icon} {name}");
        ImGui.SameLine();
        ImGui.TextDisabled(exists ? "Installed" : (string.IsNullOrWhiteSpace(missingDetail) ? "Not installed (optional)" : missingDetail));

        if (exists && !string.IsNullOrWhiteSpace(installedWarning))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0, 0, 1), installedWarning);
        }
    }

    private static void DrawConflictPluginLine(string name, bool installed, bool enabled, System.Action disableAction)
    {
        var color = enabled ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1);
        var icon = enabled ? "[!!]" : "[OK]";
        var detail = enabled
            ? "Enabled - known MOGTOME conflict"
            : installed
                ? "Installed but disabled"
                : "Not installed";

        ImGui.TextColored(color, $"{icon} {name}");
        ImGui.SameLine();
        ImGui.TextColored(color, $"- {detail}");

        if (enabled)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Disable now##{name}"))
                disableAction();
        }

        ImGui.TextDisabled("MOGTOME tries /xldisableplugin TwistOfFayte when you start it, but it no longer blocks startup.");
    }

    private void ToggleAdsExperimental(bool enable)
    {
        plugin.Configuration.UseAdsExperimental = enable;
        plugin.ConfigManager.SaveCurrentAccount();
        plugin.ConfigManager.NotifyConfigurationChanged(force: true);
        lastDepCheck = DateTime.MinValue;

        if (!enable)
            return;

        _ = Task.Run(async () => await plugin.DutyAutomationService.EnsureAutoDutyDisabledForAdsAsync("ADS config toggle"));
    }

    private bool DrawSetupWizardTab(Configuration config)
    {
        var changed = false;
        var isComplete = config.SetupWizardCompletedVersion >= SetupWizardVersion;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Setup Wizard");
        ImGui.TextWrapped("This guide is advisory. It never installs, enables, disables, or configures another plugin. It only saves MOGTOME settings when you choose them.");
        ImGui.Separator();

        if (isComplete)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Completed for this account (wizard version {config.SetupWizardCompletedVersion}).");
            if (ImGui.Button("Run Setup Wizard Again"))
            {
                config.SetupWizardCompletedVersion = 0;
                setupWizardStep = 0;
                changed = true;
            }

            return changed;
        }

        var steps = new[]
        {
            "Backend",
            "Combat provider",
            "Required plugins",
            "Party setup",
            "Optional settings",
            "Review",
        };
        ImGui.Text($"Step {setupWizardStep + 1} of {steps.Length}: {steps[setupWizardStep]}");
        ImGui.Separator();

        switch (setupWizardStep)
        {
            case 0:
                ImGui.TextWrapped("ADS is the primary MOGTOME backend. AutoDuty remains an alternative. This choice changes only MOGTOME's saved backend selection.");
                var useAds = config.UseAdsExperimental;
                if (ImGui.RadioButton("ADS (primary backend)##Wizard", useAds))
                {
                    config.UseAdsExperimental = true;
                    lastDepCheck = DateTime.MinValue;
                    changed = true;
                }

                if (ImGui.RadioButton("AutoDuty (alternative backend)##Wizard", !useAds))
                {
                    config.UseAdsExperimental = false;
                    lastDepCheck = DateTime.MinValue;
                    changed = true;
                }

                ImGui.TextDisabled(config.UseAdsExperimental
                    ? "ADS must be loaded for this selection."
                    : "AutoDuty and the selected Praetorium path must be available for this selection.");
                break;
            case 1:
                ImGui.TextWrapped("Choose the combat provider MOGTOME should request for the selected backend.");
                DrawCombatRotationSelector("Wizard");

                DrawWizardRequirement("Selected combat provider", IsCombatProviderReady(config), "Load the selected provider; RSR also requires BMR or VBM for passive support.", null);
                break;
            case 2:
                DrawWizardRequiredPluginChecks(config);
                break;
            case 3:
                ImGui.TextWrapped("Mark the client that queues duties as the party leader. Every participating client should independently review its own setup.");
                var isLeader = config.IsPartyLeader;
                if (ImGui.Checkbox("I am the Party Leader##Wizard", ref isLeader))
                {
                    config.IsPartyLeader = isLeader;
                    plugin.State.IsPartyLeader = isLeader;
                    changed = true;
                }

                var crossWorld = config.IsCrossWorldParty;
                if (ImGui.Checkbox("Cross-World Party##Wizard", ref crossWorld))
                {
                    config.IsCrossWorldParty = crossWorld;
                    changed = true;
                }

                ImGui.TextDisabled("Use Party settings for the full queue policy and the one-time outside-duty party-state refresh.");
                break;
            case 4:
                ImGui.TextWrapped("Food, potions, and repair are optional. Configure them in their tabs when wanted; leaving them unset is supported.");
                DrawDepLineOptional("Lifestream", depLifestream, "Optional. Not required by MOGTOME.");
                DrawDepLineOptional("TextAdvance", depTextAdv, "Optional. Not required by MOGTOME.");
                ImGui.TextDisabled("Food & Pots and Repair remain available after this wizard; no outside plugin settings are changed here.");
                break;
            case 5:
                DrawSetupWizardReview(config);
                if (ImGui.Button("Finish Setup"))
                {
                    config.SetupWizardCompletedVersion = SetupWizardVersion;
                    changed = true;
                }

                break;
        }

        ImGui.Spacing();
        if (setupWizardStep > 0 && ImGui.Button("Back##SetupWizard"))
            setupWizardStep--;

        if (setupWizardStep > 0)
            ImGui.SameLine();

        if (setupWizardStep < steps.Length - 1 && ImGui.Button("Next##SetupWizard"))
            setupWizardStep++;

        return changed;
    }

    private void DrawWizardRequiredPluginChecks(Configuration config)
    {
        var backendReady = config.UseAdsExperimental
            ? depAds
            : depAutoDuty && plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);

        DrawWizardRequirement(
            config.UseAdsExperimental ? "ADS (selected backend)" : "AutoDuty plus selected Praetorium path",
            backendReady,
            config.UseAdsExperimental ? "Load ADS." : "Load AutoDuty and install the selected Praetorium path.",
            config.UseAdsExperimental ? "ADS" : "AutoDuty");
        DrawWizardRequirement("Selected combat provider", IsCombatProviderReady(config), "Load the provider selected in step 2; RSR also requires BMR or VBM.", null);
        DrawWizardRequirement("vnavmesh", depVnav, "Load vnavmesh.", "vnavmesh");
        DrawWizardRequirement("XA Slave", depXaSlave, "Load XA Slave for /xa skipcutscenes on.", "XASlave");
        DrawWizardRequirement("YesAlready", depYesAlready, "Load YesAlready for dialogs.", null);

        if (!config.UseAdsExperimental)
        {
            if (ImGui.Button("Install Bundled Praetorium Paths##Wizard"))
                _ = Task.Run(async () => await plugin.AutoDutyPathService.EnsurePathExists());
            ImGui.TextDisabled("Operator-clicked only: copies MOGTOME's bundled files into AutoDuty's paths folder.");
        }

        ImGui.TextDisabled("Lifestream and TextAdvance are optional and do not affect this checklist.");
    }

    private void DrawSetupWizardReview(Configuration config)
    {
        var backendReady = config.UseAdsExperimental
            ? depAds
            : depAutoDuty && plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);

        ImGui.Text($"Backend: {(config.UseAdsExperimental ? "ADS (primary)" : "AutoDuty (alternative)")}");
        ImGui.Text($"Combat provider: {config.CombatProvider}");
        ImGui.Text($"Required checks: {(backendReady && IsCombatProviderReady(config) && depVnav && depXaSlave && depYesAlready ? "ready" : "incomplete")}");
        ImGui.Text($"Party role: {(config.IsPartyLeader ? "leader" : "participant")}");
        ImGui.Text($"Optional food: {(config.FoodItemId > 0 ? config.FoodItemName : "not configured")}");
        ImGui.Text($"Optional repair threshold: {config.RepairThreshold}%");
        ImGui.TextWrapped("Finish records only that this account completed this wizard version. It does not gate Start or alter another plugin.");
    }

    private bool IsCombatProviderReady(Configuration config)
        => config.CombatProvider switch
        {
            CombatProvider.Bmr => depBmr,
            CombatProvider.Vbm => depVbm,
            CombatProvider.Rsr => depRsr && (depBmr || depVbm),
            CombatProvider.Wrath => depWrath,
            _ => false,
        };

    private static void DrawWizardRequirement(string name, bool ready, string missingDetail, string? repoKey)
    {
        var color = ready ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
        ImGui.TextColored(color, $"[{(ready ? "OK" : "!!")}] {name}");
        ImGui.SameLine();
        ImGui.TextDisabled(ready ? "Ready" : missingDetail);

        if (!ready && repoKey != null && PluginRepos.TryGetValue(repoKey, out var repo))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Copy Repo##Wizard{repoKey}"))
                ImGui.SetClipboardText(repo);
        }
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

    private bool DrawPartyTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Party Settings");
        ImGui.Separator();

        var isLeader = config.IsPartyLeader;
        if (ImGui.Checkbox("I am the Party Leader", ref isLeader))
        {
            config.IsPartyLeader = isLeader;
            changed = true;
        }
        ImGui.TextDisabled("Runtime role follows this saved setting. Use Refresh Party State outside duty for a one-time same-world detection.");

        var isCrossWorld = config.IsCrossWorldParty;
        if (ImGui.Checkbox("Cross-World Party", ref isCrossWorld))
        {
            config.IsCrossWorldParty = isCrossWorld;
            changed = true;
        }
        ImGui.TextDisabled("Enable if you're in a cross-world party.");

        var onlyQueueWithFour = config.OnlyQueueWithFourPeople;
        if (ImGui.Checkbox("Only queue with exactly 4 visible people", ref onlyQueueWithFour))
        {
            config.OnlyQueueWithFourPeople = onlyQueueWithFour;
            changed = true;
        }
        ImGui.TextDisabled("Applies only to same-world leaders in synced modes because cross-world roster visibility is unreliable. Unsynced testing mode is exempt.");

        if (changed)
        {
            plugin.State.IsPartyLeader = config.IsPartyLeader;
            plugin.Engine?.ApplyConfiguredPartyLeaderState(reason: "party settings changed");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1.0f, 1.0f), "Runtime Party State:");
        ImGui.Text($"Leader right now: {(plugin.State.IsPartyLeader ? "Yes" : "No")}");
        ImGui.TextDisabled(config.IsCrossWorldParty
            ? "Source: configured cross-world role."
            : "Source: configured role. Refresh Party State only probes the current same-world party list once.");

        if (plugin.Engine != null && ImGui.Button("Refresh Party State", new Vector2(170f, 28f)))
        {
            plugin.Engine.RefreshPartyLeaderState();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("One-time same-world leader detection. Use only outside duty after the full party is visible.");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Party Behaviour:");
        ImGui.TextWrapped("- Leader: Queues duties and controls the MOGTOME run flow.");
        ImGui.TextWrapped("- Non-leader: Waits for party queue pops and repairs independently.");
        ImGui.TextWrapped("- Repair: MOGTOME pauses its own manual queue attempts while repair is active, then resumes after repair.");
        ImGui.TextWrapped("- Detection: Refresh Party State is manual-only and will not auto-promote solo or partial party data to leader.");

        return changed;
    }

    private bool DrawDutyTab(Configuration config)
    {
        var changed = false;
        var dutyCounterChanged = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Duty Settings");
        ImGui.Separator();

        var dutyCounter = config.DutyCounter;
        if (ImGui.InputInt("Duty Counter", ref dutyCounter))
        {
            config.DutyCounter = Math.Clamp(dutyCounter, 0, 666);
            changed = true;
            dutyCounterChanged = true;
        }
        ImGui.TextDisabled("Current Praetorium run count. Set to 0 for first run of the day.");

        if (!config.UseAdsExperimental)
        {
            var selectedPraetoriumPath = plugin.AutoDutyPathService.ResolvePraetoriumPathFileName(config.PraetoriumPathFileName);
            if (!string.Equals(selectedPraetoriumPath, config.PraetoriumPathFileName, StringComparison.OrdinalIgnoreCase))
            {
                config.PraetoriumPathFileName = selectedPraetoriumPath;
                changed = true;
            }

            var selectedPraetoriumPathLabel = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(selectedPraetoriumPath);
            if (ImGui.BeginCombo("Praetorium AutoDuty Path", selectedPraetoriumPathLabel))
            {
                foreach (var option in plugin.AutoDutyPathService.GetPraetoriumPathOptions())
                {
                    var isSelected = string.Equals(option.FileName, selectedPraetoriumPath, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(option.DisplayName, isSelected))
                    {
                        config.PraetoriumPathFileName = option.FileName;
                        selectedPraetoriumPath = option.FileName;
                        selectedPraetoriumPathLabel = option.DisplayName;
                        changed = true;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
            ImGui.TextDisabled("Bundled with MOGTOME and copied into AutoDuty's paths folder on start/install. Default: phecda.");
        }
        else
        {
            ImGui.TextDisabled("ADS mode ignores AutoDuty path selection.");
        }

        var praeThreshold = config.PraetoriumThreshold;
        if (ImGui.InputInt("Praetorium Threshold", ref praeThreshold))
        {
            config.PraetoriumThreshold = Math.Clamp(praeThreshold, 1, 666);
            changed = true;
        }
        ImGui.TextDisabled("Switch to Decumana after this many Praetorium runs.");

        var maxRuns = config.MaxRuns;
        if (ImGui.InputInt("Praetorium Daily Limit", ref maxRuns))
        {
            config.MaxRuns = Math.Clamp(maxRuns, 0, 9999);
            changed = true;
        }
        ImGui.TextDisabled("Stop after this many successful Praetorium clears today. Decumana and aborted runs do not count.");

        var quitCommand = config.QuitCommand;
        if (ImGui.InputText("Quit Command", ref quitCommand, 50))
        {
            config.QuitCommand = quitCommand;
            changed = true;
        }
        ImGui.TextDisabled("Runs once when the Praetorium daily limit is reached, after leaving duty.");

        // Sync counters if duty counter changed
        if (dutyCounterChanged)
        {
            try
            {
                plugin.DutyTrackerService.SyncCounters();
            }
            catch (Exception ex)
            {
                Log.Error($"[ConfigWindow] Failed to sync counters: {ex.Message}");
            }
        }

        // Unsynced testing mode - only visible when debug mode enabled (/mog debug)
        if (config.DebugModeEnabled)
        {
            var testMode = config.TestingModeUnsynced;
            if (ImGui.Checkbox("Testing Mode: Unsynced (uncheck level sync yourself if you really want to do this. Stats won't be recorded)", ref testMode))
            {
                config.TestingModeUnsynced = testMode;
                changed = true;
            }
            if (testMode)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "WARNING: Running Unsynced without Level Sync (Testing mode. No Stats).");
            }
            else
            {
                ImGui.TextDisabled("Default: Unsync+Level Sync (Sync+Level Sync for safety from Queueing with Randoms).");
            }
        }
        else
        {
            if (config.TestingModeUnsynced)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), "[Testing Mode active - enable debug to change]");
            }
        }

        return changed;
    }

    private bool DrawFoodPotTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Food");
        ImGui.Separator();

        // Food dropdown with search
        var foodId = config.FoodItemId;
        var foodName = config.FoodItemName;
        if (DrawItemSearchDropdown("Food", ref foodSearch, foodItems, ref foodId, ref foodName))
        {
            config.FoodItemId = foodId;
            config.FoodItemName = foodName;
            changed = true;
        }

        if (config.FoodItemId > 0)
        {
            ImGui.Text($"  Selected: {config.FoodItemName} {(config.FoodUseHighQuality ? "[HQ]" : "[NQ]")} (ID: {config.FoodItemId})");
            var useFoodHq = config.FoodUseHighQuality;
            if (ImGui.Checkbox("Use HQ food", ref useFoodHq))
            {
                config.FoodUseHighQuality = useFoodHq;
                changed = true;
            }
            ImGui.TextDisabled("Uses the selected meal as HQ when enabled; leave off for normal-quality food.");
            if (ImGui.SmallButton("Clear Food"))
            {
                config.FoodItemId = 0;
                config.FoodItemName = "";
                config.FoodUseHighQuality = false;
                changed = true;
            }
        }
        else
        {
            ImGui.TextDisabled("  No food selected. Food is optional.");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Potions");
        ImGui.Separator();

        // Potion dropdown with search
        var potId = config.PotionItemId;
        var potName = config.PotionItemName;
        if (DrawItemSearchDropdown("Potion", ref potionSearch, potionItems, ref potId, ref potName))
        {
            config.PotionItemId = potId;
            config.PotionItemName = potName;
            changed = true;
        }

        if (config.PotionItemId > 0)
        {
            ImGui.Text($"  Selected: {config.PotionItemName} {(config.PotionUseHighQuality ? "[HQ]" : "[NQ]")} (ID: {config.PotionItemId})");
            var usePotionHq = config.PotionUseHighQuality;
            if (ImGui.Checkbox("Use HQ potion", ref usePotionHq))
            {
                config.PotionUseHighQuality = usePotionHq;
                changed = true;
            }
            ImGui.TextDisabled("Uses the selected medicine as HQ when enabled; leave off for normal-quality tinctures/potions.");
            if (ImGui.SmallButton("Clear Potion"))
            {
                config.PotionItemId = 0;
                config.PotionItemName = "";
                config.PotionUseHighQuality = false;
                changed = true;
            }

            ImGui.Spacing();
            var potTarget = config.PotionTarget;
            if (ImGui.RadioButton("Pot on Gaius", ref potTarget, 0))
            {
                config.PotionTarget = 0;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Pot on Phantom Gaius", ref potTarget, 1))
            {
                config.PotionTarget = 1;
                changed = true;
            }
        }
        else
        {
            ImGui.TextDisabled("  No potion selected. Potions are optional.");
        }

        return changed;
    }

    private static bool DrawItemSearchDropdown(string label, ref string search, List<(uint Id, string Name)> items, ref int selectedId, ref string selectedName)
    {
        var changed = false;
        var displayText = selectedId > 0 ? $"{selectedName} ({selectedId})" : $"Select {label}...";

        ImGui.SetNextItemWidth(400);
        if (ImGui.BeginCombo($"##{label}Select", displayText))
        {
            ImGui.SetNextItemWidth(380);
            ImGui.InputText($"Search##{label}", ref search, 128);

            ImGui.Separator();

            var maxResults = 20;
            var shown = 0;

            if (!string.IsNullOrWhiteSpace(search) && search.Length >= 2)
            {
                var searchLower = search.ToLowerInvariant();
                var isNumeric = uint.TryParse(search, out var searchId);

                for (var i = 0; i < items.Count && shown < maxResults; i++)
                {
                    var item = items[i];
                    bool match;
                    if (isNumeric)
                        match = item.Id.ToString().Contains(search);
                    else
                        match = item.Name.ToLowerInvariant().Contains(searchLower);

                    if (!match) continue;
                    shown++;

                    var isSelected = (int)item.Id == selectedId;
                    if (ImGui.Selectable($"{item.Name} ({item.Id})##{label}{i}", isSelected))
                    {
                        selectedId = (int)item.Id;
                        selectedName = item.Name;
                        changed = true;
                    }
                }

                if (shown == 0)
                {
                    ImGui.TextDisabled("No results. Try a different search term.");
                }
            }
            else
            {
                ImGui.TextDisabled("Type at least 2 characters to search...");
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private bool DrawRepairTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Repair Settings");
        ImGui.Separator();

        // Repair threshold slider
        var repairThreshold = config.RepairThreshold;
        if (ImGui.SliderInt("Repair Threshold (%)", ref repairThreshold, 0, 100))
        {
            config.RepairThreshold = Math.Clamp(repairThreshold, 0, 100);
            changed = true;
        }
        ImGui.TextDisabled("Repair when equipment durability falls below this percentage. Set to 0 to disable auto-repair.");

        ImGui.Spacing();

        if (config.UseAdsExperimental)
        {
            var useAdsSelfRepair = config.UseAdsSelfRepair;
            if (ImGui.Checkbox("Use self repair", ref useAdsSelfRepair))
            {
                config.UseAdsSelfRepair = useAdsSelfRepair;
                changed = true;
            }

            ImGui.TextDisabled("Checked = /ads selfrepair. Unchecked = /ads npcrepair.");
            ImGui.Spacing();
        }

        // Repair method info
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Repair Behavior:");
        ImGui.TextWrapped("- Leader: Repairs automatically between duties when threshold is met");
        ImGui.TextWrapped("- Non-leader: Repairs independently after 1 second outside duty");
        ImGui.TextWrapped("- Solo: Treated as leader automatically");
        ImGui.TextWrapped(config.UseAdsExperimental
            ? $"- ADS mode: currently uses {(config.UseAdsSelfRepair ? "/ads selfrepair" : "/ads npcrepair")} and /ads enterinn"
            : "- AutoDuty mode: repair METHOD (self/NPC) is configured in AutoDuty settings; inn return uses /ads enterinn");
        
        ImGui.Spacing();
        
        // Current status info
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Status:");
        ImGui.Text($"  Current Threshold: {config.RepairThreshold}%");
        ImGui.Text($"  Auto-Repair: {(config.RepairThreshold > 0 ? "Enabled" : "Disabled")}");
        if (config.UseAdsExperimental)
            ImGui.Text($"  ADS Repair Method: {(config.UseAdsSelfRepair ? "Self" : "NPC")}");

        return changed;
    }

    private bool DrawAdvancedTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Experimental ADS");
        ImGui.Separator();
        var firstRoomSkip = config.ExperimentalFirstRoomSkip;
        ImGui.BeginDisabled(!config.UseAdsExperimental);
        if (ImGui.Checkbox("Experimental first room skip (Praetorium)##FirstRoomSkip", ref firstRoomSkip))
        {
            config.ExperimentalFirstRoomSkip = firstRoomSkip;
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled(config.UseAdsExperimental
            ? "ADS/Praetorium only. Every participating client must opt in locally. MOGTOME falls back to ADS after success or any failure."
            : "Available only when ADS is the selected backend; the saved option is inactive in AutoDuty mode.");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Debug");
        ImGui.Separator();

        var debugCounter = config.DebugCounter;
        if (ImGui.InputInt("Debug Counter", ref debugCounter))
        {
            config.DebugCounter = debugCounter;
            changed = true;
        }
        ImGui.TextDisabled("Crash recovery offset. If you restarted mid-session, set this to your last\nknown run count to continue tracking correctly for today.");

        ImGui.Spacing();

        var bailout = config.BailoutTimeout;
        if (ImGui.InputInt("Bailout Timeout (sec)", ref bailout))
        {
            config.BailoutTimeout = Math.Clamp(bailout, 60, 3600);
            changed = true;
        }
        ImGui.TextDisabled("Leave duty if stuck for this many seconds. Default: 1200 (20 min).");

        return changed;
    }
}
