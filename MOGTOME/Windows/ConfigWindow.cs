using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace MOGTOME.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Food/Pot search state
    private string foodSearch = "";
    private string potionSearch = "";
    private List<(uint Id, string Name)> foodItems = new();
    private List<(uint Id, string Name)> potionItems = new();
    private bool itemsLoaded = false;

    // Dependency check cache
    private DateTime lastDepCheck = DateTime.MinValue;
    private bool depRsr, depBmr, depVbm, depVnav, depTextAdv, depCutsceneSkip, depAutoDuty, depSimpleTweaks;
    private bool depCustomRes, depChillframes;
    private bool allDepsGreen = false;

    // Plugin repo URLs for clipboard
    private static readonly Dictionary<string, string> PluginRepos = new()
    {
        { "RSR", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json" },
        { "BMR", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json" },
        { "VBM", "https://puni.sh/api/repository/veyn" },
        { "vnavmesh", "https://raw.githubusercontent.com/awgil/ffxiv_plugin_distribution/master/pluginmaster.json" },
        { "TextAdvance", "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json" },
        { "SkipCutscene", "https://raw.githubusercontent.com/a08381/Dalamud.SkipCutscene/dist/repo.json" },
        { "AutoDuty", "https://raw.githubusercontent.com/ffxivcode/DalamudPlugins/main/pluginmaster.json" },
        { "SimpleTweaks", "" },
    };

    public ConfigWindow(Plugin plugin)
        : base("MOGTOME - Configuration##MogtomeConfig", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 600),
            MaximumSize = new Vector2(750, 1000),
        };
    }

    public void Dispose() { }

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

                // Category 46 = Medicine (food/consumables that give Well Fed)
                // Category 44 = Meal
                if (catId == 44 || catId == 46)
                {
                    // Meals (food) = category 44
                    if (catId == 44)
                        foodItems.Add((item.RowId, name));
                    // Medicine = category 46 (potions like Gemdraught)
                    if (catId == 46)
                        potionItems.Add((item.RowId, name));
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

        if (ImGui.BeginTabBar("ConfigTabs"))
        {
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

            // Only show other tabs if deps are green
            if (!allDepsGreen)
            {
                ImGui.BeginDisabled();
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

            if (ImGui.BeginTabItem("Advanced"))
            {
                changed |= DrawAdvancedTab(config);
                ImGui.EndTabItem();
            }

            if (!allDepsGreen)
            {
                ImGui.EndDisabled();
            }

            ImGui.EndTabBar();
        }

        if (changed)
        {
            config.Save();
        }
    }

    private void CheckDependencies()
    {
        try
        {
            var installed = Plugin.PluginInterface.InstalledPlugins;
            depRsr = false;
            depBmr = false;
            depVbm = false;
            depVnav = false;
            depTextAdv = false;
            depCutsceneSkip = false;
            depAutoDuty = false;
            depSimpleTweaks = false;
            depCustomRes = false;
            depChillframes = false;

            foreach (var p in installed)
            {
                if (!p.IsLoaded) continue;
                switch (p.InternalName)
                {
                    case "RotationSolver": depRsr = true; break;
                    case "BossModReborn": depBmr = true; break;
                    case "veyn.BossMod": depVbm = true; break;
                    case "vnavmesh": depVnav = true; break;
                    case "TextAdvance": depTextAdv = true; break;
                    case "SkipCutscene": depCutsceneSkip = true; break;
                    case "AutoDuty": depAutoDuty = true; break;
                    case "SimpleTweaksPlugin": depSimpleTweaks = true; break;
                    case "CustomResolution": depCustomRes = true; break;
                    case "ChillFrames": depChillframes = true; break;
                }
            }

            var pathOk = plugin.AutoDutyPathService.PathExists();
            allDepsGreen = depRsr && (depBmr || depVbm) && !(depBmr && depVbm) && depVnav && depTextAdv && depCutsceneSkip && depAutoDuty && depSimpleTweaks && pathOk;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[ConfigWindow] Dependency check failed: {ex.Message}");
        }
    }

    private void DrawDependencyCheckTab(Configuration config)
    {
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Required Plugins");
        if (!allDepsGreen)
        {
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "All required dependencies must be green before other tabs are accessible.");
        }
        ImGui.Separator();

        // RSR
        DrawDepLine("RSR (RotationSolver)", depRsr, depRsr ? "Installed" : "NOT FOUND", "RSR");

        // BMR / VBM
        if (depBmr && depVbm)
        {
            DrawDepLineColor("BMR + VBM", new Vector4(1, 1, 0, 1), "WARNING: Both enabled! Disable one.");
        }
        else if (depBmr || depVbm)
        {
            var which = depBmr ? "BMR" : "VBM";
            DrawDepLine($"BossMod ({which})", true, "Installed", depBmr ? "BMR" : "VBM");
        }
        else
        {
            DrawDepLine("BossMod (BMR or VBM)", false, "NOT FOUND - Need one", "BMR");
        }

        // VNAV
        DrawDepLine("vnavmesh", depVnav, depVnav ? "Installed" : "NOT FOUND", "vnavmesh");

        // TextAdvance
        DrawDepLine("TextAdvance", depTextAdv, depTextAdv ? "Installed" : "NOT FOUND", "TextAdvance");

        // SkipCutscene
        DrawDepLine("SkipCutscene", depCutsceneSkip, depCutsceneSkip ? "Installed" : "NOT FOUND", "SkipCutscene");

        // AutoDuty
        DrawDepLine("AutoDuty", depAutoDuty, depAutoDuty ? "Installed" : "NOT FOUND", "AutoDuty");

        // SimpleTweaks
        DrawDepLine("SimpleTweaks", depSimpleTweaks, depSimpleTweaks ? "Installed" : "NOT FOUND", "SimpleTweaks");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Optional Plugins");
        ImGui.Separator();

        DrawDepLineOptional("CustomResolution", depCustomRes);
        DrawDepLineOptional("ChillFrames", depChillframes);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "AutoDuty Path");
        ImGui.Separator();

        var pathExists = plugin.AutoDutyPathService.PathExists();
        ImGui.TextColored(
            pathExists ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1),
            pathExists ? "Praetorium path: INSTALLED" : "Praetorium path: NOT FOUND");

        if (!pathExists)
        {
            if (ImGui.Button("Install Praetorium Path"))
            {
                plugin.AutoDutyPathService.EnsurePathExists();
            }
            ImGui.TextDisabled("This copies the W2W path file to AutoDuty's paths folder.");
        }

        // Copy AutoDuty paths folder path
        if (ImGui.Button("Copy AutoDuty Paths Folder"))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var adPath = System.IO.Path.Combine(appData, "XIVLauncher", "pluginConfigs", "AutoDuty", "paths");
            ImGui.SetClipboardText(adPath);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Copy path folder location to clipboard");

        ImGui.Spacing();
        ImGui.TextWrapped("To change AutoDuty's active path: Open AutoDuty > Paths tab > Select the Praetorium W2W path.");
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

    private static void DrawDepLineOptional(string name, bool exists)
    {
        var color = exists ? new Vector4(0, 1, 0, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
        var icon = exists ? "[OK]" : "[--]";
        ImGui.TextColored(color, $"{icon} {name}");
        ImGui.SameLine();
        ImGui.TextDisabled(exists ? "Installed" : "Not installed (optional)");
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
        ImGui.TextDisabled("Auto-detected for same-world parties. Set manually for cross-world.");

        var isCrossWorld = config.IsCrossWorldParty;
        if (ImGui.Checkbox("Cross-World Party", ref isCrossWorld))
        {
            config.IsCrossWorldParty = isCrossWorld;
            changed = true;
        }
        ImGui.TextDisabled("Enable if you're in a cross-world party.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Party Behaviour:");
        ImGui.TextWrapped("- Leader: Queues duties, initiates repair, controls flow");
        ImGui.TextWrapped("- Non-leader: Waits for queue, repairs independently after 20s outside duty");
        ImGui.TextWrapped("- Solo: Treated as leader automatically");

        return changed;
    }

    private bool DrawDutyTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Duty Settings");
        ImGui.Separator();

        var dutyCounter = config.DutyCounter;
        if (ImGui.InputInt("Duty Counter", ref dutyCounter))
        {
            config.DutyCounter = Math.Clamp(dutyCounter, 0, 666);
            changed = true;
        }
        ImGui.TextDisabled("Current Praetorium run count. Set to 0 for first run of the day.");

        var praeThreshold = config.PraetoriumThreshold;
        if (ImGui.InputInt("Praetorium Threshold", ref praeThreshold))
        {
            config.PraetoriumThreshold = Math.Clamp(praeThreshold, 1, 666);
            changed = true;
        }
        ImGui.TextDisabled("Switch to Decumana after this many Praetorium runs.");

        var maxRuns = config.MaxRuns;
        if (ImGui.InputInt("Max Runs", ref maxRuns))
        {
            config.MaxRuns = Math.Clamp(maxRuns, 0, 9999);
            changed = true;
        }
        ImGui.TextDisabled("Stop after this many total runs. 9999 = unlimited.");

        var quitCommand = config.QuitCommand;
        if (ImGui.InputText("Quit Command", ref quitCommand, 256))
        {
            config.QuitCommand = quitCommand;
            changed = true;
        }
        ImGui.TextDisabled("Command to execute when max runs reached.");

        ImGui.Separator();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Queue Method");

        var queueMethod = config.QueueMethod;
        if (ImGui.RadioButton("AutoDuty (Recommended)", ref queueMethod, 0))
        {
            config.QueueMethod = 0;
            changed = true;
        }
        ImGui.TextDisabled("Uses AutoDuty to queue and run the duty.");

        if (ImGui.RadioButton("Callback Method", ref queueMethod, 1))
        {
            config.QueueMethod = 1;
            changed = true;
        }
        ImGui.TextDisabled("Use if AutoDuty queueing is broken.");

        ImGui.Separator();

        var testMode = config.TestingModeUnsynced;
        if (ImGui.Checkbox("Testing Mode: Unsynced solo+ runs", ref testMode))
        {
            config.TestingModeUnsynced = testMode;
            changed = true;
        }
        if (testMode)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "WARNING: Duty will be queued Unsynced for testing purposes.");
        }
        else
        {
            ImGui.TextDisabled("Enable to run duties unsynced for testing.");
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
            ImGui.Text($"  Selected: {config.FoodItemName} (ID: {config.FoodItemId})");
            if (ImGui.SmallButton("Clear Food"))
            {
                config.FoodItemId = 0;
                config.FoodItemName = "";
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
            ImGui.Text($"  Selected: {config.PotionItemName} (ID: {config.PotionItemId})");
            if (ImGui.SmallButton("Clear Potion"))
            {
                config.PotionItemId = 0;
                config.PotionItemName = "";
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

    private bool DrawAdvancedTab(Configuration config)
    {
        var changed = false;

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
