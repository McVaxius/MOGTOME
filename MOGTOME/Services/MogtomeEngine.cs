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

    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly DutyTrackerService dutyTracker;
    private readonly DutyQueueService dutyQueue;
    private readonly RepairService repairService;
    private readonly FoodService foodService;
    private readonly RotationService rotationService;
    private readonly BossHandlerService bossHandler;
    private readonly StuckDetectionService stuckDetection;
    private readonly DialogHandlerService dialogHandler;
    private readonly AutoDutyPathService autoDutyPath;
    private readonly ConflictPluginService conflictPluginService;
    private readonly RunHistoryService runHistoryService; // NEW
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly AutomatonIPC automatonIPC;
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
    private const int DutyExitDelaySeconds = 10;
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

    public enum RequeueState
    {
        Idle,
        WaitingAfterLeave,  // Wait 10s after leaving duty to prevent crashes
        WaitingToStop,     // Wait 2s after leaving duty
        StoppingAutoDuty,   // Execute /ad stop
        WaitingToQueue,    // Wait 1s after stop
        Queueing,           // Execute queue command
        Complete,           // Successfully queued
        Failed              // Max attempts reached
    }

    public MogtomeEngine(
        IPluginLog log, Configuration config, DutyState state,
        DutyTrackerService dutyTracker, DutyQueueService dutyQueue,
        RepairService repairService, FoodService foodService,
        RotationService rotationService, BossHandlerService bossHandler,
        StuckDetectionService stuckDetection, DialogHandlerService dialogHandler,
        AutoDutyPathService autoDutyPath, ConflictPluginService conflictPluginService, RunHistoryService runHistoryService, // NEW
        AutoDutyIPC autoDutyIPC, AutomatonIPC automatonIPC, YesAlreadyIPC yesAlreadyIPC,
        ICondition condition, IClientState clientState, ICommandManager commandManager)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.dutyTracker = dutyTracker;
        this.dutyQueue = dutyQueue;
        this.repairService = repairService;
        this.foodService = foodService;
        this.rotationService = rotationService;
        this.bossHandler = bossHandler;
        this.stuckDetection = stuckDetection;
        this.dialogHandler = dialogHandler;
        this.autoDutyPath = autoDutyPath;
        this.conflictPluginService = conflictPluginService;
        this.runHistoryService = runHistoryService; // NEW
        this.autoDutyIPC = autoDutyIPC;
        this.automatonIPC = automatonIPC;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.condition = condition;
        this.clientState = clientState;
        this.commandManager = commandManager;

        // Hook duty completed event
        Plugin.DutyStateService.DutyCompleted += OnDutyCompleted;
        ApplyConfiguredPartyLeaderState(applyAutoQueuePolicy: false, reason: "engine init");
    }

    public void Dispose()
    {
        Plugin.DutyStateService.DutyCompleted -= OnDutyCompleted;
    }

    private void OnDutyCompleted(object? sender, ushort territoryId)
    {
        if (!IsRunning) return;
        dutyCompleted = true;
        dutyCompletedTime = DateTime.UtcNow;
        DisableLeaderAutoQueueBeforeExitIfRepairNeeded($"Duty completed in territory {territoryId}");
        log.Information($"[Engine] Duty completed event in territory {territoryId} - will leave in {DutyExitDelaySeconds}s");
    }

    public async void Start()
    {
        if (IsRunning)
        {
            log.Warning("[Engine] Already running");
            return;
        }

        log.Information("[Engine] Starting MOGTOME engine");
        CurrentState = EngineState.Initializing;
        StatusMessage = "Initializing...";

        try
        {
            ClearStaleDutyStateIfNeeded();

            StatusMessage = "Checking conflicting plugins...";
            var conflictingPluginsReady = await conflictPluginService.EnsureTwistOfFayteDisabledAsync("MOGTOME start", showPopup: true);
            if (CurrentState != EngineState.Initializing)
            {
                log.Warning("[Engine] Start aborted while resolving conflicting plugins");
                return;
            }

            if (!conflictingPluginsReady)
            {
                log.Warning("[Engine] Twist of Fayte warning path reported a soft failure, but startup will continue");
            }

            log.Information("[Engine] Waiting for AutoDuty to finish profile initialization");
            var autoDutyReady = await autoDutyPath.WaitForAutoDutyInitializationAsync(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500));
            if (CurrentState != EngineState.Initializing)
            {
                log.Warning("[Engine] Start aborted while waiting for AutoDuty readiness");
                return;
            }

            if (!autoDutyReady)
            {
                const string startupFailure = "AutoDuty is still initializing or faulted; retry MOGTOME after login settles";
                log.Warning($"[Engine] {startupFailure}");
                Plugin.ChatGui.Print($"[MOGTOME] {startupFailure}");
                CurrentState = EngineState.Idle;
                StatusMessage = "Idle";
                return;
            }

            log.Information("[Engine] AutoDuty readiness gate passed");

            StatusMessage = "Installing bundled AutoDuty paths...";
            var bundledPathsReady = await autoDutyPath.EnsurePathExists();
            if (CurrentState != EngineState.Initializing)
            {
                log.Warning("[Engine] Start aborted while installing bundled AutoDuty paths");
                return;
            }

            if (!bundledPathsReady)
            {
                const string pathFailure = "Bundled Praetorium paths could not be installed into AutoDuty.";
                log.Warning($"[Engine] {pathFailure}");
                Plugin.ChatGui.Print($"[MOGTOME] {pathFailure}");
                CurrentState = EngineState.Idle;
                StatusMessage = "Idle";
                return;
            }

            var startingInsideDuty = condition[34];
            if (!startingInsideDuty && EnforceMinimumPartySizeAtLeaderStart())
                return;

            // 1. Send /ad stop FIRST to reset AutoDuty state for all characters
            log.Information("[Engine] Sending /ad stop to reset AutoDuty state");
            commandManager.ProcessCommand("/ad stop");

            log.Information("[Engine] Sending /at enable as part of startup command prep");
            GameHelpers.SendCommand("/at enable");

            log.Information($"[Engine] Using current party role at start: IsLeader={state.IsPartyLeader}, ConfiguredLeader={config.IsPartyLeader}, CrossWorld={config.IsCrossWorldParty}");

            // 2. Configure AutoDuty BEFORE setting path
            autoDutyIPC.ConfigureForMogtome(state.IsPartyLeader);

            // 3. THEN: Force path selection via reflection (after configuration)
            if (!condition[34]) // Only while not in duty
            {
                log.Information("[Engine] Forcing AutoDuty path selection via reflection (post-config)");
                autoDutyPath.ForcePathSelection(config.PraetoriumPathFileName);
            }

            // 5. Check for repair needs before starting
            log.Information("[Engine] Checking repair status before start");
            if (repairService.NeedsRepair())
            {
                log.Information("[Engine] Repair needed - repairing before start");
                EnterRepairMode(useNpcRepair: !state.IsPartyLeader, "Repairing before start...");
                // Don't set IsRunning yet - repair will complete first
                return;
            }

            // 6. Initialize rotation
            rotationService.Initialize();

            // 7. Pause YesAlready - we handle dialogs directly
            dialogHandler.Start();

            // 8. Apply the live AutoQueue policy for this character role
            dutyQueue.ApplyAutoQueuePolicyAtStart();

            // Check potion availability
            state.PotionsAvailable = config.PotionItemId > 0 &&
                                     GameHelpers.GetInventoryItemCount((uint)config.PotionItemId, config.PotionUseHighQuality) > 0;
            state.FoodAvailable = config.FoodItemId > 0 &&
                                  GameHelpers.GetInventoryItemCount((uint)config.FoodItemId, config.FoodUseHighQuality) > 0;

            // Calculate timeouts
            state.CalculateTimeouts(LoopInterval);

            // Both modes always use Unsync=true to avoid queuing with strangers.
            // Testing mode: Unsync ON + LevelSync OFF (overpowered solo, fast clear)
            // Normal mode:  Unsync ON + LevelSync ON  (appropriate level with party to get rewards)
            autoDutyIPC.SetConfig("Unsynced", "true");
            
            if (config.TestingModeUnsynced)
            {
                autoDutyIPC.SetConfig("LevelSync", "false");
                GameHelpers.SetDutyFinderLevelSync(false);
                log.Information("[Engine] Testing mode: Unsync=ON, LevelSync=OFF");
            }
            else
            {
                autoDutyIPC.SetConfig("LevelSync", "true");
                GameHelpers.SetDutyFinderLevelSync(true);

                if (startingInsideDuty)
                {
                    log.Information("[Engine] Start requested while already inside duty - skipping duty finder setup");
                }
                else
                {
                // Normal mode: Manually set duty finder options for Unsync+LevelSync
                // AutoDuty IPC doesn't work for LevelSync, so we do it manually
                log.Information("[Engine] Normal mode: Setting up duty finder for Unsync+LevelSync");
                
                try
                {
                    // 1. Open Duty Finder - use CommandHelper pattern
                    log.Debug("[Engine] Step 1: Opening duty finder");
                    
                    // Try CommandManager first (for plugin commands)
                    var commandProcessed = commandManager.ProcessCommand("/dutyfinder");
                    if (commandProcessed)
                    {
                        log.Debug("[Engine] /dutyfinder command processed by CommandManager");
                    }
                    else
                    {
                        log.Debug("[Engine] /dutyfinder not handled by CommandManager, trying UIModule fallback");
                        
                        // Fallback: Send through UIModule for native FF14 commands
                        try
                        {
                            unsafe
                            {
                                var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
                                if (uiModule == null)
                                {
                                    throw new InvalidOperationException("UIModule is null, cannot send /dutyfinder command");
                                }

                                var bytes = System.Text.Encoding.UTF8.GetBytes("/dutyfinder");
                                var utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromSequence(bytes);
                                uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
                                log.Debug("[Engine] /dutyfinder sent via UIModule successfully");
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"Failed to send /dutyfinder via UIModule: {ex.Message}", ex);
                        }
                    }
                    
                    // Wait for it to appear
                    if (!await WaitForAddonVisibleAsync("ContentsFinder", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)))
                    {
                        log.Warning("[Engine] ContentsFinder addon not visible after /dutyfinder - continuing without verified duty finder UI setup");
                    }
                    else
                    {
                        log.Debug("[Engine] ContentsFinder addon is visible");
                        
                        // 2. Open Options
                        log.Debug("[Engine] Step 2: Opening duty finder options");
                        GameHelpers.FireAddonCallback("ContentsFinder", true, 15);
                        await Task.Delay(2000);
                        
                        // 3. Set Unrestricted Party (Unsync)
                        log.Debug("[Engine] Step 3: Setting Unrestricted Party (Unsync)");
                        GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 1, 1);
                        await Task.Delay(2000);
                        
                        // 4. Set Level Sync
                        log.Debug("[Engine] Step 4: Setting Level Sync");
                        GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 1, 2, 1);
                        await Task.Delay(2000);
                        
                        // 5. Confirm
                        log.Debug("[Engine] Step 5: Confirming duty finder settings");
                        GameHelpers.FireAddonCallback("ContentsFinderSetting", true, 0);
                        await Task.Delay(2000);
                        
                        log.Information("[Engine] Duty finder setup complete: Unsync=ON, LevelSync=ON");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[Engine] Failed to set up duty finder: {ex.Message}");
                    // Continue anyway - AutoDuty will still try to queue
                }
                }
            }

            if (startingInsideDuty)
            {
                StatusMessage = "Resuming inside duty...";
                ResumeOrEnterCurrentDuty();
                return;
            }

            CurrentState = EngineState.WaitingOutsideDuty;
            StatusMessage = $"Running - Duty #{state.DutyCounter + 1}";
            log.Information($"[Engine] Initialized. Leader={state.IsPartyLeader}, Counter={state.DutyCounter}");
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Initialization failed: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        log.Information("[Engine] Stopping MOGTOME engine");
        CurrentState = EngineState.Stopping;
        StatusMessage = "Stopping...";

        try
        {
            autoDutyIPC.StopDuty();
            dialogHandler.Stop();
            rotationService.DisableRotation();
            dutyQueue.EnsureAutoQueueDisabledOnStop();
            ResetRepairRequestState();
            ResetRepairRecoveryWatchdog();
            ResetQueueRegistrationWatchdog();
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Error during stop: {ex.Message}");
        }

        CurrentState = EngineState.Idle;
        StatusMessage = "Idle";
        log.Information("[Engine] Stopped");
    }

    private void ClearStaleDutyStateIfNeeded()
    {
        if (condition[34])
        {
            if (state.DutyStartTerritory == 0 && clientState.TerritoryType > 0)
            {
                state.DutyStartTerritory = (ushort)clientState.TerritoryType;
                log.Warning($"[Engine] DutyStartTerritory was empty while already inside duty - using current territory {state.DutyStartTerritory}");
            }

            return;
        }

        if (!state.IsInDuty && !state.HasEnteredDuty)
            return;

        log.Warning("[Engine] Clearing stale in-duty state before startup because the client is currently outside duty");
        state.Reset();
        dutyCompleted = false;
        autoDutyStartedInDuty = false;
        dutyEnteredUtc = DateTime.MinValue;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
    }

    private void ResumeOrEnterCurrentDuty()
    {
        dutyCompleted = false;
        autoDutyStartedInDuty = false;
        dutyEnteredUtc = DateTime.UtcNow;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        requeueInProgress = false;
        requeueState = RequeueState.Idle;

        if (!state.IsInDuty && !state.HasEnteredDuty)
        {
            log.Information("[Engine] Starting while already inside duty - entering fresh in-duty state");
            OnEnteredDuty();
            return;
        }

        state.IsInDuty = true;
        state.IsInCombat = condition[26];
        rotationService.ForceRotation();
        CurrentState = EngineState.InDuty;
        StatusMessage = $"In Duty - #{state.DutyCounter} ({dutyTracker.GetCurrentDutyName()})";
        log.Information($"[Engine] Resuming current duty without re-counting start (HasEnteredDuty={state.HasEnteredDuty}, DutyCounter={state.DutyCounter})");
    }

    private static async Task<bool> WaitForAddonVisibleAsync(string addonName, TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GameHelpers.IsAddonVisible(addonName))
                return true;

            await Task.Delay(pollInterval);
        }

        return GameHelpers.IsAddonVisible(addonName);
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
            state.CurrentTerritory = (ushort)(clientState.TerritoryType);

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

            if (IsRepairFlowActive() && (HasQueueRegistrationCondition() || GameHelpers.IsAddonVisible("ContentsFinderConfirm")))
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
                    UpdateInDuty();
                    break;
                case EngineState.RepairingOutside:
                    UpdateRepairing();
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Update error: {ex.Message}");
        }
    }

    private void OnEnteredDuty()
    {
        state.IsInDuty = true;
        dutyTracker.OnDutyStarted();
        rotationService.ForceRotation();
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        dutyEnteredUtc = DateTime.UtcNow;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        lastRotationRefreshUtc = DateTime.MinValue;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        
        CurrentState = EngineState.InDuty;
        StatusMessage = $"In Duty - #{state.DutyCounter} ({dutyTracker.GetCurrentDutyName()})";
        log.Information($"[Engine] Entered duty #{state.DutyCounter}");

        // Always count instances fired up (even unsynced)
        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;
        if (isPrae)
            config.TotalPraes++;
        else
            config.TotalDecus++;
        // Note: ConfigManager.SaveCurrentAccount() will be called by the engine
        // We don't save here to avoid multiple saves during duty counting
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
                log.Debug($"[Engine] Stats validation - CompletionTime: {mostRecentRun.CompletionTime:F1}s, BailoutTimeout: {config.BailoutTimeout}s, Valid: {mostRecentRun.CompletionTime > 0 && mostRecentRun.CompletionTime < config.BailoutTimeout}");
                
                if (mostRecentRun.CompletionTime > 0 && mostRecentRun.CompletionTime < config.BailoutTimeout)
                {
                    var partyComp = string.Join(", ", mostRecentRun.PartyMembers);
                    var dateStr = mostRecentRun.Timestamp.ToString("yyyy-MM-dd HH:mm UTC");

                    log.Debug($"[Engine] Updating stats - Run: {mostRecentRun.CompletionTime:F1}s, Party: [{partyComp}], Date: {dateStr}, Territory: {mostRecentRun.TerritoryId}, IsPrae: {mostRecentRun.IsPraetorium}");

                    // Update global stats (kept for compatibility)
                    if (mostRecentRun.CompletionTime < config.BestTimeEver)
                    {
                        var oldBest = config.BestTimeEver;
                        config.BestTimeEver = mostRecentRun.CompletionTime;
                        config.BestTimeDate = dateStr;
                        config.BestTimeParty = partyComp;
                        log.Information($"[Engine] NEW BEST TIME: {oldBest:F1}s → {mostRecentRun.CompletionTime:F1}s by {partyComp}");
                    }

                    if (mostRecentRun.CompletionTime > config.LongestRunEver)
                    {
                        var oldLongest = config.LongestRunEver;
                        config.LongestRunEver = mostRecentRun.CompletionTime;
                        config.LongestRunDate = dateStr;
                        config.LongestRunParty = partyComp;
                        log.Information($"[Engine] NEW LONGEST RUN: {oldLongest:F1}s → {mostRecentRun.CompletionTime:F1}s by {partyComp}");
                    }

                    // Update duty-specific stats
                    UpdateDutyStatsFromRun(mostRecentRun, partyComp, dateStr);

                    log.Information($"[Engine] Stats updated successfully - Method: VALID_RUN_CHECK");
                }
                else
                {
                    log.Warning($"[Engine] Skipping stats update - INVALID_COMPLETION_TIME: {mostRecentRun.CompletionTime:F1}s (Valid range: >0 && <{config.BailoutTimeout}s)");
                    log.Debug($"[Engine] Run details - Timestamp: {mostRecentRun.Timestamp}, Territory: {mostRecentRun.TerritoryId}, WasSuccessful: {mostRecentRun.WasSuccessful}, IsPraetorium: {mostRecentRun.IsPraetorium}");
                }
            }
            else
            {
                log.Warning("[Engine] Skipping stats update - NO_RECENT_RUN_FOUND");
            }
        }
        else if (config.TestingModeUnsynced && !config.ShowDebugRuns)
        {
            log.Information("[Engine] Unsynced run - skipping stats tracking (TestingModeUnsynced=true, ShowDebugRuns=false)");
        }
        else
        {
            log.Warning("[Engine] Skipping stats update - NO_RUN_HISTORY (count: 0)");
        }
        state.IsInDuty = false;
        outsideDutyTicks = 0;
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        ResetRepairRecoveryWatchdog();
        ResetQueueRecoveryState();
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        
        CurrentState = EngineState.WaitingOutsideDuty;
        StatusMessage = $"Outside Duty - Next: #{state.DutyCounter + 1}";
        log.Information($"[Engine] Left duty. Next: #{state.DutyCounter + 1}");

        // Save configuration after leaving duty (stats updates, counter changes, etc.)
        try
        {
            // This needs to be called via the plugin since we don't have direct access to ConfigManager here
            // The plugin will handle the actual save
            log.Debug("[Engine] Configuration save requested after leaving duty");
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Failed to save configuration after leaving duty: {ex.Message}");
        }

        // Check if we should continue running
        if (state.DutyCounter < config.MaxRuns)
        {
            if (state.IsPartyLeader)
            {
                log.Information($"[Engine] Starting leader requeue sequence - {state.DutyCounter}/{config.MaxRuns} completed");
                
                // Start requeue state machine with 10s delay to prevent crashes
                requeueState = RequeueState.WaitingAfterLeave;
                requeueStartTime = DateTime.UtcNow;
                requeueInProgress = true;
            }
            else
            {
                log.Information($"[Engine] Non-leader ready for next duty - {state.DutyCounter}/{config.MaxRuns} completed");
                // Non-leader continues running, will "/ad start" when entering next duty
            }
        }
        else
        {
            log.Information($"[Engine] Run limit reached - stopping");
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

        log.Information($"[Engine] Updated {(isPrae ? "Praetorium" : "Decumana")} stats: {state.LastCompletionDuration:F0}s");
    }

    private void UpdateOutsideDuty()
    {
        outsideDutyTicks++;

        // Food check
        foodService.Update();

        // Repair check
        if (repairService.NeedsRepair())
        {
            EnterRepairMode(useNpcRepair: !state.IsPartyLeader, "Repairing...");
            return;
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
            StatusMessage = $"Waiting for leader - #{state.DutyCounter + 1}";
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
                        log.Information("[Engine] Stopping AutoDuty from previous duty");
                        try
                        {
                            autoDutyIPC?.StopDuty();
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[Engine] AutoDuty stop failed: {ex.Message}");
                        }
                        requeueState = RequeueState.StoppingAutoDuty;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    StatusMessage = $"Waiting to stop AutoDuty ({2.0 - elapsed:F0}s)";
                    break;
                    
                case RequeueState.StoppingAutoDuty:
                    if (elapsed >= 1.0) // Give time for stop to process
                    {
                        requeueState = RequeueState.WaitingToQueue;
                        requeueStartTime = DateTime.UtcNow;
                    }
                    StatusMessage = "Stopping AutoDuty...";
                    break;
                    
                case RequeueState.WaitingToQueue:
                    if (elapsed >= 1.0)
                    {
                        StartQueueAttempt(isPrae, ignoreCooldown: false, "Auto-queueing");
                        requeueState = RequeueState.Complete;
                        requeueInProgress = false;
                        log.Information($"[Engine] Auto-queue command sent for next run: {dutyTracker?.GetCurrentDutyName() ?? "Unknown"}");
                        return;
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
                            log.Information("[Engine] Requeue completed successfully");
                        }
                        else
                        {
                            // Queue failed, retry
                            requeueAttempts++;
                            if (requeueAttempts >= MaxRequeueAttempts)
                            {
                                requeueState = RequeueState.Failed;
                                requeueInProgress = false;
                                log.Error("[Engine] Requeue failed after max attempts");
                            }
                            else
                            {
                                log.Warning($"[Engine] Requeue attempt {requeueAttempts} failed, retrying in {RequeueRetryInterval}s");
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
            log.Error($"[Engine] Requeue state machine error: {ex.Message}");
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
        if (HasQueueRegistrationCondition())
        {
            if (queueRegistrationStartedUtc != DateTime.MinValue)
            {
                var elapsed = (DateTime.UtcNow - queueRegistrationStartedUtc).TotalSeconds;
                log.Information($"[Engine] Queue registration detected for {dutyName} after {elapsed:F1}s");
                ResetQueueRegistrationWatchdog();
            }

            StatusMessage = $"Queueing: {dutyName} (registered)";
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

            StatusMessage = $"Queueing: {dutyName} (waiting for queue registration {Math.Ceiling(elapsed):F0}/{QueueRegistrationWatchdogSeconds:F0}s)";
            return;
        }

        // If we're now in duty, the state will change via OnEnteredDuty
        // Otherwise keep waiting
        if (!condition[34])
        {
            StatusMessage = $"Queueing: {dutyName} (waiting...)";
        }
    }

    private void UpdateInDuty()
    {
        RefreshInDutyRotationIfNeeded();

        // Start AutoDuty if not already started (handles the case where we're already in duty)
        if (!autoDutyStartedInDuty)
        {
            if (!IsReadyToStartAutoDutyInsideDuty())
                return;

            autoDutyStartedInDuty = true;
            log.Information("[Engine] Starting AutoDuty inside duty");
            autoDutyIPC.StartDuty();
        }

        // Duty completion exit logic
        if (dutyCompleted)
        {
            // Cutscene protection: don't leave during cutscenes
            if (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene])
            {
                log.Information("[Engine] Delaying leave - in cutscene");
                return;
            }

            var elapsed = (DateTime.UtcNow - dutyCompletedTime).TotalSeconds;
            if (elapsed >= DutyExitDelaySeconds)
            {
                // Keep trying to leave until territory changes
                LeaveDuty();
                return;
            }
            else
            {
                StatusMessage = $"Waiting to leave duty ({DutyExitDelaySeconds - elapsed:F0}s)";
            }
        }

        // Boss combat handler
        bossHandler.Update();

        // Stuck detection
        stuckDetection.Update();

        StatusMessage = $"In Duty #{state.DutyCounter} - {state.TimeInDuty:F0}s";
    }

    private bool IsReadyToStartAutoDutyInsideDuty()
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
                log.Information($"[Engine] Praetorium duty entered but timer is still at {remainingTime:F0}s; waiting before starting AutoDuty");
            }
            return false;
        }

        if (secondsSinceEnter < PraetoriumDutyReadyFallbackSeconds)
        {
            StatusMessage = $"In Duty - waiting for Praetorium timer ({PraetoriumDutyReadyFallbackSeconds - secondsSinceEnter:F0}s fallback)";
            if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
            {
                lastPraetoriumReadyWaitLogUtc = now;
                log.Information($"[Engine] Praetorium duty timer not visible yet; waiting {PraetoriumDutyReadyFallbackSeconds - secondsSinceEnter:F0}s more before fallback start");
            }
            return false;
        }

        if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
        {
            lastPraetoriumReadyWaitLogUtc = now;
            log.Warning("[Engine] Praetorium duty timer never appeared; allowing AutoDuty start after fallback wait");
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
        log.Debug("[Engine] Refreshed BossMod AI + RSR inside duty ({Mode}, target={Target}, hp={Hp})",
            aggressiveRefresh ? "aggressive" : "normal",
            targetName.Length > 0 ? targetName : "none",
            currentTarget?.CurrentHp ?? 0);
    }

    private void UpdateRepairing()
    {
        outsideDutyTicks++;
        StatusMessage = "Repairing...";
		log.Information("[MOGTOME][Repair] Requesting repair via AutoDuty in case self repair being cheeky.");
		commandManager.ProcessCommand("/ad repair");
        if (outsideDutyTicks <= 5)
            return;

        if (repairService.NeedsRepair())
        {
            RetryRepairRequestIfNeeded();
            return;
        }

        ResetRepairRequestState();
        repairService.ReturnToInnIfNeeded();
        dutyQueue.RestoreAutoQueueAfterRepair();
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

    private void EnterRepairMode(bool useNpcRepair, string statusMessage)
    {
        ResetRepairRequestState();
        ResetRepairRecoveryWatchdog();
        ResetQueueRegistrationWatchdog();
        CurrentState = EngineState.RepairingOutside;
        StatusMessage = statusMessage;
        outsideDutyTicks = 0;
        dutyQueue.DisableAutoQueueForRepair();
        activeRepairUsesNpc = useNpcRepair;
        IssueRepairRequest("entered repair mode");
    }

    private void ArmRepairRecoveryWatchdog()
    {
        repairRecoveryWatchStartedUtc = DateTime.UtcNow;
        repairRecoveryRetryReadyUtc = DateTime.MinValue;
        repairRecoveryAttempts = 0;
        log.Information($"[Engine] Repair flow complete; if still outside duty after {RepairRecoveryWatchdogSeconds:F0}s, MOGTOME will /ad stop and retry");
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
    }

    private void IssueRepairRequest(string reason)
    {
        repairRequestAttempts++;
        lastRepairRequestUtc = DateTime.UtcNow;

        if (repairRequestAttempts == 1)
        {
            log.Information($"[Engine] Sending AutoDuty repair request ({(activeRepairUsesNpc ? "npc" : "self")}) - {reason}");
        }
        else
        {
            log.Warning($"[Engine] Repair still needed; retrying AutoDuty repair request attempt {repairRequestAttempts} ({(activeRepairUsesNpc ? "npc" : "self")}) - {reason}");
        }

        if (activeRepairUsesNpc)
            repairService.TryNpcRepair();
        else
            repairService.TrySelfRepair();
    }

    private void RetryRepairRequestIfNeeded()
    {
        if (lastRepairRequestUtc == DateTime.MinValue)
        {
            IssueRepairRequest("repair state had no active request timestamp");
            return;
        }

        var elapsedSinceRequest = (DateTime.UtcNow - lastRepairRequestUtc).TotalSeconds;
        if (elapsedSinceRequest < RepairRequestRetrySeconds)
            return;

        // AutoDuty repair can occasionally miss self-repair activation. Retry on a bounded cadence, never per-frame.
        IssueRepairRequest($"repair still needed after {elapsedSinceRequest:F0}s");
    }

    private bool IsRepairFlowActive()
        => CurrentState == EngineState.RepairingOutside || state.AutoQueueDisabledForRepair;

    private bool IsQueueRecoveryActive()
        => queueRecoveryStopUntilUtc != DateTime.MinValue;

    private bool HasQueueRegistrationCondition()
        => condition[QueueConditionIndex]
           || condition[WaitingForDutyConditionIndex]
           || condition[WaitingForDutyFinderConditionIndex];

    private void ResetQueueRegistrationWatchdog()
    {
        queueRegistrationStartedUtc = DateTime.MinValue;
    }

    private void StartQueueAttempt(bool isPrae, bool ignoreCooldown, string statusPrefix)
    {
        var dutyName = dutyTracker.GetCurrentDutyName();
        CurrentState = EngineState.Queueing;
        StatusMessage = $"{statusPrefix}: {dutyName}";

        try
        {
            if (ignoreCooldown)
                dutyQueue.ForceQueue(isPrae);
            else
                dutyQueue.TryQueue(isPrae);

            queueRegistrationStartedUtc = DateTime.UtcNow;
            log.Information($"[Engine] {statusPrefix} command sent for {dutyName}; waiting up to {QueueRegistrationWatchdogSeconds:F0}s for queue registration");
        }
        catch (Exception ex)
        {
            ResetQueueRegistrationWatchdog();
            log.Error($"[Engine] {statusPrefix} failed for {dutyName}: {ex.Message}");
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
        StatusMessage = $"Queue recovery: waiting after /ad stop ({QueueRecoveryStopSeconds:F0}s)";
        log.Warning($"[Engine] {reason}; sending /ad stop, waiting {QueueRecoveryStopSeconds:F0}s, then holding {QueueRecoveryRepairGraceSeconds:F0}s for repairs");
        autoDutyIPC.StopDuty();
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

        if (HasQueueRegistrationCondition())
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
            log.Warning("[Engine] Queue condition ended while repair is active; deferring queue recovery until repair completes");
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
            StatusMessage = $"Queue recovery: waiting after /ad stop ({Math.Ceiling(stopRemaining):F0}s)";
            return true;
        }

        if (queueRecoveryResumeUtc == DateTime.MinValue)
        {
            queueRecoveryResumeUtc = now.AddSeconds(QueueRecoveryRepairGraceSeconds);
            log.Information($"[Engine] Queue recovery: allowing {QueueRecoveryRepairGraceSeconds:F0}s for repairs before resuming duty entry");
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
        log.Information("[Engine] Queue recovery wait complete; resuming duty entry attempts");
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
                log.Warning($"[Engine] Repair recovery retry {repairRecoveryAttempts}: force-queueing {dutyName} after /ad stop");
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
            log.Warning($"[Engine] Still outside duty {elapsed:F0}s after repair; sending /ad stop and retrying {dutyName} (attempt {repairRecoveryAttempts})");
            autoDutyIPC.StopDuty();
            repairRecoveryRetryReadyUtc = now.AddSeconds(RepairRecoveryStopDelaySeconds);
            StatusMessage = $"Repair recovery: restarting {dutyName}";
            return true;
        }

        log.Warning($"[Engine] Still outside duty {elapsed:F0}s after repair; sending /ad stop and waiting for leader retry (attempt {repairRecoveryAttempts})");
        autoDutyIPC.StopDuty();
        repairRecoveryWatchStartedUtc = now;
        StatusMessage = "Repair recovery: waiting for leader";
        return true;
    }

    private void HandleQuit()
    {
        log.Information($"[Engine] Quit condition reached: {state.DutyCounter} runs completed");
        Stop();

        if (!string.IsNullOrEmpty(config.QuitCommand))
        {
            try
            {
                commandManager.ProcessCommand(config.QuitCommand);
            }
            catch (Exception ex)
            {
                log.Error($"[Engine] Quit command failed: {ex.Message}");
            }
        }
    }

    private void LeaveDuty()
    {
        var now = DateTime.UtcNow;
        // Throttle leave attempts to every 5 seconds
        if ((now - lastLeaveAttemptTime).TotalSeconds < 5.0) return;

        DisableLeaderAutoQueueBeforeExitIfRepairNeeded("LeaveDuty");

        lastLeaveAttemptTime = now;
        leaveAttemptCount++;

        var elapsed = (DateTime.Now - dutyCompletedTime).TotalSeconds;
        var leaveReason = $"Exit after duty ends - {elapsed:F0}s elapsed (configured: {DutyExitDelaySeconds}s)";
        
        log.Information($"[Engine] Leave duty attempt #{leaveAttemptCount} - REASON: {leaveReason}");
        log.Information($"[Engine] Opening duty panel to leave");

        // Open duty panel to access Leave Duty button
        GameHelpers.SendCommand("/dutyfinder");

        // Wait a moment for panel to open, then try to click Leave Duty
        System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => {
            try
            {
                TryClickLeaveDutyButton();
            }
            catch (Exception ex)
            {
                log.Error($"[Engine] ContinueWith exception in TryClickLeaveDutyButton: {ex.Message}");
            }
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);

        // Also try clicking Yes on any confirmation dialog that appears
        System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => {
            try
            {
                if (GameHelpers.ClickYesIfVisible())
                {
                    log.Information("[Engine] Successfully clicked Yes on leave duty confirmation");
                }
            }
            catch (Exception ex)
            {
                log.Error($"[Engine] ContinueWith exception in ClickYesIfVisible: {ex.Message}");
            }
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);

        StatusMessage = $"Leaving duty (attempt #{leaveAttemptCount}) - {leaveReason}";
    }

    private unsafe void TryClickLeaveDutyButton()
    {
        try
        {
            // Use xa docs callback pattern: Open ContentsFinderMenu directly, then click Leave button (node 43)
            log.Information("[Engine] Opening ContentsFinderMenu with callback");
            
            // Try direct callback to open ContentsFinderMenu (pattern from Character true 12)
            try
            {
                // Based on xa docs pattern - try different callback numbers to open ContentsFinderMenu
                GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 0);
            }
            catch (Exception ex)
            {
                log.Error($"[Engine] ContentsFinderMenu callback failed: {ex.Message}");
            }
            
            // Wait a moment for the menu to open, then click Leave button
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => {
                try
                {
                    TryClickLeaveButton();
                }
                catch (Exception ex)
                {
                    log.Error($"[Engine] ContinueWith exception in TryClickLeaveButton: {ex.Message}");
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Error trying to leave duty: {ex.Message}");
        }
    }

    private unsafe void TryClickLeaveButton()
    {
        try
        {
            // Click Leave button using xa docs pattern: ClickAddonButton("ContentsFinderMenu", 43)
            log.Information("[Engine] Clicking Leave button on ContentsFinderMenu");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 43);
            
            // Handle the confirmation dialog
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => {
                try
                {
                    HandleLeaveConfirmation();
                }
                catch (Exception ex)
                {
                    log.Error($"[Engine] ContinueWith exception in HandleLeaveConfirmation: {ex.Message}");
                }
            }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Error clicking Leave button: {ex.Message}");
        }
    }

    private void HandleLeaveConfirmation()
    {
        try
        {
            // Click Yes on SelectYesno confirmation dialog
            log.Information("[Engine] Clicking Yes on leave confirmation dialog");
            GameHelpers.ClickYesIfVisible();
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Error handling leave confirmation: {ex.Message}");
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
            log.Error($"[Engine] GetPartyComposition failed: {ex.Message}");
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
            log.Error($"[Engine] GetCurrentPartyMemberCount failed: {ex.Message}");
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
        log.Warning($"[Engine] start gate: {message} Party={GetPartyComposition()}");
        Plugin.ChatGui.Print($"[MOGTOME] {message}");
        Stop();
        return true;
    }

    private void DisableLeaderAutoQueueBeforeExitIfRepairNeeded(string reason)
    {
        if (!state.IsPartyLeader)
            return;

        if (state.AutoQueueDisabledForRepair)
        {
            log.Information($"[Engine] AutoQueue is already disabled for repair before duty exit ({reason})");
            return;
        }

        if (!repairService.NeedsRepair(forceRefresh: true))
            return;

        dutyQueue.DisableAutoQueueForRepair();
        log.Warning($"[Engine] Leader repair detected before duty exit; AutoQueue disabled before leaving duty ({reason})");
    }

    public void ApplyConfiguredPartyLeaderState(bool applyAutoQueuePolicy = true, string reason = "configured role")
    {
        var source = config.IsCrossWorldParty
            ? "configured cross-world role"
            : "configured party role checkbox";
        SetPartyLeaderState(config.IsPartyLeader, applyAutoQueuePolicy, reason, $"Source={source}");
    }

    public void RefreshPartyLeaderState(bool applyAutoQueuePolicy = true)
    {
        if (condition[34] || state.IsInDuty)
        {
            var message = "Manual party refresh skipped inside duty. Use Refresh Party State only outside duty after the full party has zoned out and become visible.";
            log.Warning($"[Engine] {message} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print($"[MOGTOME] {message}");
            return;
        }

        if (config.IsCrossWorldParty)
        {
            log.Information($"[Engine] Manual party refresh requested in cross-world mode; keeping configured role. Party={GetPartyComposition()}");
            ApplyConfiguredPartyLeaderState(applyAutoQueuePolicy, "manual refresh");
            return;
        }

        if (!TryDetectSameWorldPartyLeader(out var isPartyLeader, out var details))
        {
            var message = $"Manual party refresh could not determine leader from the current same-world party list. Keeping {(state.IsPartyLeader ? "leader" : "non-leader")} role.";
            log.Warning($"[Engine] {message} {details} Party={GetPartyComposition()}");
            Plugin.ChatGui.Print($"[MOGTOME] {message}");
            return;
        }

        SetPartyLeaderState(isPartyLeader, applyAutoQueuePolicy, "manual refresh", details);
    }

    private void SetPartyLeaderState(bool isPartyLeader, bool applyAutoQueuePolicy, string reason, string details)
    {
        var previousLeaderState = state.IsPartyLeader;
        state.IsPartyLeader = isPartyLeader;

        if (previousLeaderState == isPartyLeader)
        {
            log.Information($"[Engine] Party leader state unchanged ({reason}): IsLeader={isPartyLeader}. {details}");
            return;
        }

        log.Information($"[Engine] Party leader state changed ({reason}): {previousLeaderState} -> {isPartyLeader}. {details}");

        if (applyAutoQueuePolicy && IsRunning)
            dutyQueue.ApplyAutoQueuePolicyForCurrentRole(reason);
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
