using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
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
    private const string PhantomGaiusName = "Phantom Gaius";
    private const int MinimumSyncedPartyMembers = 3;
    private readonly record struct StartupSnapshot(bool StartingInsideDuty, bool AbortForMinimumPartySize, bool IsPartyLeader);
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
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly YesAlreadyIPC yesAlreadyIPC;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;

    public EngineState CurrentState { get; private set; } = EngineState.Idle;
    public bool IsRunning => CurrentState != EngineState.Idle && CurrentState != EngineState.Stopped;
    public string StatusMessage { get; private set; } = "Idle";

    private DateTime lastTick = DateTime.MinValue;
    private int outsideDutyTicks = 0;
    private const float LoopInterval = 2.0f;
    private bool autoDutyStartedInDuty = false;
    private DateTime dutyEnteredUtc = DateTime.MinValue;
    private DateTime lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
    private DateTime lastRotationRefreshUtc = DateTime.MinValue;
    private const float PraetoriumDutyReadyFallbackSeconds = 20.0f;
    private const float RotationRefreshIntervalSeconds = 10.0f;
    private const float PraetoriumAggressiveRefreshSeconds = 3.0f;

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
	
	//RSR refresh counter
	private int rsrcounter = 0;

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
        AutoDutyIPC autoDutyIPC, YesAlreadyIPC yesAlreadyIPC,
        ICondition condition, IClientState clientState, ICommandManager commandManager)
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
        this.autoDutyIPC = autoDutyIPC;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.condition = condition;
        this.clientState = clientState;
        this.commandManager = commandManager;

        // Hook duty completed event
        Plugin.DutyStateService.DutyCompleted += OnDutyCompleted;
        ApplyConfiguredPartyLeaderState(reason: "engine init");
    }

    public void Dispose()
    {
        Plugin.DutyStateService.DutyCompleted -= OnDutyCompleted;
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(object? sender, ushort territoryId)
        => OnDutyCompleted((uint)territoryId);

    private void OnDutyCompleted(uint territoryId)
    {
        if (!IsRunning) return;
        dutyCompleted = true;
        dutyCompletedTime = DateTime.UtcNow;
        ResetLeaveTracking();
        PauseLeaderQueueBeforeExitIfRepairNeeded($"Duty completed in territory {territoryId}");
        log.Information($"[MOGTOME][Engine] Duty completed event in territory {territoryId} - leave will request at first safe seam");
    }

    public void Start()
    {
        if (IsRunning)
        {
            log.Warning("[MOGTOME][Engine] Already running");
            return;
        }

        log.Information("[MOGTOME][Engine] Starting MOGTOME engine");
        CurrentState = EngineState.Initializing;
        StatusMessage = "Initializing...";

        _ = StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        var testingModeUnsynced = config.TestingModeUnsynced;

        try
        {
            await GameHelpers.RunOnFrameworkThreadAsync(ClearStaleDutyStateIfNeeded).ConfigureAwait(false);

            StatusMessage = "Checking conflicting plugins...";
            var conflictingPluginsReady = await conflictPluginService.EnsureTwistOfFayteDisabledAsync("MOGTOME start", showPopup: true);
            if (CurrentState != EngineState.Initializing)
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
                var startingInsideDuty = condition[34];
                var abortForMinimumPartySize = !startingInsideDuty && EnforceMinimumPartySizeAtLeaderStart();
                return new StartupSnapshot(startingInsideDuty, abortForMinimumPartySize, state.IsPartyLeader);
            }).ConfigureAwait(false);

            if (startupSnapshot.AbortForMinimumPartySize)
                return;

            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? "Preparing ADS..."
                : "Preparing AutoDuty...";
            var backendReady = await dutyAutomationService.PrepareForStartAsync(startupSnapshot.IsPartyLeader, startupSnapshot.StartingInsideDuty).ConfigureAwait(false);
            if (CurrentState != EngineState.Initializing)
            {
                log.Warning("[MOGTOME][Engine] Start aborted while preparing automation backend");
                return;
            }

            if (!backendReady)
            {
                await GameHelpers.RunOnFrameworkThreadAsync(() =>
                {
                    CurrentState = EngineState.Idle;
                    StatusMessage = "Idle";
                }).ConfigureAwait(false);
                return;
            }

            var preparationResult = await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
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
                    EnterRepairMode(useNpcRepair: ShouldUseNpcRepair(), "Repairing before start...");
                    return new StartupPreparationResult(EnteredRepairMode: true);
                }

                rotationService.Initialize();
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

            if (preparationResult.EnteredRepairMode)
                return;

            if (startupSnapshot.StartingInsideDuty)
            {
                log.Information("[MOGTOME][Engine] Start requested while already inside duty - skipping duty finder setup");
                await GameHelpers.RunOnFrameworkThreadAsync(() =>
                {
                    StatusMessage = "Resuming inside duty...";
                    ResumeOrEnterCurrentDuty();
                }).ConfigureAwait(false);
                return;
            }

            await ConfigureDutyFinderSettingsAsync(enableLevelSync: !testingModeUnsynced).ConfigureAwait(false);

            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (CurrentState != EngineState.Initializing)
                {
                    log.Warning("[MOGTOME][Engine] Start aborted before entering waiting-outside-duty state");
                    return;
                }

                if (dutyAutomationService.UseAdsExperimental && !startupSnapshot.IsPartyLeader)
                    dutyAutomationService.EnsureFollowerOutsideArmed("startup ready");

                CurrentState = EngineState.WaitingOutsideDuty;
                StatusMessage = $"Running - Duty #{state.DutyCounter + 1}";
                log.Information($"[MOGTOME][Engine] Initialized. Leader={state.IsPartyLeader}, Counter={state.DutyCounter}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Initialization failed: {ex.Message}");
            await GameHelpers.RunOnFrameworkThreadAsync(Stop).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        log.Information("[MOGTOME][Engine] Stopping MOGTOME engine");
        CurrentState = EngineState.Stopping;
        StatusMessage = "Stopping...";

        try
        {
            dutyAutomationService.StopDuty();
            dialogHandler.Stop();
            rotationService.DisableRotation();
            dutyQueue.ClearRepairQueuePauseOnStop();
            ResetLeaveTracking();
            ResetRepairRequestState();
            ResetRepairRecoveryWatchdog();
            ResetQueueRegistrationWatchdog();
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Error during stop: {ex.Message}");
        }

        CurrentState = EngineState.Idle;
        StatusMessage = "Idle";
        log.Information("[MOGTOME][Engine] Stopped");
    }

    private void ClearStaleDutyStateIfNeeded()
    {
        if (condition[34])
        {
            if (state.DutyStartTerritory == 0 && clientState.TerritoryType > 0)
            {
                state.DutyStartTerritory = clientState.TerritoryType;
                log.Warning($"[MOGTOME][Engine] DutyStartTerritory was empty while already inside duty - using current territory {state.DutyStartTerritory}");
            }

            return;
        }

        if (!state.IsInDuty && !state.HasEnteredDuty)
            return;

        log.Warning("[MOGTOME][Engine] Clearing stale in-duty state before startup because the client is currently outside duty");
        state.Reset();
        dutyCompleted = false;
        autoDutyStartedInDuty = false;
        dutyEnteredUtc = DateTime.MinValue;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        dutyAutomationService.InvalidateAdsQueueOperations("duty entered");
        ResetQueueRecoveryState();
    }

    private void ResumeOrEnterCurrentDuty()
    {
        dutyCompleted = false;
        autoDutyStartedInDuty = false;
        dutyEnteredUtc = DateTime.UtcNow;
        ResetLeaveTracking();
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        requeueInProgress = false;
        requeueState = RequeueState.Idle;

        if (!state.IsInDuty && !state.HasEnteredDuty)
        {
            log.Information("[MOGTOME][Engine] Starting while already inside duty - entering fresh in-duty state");
            OnEnteredDuty();
            return;
        }

        state.IsInDuty = true;
        state.IsInCombat = condition[26];
        rotationService.ForceRotation();
        CurrentState = EngineState.InDuty;
        StatusMessage = $"In Duty - #{state.DutyCounter} ({dutyTracker.GetCurrentDutyName()})";
        log.Information($"[MOGTOME][Engine] Resuming current duty without re-counting start (HasEnteredDuty={state.HasEnteredDuty}, DutyCounter={state.DutyCounter})");
    }

    private static async Task<bool> WaitForAddonVisibleAsync(string addonName, TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.IsAddonVisible(addonName)).ConfigureAwait(false))
                return true;

            await Task.Delay(pollInterval).ConfigureAwait(false);
        }

        return await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.IsAddonVisible(addonName)).ConfigureAwait(false);
    }

    private async Task ConfigureDutyFinderSettingsAsync(bool enableLevelSync)
    {
        log.Information($"[MOGTOME][Engine] Setting up duty finder for Unsync=ON, LevelSync={(enableLevelSync ? "ON" : "OFF")}");

        try
        {
            log.Debug("[MOGTOME][Engine] Step 1: Opening duty finder");
            await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.SendCommand("/dutyfinder")).ConfigureAwait(false);

            if (!await WaitForAddonVisibleAsync("ContentsFinder", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)).ConfigureAwait(false))
            {
                log.Warning("[MOGTOME][Engine] ContentsFinder addon not visible after /dutyfinder - continuing without verified duty finder UI setup");
                return;
            }

            log.Debug("[MOGTOME][Engine] ContentsFinder addon is visible");

            log.Debug("[MOGTOME][Engine] Step 2: Opening duty finder options");
            await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.FireAddonCallback("ContentsFinder", true, 15)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 3: Setting Unrestricted Party (Unsync)");
            await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 1, 1)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 4: Setting Level Sync");
            await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 2, enableLevelSync ? 1 : 0)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Debug("[MOGTOME][Engine] Step 5: Confirming duty finder settings");
            await GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 0)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);

            log.Information($"[MOGTOME][Engine] Duty finder setup complete: Unsync=ON, LevelSync={(enableLevelSync ? "ON" : "OFF")}");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Failed to set up duty finder: {ex.Message}");
        }
    }

    public void Update()
    {
        if (!IsRunning) return;
        if (!clientState.IsLoggedIn) return;

        // Throttle based on loop interval (hardcoded 2s)
        var now = DateTime.UtcNow;
        if ((now - lastTick).TotalSeconds < LoopInterval) return;
        lastTick = now;

        try
        {
            // Check daily reset
            dutyTracker.CheckDailyReset();

            // Check quit condition
            if (dutyTracker.ShouldQuit())
            {
                HandleQuit();
                return;
            }

            // Update territory
            state.CurrentTerritory = clientState.TerritoryType;
            consumableInventoryService.Refresh();

            // Condition[34] = BoundByDuty
            var inDuty = condition[34];

            if (inDuty && !state.IsInDuty)
            {
                OnEnteredDuty();
            }
            else if (!inDuty && state.IsInDuty)
            {
                OnLeftDuty();
            }

            // Condition[26] = InCombat
            state.IsInCombat = condition[26];

            if (IsRepairFlowActive() && HasQueueRegistrationOrConfirm())
            {
                dutyQueue.CancelDutyPopForRepair();
            }
            else
            {
                // Handle dialogs always unless repair is actively protecting an inn/NPC repair flow.
                dialogHandler.Update();

                // Auto-accept duty pop for non-leaders
                dutyQueue.AutoAcceptDuty();
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
					rsrcounter++;
					if (rsrcounter > 10)
					{
						UpdateInDuty();
						rsrcounter = 0;
					}
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

    private void OnEnteredDuty()
    {
        state.IsInDuty = true;
        state.IsInCombat = condition[26];
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        ResetLeaveTracking();
        dutyEnteredUtc = DateTime.UtcNow;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;

        var enteredTerritory = ResolveEnteredDutyTerritory();
        if (HandleUnexpectedDutyEntry(enteredTerritory))
            return;

        dutyTracker.OnDutyStarted();
        rotationService.ForceRotation();
        
        CurrentState = EngineState.InDuty;
        StatusMessage = $"In Duty - #{state.DutyCounter} ({dutyTracker.GetCurrentDutyName()})";
        log.Information($"[MOGTOME][Engine] Entered duty #{state.DutyCounter}");

        // Always count instances fired up (even unsynced)
        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;
        if (isPrae)
            config.TotalPraes++;
        else
            config.TotalDecus++;
        // Note: ConfigManager.SaveCurrentAccount() will be called by the engine
        // We don't save here to avoid multiple saves during duty counting
    }

    private uint ResolveEnteredDutyTerritory()
    {
        if (state.DutyStartTerritory != 0)
            return state.DutyStartTerritory;

        if (state.CurrentTerritory != 0)
        {
            state.DutyStartTerritory = state.CurrentTerritory;
            log.Warning($"[MOGTOME][Engine] DutyStartTerritory was empty on duty entry; using CurrentTerritory {state.CurrentTerritory}");
        }

        return state.DutyStartTerritory;
    }

    private bool HandleUnexpectedDutyEntry(uint territoryId)
    {
        if (territoryId == DutyState.PraetoriumTerritoryId || territoryId == DutyState.DecumanaTerritoryId)
            return false;

        var territoryName = GameHelpers.GetTerritoryName(territoryId);
        var territoryLabel = territoryId == 0
            ? "Unknown duty"
            : $"{territoryName} ({territoryId})";
        var message = $"Entered unexpected duty {territoryLabel}. Finish optional dungeon unlock quests; Praetorium index is probably wrong.";

        log.Warning($"[MOGTOME][Engine] {message}");
        Plugin.ChatGui.Print($"[MOGTOME] {message}");

        Stop();
        state.Reset();

        if (dutyAutomationService.UseAdsExperimental)
        {
            log.Warning("[MOGTOME][Engine] Unexpected duty entered in ADS mode; sending /ads leave");
            dutyAutomationService.RequestDutyLeave("Unexpected duty entered in ADS mode", DateTime.MinValue, 1);
        }

        return true;
    }

    private void OnLeftDuty()
    {
        // IMPORTANT: Call dutyTracker.OnDutyCompleted() FIRST
        // This calculates completion time and calls RecordRun() to save the record.
        // We must do this before reading stats so we get the FRESH record, not stale data.
        dutyTracker.OnDutyCompleted();

        // Now read the freshly created record for stats update
        if ((!config.TestingModeUnsynced || config.ShowDebugRuns) && runHistoryService.RunHistory.Count > 0)
        {
            var mostRecentRun = runHistoryService.RunHistory.LastOrDefault();
            if (mostRecentRun != null)
            {
                log.Debug($"[MOGTOME][Engine] Stats validation - CompletionTime: {mostRecentRun.CompletionTime:F1}s, BailoutTimeout: {config.BailoutTimeout}s, Valid: {mostRecentRun.CompletionTime > 0 && mostRecentRun.CompletionTime < config.BailoutTimeout}");
                
                if (mostRecentRun.CompletionTime > 0 && mostRecentRun.CompletionTime < config.BailoutTimeout)
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
                    log.Warning($"[MOGTOME][Engine] Skipping stats update - INVALID_COMPLETION_TIME: {mostRecentRun.CompletionTime:F1}s (Valid range: >0 && <{config.BailoutTimeout}s)");
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
        state.IsInDuty = false;
        outsideDutyTicks = 0;
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        ResetLeaveTracking();
        ResetRepairRecoveryWatchdog();
        dutyAutomationService.InvalidateAdsQueueOperations("duty left");
        dutyAutomationService.NotifyDutyLeft();
        ResetQueueRecoveryState();
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        
        CurrentState = EngineState.WaitingOutsideDuty;
        StatusMessage = $"Outside Duty - Next: #{state.DutyCounter + 1}";
        log.Information($"[MOGTOME][Engine] Left duty. Next: #{state.DutyCounter + 1}");

        // Save configuration after leaving duty (stats updates, counter changes, etc.)
        try
        {
            // This needs to be called via the plugin since we don't have direct access to ConfigManager here
            // The plugin will handle the actual save
            log.Debug("[MOGTOME][Engine] Configuration save requested after leaving duty");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Engine] Failed to save configuration after leaving duty: {ex.Message}");
        }

        // Check if we should continue running
        if (state.DutyCounter < config.MaxRuns)
        {
            if (state.IsPartyLeader)
            {
                log.Information($"[MOGTOME][Engine] Starting leader requeue sequence - {state.DutyCounter}/{config.MaxRuns} completed");
                
                // Start requeue state machine with 10s delay to prevent crashes
                requeueState = RequeueState.WaitingAfterLeave;
                requeueStartTime = DateTime.UtcNow;
                requeueInProgress = true;
            }
            else
            {
                log.Information($"[MOGTOME][Engine] Non-leader ready for next duty - {state.DutyCounter}/{config.MaxRuns} completed");
                // Non-leader continues running; selected backend starts after next duty entry
            }
        }
        else
        {
            log.Information($"[MOGTOME][Engine] Run limit reached - stopping");
            Stop();
        }
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

            EnterRepairMode(useNpcRepair: ShouldUseNpcRepair(), "Repairing...");
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
            StartQueueAttempt(isPrae, ignoreCooldown: false, "Queueing");
        }
        else if (!state.IsPartyLeader)
        {
            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? $"Waiting for leader - #{state.DutyCounter + 1} ({dutyAutomationService.GetAdsRuntimeStatusLabel()})"
                : $"Waiting for leader - #{state.DutyCounter + 1}";
        }
        else if (delayedRequeueInProgress)
        {
            StatusMessage = $"Delayed requeue in progress...";
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
                    StatusMessage = $"Waiting after leave duty ({10.0 - elapsed:F0}s)";
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
                    StatusMessage = $"Waiting to stop {dutyAutomationService.ActiveBackendDisplayName} ({2.0 - elapsed:F0}s)";
                    break;
                    
                case RequeueState.StoppingBackend:
                    if (elapsed >= 1.0) // Give time for stop to process
                    {
                        requeueState = RequeueState.WaitingToQueue;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    StatusMessage = $"Stopping {dutyAutomationService.ActiveBackendDisplayName}...";
                    break;
                    
                case RequeueState.WaitingToQueue:
                    if (elapsed >= 1.0)
                    {
                        if (StartQueueAttempt(isPrae, ignoreCooldown: false, "Auto-queueing"))
                        {
                            requeueState = RequeueState.Complete;
                            requeueInProgress = false;
                            log.Information($"[MOGTOME][Engine] Auto-queue command sent for next run: {dutyTracker?.GetCurrentDutyName() ?? "Unknown"}");
                            return;
                        }
                        break;
                    }
                    StatusMessage = $"Waiting to queue ({1.0 - elapsed:F0}s)";
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
                    StatusMessage = "Queueing...";
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

        var dutyName = dutyTracker.GetCurrentDutyName();
        if (HasQueueRegistrationOrConfirm())
        {
            if (queueRegistrationStartedUtc != DateTime.MinValue)
            {
                var elapsed = (DateTime.UtcNow - queueRegistrationStartedUtc).TotalSeconds;
                log.Information($"[MOGTOME][Engine] Queue registration detected for {dutyName} after {elapsed:F1}s");
                ResetQueueRegistrationWatchdog();
            }

            dutyAutomationService.ConfirmQueueRegistration(dutyTracker.ShouldRunPraetorium());
            dutyAutomationService.ClearAdsQueueFailure();
            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? $"Queueing: {dutyName} (registered, {dutyAutomationService.GetAdsRuntimeStatusLabel()})"
                : $"Queueing: {dutyName} (registered)";
            return;
        }

        if (dutyAutomationService.TryGetAdsQueueFailure(out var failureReason, out var cooldownRemainingSeconds))
        {
            if (cooldownRemainingSeconds > 0)
            {
                StatusMessage = $"Queueing: {dutyName} (ContentsFinder retry cooldown {Math.Ceiling(cooldownRemainingSeconds):F0}s, {dutyAutomationService.GetAdsRuntimeStatusLabel()})";
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

            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? $"Queueing: {dutyName} (waiting for queue registration {Math.Ceiling(elapsed):F0}/{QueueRegistrationWatchdogSeconds:F0}s, {dutyAutomationService.GetAdsRuntimeStatusLabel()})"
                : $"Queueing: {dutyName} (waiting for queue registration {Math.Ceiling(elapsed):F0}/{QueueRegistrationWatchdogSeconds:F0}s)";
            return;
        }

        // If we're now in duty, the state will change via OnEnteredDuty
        // Otherwise keep waiting
        if (!condition[34])
        {
            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? $"Queueing: {dutyName} (waiting, {dutyAutomationService.GetAdsRuntimeStatusLabel()})"
                : $"Queueing: {dutyName} (waiting...)";
        }
    }

    private void UpdateInDuty()
    {
        RefreshInDutyRotationIfNeeded();

        // Start selected automation backend if not already started (handles the case where we're already in duty)
        if (!autoDutyStartedInDuty)
        {
            if (!IsReadyToStartDutyBackendInsideDuty())
                return;

            autoDutyStartedInDuty = true;
            log.Information($"[MOGTOME][Engine] Starting {dutyAutomationService.ActiveBackendDisplayName} inside duty");
            dutyAutomationService.StartDutyInside(state.IsPartyLeader);
        }

        // Duty completion exit logic
        if (dutyCompleted)
        {
            if (IsLeaveBlocked(out var leaveBlocker))
            {
                LogLeaveBlocker(leaveBlocker);
                StatusMessage = $"Duty complete - leave blocked ({leaveBlocker})";
                return;
            }

            lastLeaveBlocker = string.Empty;
            ObserveLeaveConfirmationEvidence();

            if (leaveRequestedUtc == DateTime.MinValue)
            {
                LeaveDuty();
                return;
            }

            var settleElapsed = (DateTime.UtcNow - leaveRequestedUtc).TotalSeconds;
            if (settleElapsed < DutyExitSettleSeconds)
            {
                StatusMessage = $"Leave requested - waiting for zone-out ({DutyExitSettleSeconds - settleElapsed:F0}s)";
                return;
            }

            LeaveDuty();
            return;
        }

        // Boss combat handler
        bossHandler.Update();

        // Stuck detection
        stuckDetection.Update();

        StatusMessage = $"In Duty #{state.DutyCounter} - {state.TimeInDuty:F0}s";
    }

    private bool IsLeaveBlocked(out string blocker)
    {
        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene])
        {
            blocker = "cutscene";
            return true;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.Occupied33] ||
            condition[ConditionFlag.Occupied39])
        {
            blocker = "occupied transition";
            return true;
        }

        blocker = string.Empty;
        return false;
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

        if (GameHelpers.IsAddonVisible("SelectYesno"))
        {
            leaveConfirmationObserved = true;
            dutyAutomationService.ObserveLeaveConfirmationEvidence("SelectYesno visible");
        }
    }

    private void ResetLeaveTracking()
    {
        leaveRequestedUtc = DateTime.MinValue;
        leaveConfirmationObserved = false;
        lastLeaveBlocker = string.Empty;
        lastLeaveAttemptTime = DateTime.MinValue;
        leaveAttemptCount = 0;
    }

    private bool IsReadyToStartDutyBackendInsideDuty()
    {
        if (state.DutyStartTerritory != DutyState.PraetoriumTerritoryId)
            return true;

        var remainingTime = GameHelpers.GetDutyRemainingTime();
        if (remainingTime > 0f && remainingTime < DutyState.PraetoriumTimeLimit)
            return true;

        var now = DateTime.UtcNow;
        var secondsSinceEnter = dutyEnteredUtc == DateTime.MinValue
            ? double.MaxValue
            : (now - dutyEnteredUtc).TotalSeconds;

        if (remainingTime > 0f)
        {
            StatusMessage = $"In Duty - waiting for Praetorium timer ({remainingTime:F0}s)";
            if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
            {
                lastPraetoriumReadyWaitLogUtc = now;
                log.Information($"[MOGTOME][Engine] Praetorium duty entered but timer is still at {remainingTime:F0}s; waiting before starting {dutyAutomationService.ActiveBackendDisplayName}");
            }
            return false;
        }

        if (secondsSinceEnter < PraetoriumDutyReadyFallbackSeconds)
        {
            StatusMessage = $"In Duty - waiting for Praetorium timer ({PraetoriumDutyReadyFallbackSeconds - secondsSinceEnter:F0}s fallback)";
            if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
            {
                lastPraetoriumReadyWaitLogUtc = now;
                log.Information($"[MOGTOME][Engine] Praetorium duty timer not visible yet; waiting {PraetoriumDutyReadyFallbackSeconds - secondsSinceEnter:F0}s more before fallback start");
            }
            return false;
        }

        if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
        {
            lastPraetoriumReadyWaitLogUtc = now;
            log.Warning($"[MOGTOME][Engine] Praetorium duty timer never appeared; allowing {dutyAutomationService.ActiveBackendDisplayName} start after fallback wait");
        }

        return true;
    }

    private void RefreshInDutyRotationIfNeeded()
    {
        if (!state.IsInDuty || dutyCompleted)
            return;

        var now = DateTime.UtcNow;
        var currentTarget = Plugin.TargetManager.Target as IBattleChara;
        var targetName = currentTarget?.Name.TextValue ?? string.Empty;
        var aggressiveRefresh =
            state.DutyStartTerritory == DutyState.PraetoriumTerritoryId &&
            (!condition[ConditionFlag.InCombat] ||
             currentTarget == null ||
             currentTarget.CurrentHp <= 1 ||
             targetName.Contains(PhantomGaiusName, StringComparison.OrdinalIgnoreCase));

        var refreshInterval = aggressiveRefresh
            ? PraetoriumAggressiveRefreshSeconds
            : RotationRefreshIntervalSeconds;

        if ((now - lastRotationRefreshUtc).TotalSeconds < refreshInterval)
            return;

        lastRotationRefreshUtc = now;
        rotationService.EnableRotation();
        log.Debug("[MOGTOME][Engine] Refreshed BossMod AI + RSR inside duty ({Mode}, target={Target}, hp={Hp})",
            aggressiveRefresh ? "aggressive" : "normal",
            targetName.Length > 0 ? targetName : "none",
            currentTarget?.CurrentHp ?? 0);
    }

    private void UpdateRepairing()
    {
        outsideDutyTicks++;
        StatusMessage = "Repairing...";

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

        StatusMessage = "Repair done, resuming";
    }

    private bool ShouldUseNpcRepair()
    {
        if (!dutyAutomationService.UseAdsExperimental)
            return !state.IsPartyLeader;

        return !config.UseAdsSelfRepair;
    }

    private void EnterRepairMode(bool useNpcRepair, string statusMessage)
    {
        ResetRepairRequestState();
        ResetRepairRecoveryWatchdog();
        ResetQueueRegistrationWatchdog();
        CurrentState = EngineState.RepairingOutside;
        StatusMessage = statusMessage;
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

    private void RetryRepairRequestIfNeeded()
    {
        if (IsRepairRetryBlocked(out var blocker))
        {
            StatusMessage = $"Repairing - waiting for {blocker}";
            LogRepairRetryBlocked(blocker);
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

    private bool IsRepairRetryBlocked(out string blocker)
    {
        if (state.IsInDuty || condition[ConditionFlag.BoundByDuty])
        {
            blocker = "duty state";
            return true;
        }

        if (condition[ConditionFlag.BetweenAreas])
        {
            blocker = "zone transition";
            return true;
        }

        if (HasQueueRegistrationCondition())
        {
            blocker = "queue registration";
            return true;
        }

        if (GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
        {
            blocker = "duty confirm popup";
            return true;
        }

        if (GameHelpers.IsAddonVisible("SelectYesno"))
        {
            blocker = "SelectYesno dialog";
            return true;
        }

        if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene])
        {
            blocker = "cutscene";
            return true;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.Occupied33] ||
            condition[ConditionFlag.Occupied39])
        {
            blocker = "occupied transition";
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
        => CurrentState == EngineState.RepairingOutside || state.AutoQueueDisabledForRepair;

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

    private bool StartQueueAttempt(bool isPrae, bool ignoreCooldown, string statusPrefix)
    {
        var dutyName = dutyTracker.GetCurrentDutyName();

        if (dutyAutomationService.IsAdsQueueRetryCooldownActive(out var cooldownRemainingSeconds, out var cooldownReason))
        {
            CurrentState = EngineState.WaitingOutsideDuty;
            StatusMessage = dutyAutomationService.UseAdsExperimental
                ? $"ContentsFinder queue cooldown: {dutyName} ({Math.Ceiling(cooldownRemainingSeconds):F0}s, {dutyAutomationService.GetAdsRuntimeStatusLabel()})"
                : $"ContentsFinder queue cooldown: {dutyName} ({Math.Ceiling(cooldownRemainingSeconds):F0}s)";
            log.Debug($"[MOGTOME][Engine] Holding {statusPrefix} for {dutyName}; ContentsFinder queue cooldown active ({Math.Ceiling(cooldownRemainingSeconds):F0}s remaining; {cooldownReason})");
            return false;
        }

        CurrentState = EngineState.Queueing;
        StatusMessage = dutyAutomationService.UseAdsExperimental
            ? $"{statusPrefix}: {dutyName} ({dutyAutomationService.GetQueueStatusLabel()} / {dutyAutomationService.GetAdsRuntimeStatusLabel()})"
            : $"{statusPrefix}: {dutyName}";

        try
        {
            if (ignoreCooldown)
                dutyQueue.ForceQueue(isPrae);
            else
                dutyQueue.TryQueue(isPrae);

            queueRegistrationStartedUtc = DateTime.UtcNow;
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
        StatusMessage = $"Queue recovery: waiting after {dutyAutomationService.StopCommandLabel} ({QueueRecoveryStopSeconds:F0}s)";
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

        if (HasQueueRegistrationOrConfirm())
        {
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
            StatusMessage = $"Queue recovery: waiting after {dutyAutomationService.StopCommandLabel} ({Math.Ceiling(stopRemaining):F0}s)";
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
            StatusMessage = $"Queue recovery: allowing repairs ({Math.Ceiling(repairRemaining):F0}s)";
            return true;
        }

        queueRecoveryStopUntilUtc = DateTime.MinValue;
        queueRecoveryResumeUtc = DateTime.MinValue;
        CurrentState = EngineState.WaitingOutsideDuty;
        outsideDutyTicks = 0;
        StatusMessage = "Queue recovery: resuming duty entry";
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
                StatusMessage = $"Repair recovery: waiting to requeue ({Math.Ceiling(remaining):F0}s)";
                return true;
            }

            repairRecoveryRetryReadyUtc = DateTime.MinValue;
            repairRecoveryWatchStartedUtc = now;

            if (state.IsPartyLeader)
            {
                var isPrae = dutyTracker.ShouldRunPraetorium();
                var dutyName = dutyTracker.GetCurrentDutyName();
                log.Warning($"[MOGTOME][Engine] Repair recovery retry {repairRecoveryAttempts}: force-queueing {dutyName} after {dutyAutomationService.StopCommandLabel}");
                StartQueueAttempt(isPrae, ignoreCooldown: true, $"Repair recovery retry {repairRecoveryAttempts}");
                return true;
            }

            StatusMessage = $"Repair recovery retry {repairRecoveryAttempts}: waiting for leader";
            return true;
        }

        var elapsed = (now - repairRecoveryWatchStartedUtc).TotalSeconds;
        if (elapsed < RepairRecoveryWatchdogSeconds)
            return false;

        repairRecoveryAttempts++;
        if (state.IsPartyLeader)
        {
            var dutyName = dutyTracker.GetCurrentDutyName();
            log.Warning($"[MOGTOME][Engine] Still outside duty {elapsed:F0}s after repair; sending {dutyAutomationService.StopCommandLabel} and retrying {dutyName} (attempt {repairRecoveryAttempts})");
            dutyAutomationService.StopDuty();
            repairRecoveryRetryReadyUtc = now.AddSeconds(RepairRecoveryStopDelaySeconds);
            StatusMessage = $"Repair recovery: restarting {dutyName}";
            return true;
        }

        log.Warning($"[MOGTOME][Engine] Still outside duty {elapsed:F0}s after repair; sending {dutyAutomationService.StopCommandLabel} and waiting for leader retry (attempt {repairRecoveryAttempts})");
        dutyAutomationService.StopDuty();
        repairRecoveryWatchStartedUtc = now;
        StatusMessage = "Repair recovery: waiting for leader";
        return true;
    }

    private void HandleQuit()
    {
        log.Information($"[MOGTOME][Engine] Quit condition reached: {state.DutyCounter} runs completed");
        Stop();

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
        var leaveReason = $"Exit on first safe seam after duty complete ({elapsed:F1}s since completion)";

        if (dutyAutomationService.UseAdsExperimental)
        {
            log.Information($"[MOGTOME][Engine] ADS leave attempt #{leaveAttemptCount} - REASON: {leaveReason}");
            dutyAutomationService.RequestDutyLeave(leaveReason, dutyCompletedTime, leaveAttemptCount);
            StatusMessage = $"Leave requested via ADS (attempt #{leaveAttemptCount}) - waiting for zone-out";
            return;
        }

        log.Information($"[MOGTOME][Engine] Leave duty attempt #{leaveAttemptCount} - REASON: {leaveReason}");
        log.Information("[MOGTOME][Engine] Opening duty panel to leave");

        // Open duty panel to access Leave Duty button
        GameHelpers.SendCommand("/dutyfinder");

        GameHelpers.QueueFrameworkAction("Engine leave", "open leave duty button", TimeSpan.FromMilliseconds(500), TryClickLeaveDutyButton);
        GameHelpers.QueueFrameworkAction("Engine leave", "confirm leave duty", TimeSpan.FromMilliseconds(1000), () =>
        {
            if (GameHelpers.ClickYesIfVisible())
                log.Information("[MOGTOME][Engine] Successfully clicked Yes on leave duty confirmation");
        });

        StatusMessage = $"Leave requested (attempt #{leaveAttemptCount}) - waiting for zone-out";
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
            
            GameHelpers.QueueFrameworkAction("Engine leave", "click leave button", TimeSpan.FromMilliseconds(500), TryClickLeaveButton);
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
            
            GameHelpers.QueueFrameworkAction("Engine leave", "handle leave confirmation", TimeSpan.FromMilliseconds(500), HandleLeaveConfirmation);
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
            GameHelpers.ClickYesIfVisible();
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

    private bool EnforceMinimumPartySizeAtLeaderStart()
    {
        if (config.TestingModeUnsynced || condition[34] || config.IsCrossWorldParty || !config.IsPartyLeader)
            return false;

        var partyMemberCount = GetCurrentPartyMemberCount();
        if (partyMemberCount >= MinimumSyncedPartyMembers)
            return false;

        var message = $"Need at least {MinimumSyncedPartyMembers} visible same-world party members before starting synced MOGTOME as leader. Current count: {partyMemberCount}. Cross-world parties and non-leaders are exempt.";
        log.Warning($"[MOGTOME][Engine] start gate: {message} Party={GetPartyComposition()}");
        Plugin.ChatGui.Print($"[MOGTOME] {message}");
        Stop();
        return true;
    }

    private void PauseLeaderQueueBeforeExitIfRepairNeeded(string reason)
    {
        if (!state.IsPartyLeader)
            return;

        if (state.AutoQueueDisabledForRepair)
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
            var message = "Manual party refresh skipped inside duty. Use Refresh Party State only outside duty after the full party has zoned out and become visible.";
            log.Warning($"[MOGTOME][Engine] {message} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print($"[MOGTOME] {message}");
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
            var message = $"Manual party refresh could not determine leader from the current same-world party list. Keeping {(state.IsPartyLeader ? "leader" : "non-leader")} role.";
            log.Warning($"[MOGTOME][Engine] {message} {details} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print($"[MOGTOME] {message}");
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
