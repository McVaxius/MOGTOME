using System.Reflection;
using System.Reflection.Emit;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
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
    private static readonly AdsHandoffReadinessConditions Ready = new(true, true, true, false, false, false, false);

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

    [Fact]
    public async Task StopBeforeInitializationCallbackCannotIssueStartupCommands()
    {
        using var run = new Run();
        run.Engine.Start();
        var pending = (Task)Get(run.Engine, "startupTask")!;
        run.Engine.Stop();
        var commandsAfterStop = run.Commands.ToArray();
        run.Pump();
        await pending;
        Assert.Equal(commandsAfterStop, run.Commands);
        Assert.False(run.Engine.IsRunning);
        Assert.Equal("Manual Stop.", run.Engine.LastStopReason);
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
    public async Task InnDestinationsPreserveSavedValuesAndWaitForAdsTerminalResult()
    {
        var destinations = new[] { "uldah", "gridania", "limsa", "ishgard", "crystarium", "sharlayan", "tuliyollal" };
        Assert.Equal(0, (int)AdsRepairMode.Npc);
        Assert.Equal(1, (int)AdsRepairMode.Self);
        Assert.Equal(2, (int)AdsRepairMode.NpcYesInn);
        using var run = new Run();
        for (var index = 2; index <= 9; index++)
        {
            var mode = (AdsRepairMode)index;
            var suffix = index == 2 ? "" : " " + destinations[index - 3];
            Assert.Equal("/ads npcrepair yesinn" + suffix, RepairService.GetAdsRepairCommand(mode));
            foreach (var language in Enum.GetValues<MOGTOME.Localization.UiLanguage>())
            {
                var original = MOGTOME.Localization.Ui.Language;
                try
                {
                    MOGTOME.Localization.Ui.SetLanguage(language);
                    var key = RepairService.GetAdsRepairLabelKey(mode);
                    Assert.NotEqual(key, MOGTOME.Localization.Ui.T(key));
                    if (index > 2) Assert.Contains(" — ", MOGTOME.Localization.Ui.T(key));
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
            Assert.Equal(index == 2 ? "npc-yes-inn" : "npc-yes-inn-" + destinations[index - 3], run.RepairModes[^1]);
            Assert.True(run.Automation.IsAdsInnRepairPending(out failure));
            Assert.Empty(failure);
            Assert.DoesNotContain(run.Commands, command => command.StartsWith("/ads npcrepair"));

            string Terminal(DateTime time, string success, string error) => System.Text.Json.JsonSerializer.Serialize(new
            { utilityRunning = false, utilityCompletedAtUtc = time, utilityLastSuccess = success, utilityLastFailure = error });
            run.RepairStatus = Terminal(DateTime.UtcNow.AddMinutes(-5), "old entry", "");
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
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.False(service.ApplyOwnedBossModPreset("null"));
        Assert.Equal(new[] { "Original" }, native.Active);
        Assert.Equal("Original", native.Selector);
        Assert.Empty(native.Writes);
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
    public void BossModPackagedRefreshRestoresCompleteSelectionAndPreservesUnexpectedEdits(CombatProvider provider)
    {
        var names = provider == CombatProvider.Bmr ? "FRENRIDER - TANK" : "FRENRIDER - TANK|Other";
        var native = new NativeBossModProvider(provider, names);
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        Assert.True(service.RefreshPackagedPresets());
        Assert.Equal(names.Split('|'), native.Active);
        native.AfterPackagedRefresh = () => native.Active = ["External"];
        Assert.False(service.RefreshPackagedPresets());
        Assert.Equal(new[] { "External" }, native.Active);
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(new[] { "External" }, native.Active);
    }

    [Theory]
    [InlineData(CombatProvider.Bmr)]
    [InlineData(CombatProvider.Vbm)]
    public void BossModPackagedRefreshCannotContinueInAReentrantReplacementSession(CombatProvider provider)
    {
        var native = new NativeBossModProvider(provider,
            provider == CombatProvider.Bmr ? "FRENRIDER - TANK" : "FRENRIDER - TANK|Other");
        using var service = native.CreateService();
        Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        native.AfterPackagedRefresh = () =>
        {
            native.Reload();
            Assert.True(service.BeginOwnedBossModSettings(provider, "account", "character", new Configuration()));
        };
        native.Writes.Clear();

        Assert.False(service.RefreshPackagedPresets());

        var remaining = provider == CombatProvider.Bmr ? Array.Empty<string>() : new[] { "Other" };
        Assert.Equal(remaining, native.Active);
        Assert.DoesNotContain(native.Writes, write => write.StartsWith("BossMod.Presets.SetActive", StringComparison.Ordinal));
        Assert.True(service.BeginOwnedBossModCleanup());
        Assert.True(service.ClearActivePreset());
        Assert.Equal(remaining, native.Active);
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
            startup.ObserveReadiness(true, (1044, 16), Ready with { IsPlayerAlive = false, IsUnconscious = true }, now.AddSeconds(3));
            native.AiEnabled = false;
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

    private sealed class Run : IDisposable
    {
        internal readonly Configuration Config = new() { UseAdsExperimental = true, CombatProvider = CombatProvider.Wrath, EnableDetailedTracking = true, BailoutTimeout = 100 };
        internal readonly DutyState State = new();
        internal readonly List<string> Commands = [];
        internal readonly MogtomeEngine Engine;
        internal readonly DutyQueueService Queue;
        internal readonly DutyAutomationService Automation;
        internal readonly RotationService Rotation;
        internal readonly YesAlreadyIPC YesAlready;
        internal readonly RunHistoryService History;
        internal readonly ConfigManager Manager;
        internal readonly BossModIPC BossMod;
        internal ulong CurrentContentId;
        internal uint[] PartyTerritories = [];
        internal string? ThrowOnCommand;
        internal readonly List<string> RepairModes = [];
        internal string RepairStatus = "{}";
        internal bool AcceptRepair = true;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> scheduled = new();
        private readonly Dictionary<string, object?> originalStatics = new();

        internal Run(NativeBossModProvider? native = null)
        {
            CurrentContentId = native is null ? 0UL : 1UL;
            if (native is not null) Config.CombatProvider = native.Provider;
            var log = native?.Log ?? Fake<IPluginLog>();
            var client = Fake<IClientState>((method, _) => method.Name == "get_IsLoggedIn" ? true : Default(method.ReturnType));
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
            var pi = Fake<IDalamudPluginInterface>((method, args) =>
            {
                if (method.Name == "GetOrCreateData") return requests;
                if (method.Name == "get_InstalledPlugins") return native?.Interface.InstalledPlugins ?? Array.Empty<IExposedPlugin>();
                if (native is not null && method.Name == "get_AssemblyLocation") return new FileInfo(typeof(BossModIPC).Assembly.Location);
                if (native is not null && method.Name == "GetIpcSubscriber" && args![0] is string nativeName && nativeName.StartsWith("BossMod", StringComparison.Ordinal))
                    return NativeBossModProvider.Proxy(method.ReturnType, (call, values) => native.Invoke(nativeName, call, values));
                if (method.Name == "GetIpcSubscriber" && args![0] is string name && name.StartsWith("ADS."))
                {
                    object? Call(MethodInfo member, object?[]? values)
                    {
                        if (member.Name != "InvokeFunc") return Default(member.ReturnType);
                        if (name == "ADS.GetStatusJson") return RepairStatus;
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
            SetStatic("ChatGui", Fake<IChatGui>()); SetStatic("ToastGui", Fake<IToastGui>()); SetStatic("DutyStateService", Fake<IDutyState>());
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

    private sealed record NativePreset(string Name, bool HiddenByDefault = false);
    private sealed class NativePresetDatabase
    {
        public List<NativePreset> AllPresets { get; set; } = [];
        public List<NativePreset> DefaultPresets { get; set; } = [];
        public List<NativePreset> UserPresets { get; set; } = [];
    }
    private sealed record NativeRotationDatabase(NativePresetDatabase Presets);
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
        internal string? UnavailableChannel;
        internal double Distance = 7.5;
        internal int AccountSaveAttempts;
        internal Action? AfterPackagedRefresh;
        internal Action? AfterSelectorWrite;
        private readonly object node;
        private readonly object? aiManager;
        private readonly object wrapper;
        private readonly Type pluginType;
        private readonly object database;
        private readonly object? host;

        internal bool Loaded
        {
            get => (bool)wrapper.GetType().GetField("Loaded")!.GetValue(wrapper)!;
            set => wrapper.GetType().GetField("Loaded")!.SetValue(wrapper, value);
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
                aiManager?.GetType().GetField("Beh")!.SetValue(aiManager, value ? new object() : null);
            }
        }

        internal NativeBossModProvider(CombatProvider provider, string names, bool disabled = false)
        {
            Provider = provider;
            Active = names.Length == 0 ? [] : names.Split('|').ToList();
            ForceDisabled = disabled;
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MogtomeNativeBossMod" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.Run).DefineDynamicModule("NativeProvider");
            var configNodeType = module.DefineType("BossMod.ConfigNode", TypeAttributes.Public | TypeAttributes.Abstract).CreateType()!;
            var nodeBuilder = module.DefineType("BossMod.AI.AIConfig", TypeAttributes.Public, configNodeType);
            nodeBuilder.DefineField("Enabled", typeof(bool), FieldAttributes.Public);
            nodeBuilder.DefineField("AIAutorotPresetName", typeof(string), FieldAttributes.Public);
            var nodeType = nodeBuilder.CreateType()!;
            node = Activator.CreateInstance(nodeType)!;
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
            configType.GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(configRoot, nodes);
            var serviceBuilder = module.DefineType("BossMod.Service", TypeAttributes.Public);
            serviceBuilder.DefineField("Config", configType, FieldAttributes.Public | FieldAttributes.Static);
            serviceBuilder.CreateType()!.GetField("Config")!.SetValue(null, configRoot);
            if (provider == CombatProvider.Bmr)
            {
                var managerBuilder = module.DefineType("BossMod.AI.AIManager", TypeAttributes.Public);
                managerBuilder.DefineField("Instance", managerBuilder, FieldAttributes.Public | FieldAttributes.Static);
                managerBuilder.DefineField("Beh", typeof(object), FieldAttributes.Public);
                var managerType = managerBuilder.CreateType()!;
                aiManager = Activator.CreateInstance(managerType)!;
                managerType.GetField("Instance")!.SetValue(null, aiManager);
            }
            Presets.AllPresets = new[] { "Original", "Other", "null", "none", "Literal##name###suffix", "Next", "External", "Reloaded", "VBM Multibox",
                "passive - tank", "passive - melee", "passive - ranged", "FRENRIDER - TANK", "FRENRIDER - MELEE", "FRENRIDER - RANGED" }
                .Select(name => new NativePreset(name, name == "VBM Multibox")).ToList();
            Presets.DefaultPresets = Presets.AllPresets.ToList();
            database = new NativeRotationDatabase(Presets);
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
                    il.Emit(OpCodes.Ldstr, provider == CombatProvider.Vbm ? "BossMod" : "BossModReborn");
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
                "get_InstalledPlugins" => Ambiguous ? new[] { (IExposedPlugin)wrapper, Fake<IExposedPlugin>((member, _) => member.Name switch
                {
                    "get_InternalName" => provider == CombatProvider.Vbm ? "BossModReborn" : "BossMod",
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
            pluginType.GetField("_rotationDB")!.SetValue(plugin, database);
            pluginType.GetField("Host")!.SetValue(plugin, host);
            wrapper.GetType().GetField("instance")!.SetValue(wrapper, plugin);
        }

        internal bool SendCommand(string command)
        {
            Writes.Add(command);
            if (command.StartsWith("/bmrai prefdistance ", StringComparison.Ordinal))
                Distance = double.Parse(command["/bmrai prefdistance ".Length..], CultureInfo.InvariantCulture);
            else if (!RejectAiEffect && command is "/bmrai on" or "/bmrai off")
            {
                AiEnabled = command.EndsWith(" on", StringComparison.Ordinal);
                Active.Clear();
                ForceDisabled = false;
            }
            else if (!RejectAiEffect && command is "/vbmai on" or "/vbmai off")
                AiEnabled = command.EndsWith(" on", StringComparison.Ordinal);
            return true;
        }

        internal object? Invoke(string channel, MethodInfo call, object?[]? values)
        {
            if (UnavailableChannel == channel) throw new InvalidOperationException("native endpoint unavailable");
            if (channel == "BossMod.Presets.GetForceDisabled") return ForceDisabled;
            if (channel == "BossMod.Presets.GetActive") return Active.Count == 1 ? Active[0] : null;
            if (channel == "BossMod.Presets.GetActiveList") return Active.ToList();
            if (channel == "BossMod.Presets.Get")
            {
                var found = Presets.AllPresets.FirstOrDefault(preset => string.Equals(preset.Name, (string)values![0]!, StringComparison.CurrentCultureIgnoreCase));
                return found is null ? null : JsonSerializer.Serialize(new { found.Name });
            }
            if (channel == "BossMod.Configuration")
            {
                var arguments = (List<string>)values![0]!;
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
                    Selector = Presets.AllPresets.FirstOrDefault(preset => string.Equals(preset.Name.Trim(), ((string)values![0]!).Trim(), StringComparison.OrdinalIgnoreCase))?.Name;
                AfterSelectorWrite?.Invoke();
                if (ThrowAfterSelectorWrite) throw new InvalidOperationException("native selector changed before callback failure");
                return null;
            }
            if (channel == "BossMod.Presets.Delete")
            {
                var name = (string)values![0]!;
                Presets.AllPresets.RemoveAll(preset => preset.Name == name);
                Presets.DefaultPresets.RemoveAll(preset => preset.Name == name);
                Active.RemoveAll(preset => preset == name);
                return true;
            }
            if (channel == "BossMod.Presets.Create")
            {
                using var json = JsonDocument.Parse((string)values![0]!);
                var preset = new NativePreset(json.RootElement.GetProperty("Name").GetString()!);
                Presets.AllPresets.Add(preset);
                Presets.DefaultPresets.Add(preset);
                if (preset.Name == "FRENRIDER - RANGED") AfterPackagedRefresh?.Invoke();
                return true;
            }
            if (RejectRuntimeWrite) return false;
            if (channel == "BossMod.Presets.SetActive") { Active = [(string)values![0]!]; ForceDisabled = false; }
            else if (channel == "BossMod.Presets.SetActiveList") { Active = ((List<string>)values![0]!).ToList(); ForceDisabled = false; }
            else if (channel == "BossMod.Presets.ClearActive") { Active.Clear(); ForceDisabled = false; }
            else if (channel == "BossMod.Presets.SetForceDisabled") { Active.Clear(); ForceDisabled = true; }
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
