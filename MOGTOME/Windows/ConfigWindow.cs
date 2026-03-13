using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace MOGTOME.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("MOGTOME - Configuration##MogtomeConfig",
            ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 600),
            MaximumSize = new Vector2(600, 900),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var changed = false;

        if (ImGui.BeginTabBar("ConfigTabs"))
        {
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

            if (ImGui.BeginTabItem("Repair"))
            {
                changed |= DrawRepairTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Food & Pots"))
            {
                changed |= DrawFoodPotTab(config);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Rotation"))
            {
                changed |= DrawRotationTab(config);
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
            config.Save();
        }
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
        ImGui.TextDisabled("Pre-select the correct path in AutoDuty first!");

        if (ImGui.RadioButton("Callback Method", ref queueMethod, 1))
        {
            config.QueueMethod = 1;
            changed = true;
        }
        ImGui.TextDisabled("Use if AutoDuty queueing is broken.");

        return changed;
    }

    private bool DrawRepairTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Repair Settings");
        ImGui.Separator();

        var repairThreshold = config.RepairThreshold;
        if (ImGui.SliderInt("Repair Threshold %", ref repairThreshold, -1, 99))
        {
            config.RepairThreshold = repairThreshold;
            changed = true;
        }
        ImGui.TextDisabled("-1 = Never repair. Leader repairs at this %. Party members repair at 99%.");

        var repairMat = config.RepairMaterialId;
        if (ImGui.InputInt("Repair Material ID", ref repairMat))
        {
            config.RepairMaterialId = repairMat;
            changed = true;
        }
        ImGui.TextDisabled("33916 = Grade 8 Dark Matter, 17837 = Grade 7, 10386 = Grade 6");

        var autoEquip = config.AutoEquipRecommended;
        if (ImGui.Checkbox("Auto-Equip Recommended Gear", ref autoEquip))
        {
            config.AutoEquipRecommended = autoEquip;
            changed = true;
        }
        ImGui.TextDisabled("Uses /equiprecommended + /updategearset. Disable if you manage BiS manually.");

        return changed;
    }

    private bool DrawFoodPotTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Food Settings");
        ImGui.Separator();

        var foodId = config.FoodItemId;
        if (ImGui.InputInt("Food Item ID", ref foodId))
        {
            config.FoodItemId = foodId;
            changed = true;
        }
        ImGui.TextDisabled("0 = Disabled. Use SimpleTweaks ShowID to find item IDs.");

        var foodName = config.FoodItemName;
        if (ImGui.InputText("Food Name (display)", ref foodName, 128))
        {
            config.FoodItemName = foodName;
            changed = true;
        }
        ImGui.TextDisabled("For display only, doesn't affect functionality.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Potion Settings");
        ImGui.Separator();

        var potId = config.PotionItemId;
        if (ImGui.InputInt("Potion Item ID", ref potId))
        {
            config.PotionItemId = potId;
            changed = true;
        }
        ImGui.TextDisabled("0 = Disabled. e.g. Gemdraught of Strength III");

        var potName = config.PotionItemName;
        if (ImGui.InputText("Potion Name (display)", ref potName, 128))
        {
            config.PotionItemName = potName;
            changed = true;
        }

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

        return changed;
    }

    private bool DrawRotationTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Rotation Plugin");
        ImGui.Separator();

        var preset = config.BossModPreset;
        if (ImGui.InputText("BossMod AI Preset", ref preset, 128))
        {
            config.BossModPreset = preset;
            changed = true;
        }
        ImGui.TextDisabled("Set to 'none' to use RSR instead of BossMod.");
        ImGui.TextDisabled("Otherwise, enter the BossMod/VBM AI preset name.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Rotation Plugin Notes:");
        ImGui.TextWrapped("- BossMod: Raiding theme, decent DPS, no slowdowns");
        ImGui.TextWrapped("- RSR: Easy to use, best DPS at level 50");
        ImGui.TextWrapped("- WRATH: LARPing theme, may cause FPS drops");
        ImGui.TextWrapped("- UCOMBO: Higher DPS than RSR, not ready for Prae");

        return changed;
    }

    private bool DrawAdvancedTab(Configuration config)
    {
        var changed = false;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Timing");
        ImGui.Separator();

        var loopInterval = config.LoopInterval;
        if (ImGui.SliderFloat("Loop Interval (sec)", ref loopInterval, 0.5f, 10.0f))
        {
            config.LoopInterval = loopInterval;
            changed = true;
        }
        ImGui.TextDisabled("2.0 for leader, 5.0+ for party members. Lower = faster but more CPU.");

        var echoLevel = config.EchoLevel;
        if (ImGui.SliderInt("Echo Level", ref echoLevel, 0, 5))
        {
            config.EchoLevel = echoLevel;
            changed = true;
        }
        ImGui.TextDisabled("5=Critical only, 4=Counters, 3=Important, 2=Progress, 1=More, 0=All");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Debug");
        ImGui.Separator();

        var debugCounter = config.DebugCounter;
        if (ImGui.InputInt("Debug Counter", ref debugCounter))
        {
            config.DebugCounter = debugCounter;
            changed = true;
        }
        ImGui.TextDisabled("Crash recovery offset. Set to last crash counter+1 to resume.");

        var bailout = config.BailoutTimeout;
        if (ImGui.InputInt("Bailout Timeout (sec)", ref bailout))
        {
            config.BailoutTimeout = Math.Clamp(bailout, 60, 3600);
            changed = true;
        }
        ImGui.TextDisabled("Leave duty after this many seconds. Default: 1200 (20 min).");

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
        }

        return changed;
    }
}
