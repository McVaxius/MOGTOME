using MOGTOME.Localization;
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
    private UiLanguage? itemLanguage;

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
        : base(Ui.T("Window_MOGTOMEConfiguration") + "###Window_MOGTOMEConfiguration_#MogtomeConfig", ImGuiWindowFlags.None)
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
        WindowName = Ui.T("Window_MOGTOMEConfiguration") + "###Window_MOGTOMEConfiguration_#MogtomeConfig";
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
        if (itemsLoaded && itemLanguage == Ui.Language) return;
        itemLanguage = Ui.Language;
        foodItems.Clear();
        potionItems.Clear();
        foodSearch = potionSearch = string.Empty;
        itemsLoaded = true;

        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>(Ui.SheetLanguage);
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
                    Ui.L("Config_SetupWizard"),
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
            var depOpen = ImGui.BeginTabItem(Ui.L("Config_DependencyCheck"));
            ImGui.PopStyleColor();
            if (depOpen)
            {
                DrawDependencyCheckTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Config_Party")))
            {
                changed |= DrawPartyTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Config_Duty")))
            {
                changed |= DrawDutyTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Config_FoodPots")))
            {
                changed |= DrawFoodPotTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Config_Repair")))
            {
                changed |= DrawRepairTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(Ui.L("Config_Advanced")))
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
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_BackendMode"));
        var useAdsExperimental = config.UseAdsExperimental;
        if (ImGui.Checkbox(Ui.L("Config_UseADSPrimaryBackend"), ref useAdsExperimental))
        {
            ToggleAdsExperimental(useAdsExperimental);
            config = plugin.Configuration;
        }
        UiLayout.TextDisabled(config.UseAdsExperimental
            ? Ui.T("Config_ADSHandlesDutyAutomationAndInnReturn")
            : Ui.T("Config_AutoDutyIsTheAlternativeDutyBackendADS"));
        ImGui.Spacing();

        DrawCombatRotationSelector("Dependencies");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_RequiredPlugins"));
        if (!allDepsGreen)
        {
            ImGui.TextColored(new Vector4(1, 0, 0, 1), Ui.T("Config_SetupRequirementsAreIncompleteTheWizardIs"));
        }
        ImGui.Separator();

        switch (config.CombatProvider)
        {
            case CombatProvider.Rsr:
                DrawDepLine(Ui.T("Config_RSRRotationSolverReborn"), depRsr, depRsr ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "RSR");
                DrawDepLine(Ui.T("Config_BossModPassiveSupportBMROrVBM"), depBmr || depVbm,
                    depBmr ? Ui.T("Config_BMRLoaded") : depVbm ? Ui.T("Config_VBMLoaded") : Ui.T("Config_NOTFOUND"), "BMR");
                break;
            case CombatProvider.Bmr:
                DrawDepLine(Ui.T("Config_BossModRebornBMR"), depBmr, depBmr ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "BMR");
                break;
            case CombatProvider.Vbm:
                DrawDepLine(Ui.T("Config_BossModVBM"), depVbm, depVbm ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "VBM");
                break;
            case CombatProvider.Wrath:
                DrawDepLine("Wrath Combo", depWrath, depWrath ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), null);
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
        if (ImGui.BeginCombo(Ui.L("Config_CombatRotation"), Ui.EnumLabel(config.CombatProvider)))
        {
            foreach (var provider in Enum.GetValues<CombatProvider>())
            {
                var selected = config.CombatProvider == provider;
                if (ImGui.Selectable(Ui.EnumLabel(provider) + "###Provider" + (int)provider, selected))
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
            CombatProvider.Rsr => Ui.T("Config_RSRHandlesAttacksTheLoadedBossModVariant"),
            CombatProvider.Bmr => Ui.T("Config_BMRHandlesAttacksAndMovementWithFRENRIDER"),
            CombatProvider.Vbm => Ui.T("Config_VBMHandlesAttacksAndMovementWithFRENRIDER"),
            _ => Ui.T("Config_WrathHandlesAttacksUsingItsCurrentSettings"),
        });

        if (config.CombatProvider is CombatProvider.Bmr or CombatProvider.Vbm)
        {
            var manualPreset = config.UseManualBossModPreset;
            if (ImGui.Checkbox(Ui.L("Config_UseManualBossModPreset"), ref manualPreset))
            {
                config.UseManualBossModPreset = manualPreset;
                plugin.ConfigManager.SaveCurrentAccount();
            }

            if (manualPreset)
            {
                var presetName = config.ManualBossModPresetName;
                if (ImGui.InputText(Ui.L("Config_PresetName"), ref presetName, 128))
                {
                    config.ManualBossModPresetName = presetName;
                    plugin.ConfigManager.SaveCurrentAccount();
                }
                if (string.IsNullOrWhiteSpace(config.ManualBossModPresetName))
                    ImGui.TextWrapped(Ui.T("Config_EnterAnExistingPresetNameBeforeStarting"));
            }
            else
            {
                ImGui.TextWrapped(Ui.T("Config_MOGTOMESelectsItsPackagedActivePresetBy"));
            }
        }
        ImGui.EndDisabled();
        if (depBmr && depVbm)
            ImGui.TextWrapped(Ui.T("Config_BothBossModVariantsAreLoadedStartDisables"));
        ImGui.PopID();
    }

    private void DrawRemainingDependencies(Configuration config)
    {
        // VNAV
        DrawDepLine("vnavmesh", depVnav, depVnav ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "vnavmesh");

        // XA Slave
        DrawDepLine("XA Slave", depXaSlave, depXaSlave ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "XASlave");
        UiLayout.TextDisabled(Ui.T("Config_MOGTOMERunsXaSkipcutscenesOnBeforeEvery"));

        DrawDepLine("YesAlready", depYesAlready, depYesAlready ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), null);

        if (config.UseAdsExperimental)
        {
            DrawDepLine("ADS", depAds, depAds ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "ADS");
        }
        else
        {
            DrawDepLine("AutoDuty", depAutoDuty, depAutoDuty ? Ui.T("Config_Installed") : Ui.T("Config_NOTFOUND"), "AutoDuty");
            DrawDepLineOptional("ADS", depAds, Ui.T("Config_OptionalEnablesMogInnDelegation"));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_OptionalPlugins"));
        ImGui.Separator();

        DrawDepLineOptional("Lifestream", depLifestream, Ui.T("Config_OptionalNotRequiredByMOGTOME"));
        DrawDepLineOptional("TextAdvance", depTextAdv, Ui.T("Config_OptionalNotRequiredByMOGTOME"));
        DrawDepLineOptional("Krangler", depKrangler, Ui.T("Config_RecommendedAppearanceNameplateRandomizer"));
        DrawDepLineOptional(Ui.T("Config_CustomResolutionPpByX0ade"), depCustomRes, Ui.T("Config_ExperimentalAndNotRecommendedLowSpecHelper"),
		Ui.T("Config_MaybeCrashyWithClients"));
        DrawDepLineOptional("ChillFrames", depChillframes, Ui.T("Config_WillCauseSomeIssuesSometimes"));
        DrawDepLineOptional(
            Ui.T("Config_DPSDhogPotatoSystem"),
            depDps,
            Ui.T("Config_ExperimentalAndNotRecommendedLowSpecHelper"),
            Ui.T("Config_MayHavePathingIssues"));
        DrawDepLineOptional("Thick Thighs Save Lives", depTtsl, Ui.T("Config_ExperimentalAndNotRecommendedRemoteHUDControl"),
            Ui.T("Config_VeryNewPluginUnknownIssues"));

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_ConflictingPlugins"));
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
            ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_AutoDutyPath"));
            ImGui.Separator();

            var pathDisplayName = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName);
            var pathExists = plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);
            ImGui.TextColored(
                pathExists ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1),
                pathExists ? Ui.T("Config_PraetoriumPathINSTALLED", pathDisplayName) : Ui.T("Config_PraetoriumPathNOTFOUND", pathDisplayName));

            if (UiLayout.Button(Ui.L("Config_InstallBundledPraetoriumPaths")))
            {
                _ = Task.Run(async () => await plugin.AutoDutyPathService.EnsurePathExists());
            }
            UiLayout.TextDisabled(Ui.T("Config_ThisCopiesTheBundledW2WPraetoriumPath"));
            UiLayout.TextDisabled(Ui.T("Config_ADSIsOptionalInAutoDutyModeMog"));
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_ADSModeNotes"));
            ImGui.Separator();
            ImGui.TextWrapped(Ui.T("Config_ADSModeDisablesAutoDutyImmediatelyAndAgain"));
            ImGui.TextWrapped(Ui.T("Config_RepairInnLeaveSwitchToAdsNpcrepair"));
            ImGui.TextWrapped(Ui.T("Config_TheCheckboxChangesTheDutyBackendADS"));
            ImGui.Spacing();
        }
    }

    private static void DrawDepLine(string name, bool ok, string detail, string? repoKey)
    {
        var color = ok ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
        var icon = ok ? Ui.T("Config_OK") : "[!!]";
        ImGui.TextColored(color, string.Create(Ui.Culture, $"{icon} {name}"));
        ImGui.SameLine();
        UiLayout.TextDisabled(string.Create(Ui.Culture, $"- {detail}"));

        if (!ok && repoKey != null && PluginRepos.TryGetValue(repoKey, out var repo) && !string.IsNullOrEmpty(repo))
        {
            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.T("Config_CopyRepo") + string.Format(System.Globalization.CultureInfo.InvariantCulture, "###Config_CopyRepo_{0}", name)))
            {
                ImGui.SetClipboardText(repo);
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Config_CopyRepoURLToClipboardThenAdd"));
            }
        }
    }

    private static void DrawDepLineColor(string name, Vector4 color, string detail)
    {
        ImGui.TextColored(color, string.Create(Ui.Culture, $"[!!] {name}"));
        ImGui.SameLine();
        ImGui.TextColored(color, string.Create(Ui.Culture, $"- {detail}"));
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
        var icon = exists ? Ui.T("Config_OK") : "[--]";
        ImGui.TextColored(color, string.Create(Ui.Culture, $"{icon} {name}"));
        ImGui.SameLine();
        UiLayout.TextDisabled(exists ? Ui.T("Config_Installed") : (string.IsNullOrWhiteSpace(missingDetail) ? Ui.T("Config_NotInstalledOptional") : missingDetail));

        if (exists && !string.IsNullOrWhiteSpace(installedWarning))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1, 0, 0, 1), installedWarning);
        }
    }

    private static void DrawConflictPluginLine(string name, bool installed, bool enabled, System.Action disableAction)
    {
        var color = enabled ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1);
        var icon = enabled ? "[!!]" : Ui.T("Config_OK");
        var detail = enabled
            ? Ui.T("Config_EnabledKnownMOGTOMEConflict")
            : installed
                ? Ui.T("Config_InstalledButDisabled")
                : Ui.T("Config_NotInstalled");

        ImGui.TextColored(color, string.Create(Ui.Culture, $"{icon} {name}"));
        ImGui.SameLine();
        ImGui.TextColored(color, string.Create(Ui.Culture, $"- {detail}"));

        if (enabled)
        {
            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.T("Config_DisableNow") + string.Format(System.Globalization.CultureInfo.InvariantCulture, "###Config_DisableNow_{0}", name)))
                disableAction();
        }

        UiLayout.TextDisabled(Ui.T("Config_MOGTOMETriesXldisablepluginTwistOfFayteWhenYouStart"));
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

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_SetupWizard"));
        ImGui.TextWrapped(Ui.T("Config_ThisGuideIsAdvisoryItNeverInstalls"));
        ImGui.Separator();

        if (isComplete)
        {
            ImGui.TextColored(new Vector4(0, 1, 0, 1), Ui.T("Config_CompletedForThisAccountWizardVersion", config.SetupWizardCompletedVersion));
            if (UiLayout.Button(Ui.L("Config_RunSetupWizardAgain")))
            {
                config.SetupWizardCompletedVersion = 0;
                setupWizardStep = 0;
                changed = true;
            }

            return changed;
        }

        var steps = new[]
        {
            Ui.T("Config_Backend"),
            Ui.T("Config_CombatProvider"),
            Ui.T("Config_RequiredPlugins2"),
            Ui.T("Config_PartySetup"),
            Ui.T("Config_OptionalSettings"),
            Ui.T("Config_Review"),
        };
        ImGui.Text(Ui.T("Config_StepOf", setupWizardStep + 1, steps.Length, steps[setupWizardStep]));
        ImGui.Separator();

        switch (setupWizardStep)
        {
            case 0:
                ImGui.TextWrapped(Ui.T("Config_ADSIsThePrimaryMOGTOMEBackendAutoDuty"));
                var useAds = config.UseAdsExperimental;
                if (ImGui.RadioButton(Ui.T("Config_ADSPrimaryBackend") + "###Config_ADSPrimaryBackend_Wizard", useAds))
                {
                    config.UseAdsExperimental = true;
                    lastDepCheck = DateTime.MinValue;
                    changed = true;
                }

                if (ImGui.RadioButton(Ui.T("Config_AutoDutyAlternativeBackend") + "###Config_AutoDutyAlternativeBackend_Wizard", !useAds))
                {
                    config.UseAdsExperimental = false;
                    lastDepCheck = DateTime.MinValue;
                    changed = true;
                }

                UiLayout.TextDisabled(config.UseAdsExperimental
                    ? Ui.T("Config_ADSMustBeLoadedForThisSelection")
                    : Ui.T("Config_AutoDutyAndTheSelectedPraetoriumPathMust"));
                break;
            case 1:
                ImGui.TextWrapped(Ui.T("Config_ChooseTheCombatProviderMOGTOMEShouldRequest"));
                DrawCombatRotationSelector("Wizard");

                DrawWizardRequirement(Ui.T("Config_SelectedCombatProvider"), IsCombatProviderReady(config), Ui.T("Config_LoadTheSelectedProviderRSRAlsoRequires"), null);
                break;
            case 2:
                DrawWizardRequiredPluginChecks(config);
                break;
            case 3:
                ImGui.TextWrapped(Ui.T("Config_MarkTheClientThatQueuesDutiesAs"));
                var isLeader = config.IsPartyLeader;
                if (ImGui.Checkbox(Ui.T("Config_IAmThePartyLeader") + "###Config_IAmThePartyLeader_Wizard", ref isLeader))
                {
                    config.IsPartyLeader = isLeader;
                    plugin.State.IsPartyLeader = isLeader;
                    changed = true;
                }

                var crossWorld = config.IsCrossWorldParty;
                if (ImGui.Checkbox(Ui.T("Config_CrossWorldParty") + "###Config_CrossWorldParty_Wizard", ref crossWorld))
                {
                    config.IsCrossWorldParty = crossWorld;
                    changed = true;
                }

                UiLayout.TextDisabled(Ui.T("Config_UsePartySettingsForTheFullQueue"));
                break;
            case 4:
                ImGui.TextWrapped(Ui.T("Config_FoodPotionsAndRepairAreOptionalConfigure"));
                DrawDepLineOptional("Lifestream", depLifestream, Ui.T("Config_OptionalNotRequiredByMOGTOME"));
                DrawDepLineOptional("TextAdvance", depTextAdv, Ui.T("Config_OptionalNotRequiredByMOGTOME"));
                UiLayout.TextDisabled(Ui.T("Config_FoodPotsAndRepairRemainAvailableAfter"));
                break;
            case 5:
                DrawSetupWizardReview(config);
                if (UiLayout.Button(Ui.L("Config_FinishSetup")))
                {
                    config.SetupWizardCompletedVersion = SetupWizardVersion;
                    changed = true;
                }

                break;
        }

        ImGui.Spacing();
        if (setupWizardStep > 0 && UiLayout.Button(Ui.T("Config_Back") + "###Config_Back_SetupWizard"))
            setupWizardStep--;

        if (setupWizardStep > 0)
            ImGui.SameLine();

        if (setupWizardStep < steps.Length - 1 && UiLayout.Button(Ui.T("Config_Next") + "###Config_Next_SetupWizard"))
            setupWizardStep++;

        return changed;
    }

    private void DrawWizardRequiredPluginChecks(Configuration config)
    {
        var backendReady = config.UseAdsExperimental
            ? depAds
            : depAutoDuty && plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);

        DrawWizardRequirement(
            config.UseAdsExperimental ? Ui.T("Config_ADSSelectedBackend") : Ui.T("Config_AutoDutyPlusSelectedPraetoriumPath"),
            backendReady,
            config.UseAdsExperimental ? Ui.T("Config_LoadADS") : Ui.T("Config_LoadAutoDutyAndInstallTheSelectedPraetorium"),
            config.UseAdsExperimental ? "ADS" : "AutoDuty");
        DrawWizardRequirement(Ui.T("Config_SelectedCombatProvider"), IsCombatProviderReady(config), Ui.T("Config_LoadTheProviderSelectedInStepRSR"), null);
        DrawWizardRequirement("vnavmesh", depVnav, Ui.T("Config_LoadVnavmesh"), "vnavmesh");
        DrawWizardRequirement("XA Slave", depXaSlave, Ui.T("Config_LoadXASlaveForXaSkipcutscenesOn"), "XASlave");
        DrawWizardRequirement("YesAlready", depYesAlready, Ui.T("Config_LoadYesAlreadyForDialogs"), null);

        if (!config.UseAdsExperimental)
        {
            if (UiLayout.Button(Ui.T("Config_InstallBundledPraetoriumPaths") + "###Config_InstallBundledPraetoriumPaths_Wizard"))
                _ = Task.Run(async () => await plugin.AutoDutyPathService.EnsurePathExists());
            UiLayout.TextDisabled(Ui.T("Config_OperatorClickedOnlyCopiesMOGTOMESBundled"));
        }

        UiLayout.TextDisabled(Ui.T("Config_LifestreamAndTextAdvanceAreOptionalAndDo"));
    }

    private void DrawSetupWizardReview(Configuration config)
    {
        var backendReady = config.UseAdsExperimental
            ? depAds
            : depAutoDuty && plugin.AutoDutyPathService.PathExists(config.PraetoriumPathFileName);

        ImGui.Text(Ui.T("Config_Backend2", (config.UseAdsExperimental ? Ui.T("Config_ADSPrimary") : Ui.T("Config_AutoDutyAlternative"))));
        ImGui.Text(Ui.T("Config_CombatProvider2", Ui.EnumLabel(config.CombatProvider)));
        ImGui.Text(Ui.T("Config_RequiredChecks", (backendReady && IsCombatProviderReady(config) && depVnav && depXaSlave && depYesAlready ? Ui.T("Config_Ready") : Ui.T("Config_Incomplete"))));
        ImGui.Text(Ui.T("Config_PartyRole", (config.IsPartyLeader ? Ui.T("Config_Leader") : Ui.T("Config_Participant"))));
        ImGui.Text(Ui.T("Config_OptionalFood", (config.FoodItemId > 0 ? Ui.Item((uint)config.FoodItemId).Render() : Ui.T("Config_NotConfigured"))));
        ImGui.Text(Ui.T("Config_OptionalRepairThreshold", config.RepairThreshold));
        ImGui.TextWrapped(Ui.T("Config_FinishRecordsOnlyThatThisAccountCompleted"));
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
        ImGui.TextColored(color, string.Create(Ui.Culture, $"[{(ready ? "OK" : "!!")}] {name}"));
        ImGui.SameLine();
        UiLayout.TextDisabled(ready ? Ui.T("Config_Ready2") : missingDetail);

        if (!ready && repoKey != null && PluginRepos.TryGetValue(repoKey, out var repo))
        {
            ImGui.SameLine();
            if (UiLayout.SmallButton(Ui.T("Config_CopyRepo") + string.Format(System.Globalization.CultureInfo.InvariantCulture, "###Config_CopyRepo_Wizard{0}", repoKey)))
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

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_PartySettings"));
        ImGui.Separator();

        var isLeader = config.IsPartyLeader;
        if (ImGui.Checkbox(Ui.L("Config_IAmThePartyLeader"), ref isLeader))
        {
            config.IsPartyLeader = isLeader;
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_RuntimeRoleFollowsThisSavedSettingUse"));

        var isCrossWorld = config.IsCrossWorldParty;
        if (ImGui.Checkbox(Ui.L("Config_CrossWorldParty"), ref isCrossWorld))
        {
            config.IsCrossWorldParty = isCrossWorld;
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_EnableIfYouReInACross"));

        var onlyQueueWithFour = config.OnlyQueueWithFourPeople;
        if (ImGui.Checkbox(Ui.L("Config_OnlyQueueWithExactlyVisiblePeople"), ref onlyQueueWithFour))
        {
            config.OnlyQueueWithFourPeople = onlyQueueWithFour;
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_AppliesOnlyToSameWorldLeadersIn"));

        if (changed)
        {
            plugin.State.IsPartyLeader = config.IsPartyLeader;
            plugin.Engine?.ApplyConfiguredPartyLeaderState(reason: "party settings changed");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1.0f, 1.0f), Ui.T("Config_RuntimePartyState"));
        ImGui.Text(Ui.T("Config_LeaderRightNow", (plugin.State.IsPartyLeader ? Ui.T("Config_Yes") : Ui.T("Config_No"))));
        UiLayout.TextDisabled(config.IsCrossWorldParty
            ? Ui.T("Config_SourceConfiguredCrossWorldRole")
            : Ui.T("Config_SourceConfiguredRoleRefreshPartyStateOnly"));

        if (plugin.Engine != null && UiLayout.Button(Ui.L("Config_RefreshPartyState"), new Vector2(170f, 28f)))
        {
            plugin.Engine.RefreshPartyLeaderState();
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Config_OneTimeSameWorldLeaderDetectionUse"));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), Ui.T("Config_PartyBehaviour"));
        ImGui.TextWrapped(Ui.T("Config_LeaderQueuesDutiesAndControlsTheMOGTOME"));
        ImGui.TextWrapped(Ui.T("Config_NonLeaderWaitsForPartyQueuePops"));
        ImGui.TextWrapped(Ui.T("Config_RepairMOGTOMEPausesItsOwnManualQueue"));
        ImGui.TextWrapped(Ui.T("Config_DetectionRefreshPartyStateIsManualOnly"));

        return changed;
    }

    private bool DrawDutyTab(Configuration config)
    {
        var changed = false;
        var dutyCounterChanged = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_DutySettings"));
        ImGui.Separator();

        var dutyCounter = config.DutyCounter;
        if (ImGui.InputInt(Ui.L("Config_DutyCounter"), ref dutyCounter))
        {
            config.DutyCounter = Math.Clamp(dutyCounter, 0, 666);
            changed = true;
            dutyCounterChanged = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_CurrentPraetoriumRunCountSetToFor"));

        var returnDelay = config.ReturnToEntranceDelaySeconds;
        if (ImGui.InputInt(Ui.L("Config_ReturnToEntranceDelaySeconds"), ref returnDelay))
        {
            config.ReturnToEntranceDelaySeconds = Math.Max(1, returnDelay);
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_ReturnToEntranceDelayHelp"));

        if (!config.UseAdsExperimental)
        {
            var selectedPraetoriumPath = plugin.AutoDutyPathService.ResolvePraetoriumPathFileName(config.PraetoriumPathFileName);
            if (!string.Equals(selectedPraetoriumPath, config.PraetoriumPathFileName, StringComparison.OrdinalIgnoreCase))
            {
                config.PraetoriumPathFileName = selectedPraetoriumPath;
                changed = true;
            }

            var selectedPraetoriumPathLabel = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(selectedPraetoriumPath);
            if (ImGui.BeginCombo(Ui.L("Config_PraetoriumAutoDutyPath"), selectedPraetoriumPathLabel))
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
            UiLayout.TextDisabled(Ui.T("Config_BundledWithMOGTOMEAndCopiedIntoAutoDuty"));
        }
        else
        {
            UiLayout.TextDisabled(Ui.T("Config_ADSModeIgnoresAutoDutyPathSelection"));
        }

        var praeThreshold = config.PraetoriumThreshold;
        if (ImGui.InputInt(Ui.L("Config_PraetoriumThreshold"), ref praeThreshold))
        {
            config.PraetoriumThreshold = Math.Clamp(praeThreshold, 1, 666);
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_SwitchToDecumanaAfterThisManyPraetorium"));

        var maxRuns = config.MaxRuns;
        if (ImGui.InputInt(Ui.L("Config_PraetoriumDailyLimit"), ref maxRuns))
        {
            config.MaxRuns = Math.Clamp(maxRuns, 0, 9999);
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_StopAfterThisManySuccessfulPraetoriumClears"));

        var quitCommand = config.QuitCommand;
        if (ImGui.InputText(Ui.L("Config_QuitCommand"), ref quitCommand, 50))
        {
            config.QuitCommand = quitCommand;
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_RunsOnceWhenThePraetoriumDailyLimit"));

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
            if (ImGui.Checkbox(Ui.L("Config_TestingModeUnsyncedUncheckLevelSyncYourself"), ref testMode))
            {
                config.TestingModeUnsynced = testMode;
                changed = true;
            }
            if (testMode)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), Ui.T("Config_WARNINGRunningUnsyncedWithoutLevelSyncTesting"));
            }
            else
            {
                UiLayout.TextDisabled(Ui.T("Config_DefaultUnsyncLevelSyncSyncLevelSync"));
            }
        }
        else
        {
            if (config.TestingModeUnsynced)
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), Ui.T("Config_TestingModeActiveEnableDebugToChange"));
            }
        }

        return changed;
    }

    private bool DrawFoodPotTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_Food"));
        ImGui.Separator();

        // Food dropdown with search
        var foodId = config.FoodItemId;
        var foodName = Ui.Item((uint)config.FoodItemId).Render();
        if (DrawItemSearchDropdown("Config_Food", ref foodSearch, foodItems, ref foodId, ref foodName))
        {
            config.FoodItemId = foodId;
            config.FoodItemName = foodName;
            changed = true;
        }

        if (config.FoodItemId > 0)
        {
            ImGui.Text(Ui.T("Config_SelectedID", Ui.Item((uint)config.FoodItemId), (config.FoodUseHighQuality ? Ui.T("Config_HQ") : Ui.T("Config_NQ")), config.FoodItemId));
            var useFoodHq = config.FoodUseHighQuality;
            if (ImGui.Checkbox(Ui.L("Config_UseHQFood"), ref useFoodHq))
            {
                config.FoodUseHighQuality = useFoodHq;
                changed = true;
            }
            UiLayout.TextDisabled(Ui.T("Config_UsesTheSelectedMealAsHQWhen"));
            if (UiLayout.SmallButton(Ui.L("Config_ClearFood")))
            {
                config.FoodItemId = 0;
                config.FoodItemName = "";
                config.FoodUseHighQuality = false;
                changed = true;
            }
        }
        else
        {
            UiLayout.TextDisabled(Ui.T("Config_NoFoodSelectedFoodIsOptional"));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_Potions"));
        ImGui.Separator();

        // Potion dropdown with search
        var potId = config.PotionItemId;
        var potName = Ui.Item((uint)config.PotionItemId).Render();
        if (DrawItemSearchDropdown("Config_Potion", ref potionSearch, potionItems, ref potId, ref potName))
        {
            config.PotionItemId = potId;
            config.PotionItemName = potName;
            changed = true;
        }

        if (config.PotionItemId > 0)
        {
            ImGui.Text(Ui.T("Config_SelectedID", Ui.Item((uint)config.PotionItemId), (config.PotionUseHighQuality ? Ui.T("Config_HQ") : Ui.T("Config_NQ")), config.PotionItemId));
            var usePotionHq = config.PotionUseHighQuality;
            if (ImGui.Checkbox(Ui.L("Config_UseHQPotion"), ref usePotionHq))
            {
                config.PotionUseHighQuality = usePotionHq;
                changed = true;
            }
            UiLayout.TextDisabled(Ui.T("Config_UsesTheSelectedMedicineAsHQWhen"));
            if (UiLayout.SmallButton(Ui.L("Config_ClearPotion")))
            {
                config.PotionItemId = 0;
                config.PotionItemName = "";
                config.PotionUseHighQuality = false;
                changed = true;
            }

            ImGui.Spacing();
            var potTarget = config.PotionTarget;
            if (ImGui.RadioButton(Ui.L("Config_PotOnBoss", Ui.Boss(2136)) + "_Gaius", ref potTarget, 0))
            {
                config.PotionTarget = 0;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Ui.L("Config_PotOnBoss", Ui.Boss(11285)) + "_Phantom", ref potTarget, 1))
            {
                config.PotionTarget = 1;
                changed = true;
            }
        }
        else
        {
            UiLayout.TextDisabled(Ui.T("Config_NoPotionSelectedPotionsAreOptional"));
        }

        return changed;
    }

    private static bool DrawItemSearchDropdown(string label, ref string search, List<(uint Id, string Name)> items, ref int selectedId, ref string selectedName)
    {
        var changed = false;
        var displayText = selectedId > 0 ? string.Create(Ui.Culture, $"{selectedName} ({selectedId})") : Ui.T("Config_Select", Ui.T(label));

        ImGui.SetNextItemWidth(400);
        if (ImGui.BeginCombo($"##{label}Select", displayText))
        {
            ImGui.SetNextItemWidth(380);
            ImGui.InputText(Ui.T("Config_Search") + string.Format(System.Globalization.CultureInfo.InvariantCulture, "###Config_Search_{0}", label), ref search, 128);

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
                    if (ImGui.Selectable(string.Create(Ui.Culture, $"{item.Name} ({item.Id})###{label}{item.Id}"), isSelected))
                    {
                        selectedId = (int)item.Id;
                        selectedName = item.Name;
                        changed = true;
                    }
                }

                if (shown == 0)
                {
                    UiLayout.TextDisabled(Ui.T("Config_NoResultsTryADifferentSearchTerm"));
                }
            }
            else
            {
                UiLayout.TextDisabled(Ui.T("Config_TypeAtLeastCharactersToSearch"));
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private bool DrawRepairTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_RepairSettings"));
        ImGui.Separator();

        // Repair threshold slider
        var repairThreshold = config.RepairThreshold;
        if (ImGui.SliderInt(Ui.L("Config_RepairThreshold"), ref repairThreshold, 0, 100))
        {
            config.RepairThreshold = Math.Clamp(repairThreshold, 0, 100);
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_RepairWhenEquipmentDurabilityFallsBelowThis"));

        ImGui.Spacing();

        if (config.UseAdsExperimental)
        {
            var repairMode = (int)config.AdsRepairMode;
            var repairModes = Enum.GetValues<AdsRepairMode>().Select(mode => Ui.T(Services.RepairService.GetAdsRepairLabelKey(mode))).ToArray();
            if (ImGui.Combo(Ui.L("Config_RepairMode"), ref repairMode, repairModes, repairModes.Length))
            {
                config.AdsRepairMode = (AdsRepairMode)repairMode;
                changed = true;
            }

            UiLayout.TextDisabled(Ui.T("Config_RepairModeHelp"));
            ImGui.Spacing();
        }

        // Repair method info
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), Ui.T("Config_RepairBehavior"));
        ImGui.TextWrapped(Ui.T("Config_LeaderRepairsAutomaticallyBetweenDutiesWhenThreshold"));
        ImGui.TextWrapped(Ui.T("Config_NonLeaderRepairsIndependentlyAfterSecondOutside"));
        ImGui.TextWrapped(Ui.T("Config_SoloTreatedAsLeaderAutomatically"));
        ImGui.TextWrapped(config.UseAdsExperimental
            ? Ui.T("Config_ADSRepairCommand", Services.RepairService.GetAdsRepairCommand(config.AdsRepairMode))
            : Ui.T("Config_AutoDutyModeRepairMETHODSelfNPCIs"));
        
        ImGui.Spacing();
        
        // Current status info
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), Ui.T("Config_Status"));
        ImGui.Text(Ui.T("Config_CurrentThreshold", config.RepairThreshold));
        ImGui.Text(Ui.T("Config_AutoRepair", (config.RepairThreshold > 0 ? Ui.T("Config_Enabled") : Ui.T("Config_Disabled"))));
        if (config.UseAdsExperimental)
            ImGui.Text(Ui.T("Config_ADSRepairMethod", Ui.T(Services.RepairService.GetAdsRepairLabelKey(config.AdsRepairMode))));

        return changed;
    }

    private bool DrawAdvancedTab(Configuration config)
    {
        var changed = false;

        var obstacleMapsOn = config.ObstacleMapsOn;
        if (ImGui.Checkbox(Ui.L("Config_ObstacleMapsOn"), ref obstacleMapsOn))
        {
            config.ObstacleMapsOn = obstacleMapsOn;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            UiLayout.SetTooltip(Ui.T("Config_ObstacleMapsOnTooltip"));
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_ExperimentalADS"));
        ImGui.Separator();
        var firstRoomSkip = config.ExperimentalFirstRoomSkip;
        ImGui.BeginDisabled(!config.UseAdsExperimental);
        if (ImGui.Checkbox(Ui.T("Config_ExperimentalFirstRoomSkipPraetorium") + "###Config_ExperimentalFirstRoomSkipPraetorium_FirstRoomSkip", ref firstRoomSkip))
        {
            config.ExperimentalFirstRoomSkip = firstRoomSkip;
            changed = true;
        }
        ImGui.EndDisabled();
        UiLayout.TextDisabled(config.UseAdsExperimental
            ? Ui.T("Config_ADSPraetoriumOnlyEveryParticipatingClientMust")
            : Ui.T("Config_AvailableOnlyWhenADSIsTheSelected"));
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Config_Debug"));
        ImGui.Separator();

        var debugCounter = config.DebugCounter;
        if (ImGui.InputInt(Ui.L("Config_DebugCounter"), ref debugCounter))
        {
            config.DebugCounter = debugCounter;
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_CrashRecoveryOffsetIfYouRestartedMid"));

        ImGui.Spacing();

        var bailout = config.BailoutTimeout;
        if (ImGui.InputInt(Ui.L("Config_BailoutTimeoutSec"), ref bailout))
        {
            config.BailoutTimeout = Math.Clamp(bailout, 60, 3600);
            changed = true;
        }
        UiLayout.TextDisabled(Ui.T("Config_LeaveDutyIfStuckForThisMany"));

        return changed;
    }
}
