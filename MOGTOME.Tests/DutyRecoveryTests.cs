using System.Reflection;
using System.Reflection.Emit;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Dalamud.Game;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Action = System.Action;

namespace MOGTOME.Tests;

// Real engine/services with in-memory Dalamud boundaries. No game, config files,
// database, or native callbacks are used by this fixture.
public sealed class DutyRecoveryTests
{
    internal static BossModIPC CreateReadyAutomationHost()
        => new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod").CreateService();

    [Theory]
    [InlineData("BossModReborn")]
    [InlineData("GodMod")]
    public void BeastmasterPreferencesSurviveTwoMogActivationsAndHostSaveReload(string host)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "", internalName: host);
        native.EnableRegularBeastmaster();
        var saved = JsonNode.Parse("""
            {"Name":"FRENRIDER - MELEE","Modules":{"BMRVariantModulePack.Autorotation.Dhog.DhogBST":[
              {"Track":"Targeting","Option":"Auto"},
              {"Track":"DhogFirstBeast","Option":"Familiar01"},
              {"Track":"DhogSecondBeast","Option":"Familiar32"},
              {"Track":"DhogThirdBeast","Option":"Familiar34"},
              {"Track":"DhogFirstBeast","Option":"Familiar06","Mod":"Shift"},
              {"Track":"DhogFirstBeast","Option":"Familiar29","Mod":"Shift, Ctrl","PriorityOverride":4500},
              {"Track":"DhogFirstBeast","Option":"Familiar50","Mod":"Ctrl"}
            ]}}
            """)!;
        if (host != "GodMod") saved["Modules"]![NativeBossModProvider.BstModule]!.AsArray().Add(
            JsonNode.Parse("""{"Track":"DhogBurst","Option":"Hold"}"""));
        var expectedSettings = saved["Modules"]![NativeBossModProvider.BstModule]!.DeepClone();
        native.SavePreset(saved.ToJsonString());
        var other = saved.DeepClone();
        other["Name"] = "Other";
        other["Modules"]![NativeBossModProvider.BstModule] = JsonNode.Parse("""
            [{"Track":"DhogFirstBeast","Option":"Familiar50"},{"Track":"DhogSecondBeast","Option":"Familiar29"},
             {"Track":"DhogThirdBeast","Option":"Familiar01"}]
            """);
        native.SavePreset(other.ToJsonString());
        native.Selector = "Other";
        var otherBefore = native.SerializePreset(native.Presets.AllPresets.Single(preset => preset.Name == "Other"));
        var usesGmc = host == "BossModReborn";
        using var run = new Run(native) { GmcLoaded = usesGmc };
        run.Config.CombatProvider = usesGmc ? CombatProvider.Wrath : CombatProvider.Bmr;
        run.Config.UseManualBossModPreset = usesGmc;
        run.Config.ManualBossModPresetName = "Other";
        for (var activation = 0; activation < 2; ++activation)
        {
            native.CurrentModifiers = activation == 0 ? NativeModifier.Shift : NativeModifier.Ctrl;
            var savedSource = native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - MELEE");
            var savedModule = savedSource.Modules.Single(module => module.ModuleName == NativeBossModProvider.BstModule);
            Assert.Empty(savedModule.TransientSettings); // JSON/save-reload never persists transients.
            if (activation == 0)
            {
                var targetingTrack = savedModule.Definition.Configs.FindIndex(config => config.InternalName == "Targeting");
                savedModule.TransientSettings.Add(new(NativeModifier.Shift, targetingTrack,
                    new JsonObject { ["Option"] = "Auto", ["PriorityOverride"] = 4400 }));
                savedModule.TransientSettings.Add(new(NativeModifier.Shift, targetingTrack,
                    new JsonObject { ["Option"] = "Auto", ["PriorityOverride"] = 4500 }));
            }
            native.Selector = "Other";
            native.AiEnabled = true;
            native.AiEnabled = false;
            Assert.Equal("Other", native.AiPreset!.Name);
            native.Rotation.Player.InCombat = false;
            Assert.Null(native.Rotation.Preset); // Native targetless/Idle start.
            Assert.True(run.Rotation.Initialize());
            Assert.False(run.Rotation.IsCombatActivationDeferred);
            var source = native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - MELEE");
            var sourceBefore = native.SerializePreset(source);
            Assert.Same(savedSource, source);
            native.Rotation.Player.InCombat = true;
            Assert.True(run.Rotation.EnableRotation());
            var clone = Assert.IsAssignableFrom<NativePreset>(native.Rotation.Preset);
            Assert.NotSame(source, clone);
            Assert.DoesNotContain(clone.Modules, module => module.Definition.Order == NativeModuleOrder.Movement);
            var cloneModule = clone.Modules.Single(module => module.ModuleName == NativeBossModProvider.BstModule);
            Assert.True(savedModule.TransientSettings.SequenceEqual(cloneModule.TransientSettings));
            Assert.NotSame(savedModule.TransientSettings, cloneModule.TransientSettings);
            Assert.Equal(activation == 0 ? 2 : 0, cloneModule.TransientSettings.Count);
            var resolved = clone.ActiveStrategyOverrides(clone.Modules.IndexOf(cloneModule));
            Assert.Equal(activation == 0 ? "Familiar06" : "Familiar50", resolved.Option("DhogFirstBeast"));
            Assert.Equal("Familiar32", resolved.Option("DhogSecondBeast"));
            Assert.Equal("Familiar34", resolved.Option("DhogThirdBeast"));
            Assert.Equal(sourceBefore, native.SerializePreset(source));
            var current = native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - MELEE");
            var actual = JsonNode.Parse(native.SerializePreset(current))!;
            Assert.True(JsonNode.DeepEquals(expectedSettings, actual["Modules"]![NativeBossModProvider.BstModule]));
            Assert.True(run.Rotation.DisableRotation());
            Assert.DoesNotContain(native.Writes, write => write.Contains("Presets.Create") || write.Contains("Presets.Delete"));
            Assert.Equal(usesGmc ? CombatProvider.Wrath : CombatProvider.Bmr, run.Config.CombatProvider);
            Assert.Equal(usesGmc, run.Config.UseManualBossModPreset);
            Assert.Equal("Other", run.Config.ManualBossModPresetName);
            Assert.Equal(otherBefore, native.SerializePreset(native.Presets.AllPresets.Single(preset => preset.Name == "Other")));
            if (activation == 0) native.ReloadSavedPresets();
        }
        Assert.Equal(2, native.NativeClones);
        Assert.True(native.NativeDeserializations > 2);
        Assert.True(native.NativeSerializations > 2);
        Assert.True(native.NativeResolutions >= 2);
    }

    [Theory]
    [InlineData("BossModReborn")]
    [InlineData("GodMod")]
    public void BeastmasterUsesTheManualPresetWithoutGmc(string host)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "", internalName: host);
        native.EnableRegularBeastmaster();
        native.SavePreset("""{"Name":"Other","Modules":{"BMRVariantModulePack.Autorotation.Dhog.DhogBST":[{"Track":"DhogFirstBeast","Option":"Familiar50"},{"Track":"DhogSecondBeast","Option":"Familiar29"},{"Track":"DhogThirdBeast","Option":"Familiar01"}]}}""");
        var manual = native.Presets.AllPresets.Single(preset => preset.Name == "Other");
        var module = manual.Modules.Single();
        module.TransientSettings.Add(new(NativeModifier.Shift, module.Definition.Configs.FindIndex(config => config.InternalName == "DhogFirstBeast"),
            new JsonObject { ["Option"] = "Familiar06" }));
        native.CurrentModifiers = NativeModifier.Shift;
        var saved = native.SerializePreset(manual);
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "Other";
        Assert.True(run.Rotation.Initialize());
        Assert.False(run.Rotation.IsCombatActivationDeferred);
        Assert.True(run.Rotation.EnableRotation());
        Assert.Equal("Other", native.Selector);
        Assert.Same(manual, native.AiPreset);
        var aiManager = Get(native, "aiManager")!;
        var behavior = Assert.IsType<NativeAiBehavior>(aiManager.GetType().GetField("Beh")!.GetValue(aiManager));
        Assert.Same(manual, behavior.AIPreset);
        if (host == "BossModReborn")
        {
            Assert.Empty(native.Active); // Existing /bmrai on clears targetless runtime.
            Assert.Null(native.Rotation.Preset);
        }
        else
        {
            Assert.Equal(new[] { "Other" }, native.Active);
            Assert.Same(manual, native.Rotation.Preset);
        }
        var resolved = manual.ActiveStrategyOverrides(manual.Modules.IndexOf(module));
        Assert.Equal("Familiar06", resolved.Option("DhogFirstBeast"));
        Assert.Equal("Familiar29", resolved.Option("DhogSecondBeast"));
        Assert.Same(manual, native.Presets.AllPresets.Single(preset => preset.Name == "Other"));
        Assert.Single(module.TransientSettings);
        Assert.Equal(saved, native.SerializePreset(manual));
    }

    [Theory]
    [InlineData(0, "D141Colossus")]
    [InlineData(1, "D142Nero")]
    [InlineData(2, "D143Gaius")]
    public void NativePraetoriumTypesUseOnlyTheirExactStablePlanName(int encounter, string stableBoss)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        var boss = native.NativePraetoriumEncounters[encounter];
        var module = Activator.CreateInstance(boss)!;
        native.Rotation.Bossmods.ActiveModule = module;
        var stable = new NativePlan($"BMRV-{stableBoss}-Tank-WAR", NativeJob.WAR, boss);
        var foreign = new NativePlan($"BMRV-{stableBoss}-Tank-WAR", NativeJob.WAR, typeof(D141Colossus));
        var wrongJob = new NativePlan($"BMRV-{stableBoss}-Tank-WAR", NativeJob.BST, boss);
        var list = new NativePlanList { Plans = [foreign, wrongJob, stable] };
        native.Rotation.Database.Plans.Plans[boss] = new() { [NativeJob.WAR] = list };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);
        native.Rotation.Planner = new(module, stable);
        Assert.True(run.BossMod.UpdateGmcAutorotation());
        Assert.Empty(native.Active);
    }

    [Fact]
    public void BeastmasterReturnsFromPlanToTheUpdatedGroupPreset()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "");
        native.EnableRegularBeastmaster();
        native.SavePreset("""{"Name":"FRENRIDER - MELEE","Modules":{"BMRVariantModulePack.Autorotation.Dhog.DhogBST":[{"Track":"DhogFirstBeast","Option":"Familiar01"},{"Track":"DhogSecondBeast","Option":"Familiar32"},{"Track":"DhogThirdBeast","Option":"Familiar34"}]}}""");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        var module = new D141Colossus();
        var plan = new NativePlan("BMRV-D141Colossus-Melee-BST", NativeJob.BST, typeof(D141Colossus));
        native.Rotation.Bossmods.ActiveModule = module;
        native.Rotation.Database.Plans.Plans[typeof(D141Colossus)] = new() { [NativeJob.BST] = new() { Plans = [plan] } };
        Assert.True(run.BossMod.UpdateGmcAutorotation());
        native.Rotation.Planner = new(module, plan);
        Assert.True(run.BossMod.UpdateGmcAutorotation());
        Assert.Null(Get(run.BossMod, "gmcOwnedRotation"));
        Assert.Null(native.Rotation.Preset);
        var changed = JsonNode.Parse(native.SerializePreset(native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - MELEE")))!;
        changed["Modules"]![NativeBossModProvider.BstModule]!.AsArray()
            .Single(row => row!["Track"]!.GetValue<string>() == "DhogFirstBeast")!["Option"] = "Familiar50";
        native.SavePreset(changed.ToJsonString());
        native.Rotation.Planner = null;
        Assert.True(run.BossMod.UpdateGmcAutorotation());
        Assert.Equal("FRENRIDER - MELEE", native.Rotation.Preset!.Name);
        var moduleIndex = native.Rotation.Preset.Modules.FindIndex(candidate => candidate.ModuleName == NativeBossModProvider.BstModule);
        Assert.Equal("Familiar50", native.Rotation.Preset.ActiveStrategyOverrides(moduleIndex).Option("DhogFirstBeast"));
    }

    [Theory]
    [InlineData(CombatProvider.Rsr)]
    [InlineData(CombatProvider.Wrath)]
    [InlineData(CombatProvider.Vbm)]
    public void GmcUsesOwnedNativeCombatAiAndPreservesSavedProvider(CombatProvider saved)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        run.Config.CombatProvider = saved;
        Assert.True(run.Rotation.Initialize(preferBmr: true));
        run.Rotation.RestoreRsrHealingForStart();
        Assert.Equal(0, run.RsrSettingsRequests);
        Assert.True(run.Rotation.EnableRotationOncePerDuty("GMC test"));
        Assert.Equal(saved, run.Config.CombatProvider);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);
        Assert.True(native.AiEnabled);
        Assert.Equal("FRENRIDER - TANK", native.Selector);
        Assert.NotSame(native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - TANK"), native.Rotation.Preset);
        Assert.All(native.Rotation.Preset!.Modules, module => Assert.NotEqual(NativeModuleOrder.Movement, module.Definition.Order));
        Assert.Contains(native.Presets.AllPresets.Single(preset => preset.Name == "FRENRIDER - TANK").Modules,
            module => module.Definition.Order == NativeModuleOrder.Movement);
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.AI.SetPreset", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Commands, command => command.EndsWith(" on", StringComparison.Ordinal)
            || command.StartsWith("/rotation", StringComparison.Ordinal) || command.StartsWith("/wrath", StringComparison.Ordinal));
        Assert.True(run.Rotation.DisableRotationForDutyEnd("GMC Stop"));
        Assert.Equal(new[] { "Original" }, native.Active);
    }

    [Theory]
    [InlineData(typeof(D141Colossus), 1044u)]
    [InlineData(typeof(D142Nero), 1044u)]
    [InlineData(typeof(D143Gaius), 1044u)]
    [InlineData(typeof(T04PortaDecumana1), 1048u)]
    [InlineData(typeof(T04PortaDecumana2), 1048u)]
    public void GmcUsesInjectedBossPlanOnlyAfterNativeBindingAndRestoresSelections(Type boss, uint territory)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = territory };
        var module = Activator.CreateInstance(boss, nonPublic: true)!;
        native.Rotation.Bossmods.ActiveModule = module;
        var original = new NativePlan("Existing job plan", NativeJob.WAR, boss);
        var bundled = new NativePlan($"BMRV-{boss.Name}-Tank-WAR", NativeJob.WAR, boss);
        var list = new NativePlanList { SelectedIndex = 0, Plans = [original, bundled] };
        native.Rotation.Database.Plans.Plans[boss] = new() { [NativeJob.WAR] = list };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("GMC boss"));
        Assert.Equal(1, list.SelectedIndex);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);

        native.Rotation.Planner = new(module, bundled); // BMR's next native update.
        run.Rotation.UpdateDutyRotationHealth(territory, true, false, false, "boss plan ready");
        Assert.Empty(native.Active);
        Assert.False(native.ForceDisabled);
        Assert.True(native.AiEnabled);
        Assert.Null(native.Selector); // A bound plan owns an explicit null selector.
        Assert.True(run.Rotation.DisableRotationForDutyEnd("GMC Stop"));
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(new[] { "Original" }, native.Active);
        native.Rotation.Planner = null;
        run.Rotation.UpdateDutyRotationHealth(territory, true, false, false, "after Stop");
        Assert.Equal(new[] { "Original" }, native.Active);
    }

    [Fact]
    public void GmcKeepsNormalGroupRotationWithoutPlansAndChangesWithCurrentJob()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1048 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("Decumana without plans"));
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);
        native.Rotation.Player.Class = NativeJob.NIN;
        run.Rotation.UpdateDutyRotationHealth(1048, true, false, false, "current job changed");
        Assert.Equal(new[] { "FRENRIDER - MELEE" }, native.Active);
        Assert.Empty(native.Rotation.Database.Plans.Plans);
    }

    [Fact]
    public void GmcReturnsToGroupRotationBetweenBossesAndRetainsLaterNativeChoices()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        var module = new D142Nero();
        var plan = new NativePlan("BMRV-D142Nero-Tank-WAR", NativeJob.WAR, typeof(D142Nero));
        var list = new NativePlanList { Plans = [plan] };
        native.Rotation.Bossmods.ActiveModule = module;
        native.Rotation.Database.Plans.Plans[typeof(D142Nero)] = new() { [NativeJob.WAR] = list };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("GMC boss"));
        native.Rotation.Planner = new(module, plan);
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "plan bound");
        Assert.Empty(native.Active);
        native.Rotation.Bossmods.ActiveModule = null;
        native.Rotation.Planner = null;
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "boss ended");
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);

        native.Rotation.Bossmods.ActiveModule = module;
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "next pull");
        Assert.Equal(0, list.SelectedIndex);
        list.SelectedIndex = -1; // An explicit later native None is retained.
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "native user choice");
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "no reselection");
        Assert.Equal(-1, list.SelectedIndex);
        var external = native.Presets.AllPresets.Single(preset => preset.Name == "External");
        native.Rotation.Preset = external;
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "native preset choice");
        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Same(external, native.Rotation.Preset);
    }

    [Fact]
    public void GmcDepartureStopsItsOwnedAutorotationWithoutStartingAnotherProvider()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { AiEnabled = true };
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        run.Config.CombatProvider = CombatProvider.Rsr;
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("GMC start"));
        Assert.True(native.AiEnabled);
        Assert.Empty(run.Commands);
        run.GmcLoaded = false;
        run.Rotation.ObserveSessionDeparture();
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.False(run.Rotation.EnableRotationOncePerDuty("departed GMC"));
        Assert.False(native.AiEnabled);
        Assert.Empty(run.Commands);
    }

    [Theory]
    [InlineData("BossModReborn", false)]
    [InlineData("GodMod", false)]
    [InlineData("BossModReborn", true)]
    [InlineData("GodMod", true)]
    public void GmcFightAuthorityLeavesRouteIdleAndRestoresPriorModeAndFlags(string host, bool originallyActive)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: host)
            { AiEnabled = originallyActive, ManualTarget = true, ForbidActions = true, ForbidMovement = true };
        native.AiMasterSlot = originallyActive ? 2 : 0;
        native.Rotation.Player.InCombat = false;
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("route ready"));
        Assert.False(native.AiEnabled); // State oracle, not proof of native controller quiescence.
        Assert.False(native.ManualTarget);
        Assert.False(native.ForbidActions);
        Assert.False(native.ForbidMovement);
        var group = native.Rotation.Preset;
        native.Rotation.Player.InCombat = true;
        run.Rotation.ObserveSessionDeparture();
        Assert.True(native.AiEnabled);
        Assert.Same(group, native.AiPreset);
        Assert.Same(group, native.Rotation.Preset);
        native.Rotation.Player.InCombat = false;
        run.Rotation.ObserveSessionDeparture();
        Assert.False(native.AiEnabled);
        Assert.Same(group, native.Rotation.Preset);
        Assert.True(run.Rotation.DisableRotationForDutyEnd("Stop"));
        Assert.Equal(originallyActive, native.AiEnabled);
        Assert.Equal(originallyActive ? 2 : 0, native.AiMasterSlot);
        Assert.True(native.ManualTarget);
        Assert.True(native.ForbidActions);
        Assert.True(native.ForbidMovement);
        Assert.Equal("Original", native.Selector);
        Assert.Equal(new[] { "Original" }, native.Active);
        var writes = native.Writes.ToArray();
        Assert.False(run.Rotation.EnableRotationOncePerDuty("after Stop"));
        Assert.Equal(writes, native.Writes);
    }

    [Theory]
    [InlineData(6u, "FRENRIDER - RANGED (GMC)")]
    [InlineData(24u, "FRENRIDER - RANGED (GMC)")]
    [InlineData(28u, "FRENRIDER - RANGED (GMC)")]
    [InlineData(33u, "FRENRIDER - RANGED (GMC)")]
    [InlineData(40u, "FRENRIDER - RANGED (GMC)")]
    [InlineData(31u, "FRENRIDER - RANGED")]
    [InlineData(37u, "FRENRIDER - TANK")]
    public void GmcSelectsProviderHealerGroupAndLeavesTheLegacyRangedPresetUntouched(uint job, string expected)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { DhogHealersAvailable = true };
        native.SavePreset("""{"Name":"FRENRIDER - RANGED","Modules":{"BossMod.Autorotation.xan.BRD":[]}}""");
        var legacy = native.Presets.UserPresets.Single(preset => preset.Name == "FRENRIDER - RANGED");
        var legacyBefore = native.SerializePreset(legacy);
        native.Rotation.Player.Class = (NativeJob)job;
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        run.Config.CombatProvider = CombatProvider.Wrath;
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "Other";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("automatic GMC role"));
        Assert.Equal(new[] { expected }, native.Active);
        Assert.Same(native.Rotation.Preset, native.AiPreset);
        Assert.Equal(CombatProvider.Wrath, run.Config.CombatProvider);
        Assert.True(run.Config.UseManualBossModPreset);
        Assert.Equal("Other", run.Config.ManualBossModPresetName);
        if (expected == "FRENRIDER - RANGED (GMC)")
            Assert.Contains(native.Rotation.Preset!.Modules, module => module.ModuleName == "BMRVariantModulePack.Autorotation.Dhog.DhogSGE");
        Assert.True(run.Rotation.DisableRotation());
        Assert.Same(legacy, native.Presets.UserPresets.Single(preset => preset.Name == "FRENRIDER - RANGED"));
        Assert.Equal(legacyBefore, native.SerializePreset(legacy));
        Assert.DoesNotContain(native.Writes, write => write.Contains("Presets.Create") || write.Contains("Presets.Delete"));
    }

    [Theory]
    [InlineData("BossModReborn", true, false)]
    [InlineData("BossModReborn", false, false)]
    [InlineData("GodMod", true, false)]
    [InlineData("GodMod", false, false)]
    [InlineData("GodMod", true, true)]
    public void GmcBoundSagePlanDeathPreservesModulesAndRecoversWithoutAssumingNativeClear(string host, bool clear, bool scripted)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: host)
            { DhogHealersAvailable = true, ClearPresetOnDeath = clear };
        native.Rotation.Player.Class = NativeJob.SGE;
        native.Rotation.Hints.ScriptedDeath = scripted;
        using var run = new Run(native) { GmcLoaded = host == "BossModReborn", Territory = 1044 };
        var module = new D142Nero();
        var plan = new NativePlan("BMRV-D142Nero-Healer-SGE", NativeJob.SGE, typeof(D142Nero))
            { Modules = ["native SGE", "native HealerAI", "native SGE utility"] };
        var modules = plan.Modules.ToArray();
        var list = new NativePlanList { Plans = [plan] };
        native.Rotation.Database.Plans.Plans[typeof(D142Nero)] = new() { [NativeJob.SGE] = list };
        native.Rotation.Bossmods.ActiveModule = module;
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("Sage group"));
        Assert.Equal(host == "BossModReborn" ? "FRENRIDER - RANGED (GMC)" : "FRENRIDER - RANGED", native.Rotation.Preset!.Name);
        native.Rotation.Planner = new(module, plan);
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "plan bound");
        Assert.Null(native.Rotation.Preset);
        Assert.Null(native.AiPreset);
        Assert.Null(native.Selector);
        native.Die();
        Assert.Equal(clear && !scripted, native.ForceDisabled);
        run.Rotation.InvalidateCombatActivation();
        run.Rotation.ObserveSessionDeparture();
        native.Rotation.Player.IsDead = false;
        Assert.True(run.Rotation.EnableRotationOncePerDuty("raise"));
        Assert.False(native.ForceDisabled);
        Assert.Null(native.Rotation.Preset);
        Assert.Same(plan, native.Rotation.Planner.Plan);
        Assert.Equal(modules, plan.Modules);
        Assert.True(run.Rotation.DisableRotationForDutyEnd("Stop"));
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal("Original", native.Selector);
    }

    [Theory]
    [InlineData("BossModReborn", true)]
    [InlineData("BossModReborn", false)]
    [InlineData("GodMod", true)]
    [InlineData("GodMod", false)]
    public void GmcGroupDeathWaitsForNativeWitnessAndAllowsUnchangedRuntime(string host, bool clear)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: host)
            { ClearPresetOnDeath = clear };
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("before HP0"));
        run.Rotation.InvalidateCombatActivation(); // Consumer HP0 precedes native actor processing.
        Assert.False(run.Rotation.EnableRotationOncePerDuty("native death still pending"));
        native.Die();
        run.Rotation.ObserveSessionDeparture();
        var player = native.Rotation.Player;
        var ownership = Get(run.BossMod, "ownedSettings");
        var beforeReturn = native.Writes.ToArray();
        typeof(NativeRotationManager).GetField("Player")!.SetValue(native.Rotation, null);
        run.Rotation.ObserveSessionDeparture(); // Return/loading must not discard the receipt.
        Assert.True(run.Rotation.IsCombatActivationDeferred);
        Assert.False(run.Rotation.EnableRotationOncePerDuty("return player is unknown"));
        Assert.Same(ownership, Get(run.BossMod, "ownedSettings"));
        Assert.True((bool)Get(run.BossMod, "ownedDeathObserved")!);
        Assert.False((bool)Get(run.Rotation, "sessionEnded")!);
        Assert.Equal(beforeReturn, native.Writes);
        typeof(NativeRotationManager).GetField("Player")!.SetValue(native.Rotation, player);
        player.IsDead = false;
        player.InCombat = false;
        Assert.True(run.Rotation.EnableRotationOncePerDuty("entrance respawn"));
        Assert.False(native.AiEnabled);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);
        var writes = native.Writes.ToArray();
        Assert.True(run.Rotation.EnableRotationOncePerDuty("duplicate ready"));
        Assert.Equal(writes, native.Writes);
    }

    [Theory]
    [InlineData("death-setting")]
    [InlineData("manual-target")]
    [InlineData("actions")]
    [InlineData("AI-off")]
    [InlineData("plan-choice")]
    public void GmcChangedNativeAuthorityDuringDeathCannotReopenCombat(string change)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { ClearPresetOnDeath = false };
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        var module = new D142Nero();
        var plan = new NativePlan("BMRV-D142Nero-Tank-WAR", NativeJob.WAR, typeof(D142Nero));
        var list = new NativePlanList { Plans = [plan] };
        native.Rotation.Database.Plans.Plans[typeof(D142Nero)] = new() { [NativeJob.WAR] = list };
        native.Rotation.Bossmods.ActiveModule = module;
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("start"));
        native.Rotation.Planner = new(module, plan);
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "bound");
        native.Die();
        run.Rotation.InvalidateCombatActivation();
        switch (change)
        {
            case "death-setting": native.ClearPresetOnDeath = true; break;
            case "manual-target": native.ManualTarget = true; break;
            case "actions": native.ForbidActions = true; break;
            case "AI-off": native.AiEnabled = false; break;
            case "plan-choice": list.SelectedIndex = -1; break;
        }
        run.Rotation.ObserveSessionDeparture();
        native.Rotation.Player.IsDead = false;
        Assert.False(run.Rotation.EnableRotationOncePerDuty("newer native choice"));
        var writes = native.Writes.ToArray();
        Assert.False(run.Rotation.EnableRotationOncePerDuty("still terminal"));
        Assert.Equal(writes, native.Writes);
        if (change == "plan-choice") Assert.Equal(-1, list.SelectedIndex);
        if (change == "manual-target") Assert.True(native.ManualTarget);
        if (change == "actions") Assert.True(native.ForbidActions);
    }

    [Fact]
    public void GmcNativeNoTargetClearIsNotATimedGroupReassertion()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("group"));
        var group = native.AiPreset;
        native.PublishAiPreset(false);
        var writes = native.Writes.ToArray();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "no target");
        Assert.Null(native.Rotation.Preset);
        Assert.Same(group, native.AiPreset);
        Assert.Equal(writes, native.Writes);
        native.PublishAiPreset(true);
        Assert.Same(group, native.Rotation.Preset);
    }

    [Fact]
    public void GmcPreservesALaterRuntimeObjectEvenWithTheSamePresetName()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("group"));
        var later = new NativePreset("FRENRIDER - TANK");
        native.Rotation.Preset = later;
        run.Rotation.ObserveSessionDeparture();
        Assert.Same(later, native.Rotation.Preset);
        Assert.False(native.AiEnabled);
        Assert.False(run.Rotation.EnableRotationOncePerDuty("later manual runtime"));
    }

    [Fact]
    public void GmcInactiveNativeNoneAndNewNativeFollowChoiceRemainAuthoritative()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        native.Rotation.Player.InCombat = false;
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("route"));
        native.Rotation.Preset = null; // No active native AI can explain this clear.
        run.Rotation.ObserveSessionDeparture();
        Assert.Null(native.Rotation.Preset);
        Assert.False(run.Rotation.EnableRotationOncePerDuty("native None"));

        var module = new D142Nero();
        var originalPlan = new NativePlan("Earlier plan", NativeJob.WAR, typeof(D142Nero));
        var bundledPlan = new NativePlan("BMRV-D142Nero-Tank-WAR", NativeJob.WAR, typeof(D142Nero));
        var list = new NativePlanList { SelectedIndex = 0, Plans = [originalPlan, bundledPlan] };
        native.Rotation.Bossmods.ActiveModule = module;
        native.Rotation.Database.Plans.Plans[typeof(D142Nero)] = new() { [NativeJob.WAR] = list };
        Assert.True(run.Rotation.Initialize());
        native.Rotation.Player.InCombat = true;
        Assert.True(run.Rotation.EnableRotationOncePerDuty("new run"));
        Assert.Equal(1, list.SelectedIndex);
        native.AiEnabled = true; // Replaces the captured native behavior.
        native.AiMasterSlot = 2;
        var later = native.AiPreset;
        run.Rotation.ObserveSessionDeparture();
        Assert.True(native.AiEnabled);
        Assert.Equal(2, native.AiMasterSlot);
        Assert.Same(later, native.AiPreset);
        Assert.Equal(0, list.SelectedIndex); // Only the unchanged owned plan selection is restored.
        Assert.False(run.Rotation.EnableRotationOncePerDuty("new native follow"));
    }

    [Fact]
    public void GmcDefersPlanSelectionUntilNativeCombatEntryWithoutChangingPullSafety()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native) { GmcLoaded = true, Territory = 1044 };
        var module = new D142Nero();
        var original = new NativePlan("Original job plan", NativeJob.WAR, typeof(D142Nero));
        var bundled = new NativePlan("BMRV-D142Nero-Tank-WAR", NativeJob.WAR, typeof(D142Nero));
        var list = new NativePlanList { SelectedIndex = 0, Plans = [original, bundled] };
        native.Rotation.Database.Plans.Plans[typeof(D142Nero)] = new() { [NativeJob.WAR] = list };
        native.Rotation.Bossmods.LoadedModules = [module];
        native.Rotation.Player.InCombat = false;
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("before automated pull"));
        Assert.Equal(-1, list.SelectedIndex);
        Assert.Equal(new[] { "FRENRIDER - TANK" }, native.Active);
        // This is the native event's actual selected-plan predicate; no plan is
        // selected when it runs, so it cannot force-disable this no-countdown pull.
        Assert.False(list.SelectedIndex >= 0);
        native.Rotation.Player.InCombat = true;
        native.Rotation.Bossmods.ActiveModule = module;
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "native combat event passed");
        Assert.Equal(1, list.SelectedIndex);
        native.Rotation.Planner = new(module, bundled);
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "plan bound");
        Assert.Empty(native.Active);
        Assert.True(run.Rotation.DisableRotationForDutyEnd("Stop"));
        Assert.Equal(0, list.SelectedIndex);
        Assert.DoesNotContain(native.Writes, write => write.Contains("PlannedPullSafety", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VbmCatalogUsesTheReleasedTickOrNewRegisteredDatabase(bool releasedLayout)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SyntheticVbmCatalog"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Provider");
        var databaseType = module.DefineType("BossMod.Autorotation.RotationDatabase", TypeAttributes.Public).CreateType()!;
        var tickBuilder = module.DefineType("BossMod.Services.TickService", TypeAttributes.Public);
        if (releasedLayout)
            tickBuilder.DefineField("_rotationDB", databaseType, FieldAttributes.Private);
        var tickType = tickBuilder.CreateType()!;
        var database = Activator.CreateInstance(databaseType)!;
        var service = releasedLayout ? Activator.CreateInstance(tickType)! : database;
        if (releasedLayout)
            tickType.GetField("_rotationDB", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, database);
        var services = new CatalogServices(service);
        var provider = new CatalogPlugin(new NativeHost(services));

        Assert.Same(database, BossModIPC.ReadRotationDatabase("VBM", provider, assembly));
        Assert.Equal(new[] { releasedLayout ? tickType : databaseType }, services.Requests);
        services.Service = new object();
        Assert.Null(BossModIPC.ReadRotationDatabase("VBM", provider, assembly));
    }

    private sealed record CatalogPlugin(NativeHost Host);
    private sealed class CatalogServices(object service) : IServiceProvider
    {
        internal object Service = service;
        internal List<Type> Requests = new();
        public object? GetService(Type type) { Requests.Add(type); return Service; }
    }

    private static readonly AdsHandoffReadinessConditions Ready = new(true, true, true, false, false, false, false);

    [Theory]
    [InlineData(ClientLanguage.English)]
    [InlineData(ClientLanguage.French)]
    [InlineData(ClientLanguage.German)]
    [InlineData(ClientLanguage.Japanese)]
    public void DutyLimitAndPenaltyStopOwnedRegistrationWithoutRetryOrCompletion(ClientLanguage client)
    {
        foreach (var ads in new[] { false, true })
        foreach (var row in new uint[] { 4394, 7567 })
        {
            var evaluator = new LocalizationTests.SheetEvaluator(client);
            using var run = new Run(clientLanguage: client, evaluator: evaluator);
            run.Config.UseAdsExperimental = ads;
            run.Config.QuitCommand = "/quit-test";
            run.State.IsPartyLeader = true;
            var message = evaluator.Text(row);
            var selected = Enum.Parse(typeof(DutyAutomationService).GetNestedType("SelectedMogtomeDuty", BindingFlags.NonPublic)!, "Praetorium");

            Set(run.Engine, "<CurrentState>k__BackingField", EngineState.Queueing);
            run.Engine.HandleRegistrationChatMessage(message); // No owned operation.
            Assert.Equal(EngineState.Queueing, run.Engine.CurrentState);
            Assert.Equal(0, evaluator.Evaluations);
            Invoke(run.Automation, "BeginAdsQueueOperation", selected, "The Praetorium");
            Assert.True(run.Automation.HasPendingQueueRegistration);

            foreach (var ignoredState in new[] { EngineState.Idle, EngineState.WaitingOutsideDuty, EngineState.InDuty })
            {
                Set(run.Engine, "<CurrentState>k__BackingField", ignoredState);
                run.Engine.HandleRegistrationChatMessage(message);
                Assert.Equal(ignoredState, run.Engine.CurrentState);
            }
            Set(run.Engine, "<CurrentState>k__BackingField", EngineState.Queueing);
            run.State.IsPartyLeader = false;
            run.Engine.HandleRegistrationChatMessage(message);
            run.State.IsPartyLeader = true;
            run.State.IsInDuty = true;
            run.Engine.HandleRegistrationChatMessage(message);
            run.State.IsInDuty = false;
            Assert.Equal(0, evaluator.Evaluations);
            evaluator.Throw = true;
            run.Engine.HandleRegistrationChatMessage(message);
            evaluator.Throw = false;
            run.Engine.HandleRegistrationChatMessage("Unrelated " + message);
            run.Engine.HandleRegistrationChatMessage(evaluator.Text(880));
            Assert.Equal(EngineState.Queueing, run.Engine.CurrentState);
            Assert.True(run.Automation.HasPendingQueueRegistration);

            run.Engine.HandleRegistrationChatMessage(message);
            Assert.Equal(EngineState.Idle, run.Engine.CurrentState);
            Assert.Equal(GameText.Normalize(message), run.Engine.StopReason.English);
            Assert.False(run.Automation.HasPendingQueueRegistration);
            Assert.DoesNotContain("/quit-test", run.Commands);
            Assert.Empty(run.History.RunHistory);
            var evaluations = evaluator.Evaluations;
            run.Engine.HandleRegistrationChatMessage(message);
            Assert.Equal(evaluations, evaluator.Evaluations);
            run.Pump();
            Assert.Equal(EngineState.Idle, run.Engine.CurrentState);

            // A subsequent explicit registration owns a fresh operation, not a latched refusal.
            Set(run.Engine, "<CurrentState>k__BackingField", EngineState.Queueing);
            Invoke(run.Automation, "BeginAdsQueueOperation", selected, "The Praetorium");
            Assert.True(run.Automation.HasPendingQueueRegistration);
            run.Engine.Stop();
            Assert.False(run.Automation.HasPendingQueueRegistration);
        }
    }

    [Theory]
    [InlineData(false, 9999, 0, 1000, false, false)]
    [InlineData(true, 0, 0, 999, false, false)]
    [InlineData(true, 0, 0, 1000, true, true)]
    [InlineData(true, 2, 2, 0, true, true)]
    [InlineData(true, 2, 1, 1000, true, true)]
    [InlineData(true, 2, 1, null, true, false)]
    public void FarmingLimitsUseFreshOwnedCurrentCurrencyAndEitherEnabledLimit(bool enabled, int limit,
        int clears, int? owned, bool stopped, bool finished)
    {
        using var run = new Run();
        run.Config.TomestoneTargetEnabled = enabled;
        run.Config.TomestoneTarget = 1000;
        run.Config.MaxRuns = limit;
        run.Config.QuitCommand = "/quit-test";
        run.State.DutyCounter = clears;
        Set(run.Engine, "<CurrentState>k__BackingField", EngineState.WaitingOutsideDuty);
        var reads = 0;
        run.Engine.ReadCurrentTomestones = () => { reads++; return (51750u, owned); };
        Assert.Equal(stopped, (bool)Invoke(run.Engine, "TryHandleFarmingLimit")!);
        Assert.Equal(!stopped, run.Engine.IsRunning);
        Assert.Equal(finished, run.Commands.Contains("/quit-test"));
        if (!enabled || clears >= limit && limit > 0) Assert.Equal(0, reads);
        if (stopped)
        {
            Assert.True((bool)Invoke(run.Engine, "TryHandleFarmingLimit")!);
            Assert.Equal(finished ? 1 : 0, run.Commands.Count(command => command == "/quit-test"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TomestoneTargetDefersInsideAndFinishesOnlyAfterConfirmedReturn(bool leader)
    {
        using var run = new Run();
        run.Config.TomestoneTargetEnabled = true;
        run.Config.TomestoneTarget = 1000;
        run.Config.MaxRuns = 0;
        run.Config.QuitCommand = "/quit-test";
        run.State.IsPartyLeader = leader;
        var reads = 0;
        run.Engine.ReadCurrentTomestones = () => { reads++; return (51750u, 1000); };
        run.Enter(120);
        Set(run.Engine, "dutyCompleted", true);
        Assert.False((bool)Invoke(run.Engine, "TryHandleFarmingLimit")!);
        Assert.Equal(0, reads);
        Assert.True(run.Engine.IsRunning);
        run.CallExit();
        Assert.Equal(1, reads);
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("/quit-test", run.Commands);
        Assert.Single(run.History.RunHistory);
        Assert.Equal(1, run.State.DutyCounter);
        Assert.DoesNotContain(run.Commands, command => command.Contains("buy", StringComparison.OrdinalIgnoreCase)
            || command.Contains("leaveparty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FinalRegistrationEligibilityRechecksTargetAndManualStopDoesNotFinishOrReadIt()
    {
        using var run = new Run();
        run.Config.TomestoneTargetEnabled = true;
        run.Config.TomestoneTarget = 1000;
        run.Config.MaxRuns = 0;
        run.Config.OnlyQueueWithFourPeople = false;
        run.Config.QuitCommand = "/quit-test";
        run.State.IsPartyLeader = true;
        Set(run.Engine, "<CurrentState>k__BackingField", EngineState.WaitingOutsideDuty);
        var owned = 999;
        var reads = 0;
        run.Engine.ReadCurrentTomestones = () => { reads++; return (51750u, owned); };
        Assert.True(run.Automation.QueueEligibility!());
        owned = 1000;
        Assert.False(run.Automation.QueueEligibility!());
        Assert.Equal(2, reads);
        Assert.False(run.Engine.IsRunning);
        Assert.Single(run.Commands, command => command == "/quit-test");
        run.Commands.Clear();
        Set(run.Engine, "<CurrentState>k__BackingField", EngineState.WaitingOutsideDuty);
        run.Engine.Stop();
        Assert.False(run.Automation.QueueEligibility!());
        Assert.Equal(2, reads);
        Assert.DoesNotContain("/quit-test", run.Commands);
    }

    [Fact]
    public void RecordedHistoryBeyond25RemainsAvailableWithRecordingOffAndKeepsDebugFiltering()
    {
        using var run = new Run();
        var records = (List<RunRecord>)typeof(RunHistoryService)
            .GetField("runHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run.History)!;
        var start = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 60; index++)
            records.Add(new RunRecord { Timestamp = start.AddMinutes(index), IsDebugRun = index == 59 });
        run.Config.EnableDetailedTracking = false;
        Assert.Equal(59, run.History.GetRecentRuns().Count);
        Assert.Equal(start.AddMinutes(58), run.History.GetRecentRuns()[0].Timestamp);
        Assert.Equal(25, run.History.GetRecentRuns(25).Count);
        Assert.Equal(60, records.Count);
        run.Config.ShowDebugRuns = true;
        Assert.Equal(60, run.History.GetRecentRuns().Count);
        Assert.Equal(start.AddMinutes(59), run.History.GetRecentRuns()[0].Timestamp);
    }

    [Theory]
    [InlineData("missed", 0)]
    [InlineData("current", 99)]
    [InlineData("legacy", 99)]
    public void AccountCounterUsesExistingResetTimestampAcrossRestart(string scenario, int expected)
    {
        using var run = new Run();
        var now = DateTime.UtcNow;
        var boundary = now.Date.AddHours(7);
        if (now < boundary) boundary = boundary.AddDays(-1);
        run.Config.DutyCounter = 99;
        run.Config.LastDailyDecuReset = scenario switch
        {
            "missed" => now.AddDays(-2),
            "current" => boundary,
            _ => null,
        };
        var tracker = new DutyTrackerService(Plugin.Log, run.Config, run.State, run.Manager, run.History);
        Assert.Equal(expected, run.State.DutyCounter);
        Assert.Equal(expected, run.Config.DutyCounter);
        Assert.True(run.State.NextResetTime > now);
        Assert.NotNull(run.Config.LastDailyDecuReset);
        Assert.False(tracker.CheckDailyReset());
        tracker.UpdateConfiguration(run.Config);
        Assert.Equal(expected, run.State.DutyCounter);
        Assert.Equal(expected, run.Config.DutyCounter);
    }

    [Fact]
    public void DuplicateSuccessfulExitAndStopDoNotAddRunsAndDailyResetAppliesOnce()
    {
        using var run = new Run();
        run.Enter(120, 1044);
        Set(run.Engine, "dutyCompleted", true);
        run.CallExit();
        run.CallExit();
        run.Engine.Stop();
        run.CallExit();
        Assert.Equal(1, run.State.DutyCounter);
        Assert.Equal(1, run.Config.DutyCounter);
        Assert.Single(run.History.RunHistory);
        var tracker = new DutyTrackerService(Plugin.Log, run.Config, run.State, run.Manager, run.History);
        run.State.NextResetTime = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(tracker.CheckDailyReset());
        Assert.False(tracker.CheckDailyReset());
        Assert.Equal(0, run.State.DutyCounter);
        Assert.Equal(0, run.Config.DutyCounter);
        Assert.Single(run.History.RunHistory);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(59)]
    [InlineData(61)]
    [InlineData(1800)]
    public void IncompleteExitRecordsOneAbortAndPreservesStopNext(int seconds)
    {
        using var run = new Run();
        run.Enter(seconds);
        run.Engine.ToggleStopAfterNextSuccessfulRun();
        run.CallExit();
        run.CallExit();
        var record = Assert.Single(run.History.RunHistory);
        Assert.Equal(RunOutcome.Aborted, record.Outcome);
        Assert.Equal(0, run.State.DutyCounter);
        Assert.Equal(0, run.State.DecumanaCounter);
        Assert.True(run.Engine.StopAfterNextSuccessfulRunArmed);
        Assert.True(run.Engine.IsRunning);
        Assert.Equal(EngineState.WaitingOutsideDuty, run.Engine.CurrentState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletionWinsOverBailoutAndStopsOnlyAfterVerifiedExit(bool prae)
    {
        using var run = new Run();
        run.Enter(1800, prae ? 1044u : 1048u);
        run.State.CheckBailout(DateTime.UtcNow, 100);
        run.Engine.ToggleStopAfterNextSuccessfulRun();
        Set(run.Engine, "dutyCompleted", true); // Verified event accepted by DutyStartupService (tested separately).
        Assert.True(run.Engine.IsRunning);
        Assert.Empty(run.History.RunHistory);
        run.CallExit();
        Assert.Equal(RunOutcome.Successful, Assert.Single(run.History.RunHistory).Outcome);
        Assert.Equal(prae ? 1 : 0, run.State.DutyCounter);
        Assert.Equal(prae ? 0 : 1, run.State.DecumanaCounter);
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("Stop-after-next", run.Engine.LastStopReason);
        Assert.DoesNotContain("/quit-test", run.Commands);
        Assert.True(run.Config.LongestRunEver > run.Config.BailoutTimeout);
    }

    [Fact]
    public void ConsecutiveFailuresAndDecumanaDoNotAdvancePraetoriumOrDadCounter()
    {
        using var run = new Run();
        run.Config.MaxRuns = 1;
        run.Config.QuitCommand = "/quit-test";
        foreach (var (territory, success) in new[] { (1044u, false), (1048u, true), (1048u, false), (1044u, true) })
        {
            run.Enter(100, territory);
            Set(run.Engine, "dutyCompleted", success);
            run.CallExit();
        }
        Assert.Equal(4, run.History.RunHistory.Count);
        Assert.Equal(1, run.State.DutyCounter);
        Assert.Equal(1, run.State.DecumanaCounter);
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("Praetorium daily limit", run.Engine.LastStopReason);
        run.Engine.Update();
        Assert.Single(run.Commands, command => command == "/quit-test");
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("death")]
    [InlineData("loading")]
    [InlineData("combat")]
    public void TimeoutIsIndependentOfReadinessAndWaitsForPermittedExit(string stage)
    {
        var now = DateTime.UtcNow;
        var state = new DutyState { HasEnteredDuty = true, IsInDuty = true, DutyStartTime = now, DutyStartTerritory = 1044 };
        var conditions = stage switch
        {
            "startup" => Ready with { HasJob = false },
            "death" => Ready with { IsUnconscious = true, IsPlayerAlive = false },
            "loading" => Ready with { IsBetweenAreas51 = true, HasLocalPlayer = false },
            _ => Ready,
        };
        Assert.False(state.CheckBailout(now.AddSeconds(100), 100));
        Assert.True(state.CheckBailout(now.AddSeconds(101), 100));
        Assert.False(state.CheckBailout(now.AddSeconds(200), 100));
        Assert.Equal(now, state.DutyStartTime);
        var blocker = AdsIntegrationPolicy.GetDutyLeaveBlocker(1044, true, (1044, 16), conditions, stage == "combat", false);
        Assert.Equal(stage is "loading" or "combat", blocker != null);
        Assert.Null(AdsIntegrationPolicy.GetDutyLeaveBlocker(1044, true, (1044, 16), Ready, false, false));
        Assert.True(state.BailoutRequested);
        Assert.Equal(0, state.DutyCounter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalAndForcedQueueWaitForPartyDutyTerritories(bool force)
    {
        using var run = new Run();
        run.State.IsPartyLeader = true;
        run.PartyTerritories = [129, 1044, 1044, 129];
        Assert.False(force ? run.Queue.ForceQueue(true) : run.Queue.TryQueue(true));
        run.PartyTerritories[1] = 129;
        Assert.False(force ? run.Queue.ForceQueue(true) : run.Queue.TryQueue(true));
        Assert.True(run.Queue.LastQueueBlockedForPartyDuty);
        run.PartyTerritories[2] = 129;
        Assert.True(run.Automation.QueueEligibility!());
        run.State.IsPartyLeader = false;
        Assert.False(force ? run.Queue.ForceQueue(true) : run.Queue.TryQueue(true));
        Assert.Empty(run.Commands);
    }

    [Theory]
    [InlineData(16u, 1, true, 16u, true)]
    [InlineData(830u, 1, true, 830u, true)]
    [InlineData(16u, 2, true, 16u, false)]
    [InlineData(16u, 1, true, 830u, false)]
    [InlineData(16u, 0, true, 16u, false)]
    [InlineData(16u, 1, false, 16u, false)]
    public void DirectRegistrationRequiresExactlyIntendedRegularDuty(uint expected, int count, bool regular, uint selected, bool allowed)
        => Assert.Equal(allowed, DutyAutomationService.IsIntendedSelection(expected, count, regular, selected));

    [Fact]
    public void UncheckedDutyIsReselectedAfterStopAndRepairRecoveryDespiteCachedConfirmation()
    {
        using var run = new Run();
        var targetType = typeof(DutyAutomationService).GetNestedType("SelectedMogtomeDuty", BindingFlags.NonPublic)!;
        var praetorium = Enum.Parse(targetType, "Praetorium");
        var decumana = Enum.Parse(targetType, "Decumana");
        bool NeedsSelection(object target, uint? current)
            => (bool)Invoke(run.Automation, "IsDutySelectionRequired", target, current!)!;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            run.Automation.ConfirmQueueRegistration(true);
            run.Engine.Stop();
            // The checkbox can be cleared while stopped; the earlier confirmation survives.
            Assert.True(NeedsSelection(praetorium, null));
            Assert.False(NeedsSelection(praetorium, 16));
            Assert.True(NeedsSelection(praetorium, 830));

            run.Automation.ConfirmQueueRegistration(true);
            Invoke(run.Engine, "BeginQueueRecovery", "test cancelled registration");
            Assert.True(NeedsSelection(praetorium, null));
            Assert.False(NeedsSelection(praetorium, 16));
        }

        // Current checked entries also take precedence over a different cached target.
        run.Automation.ConfirmQueueRegistration(false);
        Assert.False(NeedsSelection(praetorium, 16));
        Assert.True(NeedsSelection(decumana, 16));
        Assert.False(NeedsSelection(decumana, 830));
        Assert.True(NeedsSelection(decumana, null));
    }

    [Fact]
    public void StopContinuesCleanupAfterBackendExceptionAndInvalidatesLeaveCallbacks()
    {
        using var run = new Run();
        run.Enter(100);
        run.YesAlready.Pause();
        Assert.True(run.Rotation.EnableRotation());
        run.State.QueuePausedForRepair = true;
        var staleActionRan = false;
        Invoke(run.Engine, "QueueLeaveAction", "test stale callback", (Action)(() => staleActionRan = true));
        run.ThrowOnCommand = "/ads stop";
        run.Engine.Stop();
        run.Pump();
        Assert.False(staleActionRan);
        Assert.False(run.YesAlready.IsPaused);
        Assert.False(run.State.QueuePausedForRepair);
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("/wrath auto off", run.Commands);
        Assert.Contains("backend cleanup failed", run.Engine.LastStopReason);
        Assert.False(run.Rotation.EnableRotation());
    }

    [Fact]
    public async Task StoppedRepairHandoffCannotSendScheduledStopOrRepairCommands()
    {
        using var run = new Run();
        Set(run.Automation, "adsRepairOperationId", 1);
        Set(run.Automation, "activeAdsRepairOperationId", 1);
        var pending = (Task)Invoke(run.Automation, "ExecuteAdsRepairHandoffAsync", 1, "/ads repair", "test")!;
        run.Automation.CancelPendingOperations("stopped before framework callback");
        run.Pump();
        await pending;
        Assert.Empty(run.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopBeforeInitializationCallbackCannotIssueStartupCommands(bool databaseLoaded)
    {
        using var run = new Run();
        run.XaDatabaseLoaded = databaseLoaded;
        run.Engine.Start();
        Assert.Equal(EngineState.Initializing, run.Engine.CurrentState);
        var pending = (Task)Get(run.Engine, "startupTask")!;
        run.Engine.Stop();
        var commandsAfterStop = run.Commands.ToArray();
        run.Pump();
        await pending;
        Assert.Equal(commandsAfterStop, run.Commands);
        Assert.False(run.Engine.IsRunning);
        Assert.Equal("Manual Stop.", run.Engine.LastStopReason);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("command")]
    [InlineData("dad")]
    public async Task OrdinaryStartsWarnWithoutDatabaseAndRetainStopOwnership(string source)
    {
        using var run = new Run();
        run.XaDatabaseLoaded = false;
        var warning = MOGTOME.Localization.Ui.GameT("Plugin_XaDatabaseFarmingWarning");
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin, "<Engine>k__BackingField", run.Engine);
        Set(plugin, "<BossModIPC>k__BackingField", run.BossMod);
        Set(plugin, "<ConfigManager>k__BackingField", run.Manager);
        Set(plugin, "<State>k__BackingField", run.State);
        var appearance = RuntimeHelpers.GetUninitializedObject(typeof(MOGTOME.UiDesign.MogtomeAppearance));
        Set(appearance, "plugin", plugin);
        Set(plugin, "<Appearance>k__BackingField", appearance);
        if (source == "dad")
        {
            var dad = (MogtomeDadIpcService)RuntimeHelpers.GetUninitializedObject(typeof(MogtomeDadIpcService));
            Set(dad, "plugin", plugin);
            Set(dad, "status", new MogtomeDadRunStatus());
            var json = (string)typeof(MogtomeDadIpcService).GetMethod("StartRun", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dad, [JsonSerializer.Serialize(new MogtomeDadRunRequest { DadRunId = "synthetic-farming", Role = "QueueLeader" })])!;
            using var result = JsonDocument.Parse(json);
            Assert.True(result.RootElement.GetProperty("accepted").GetBoolean());
            Assert.True(result.RootElement.GetProperty("ready").GetBoolean());
            Assert.True(result.RootElement.GetProperty("dadOwned").GetBoolean());
        }
        else
        {
            if (source == "command")
                typeof(Plugin).GetMethod("OnAliasCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, ["/mog", "start"]);
            else
                new MainWindow(plugin).TitleBarButtons.Single(button => button.Icon == FontAwesomeIcon.Play).Click(ImGuiMouseButton.Left);
            Assert.True(plugin.IsEngineStartQueued);
            Assert.DoesNotContain(warning, run.ChatMessages);
            Invoke(plugin, "TryConsumeQueuedEngineStart");
            Assert.False(plugin.IsEngineStartQueued);
        }
        Assert.Equal(EngineState.Initializing, run.Engine.CurrentState);
        Assert.Single(run.ChatMessages, message => message == warning);
        Assert.Equal("XA Database", GameHelpers.MissingXaDependencies()); // Shops still require it.
        var pending = (Task)Get(run.Engine, "startupTask")!;
        run.Engine.Start(); // Duplicate requests must not repeat the warning.
        Assert.Single(run.ChatMessages, message => message == warning);
        run.Engine.Stop();
        var afterStop = run.Commands.ToArray();
        run.Pump();
        await pending;
        Assert.Equal(afterStop, run.Commands);
        Assert.False(run.Engine.IsRunning);
    }

    [Fact]
    public void StopClearsQueuedAndDeferredStartEvenBeforeEngineExists()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin, "pendingEngineStartRequest", true);
        Set(plugin, "deferredSharedConfigStartRequest", true);
        Assert.True(plugin.StopEngine());
        Assert.False(plugin.IsEngineStartQueued);
        Invoke(plugin, "TryConsumeQueuedEngineStart");
        Assert.Null(plugin.Engine);
    }

    [Fact]
    public void CombatCleanupAttemptsEveryComponentAndKeepsActivationTerminalOnFailure()
    {
        using var run = new Run();
        var components = (HashSet<CombatProvider>)Get(run.Rotation, "enabledComponents")!;
        components.UnionWith([CombatProvider.Bmr, CombatProvider.Rsr, CombatProvider.Wrath]);
        run.ThrowOnCommand = "/bmrai off";
        Assert.False(run.Rotation.DisableRotationForDutyEnd("test failure"));
        Assert.Contains("/rotation cancel", run.Commands);
        Assert.Contains("/wrath auto off", run.Commands);
        Assert.Equal(CombatProvider.Bmr, Assert.Single(components));
        run.Rotation.InvalidateCombatActivation();
        Assert.False(run.Rotation.EnableRotation());
        run.ThrowOnCommand = null;
        Assert.True(run.Rotation.DisableRotationForDutyEnd("retry cleanup"));
        Assert.Empty(components);
    }

    [Fact]
    public void InitializationFailureRetainsSpecificCauseAfterCleanup()
    {
        using var run = new Run();
        Set(run.Engine, "<CurrentState>k__BackingField", EngineState.Initializing);
        Invoke(run.Engine, "StopWithCombatFailure", "ADS is not loaded");
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("ADS is not loaded", run.Engine.LastStopReason);
        Assert.Contains(run.Engine.LastStopReason, run.Engine.StatusMessage);
    }

    [Theory]
    [InlineData(19u, false, "FRENRIDER - TANK")]
    [InlineData(43u, false, "FRENRIDER - MELEE")]
    [InlineData(31u, false, "FRENRIDER - RANGED")]
    [InlineData(19u, true, "passive - tank")]
    [InlineData(43u, true, "passive - melee")]
    [InlineData(31u, true, "passive - ranged")]
    public void ActiveAndRsrPassivePresetMappingsRemainAvailable(uint job, bool passive, string expected)
        => Assert.Equal(expected, BossModIPC.SelectPresetForJob(job, passive));

    [Theory]
    [InlineData(0u, 0u, true, false, false)]
    [InlineData(1048u, 830u, true, false, false)]
    [InlineData(1044u, 830u, true, false, false)]
    [InlineData(1044u, 16u, false, false, false)]
    [InlineData(1044u, 16u, true, true, false)]
    [InlineData(1044u, 16u, true, false, true)]
    public void UncertainDutyLogoutCutscenesAndOccupiedStateBlockExit(uint territory, uint cfc, bool loggedIn, bool cutscene, bool occupied)
        => Assert.NotNull(AdsIntegrationPolicy.GetDutyLeaveBlocker(1044, true, (territory, cfc),
            Ready with { IsLoggedIn = loggedIn, IsWatchingCutscene78 = cutscene }, false, occupied));

    [Fact]
    public async Task NpcDestinationsPreserveSavedValuesAndWaitForAdsTerminalResult()
    {
        var destinations = new[] { "uldah", "gridania", "limsa", "ishgard", "crystarium", "sharlayan", "tuliyollal" };
        Assert.Equal(0, (int)AdsRepairMode.Npc);
        Assert.Equal(1, (int)AdsRepairMode.Self);
        Assert.Equal(2, (int)AdsRepairMode.NpcYesInn);
        using var run = new Run();
        for (var index = 2; index <= 17; index++)
        {
            var mode = (AdsRepairMode)index;
            var noInn = index >= 10;
            var first = noInn ? 10 : 2;
            var suffix = index == first ? "" : " " + destinations[index - first - 1];
            Assert.Equal("/ads npcrepair " + (noInn ? "noinn" : "yesinn") + suffix, RepairService.GetAdsRepairCommand(mode));
            foreach (var language in Enum.GetValues<MOGTOME.Localization.UiLanguage>())
            {
                var original = MOGTOME.Localization.Ui.Language;
                try
                {
                    MOGTOME.Localization.Ui.SetLanguage(language);
                    var key = RepairService.GetAdsRepairLabelKey(mode);
                    Assert.NotEqual(key, MOGTOME.Localization.Ui.T(key));
                    if (index != first) Assert.Contains(" — ", RepairService.GetAdsRepairLabel(mode));
                }
                finally { MOGTOME.Localization.Ui.SetLanguage(original); }
            }
            run.Config.AdsRepairMode = mode;
            run.RepairStatus = "{\"utilityRunning\":true}";
            run.Automation.RequestNpcRepair();
            Assert.True(run.Automation.IsAdsInnRepairPending(out var failure));
            Assert.Empty(failure);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!run.Automation.IsAdsRepairWaitingForCompletion && DateTime.UtcNow < deadline)
            {
                run.Pump();
                await Task.Delay(10);
            }
            Assert.True(run.Automation.IsAdsRepairWaitingForCompletion);
            Assert.Equal((noInn ? "npc-no-inn" : "npc-yes-inn") + (index == first ? "" : "-" + destinations[index - first - 1]), run.RepairModes[^1]);
            Assert.True(run.Automation.IsAdsInnRepairPending(out failure));
            Assert.Empty(failure);
            Assert.DoesNotContain(run.Commands, command => command.StartsWith("/ads npcrepair"));

            string Terminal(DateTime time, string success, string error, string? utilityMode = null) => System.Text.Json.JsonSerializer.Serialize(new
            { utilityRunning = false, utilityCompletedAtUtc = time, utilityLastSuccess = success, utilityLastFailure = error,
                utilityMode = utilityMode ?? run.RepairModes[^1] });
            run.RepairStatus = Terminal(DateTime.UtcNow.AddMinutes(-5), "old entry", "");
            Assert.True(run.Automation.IsAdsInnRepairPending(out _));
            run.RepairStatus = Terminal(DateTime.UtcNow, "unrelated utility", "", "self");
            Assert.True(run.Automation.IsAdsInnRepairPending(out _));
            run.RepairStatus = Terminal(DateTime.UtcNow, "confirmed room entry", "");
            Assert.False(run.Automation.IsAdsInnRepairPending(out failure));
            Assert.Empty(failure);
            run.RepairStatus = Terminal(DateTime.UtcNow, "", "entry failed");
            Assert.False(run.Automation.IsAdsInnRepairPending(out failure));
            Assert.Equal("entry failed", failure);
            run.Automation.CancelPendingOperations("test complete");
        }
        run.Config.AdsRepairMode = (AdsRepairMode)999;
        var count = run.RepairModes.Count;
        run.Automation.RequestNpcRepair();
        Assert.False(run.Automation.IsAdsInnRepairPending(out var unsupported));
        Assert.Contains("Unsupported", unsupported);
        Assert.Equal(count, run.RepairModes.Count);
        run.Automation.CancelPendingOperations("test complete");
        run.Config.AdsRepairMode = AdsRepairMode.NpcYesInnUldah;
        run.AcceptRepair = false;
        run.Automation.RequestNpcRepair();
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!run.Automation.IsAdsRepairWaitingForCompletion && DateTime.UtcNow < end)
        {
            run.Pump();
            await Task.Delay(10);
        }
        Assert.False(run.Automation.IsAdsInnRepairPending(out var rejected));
        Assert.Contains("did not accept", rejected);

        run.Automation.CancelPendingOperations("old ADS capability check");
        run.AcceptRepair = true;
        run.RepairCapabilities = "{\"schemaVersion\":1}";
        run.Config.AdsRepairMode = AdsRepairMode.NpcNoInnGridania;
        count = run.RepairModes.Count;
        run.Automation.RequestNpcRepair();
        end = DateTime.UtcNow.AddSeconds(10);
        while (!run.Automation.IsAdsRepairWaitingForCompletion && DateTime.UtcNow < end)
        {
            run.Pump();
            await Task.Delay(10);
        }
        Assert.False(run.Automation.IsAdsInnRepairPending(out var incompatible));
        Assert.Contains("does not support", incompatible);
        Assert.Equal(count, run.RepairModes.Count);

        // Existing no-inn selection works with an older ADS that has no additive mode list.
        run.Automation.CancelPendingOperations("legacy no-inn capability check");
        run.Config.AdsRepairMode = AdsRepairMode.NpcNoInn;
        run.Automation.RequestNpcRepair();
        end = DateTime.UtcNow.AddSeconds(10);
        while (!run.Automation.IsAdsRepairWaitingForCompletion && DateTime.UtcNow < end)
        {
            run.Pump();
            await Task.Delay(10);
        }
        Assert.Equal("npc-no-inn", run.RepairModes[^1]);
        run.Automation.ReturnToInnIfNeeded();
        Assert.DoesNotContain("/ads enterinn", run.Commands);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModCatalogUsesTheCompleteLiveDatabaseAndLiteralNativeOrder(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Next");
        native.Presets.AllPresets = [new("null"), new("Literal##name###suffix"), new("none"), new("Next"), new("VBM Multibox", true)];
        native.Presets.DefaultPresets = [.. native.Presets.AllPresets, new("Hidden original", true)];
        using var service = native.CreateService();
        var catalog = service.ReadPresetCatalog(provider);
        Assert.True(catalog.Readable);
        var expected = new[] { "null", "Literal##name###suffix", "none", "Next" };
        Assert.Equal(provider == CombatProvider.Vbm ? expected : expected.Append("VBM Multibox"), catalog.DisplayedNames);
        Assert.Contains("Hidden original", catalog.DefinitionNames);
        Assert.Equal("Hidden original", BossModIPC.ResolvePresetSelection("Hidden original", catalog));
        Assert.Equal("null", BossModIPC.ResolvePresetSelection("Deleted", catalog));
        Assert.Equal("", BossModIPC.ResolvePresetSelection("", catalog));
        Assert.Equal("none", BossModIPC.ResolvePresetSelection("none", catalog));
        Assert.Empty(native.Writes);
    }

    [Fact]
    public void BossModCatalogRetainsUnavailableEmptyHiddenAndDuplicateSelections()
    {
        var unavailable = BossModPresetCatalog.Unavailable("BMR", "unavailable");
        var empty = BossModIPC.ReadPresetCatalog("VBM", Array.Empty<NativePreset>(), Array.Empty<NativePreset>(), Array.Empty<NativePreset>());
        Assert.Equal("Saved", BossModIPC.ResolvePresetSelection("Saved", unavailable));
        Assert.Equal("Saved", BossModIPC.ResolvePresetSelection("Saved", empty));
        var definitions = new[] { new NativePreset("First"), new NativePreset("first"), new NativePreset(" First "), new NativePreset("Hidden", true) };
        var catalog = BossModIPC.ReadPresetCatalog("BMR", definitions.Take(3), definitions, Array.Empty<NativePreset>());
        Assert.Equal(new[] { "First" }, catalog.DisplayedNames);
        Assert.Equal("Hidden", BossModIPC.ResolvePresetSelection("Hidden", catalog));
        Assert.Equal("first", BossModIPC.ResolvePresetSelection("first", catalog));
        Assert.Equal("First", BossModIPC.ResolvePresetSelection("Deleted", catalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BossModUnavailableOrAmbiguousProviderNeverSuppliesAReplacement(bool ambiguous)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { Ambiguous = ambiguous, Loaded = ambiguous };
        using var service = native.CreateService();
        var catalog = service.ReadPresetCatalog(CombatProvider.Bmr);
        Assert.False(catalog.Readable);
        Assert.Equal("Deleted", BossModIPC.ResolvePresetSelection("Deleted", catalog));
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr, "", false)]
    [InlineData(CombatProvider.Bmr, "Original", false)]
    [InlineData(CombatProvider.Bmr, "", true)]
    [InlineData(CombatProvider.Vbm, "", false)]
    [InlineData(CombatProvider.Vbm, "Original|Other", false)]
    [InlineData(CombatProvider.Vbm, "", true)]
    public void BossModLiteralWritesRestoreTheFirstCompleteRuntimeBaseline(CombatProvider provider, string names, bool disabled)
    {
        var native = new NativeBossModProvider(provider, names, disabled);
        using var service = native.CreateService();
        var config = new Configuration();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(new[] { "null" }, native.Active);
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("none"));
        Assert.True(service.ApplyOwnedBossModPreset("Literal##name###suffix"));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(names.Length == 0 ? Array.Empty<string>() : names.Split('|'), native.Active);
        Assert.Equal(disabled, native.ForceDisabled);
        Assert.Equal("Original", native.Selector);
        var writes = native.Writes.ToArray();
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(writes, native.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Missing")]
    public void BossModUnsupportedBmrOriginalSelectorNeverAuthorizesAnOverride(string? original)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { Selector = original };
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(original, native.Selector);
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Original", false)]
    [InlineData("", true)]
    [InlineData("Original", true)]
    public void BossModUnsetBmrSelectorStartsAndRestoresTheOriginalSelection(string originalRuntime, bool callbackThrows)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, originalRuntime)
            { Selector = null, ThrowAfterSelectorWrite = callbackThrows };
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 1.5));

        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());

        Assert.Null(native.Selector);
        Assert.Equal(originalRuntime.Length == 0 ? Array.Empty<string>() : new[] { originalRuntime }, native.Active);
        Assert.Equal(7.5, native.Distance);
        Assert.False(native.ForceDisabled);
        Assert.Contains("BossMod.AI.SetPreset ", native.Writes);
    }

    [Fact]
    public void BossModUnsetSelectorDoesNotClaimAClearValueThatMatchesANativePreset()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { Selector = null };
        native.Presets.AllPresets.Add(new NativePreset(" "));
        native.Presets.UserPresets.Add(native.Presets.AllPresets[^1]);
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));

        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Null(native.Selector);
        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr, "BossMod.Presets.GetForceDisabled")]
    [InlineData(CombatProvider.Vbm, "BossMod.Presets.GetActiveList")]
    public void BossModUnreadableOriginalRuntimeNeverAuthorizesPresetMutation(CombatProvider provider, string channel)
    {
        var native = new NativeBossModProvider(provider, "Original") { UnavailableChannel = channel };
        using var service = native.CreateService();
        Assert.False(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal("Original", native.Selector);
        Assert.Empty(native.Writes);
        native.UnavailableChannel = null;
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "Original" }, native.Active);
    }

    [Theory]
    [InlineData("GodMod", false, "GodMod")]
    [InlineData("GodMod,BossModReborn", true, "GodMod")]
    [InlineData("GodMod,BossMod", false, "GodMod")]
    [InlineData("BossModReborn,BossMod", true, "BossModReborn")]
    [InlineData("BossModReborn", false, null)]
    [InlineData("BossMod", true, null)]
    public void StartupSelectsOnlyGodModOrTheGmcBmrRoute(string available, bool gmc, string? expected)
        => Assert.Equal(expected, BossModIPC.SelectAutomationHost(available.Split(','), gmc));

    [Theory]
    [InlineData("GodMod", false, false, true, true)]
    [InlineData("GodMod", true, false, true, true)]
    [InlineData("GodMod", false, false, false, false)]
    [InlineData("BossModReborn", true, true, true, true)]
    [InlineData("BossModReborn", true, true, false, false)]
    [InlineData("BossModReborn", true, false, true, false)]
    [InlineData("BossModReborn", false, true, true, false)]
    public void AutomationRequiresTheReadyProviderRoute(string host, bool gmc, bool suiteReady, bool hostReady, bool expected)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: host) { Ready = hostReady };
        using var run = new Run(native) { GmcLoaded = gmc, GmcSuiteReady = suiteReady };
        Assert.Equal(expected, run.BossMod.HasUsableAutomationHost());
        if (!expected)
        {
            run.Engine.Start();
            Assert.False(run.Engine.IsRunning);
            Assert.Equal("Plugin_RequiredXaPlugins", run.Engine.StopReason?.Key);
            Assert.Empty(native.Writes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GodModRequiresAllExactSuitePresetNamesBeforeStarting(bool oldBuild)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
        using var run = new Run(native);
        Assert.True(run.BossMod.HasUsableAutomationHost());
        if (oldBuild)
            native.Presets.AllPresets.RemoveAll(preset => preset.Name.StartsWith("FRENRIDER - ", StringComparison.Ordinal)
                || preset.Name.StartsWith("passive - ", StringComparison.Ordinal));
        else
        {
            native.Presets.AllPresets.RemoveAll(preset => preset.Name == "FRENRIDER - RANGED");
            native.Presets.AllPresets.Add(new NativePreset("frenrider - ranged"));
        }

        Assert.False(run.BossMod.HasUsableAutomationHost());
        run.Engine.Start();
        Assert.False(run.Engine.IsRunning);
        Assert.Equal("Plugin_RequiredXaPlugins", run.Engine.StopReason.Key);
        Assert.Contains("GodMod with suite presets", Assert.IsType<string>(run.Engine.StopReason.Arguments[0]));
        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData("GodMod")]
    [InlineData("BossModReborn")]
    public void ActiveEngineStopsWhenItsProviderRouteDeparts(string host)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: host);
        using var run = new Run(native) { GmcLoaded = host == "BossModReborn" };
        run.Enter(30);
        Assert.True(run.BossMod.HasUsableAutomationHost());
        if (host == "GodMod") native.Loaded = false;
        else run.GmcSuiteReady = false;
        run.Engine.Update();
        Assert.False(run.Engine.IsRunning);
        Assert.Equal("Plugin_RequiredXaPlugins", run.Engine.StopReason?.Key);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr, false)]
    [InlineData(CombatProvider.Vbm, false)]
    [InlineData(CombatProvider.Bmr, true)]
    public void GodModUsesCompatibleIpcAndOnlyItsOwnCommandsWithoutChangingSavedProviders(CombatProvider saved, bool gmc)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod") { AiEnabled = gmc };
        using var run = new Run(native) { GmcLoaded = gmc, Territory = 1044 };
        run.Config.CombatProvider = saved;
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.Equal("GodMod", run.BossMod.NativeHost);
        Assert.False(run.Rotation.SupportsBmrObstacleMaps);
        Assert.False(run.BossMod.SendCommand("/bmrai obstaclemaps on", "unsupported GM command"));
        Assert.True(run.Rotation.EnableRotationOncePerDuty("GM compatible host"));
        Assert.Equal(saved, run.Config.CombatProvider);
        Assert.Equal(new[] { "null" }, native.Active);
        Assert.Contains("/gmai on", run.Commands);
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/bmrai", StringComparison.Ordinal)
            || command.StartsWith("/vbmai", StringComparison.Ordinal));
        Assert.True(run.Rotation.DisableRotationForDutyEnd("GM cleanup"));
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("GodMod.", StringComparison.Ordinal)
            || write.Contains("ActiveList", StringComparison.Ordinal));
    }

    [Fact]
    public void GodModAsynchronousReadinessNeverCapturesAnUnreadableFirstBaseline()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod") { Ready = false };
        using var service = native.CreateService();
        Assert.False(service.ResolveReadyHost(CombatProvider.Vbm, out _));
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.Empty(native.Writes);
        native.Ready = true;
        native.UnavailableChannel = "BossMod.Presets.GetForceDisabled";
        Assert.False(service.ResolveReadyHost(CombatProvider.Bmr, out _));
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        native.UnavailableChannel = null;
        native.Active = ["External"];
        native.Selector = "External";
        Assert.True(service.ResolveReadyHost(CombatProvider.Vbm, out var provider));
        Assert.Equal(CombatProvider.Bmr, provider);
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Equal("External", native.Selector);
    }

    [Theory]
    [InlineData("BossModReborn")]
    [InlineData("BossMod")]
    public void GodModSharedIpcAmbiguityNeverAuthorizesPresetWrites(string competing)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod")
            { AdditionalHost = competing };
        using var service = native.CreateService();
        Assert.False(service.ResolveReadyHost(CombatProvider.Bmr, out _));
        Assert.False(service.ReadPresetCatalog(CombatProvider.Bmr).Readable);
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.Empty(native.Writes);
    }

    [Fact]
    public void GodModReadyHostReplacementCannotBeAdoptedByAPendingStart()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
        using var service = native.CreateService();
        Assert.True(service.ResolveReadyHost(CombatProvider.Bmr, out _));
        native.Reload();
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.Empty(native.Writes);
        Assert.True(service.ResolveReadyHost(CombatProvider.Bmr, out _));
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
    }

    [Fact]
    public void GodModCommandFailureNeverRetriesTheBmrPrefix()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        run.ThrowOnCommand = "/gmai on";
        Assert.False(run.Rotation.EnableRotationOncePerDuty("rejected GM command"));
        Assert.Single(run.Commands.Where(command => command == "/gmai on"));
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/bmrai", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("success", true)]
    [InlineData("ai-off", false)]
    [InlineData("preset", false)]
    [InlineData("stop", false)]
    [InlineData("settings", false)]
    [InlineData("session", false)]
    [InlineData("host", false)]
    public void GodModDeathReceiptSurvivesHpZeroBeforeNativeClearAndRetainsInvalidators(string change, bool clearBeforeObservation)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        var (startup, _) = run.BindAdsStartup(() => false);
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DutyStartupResult.Pending, startup.Update(true, (1044, 16), Ready, now));
        Assert.Equal(DutyStartupResult.Confirmed, startup.Update(true, (1044, 16), Ready, now.AddSeconds(2)));
        if (clearBeforeObservation) native.Die();
        var hpZero = Ready with { IsPlayerAlive = false, IsUnconscious = true };
        startup.ObserveReadiness(true, (1044, 16), hpZero, now.AddSeconds(3));
        startup.ObserveReadiness(true, (1044, 16), hpZero, now.AddSeconds(3.1));
        run.Rotation.ObserveSessionDeparture();
        native.Writes.Clear();
        run.Commands.Clear();
        if (!clearBeforeObservation)
        {
            Assert.False(native.Rotation.Player.IsDead); // Consumer HP0 precedes native actor processing.
            Assert.False(run.Rotation.EnableRotationOncePerDuty("native death clear still pending"));
            Assert.Empty(native.Writes);
            Assert.Empty(run.Commands);
        }
        switch (change)
        {
            case "ai-off": native.AiEnabled = false; break;
            case "preset": native.Active = ["External"]; break;
            case "stop": Assert.True(run.Rotation.DisableRotationForDutyEnd("Stop while native death is pending")); break;
            case "settings": run.Config.ManualBossModPresetName = "External"; break;
            case "session": run.CurrentContentId = 2; break;
            case "host": native.Reload(); break;
        }
        run.Rotation.ObserveSessionDeparture();
        if (!clearBeforeObservation) native.Die();
        Assert.True(native.ForceDisabled);
        Assert.Empty(native.Active);
        run.Rotation.ObserveSessionDeparture();
        native.Rotation.Player.IsDead = false;
        native.Writes.Clear();
        run.Commands.Clear();
        Assert.Equal(change == "success", run.Rotation.EnableRotationOncePerDuty("ready after delayed native clear"));
        if (change == "success")
        {
            Assert.Equal(new[] { "null" }, native.Active);
            Assert.Single(native.Writes.Where(write => write == "BossMod.Presets.SetActive null"));
            Assert.True(run.Rotation.EnableRotationOncePerDuty("duplicate ready after delayed clear"));
            Assert.Single(native.Writes.Where(write => write == "BossMod.Presets.SetActive null"));
        }
        else
        {
            Assert.False(run.Rotation.EnableRotationOncePerDuty("invalid death receipt remains terminal"));
            Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.SetActive", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/bmrai", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("before-force")]
    [InlineData("before-none")]
    [InlineData("before-ai-off")]
    [InlineData("after-ai-off")]
    [InlineData("after-none")]
    [InlineData("after-preset")]
    [InlineData("settings")]
    [InlineData("native-settings")]
    [InlineData("native-death-setting")]
    [InlineData("provider")]
    [InlineData("host")]
    [InlineData("stop")]
    public void GodModNativeDeathRearmsOnlyThePreviouslyOwnedActiveRuntime(string change)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotationOncePerDuty("before native death"));
        if (change.StartsWith("before-", StringComparison.Ordinal))
        {
            if (change == "before-ai-off") native.AiEnabled = false;
            else { native.Active.Clear(); native.ForceDisabled = change == "before-force"; }
            run.Rotation.ObserveSessionDeparture();
        }
        native.Die();
        Assert.True(native.ForceDisabled);
        Assert.Empty(native.Active);
        run.Rotation.InvalidateCombatActivation();
        native.Rotation.Player.IsDead = false;
        switch (change)
        {
            case "after-ai-off": native.AiEnabled = false; break;
            case "after-none": native.ForceDisabled = false; break;
            case "after-preset": native.ForceDisabled = false; native.Active = ["External"]; break;
            case "settings": run.Config.ManualBossModPresetName = "External"; break;
            case "native-settings": native.ManualTarget = true; break;
            case "native-death-setting": native.ClearPresetOnDeath = false; break;
            case "provider": run.Config.CombatProvider = CombatProvider.Vbm; break;
            case "host": native.Reload(); break;
            case "stop": Assert.True(run.Rotation.DisableRotationForDutyEnd("Stop after death")); break;
        }
        native.Writes.Clear();
        run.Commands.Clear();
        Assert.Equal(change == "success", run.Rotation.EnableRotationOncePerDuty("ready after native death"));
        if (change == "success")
        {
            Assert.False(native.ForceDisabled);
            Assert.Equal(new[] { "null" }, native.Active);
            Assert.Single(native.Writes.Where(write => write == "BossMod.Presets.SetActive null"));
            Assert.True(run.Rotation.EnableRotationOncePerDuty("duplicate readiness"));
            Assert.Single(native.Writes.Where(write => write == "BossMod.Presets.SetActive null"));
        }
        else
        {
            Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.SetActive", StringComparison.Ordinal));
            Assert.False(run.Rotation.EnableRotationOncePerDuty("later readiness cannot reclaim external off"));
            Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.SetActive", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/bmrai", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModRetainedCaseAliasCannotBeSilentlyWrittenAsAnotherLiteralName(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var service = native.CreateService();
        Assert.Equal("next", BossModIPC.ResolvePresetSelection("next", service.ReadPresetCatalog(provider)));
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("next"));
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Empty(native.Writes);
    }

    [Theory]
    [InlineData("FRENRIDER - MELEE", "frenrider - melee")]
    [InlineData("frenrider - melee", "FRENRIDER - MELEE")]
    public void GodModManualPresetKeepsBothExactSuiteNamesAndRestoresTheOriginal(string original, string selected)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, original, internalName: "GodMod");
        native.Presets.UserPresets.Add(new NativePreset("frenrider - melee"));
        native.Presets.AllPresets.Add(native.Presets.UserPresets.Single());
        native.Presets.AllPresets.Add(new NativePreset("other"));
        native.Selector = original;
        native.Rotation.Preset = native.Presets.AllPresets.Single(preset => preset.Name == original);

        using var service = native.CreateService();
        var catalog = service.ReadPresetCatalog(CombatProvider.Bmr);
        Assert.Contains(original, catalog.DisplayedNames);
        Assert.Contains(selected, catalog.DisplayedNames);
        Assert.DoesNotContain("other", catalog.DisplayedNames);
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset(selected));
        Assert.Equal(selected, native.Selector);
        Assert.Equal(new[] { selected }, native.Active);
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(original, native.Selector);
        Assert.Equal(new[] { original }, native.Active);
    }

    [Fact]
    public void BossModRejectedRuntimeWriteStillRestoresTheConfirmedPartialSelector()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { RejectRuntimeWrite = true };
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal("null", native.Selector);
        Assert.Equal(new[] { "Original" }, native.Active);
        native.RejectRuntimeWrite = false;
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal("Original", native.Selector);
        Assert.Equal(new[] { "Original" }, native.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BossModOnlyReadbackConfirmsPartialAndRejectedNativeWrites(bool selectorCallbackThrows)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original")
            { RejectSelectorWrite = true, ThrowAfterSelectorWrite = selectorCallbackThrows };
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.SetActive", StringComparison.Ordinal));
        native.RejectSelectorWrite = false;
        native.ThrowAfterRuntimeWrite = true;
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(new[] { "null" }, native.Active);
        native.ThrowAfterRuntimeWrite = false;
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal("Original", native.Selector);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void BossModPreferredDistanceUsesNativeCultureReadbackAndInvariantCommands(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
            using var service = native.CreateService();
            Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
            Assert.False(service.ApplyOwnedPreferredDistance(_ => true, 1.5));
            Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 1.5));
            Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 2.5));
            Assert.Contains("/bmrai prefdistance 1.5", native.Writes);
            Assert.True(service.BeginOwnedBossModCleanup());
            Assert.True(service.ClearActivePreset());
            Assert.Equal(7.5, native.Distance);
            Assert.Contains("Configuration " + 7.5.ToString("R", CultureInfo.CurrentCulture), native.Writes);
            Assert.False(BossModIPC.TryParsePreferredDistance(["NaN"], CultureInfo.CurrentCulture, out _));
            Assert.False(BossModIPC.TryParsePreferredDistance(["PreferredDistance = 1.5"], CultureInfo.CurrentCulture, out _));
            Assert.False(BossModIPC.TryParsePreferredDistance(["1.5", "extra"], CultureInfo.CurrentCulture, out _));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void BossModCleanupPreservesExternalFieldsAcrossItsOwnBmrOffEffect()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 1.5));
        native.UnavailableChannel = "BossMod.Configuration";
        Assert.False(service.BeginOwnedBossModCleanup());
        Assert.False(service.ClearActivePreset());
        native.UnavailableChannel = null;
        native.Active = ["External"];
        native.Selector = "External";
        native.Distance = 9.5;
        native.AiEnabled = true;
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.SendCommand("/bmrai off", "test off"));
        Assert.Empty(native.Active);
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Equal("External", native.Selector);
        Assert.Equal(9.5, native.Distance);
        Assert.False(native.AiEnabled);
    }

    [Theory]
    [InlineData("Original")]
    [InlineData(null)]
    public void BossModSelectorCleanupCallbackPreservesAnIndependentRuntimeAcrossItsOwnOffEffect(string? originalSelector)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { Selector = originalSelector };
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        native.AfterSelectorWrite = () => native.Active = ["External"];

        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.SendCommand("/bmrai off", "cleanup after selector callback"));
        Assert.Empty(native.Active);
        Assert.True(service.ClearActivePreset());

        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Equal(originalSelector, native.Selector);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BossModSelectorActivationCallbackPreservesAnIndependentRuntime(bool callbackThrows)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original")
            { ThrowAfterSelectorWrite = callbackThrows };
        native.AfterSelectorWrite = () => native.Active = ["External"];
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));

        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Equal("null", native.Selector);
        native.AfterSelectorWrite = null;

        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());

        Assert.Equal(new[] { "External" }, native.Active);
        Assert.Equal("Original", native.Selector);
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.Set", StringComparison.Ordinal));
    }

    [Fact]
    public void BossModDelayedVbmMultiboxEffectsKeepTheOriginalPresetOrder()
    {
        var native = new NativeBossModProvider(CombatProvider.Vbm, "Original|Other");
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Vbm, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.SendCommand("/vbmai on", "test on"));
        native.Active.Add("VBM Multibox");
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.IsOwnedProviderCurrent);
        Assert.True(service.SendCommand("/vbmai off", "test off"));
        native.Active.Remove("VBM Multibox");
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "Original", "Other" }, native.Active);
        Assert.False(native.AiEnabled);
    }

    [Fact]
    public void BossModUnconfirmedAiEffectDoesNotOwnAnUnexpectedRuntime()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        native.RejectAiEffect = true;
        Assert.True(service.SendCommand("/bmrai on", "rejected native effect"));
        native.Active.Clear();
        native.Writes.Clear();
        Assert.False(service.ApplyOwnedBossModPreset("Next"));
        Assert.Empty(native.Writes);
        Assert.True(service.BeginOwnedBossModCleanup());
        native.Active = ["External"];
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "External" }, native.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BossModUnreadableCurrentStateReportsIncompleteCleanupWithoutInventingAnOriginal(bool externalEdit)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var service = native.CreateService();
        var config = new Configuration();
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", config));
        Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 1.5));
        native.UnavailableChannel = "BossMod.Configuration";
        native.Writes.Clear();
        Assert.False(service.BeginOwnedBossModCleanup());
        Assert.False(service.ClearActivePreset());
        Assert.Equal(1.5, native.Distance);
        Assert.Empty(native.Writes);
        Assert.Contains("unreadable", service.LastSettingsStatus);
        Assert.False(service.ClearActivePreset());
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", config));
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "other", "character", new Configuration()));
        Assert.False(service.BeginOwnedBossModCleanup("other", "character"));
        Assert.False(service.ClearActivePreset());
        Assert.Empty(native.Writes);
        Assert.Equal(1.5, native.Distance);
        native.UnavailableChannel = null;
        if (externalEdit) native.Distance = 9.5;
        Assert.False(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", config));
        Assert.False(service.ClearActivePreset());
        Assert.Empty(native.Writes);
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(externalEdit ? 9.5 : 7.5, native.Distance);
        Assert.True(service.BeginOwnedBossModSettings(CombatProvider.Bmr, "account", "character", config));
        Assert.True(service.ApplyOwnedPreferredDistance(native.SendCommand, 2.5));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(externalEdit ? 9.5 : 7.5, native.Distance);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr, false)]
    [InlineData(CombatProvider.Bmr, true)]
    [InlineData(CombatProvider.Vbm, false)]
    [InlineData(CombatProvider.Vbm, true)]
    public void BossModIncompleteRuntimeCleanupRetainsItsBaselineUntilExplicitRecovery(CombatProvider provider, bool identityChanged)
    {
        var names = provider == CombatProvider.Vbm ? "Original|Other" : "Original";
        var native = new NativeBossModProvider(provider, names);
        using var service = native.CreateService();
        var config = new Configuration();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        native.RejectRuntimeWrite = true;
        if (identityChanged)
            Assert.False(service.BeginOwnedBossModSettings(provider, "other", "character", new Configuration()));
        else
        {
            Assert.True(service.BeginOwnedBossModCleanup());
            Assert.False(service.ClearActivePreset());
        }
        Assert.Equal(new[] { "null" }, native.Active);
        Assert.False(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.False(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        var writes = native.Writes.ToArray();
        native.RejectRuntimeWrite = false;
        Assert.False(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.False(service.ClearActivePreset());
        Assert.Equal(writes, native.Writes);
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(names.Split('|'), native.Active);
        Assert.Equal("Original", native.Selector);
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("Next"));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(names.Split('|'), native.Active);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModProviderReloadCannotRestoreOrStopItsReplacement(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var service = native.CreateService();
        var config = new Configuration();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        native.Reload();
        native.Active = ["Reloaded"];
        native.Selector = "Reloaded";
        native.Writes.Clear();
        Assert.False(service.IsOwnedProviderCurrent);
        Assert.False(service.SendCommand(provider == CombatProvider.Bmr ? "/bmrai off" : "/vbmai off", "departed provider"));
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.Empty(native.Writes);
        Assert.Contains(native.Warnings, warning => warning.Contains("reloaded", StringComparison.Ordinal));
        var warnings = native.Warnings.ToArray();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.Equal(warnings, native.Warnings);
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Empty(native.Writes);
        Assert.Equal(new[] { "Reloaded" }, native.Active);
        Assert.Equal("Reloaded", native.Selector);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModExplicitInitializeRetiresAProvenDepartedProviderBeforeTheNextFreshStart(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        native.Reload();
        var replacement = provider == CombatProvider.Bmr ? new[] { "Reloaded" } : new[] { "Reloaded", "Other" };
        native.Active = replacement.ToList();
        native.Selector = "Reloaded";
        native.Writes.Clear();
        run.Rotation.ObserveSessionDeparture();

        Assert.False(run.Rotation.Initialize());
        Assert.False(run.Rotation.EnableRotation());
        Assert.Empty(native.Writes);
        Assert.Equal(replacement, native.Active);
        Assert.Equal("Reloaded", native.Selector);
        Assert.Contains(native.Warnings, warning => warning.Contains("cleanup was incomplete", StringComparison.Ordinal));

        Assert.True(run.Rotation.Initialize());
        Assert.Equal(replacement, native.Active);
        Assert.DoesNotContain(provider == CombatProvider.Bmr ? "/bmrai off" : "/vbmai off", native.Writes);
        Assert.True(run.Rotation.EnableRotation());
        Assert.True(run.Rotation.DisableRotation());
        Assert.Equal(replacement, native.Active);
        Assert.Equal("Reloaded", native.Selector);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModRuntimeCleanupConfirmsAWriteThatMutatesBeforeThrowing(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        native.ThrowAfterRuntimeWrite = true;

        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal("Original", native.Selector);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModReadbackLossBetweenCleanupPhasesRetainsTheOriginalForExplicitRecovery(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var service = native.CreateService();
        var config = new Configuration();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.True(service.ApplyOwnedBossModPreset("null"));
        Assert.True(service.BeginOwnedBossModCleanup());
        native.UnavailableChannel = provider == CombatProvider.Bmr ? "BossMod.Presets.GetActive" : "BossMod.Presets.GetActiveList";

        Assert.False(service.ClearActivePreset());
        native.UnavailableChannel = null;
        Assert.False(service.BeginOwnedBossModSettings(provider, "account", "character", config));
        Assert.False(service.ApplyOwnedBossModPreset("Next"));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "Original" }, native.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BmrActivationOwnsAutomaticTargetingAndCleanupPreservesNewerManualChanges(bool externalManualChange)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { ManualTarget = true };
        using var run = new Run(native);
        run.Config.CombatProvider = CombatProvider.Bmr;
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(native.ManualTarget);

        Assert.True(run.Rotation.EnableRotation(), run.Rotation.Failure + " | " + run.BossMod.LastSettingsStatus);
        Assert.False(native.ManualTarget);
        Assert.Contains("/bmrai manualtarget off", native.Writes);
        Assert.Contains("/bmrai on", native.Writes);
        Assert.True(native.Writes.IndexOf("/bmrai manualtarget off") < native.Writes.IndexOf("/bmrai on"));
        native.Writes.Clear();
        if (externalManualChange) native.ManualTarget = true;
        Assert.True(run.Rotation.DisableRotationForDutyEnd("test targeting cleanup"));
        Assert.True(native.ManualTarget);
        Assert.Equal(externalManualChange ? 0 : 1,
            native.Writes.Count(write => write.StartsWith("Configuration ManualTarget ", StringComparison.Ordinal)));
        if (!externalManualChange) Assert.Contains("Configuration ManualTarget True", native.Writes);
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("/bmrai manualtarget ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModCommittedPresetChangesUseSettingsOnlyWithoutRestartingTheDuty(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original|Other");
        if (provider == CombatProvider.Bmr) native.Active = ["Original"];
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        run.Enter(45);
        Set(run.Engine, "autoDutyStartedInDuty", true);
        var startTime = run.State.DutyStartTime;
        native.Writes.Clear();
        run.Commands.Clear();
        run.Config.ManualBossModPresetName = "Next";
        run.Manager.NotifyConfigurationChanged();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "committed selection");
        Assert.Equal(new[] { "Next" }, native.Active);
        Assert.Empty(run.Commands);
        Assert.True((bool)Get(run.Engine, "autoDutyStartedInDuty")!);
        Assert.Equal(EngineState.InDuty, run.Engine.CurrentState);
        Assert.Equal(startTime, run.State.DutyStartTime);
        var writes = native.Writes.ToArray();
        run.Config.UiCompact = true;
        run.Manager.NotifyConfigurationChanged();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "unrelated settings");
        Assert.Equal(writes, native.Writes);
        Assert.True(run.Rotation.DisableRotationForDutyEnd("terminal stop"));
        Assert.Equal(provider == CombatProvider.Vbm ? new[] { "Original", "Other" } : new[] { "Original" }, native.Active);
        Assert.Equal(7.5, native.Distance);
        native.Writes.Clear();
        run.Commands.Clear();
        run.Config.ManualBossModPresetName = "null";
        run.Manager.NotifyConfigurationChanged();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "after stop");
        Assert.Empty(native.Writes);
        Assert.Empty(run.Commands);
        Assert.False(run.Rotation.EnableRotation());
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("backend")]
    [InlineData("death")]
    [InlineData("completed")]
    public void BossModLivePresetChangeWaitsForTheExistingEligibleDutyBoundary(string hold)
    {
        var native = new NativeBossModProvider(CombatProvider.Vbm, "Original|Other");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        native.Writes.Clear();
        run.Config.ManualBossModPresetName = "Next";
        run.Manager.NotifyConfigurationChanged();
        run.Rotation.UpdateDutyRotationHealth(hold == "outside" ? 129u : 1044u, hold != "backend", hold == "completed", hold == "death", "held settings");
        Assert.Empty(native.Writes);
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "hold released");
        Assert.Equal(new[] { "Next" }, native.Active);
    }

    [Theory]
    [InlineData(CombatProvider.Vbm, false)]
    [InlineData(CombatProvider.Vbm, true)]
    [InlineData(CombatProvider.Bmr, false)]
    [InlineData(CombatProvider.Bmr, true)]
    public void AdsInteractionHoldDefersOnlyVbmStartupAndDeathRecovery(CombatProvider provider, bool recovery)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        var held = false;
        var (startup, _) = run.BindAdsStartup(() => held);
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DutyStartupResult.Pending, startup.Update(true, (1044, 16), Ready, now));
        if (recovery)
        {
            Assert.Equal(DutyStartupResult.Confirmed, startup.Update(true, (1044, 16), Ready, now.AddSeconds(2)));
            if (provider == CombatProvider.Bmr)
            {
                native.Rotation.Preset = native.Presets.AllPresets.Single(preset => preset.Name == native.Selector);
                run.Rotation.ObserveSessionDeparture();
                native.Die();
            }
            startup.ObserveReadiness(true, (1044, 16), Ready with { IsPlayerAlive = false, IsUnconscious = true }, now.AddSeconds(3));
            if (provider == CombatProvider.Bmr) native.Rotation.Player.IsDead = false;
            else native.AiEnabled = false;
            now = now.AddSeconds(4);
            Assert.Equal(DutyStartupResult.Pending, startup.Update(true, (1044, 16), Ready, now));
        }
        var previousActive = native.Active.ToArray();
        held = true;
        if (provider == CombatProvider.Vbm)
        {
            native.Active.Clear();
            native.ForceDisabled = true;
        }
        native.Writes.Clear();
        run.Commands.Clear();
        var result = startup.Update(true, (1044, 16), Ready, now.AddSeconds(2));
        Assert.True(startup.BackendConfirmed);
        if (provider == CombatProvider.Bmr)
        {
            Assert.Equal(DutyStartupResult.Confirmed, result);
            Assert.True(native.AiEnabled);
            Assert.Contains("/bmrai on", run.Commands);
            return;
        }
        Assert.Equal(DutyStartupResult.Pending, result);
        Assert.False(startup.CombatActivated);
        Assert.False((bool)Get(run.Rotation, "rotationEnableSentForDuty")!);
        Assert.True(native.ForceDisabled);
        Assert.Empty(native.Active);
        Assert.Empty(native.Writes);
        Assert.Empty(run.Commands);

        held = false;
        native.Active = previousActive.ToList();
        native.ForceDisabled = false;
        Assert.Equal(DutyStartupResult.Confirmed, startup.Update(true, (1044, 16), Ready, now.AddSeconds(2.25)));
        Assert.True(native.AiEnabled);
        Assert.Contains("/vbmai on", run.Commands);
        Assert.DoesNotContain("/ads inside", run.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VbmReactivationBoundariesRefreshAnEarlierUnpausedSnapshot(bool aiCommand)
    {
        var native = new NativeBossModProvider(CombatProvider.Vbm, "Original");
        using var run = new Run(native);
        Assert.True(run.Rotation.Initialize());
        var held = false;
        var (_, ipc) = run.BindAdsStartup(() => held);
        ipc.Refresh(true, 1044, 16, force: true);
        Assert.False(ipc.IsInteractionVbmPauseActive);
        held = true;
        native.Writes.Clear();
        Assert.False(aiCommand ? run.BossMod.SendCommand("/vbmai on", "held activation") : run.BossMod.ApplyOwnedBossModPreset("null"));
        Assert.True(ipc.IsInteractionVbmPauseActive);
        Assert.Empty(native.Writes);

        held = false;
        Assert.True(aiCommand ? run.BossMod.SendCommand("/vbmai on", "released activation") : run.BossMod.ApplyOwnedBossModPreset("null"));
    }

    [Theory]
    [InlineData(CombatProvider.Vbm)]
    [InlineData(CombatProvider.Bmr)]
    public void AdsInteractionHoldRetainsPendingVbmPresetSettingsWhileBmrAppliesThem(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider, "Original");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        var held = false;
        var (startup, _) = run.BindAdsStartup(() => held);
        startup.ObserveReadiness(true, (1044, 16), Ready, DateTime.UtcNow);
        Assert.True(run.Rotation.EnableRotation());
        held = true;
        run.Config.ManualBossModPresetName = "Next";
        run.Manager.NotifyConfigurationChanged();
        native.Writes.Clear();
        run.Commands.Clear();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "ADS interaction hold");
        Assert.Empty(run.Commands);
        if (provider == CombatProvider.Bmr)
        {
            Assert.Equal(new[] { "Next" }, native.Active);
            Assert.True(native.AiEnabled);
            return;
        }
        Assert.Empty(native.Writes);
        Assert.True((bool)Get(run.Rotation, "settingsPending")!);
        Assert.Equal(new[] { "null" }, native.Active);
        held = false;
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "ADS interaction released");
        Assert.Equal(new[] { "Next" }, native.Active);
        Assert.False((bool)Get(run.Rotation, "settingsPending")!);
        Assert.Empty(run.Commands);
    }

    [Fact]
    public void VbmHoldAcquiredAfterPresetActivationPreventsAiOnWithoutCleanupOrFailureBackoff()
    {
        var native = new NativeBossModProvider(CombatProvider.Vbm, "Original");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        var released = false;
        var (startup, _) = run.BindAdsStartup(() => !released && native.Active.SequenceEqual(new[] { "null" }));
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        startup.Update(true, (1044, 16), Ready, now);
        native.Writes.Clear();
        run.Commands.Clear();
        Assert.Equal(DutyStartupResult.Pending, startup.Update(true, (1044, 16), Ready, now.AddSeconds(2)));
        Assert.Equal(new[] { "null" }, native.Active);
        Assert.False(startup.CombatActivated);
        Assert.False(native.AiEnabled);
        Assert.Empty(run.Commands);
        Assert.Contains("BossMod.Presets.SetActiveList", native.Writes);
        released = true;
        Assert.Equal(DutyStartupResult.Confirmed, startup.Update(true, (1044, 16), Ready, now.AddSeconds(2.25)));
        Assert.Contains("/vbmai on", run.Commands);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("character")]
    [InlineData("logout")]
    [InlineData("logout-unreadable")]
    [InlineData("reload")]
    public void BossModDeparturesEndTheOldSessionBeforeAnyNewCombatActivation(string departure)
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        native.Writes.Clear();
        var beforeDeparture = (Runtime: native.Active.ToArray(), native.Selector, native.Distance, native.AiEnabled);
        switch (departure)
        {
            case "provider": run.Config.CombatProvider = CombatProvider.Wrath; run.Manager.NotifyConfigurationChanged(); break;
            case "profile": run.Manager.GetCurrentAccount().Settings = new Configuration { CombatProvider = CombatProvider.Bmr }; run.Manager.NotifyConfigurationChanged(); break;
            case "account":
                var account = new AccountConfig { Settings = run.Config };
                account.SetCharacter(new CharacterConfig { ContentId = run.CurrentContentId, CharacterName = "Synthetic character", WorldName = "Synthetic world" });
                ((Dictionary<string, AccountConfig>)Get(run.Manager, "accounts")!).Add("other", account);
                Set(run.Manager, "currentAccountId", "other");
                run.Manager.NotifyConfigurationChanged();
                break;
            case "character": run.CurrentContentId = 2; run.Rotation.ObserveSessionDeparture(); break;
            case "logout": run.Rotation.EndSessionForLogout(); break;
            case "logout-unreadable":
                native.UnavailableChannel = "BossMod.Configuration";
                run.Rotation.EndSessionForLogout();
                break;
            default: native.Reload(); native.Active = ["Reloaded"]; native.Selector = "Reloaded"; run.Rotation.ObserveSessionDeparture(); break;
        }
        if (departure == "reload")
        {
            Assert.Empty(native.Writes);
            Assert.Equal(new[] { "Reloaded" }, native.Active);
            Assert.Equal("Reloaded", native.Selector);
        }
        else if (departure is "account" or "character")
        {
            Assert.Empty(native.Writes);
            Assert.Equal(beforeDeparture.Runtime, native.Active);
            Assert.Equal(beforeDeparture.Selector, native.Selector);
            Assert.Equal(beforeDeparture.Distance, native.Distance);
            Assert.Equal(beforeDeparture.AiEnabled, native.AiEnabled);
        }
        else
        {
            Assert.Contains("/bmrai off", native.Writes);
            Assert.Equal(new[] { "Original" }, native.Active);
            Assert.Equal("Original", native.Selector);
            Assert.Equal(departure == "logout-unreadable" ? 1.5 : 7.5, native.Distance);
        }
        var writes = native.Writes.ToArray();
        run.Rotation.ResetDutyRotationState("does not restart a departed session");
        Assert.False(run.Rotation.EnableRotation());
        run.Rotation.ObserveSessionDeparture();
        run.Rotation.EndSessionForLogout();
        Assert.Equal(writes, native.Writes);
        if (departure == "logout-unreadable")
        {
            Assert.False(run.Rotation.Initialize());
            Assert.Equal(1.5, native.Distance);
            native.UnavailableChannel = null;
            Assert.True(run.Rotation.DisableRotation());
            Assert.Equal(7.5, native.Distance);
            Assert.False(run.Rotation.EnableRotation());
        }
    }

    [Fact]
    public void BossModManualDeletionSavesAndNotifiesOnlyTheConsumedSetting()
    {
        var native = new NativeBossModProvider(CombatProvider.Vbm, "Original");
        using var run = new Run(native);
        var notifications = 0;
        run.Manager.ConfigurationChanged += _ => notifications++;
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "Deleted";
        Assert.True(run.Rotation.ValidateManualPresetSelection(run.Config));
        Assert.Equal("Original", run.Config.ManualBossModPresetName);
        Assert.Equal(1, notifications);
        Assert.Equal(1, native.AccountSaveAttempts);
        run.Config.ManualBossModPresetName = "Deleted";
        run.Config.CombatProvider = CombatProvider.Wrath;
        Assert.True(run.Rotation.ValidateManualPresetSelection(run.Config));
        run.Config.CombatProvider = CombatProvider.Rsr;
        Assert.True(run.Rotation.ValidateManualPresetSelection(run.Config));
        Assert.Equal("Deleted", run.Config.ManualBossModPresetName);
        Assert.Equal(1, notifications);
        Assert.Equal(1, native.AccountSaveAttempts);
    }

    [Fact]
    public void BossModPartialStartCleansConfirmedFieldsBeforeFailureReturns()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original") { RejectRuntimeWrite = true };
        using var run = new Run(native);
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "null";
        Assert.True(run.Rotation.Initialize());
        Assert.False(run.Rotation.EnableRotation());
        Assert.Equal("Original", native.Selector);
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal(7.5, native.Distance);
        Assert.DoesNotContain("/bmrai on", native.Writes);
        Assert.Empty((HashSet<CombatProvider>)Get(run.Rotation, "enabledComponents")!);
    }

    [Fact]
    public void BossModWrathManualSettingRemainsDormantDuringLiveNotifications()
    {
        var native = new NativeBossModProvider(CombatProvider.Bmr, "Original");
        using var run = new Run(native);
        run.Config.CombatProvider = CombatProvider.Wrath;
        Assert.True(run.Rotation.Initialize());
        Assert.True(run.Rotation.EnableRotation());
        native.Writes.Clear();
        run.Commands.Clear();
        run.Config.UseManualBossModPreset = true;
        run.Config.ManualBossModPresetName = "Deleted";
        run.Manager.NotifyConfigurationChanged();
        run.Rotation.UpdateDutyRotationHealth(1044, true, false, false, "dormant BossMod choice");
        Assert.Equal("Deleted", run.Config.ManualBossModPresetName);
        Assert.Empty(native.Writes);
        Assert.Empty(run.Commands);
        Assert.Equal(0, native.AccountSaveAttempts);
    }

    [Fact]
    public void MainTitlebarRechecksQueueCancellationAndUsesTheRealRunningStopHandler()
    {
        using var run = new Run();
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin, "<Engine>k__BackingField", run.Engine);
        Set(plugin, "<BossModIPC>k__BackingField", run.BossMod);
        Set(plugin, "<ConfigManager>k__BackingField", run.Manager);
        var main = new MainWindow(plugin);
        var start = main.TitleBarButtons.Single(button => button.Icon == FontAwesomeIcon.Play);
        var stop = main.TitleBarButtons.Single(button => button.Icon == FontAwesomeIcon.Stop);
        var stopNext = main.TitleBarButtons.Single(button => button.Icon == FontAwesomeIcon.Clock);
        start.Click(ImGuiMouseButton.Right);
        Assert.False(plugin.IsEngineStartQueued);
        start.Click(ImGuiMouseButton.Left);
        Assert.True(plugin.IsEngineStartQueued);
        stopNext.Click(ImGuiMouseButton.Left);
        Assert.True(run.Engine.StopAfterNextSuccessfulRunArmed);
        stop.Click(ImGuiMouseButton.Left);
        Assert.False(plugin.IsEngineStartQueued);
        Assert.False(run.Engine.StopAfterNextSuccessfulRunArmed);
        Assert.Equal(EngineState.Idle, run.Engine.CurrentState);
        run.Enter(30);
        Assert.True(run.Rotation.EnableRotation());
        start.Click(ImGuiMouseButton.Left);
        Assert.False(plugin.IsEngineStartQueued);
        Set(plugin, "pendingEngineStartRequest", true);
        Set(plugin, "deferredSharedConfigStartRequest", true);
        run.Commands.Clear();
        stop.Click(ImGuiMouseButton.Left);
        Assert.False(plugin.IsEngineStartQueued);
        Assert.False(run.Engine.IsRunning);
        Assert.Contains("/wrath auto off", run.Commands);
        Assert.Contains("/ads stop", run.Commands);
        Assert.False(run.Rotation.EnableRotation());
        Set(plugin, "<Engine>k__BackingField", null);
        Set(plugin, "pendingEngineStartRequest", true);
        Set(plugin, "deferredSharedConfigStartRequest", true);
        stop.Click(ImGuiMouseButton.Left);
        Assert.False(plugin.IsEngineStartQueued);
        start.Click(ImGuiMouseButton.Left);
        Assert.False(plugin.IsEngineStartQueued);
    }

    [Fact]
    public void RsrActivationDoesNotAcknowledgeUnavailableModeIpcThroughChat()
    {
        using var run = new Run();
        run.Config.CombatProvider = CombatProvider.Rsr;

        Assert.False((bool)Invoke(run.Rotation, "EnableRsr")!);
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/rotation", StringComparison.OrdinalIgnoreCase));

        Set(run.Rotation, "rotationDisableSentForDuty", true);
        Assert.False((bool)Invoke(run.Rotation, "EnableRsr")!);
        Assert.DoesNotContain(run.Commands, command => command.StartsWith("/rotation", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Run : IDisposable
    {
        internal readonly Configuration Config = new() { UseAdsExperimental = true, CombatProvider = CombatProvider.Wrath,
            EnableDetailedTracking = true, BailoutTimeout = 100, LastDailyDecuReset = DateTime.UtcNow };
        internal readonly DutyState State = new();
        internal readonly List<string> Commands = [];
        internal readonly List<string> ChatMessages = [];
        internal readonly MogtomeEngine Engine;
        internal readonly DutyQueueService Queue;
        internal readonly DutyAutomationService Automation;
        internal readonly RotationService Rotation;
        internal readonly YesAlreadyIPC YesAlready;
        internal readonly RunHistoryService History;
        internal readonly ConfigManager Manager;
        internal readonly BossModIPC BossMod;
        internal ulong CurrentContentId;
        internal bool XaDatabaseLoaded = true;
        internal bool GmcLoaded;
        internal bool GmcSuiteReady = true;
        internal uint Territory;
        internal int RsrSettingsRequests;
        internal uint[] PartyTerritories = [];
        internal string? ThrowOnCommand;
        internal readonly List<string> RepairModes = [];
        internal string RepairStatus = "{}";
        internal string RepairCapabilities = """{"schemaVersion":1,"repairModes":["npc-no-inn","npc-no-inn-uldah","npc-no-inn-gridania","npc-no-inn-limsa","npc-no-inn-ishgard","npc-no-inn-crystarium","npc-no-inn-sharlayan","npc-no-inn-tuliyollal"]}""";
        internal bool AcceptRepair = true;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> scheduled = new();
        private readonly Dictionary<string, object?> originalStatics = new();

        internal Run(NativeBossModProvider? native = null, ClientLanguage clientLanguage = ClientLanguage.English,
            ISeStringEvaluator? evaluator = null)
        {
            var automationHost = native ?? new NativeBossModProvider(CombatProvider.Bmr, "Original", internalName: "GodMod");
            CurrentContentId = native is null ? 0UL : 1UL;
            if (native is not null) Config.CombatProvider = native.Provider;
            var log = native?.Log ?? Fake<IPluginLog>();
            var client = Fake<IClientState>((method, _) => method.Name switch
            {
                "get_IsLoggedIn" => true,
                "get_ClientLanguage" => clientLanguage,
                "get_TerritoryType" => Territory,
                _ => Default(method.ReturnType),
            });
            var condition = Fake<ICondition>();
            var objects = Fake<IObjectTable>();
            var player = Fake<IPlayerState>((method, _) => method.Name == "get_ContentId" ? CurrentContentId : Default(method.ReturnType));
            var framework = Fake<IFramework>((method, args) =>
            {
                if (method.Name == "RunOnTick")
                {
                    var action = (Delegate)args![0]!;
                    if (method.ReturnType == typeof(Task))
                    {
                        var completion = new TaskCompletionSource();
                        scheduled.Enqueue(() => { try { action.DynamicInvoke(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
                        return completion.Task;
                    }
                    return typeof(Run).GetMethod(nameof(ScheduleValue), BindingFlags.Instance | BindingFlags.NonPublic)!
                        .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(this, [action]);
                }
                return Default(method.ReturnType);
            });
            var requests = new HashSet<string>();
            var dependencies = new[] { "XADatabase", "XASlave", "BMRVariantLoader" }.Select(name =>
                Fake<IExposedPlugin>((member, _) => member.Name switch
                {
                    "get_InternalName" or "get_Name" => name,
                    "get_IsLoaded" => name == "BMRVariantLoader" ? GmcLoaded : name != "XADatabase" || XaDatabaseLoaded,
                    _ => Default(member.ReturnType),
                })).ToArray();
            var pi = Fake<IDalamudPluginInterface>((method, args) =>
            {
                if (method.Name == "GetOrCreateData") return requests;
                if (method.Name == "GetIpcSubscriber" && Equals(args![0], "RotationSolverReborn.OtherCommand"))
                    RsrSettingsRequests++;
                if (method.Name == "get_InstalledPlugins") return automationHost.Interface.InstalledPlugins.Concat(dependencies).ToArray();
                if (method.Name == "GetIpcSubscriber" && Equals(args![0], "BMRVariantLoader.Suite.IsReady.v1"))
                    return NativeBossModProvider.Proxy(method.ReturnType, (call, _) => call.Name == "InvokeFunc" && GmcSuiteReady);
                if (method.Name == "GetIpcSubscriber" && args![0] is string nativeName && nativeName.StartsWith("BossMod", StringComparison.Ordinal))
                    return NativeBossModProvider.Proxy(method.ReturnType, (call, values) => automationHost.Invoke(nativeName, call, values));
                if (method.Name == "GetIpcSubscriber" && args![0] is string name && name.StartsWith("ADS."))
                {
                    object? Call(MethodInfo member, object?[]? values)
                    {
                        if (member.Name != "InvokeFunc") return Default(member.ReturnType);
                        if (name == "ADS.GetStatusJson") return RepairStatus;
                        if (name == "ADS.GetCapabilitiesJson") return RepairCapabilities;
                        if (name == "ADS.StartRepair") { RepairModes.Add((string)values![0]!); return AcceptRepair; }
                        return Default(member.ReturnType);
                    }
                    return typeof(DutyRecoveryTests).GetMethod(nameof(Fake), BindingFlags.Static | BindingFlags.NonPublic)!
                        .MakeGenericMethod(method.ReturnType).Invoke(null, new object[] { (Func<MethodInfo, object?[]?, object?>)Call });
                }
                return Default(method.ReturnType);
            });
            var party = Fake<IPartyList>((method, args) => method.Name switch
            {
                "get_Length" => PartyTerritories.Length,
                "get_Item" => Fake<IPartyMember>((member, _) => member.Name == "get_Territory"
                    ? TerritoryRow(PartyTerritories[(int)args![0]!]) : Default(member.ReturnType)),
                _ => Default(method.ReturnType),
            });
            var command = Fake<ICommandManager>((method, args) =>
            {
                if (method.Name != "ProcessCommand") return Default(method.ReturnType);
                var text = (string)args![0]!;
                Commands.Add(text);
                if (text == ThrowOnCommand) throw new InvalidOperationException("injected command failure");
                return native?.SendCommand(text) ?? true;
            });
            SetStatic("Log", log); SetStatic("ClientState", client); SetStatic("Condition", condition);
            SetStatic("ObjectTable", objects); SetStatic("PlayerState", player); SetStatic("PartyList", party);
            SetStatic("Framework", framework); SetStatic("PluginInterface", pi); SetStatic("GameGui", Fake<IGameGui>());
            SetStatic("ChatGui", Fake<IChatGui>((method, args) =>
            {
                if (method.Name == "Print") ChatMessages.Add(args![0]?.ToString() ?? string.Empty);
                return Default(method.ReturnType);
            }));
            SetStatic("ToastGui", Fake<IToastGui>()); SetStatic("DutyStateService", Fake<IDutyState>());
            if (evaluator != null) SetStatic("SeStringEvaluator", evaluator);
            var manager = (ConfigManager)RuntimeHelpers.GetUninitializedObject(typeof(ConfigManager));
            Set(manager, "log", log);
            Set(manager, "currentAccountId", "temporary");
            Set(manager, "accounts", new Dictionary<string, AccountConfig> { ["temporary"] = new() { Settings = Config } });
            if (native is not null) manager.GetCurrentAccount().SetCharacter(new CharacterConfig { ContentId = CurrentContentId, CharacterName = "Synthetic character", WorldName = "Synthetic world" });
            Manager = manager;
            var deaths = new DeathTrackingService(log, framework, objects, party, player, condition, client);
            History = new RunHistoryService(log, Config, State, player, manager, null!, deaths);
            var autoDuty = new AutoDutyIPC(log, command, History);
            Automation = new DutyAutomationService(log, manager, autoDuty, null!, null!, command, History);
            Queue = new DutyQueueService(log, State, Automation, condition, manager);
            BossMod = new BossModIPC(pi, log, command);
            Rotation = new RotationService(log, manager, BossMod);
            YesAlready = new YesAlreadyIPC(log);
            var dialog = new DialogHandlerService(log, YesAlready, command, Fake<IGameGui>(), manager);
            var tracker = new DutyTrackerService(log, Config, State, manager, History);
            Engine = new MogtomeEngine(log, Config, State, tracker, Queue, null!, null!, null!, Rotation, null!, null!,
                dialog, Automation, null!, null!, History, deaths, autoDuty, YesAlready, null!, condition, client, command, () => { });
        }

        internal void Enter(int seconds, uint territory = 1044)
        {
            State.HasEnteredDuty = true;
            State.IsInDuty = true;
            State.DutyStartTerritory = territory;
            State.DutyStartTime = DateTime.UtcNow.AddSeconds(-seconds);
            Set(Engine, "<CurrentState>k__BackingField", EngineState.InDuty);
        }
        internal void CallExit() => Invoke(Engine, "OnLeftDuty");

        internal (DutyStartupService Startup, AdsDutyIpcService Ipc) BindAdsStartup(Func<bool> interactionHeld)
        {
            var ipc = new AdsDutyIpcService(() => true, () => true,
                () => JsonSerializer.Serialize(new
                {
                    inInstancedDuty = true,
                    ownershipMode = "OwnedStartInside",
                    hasCatalogMetadata = true,
                    duty = "Test duty",
                    territoryTypeId = 1044,
                    contentFinderConditionId = 16,
                    dutyCategory = "FourMan",
                    supportLevel = "ActiveSupported",
                    clearanceStatus = "FourPlayerSyncCleared",
                    interactionVbmPauseActive = interactionHeld(),
                }), () => throw new InvalidOperationException("owned backend must not restart"), () => DateTime.UtcNow);
            Rotation.SetAdsDutyIpcService(ipc);
            return (new DutyStartupService(ipc, () => true, () => throw new InvalidOperationException("unexpected AutoDuty start"),
                () => Rotation.EnableRotationOncePerDuty("test duty startup"), () => Rotation.Failure,
                _ => throw new InvalidOperationException("unexpected backend command"), () => 7190, _ => { }, _ => { },
                Rotation.InvalidateCombatActivation, () => Rotation.IsVbmActivationDeferred), ipc);
        }

        internal void Pump() { while (scheduled.TryDequeue(out var action)) action(); }
        private Task<T> ScheduleValue<T>(Delegate action)
        {
            var completion = new TaskCompletionSource<T>();
            scheduled.Enqueue(() => { try { completion.SetResult((T)action.DynamicInvoke()!); } catch (Exception ex) { completion.SetException(ex); } });
            return completion.Task;
        }
        private void SetStatic(string property, object value)
        {
            var info = typeof(Plugin).GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic)!;
            originalStatics[property] = info.GetValue(null);
            info.SetValue(null, value);
        }
        public void Dispose()
        {
            ThrowOnCommand = null;
            Engine.Dispose();
            Rotation.Dispose();
            BossMod.Dispose();
            History.Dispose();
            Pump();
            foreach (var (name, value) in originalStatics)
                typeof(Plugin).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
        }
    }

    public enum NativeModuleOrder { Rotation, Movement }
    [Flags]
    public enum NativeModifier { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }
    public sealed record NativeStrategyConfig(string InternalName);
    public sealed record NativeModuleDefinition(NativeModuleOrder Order)
    {
        public readonly List<NativeStrategyConfig> Configs = [];
    }
    public record struct NativeSetting(NativeModifier Mod, int Track, JsonObject Value)
    {
        public NativeModifier Mod = Mod;
        public JsonObject Value = Value;
    }
    public sealed record NativePresetModule(NativeModuleDefinition Definition)
    {
        public string ModuleName = "";
        public Type Type = typeof(object);
        public readonly List<NativeSetting> SerializedSettings = [], TransientSettings = [];
    }
    public sealed record NativeStrategyValues(List<NativeStrategyConfig> Configs, JsonObject[] Values)
    {
        public string Option(string track) => Values[Configs.FindIndex(config => config.InternalName == track)]["Option"]!.GetValue<string>();
    }
    public record NativePreset(string Name, bool HiddenByDefault = false)
    {
        public static readonly NativePreset ForceDisable = new("Fixture force disabled");
        public List<NativePresetModule> Modules = [new(new(NativeModuleOrder.Rotation)), new(new(NativeModuleOrder.Movement))];
        public Func<NativeModifier> Modifiers = () => NativeModifier.None;
        public Action? OnClone, OnResolve;
        public NativePreset MakeClone(bool includeTransient)
        {
            OnClone?.Invoke();
            var clone = (NativePreset)Activator.CreateInstance(GetType(), Name, HiddenByDefault)!;
            clone.Modules = [];
            clone.Modifiers = Modifiers;
            clone.OnClone = OnClone;
            clone.OnResolve = OnResolve;
            foreach (var module in Modules)
            {
                var copy = new NativePresetModule(module.Definition) { ModuleName = module.ModuleName, Type = module.Type };
                copy.SerializedSettings.AddRange(module.SerializedSettings);
                if (includeTransient) copy.TransientSettings.AddRange(module.TransientSettings);
                clone.Modules.Add(copy);
            }
            return clone;
        }
        public NativeStrategyValues ActiveStrategyOverrides(int index)
        {
            OnResolve?.Invoke();
            return ActiveStrategyOverrides(index, Modifiers());
        }
        public NativeStrategyValues ActiveStrategyOverrides(int index, NativeModifier modifiers)
        {
            var module = Modules[index];
            var values = module.Definition.Configs.Select(_ => new JsonObject { ["Option"] = "Auto" }).ToArray();
            // Model the selected frozen resolver: serialized order, then transient
            // order; subset matching and last matching entry wins (5e2727).
            foreach (var setting in module.SerializedSettings.Concat(module.TransientSettings))
                if ((setting.Mod & modifiers) == setting.Mod) values[setting.Track] = setting.Value;
            return new(module.Definition.Configs, values);
        }
    }

    // This converter models the native preset serialization contract.
    private sealed class NativePresetConverterFactory(NativeBossModProvider provider) : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => typeof(NativePreset).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(NativePresetConverter<>).MakeGenericType(type),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [provider], CultureInfo.InvariantCulture)!;
    }
    private sealed class NativePresetConverter<T>(NativeBossModProvider provider) : JsonConverter<T> where T : NativePreset
    {
        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            ++provider.NativeDeserializations;
            using var document = JsonDocument.ParseValue(ref reader);
            var preset = (T)Activator.CreateInstance(type, document.RootElement.GetProperty("Name").GetString()!, false)!;
            preset.Modules = [];
            preset.Modifiers = () => provider.CurrentModifiers;
            preset.OnClone = () => ++provider.NativeClones;
            preset.OnResolve = () => ++provider.NativeResolutions;
            foreach (var item in document.RootElement.GetProperty("Modules").EnumerateObject())
            {
                var regular = item.Name == NativeBossModProvider.BstModule;
                var definition = regular ? provider.BeastmasterDefinition
                    : new NativeModuleDefinition(item.Name.Contains("MiscAI", StringComparison.Ordinal)
                        ? NativeModuleOrder.Movement : NativeModuleOrder.Rotation);
                var module = new NativePresetModule(definition)
                {
                    ModuleName = item.Name,
                    Type = regular ? provider.BeastmasterType : item.Name == "BossMod.Autorotation.xan.BST"
                        ? provider.NativeBeastmasterType : typeof(object)
                };
                foreach (var entry in item.Value.EnumerateArray())
                {
                    var name = entry.GetProperty("Track").GetString()!;
                    var track = definition.Configs.FindIndex(config => config.InternalName == name);
                    if (track < 0 && regular) continue;
                    if (track < 0) { track = definition.Configs.Count; definition.Configs.Add(new(name)); }
                    var value = JsonNode.Parse(entry.GetRawText())!.AsObject();
                    var modifier = value["Mod"] is { } mod ? Enum.Parse<NativeModifier>(mod.GetValue<string>()) : NativeModifier.None;
                    value.Remove("Track");
                    value.Remove("Mod");
                    module.SerializedSettings.Add(new(modifier, track, value));
                }
                preset.Modules.Add(module);
            }
            return preset;
        }
        public override void Write(Utf8JsonWriter writer, T preset, JsonSerializerOptions options)
        {
            ++provider.NativeSerializations;
            writer.WriteStartObject();
            writer.WriteString("Name", preset.Name);
            writer.WriteStartObject("Modules");
            foreach (var module in preset.Modules.Where(module => module.ModuleName.Length > 0))
            {
                writer.WriteStartArray(module.ModuleName);
                foreach (var setting in module.SerializedSettings)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Track", module.Definition.Configs[setting.Track].InternalName);
                    foreach (var field in setting.Value)
                    {
                        writer.WritePropertyName(field.Key);
                        if (field.Value is null) writer.WriteNullValue(); else field.Value.WriteTo(writer);
                    }
                    if (setting.Mod != NativeModifier.None) writer.WriteString("Mod", setting.Mod.ToString());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
    private sealed class NativePresetDatabase
    {
        public List<NativePreset> AllPresets { get; set; } = [];
        public List<NativePreset> DefaultPresets { get; set; } = [];
        public List<NativePreset> UserPresets { get; set; } = [];
    }
    private enum NativeJob : uint { WAR = 21, NIN = 30, GNB = 37, SGE = 40, BST = 43 }
    private sealed record NativePlan(string Name, NativeJob Class, Type Encounter)
    {
        public List<object> Modules = [new object()];
    }
    private sealed class NativePlanList
    {
        public int SelectedIndex = -1;
        public List<NativePlan> Plans = [];
    }
    private sealed class NativePlanDatabase
    {
        public Dictionary<Type, Dictionary<NativeJob, NativePlanList>> Plans = [];
    }
    private sealed record NativeRotationDatabase(NativePresetDatabase Presets)
    {
        public NativePlanDatabase Plans = new();
    }
    private sealed class NativeActor
    {
        public NativeJob Class = NativeJob.WAR;
        public bool InCombat = true, IsDead;
        public ulong InstanceID = 101;
        public string Name = "Fixture actor";
    }
    private sealed class NativeParty
    {
        public NativeActor?[] Members = Enumerable.Range(0, 4).Select(i => (NativeActor?)new NativeActor { InstanceID = (ulong)(101 + i) }).ToArray();
        public NativeActor? this[int slot] => slot >= 0 && slot < Members.Length ? Members[slot] : null;
    }
    private sealed class NativeWorld { public NativeParty Party = new(); public uint CurrentZone = 0; }
    private sealed class NativeAiBehavior(NativePreset? preset) { public NativePreset? AIPreset = preset; }
    private sealed class NativeHints { public bool ScriptedDeath; }
    private sealed class NativeBossmods { public object? ActiveModule; public List<object> LoadedModules = []; }
    private sealed record NativePlanner(object Module, NativePlan Plan);
    private sealed class NativeRotationManager(NativeRotationDatabase database, Action<NativePreset?> onPreset)
    {
        public NativeRotationDatabase Database = database;
        public NativeWorld WorldState = new();
        public NativeActor Player = new();
        public NativeBossmods Bossmods = new();
        public NativePlanner? Planner;
        public NativeHints Hints = new();
        public NativePreset? Preset { get; set { field = value; onPreset(value); } }
    }
    private sealed class D141Colossus { }
    private sealed class D142Nero { }
    private sealed class D143Gaius { }
    private sealed class T04PortaDecumana1 { }
    private sealed class T04PortaDecumana2 { }
    private sealed record NativeHost(IServiceProvider Services);
    private sealed class NativeTickServices(object tick) : IServiceProvider
    {
        public object? GetService(Type serviceType) => tick.GetType() == serviceType ? tick : null;
    }

    // Existing native provider members and IPC contracts, supplied by an in-memory
    // assembly. This does not create a Dalamud host, game service, or dependency.
    private sealed class NativeBossModProvider
    {
        internal readonly CombatProvider Provider;
        internal readonly string InternalName;
        internal readonly IDalamudPluginInterface Interface;
        internal readonly IPluginLog Log;
        internal readonly NativePresetDatabase Presets = new();
        internal readonly List<string> Writes = [];
        internal readonly List<string> Warnings = [];
        internal List<string> Active;
        internal bool ForceDisabled;
        internal bool RejectRuntimeWrite;
        internal bool RejectSelectorWrite;
        internal bool ThrowAfterSelectorWrite;
        internal bool ThrowAfterRuntimeWrite;
        internal bool RejectAiEffect;
        internal bool Ambiguous;
        internal string? AdditionalHost;
        internal string? UnavailableChannel;
        internal double Distance = 7.5;
        internal int AccountSaveAttempts;
        private readonly Dictionary<string, string> presetDefinitions = new(StringComparer.Ordinal);
        internal Action? AfterSelectorWrite;
        private readonly object node;
        private readonly object autorotationConfig;
        private bool ready = true;
        private readonly object? aiManager;
        private readonly System.Collections.IDictionary dhogModules;
        private readonly Type[] dhogTypes;
        internal const string BstModule = "BMRVariantModulePack.Autorotation.Dhog.DhogBST";
        internal readonly Type BeastmasterType, NativeBeastmasterType;
        internal readonly NativeModuleDefinition BeastmasterDefinition = new(NativeModuleOrder.Rotation);
        internal readonly Type[] NativePraetoriumEncounters;
        private readonly Type nativePresetType;
        internal bool RegularBeastmasterAvailable;
        internal NativeModifier CurrentModifiers;
        internal int NativeSerializations, NativeDeserializations, NativeClones, NativeResolutions;

        internal JsonSerializerOptions BuildNativeOptions() => new()
        {
            IncludeFields = true,
            Converters = { new NativePresetConverterFactory(this) }
        };
        internal string SerializePreset(NativePreset preset)
            => JsonSerializer.Serialize(preset, preset.GetType(), BuildNativeOptions());
        internal NativePreset DeserializePreset(string json)
            => (NativePreset)JsonSerializer.Deserialize(json, nativePresetType, BuildNativeOptions())!;
        internal void SavePreset(string json)
        {
            var preset = DeserializePreset(json);
            Presets.AllPresets.RemoveAll(previous => previous.Name == preset.Name);
            Presets.DefaultPresets.RemoveAll(previous => previous.Name == preset.Name);
            Presets.UserPresets.RemoveAll(previous => previous.Name == preset.Name);
            Presets.AllPresets.Add(preset);
            Presets.UserPresets.Add(preset);
            presetDefinitions[preset.Name] = SerializePreset(preset);
        }
        internal void ReloadSavedPresets()
        {
            var saved = Presets.UserPresets.Select(SerializePreset).ToArray();
            Presets.UserPresets = saved.Select(DeserializePreset).ToList();
            Presets.AllPresets = [.. Presets.DefaultPresets, .. Presets.UserPresets];
            Rotation.Preset = Rotation.Preset is { } active ? Presets.AllPresets.Single(preset => preset.Name == active.Name) : null;
            AiPreset = AiPreset is { } ai ? Presets.AllPresets.Single(preset => preset.Name == ai.Name) : null;
        }
        internal void EnableRegularBeastmaster()
        {
            RegularBeastmasterAvailable = true;
            dhogModules.Add(BeastmasterType, new { Definition = BeastmasterDefinition });
            Rotation.Player.Class = NativeJob.BST;
            Rotation.WorldState = World;
            World.Party.Members[0] = Rotation.Player;
        }
        internal readonly NativeWorld World = new();
        private readonly object wrapper;
        private readonly Type pluginType;
        private readonly object database;
        internal readonly NativeRotationManager Rotation;
        private readonly object? host;

        internal bool Loaded
        {
            get => (bool)wrapper.GetType().GetField("Loaded")!.GetValue(wrapper)!;
            set => wrapper.GetType().GetField("Loaded")!.SetValue(wrapper, value);
        }
        internal bool Ready { get => ready; set { ready = value; Reload(); } }
        internal bool ClearPresetOnDeath
        {
            get => (bool)autorotationConfig.GetType().GetField("ClearPresetOnDeath")!.GetValue(autorotationConfig)!;
            set => autorotationConfig.GetType().GetField("ClearPresetOnDeath")!.SetValue(autorotationConfig, value);
        }
        internal void Die()
        {
            Rotation.Player.IsDead = true;
            if (Rotation.Player.InCombat && ClearPresetOnDeath && !(InternalName == "GodMod" && Rotation.Hints.ScriptedDeath))
                Rotation.Preset = NativePreset.ForceDisable;
        }
        internal string? Selector
        {
            get => (string?)node.GetType().GetField("AIAutorotPresetName")!.GetValue(node);
            set => node.GetType().GetField("AIAutorotPresetName")!.SetValue(node, value);
        }
        internal bool AiEnabled
        {
            get => (bool)node.GetType().GetField("Enabled")!.GetValue(node)!;
            set
            {
                node.GetType().GetField("Enabled")!.SetValue(node, value);
                if (aiManager is null) return;
                var preset = value ? Presets.AllPresets.SingleOrDefault(p => p.Name == Selector) : AiPreset;
                if (value) AiPreset = preset;
                aiManager.GetType().GetField("Beh")!.SetValue(aiManager, value ? new NativeAiBehavior(preset) : null);
                AiMasterSlot = 0;
            }
        }
        internal NativePreset? AiPreset
        {
            get => aiManager?.GetType().GetField("AiPreset")!.GetValue(aiManager) as NativePreset;
            private set => aiManager?.GetType().GetField("AiPreset")!.SetValue(aiManager, value);
        }
        internal int AiMasterSlot
        {
            get => (int)(aiManager?.GetType().GetField("MasterSlot")!.GetValue(aiManager) ?? 0);
            set => aiManager?.GetType().GetField("MasterSlot")!.SetValue(aiManager, value);
        }
        internal bool ForbidActions
        {
            get => (bool)node.GetType().GetField("ForbidActions")!.GetValue(node)!;
            set => node.GetType().GetField("ForbidActions")!.SetValue(node, value);
        }
        internal bool ForbidMovement
        {
            get => (bool)node.GetType().GetField("ForbidMovement")!.GetValue(node)!;
            set => node.GetType().GetField("ForbidMovement")!.SetValue(node, value);
        }
        internal bool DhogHealersAvailable
        {
            get => dhogModules.Count == dhogTypes.Length;
            set { dhogModules.Clear(); if (value) foreach (var type in dhogTypes) dhogModules.Add(type, new object()); }
        }

        // Only the qualified publication branch is modeled. No navigation,
        // native job/healer module, action queue or game action executes here.
        internal void PublishAiPreset(bool hasTarget)
        {
            if (AiEnabled && !ForbidActions && !Rotation.Player.IsDead)
                Rotation.Preset = hasTarget ? AiPreset : null;
        }

        internal bool ManualTarget
        {
            get => (bool)node.GetType().GetField("ManualTarget")!.GetValue(node)!;
            set => node.GetType().GetField("ManualTarget")!.SetValue(node, value);
        }

        internal NativeBossModProvider(CombatProvider provider, string names, bool disabled = false, string? internalName = null)
        {
            Provider = provider;
            InternalName = internalName ?? (provider == CombatProvider.Vbm ? "BossMod" : "BossModReborn");
            Active = names.Length == 0 ? [] : names.Split('|').ToList();
            ForceDisabled = disabled;
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MogtomeNativeBossMod" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.Run).DefineDynamicModule("NativeProvider");
            var configNodeType = module.DefineType("BossMod.ConfigNode", TypeAttributes.Public | TypeAttributes.Abstract).CreateType()!;
            var nodeBuilder = module.DefineType("BossMod.AI.AIConfig", TypeAttributes.Public, configNodeType);
            nodeBuilder.DefineField("Enabled", typeof(bool), FieldAttributes.Public);
            nodeBuilder.DefineField("ManualTarget", typeof(bool), FieldAttributes.Public);
            nodeBuilder.DefineField("ForbidActions", typeof(bool), FieldAttributes.Public);
            nodeBuilder.DefineField("ForbidMovement", typeof(bool), FieldAttributes.Public);
            nodeBuilder.DefineField("AIAutorotPresetName", typeof(string), FieldAttributes.Public);
            var presetBuilder = module.DefineType("BossMod.Autorotation.Preset", TypeAttributes.Public, typeof(NativePreset));
            var presetCtor = presetBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
                [typeof(string), typeof(bool)]).GetILGenerator();
            presetCtor.Emit(OpCodes.Ldarg_0);
            presetCtor.Emit(OpCodes.Ldarg_1);
            presetCtor.Emit(OpCodes.Ldarg_2);
            presetCtor.Emit(OpCodes.Call, typeof(NativePreset).GetConstructor([typeof(string), typeof(bool)])!);
            presetCtor.Emit(OpCodes.Ret);
            nativePresetType = presetBuilder.CreateType()!;
            NativePraetoriumEncounters = new[]
            {
                "BossMod.RealmReborn.Dungeon.D15Praetorium.D151Colossus.D151Colossus",
                "BossMod.RealmReborn.Dungeon.D15Praetorium.D152Nero.D152Nero",
                "BossMod.RealmReborn.Dungeon.D15Praetorium.D153Gaius.D153Gaius",
            }.Select(name => module.DefineType(name, TypeAttributes.Public).CreateType()!).ToArray();
            BeastmasterDefinition.Configs.AddRange((InternalName == "GodMod"
                ? new[] { "Targeting", "DhogThirdBeast", "DhogFirstBeast", "DhogSecondBeast", "AOE", "Combo", "TemperedRelease" }
                : new[] { "DhogBurst", "Targeting", "DhogSecondBeast", "DhogThirdBeast", "DhogFirstBeast", "AOE", "Combo" })
                .Select(name => new NativeStrategyConfig(name)));
            NativeBeastmasterType = module.DefineType("BossMod.Autorotation.xan.BST", TypeAttributes.Public).CreateType()!;
            BeastmasterType = module.DefineType(BstModule, TypeAttributes.Public).CreateType()!;
            var serialization = module.DefineType("BossMod.Serialization", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var optionsFactory = serialization.DefineField("OptionsFactory", typeof(Func<JsonSerializerOptions>), FieldAttributes.Public | FieldAttributes.Static);
            var optionsMethod = serialization.DefineMethod("BuildSerializationOptions", MethodAttributes.Public | MethodAttributes.Static,
                typeof(JsonSerializerOptions), Type.EmptyTypes).GetILGenerator();
            optionsMethod.Emit(OpCodes.Ldsfld, optionsFactory);
            optionsMethod.Emit(OpCodes.Callvirt, typeof(Func<JsonSerializerOptions>).GetMethod("Invoke")!);
            optionsMethod.Emit(OpCodes.Ret);
            serialization.CreateType()!.GetField("OptionsFactory")!.SetValue(null, (Func<JsonSerializerOptions>)BuildNativeOptions);
            var nodeType = nodeBuilder.CreateType()!;
            node = Activator.CreateInstance(nodeType)!;
            var autorotationBuilder = module.DefineType("BossMod.Autorotation.AutorotationConfig", TypeAttributes.Public, configNodeType);
            autorotationBuilder.DefineField("ClearPresetOnDeath", typeof(bool), FieldAttributes.Public);
            autorotationConfig = Activator.CreateInstance(autorotationBuilder.CreateType()!)!;
            ClearPresetOnDeath = true;
            Selector = "Original";
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(Type), configNodeType);
            var configBuilder = module.DefineType("BossMod.ConfigRoot", TypeAttributes.Public);
            var nodesField = configBuilder.DefineField("_nodes", dictionaryType, FieldAttributes.Private);
            MethodBuilder? typedAccessor = null;
            if (provider == CombatProvider.Vbm)
            {
                var nodesGetter = configBuilder.DefineMethod("get_Nodes", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    typeof(IEnumerable<Type>), Type.EmptyTypes);
                var nodesIl = nodesGetter.GetILGenerator();
                nodesIl.Emit(OpCodes.Ldarg_0);
                nodesIl.Emit(OpCodes.Ldfld, nodesField);
                nodesIl.Emit(OpCodes.Callvirt, dictionaryType.GetProperty("Keys")!.GetMethod!);
                nodesIl.Emit(OpCodes.Ret);
                configBuilder.DefineProperty("Nodes", PropertyAttributes.None, typeof(IEnumerable<Type>), Type.EmptyTypes).SetGetMethod(nodesGetter);
                typedAccessor = configBuilder.DefineMethod("Get", MethodAttributes.Public);
                var typedArgument = typedAccessor.DefineGenericParameters("T")[0];
                typedArgument.SetBaseTypeConstraint(configNodeType);
                typedAccessor.SetReturnType(typedArgument);
                typedAccessor.SetParameters(typeof(Type));
                var typedIl = typedAccessor.GetILGenerator();
                typedIl.Emit(OpCodes.Ldarg_0);
                typedIl.Emit(OpCodes.Ldfld, nodesField);
                typedIl.Emit(OpCodes.Ldarg_1);
                typedIl.Emit(OpCodes.Callvirt, dictionaryType.GetProperty("Item")!.GetMethod!);
                typedIl.Emit(OpCodes.Unbox_Any, typedArgument);
                typedIl.Emit(OpCodes.Ret);
            }
            var accessor = configBuilder.DefineMethod("Get", MethodAttributes.Public);
            var configArgument = accessor.DefineGenericParameters("T")[0];
            configArgument.SetBaseTypeConstraint(configNodeType);
            accessor.SetReturnType(configArgument);
            var configIl = accessor.GetILGenerator();
            configIl.Emit(OpCodes.Ldarg_0);
            if (typedAccessor is null)
                configIl.Emit(OpCodes.Ldfld, nodesField);
            configIl.Emit(OpCodes.Ldtoken, configArgument);
            configIl.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            configIl.Emit(typedAccessor is null ? OpCodes.Callvirt : OpCodes.Call,
                typedAccessor is null ? dictionaryType.GetProperty("Item")!.GetMethod! : typedAccessor.MakeGenericMethod(configArgument));
            if (typedAccessor is null)
                configIl.Emit(OpCodes.Unbox_Any, configArgument);
            configIl.Emit(OpCodes.Ret);
            var configType = configBuilder.CreateType()!;
            var configRoot = Activator.CreateInstance(configType)!;
            var nodes = (System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
            nodes.Add(nodeType, node);
            nodes.Add(autorotationConfig.GetType(), autorotationConfig);
            configType.GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(configRoot, nodes);
            var serviceBuilder = module.DefineType("BossMod.Service", TypeAttributes.Public);
            serviceBuilder.DefineField("Config", configType, FieldAttributes.Public | FieldAttributes.Static);
            serviceBuilder.CreateType()!.GetField("Config")!.SetValue(null, configRoot);
            dhogTypes = new[] { "WHM", "SCH", "AST", "SGE" }.Select(job =>
                module.DefineType("BMRVariantModulePack.Autorotation.Dhog.Dhog" + job, TypeAttributes.Public).CreateType()!).ToArray();
            dhogModules = new Dictionary<Type, object>();
            var registryBuilder = module.DefineType("BossMod.Autorotation.RotationModuleRegistry", TypeAttributes.Public);
            registryBuilder.DefineField("Modules", typeof(System.Collections.IDictionary), FieldAttributes.Public | FieldAttributes.Static);
            var resolver = registryBuilder.DefineField("Resolver", typeof(Func<string, Type>), FieldAttributes.Public | FieldAttributes.Static);
            var find = registryBuilder.DefineMethod("FindType", MethodAttributes.Public | MethodAttributes.Static, typeof(Type), [typeof(string)]);
            var findIl = find.GetILGenerator();
            findIl.Emit(OpCodes.Ldsfld, resolver);
            findIl.Emit(OpCodes.Ldarg_0);
            findIl.Emit(OpCodes.Callvirt, typeof(Func<string, Type>).GetMethod("Invoke")!);
            findIl.Emit(OpCodes.Ret);
            var registry = registryBuilder.CreateType()!;
            registry.GetField("Modules")!.SetValue(null, dhogModules);
            registry.GetField("Resolver")!.SetValue(null, (Func<string, Type?>)(name => name == BstModule
                ? BeastmasterType : dhogTypes.SingleOrDefault(type => type.FullName == name)));
            if (provider == CombatProvider.Bmr)
            {
                var managerBuilder = module.DefineType("BossMod.AI.AIManager", TypeAttributes.Public);
                managerBuilder.DefineField("Instance", managerBuilder, FieldAttributes.Public | FieldAttributes.Static);
                managerBuilder.DefineField("Beh", typeof(object), FieldAttributes.Public);
                managerBuilder.DefineField("AiPreset", typeof(object), FieldAttributes.Public);
                managerBuilder.DefineField("MasterSlot", typeof(int), FieldAttributes.Public);
                managerBuilder.DefineField("WorldState", typeof(object), FieldAttributes.Public);
                foreach (var (name, callbackType, arguments) in new[]
                {
                    ("SetAIPreset", typeof(Action<object?>), new[] { typeof(object) }),
                    ("SwitchToFollow", typeof(Action<int>), new[] { typeof(int) }),
                    ("SwitchToIdle", typeof(Action), Type.EmptyTypes),
                })
                {
                    var callback = managerBuilder.DefineField(name + "Callback", callbackType, FieldAttributes.Public);
                    var method = managerBuilder.DefineMethod(name, MethodAttributes.Public, typeof(void), arguments);
                    var il = method.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, callback);
                    if (arguments.Length != 0) il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, callbackType.GetMethod("Invoke")!);
                    il.Emit(OpCodes.Ret);
                }
                var managerType = managerBuilder.CreateType()!;
                aiManager = Activator.CreateInstance(managerType)!;
                managerType.GetField("Instance")!.SetValue(null, aiManager);
                managerType.GetField("WorldState")!.SetValue(aiManager, World);
                managerType.GetField("SetAIPresetCallback")!.SetValue(aiManager, (Action<object?>)(value =>
                {
                    var preset = (NativePreset?)value;
                    Writes.Add("AI.SetAIPreset " + (preset?.Name ?? "<none>"));
                    AiPreset = preset;
                    Selector = preset?.Name;
                    if (managerType.GetField("Beh")!.GetValue(aiManager) is NativeAiBehavior behavior) behavior.AIPreset = preset;
                }));
                managerType.GetField("SwitchToIdleCallback")!.SetValue(aiManager, (Action)(() =>
                {
                    Writes.Add("AI.SwitchToIdle");
                    AiEnabled = false;
                    Rotation.Preset = null; // Frozen Idle always clears the runtime.
                }));
                managerType.GetField("SwitchToFollowCallback")!.SetValue(aiManager, (Action<int>)(slot =>
                {
                    Writes.Add("AI.SwitchToFollow " + slot);
                    AiEnabled = false;
                    Rotation.Preset = null;
                    var actualSlot = World.Party[slot]?.Name is not null ? slot : 0;
                    var preset = Presets.AllPresets.SingleOrDefault(p => p.Name == Selector);
                    AiPreset = preset;
                    node.GetType().GetField("Enabled")!.SetValue(node, true);
                    managerType.GetField("Beh")!.SetValue(aiManager, new NativeAiBehavior(preset));
                    AiMasterSlot = actualSlot;
                }));
            }
            Presets.AllPresets = new[] { "Original", "Other", "null", "none", "Literal##name###suffix", "Next", "External", "Reloaded", "VBM Multibox",
                "passive - tank", "passive - melee", "passive - ranged", "FRENRIDER - TANK", "FRENRIDER - MELEE", "FRENRIDER - RANGED" }
                .Select(name => new NativePreset(name, name == "VBM Multibox")).ToList();
            Presets.DefaultPresets = Presets.AllPresets.ToList();
            presetDefinitions["FRENRIDER - TANK"] = """{"Name":"FRENRIDER - TANK","Modules":{"BossMod.Autorotation.xan.TankAI":[]}}""";
            presetDefinitions["FRENRIDER - RANGED"] = """{"Name":"FRENRIDER - RANGED","Modules":{"BMRVariantModulePack.Autorotation.Dhog.DhogSGE":[]}}""";
            if (InternalName == "BossModReborn")
            {
                const string healerPreset = """
                    {"Name":"FRENRIDER - RANGED (GMC)","Modules":{"BMRVariantModulePack.Autorotation.Dhog.DhogSGE":[]}}
                    """;
                var preset = DeserializePreset(healerPreset);
                Presets.DefaultPresets.Add(preset);
                Presets.AllPresets.Add(preset);
                presetDefinitions[preset.Name] = healerPreset;
            }
            database = new NativeRotationDatabase(Presets);
            Rotation = new((NativeRotationDatabase)database, preset =>
            {
                ForceDisabled = ReferenceEquals(preset, NativePreset.ForceDisable);
                Active = preset is null || ForceDisabled ? [] : [preset.Name];
            });
            if (disabled) Rotation.Preset = NativePreset.ForceDisable;
            else if (Active.Count == 1)
                Rotation.Preset = Presets.AllPresets.FirstOrDefault(p => p.Name == Active[0]) ?? new NativePreset(Active[0]);
            if (provider == CombatProvider.Vbm)
            {
                var tickBuilder = module.DefineType("BossMod.Services.TickService", TypeAttributes.Public);
                tickBuilder.DefineField("_rotationDB", typeof(object), FieldAttributes.Public);
                var tickType = tickBuilder.CreateType()!;
                var tick = Activator.CreateInstance(tickType)!;
                tickType.GetField("_rotationDB")!.SetValue(tick, database);
                host = new NativeHost(new NativeTickServices(tick));
            }
            var pluginBuilder = module.DefineType("BossMod.Plugin", TypeAttributes.Public);
            pluginBuilder.DefineField("_rotationDB", typeof(object), FieldAttributes.Public);
            pluginBuilder.DefineField("_rotation", typeof(object), FieldAttributes.Public);
            pluginBuilder.DefineField("Host", typeof(object), FieldAttributes.Public);
            pluginType = pluginBuilder.CreateType()!;
            var wrapperBuilder = module.DefineType("Dalamud.Plugin.Internal.Types.LocalPlugin", TypeAttributes.Public,
                typeof(object), [typeof(IExposedPlugin)]);
            wrapperBuilder.DefineField("instance", typeof(object), FieldAttributes.Public);
            var loaded = wrapperBuilder.DefineField("Loaded", typeof(bool), FieldAttributes.Public);
            foreach (var method in typeof(IExposedPlugin).GetMethods())
            {
                var getter = wrapperBuilder.DefineMethod(method.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                    method.ReturnType, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
                var il = getter.GetILGenerator();
                if (method.Name == "get_InternalName")
                    il.Emit(OpCodes.Ldstr, InternalName);
                else if (method.Name == "get_IsLoaded")
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, loaded);
                }
                else if (method.ReturnType != typeof(void))
                {
                    if (method.ReturnType.IsValueType)
                    {
                        var local = il.DeclareLocal(method.ReturnType);
                        il.Emit(OpCodes.Ldloca, local);
                        il.Emit(OpCodes.Initobj, method.ReturnType);
                        il.Emit(OpCodes.Ldloc, local);
                    }
                    else il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Ret);
                wrapperBuilder.DefineMethodOverride(getter, method);
            }
            wrapper = Activator.CreateInstance(wrapperBuilder.CreateType()!)!;
            Loaded = true;
            Reload();
            Log = Fake<IPluginLog>((method, args) =>
            {
                if (method.Name == "Warning" && args?.FirstOrDefault() is string warning) Warnings.Add(warning);
                if (method.Name == "Debug" && args?.FirstOrDefault() is string message && message.Contains("Skipping save for temporary account", StringComparison.Ordinal)) AccountSaveAttempts++;
                return Default(method.ReturnType);
            });
            Interface = Fake<IDalamudPluginInterface>((method, args) => method.Name switch
            {
                "get_InstalledPlugins" => Ambiguous || AdditionalHost is not null ? new[] { (IExposedPlugin)wrapper, Fake<IExposedPlugin>((member, _) => member.Name switch
                {
                    "get_InternalName" => AdditionalHost ?? (provider == CombatProvider.Vbm ? "BossModReborn" : "BossMod"),
                    "get_IsLoaded" => true,
                    _ => Default(member.ReturnType),
                }) } : new[] { (IExposedPlugin)wrapper },
                "get_AssemblyLocation" => new FileInfo(typeof(BossModIPC).Assembly.Location),
                "GetIpcSubscriber" => Proxy(method.ReturnType, (call, values) => Invoke((string)args![0]!, call, values)),
                _ => Default(method.ReturnType),
            });
        }

        internal BossModIPC CreateService() => new(Interface, Log, Fake<ICommandManager>((method, args) =>
            method.Name == "ProcessCommand" ? SendCommand((string)args![0]!) : Default(method.ReturnType)));

        internal void Reload()
        {
            var plugin = Activator.CreateInstance(pluginType)!;
            pluginType.GetField("_rotationDB")!.SetValue(plugin, ready ? database : null);
            pluginType.GetField("_rotation")!.SetValue(plugin, ready ? Rotation : null);
            pluginType.GetField("Host")!.SetValue(plugin, host);
            wrapper.GetType().GetField("instance")!.SetValue(wrapper, plugin);
        }

        internal bool SendCommand(string command)
        {
            Writes.Add(command);
            if (InternalName == "GodMod")
            {
                if (command.StartsWith("/bmrai", StringComparison.Ordinal) || command.StartsWith("/vbmai", StringComparison.Ordinal)) return false;
                if (command.StartsWith("/gmai prefdistance ", StringComparison.Ordinal))
                    Distance = double.Parse(command["/gmai prefdistance ".Length..], CultureInfo.InvariantCulture);
                else if (command is "/gmai manualtarget on" or "/gmai manualtarget off") ManualTarget = command.EndsWith(" on", StringComparison.Ordinal);
                else if (!RejectAiEffect && command is "/gmai on" or "/gmai off")
                {
                    AiEnabled = command.EndsWith(" on", StringComparison.Ordinal);
                    if (AiEnabled) Rotation.Preset = Presets.AllPresets.SingleOrDefault(preset => preset.Name == Selector);
                    else if (Active.Count == 1 && Active[0] == Selector) Rotation.Preset = null;
                }
                return true;
            }
            if (command.StartsWith("/bmrai prefdistance ", StringComparison.Ordinal))
                Distance = double.Parse(command["/bmrai prefdistance ".Length..], CultureInfo.InvariantCulture);
            else if (command is "/bmrai manualtarget on" or "/bmrai manualtarget off")
                ManualTarget = command.EndsWith(" on", StringComparison.Ordinal);
            else if (!RejectAiEffect && command is "/bmrai on" or "/bmrai off")
            {
                AiEnabled = command.EndsWith(" on", StringComparison.Ordinal);
                Rotation.Preset = null;
                Active.Clear();
                ForceDisabled = false;
            }
            else if (!RejectAiEffect && command is "/vbmai on" or "/vbmai off")
                AiEnabled = command.EndsWith(" on", StringComparison.Ordinal);
            return true;
        }

        private NativePreset? FindGodModSuitePreset(string name)
            => InternalName == "GodMod" && name.Equals("FRENRIDER - MELEE", StringComparison.OrdinalIgnoreCase)
                ? Presets.AllPresets.FirstOrDefault(preset => preset.Name == name)
                : null;

        internal object? Invoke(string channel, MethodInfo call, object?[]? values)
        {
            if (InternalName == "GodMod" && channel is "BossMod.Presets.GetActiveList" or "BossMod.Presets.SetActiveList")
                throw new InvalidOperationException("GodMod exposes a single active preset");
            if (UnavailableChannel == channel) throw new InvalidOperationException("native endpoint unavailable");
            if (channel == "BossMod.Presets.ListAll") return Presets.AllPresets.Select(preset => preset.Name).ToList();
            if (channel == "BossMod.Presets.GetForceDisabled") return ForceDisabled;
            if (channel == "BossMod.Presets.GetActive") return Active.Count == 1 ? Active[0] : null;
            if (channel == "BossMod.Presets.GetActiveList") return Active.ToList();
            if (channel == "BossMod.Presets.Get")
            {
                var name = (string)values![0]!;
                var found = FindGodModSuitePreset(name) ?? Presets.AllPresets.FirstOrDefault(preset =>
                    string.Equals(preset.Name, name, StringComparison.CurrentCultureIgnoreCase));
                return found is null ? null : RegularBeastmasterAvailable ? SerializePreset(found)
                    : presetDefinitions.GetValueOrDefault(found.Name) ?? JsonSerializer.Serialize(new { found.Name });
            }
            if (channel == "BossMod.Configuration")
            {
                var arguments = (List<string>)values![0]!;
                if (arguments[1] is "ForbidActions" or "ForbidMovement")
                {
                    var field = node.GetType().GetField(arguments[1])!;
                    if ((bool)values[1]!)
                    {
                        Writes.Add("Configuration " + arguments[1] + " " + arguments[2]);
                        field.SetValue(node, bool.Parse(arguments[2]));
                    }
                    return new List<string> { field.GetValue(node)!.ToString()! };
                }
                if (arguments[1] == "ManualTarget")
                {
                    if ((bool)values[1]!)
                    {
                        Writes.Add("Configuration ManualTarget " + arguments[2]);
                        ManualTarget = bool.Parse(arguments[2]);
                    }
                    return new List<string> { ManualTarget.ToString() };
                }
                if ((bool)values[1]!)
                {
                    Writes.Add("Configuration " + arguments[2]);
                    Distance = double.Parse(arguments[2], CultureInfo.CurrentCulture);
                }
                return new List<string> { Distance.ToString("R", CultureInfo.CurrentCulture) };
            }
            Writes.Add(channel + (values?.FirstOrDefault() is string literalName ? " " + literalName : string.Empty));
            if (channel == "BossMod.AI.SetPreset")
            {
                Assert.Equal("InvokeAction", call.Name);
                if (!RejectSelectorWrite)
                {
                    var name = (string)values![0]!;
                    Selector = (FindGodModSuitePreset(name) ?? Presets.AllPresets.FirstOrDefault(preset =>
                        string.Equals(preset.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))?.Name;
                }
                AfterSelectorWrite?.Invoke();
                if (ThrowAfterSelectorWrite) throw new InvalidOperationException("native selector changed before callback failure");
                return null;
            }
            if (RejectRuntimeWrite) return false;
            if (channel == "BossMod.Presets.SetActive")
            {
                Rotation.Preset = Presets.AllPresets.Single(preset => preset.Name == (string)values![0]!);
                Active = [(string)values![0]!]; ForceDisabled = false;
            }
            else if (channel == "BossMod.Presets.SetActiveList") { Active = ((List<string>)values![0]!).ToList(); ForceDisabled = false; }
            else if (channel == "BossMod.Presets.ClearActive") { Rotation.Preset = null; Active.Clear(); ForceDisabled = false; }
            else if (channel == "BossMod.Presets.SetForceDisabled") Rotation.Preset = NativePreset.ForceDisable;
            else throw new InvalidOperationException("unexpected native endpoint " + channel);
            if (ThrowAfterRuntimeWrite) throw new InvalidOperationException("native write completed before endpoint failure");
            return true;
        }

        internal static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = DispatchProxy.Create(type, typeof(Stub));
            ((Stub)proxy).Handler = handler;
            return proxy;
        }
    }

    private static void Set(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object TerritoryRow(uint id)
    {
        object row = default(RowRef<TerritoryType>);
        typeof(RowRef<TerritoryType>).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(uint)).SetValue(row, id);
        return row;
    }
    private static object? Get(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Invoke(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var fake = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)fake).Handler = handler;
        return fake;
    }
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    public class Stub : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => Handler == null ? Default(method!.ReturnType) : Handler(method!, args);
    }
}
