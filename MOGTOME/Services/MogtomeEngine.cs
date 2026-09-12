using MOGTOME.Localization;
using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public enum EngineState
{
    Idle,
    Initializing,
    WaitingOutsideDuty,
    Queueing,
    InDuty,
    RepairingOutside,
    Stopping,
    Stopped,
}

public class MogtomeEngine
{
    private readonly record struct StartupSnapshot(bool StartingInsideDuty, bool IsPartyLeader);
    private readonly record struct StartupPreparationResult(bool EnteredRepairMode);

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly DutyTrackerService dutyTracker;
    private readonly DutyQueueService dutyQueue;
    private readonly RepairService repairService;
    private readonly FoodService foodService;
    private readonly ConsumableInventoryService consumableInventoryService;
    private readonly RotationService rotationService;
    private readonly BossHandlerService bossHandler;
    private readonly StuckDetectionService stuckDetection;
    private readonly DialogHandlerService dialogHandler;
    private readonly DutyAutomationService dutyAutomationService;
    private readonly AutoDutyPathService autoDutyPath;
    private readonly ConflictPluginService conflictPluginService;
    private readonly RunHistoryService runHistoryService; // NEW
    private readonly DeathTrackingService deathTrackingService;
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly YesAlreadyIPC yesAlreadyIPC;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly Action cancelQueuedStart;
    private readonly PraetoriumFirstRoomSkipService firstRoomSkip;
    private readonly DutyStartupService dutyStartup;
    private bool firstRoomSkipAttempted;
    private bool confirmedDutyExitPending;
    private int sessionId;
    private int leaveOperationId;
    private bool bailoutExitPending;
    private bool logoutObserved;

    public EngineState CurrentState { get; private set; } = EngineState.Idle;
    public bool IsRunning => CurrentState != EngineState.Idle && CurrentState != EngineState.Stopped;
    public string LastStopReason => StopReason.English;
    public UiText StopReason { get; private set; } = Ui.M("Engine_HasNotBeenStartedSinceLoading");
    public string StatusMessage => Status.English;
    public UiText Status { get; private set; } = Ui.M("Engine_HasNotBeenStartedSinceLoading");
    private Task? startupTask;
    public bool IsStartupPending => startupTask is { IsCompleted: false };
    public bool StopAfterNextSuccessfulRunArmed { get; private set; }

    private DateTime lastTick = DateTime.MinValue;
    private int outsideDutyTicks = 0;
    private const float LoopInterval = 2.0f;
    private bool autoDutyStartedInDuty = false;
    private DateTime dutyEnteredUtc = DateTime.MinValue;

    // Duty exit tracking
    private bool dutyCompleted = false;
    private DateTime dutyCompletedTime;
    private DateTime lastLeaveAttemptTime = DateTime.MinValue;
    private int leaveAttemptCount = 0;
    private DateTime leaveRequestedUtc = DateTime.MinValue;
    private bool leaveConfirmationObserved = false;
    private string lastLeaveBlocker = string.Empty;
    private const int DutyExitSettleSeconds = 10;
    private bool delayedRequeueInProgress = false;

    // Requeue state machine
    private bool requeueInProgress = false;
    private DateTime requeueStartTime = DateTime.MinValue;
    private int requeueAttempts = 0;
    private const int MaxRequeueAttempts = 5;
    private const float RequeueRetryInterval = 10.0f;
    private RequeueState requeueState = RequeueState.Idle;
    private DateTime repairRecoveryWatchStartedUtc = DateTime.MinValue;
    private DateTime repairRecoveryRetryReadyUtc = DateTime.MinValue;
    private int repairRecoveryAttempts = 0;
    private const float RepairRecoveryWatchdogSeconds = 120.0f;
    private const float RepairRecoveryStopDelaySeconds = 2.0f;
    private DateTime lastRepairRequestUtc = DateTime.MinValue;
    private int repairRequestAttempts = 0;
    private bool activeRepairUsesNpc = false;
    private const float RepairRequestRetrySeconds = 10.0f;
    private DateTime lastRepairRetryBlockedLogUtc = DateTime.MinValue;
    private string lastRepairRetryBlockedReason = string.Empty;
    private const float RepairRetryBlockedLogSeconds = 15.0f;
    private DateTime telepotTownVisibleSinceUtc = DateTime.MinValue;
    private const string TelepotTownAddon = "TelepotTown";
    private const float TelepotTownRepairCancelSeconds = 10.0f;
    private bool sawQueueConditionOutsideDuty = false;
    private bool pendingQueueRecoveryAfterRepair = false;
    private DateTime queueRecoveryStopUntilUtc = DateTime.MinValue;
    private DateTime queueRecoveryResumeUtc = DateTime.MinValue;
    private const int QueueConditionIndex = 91;
    private const int WaitingForDutyConditionIndex = 55;
    private const int WaitingForDutyFinderConditionIndex = 59;
    private const float QueueRecoveryStopSeconds = 10.0f;
    private const float QueueRecoveryRepairGraceSeconds = 30.0f;
    private const float QueueRegistrationWatchdogSeconds = 30.0f;
    private DateTime queueRegistrationStartedUtc = DateTime.MinValue;
    private DateTime dutyTerritoryWaitStartedUtc = DateTime.MinValue;
    private DateTime lastDutyTerritoryWaitLogUtc = DateTime.MinValue;
    private DateTime lastQueueAcceptanceEvidenceUtc = DateTime.MinValue;
    private const int BoundByDuty56ConditionIndex = 56;
    private const float DutyTerritorySettleSeconds = 12.0f;
    private const float RecentQueueAcceptanceEvidenceSeconds = 20.0f;

    public enum RequeueState
    {
        Idle,
        WaitingAfterLeave,  // Wait 10s after leaving duty to prevent crashes
        WaitingToStop,     // Wait 2s after leaving duty
        StoppingBackend,    // Execute backend stop command
        WaitingToQueue,    // Wait 1s after stop
        Queueing,           // Execute queue command
        Complete,           // Successfully queued
        Failed              // Max attempts reached
    }

    public MogtomeEngine(
        IPluginLog log, Configuration config, DutyState state,
        DutyTrackerService dutyTracker, DutyQueueService dutyQueue,
        RepairService repairService, FoodService foodService, ConsumableInventoryService consumableInventoryService,
        RotationService rotationService, BossHandlerService bossHandler,
        StuckDetectionService stuckDetection, DialogHandlerService dialogHandler,
        DutyAutomationService dutyAutomationService,
        AutoDutyPathService autoDutyPath, ConflictPluginService conflictPluginService, RunHistoryService runHistoryService, // NEW
        DeathTrackingService deathTrackingService,
        AutoDutyIPC autoDutyIPC, YesAlreadyIPC yesAlreadyIPC, VNavIPC vnavIPC,
        ICondition condition, IClientState clientState, ICommandManager commandManager, Action cancelQueuedStart)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.dutyTracker = dutyTracker;
        this.dutyQueue = dutyQueue;
        this.repairService = repairService;
        this.foodService = foodService;
        this.consumableInventoryService = consumableInventoryService;
        this.rotationService = rotationService;
        this.bossHandler = bossHandler;
        this.stuckDetection = stuckDetection;
        this.dialogHandler = dialogHandler;
        this.dutyAutomationService = dutyAutomationService;
        this.autoDutyPath = autoDutyPath;
        this.conflictPluginService = conflictPluginService;
        this.runHistoryService = runHistoryService; // NEW
        this.deathTrackingService = deathTrackingService;
        this.autoDutyIPC = autoDutyIPC;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.condition = condition;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.cancelQueuedStart = cancelQueuedStart;
        dutyStartup = new DutyStartupService(
            new AdsDutyIpcService(Plugin.PluginInterface, log),
            () => dutyAutomationService.UseAdsExperimental,
            autoDutyIPC.StartDuty,
            () => rotationService.EnableRotationOncePerDuty("ready in-duty startup"),
            () => rotationService.Failure,
            commandManager.ProcessCommand,
            GameHelpers.GetDutyRemainingTime,
            message => log.Information(message), message => log.Warning(message),
            rotationService.InvalidateCombatActivation);
        firstRoomSkip = new PraetoriumFirstRoomSkipService(
            log,
            Plugin.Framework,
            condition,
            clientState,
            Plugin.ObjectTable,
            vnavIPC,
            OnFirstRoomSkipFinished, () => sessionId);

        // Hook duty events
        Plugin.DutyStateService.DutyStarted += OnDutyStarted;
        Plugin.DutyStateService.DutyCompleted += OnDutyCompleted;
        ApplyConfiguredPartyLeaderState(reason: "engine init");
    }

    public void Dispose()
    {
        if (IsRunning)
            StopWithReason(Ui.M("Engine_PluginUnloadedOrReloaded"));
        else
        {
            ++sessionId;
            cancelQueuedStart();
            ResetLeaveTracking();
            dutyAutomationService.CancelPendingOperations("plugin unload");
            CleanupStep("unload combat", () => rotationService.DisableRotationForDutyEnd("plugin unload"));
        }
        dutyStartup.Cancel();
        firstRoomSkip.Dispose();
        Plugin.DutyStateService.DutyCompleted -= OnDutyCompleted;
        Plugin.DutyStateService.DutyStarted -= OnDutyStarted;
    }

    private void OnDutyStarted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyStarted(args.TerritoryType.RowId);

    private void OnDutyStarted(uint territoryId)
    {
        if (!IsRunning || !IsMogtomeDutyTerritory(territoryId) || dutyCompleted)
            return;

        var identity = DutyStartupService.ReadLiveDutyIdentity();
        if (!clientState.IsLoggedIn || !DutyStartupService.IsInDuty() || identity.TerritoryTypeId != territoryId
            || !DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
            return;

        dutyStartup.OnDutyStarted(territoryId, DateTime.UtcNow);
        state.DutyStartTerritory = territoryId;
        if (dutyEnteredUtc == DateTime.MinValue)
            dutyEnteredUtc = DateTime.UtcNow;

        // All events use the same flow as normal entry/resume. No delayed action
        // can outlive Stop, completion, or a subsequent duty session.
        StartDutyBackendInsideDuty($"DutyStarted territory {territoryId}");
    }

    private bool TryStartFirstRoomSkip()
    {
        if (firstRoomSkipAttempted || !dutyAutomationService.UseAdsExperimental ||
            !config.ExperimentalFirstRoomSkip ||
            clientState.TerritoryType != DutyState.PraetoriumTerritoryId)
            return false;

        firstRoomSkipAttempted = true;
        return firstRoomSkip.TryStart();
    }

    private void OnFirstRoomSkipFinished(UiText reason)
    {
        if (!IsRunning || dutyCompleted || bailoutExitPending)
            return;

        // The opener can finish synchronously from TryStart. Resume on the next
        // engine update instead of re-entering the handoff in its callback.
        Status = Ui.M("Engine_DutyStartupPendingAfterExperimentalOpener", reason);
        lastTick = DateTime.MinValue;
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        if (DutyState.IsSupportedDutyIdentity(args.TerritoryType.RowId, args.ContentFinderCondition.RowId))
            OnDutyCompleted(args.TerritoryType.RowId);
    }

    private void OnDutyCompleted(uint territoryId)
    {
        if (!IsRunning || dutyCompleted) return;
        var identity = DutyStartupService.ReadLiveDutyIdentity();
        if (!clientState.IsLoggedIn || !DutyStartupService.IsInDuty()
            || identity.TerritoryTypeId != territoryId || state.DutyStartTerritory != territoryId
            || !DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
            return;
        var now = DateTime.UtcNow;
        if (!dutyStartup.OnDutyCompleted(territoryId, now))
            return;

        dutyCompleted = true;
        dutyCompletedTime = now;
        bailoutExitPending = false;
        dutyTracker.CaptureCompletionRemainingTime();
        CleanupStep("completed-duty opener", () => firstRoomSkip.Cancel("duty completed"));
        CleanupStep("completed-duty combat", () => rotationService.DisableRotationForDutyEnd($"duty completed territory {territoryId}"));
        dialogHandler.ResetReturnPromptWait();
        ResetLeaveTracking();
        PauseLeaderQueueBeforeExitIfRepairNeeded($"Duty completed in territory {territoryId}");
        log.Information($"[MOGTOME][Engine] Duty completed event in territory {territoryId} - leave will request at first safe seam");
    }

    public void Start()
    {
        if (IsRunning || IsStartupPending)
        {
            log.Warning("[MOGTOME][Engine] Already running or finishing previous startup cleanup");
            return;
        }

        log.Information("[MOGTOME][Engine] Starting MOGTOME engine");
        var currentSession = ++sessionId;
        dutyStartup.ResetSession(DutyStartupService.IsInDuty());
        confirmedDutyExitPending = false;
        bailoutExitPending = false;
        firstRoomSkipAttempted = false;
        CurrentState = EngineState.Initializing;
        Status = Ui.M("Engine_Initializing");

        startupTask = StartCoreAsync(currentSession);
    }

    private bool IsCurrentStartup(int operation) => operation == sessionId && CurrentState == EngineState.Initializing && clientState.IsLoggedIn;

    private async Task StartCoreAsync(int operation)
    {
        var testingModeUnsynced = config.TestingModeUnsynced;

        try
        {
            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (IsCurrentStartup(operation)) ClearStaleDutyStateIfNeeded();
            }).ConfigureAwait(false);
            if (!IsCurrentStartup(operation)) return;

            Status = Ui.M("Engine_PreparingBossModSupport");
            var bossModReady = await conflictPluginService.EnsureBossModReadyAsync(() => IsCurrentStartup(operation)).ConfigureAwait(false);
            if (!bossModReady.Ready)
            {
                await GameHelpers.RunOnFrameworkThreadAsync(() => { if (IsCurrentStartup(operation)) StopWithCombatFailureMessage(bossModReady.Reason); }).ConfigureAwait(false);
                return;
            }
            if (!IsCurrentStartup(operation))
                return;

            var rotationReady = await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!IsCurrentStartup(operation))
                    return false;
                return rotationService.Initialize(bossModReady.PreferBmr);
            }).ConfigureAwait(false);
            if (!IsCurrentStartup(operation))
                return;
            if (!rotationReady)
            {
                await GameHelpers.RunOnFrameworkThreadAsync(() => { if (IsCurrentStartup(operation)) StopWithCombatFailureMessage(rotationService.Failure); }).ConfigureAwait(false);
                return;
            }

            Status = Ui.M("Engine_CheckingConflictingPlugins");
            var conflictingPluginsReady = await conflictPluginService.EnsureTwistOfFayteDisabledAsync("MOGTOME start", showPopup: true, () => IsCurrentStartup(operation));
            if (!IsCurrentStartup(operation))
            {
                log.Warning("[MOGTOME][Engine] Start aborted while resolving conflicting plugins");
                return;
            }

            if (!conflictingPluginsReady)
            {
                log.Warning("[MOGTOME][Engine] Twist of Fayte warning path reported a soft failure, but startup will continue");
            }

            var startupSnapshot = await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!IsCurrentStartup(operation)) return default(StartupSnapshot);
                var startingInsideDuty = DutyStartupService.IsInDuty();
                return new StartupSnapshot(startingInsideDuty, state.IsPartyLeader);
            }).ConfigureAwait(false);
            if (!IsCurrentStartup(operation)) return;

            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_PreparingADS")
                : Ui.M("Engine_PreparingAutoDuty");
            var backendReady = await dutyAutomationService.PrepareForStartAsync(startupSnapshot.IsPartyLeader, startupSnapshot.StartingInsideDuty, () => IsCurrentStartup(operation)).ConfigureAwait(false);
            if (!IsCurrentStartup(operation))
            {
                log.Warning("[MOGTOME][Engine] Start aborted while preparing automation backend");
                return;
            }

            if (!backendReady)
            {
                await GameHelpers.RunOnFrameworkThreadAsync(() =>
                {
                    if (IsCurrentStartup(operation)) StopWithCombatFailureMessage(dutyAutomationService.PreparationFailure);
                }).ConfigureAwait(false);
                return;
            }

            var preparationResult = await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!IsCurrentStartup(operation))
                    return new StartupPreparationResult(EnteredRepairMode: false);
                log.Information("[MOGTOME][Engine] Sending /at enable as part of startup command prep");
                GameHelpers.SendCommand("/at enable");

                log.Information($"[MOGTOME][Engine] Using current party role at start: IsLeader={state.IsPartyLeader}, ConfiguredLeader={config.IsPartyLeader}, CrossWorld={config.IsCrossWorldParty}");

                log.Information("[MOGTOME][Engine] Checking repair status before start");
                if (repairService.NeedsRepair(forceRefresh: true))
                {
                    if (dutyAutomationService.UseAdsExperimental && !state.IsPartyLeader && !startupSnapshot.StartingInsideDuty)
                    {
                        log.Information("[MOGTOME][ADS][Repair] Repair needed before follower outside-arm at startup; deferring /ads outside until repair completes");
                    }

                    log.Information("[Engine] Repair needed - repairing before start");
                    EnterRepairMode(useNpcRepair: ShouldUseNpcRepair(), Ui.M("Engine_RepairingBeforeStart"));
                    return new StartupPreparationResult(EnteredRepairMode: true);
                }

                dialogHandler.Start();

                consumableInventoryService.Refresh(force: true);
                state.CalculateTimeouts(LoopInterval);

                dutyAutomationService.ApplyQueueConfiguration(testingModeUnsynced);
                GameHelpers.SetDutyFinderLevelSync(!testingModeUnsynced);

                if (testingModeUnsynced)
                    log.Information("[MOGTOME][Engine] Testing mode: Unsync=ON, LevelSync=OFF");
                else
                    log.Information("[MOGTOME][Engine] Normal mode: Unsync=ON, LevelSync=ON");

                return new StartupPreparationResult(EnteredRepairMode: false);
            }).ConfigureAwait(false);

            if (!IsCurrentStartup(operation) && !preparationResult.EnteredRepairMode)
                return;
            if (preparationResult.EnteredRepairMode)
                return;

            if (startupSnapshot.StartingInsideDuty)
            {
                log.Information("[MOGTOME][Engine] Start requested while already inside duty - skipping duty finder setup");
                await GameHelpers.RunOnFrameworkThreadAsync(() =>
                {
                    if (!IsCurrentStartup(operation))
                        return;
                    Status = Ui.M("Engine_ResumingInsideDuty");
                    ResumeOrEnterCurrentDuty();
                }).ConfigureAwait(false);
                return;
            }

            await ConfigureDutyFinderSettingsAsync(operation, enableLevelSync: !testingModeUnsynced).ConfigureAwait(false);

            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!IsCurrentStartup(operation))
                {
                    log.Warning("[MOGTOME][Engine] Start aborted before entering waiting-outside-duty state");
                    return;
                }

                if (dutyAutomationService.UseAdsExperimental && !startupSnapshot.IsPartyLeader)
                    dutyAutomationService.EnsureFollowerOutsideArmed("startup ready");

                CurrentState = EngineState.WaitingOutsideDuty;
                Status = Ui.M("Engine_RunningDuty", state.DutyCounter + 1);
                log.Information($"[MOGTOME][Engine] Initialized. Leader={state.IsPartyLeader}, Counter={state.DutyCounter}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Initialization failed: {ex.Message}");
            await GameHelpers.RunOnFrameworkThreadAsync(() => { if (IsCurrentStartup(operation)) StopWithCombatFailure(ex.Message); }).ConfigureAwait(false);
        }
    }

    public void Stop(string? reason = null) => StopWithReason(reason == null ? Ui.M("Engine_ManualStop") : (UiText)reason);

    internal void StopWithReason(UiText reason)
    {
        ++sessionId;
        cancelQueuedStart();
        log.Information("[MOGTOME][Engine] Stopping MOGTOME engine");
        ClearStopAfterNextSuccessfulRun();
        CurrentState = EngineState.Stopping;
        dutyStartup.Cancel();
        Status = Ui.M("Engine_Stopping");

        StopReason = reason;
        CleanupStep("opener", () => firstRoomSkip.Cancel("engine stop"));
        CleanupStep("backend", dutyAutomationService.StopDuty);
        CleanupStep("dialogs", dialogHandler.Stop);
        CleanupStep("repair queue", dutyQueue.ClearRepairQueuePauseOnStop);
        ResetLeaveTracking();
        CleanupStep("death tracking", () => deathTrackingService.Clear("engine stop"));
        ResetRepairRequestState();
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        requeueInProgress = false;
        delayedRequeueInProgress = false;
        requeueState = RequeueState.Idle;
        CleanupStep("combat", () =>
        {
            if (!rotationService.DisableRotationForDutyEnd("engine stop"))
                throw new InvalidOperationException(rotationService.LastFailureReason);
        });
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        bailoutExitPending = false;
        confirmedDutyExitPending = false;
        dutyEnteredUtc = DateTime.MinValue;
        ResetDutyEntryTerritoryWait();
        state.Reset();
        CurrentState = EngineState.Idle;
        Status = Ui.M("Engine_Stopped", StopReason);
        log.Information("[MOGTOME][Engine] Stopped");
    }

    private void CleanupStep(string step, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Engine] {step} cleanup failed: {ex.Message}");
            if (CurrentState == EngineState.Stopping)
                StopReason = Ui.M("Engine_CleanupFailed", StopReason, step, ex.Message);
        }
    }

    internal void RecordStopReason(UiText reason)
    {
        StopReason = reason;
        Status = reason;
    }

    public bool ToggleStopAfterNextSuccessfulRun()
    {
        StopAfterNextSuccessfulRunArmed = !StopAfterNextSuccessfulRunArmed;
        log.Information($"[MOGTOME][Engine] Stop after next successful run {(StopAfterNextSuccessfulRunArmed ? "armed" : "cancelled")}");
        return StopAfterNextSuccessfulRunArmed;
    }

    public bool ClearStopAfterNextSuccessfulRun()
    {
        if (!StopAfterNextSuccessfulRunArmed)
            return false;

        StopAfterNextSuccessfulRunArmed = false;
        log.Information("[MOGTOME][Engine] Stop after next successful run cleared");
        return true;
    }

    private void ClearStaleDutyStateIfNeeded()
    {
        ResetDutyEntryTerritoryWait();
        var liveTerritory = clientState.TerritoryType;

        if (DutyStartupService.IsInDuty())
        {
            state.CurrentTerritory = liveTerritory;

            if (IsMogtomeDutyTerritory(liveTerritory))
            {
                if (state.DutyStartTerritory != liveTerritory)
                    log.Warning($"[MOGTOME][Engine] DutyStartTerritory refreshed from live MOGTOME territory {liveTerritory} while already inside duty");

                state.DutyStartTerritory = liveTerritory;
                return;
            }

            if (state.DutyStartTerritory != 0 && !IsMogtomeDutyTerritory(state.DutyStartTerritory))
            {
                log.Warning($"[MOGTOME][Engine] Clearing stale non-MOGTOME DutyStartTerritory {state.DutyStartTerritory} while already inside duty");
                state.DutyStartTerritory = 0;
            }

            return;
        }

        if (state.DutyStartTerritory != 0 || state.CurrentTerritory != 0)
        {
            log.Information($"[MOGTOME][Engine] Clearing cached duty territory before startup outside duty (start={state.DutyStartTerritory}, current={state.CurrentTerritory})");
            state.DutyStartTerritory = 0;
            state.CurrentTerritory = 0;
        }

        if (!state.IsInDuty && !state.HasEnteredDuty)
            return;

        log.Warning("[MOGTOME][Engine] Clearing stale in-duty state before startup because the client is currently outside duty");
        state.Reset();
        deathTrackingService.Clear("stale duty state");
        dutyCompleted = false;
        autoDutyStartedInDuty = false;
        dutyEnteredUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        dutyAutomationService.InvalidateAdsQueueOperations("duty entered");
        ResetQueueRecoveryState();
    }

    private void ResumeOrEnterCurrentDuty()
    {
        CurrentState = EngineState.InDuty;
        // Completion and already successful stages can arrive during initialization.
        autoDutyStartedInDuty = dutyStartup.IsConfirmed;
        if (dutyEnteredUtc == DateTime.MinValue)
            dutyEnteredUtc = DateTime.UtcNow;
        ResetLeaveTracking();
        if (!dutyCompleted && !dutyStartup.CombatActivated)
            rotationService.ResetDutyRotationState("resume current duty");
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        requeueInProgress = false;
        requeueState = RequeueState.Idle;

        if (!state.IsInDuty && !state.HasEnteredDuty)
        {
            log.Information("[MOGTOME][Engine] Starting while already inside duty - entering fresh in-duty state");
            if (!OnEnteredDuty())
                return;

            if (CurrentState == EngineState.InDuty)
                StartDutyBackendInsideDuty($"startup inside territory {state.DutyStartTerritory}");
            return;
        }

        state.IsInDuty = true;
        state.IsInCombat = condition[26];
        var knownTerritory = GetKnownDutyTerritory();
        if (IsMogtomeDutyTerritory(knownTerritory))
            deathTrackingService.Start(knownTerritory);
        CurrentState = EngineState.InDuty;
        Status = Ui.M("Engine_InDuty", state.DutyCounter + 1, Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName()));
        log.Information($"[MOGTOME][Engine] Resuming current duty without re-counting start (HasEnteredDuty={state.HasEnteredDuty}, DutyCounter={state.DutyCounter})");

        StartDutyBackendInsideDuty($"resuming territory {state.DutyStartTerritory}");
    }

    private Task RunStartupActionAsync(int operation, Action action)
        => GameHelpers.RunOnFrameworkThreadAsync(() =>
        {
            if (!IsCurrentStartup(operation)) throw new OperationCanceledException();
            action();
        });

    private async Task<bool> WaitForAddonVisibleAsync(int operation, string addonName, TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsCurrentStartup(operation) && DateTime.UtcNow < deadline)
        {
            if (await GameHelpers.RunOnFrameworkThreadAsync(() => IsCurrentStartup(operation) && GameHelpers.IsAddonVisible(addonName)).ConfigureAwait(false))
                return true;

            await Task.Delay(pollInterval).ConfigureAwait(false);
        }

        return await GameHelpers.RunOnFrameworkThreadAsync(() => IsCurrentStartup(operation) && GameHelpers.IsAddonVisible(addonName)).ConfigureAwait(false);
    }

    private async Task ConfigureDutyFinderSettingsAsync(int operation, bool enableLevelSync)
    {
        log.Information($"[MOGTOME][Engine] Setting up duty finder for Unsync=ON, LevelSync={(enableLevelSync ? "ON" : "OFF")}");

        try
        {
            log.Debug("[MOGTOME][Engine] Step 1: Opening duty finder");
            await RunStartupActionAsync(operation, () => GameHelpers.OpenDutyFinder()).ConfigureAwait(false);

            if (!await WaitForAddonVisibleAsync(operation, "ContentsFinder", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)).ConfigureAwait(false))
            {
                log.Warning("[MOGTOME][Engine] ContentsFinder addon not visible after /dutyfinder - continuing without verified duty finder UI setup");
                return;
            }

            log.Debug("[MOGTOME][Engine] ContentsFinder addon is visible");

            log.Debug("[MOGTOME][Engine] Step 2: Opening duty finder options");
            await RunStartupActionAsync(operation, () => GameHelpers.FireAddonCallback("ContentsFinder", true, 15)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 3: Setting Unrestricted Party (Unsync)");
            await RunStartupActionAsync(operation, () => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 1, 1)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 4: Setting Level Sync");
            await RunStartupActionAsync(operation, () => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 2, enableLevelSync ? 1 : 0)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 5: Confirming duty finder settings");
            await RunStartupActionAsync(operation, () => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 0)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            if (!IsCurrentStartup(operation)) return;
            log.Information($"[MOGTOME][Engine] Duty finder setup complete: Unsync=ON, LevelSync={(enableLevelSync ? "ON" : "OFF")}");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Failed to set up duty finder: {ex.Message}");
        }
    }

    public void Update()
    {
        if (!IsRunning || CurrentState == EngineState.Stopping) return;

        var inDuty = DutyStartupService.IsInDuty();
        var identity = DutyStartupService.ReadLiveDutyIdentity();
        var readiness = DutyStartupService.ReadReadinessConditions();
        confirmedDutyExitPending |= dutyStartup.ObserveReadiness(inDuty, identity, readiness, DateTime.UtcNow);
        if (!dutyStartup.CombatActivated)
            autoDutyStartedInDuty = false;

        if (!clientState.IsLoggedIn)
        {
            if (!logoutObserved)
            {
                logoutObserved = true;
                ++sessionId;
                ResetLeaveTracking();
                dutyAutomationService.CancelPendingOperations("logout");
                dutyQueue.ClearRepairQueuePauseOnStop();
            }
            Status = Ui.M("Engine_SuspendedWaitingForLoginAndDutyContext");
            return;
        }
        logoutObserved = false;
        if (!dutyCompleted)
            state.CheckBailout(DateTime.UtcNow, config.BailoutTimeout);
        if (CurrentState == EngineState.Initializing)
        {
            if (!IsStartupPending) startupTask = StartCoreAsync(sessionId);
            return;
        }

        // Throttle based on loop interval (hardcoded 2s)
        var now = DateTime.UtcNow;
        if ((now - lastTick).TotalSeconds < LoopInterval) return;
        lastTick = now;

        try
        {
            // Check daily reset
            dutyTracker.CheckDailyReset();

            // Update territory
            state.CurrentTerritory = clientState.TerritoryType;
            consumableInventoryService.Refresh();

            // A confirmed outside frame can occur between throttled updates.
            // Finish the previous session before processing any subsequent entry.
            if (confirmedDutyExitPending)
            {
                confirmedDutyExitPending = false;
                if (state.IsInDuty)
                {
                    if (state.HasEnteredDuty)
                        OnLeftDuty();
                    else
                        HandlePendingDutyEntryCancelled();
                    if (!IsRunning)
                        return;
                }
            }

            if (inDuty && (!state.IsInDuty || !state.HasEnteredDuty))
            {
                if (!OnEnteredDuty())
                    return;
            }
            else if (!inDuty && state.IsInDuty)
            {
                Status = Ui.M("Engine_DutyTransitionWaitingForConfirmedDutyContext");
                return;
            }

            if (!inDuty && !state.IsInDuty && dutyTracker.ShouldQuit())
            {
                HandleQuit();
                return;
            }

            if (inDuty && (!DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId)
                           || identity.TerritoryTypeId != state.DutyStartTerritory))
            {
                Status = Ui.M("Engine_DutyPendingWaitingForMatchingSupportedTerritory");
                return;
            }

            // Condition[26] = InCombat
            state.IsInCombat = condition[26];

            var repairFlowBlockingDutyPop = IsRepairFlowActive()
                && (dutyQueue.CancelDutyPopForRepair() || HasQueueRegistrationCondition());
            if (!repairFlowBlockingDutyPop)
            {
                // Handle dialogs always unless repair is actively protecting an inn/NPC repair flow.
                dialogHandler.Update(returnToStartEligible:
                    CurrentState == EngineState.InDuty && inDuty && !dutyCompleted &&
                    IsMogtomeDutyTerritory(state.CurrentTerritory) &&
                    Plugin.ObjectTable.LocalPlayer?.IsDead == true);

                // Auto-accept duty pop for non-leaders
                dutyQueue.AutoAcceptDuty();
            }
            else
            {
                dialogHandler.ResetReturnPromptWait();
            }

            HandleQueueConditionTransitions(inDuty);

            switch (CurrentState)
            {
                case EngineState.WaitingOutsideDuty:
                    UpdateOutsideDuty();
                    break;
                case EngineState.Queueing:
                    UpdateQueueing();
                    break;
                case EngineState.InDuty:
                    UpdateInDuty();
                    break;
                case EngineState.RepairingOutside:
                    UpdateRepairing();
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Update error: {ex.Message}");
        }
    }

    private bool OnEnteredDuty()
    {
        if (!state.IsInDuty)
        {
            state.IsInDuty = true;
            state.IsInCombat = condition[26];
            ResetLeaveTracking();
            if (dutyEnteredUtc == DateTime.MinValue)
                dutyEnteredUtc = DateTime.UtcNow;
            dutyTerritoryWaitStartedUtc = dutyEnteredUtc;
            lastDutyTerritoryWaitLogUtc = DateTime.MinValue;
            if (!dutyCompleted && !dutyStartup.CombatActivated)
                rotationService.ResetDutyRotationState("entered duty");
            ResetRepairRecoveryWatchdog();
            dutyAutomationService.CancelPendingOperations("duty entered");
            dutyQueue.ClearRepairQueuePauseOnStop();
            ResetQueueRecoveryState();

            // Reset requeue state when successfully entering duty
            requeueInProgress = false;
            requeueState = RequeueState.Idle;
        }
        else
        {
            state.IsInCombat = condition[26];
            if (dutyTerritoryWaitStartedUtc == DateTime.MinValue)
                dutyTerritoryWaitStartedUtc = DateTime.UtcNow;
        }

        var enteredTerritory = ResolveEnteredDutyTerritory();
        if (!IsMogtomeDutyTerritory(enteredTerritory))
        {
            if (ShouldWaitForDutyTerritory())
                return false;

            if (HandleUnexpectedDutyEntry(enteredTerritory))
                return false;
        }

        ResetDutyEntryTerritoryWait();
        deathTrackingService.Start(enteredTerritory);
        dutyTracker.OnDutyStarted();
        
        CurrentState = EngineState.InDuty;
        Status = Ui.M("Engine_InDuty", state.DutyCounter + 1, Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName()));
        log.Information($"[MOGTOME][Engine] Entered duty attempt #{state.DutyCounter + 1}");

        return true;
    }

    private uint ResolveEnteredDutyTerritory()
    {
        var identity = DutyStartupService.ReadLiveDutyIdentity();
        if (identity.TerritoryTypeId == 0 || identity.ContentFinderConditionId == 0)
            return 0;

        if (DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
        {
            state.CurrentTerritory = identity.TerritoryTypeId;
            state.DutyStartTerritory = identity.TerritoryTypeId;
        }

        return DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId)
            ? identity.TerritoryTypeId : 0;
    }

    private uint GetKnownDutyTerritory(uint eventTerritoryId = 0)
    {
        if (IsMogtomeDutyTerritory(state.DutyStartTerritory))
            return state.DutyStartTerritory;

        if (IsMogtomeDutyTerritory(eventTerritoryId))
            return eventTerritoryId;

        var liveTerritory = clientState.TerritoryType;
        if (IsMogtomeDutyTerritory(liveTerritory))
            return liveTerritory;

        if (IsMogtomeDutyTerritory(state.CurrentTerritory))
            return state.CurrentTerritory;

        if (eventTerritoryId != 0)
            return eventTerritoryId;

        if (liveTerritory != 0)
            return liveTerritory;

        return state.CurrentTerritory;
    }

    private static bool IsMogtomeDutyTerritory(uint territoryId)
        => DutyState.IsMogtomeDutyTerritory(territoryId);

    private bool ShouldWaitForDutyTerritory()
    {
        var now = DateTime.UtcNow;
        if (dutyTerritoryWaitStartedUtc == DateTime.MinValue)
            dutyTerritoryWaitStartedUtc = now;

        var elapsed = (now - dutyTerritoryWaitStartedUtc).TotalSeconds;
        var identity = DutyStartupService.ReadLiveDutyIdentity();
        if (identity.TerritoryTypeId == 0 || identity.ContentFinderConditionId == 0 ||
            AdsIntegrationPolicy.GetHandoffReadinessBlocker(DutyStartupService.ReadReadinessConditions()) is not null)
        {
            Status = Ui.M("Engine_EnteringDutyWaitingForReadyDutyContext");
            LogDutyTerritoryWait(now, elapsed);
            return true;
        }
        if (elapsed >= DutyTerritorySettleSeconds)
            return false;

        Status = Ui.M("Engine_EnteringDutyWaitingForTerritory");
        LogDutyTerritoryWait(now, elapsed);
        return true;
    }

    private bool IsDutyEntryTransitionActive(DateTime now)
        => condition[ConditionFlag.BetweenAreas] ||
           condition[BoundByDuty56ConditionIndex] ||
           IsRecentQueueAcceptanceEvidence(now);

    private bool IsRecentQueueAcceptanceEvidence(DateTime now)
        => lastQueueAcceptanceEvidenceUtc != DateTime.MinValue &&
           (now - lastQueueAcceptanceEvidenceUtc).TotalSeconds <= RecentQueueAcceptanceEvidenceSeconds;

    private void MarkQueueAcceptanceEvidence()
        => lastQueueAcceptanceEvidenceUtc = DateTime.UtcNow;

    private void ResetDutyEntryTerritoryWait()
    {
        dutyTerritoryWaitStartedUtc = DateTime.MinValue;
        lastDutyTerritoryWaitLogUtc = DateTime.MinValue;
    }

    private void LogDutyTerritoryWait(DateTime now, double elapsed)
    {
        if (lastDutyTerritoryWaitLogUtc != DateTime.MinValue &&
            (now - lastDutyTerritoryWaitLogUtc).TotalSeconds < 3.0)
        {
            return;
        }

        lastDutyTerritoryWaitLogUtc = now;
        log.Information($"[MOGTOME][Engine] BoundByDuty active but no MOGTOME territory is known yet; waiting for territory settle ({elapsed:F1}/{DutyTerritorySettleSeconds:F0}s). Diagnostics: {BuildDutyEntryDiagnostics(now)}");
    }

    private string BuildDutyEntryDiagnostics(DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.UtcNow;
        var queueEvidenceAge = lastQueueAcceptanceEvidenceUtc == DateTime.MinValue
            ? "none"
            : $"{Math.Max(0, (now - lastQueueAcceptanceEvidenceUtc).TotalSeconds):F1}s ago";
        var waitElapsed = dutyTerritoryWaitStartedUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (now - dutyTerritoryWaitStartedUtc).TotalSeconds);

        return $"cachedStart={state.DutyStartTerritory}, current={state.CurrentTerritory}, live={clientState.TerritoryType}, BoundByDuty={condition[34]}, BoundByDuty56={condition[BoundByDuty56ConditionIndex]}, BetweenAreas={condition[ConditionFlag.BetweenAreas]}, transitionActive={IsDutyEntryTransitionActive(now)}, queueEvidence={queueEvidenceAge}, waitElapsed={waitElapsed:F1}s";
    }

    private uint GetConfirmedUnexpectedDutyTerritory()
    {
        var liveTerritory = clientState.TerritoryType;
        return liveTerritory != 0 && !IsMogtomeDutyTerritory(liveTerritory)
            ? liveTerritory
            : 0;
    }

    private bool HandleUnexpectedDutyEntry(uint territoryId)
    {
        Status = Ui.M("Engine_DutyPendingUnexpectedOrMissingTerritoryCFC", territoryId);
        return true;
    }
    private void HandlePendingDutyEntryCancelled()
    {
        ++sessionId;
        dutyStartup.ResetSession();
        confirmedDutyExitPending = false;
        firstRoomSkipAttempted = false;
        log.Warning($"[MOGTOME][Engine] BoundByDuty ended before MOGTOME duty territory resolved. Diagnostics: {BuildDutyEntryDiagnostics()}");
        firstRoomSkip.Cancel("duty entry cancelled before territory resolved");
        state.Reset();
        deathTrackingService.Clear("pending duty entry cancelled");
        outsideDutyTicks = 0;
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        dutyCompletedTime = DateTime.MinValue;
        dutyEnteredUtc = DateTime.MinValue;
        delayedRequeueInProgress = false;
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        ResetLeaveTracking();
        ResetRepairRecoveryWatchdog();
        ResetDutyEntryTerritoryWait();
        ResetQueueRecoveryState();
        dutyAutomationService.CancelPendingOperations("duty entry cancelled before territory resolved");
        CurrentState = EngineState.WaitingOutsideDuty;
        Status = Ui.M("Engine_OutsideDutyNext", state.DutyCounter + 1);
    }

    private void OnLeftDuty()
    {
        if (!state.HasEnteredDuty) return;
        state.HasEnteredDuty = false;
        dialogHandler.ResetReturnPromptWait();
        CleanupStep("duty-exit combat", () =>
        {
            if (rotationService.DisableRotationForDutyEnd("left duty")) return;
            var message = Ui.M("MogtomeEngine_CombatCleanupFailedAfterDutyExitContinuing");
            var reason = rotationService.Failure;
            log.Warning($"[MOGTOME][Engine] {message} {reason}");
            Plugin.ChatGui.Print(new XivChatEntry
            {
                Type = XivChatType.Echo,
                Message = Ui.T("Chat_MessageAndReason", message, reason),
            });
            Plugin.ToastGui.ShowNormal(message.Render());
        });
        if (!dutyCompleted)
        {
            HandleAbortedDutyExit();
            return;
        }

        // IMPORTANT: Call dutyTracker.OnDutyCompleted() FIRST
        // This calculates completion time and calls RecordRun() to save the record.
        // We must do this before reading stats so we get the FRESH record, not stale data.
        dutyTracker.OnDutyCompleted();

        // Now read the freshly created record for stats update
        if ((!config.TestingModeUnsynced || config.ShowDebugRuns) && runHistoryService.RunHistory.Count > 0)
        {
            var mostRecentRun = runHistoryService.SuccessfulRunHistory.LastOrDefault();
            if (mostRecentRun != null)
            {
                log.Debug($"[MOGTOME][Engine] Verified successful completion time: {mostRecentRun.CompletionTime:F1}s");
                
                if (float.IsFinite(mostRecentRun.CompletionTime) && mostRecentRun.CompletionTime > 0)
                {
                    var partyComp = string.Join(", ", mostRecentRun.PartyMembers);
                    var dateStr = mostRecentRun.Timestamp.ToString("yyyy-MM-dd HH:mm UTC");

                    log.Debug($"[MOGTOME][Engine] Updating stats - Run: {mostRecentRun.CompletionTime:F1}s, Party: [{partyComp}], Date: {dateStr}, Territory: {mostRecentRun.TerritoryId}, IsPrae: {mostRecentRun.IsPraetorium}");

                    // Update global stats (kept for compatibility)
                    if (mostRecentRun.CompletionTime < config.BestTimeEver)
                    {
                        var oldBest = config.BestTimeEver;
                        config.BestTimeEver = mostRecentRun.CompletionTime;
                        config.BestTimeDate = dateStr;
                        config.BestTimeParty = partyComp;
                        log.Information($"[MOGTOME][Engine] NEW BEST TIME: {oldBest:F1}s → {mostRecentRun.CompletionTime:F1}s by {partyComp}");
                    }

                    if (mostRecentRun.CompletionTime > config.LongestRunEver)
                    {
                        var oldLongest = config.LongestRunEver;
                        config.LongestRunEver = mostRecentRun.CompletionTime;
                        config.LongestRunDate = dateStr;
                        config.LongestRunParty = partyComp;
                        log.Information($"[MOGTOME][Engine] NEW LONGEST RUN: {oldLongest:F1}s → {mostRecentRun.CompletionTime:F1}s by {partyComp}");
                    }

                    // Update duty-specific stats
                    UpdateDutyStatsFromRun(mostRecentRun, partyComp, dateStr);

                    log.Information($"[MOGTOME][Engine] Stats updated successfully - Method: VALID_RUN_CHECK");
                }
                else
                {
                    log.Warning($"[MOGTOME][Engine] Skipping stats update - INVALID_COMPLETION_TIME: {mostRecentRun.CompletionTime:F1}s");
                    log.Debug($"[MOGTOME][Engine] Run details - Timestamp: {mostRecentRun.Timestamp}, Territory: {mostRecentRun.TerritoryId}, WasSuccessful: {mostRecentRun.WasSuccessful}, IsPraetorium: {mostRecentRun.IsPraetorium}");
                }
            }
            else
            {
                log.Warning("[MOGTOME][Engine] Skipping stats update - NO_RECENT_RUN_FOUND");
            }
        }
        else if (config.TestingModeUnsynced && !config.ShowDebugRuns)
        {
            log.Information("[MOGTOME][Engine] Unsynced run - skipping stats tracking (TestingModeUnsynced=true, ShowDebugRuns=false)");
        }
        else
        {
            log.Warning("[MOGTOME][Engine] Skipping stats update - NO_RUN_HISTORY (count: 0)");
        }
        ResetAfterConfirmedDutyExit("successful duty left");
        ContinueAfterConfirmedDutyExit(successful: true);
    }

    private void HandleAbortedDutyExit()
    {
        var reason = string.IsNullOrWhiteSpace(state.BailoutReason)
            ? "Left duty without verified completion"
            : state.BailoutReason;
        var elapsed = state.BailoutElapsedTime;
        if (!float.IsFinite(elapsed) || elapsed <= 0)
        {
            var start = state.DutyStartTime?.ToUniversalTime() ?? dutyEnteredUtc;
            elapsed = start == DateTime.MinValue
                ? 0
                : (float)Math.Max(0, (DateTime.UtcNow - start).TotalSeconds);
        }

        try
        {
            runHistoryService.RecordRun(RunOutcome.Aborted, reason, elapsed);
            log.Warning($"[MOGTOME][Engine] Recorded aborted run after confirmed exit: elapsed={elapsed:F1}s, reason={reason}");
        }
        finally
        {
            state.Reset();
            ResetAfterConfirmedDutyExit("incomplete duty left");
        }
        ContinueAfterConfirmedDutyExit(successful: false);
    }

    private void ResetAfterConfirmedDutyExit(string reason)
    {
        ++sessionId;
        dutyStartup.ResetSession();
        confirmedDutyExitPending = false;
        firstRoomSkipAttempted = false;
        CleanupStep("duty-exit opener", () => firstRoomSkip.Cancel(reason));
        CleanupStep("duty-exit rotation state", () => rotationService.ResetDutyRotationState(reason));
        state.IsInDuty = false;
        CleanupStep("duty-exit death tracking", () => deathTrackingService.Clear(reason));
        outsideDutyTicks = 0;
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        bailoutExitPending = false;
        dutyCompletedTime = DateTime.MinValue;
        dutyEnteredUtc = DateTime.MinValue;
        delayedRequeueInProgress = false;
        ResetLeaveTracking();
        ResetDutyEntryTerritoryWait();
        ResetRepairRecoveryWatchdog();
        dutyAutomationService.CancelPendingOperations(reason);
        CleanupStep("backend duty exit", dutyAutomationService.NotifyDutyLeft);
        ResetQueueRecoveryState();
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        CurrentState = EngineState.WaitingOutsideDuty;
        Status = Ui.M("Engine_OutsideDutyNext", state.DutyCounter + 1);
        log.Information($"[MOGTOME][Engine] Confirmed duty exit ({reason}). Next: #{state.DutyCounter + 1}");
    }

    private void ContinueAfterConfirmedDutyExit(bool successful)
    {
        if (dutyTracker.ShouldQuit())
        {
            HandleQuit();
            return;
        }
        if (successful && StopAfterNextSuccessfulRunArmed)
        {
            log.Information("[MOGTOME][Engine] Stop-after-next consumed after successful run and confirmed duty exit");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEStopAfterNextCompletedStoppingBefore"));
            StopWithReason(Ui.M("Engine_StopAfterNextSuccessfulClearAndConfirmed"));
            return;
        }

        if (state.IsPartyLeader)
        {
            log.Information($"[MOGTOME][Engine] Starting leader requeue sequence - {state.DutyCounter}/{config.MaxRuns} completed");
            requeueState = RequeueState.WaitingAfterLeave;
            requeueStartTime = DateTime.UtcNow;
            requeueInProgress = true;
            return;
        }

        log.Information($"[MOGTOME][Engine] Non-leader ready for next duty - {state.DutyCounter}/{config.MaxRuns} completed");
    }

    /// <summary>
    /// Update duty-specific stats from recorded run data
    /// </summary>
    private void UpdateDutyStatsFromRun(RunRecord run, string partyComp, string dateStr)
    {
        if (run.IsPraetorium)
        {
            // Praetorium stats
            if (run.CompletionTime < config.PraeBestTime)
            {
                config.PraeBestTime = run.CompletionTime;
                config.PraeBestTimeDate = dateStr;
                config.PraeBestTimeParty = partyComp;
            }

            if (run.CompletionTime > config.PraeLongestRun)
            {
                config.PraeLongestRun = run.CompletionTime;
                config.PraeLongestRunDate = dateStr;
                config.PraeLongestRunParty = partyComp;
            }
        }
        else
        {
            // Decumana stats (all-time)
            if (run.CompletionTime < config.DecuBestTime)
            {
                config.DecuBestTime = run.CompletionTime;
                config.DecuBestTimeDate = dateStr;
                config.DecuBestTimeParty = partyComp;
            }

            if (run.CompletionTime > config.DecuLongestRun)
            {
                config.DecuLongestRun = run.CompletionTime;
                config.DecuLongestRunDate = dateStr;
                config.DecuLongestRunParty = partyComp;
            }

            // Daily Decumana stats
            if (run.Timestamp.Date == DateTime.UtcNow.Date)
            {
                if (run.CompletionTime < config.DailyDecuBestTime)
                {
                    config.DailyDecuBestTime = run.CompletionTime;
                }

                if (run.CompletionTime > config.DailyDecuLongestRun)
                {
                    config.DailyDecuLongestRun = run.CompletionTime;
                }
            }
        }
    }

    private void UpdateDutyStats(string partyComp, string dateStr)
    {
        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;
        
        if (isPrae)
        {
            // Praetorium stats
            if (state.LastCompletionDuration < config.PraeBestTime)
            {
                config.PraeBestTime = state.LastCompletionDuration;
                config.PraeBestTimeDate = dateStr;
                config.PraeBestTimeParty = partyComp;
            }

            if (state.LastCompletionDuration > config.PraeLongestRun)
            {
                config.PraeLongestRun = state.LastCompletionDuration;
                config.PraeLongestRunDate = dateStr;
                config.PraeLongestRunParty = partyComp;
            }
        }
        else
        {
            // Decumana stats (all-time)
            if (state.LastCompletionDuration < config.DecuBestTime)
            {
                config.DecuBestTime = state.LastCompletionDuration;
                config.DecuBestTimeDate = dateStr;
                config.DecuBestTimeParty = partyComp;
            }

            if (state.LastCompletionDuration > config.DecuLongestRun)
            {
                config.DecuLongestRun = state.LastCompletionDuration;
                config.DecuLongestRunDate = dateStr;
                config.DecuLongestRunParty = partyComp;
            }
            
            // Daily Decumana stats
            config.DailyDecuRuns++;
            if (state.LastCompletionDuration < config.DailyDecuBestTime)
            {
                config.DailyDecuBestTime = state.LastCompletionDuration;
            }
            if (state.LastCompletionDuration > config.DailyDecuLongestRun)
            {
                config.DailyDecuLongestRun = state.LastCompletionDuration;
            }
        }

        log.Information($"[MOGTOME][Engine] Updated {(isPrae ? "Praetorium" : "Decumana")} stats: {state.LastCompletionDuration:F0}s");
    }

    private void UpdateOutsideDuty()
    {
        outsideDutyTicks++;

        // Food check
        foodService.Update();

        // Repair check
        if (repairService.NeedsRepair(forceRefresh: true))
        {
            if (dutyAutomationService.UseAdsExperimental && !state.IsPartyLeader)
            {
                log.Information("[MOGTOME][ADS][Repair] Repair needed while outside duty; deferring follower /ads outside until repair completes");
            }

            EnterRepairMode(useNpcRepair: ShouldUseNpcRepair(), Ui.M("Engine_Repairing"));
            return;
        }

        if (dutyAutomationService.UseAdsExperimental && !state.IsPartyLeader)
        {
            dutyAutomationService.EnsureFollowerOutsideArmed("outside-duty follower wait");
            dutyAutomationService.UpdateFollowerWaitingForEntry();
        }

        // Auto-equip
        repairService.AutoEquipIfEnabled();

        if (HandleQueueRecoveryPause())
        {
            return;
        }

        // Handle requeue state machine
        if (requeueInProgress)
        {
            HandleRequeueStateMachine();
            return; // Skip normal queue logic while requeue in progress
        }

        if (HandleRepairRecoveryWatchdog())
        {
            return;
        }

        // Normal queue logic (only if not requeueing)
        if (state.IsPartyLeader && !delayedRequeueInProgress)
        {
            var isPrae = dutyTracker.ShouldRunPraetorium();
            StartQueueAttempt(isPrae, ignoreCooldown: false, Ui.M("EngineState_Queueing"));
        }
        else if (!state.IsPartyLeader)
        {
            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_WaitingForLeader", state.DutyCounter + 1, dutyAutomationService.GetAdsRuntimeStatusText())
                : Ui.M("Engine_WaitingForLeader2", state.DutyCounter + 1);
        }
        else if (delayedRequeueInProgress)
        {
            Status = Ui.M("Engine_DelayedRequeueInProgress");
        }
    }

    private void HandleRequeueStateMachine()
    {
        try
        {
            if (requeueStartTime == DateTime.MinValue) return;
            
            var elapsed = (DateTime.UtcNow - requeueStartTime).TotalSeconds;
            var isPrae = dutyTracker?.ShouldRunPraetorium() ?? false;
            
            switch (requeueState)
            {
                case RequeueState.WaitingAfterLeave:
                    if (elapsed >= 10.0)
                    {
                        requeueState = RequeueState.WaitingToStop;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    Status = Ui.M("Engine_WaitingAfterLeaveDutyS", 10.0 - elapsed);
                    break;
                    
                case RequeueState.WaitingToStop:
                    if (elapsed >= 2.0)
                    {
                        log.Information($"[MOGTOME][Engine] Stopping {dutyAutomationService.ActiveBackendDisplayName} from previous duty");
                        try
                        {
                            dutyAutomationService.StopDuty();
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[MOGTOME][Engine] {dutyAutomationService.ActiveBackendDisplayName} stop failed: {ex.Message}");
                        }
                        requeueState = RequeueState.StoppingBackend;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    Status = Ui.M("Engine_WaitingToStopS", dutyAutomationService.ActiveBackendDisplayName, 2.0 - elapsed);
                    break;
                    
                case RequeueState.StoppingBackend:
                    if (elapsed >= 1.0) // Give time for stop to process
                    {
                        requeueState = RequeueState.WaitingToQueue;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    Status = Ui.M("Engine_Stopping2", dutyAutomationService.ActiveBackendDisplayName);
                    break;
                    
                case RequeueState.WaitingToQueue:
                    if (elapsed >= 1.0)
                    {
                        if (StartQueueAttempt(isPrae, ignoreCooldown: false, Ui.M("Engine_AutoQueueing")))
                        {
                            requeueState = RequeueState.Complete;
                            requeueInProgress = false;
                            log.Information($"[MOGTOME][Engine] Auto-queue command sent for next run: {dutyTracker?.GetCurrentDutyName() ?? "Unknown"}");
                            return;
                        }
                        break;
                    }
                    Status = Ui.M("Engine_WaitingToQueueS", 1.0 - elapsed);
                    break;
                    
                case RequeueState.Queueing:
                    if (elapsed >= 5.0) // Give 5s for queue to process
                    {
                        // Check if successfully queued (entered duty or in queue)
                        if (condition?[ConditionFlag.BoundByDuty] == true || CurrentState == EngineState.InDuty)
                        {
                            requeueState = RequeueState.Complete;
                            requeueInProgress = false;
                            log.Information("[MOGTOME][Engine] Requeue completed successfully");
                        }
                        else
                        {
                            // Queue failed, retry
                            requeueAttempts++;
                            if (requeueAttempts >= MaxRequeueAttempts)
                            {
                                requeueState = RequeueState.Failed;
                                requeueInProgress = false;
                                log.Error("[MOGTOME][Engine] Requeue failed after max attempts");
                            }
                            else
                            {
                                log.Warning($"[MOGTOME][Engine] Requeue attempt {requeueAttempts} failed, retrying in {RequeueRetryInterval}s");
                                requeueState = RequeueState.WaitingToStop;
                                requeueStartTime = DateTime.UtcNow.AddSeconds(RequeueRetryInterval - 3.0); // Account for 2s wait
                            }
                        }
                    }
                    Status = Ui.M("Engine_Queueing");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Requeue state machine error: {ex.Message}");
            // Reset to safe state on error
            requeueInProgress = false;
            requeueState = RequeueState.Idle;
        }
    }

    private void UpdateQueueing()
    {
        if (HandleQueueRecoveryPause())
            return;

        if (HandleRepairRecoveryWatchdog())
            return;

        var dutyName = Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName());
        if (HasQueueRegistrationOrConfirm())
        {
            MarkQueueAcceptanceEvidence();
            if (queueRegistrationStartedUtc != DateTime.MinValue)
            {
                var elapsed = (DateTime.UtcNow - queueRegistrationStartedUtc).TotalSeconds;
                log.Information($"[MOGTOME][Engine] Queue registration detected for {dutyName} after {elapsed:F1}s");
                ResetQueueRegistrationWatchdog();
            }

            dutyAutomationService.ConfirmQueueRegistration(dutyTracker.ShouldRunPraetorium());
            dutyAutomationService.ClearAdsQueueFailure();
            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_QueueingRegistered", dutyName, dutyAutomationService.GetAdsRuntimeStatusText())
                : Ui.M("Engine_QueueingRegistered2", dutyName);
            return;
        }

        if (dutyAutomationService.TryGetAdsQueueFailure(out var failureReason, out var cooldownRemainingSeconds))
        {
            if (cooldownRemainingSeconds > 0)
            {
                Status = Ui.M("Engine_QueueingContentsFinderRetryCooldownS", dutyName, Math.Ceiling(cooldownRemainingSeconds), dutyAutomationService.GetAdsRuntimeStatusText());
                return;
            }

            dutyAutomationService.ClearAdsQueueFailure();
            BeginQueueRecovery($"ContentsFinder queue attempt failed: {failureReason}");
            return;
        }

        if (queueRegistrationStartedUtc != DateTime.MinValue)
        {
            var elapsed = (DateTime.UtcNow - queueRegistrationStartedUtc).TotalSeconds;
            if (elapsed >= QueueRegistrationWatchdogSeconds)
            {
                BeginQueueRecovery($"Queue registration did not start within {QueueRegistrationWatchdogSeconds:F0}s");
                return;
            }

            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_QueueingWaitingForQueueRegistrationS", dutyName, Math.Ceiling(elapsed), QueueRegistrationWatchdogSeconds, dutyAutomationService.GetAdsRuntimeStatusText())
                : Ui.M("Engine_QueueingWaitingForQueueRegistrationS2", dutyName, Math.Ceiling(elapsed), QueueRegistrationWatchdogSeconds);
            return;
        }

        // If we're now in duty, the state will change via OnEnteredDuty
        // Otherwise keep waiting
        if (!condition[34])
        {
            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_QueueingWaiting", dutyName, dutyAutomationService.GetAdsRuntimeStatusText())
                : Ui.M("Engine_QueueingWaiting2", dutyName);
        }
    }

    private void UpdateInDuty()
    {
        dutyTracker.ObserveRemainingTime();
        if (!dutyCompleted)
            state.CheckBailout(DateTime.UtcNow, config.BailoutTimeout);

        if (!dutyCompleted && state.BailoutRequested && !bailoutExitPending)
        {
            bailoutExitPending = true;
            dutyStartup.HoldForExit();
            CleanupStep("bailout opener", () => firstRoomSkip.Cancel("bailout requested"));
            dialogHandler.ResetReturnPromptWait();
            ResetLeaveTracking();
            log.Warning($"[MOGTOME][Engine] Waiting to exit aborted attempt: {state.BailoutReason}");
        }

        if (dutyCompleted || bailoutExitPending)
        {
            if (IsLeaveBlocked(out var leaveBlocker))
            {
                LogLeaveBlocker(leaveBlocker.English);
                Status = Ui.M("Engine_LeaveBlocked", (dutyCompleted ? Ui.M("Engine_DutyComplete") : Ui.M("Engine_BailoutPending")), leaveBlocker);
                return;
            }

            lastLeaveBlocker = string.Empty;
            ObserveLeaveConfirmationEvidence();
            if (leaveRequestedUtc != DateTime.MinValue)
            {
                var settleElapsed = (DateTime.UtcNow - leaveRequestedUtc).TotalSeconds;
                if (settleElapsed < DutyExitSettleSeconds)
                {
                    Status = Ui.M("Engine_LeaveRequestedWaitingForZoneOutS", DutyExitSettleSeconds - settleElapsed);
                    return;
                }
            }
            LeaveDuty();
            return;
        }

        if (!autoDutyStartedInDuty)
        {
            StartDutyBackendInsideDuty("in-duty update");
            if (!autoDutyStartedInDuty)
                return;
        }

        var blocker = AdsIntegrationPolicy.GetHandoffReadinessMessage(DutyStartupService.ReadReadinessConditions());
        if (blocker != null)
        {
            Status = Ui.M("Engine_CombatPending", blocker);
            return;
        }

        rotationService.UpdateDutyRotationHealth(GetKnownDutyTerritory(), autoDutyStartedInDuty,
            dutyCompleted, localPlayerDead: false, "in-duty update");
        bossHandler.Update();
        stuckDetection.Update();
        Status = Ui.M("Engine_InDutyS", state.DutyCounter + 1, state.TimeInDuty);
    }

    private void StartDutyBackendInsideDuty(string reason)
    {
        if (!IsRunning || CurrentState != EngineState.InDuty || dutyCompleted ||
            state.BailoutRequested || autoDutyStartedInDuty)
            return;

        var operation = sessionId;
        try
        {
            var result = dutyStartup.Update(DutyStartupService.IsInDuty(),
                DutyStartupService.ReadLiveDutyIdentity(), DutyStartupService.ReadReadinessConditions(),
                DateTime.UtcNow, firstRoomSkip.IsActive, TryStartFirstRoomSkip);
            if (operation != sessionId || !IsRunning || dutyCompleted || bailoutExitPending) return;
            if (dutyStartup.BackendConfirmed && dutyAutomationService.UseAdsExperimental)
                dutyAutomationService.ConfirmAdsDutyInside(state.IsPartyLeader);
            if (result == DutyStartupResult.Confirmed)
            {
                autoDutyStartedInDuty = true;
                log.Information($"[MOGTOME][Engine] Combat and {dutyAutomationService.ActiveBackendDisplayName} startup confirmed ({reason})");
            }
            else
            {
                Status = dutyStartup.Status;
            }
        }
        catch (Exception ex)
        {
            if (operation != sessionId || !IsRunning || dutyCompleted || bailoutExitPending) return;
            dutyStartup.DeferFailure(DateTime.UtcNow, ex.Message);
            Status = dutyStartup.Status;
        }
    }

    private void StopWithCombatFailure(string reason) => StopWithCombatFailureMessage(reason);

    private void StopWithCombatFailureMessage(UiText reason)
    {
        StopWithReason(Ui.M("Engine_InitializationFailed", reason));
        log.Error($"[MOGTOME][Engine] {StatusMessage}");
        Plugin.ChatGui.PrintError(Ui.T("Chat_MOGTOME", Status));
    }

    private bool IsLeaveBlocked(out UiText blocker)
    {
        blocker = AdsIntegrationPolicy.GetDutyLeaveMessage(state.DutyStartTerritory, DutyStartupService.IsInDuty(),
            DutyStartupService.ReadLiveDutyIdentity(), DutyStartupService.ReadReadinessConditions(),
            condition[ConditionFlag.InCombat], condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.Occupied33] || condition[ConditionFlag.Occupied39]) ?? string.Empty;
        return blocker.English.Length != 0;
    }

    private void LogLeaveBlocker(string blocker)
    {
        if (string.Equals(lastLeaveBlocker, blocker, StringComparison.OrdinalIgnoreCase))
            return;

        lastLeaveBlocker = blocker;
        log.Information($"[MOGTOME][Engine] Leave request blocked by {blocker}; waiting for safe exit seam");
    }

    private void ObserveLeaveConfirmationEvidence()
    {
        if (leaveRequestedUtc == DateTime.MinValue || leaveConfirmationObserved)
            return;

        if (condition[ConditionFlag.BetweenAreas])
        {
            leaveConfirmationObserved = true;
            dutyAutomationService.ObserveLeaveConfirmationEvidence("ConditionFlag.BetweenAreas");
            return;
        }

        if (GameHelpers.IsLeaveDutyPromptVisible())
        {
            leaveConfirmationObserved = true;
            dutyAutomationService.ObserveLeaveConfirmationEvidence("recognized leave-duty prompt");
        }
    }

    private void ResetLeaveTracking()
    {
        ++leaveOperationId;
        leaveRequestedUtc = DateTime.MinValue;
        leaveConfirmationObserved = false;
        lastLeaveBlocker = string.Empty;
        lastLeaveAttemptTime = DateTime.MinValue;
        leaveAttemptCount = 0;
    }

    private void UpdateRepairing()
    {
        outsideDutyTicks++;
        Status = Ui.M("Engine_Repairing");

        if (outsideDutyTicks <= 5)
            return;

        var outsideDutyForRepairTruth = !state.IsInDuty && !condition[ConditionFlag.BoundByDuty];
        var waitingForAdsRepairCompletion = dutyAutomationService.IsAdsRepairHandoffActive && outsideDutyForRepairTruth;
        if (!outsideDutyForRepairTruth)
        {
            RetryRepairRequestIfNeeded();
            return;
        }

        // Repair completion must win over stale cached state before retrying commands.
        if (repairService.NeedsRepair(forceRefresh: true))
        {
            RetryRepairRequestIfNeeded();
            return;
        }

        if (waitingForAdsRepairCompletion)
            dutyAutomationService.RestoreAdsOutsideAfterRepair();

        ResetRepairRequestState();
        repairService.ReturnToInnIfNeeded();
        dutyQueue.ResumeQueueAfterRepair();
        CurrentState = EngineState.WaitingOutsideDuty;
        outsideDutyTicks = 0;
        ArmRepairRecoveryWatchdog();

        if (pendingQueueRecoveryAfterRepair)
        {
            pendingQueueRecoveryAfterRepair = false;
            if (!IsQueueRecoveryActive())
            {
                BeginQueueRecovery("Queue condition ended while repair was active");
            }
            return;
        }

        Status = Ui.M("Engine_RepairDoneResuming");
    }

    private bool ShouldUseNpcRepair()
    {
        if (!dutyAutomationService.UseAdsExperimental)
            return !state.IsPartyLeader;

        return !config.UseAdsSelfRepair;
    }

    private void EnterRepairMode(bool useNpcRepair, UiText statusMessage)
    {
        ResetRepairRequestState();
        ResetRepairRecoveryWatchdog();
        ResetQueueRegistrationWatchdog();
        CurrentState = EngineState.RepairingOutside;
        Status = statusMessage;
        outsideDutyTicks = 0;
        dutyQueue.PauseQueueForRepair();
        activeRepairUsesNpc = useNpcRepair;
        IssueRepairRequest("entered repair mode");
    }

    private void ArmRepairRecoveryWatchdog()
    {
        repairRecoveryWatchStartedUtc = DateTime.UtcNow;
        repairRecoveryRetryReadyUtc = DateTime.MinValue;
        repairRecoveryAttempts = 0;
        log.Information($"[MOGTOME][Engine] Repair flow complete; if still outside duty after {RepairRecoveryWatchdogSeconds:F0}s, MOGTOME will {dutyAutomationService.StopCommandLabel} and retry");
    }

    private void ResetRepairRecoveryWatchdog()
    {
        repairRecoveryWatchStartedUtc = DateTime.MinValue;
        repairRecoveryRetryReadyUtc = DateTime.MinValue;
        repairRecoveryAttempts = 0;
    }

    private void ResetRepairRequestState()
    {
        lastRepairRequestUtc = DateTime.MinValue;
        repairRequestAttempts = 0;
        activeRepairUsesNpc = false;
        lastRepairRetryBlockedLogUtc = DateTime.MinValue;
        lastRepairRetryBlockedReason = string.Empty;
        ResetTelepotTownRepairState();
    }

    private void IssueRepairRequest(string reason)
    {
        repairRequestAttempts++;
        lastRepairRequestUtc = DateTime.UtcNow;

        if (repairRequestAttempts == 1)
        {
            log.Information($"[MOGTOME][Engine] Sending {dutyAutomationService.ActiveBackendDisplayName} repair request ({(activeRepairUsesNpc ? "npc" : "self")}) - {reason}");
        }
        else
        {
            log.Warning($"[MOGTOME][Engine] Repair still needed; retrying {dutyAutomationService.ActiveBackendDisplayName} repair request attempt {repairRequestAttempts} ({(activeRepairUsesNpc ? "npc" : "self")}) - {reason}");
        }

        if (activeRepairUsesNpc)
            repairService.TryNpcRepair();
        else
            repairService.TrySelfRepair();
    }

    private bool HandleTelepotTownRepairBlock()
    {
        if (!GameHelpers.IsAddonVisible(TelepotTownAddon))
        {
            ResetTelepotTownRepairState();
            return false;
        }

        if (state.IsInDuty ||
            condition[ConditionFlag.BoundByDuty] ||
            condition[ConditionFlag.BetweenAreas] ||
            HasQueueRegistrationCondition() ||
            GameHelpers.IsAddonVisible("ContentsFinderConfirm") ||
            dutyQueue.IsRepairDutyPopCancelInFlight)
        {
            ResetTelepotTownRepairState();
            return false;
        }

        var now = DateTime.UtcNow;
        if (telepotTownVisibleSinceUtc == DateTime.MinValue)
            telepotTownVisibleSinceUtc = now;

        var visibleSeconds = (now - telepotTownVisibleSinceUtc).TotalSeconds;
        if (visibleSeconds < TelepotTownRepairCancelSeconds)
        {
            var remainingSeconds = Math.Ceiling(TelepotTownRepairCancelSeconds - visibleSeconds);
            Status = Ui.M("Engine_RepairingWaitingForTelepotTownS", remainingSeconds);
            LogRepairRetryBlocked("TelepotTown dialog");
            return true;
        }

        log.Warning($"[MOGTOME][Engine] TelepotTown blocked repair for {visibleSeconds:F0}s; cancelling it and retrying ADS NPC repair");
        var cancelFired = GameHelpers.TryFireAddonCallback(TelepotTownAddon, true, -2);
        var closeFired = GameHelpers.TryFireAddonCallback(TelepotTownAddon, true, 1);
        if (!cancelFired || !closeFired)
            log.Warning($"[MOGTOME][Engine] TelepotTown cancel callbacks incomplete: -2={cancelFired}, 1={closeFired}");

        ResetTelepotTownRepairState();
        activeRepairUsesNpc = true;
        IssueRepairRequest($"TelepotTown blocked repair for {visibleSeconds:F0}s; forcing NPC repair");
        return true;
    }

    private void RetryRepairRequestIfNeeded()
    {
        if (HandleTelepotTownRepairBlock())
            return;

        if (IsRepairRetryBlocked(out var blocker))
        {
            Status = Ui.M("Engine_RepairingWaitingFor", blocker);
            LogRepairRetryBlocked(blocker.English);
            return;
        }

        if (lastRepairRequestUtc == DateTime.MinValue)
        {
            IssueRepairRequest("repair state had no active request timestamp");
            return;
        }

        var elapsedSinceRequest = (DateTime.UtcNow - lastRepairRequestUtc).TotalSeconds;
        if (elapsedSinceRequest < RepairRequestRetrySeconds)
            return;

        // Retry repair requests on a bounded cadence, never per-frame.
        IssueRepairRequest($"repair still needed after {elapsedSinceRequest:F0}s");
    }

    private void ResetTelepotTownRepairState()
    {
        telepotTownVisibleSinceUtc = DateTime.MinValue;
    }

    private bool IsRepairRetryBlocked(out UiText blocker)
    {
        if (state.IsInDuty || condition[ConditionFlag.BoundByDuty])
        {
            blocker = Ui.M("RepairBlocker_DutyState");
            return true;
        }

        if (condition[ConditionFlag.BetweenAreas])
        {
            blocker = Ui.M("RepairBlocker_ZoneTransition");
            return true;
        }

        if (HasQueueRegistrationCondition())
        {
            blocker = Ui.M("RepairBlocker_QueueRegistration");
            return true;
        }

        if (GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
        {
            blocker = Ui.M("RepairBlocker_DutyConfirmation");
            return true;
        }

        if (GameHelpers.IsAddonVisible("_NotificationFinder"))
        {
            blocker = Ui.M("RepairBlocker_MinimizedDutyConfirmation");
            return true;
        }

        if (dutyQueue.IsRepairDutyPopCancelInFlight)
        {
            blocker = Ui.M("RepairBlocker_CancelDutyConfirmation");
            return true;
        }

        if (GameHelpers.IsAddonVisible("SelectYesno"))
        {
            blocker = Ui.M("RepairBlocker_YesNoDialog");
            return true;
        }

        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene])
        {
            blocker = Ui.M("RepairBlocker_Cutscene");
            return true;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.Occupied33] ||
            condition[ConditionFlag.Occupied39])
        {
            blocker = Ui.M("RepairBlocker_Occupied");
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private void LogRepairRetryBlocked(string blocker)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(lastRepairRetryBlockedReason, blocker, StringComparison.OrdinalIgnoreCase) &&
            (now - lastRepairRetryBlockedLogUtc).TotalSeconds < RepairRetryBlockedLogSeconds)
        {
            return;
        }

        lastRepairRetryBlockedReason = blocker;
        lastRepairRetryBlockedLogUtc = now;
        log.Information($"[MOGTOME][Engine] Repair retry held while {blocker}; rechecking durability instead of sending another repair command");
    }

    private bool IsRepairFlowActive()
        => CurrentState == EngineState.RepairingOutside || state.QueuePausedForRepair;

    private bool IsQueueRecoveryActive()
        => queueRecoveryStopUntilUtc != DateTime.MinValue;

    private bool HasQueueRegistrationCondition()
        => condition[QueueConditionIndex]
           || condition[WaitingForDutyConditionIndex]
           || condition[WaitingForDutyFinderConditionIndex];

    private bool HasQueueRegistrationOrConfirm()
        => HasQueueRegistrationCondition() || GameHelpers.IsAddonVisible("ContentsFinderConfirm");

    private void ResetQueueRegistrationWatchdog()
    {
        queueRegistrationStartedUtc = DateTime.MinValue;
    }

    private bool StartQueueAttempt(bool isPrae, bool ignoreCooldown, UiText statusPrefix)
    {
        var dutyName = Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName());

        if (dutyAutomationService.IsAdsQueueRetryCooldownActive(out var cooldownRemainingSeconds, out var cooldownReason))
        {
            CurrentState = EngineState.WaitingOutsideDuty;
            Status = dutyAutomationService.UseAdsExperimental
                ? Ui.M("Engine_ContentsFinderQueueCooldownS", dutyName, Math.Ceiling(cooldownRemainingSeconds), dutyAutomationService.GetAdsRuntimeStatusText())
                : Ui.M("Engine_ContentsFinderQueueCooldownS2", dutyName, Math.Ceiling(cooldownRemainingSeconds));
            log.Debug($"[MOGTOME][Engine] Holding {statusPrefix} for {dutyName}; ContentsFinder queue cooldown active ({Math.Ceiling(cooldownRemainingSeconds):F0}s remaining; {cooldownReason})");
            return false;
        }

        CurrentState = EngineState.Queueing;
        Status = dutyAutomationService.UseAdsExperimental
            ? Ui.M("Engine_Value", statusPrefix, dutyName, dutyAutomationService.GetQueueStatusText(), dutyAutomationService.GetAdsRuntimeStatusText())
            : Ui.M("Engine_Value2", statusPrefix, dutyName);

        try
        {
            var queueSent = ignoreCooldown
                ? dutyQueue.ForceQueue(isPrae)
                : dutyQueue.TryQueue(isPrae);
            if (!queueSent)
            {
                ResetQueueRegistrationWatchdog();
                CurrentState = EngineState.WaitingOutsideDuty;
                if (dutyQueue.LastQueueBlockedForPartySize)
                    Status = Ui.M("Engine_WaitingForPeopleVisible", dutyQueue.VisiblePartyMemberCount);
                else if (dutyQueue.LastQueueBlockedForPartyDuty)
                    Status = Ui.M("Engine_WaitingForPartyMembersToLeaveDuty");
                return false;
            }

            queueRegistrationStartedUtc = DateTime.UtcNow;
            MarkQueueAcceptanceEvidence();
            log.Information($"[MOGTOME][Engine] {statusPrefix} command sent for {dutyName}; waiting up to {QueueRegistrationWatchdogSeconds:F0}s for queue registration");
            return true;
        }
        catch (Exception ex)
        {
            ResetQueueRegistrationWatchdog();
            log.Error($"[MOGTOME][Engine] {statusPrefix} failed for {dutyName}: {ex.Message}");
            return false;
        }
    }

    private void ResetQueueRecoveryState()
    {
        sawQueueConditionOutsideDuty = false;
        pendingQueueRecoveryAfterRepair = false;
        queueRecoveryStopUntilUtc = DateTime.MinValue;
        queueRecoveryResumeUtc = DateTime.MinValue;
        ResetQueueRegistrationWatchdog();
    }

    private void BeginQueueRecovery(string reason)
    {
        var now = DateTime.UtcNow;
        sawQueueConditionOutsideDuty = false;
        pendingQueueRecoveryAfterRepair = false;
        ResetQueueRegistrationWatchdog();
        queueRecoveryStopUntilUtc = now.AddSeconds(QueueRecoveryStopSeconds);
        queueRecoveryResumeUtc = DateTime.MinValue;
        CurrentState = EngineState.WaitingOutsideDuty;
        outsideDutyTicks = 0;
        Status = Ui.M("Engine_QueueRecoveryWaitingAfterS", dutyAutomationService.StopCommandLabel, QueueRecoveryStopSeconds);
        log.Warning($"[MOGTOME][Engine] {reason}; sending {dutyAutomationService.StopCommandLabel}, waiting {QueueRecoveryStopSeconds:F0}s, then holding {QueueRecoveryRepairGraceSeconds:F0}s for repairs");
        dutyAutomationService.InvalidateAdsQueueOperations("queue recovery");
        dutyAutomationService.StopDuty();
    }

    private void HandleQueueConditionTransitions(bool inDuty)
    {
        if (inDuty)
        {
            ResetQueueRecoveryState();
            return;
        }

        if (IsQueueRecoveryActive())
            return;

        if (IsRepairFlowActive() && (dutyQueue.IsRepairDutyPopCancelInFlight || GameHelpers.IsAddonVisible("_NotificationFinder")))
        {
            MarkQueueAcceptanceEvidence();
            sawQueueConditionOutsideDuty = true;
            return;
        }

        if (HasQueueRegistrationOrConfirm())
        {
            MarkQueueAcceptanceEvidence();
            sawQueueConditionOutsideDuty = true;
            return;
        }

        if (!sawQueueConditionOutsideDuty)
            return;

        sawQueueConditionOutsideDuty = false;
        if (IsRepairFlowActive())
        {
            pendingQueueRecoveryAfterRepair = true;
            log.Warning("[MOGTOME][Engine] Queue condition ended while repair is active; deferring queue recovery until repair completes");
            return;
        }

        BeginQueueRecovery("Queue condition ended before duty entry");
    }

    private bool HandleQueueRecoveryPause()
    {
        if (!IsQueueRecoveryActive())
            return false;

        if (state.IsInDuty || condition[34])
        {
            ResetQueueRecoveryState();
            return false;
        }

        var now = DateTime.UtcNow;
        var stopRemaining = (queueRecoveryStopUntilUtc - now).TotalSeconds;
        if (stopRemaining > 0)
        {
            Status = Ui.M("Engine_QueueRecoveryWaitingAfterS", dutyAutomationService.StopCommandLabel, Math.Ceiling(stopRemaining));
            return true;
        }

        if (queueRecoveryResumeUtc == DateTime.MinValue)
        {
            queueRecoveryResumeUtc = now.AddSeconds(QueueRecoveryRepairGraceSeconds);
            log.Information($"[MOGTOME][Engine] Queue recovery: allowing {QueueRecoveryRepairGraceSeconds:F0}s for repairs before resuming duty entry");
        }

        var repairRemaining = (queueRecoveryResumeUtc - now).TotalSeconds;
        if (repairRemaining > 0)
        {
            Status = Ui.M("Engine_QueueRecoveryAllowingRepairsS", Math.Ceiling(repairRemaining));
            return true;
        }

        queueRecoveryStopUntilUtc = DateTime.MinValue;
        queueRecoveryResumeUtc = DateTime.MinValue;
        CurrentState = EngineState.WaitingOutsideDuty;
        outsideDutyTicks = 0;
        Status = Ui.M("Engine_QueueRecoveryResumingDutyEntry");
        log.Information("[MOGTOME][Engine] Queue recovery wait complete; resuming duty entry attempts");
        return false;
    }

    private bool HandleRepairRecoveryWatchdog()
    {
        if (repairRecoveryWatchStartedUtc == DateTime.MinValue || state.IsInDuty)
            return false;

        var now = DateTime.UtcNow;
        if (repairRecoveryRetryReadyUtc != DateTime.MinValue)
        {
            var remaining = (repairRecoveryRetryReadyUtc - now).TotalSeconds;
            if (remaining > 0)
            {
                Status = Ui.M("Engine_RepairRecoveryWaitingToRequeueS", Math.Ceiling(remaining));
                return true;
            }

            repairRecoveryRetryReadyUtc = DateTime.MinValue;
            repairRecoveryWatchStartedUtc = now;

            if (state.IsPartyLeader)
            {
                var isPrae = dutyTracker.ShouldRunPraetorium();
                var dutyName = Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName());
                log.Warning($"[MOGTOME][Engine] Repair recovery retry {repairRecoveryAttempts}: force-queueing {dutyName} after {dutyAutomationService.StopCommandLabel}");
                StartQueueAttempt(isPrae, ignoreCooldown: true, Ui.M("Engine_RepairRecoveryRetry", repairRecoveryAttempts));
                return true;
            }

            Status = Ui.M("Engine_RepairRecoveryRetryWaitingForLeader", repairRecoveryAttempts);
            return true;
        }

        var elapsed = (now - repairRecoveryWatchStartedUtc).TotalSeconds;
        if (elapsed < RepairRecoveryWatchdogSeconds)
            return false;

        repairRecoveryAttempts++;
        if (state.IsPartyLeader)
        {
            var dutyName = Ui.Duty(dutyTracker.ShouldRunPraetorium() ? 1044u : 1048u, dutyTracker.GetCurrentDutyName());
            log.Warning($"[MOGTOME][Engine] Still outside duty {elapsed:F0}s after repair; sending {dutyAutomationService.StopCommandLabel} and retrying {dutyName} (attempt {repairRecoveryAttempts})");
            dutyAutomationService.StopDuty();
            repairRecoveryRetryReadyUtc = now.AddSeconds(RepairRecoveryStopDelaySeconds);
            Status = Ui.M("Engine_RepairRecoveryRestarting", dutyName);
            return true;
        }

        log.Warning($"[MOGTOME][Engine] Still outside duty {elapsed:F0}s after repair; sending {dutyAutomationService.StopCommandLabel} and waiting for leader retry (attempt {repairRecoveryAttempts})");
        dutyAutomationService.StopDuty();
        repairRecoveryWatchStartedUtc = now;
        Status = Ui.M("Engine_RepairRecoveryWaitingForLeader");
        return true;
    }

    private void HandleQuit()
    {
        log.Information($"[MOGTOME][Engine] Quit condition reached: {state.DutyCounter} runs completed");
        StopWithReason(Ui.M("Engine_PraetoriumDailyLimitReached", state.DutyCounter, config.MaxRuns));

        if (!string.IsNullOrEmpty(config.QuitCommand))
        {
            try
            {
                commandManager.ProcessCommand(config.QuitCommand);
            }
            catch (Exception ex)
            {
                log.Error($"[MOGTOME][Engine] Quit command failed: {ex.Message}");
            }
        }
    }

    private void LeaveDuty()
    {
        if (IsLeaveBlocked(out _)) return;
        var now = DateTime.UtcNow;
        // Throttle leave attempts to every 5 seconds
        if ((now - lastLeaveAttemptTime).TotalSeconds < 5.0) return;

        PauseLeaderQueueBeforeExitIfRepairNeeded("LeaveDuty");

        lastLeaveAttemptTime = now;
        leaveAttemptCount++;
        leaveRequestedUtc = now;
        leaveConfirmationObserved = false;
        lastLeaveBlocker = string.Empty;

        var elapsed = (now - dutyCompletedTime).TotalSeconds;
        var leaveReason = dutyCompleted
            ? $"Exit on first safe seam after duty complete ({elapsed:F1}s since completion)"
            : state.BailoutReason;

        if (dutyAutomationService.UseAdsExperimental)
        {
            log.Information($"[MOGTOME][Engine] ADS leave attempt #{leaveAttemptCount} - REASON: {leaveReason}");
            dutyAutomationService.RequestDutyLeave(leaveReason, dutyCompletedTime, leaveAttemptCount);
            Status = Ui.M("Engine_LeaveRequestedViaADSAttemptWaitingFor", leaveAttemptCount);
            return;
        }

        log.Information($"[MOGTOME][Engine] Leave duty attempt #{leaveAttemptCount} - REASON: {leaveReason}");
        log.Information("[MOGTOME][Engine] Opening duty panel to leave");

        // Open duty panel to access Leave Duty button
        GameHelpers.OpenDutyFinder();

        QueueLeaveAction("open leave duty button", TryClickLeaveDutyButton);
        QueueLeaveAction("confirm leave duty", () =>
        {
            if (GameHelpers.ClickLeaveDutyYesIfVisible())
                log.Information("[MOGTOME][Engine] Successfully clicked Yes on leave duty confirmation");
        });

        Status = Ui.M("Engine_LeaveRequestedAttemptWaitingForZoneOut", leaveAttemptCount);
    }

    private void QueueLeaveAction(string step, Action action)
    {
        var session = sessionId;
        var operation = leaveOperationId;
        GameHelpers.QueueFrameworkAction("Engine leave", step, TimeSpan.FromMilliseconds(500), () =>
        {
            if (session == sessionId && operation == leaveOperationId && IsRunning
                && (dutyCompleted || bailoutExitPending) && !IsLeaveBlocked(out _))
                action();
        });
    }

    private unsafe void TryClickLeaveDutyButton()
    {
        try
        {
            // Use xa docs callback pattern: Open ContentsFinderMenu directly, then click Leave button (node 43)
            log.Information("[MOGTOME][Engine] Opening ContentsFinderMenu with callback");
            
            // Try direct callback to open ContentsFinderMenu (pattern from Character true 12)
            try
            {
                // Based on xa docs pattern - try different callback numbers to open ContentsFinderMenu
                GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 0);
            }
            catch (Exception ex)
            {
                log.Error($"[MOGTOME][Engine] ContentsFinderMenu callback failed: {ex.Message}");
            }
            
            QueueLeaveAction("click leave button", TryClickLeaveButton);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Error trying to leave duty: {ex.Message}");
        }
    }

    private unsafe void TryClickLeaveButton()
    {
        try
        {
            // Click Leave button using xa docs pattern: ClickAddonButton("ContentsFinderMenu", 43)
            log.Information("[MOGTOME][Engine] Clicking Leave button on ContentsFinderMenu");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 43);
            
            QueueLeaveAction("handle leave confirmation", HandleLeaveConfirmation);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Error clicking Leave button: {ex.Message}");
        }
    }

    private void HandleLeaveConfirmation()
    {
        try
        {
            // Click Yes on SelectYesno confirmation dialog
            log.Information("[MOGTOME][Engine] Clicking Yes on leave confirmation dialog");
            GameHelpers.ClickLeaveDutyYesIfVisible();
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Error handling leave confirmation: {ex.Message}");
        }
    }

    private string GetPartyComposition()
    {
        try
        {
            var party = Plugin.PartyList;
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (party.Length == 0 && localPlayer == null) return "None";

            var members = new System.Collections.Generic.List<string>();
            
            // Add party members
            for (var i = 0; i < party.Length; i++)
            {
                var member = party[i];
                if (member != null)
                {
                    var name = member.Name.ToString();
                    var job = member.ClassJob.Value.Abbreviation.ToString();
                    var level = member.Level.ToString();
                    members.Add($"{name}-{job}-{level}");
                }
            }
            
            // Add local player if solo
            if (party.Length == 0 && localPlayer != null)
            {
                var name = localPlayer.Name.ToString();
                var job = localPlayer.ClassJob.Value.Abbreviation.ToString();
                var level = localPlayer.Level.ToString();
                members.Add($"{name}-{job}-{level}");
            }
            
            return members.Count > 0 ? string.Join(", ", members) : "Unknown";
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] GetPartyComposition failed: {ex.Message}");
            return "Unknown";
        }
    }

    private int GetCurrentPartyMemberCount()
    {
        try
        {
            var party = Plugin.PartyList;
            var memberCount = 0;
            for (var i = 0; i < party.Length; i++)
            {
                if (party[i] != null)
                    memberCount++;
            }

            if (memberCount > 0)
                return memberCount;

            return Plugin.ObjectTable.LocalPlayer != null ? 1 : 0;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] GetCurrentPartyMemberCount failed: {ex.Message}");
            return 0;
        }
    }

    private void PauseLeaderQueueBeforeExitIfRepairNeeded(string reason)
    {
        if (!state.IsPartyLeader)
            return;

        if (state.QueuePausedForRepair)
        {
            log.Information($"[MOGTOME][Engine] MOGTOME queue already paused for repair before duty exit ({reason})");
            return;
        }

        if (!repairService.NeedsRepair(forceRefresh: true))
            return;

        dutyQueue.PauseQueueForRepair();
        log.Warning($"[MOGTOME][Engine] Leader repair detected before duty exit; MOGTOME queue paused before leaving duty ({reason})");
    }

    public void ApplyConfiguredPartyLeaderState(string reason = "configured role")
    {
        var source = config.IsCrossWorldParty
            ? "configured cross-world role"
            : "configured party role checkbox";
        SetPartyLeaderState(config.IsPartyLeader, reason, $"Source={source}");
    }

    public void RefreshPartyLeaderState()
    {
        if (condition[34] || state.IsInDuty)
        {
            var message = Ui.M("MogtomeEngine_ManualPartyRefreshSkippedInsideDutyUse");
            log.Warning($"[MOGTOME][Engine] {message} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
            return;
        }

        if (config.IsCrossWorldParty)
        {
            log.Information($"[MOGTOME][Engine] Manual party refresh requested in cross-world mode; keeping configured role. Party={GetPartyComposition()}");
            ApplyConfiguredPartyLeaderState("manual refresh");
            return;
        }

        if (!TryDetectSameWorldPartyLeader(out var isPartyLeader, out var details))
        {
            var message = Ui.M("MogtomeEngine_ManualPartyRefreshCouldNotDetermineLeader", (state.IsPartyLeader ? Ui.M("Config_Leader") : Ui.M("MogtomeEngine_NonLeader")));
            log.Warning($"[MOGTOME][Engine] {message} {details} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
            return;
        }

        SetPartyLeaderState(isPartyLeader, "manual refresh", details);
    }

    private void SetPartyLeaderState(bool isPartyLeader, string reason, string details)
    {
        var previousLeaderState = state.IsPartyLeader;
        state.IsPartyLeader = isPartyLeader;

        if (previousLeaderState == isPartyLeader)
        {
            log.Information($"[MOGTOME][Engine] Party leader state unchanged ({reason}): IsLeader={isPartyLeader}. {details}");
            return;
        }

        log.Information($"[MOGTOME][Engine] Party leader state changed ({reason}): {previousLeaderState} -> {isPartyLeader}. {details}");
    }

    private bool TryDetectSameWorldPartyLeader(out bool isPartyLeader, out string details)
    {
        isPartyLeader = state.IsPartyLeader;

        try
        {
            var party = Plugin.PartyList;
            if (party.Length <= 1)
            {
                details = $"Same-world detection requires at least 2 visible party members; visible count={party.Length}.";
                return false;
            }

            var leaderIndex = (int)party.PartyLeaderIndex;
            if (leaderIndex < 0 || leaderIndex >= party.Length)
            {
                details = $"Same-world detection failed because PartyLeaderIndex={leaderIndex} is invalid for party length {party.Length}.";
                return false;
            }

            var leader = party[leaderIndex];
            if (leader == null)
            {
                details = $"Same-world detection failed because party leader entry {leaderIndex} was null.";
                return false;
            }

            var localContentId = (long)Plugin.PlayerState.ContentId;
            var leaderContentId = (long)leader.ContentId;
            if (localContentId == 0 || leaderContentId == 0)
            {
                details = $"Same-world detection failed because content IDs were not ready (local={localContentId}, leader={leaderContentId}).";
                return false;
            }

            isPartyLeader = leaderContentId == localContentId;
            details = $"Source=same-world manual detection, PartyLeaderIndex={leaderIndex}, LocalContentId={localContentId}, LeaderContentId={leaderContentId}, VisiblePartyMembers={party.Length}";
            return true;
        }
        catch (Exception ex)
        {
            details = $"Same-world detection failed with exception: {ex.Message}";
            return false;
        }
    }
}
