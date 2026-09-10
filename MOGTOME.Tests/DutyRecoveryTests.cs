using System.Reflection;
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

    [Theory]
    [InlineData("Do you wish to leave the duty?", true)]
    [InlineData("Are you sure you wish to abandon the duty?", true)]
    [InlineData("Would you like to be raised?", false)]
    [InlineData("Return to the starting point?", false)]
    [InlineData("Move immediately to the sealed area?", false)]
    [InlineData("Accept this party invitation?", false)]
    public void OnlyActualLeavePromptsAreExitEvidence(string prompt, bool leave)
        => Assert.Equal(leave, GameHelpers.IsLeaveDutyPrompt(prompt));

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
        internal uint[] PartyTerritories = [];
        internal string? ThrowOnCommand;
        private readonly Queue<Action> scheduled = new();
        private readonly Dictionary<string, object?> originalStatics = new();

        internal Run()
        {
            var log = Fake<IPluginLog>();
            var client = Fake<IClientState>((method, _) => method.Name == "get_IsLoggedIn" ? true : Default(method.ReturnType));
            var condition = Fake<ICondition>();
            var objects = Fake<IObjectTable>();
            var player = Fake<IPlayerState>();
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
            var pi = Fake<IDalamudPluginInterface>((method, _) => method.Name == "GetOrCreateData" ? requests : Default(method.ReturnType));
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
                return true;
            });
            SetStatic("Log", log); SetStatic("ClientState", client); SetStatic("Condition", condition);
            SetStatic("ObjectTable", objects); SetStatic("PlayerState", player); SetStatic("PartyList", party);
            SetStatic("Framework", framework); SetStatic("PluginInterface", pi); SetStatic("GameGui", Fake<IGameGui>());
            SetStatic("ChatGui", Fake<IChatGui>()); SetStatic("ToastGui", Fake<IToastGui>()); SetStatic("DutyStateService", Fake<IDutyState>());
            var manager = (ConfigManager)RuntimeHelpers.GetUninitializedObject(typeof(ConfigManager));
            Set(manager, "log", log);
            Set(manager, "currentAccountId", "temporary");
            Set(manager, "accounts", new Dictionary<string, AccountConfig> { ["temporary"] = new() { Settings = Config } });
            var deaths = new DeathTrackingService(log, framework, objects, party, player, condition, client);
            History = new RunHistoryService(log, Config, State, player, manager, null!, deaths);
            var autoDuty = new AutoDutyIPC(log, command, History);
            Automation = new DutyAutomationService(log, manager, autoDuty, null!, null!, command, History);
            Queue = new DutyQueueService(log, State, Automation, condition, manager);
            Rotation = new RotationService(log, manager, new BossModIPC(pi, log, command));
            YesAlready = new YesAlreadyIPC(log);
            var dialog = new DialogHandlerService(log, YesAlready, command, Fake<IGameGui>());
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
            History.Dispose();
            Pump();
            foreach (var (name, value) in originalStatics)
                typeof(Plugin).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
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
