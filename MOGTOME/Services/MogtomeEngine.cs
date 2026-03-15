using System;
using Dalamud.Game.ClientState.Conditions;
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
        AutoDutyPathService autoDutyPath, AutoDutyIPC autoDutyIPC,
        AutomatonIPC automatonIPC, YesAlreadyIPC yesAlreadyIPC,
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
        this.autoDutyIPC = autoDutyIPC;
        this.automatonIPC = automatonIPC;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.condition = condition;
        this.clientState = clientState;
        this.commandManager = commandManager;

        // Hook duty completed event
        Plugin.DutyStateService.DutyCompleted += OnDutyCompleted;
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
        log.Information($"[Engine] Duty completed event in territory {territoryId} - will leave in {DutyExitDelaySeconds}s");
    }

    public void Start()
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
            // Ensure AutoDuty path exists
            autoDutyPath.EnsurePathExists();

            // Initialize rotation
            rotationService.Initialize();

            // Configure AutoDuty
            autoDutyIPC.ConfigureForMogtome(config.IsPartyLeader);

            // Pause YesAlready - we handle dialogs directly
            dialogHandler.Start();

            // Disable AutoQueue initially
            automatonIPC.DisableAutoQueue();

            // Detect party leader
            DetectPartyLeader();

            // Check potion availability
            state.PotionsAvailable = config.PotionItemId > 0;

            // Calculate timeouts
            state.CalculateTimeouts(LoopInterval);

            // Configure unsynced mode if testing
            if (config.TestingModeUnsynced)
            {
                autoDutyIPC.SetConfig("Unsynced", "true");
                log.Information("[Engine] Testing mode: Unsynced enabled");
            }
            else
            {
                autoDutyIPC.SetConfig("Unsynced", "false");
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
            dialogHandler.Stop();
            rotationService.DisableRotation();
            dutyQueue.EnableAutoQueueAfterRepair();
        }
        catch (Exception ex)
        {
            log.Error($"[Engine] Error during stop: {ex.Message}");
        }

        CurrentState = EngineState.Idle;
        StatusMessage = "Idle";
        log.Information("[Engine] Stopped");
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

            // Handle dialogs always
            dialogHandler.Update();

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
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        
        CurrentState = EngineState.InDuty;
        StatusMessage = $"In Duty - #{state.DutyCounter} ({dutyTracker.GetCurrentDutyName()})";
        log.Information($"[Engine] Entered duty #{state.DutyCounter}");

        // Always count instances fired up (even unsynced)
        var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
        if (isPrae)
            config.TotalPraes++;
        else
            config.TotalDecus++;
        config.Save();
    }

    private void OnLeftDuty()
    {
        // Update best/longest time stats only for synced runs
        if (!config.TestingModeUnsynced && state.LastCompletionDuration > 0 && state.LastCompletionDuration < config.BailoutTimeout)
        {
            var partyComp = GetPartyComposition();
            var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC");

            // Update global stats (kept for compatibility)
            if (state.LastCompletionDuration < config.BestTimeEver)
            {
                config.BestTimeEver = state.LastCompletionDuration;
                config.BestTimeDate = dateStr;
                config.BestTimeParty = partyComp;
            }

            if (state.LastCompletionDuration > config.LongestRunEver)
            {
                config.LongestRunEver = state.LastCompletionDuration;
                config.LongestRunDate = dateStr;
                config.LongestRunParty = partyComp;
            }

            // Update duty-specific stats
            UpdateDutyStats(partyComp, dateStr);

            config.Save();
        }
        else if (config.TestingModeUnsynced)
        {
            log.Information("[Engine] Unsynced run - skipping stats tracking");
        }

        dutyTracker.OnDutyCompleted();
        state.IsInDuty = false;
        outsideDutyTicks = 0;
        autoDutyStartedInDuty = false;
        dutyCompleted = false;
        
        // Reset requeue state when successfully entering duty
        requeueInProgress = false;
        requeueState = RequeueState.Idle;
        
        CurrentState = EngineState.WaitingOutsideDuty;
        StatusMessage = $"Outside Duty - Next: #{state.DutyCounter + 1}";
        log.Information($"[Engine] Left duty. Next: #{state.DutyCounter + 1}");

        // Check if we should continue running
        if (state.DutyCounter < config.MaxRuns && state.IsPartyLeader)
        {
            log.Information($"[Engine] Starting requeue sequence - {state.DutyCounter}/{config.MaxRuns} completed");
            
            // Start requeue state machine with 10s delay to prevent crashes
            requeueInProgress = true;
            requeueState = RequeueState.WaitingAfterLeave;
            requeueStartTime = DateTime.UtcNow;
            requeueAttempts = 0;
        }
        else if (state.DutyCounter < config.MaxRuns && !state.IsPartyLeader)
        {
            log.Information("[Engine] Waiting for party leader to queue next run");
        }
        else
        {
            log.Information($"[Engine] All runs completed - stopping");
            HandleQuit();
        }
    }

    private void UpdateDutyStats(string partyComp, string dateStr)
    {
        var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
        
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
            CurrentState = EngineState.RepairingOutside;
            StatusMessage = "Repairing...";
            dutyQueue.DisableAutoQueueForRepair();
            repairService.TrySelfRepair();
            return;
        }

        // Auto-equip
        repairService.AutoEquipIfEnabled();

        // Handle requeue state machine
        if (requeueInProgress)
        {
            HandleRequeueStateMachine();
            return; // Skip normal queue logic while requeue in progress
        }

        // Normal queue logic (only if not requeueing)
        if (state.IsPartyLeader && !delayedRequeueInProgress)
        {
            var isPrae = dutyTracker.ShouldRunPraetorium();
            CurrentState = EngineState.Queueing;
            StatusMessage = $"Queueing: {dutyTracker.GetCurrentDutyName()}";
            dutyQueue.TryQueue(isPrae);
        }
        else if (!state.IsPartyLeader)
        {
            StatusMessage = $"Waiting for leader - #{state.DutyCounter + 1}";
        }
        else if (delayedRequeueInProgress)
        {
            StatusMessage = $"Delayed requeue in progress...";
        }

        // Non-leader repair check after 20 seconds outside duty
        if (!state.IsPartyLeader && outsideDutyTicks > (int)(20 / LoopInterval))
        {
            if (repairService.NeedsRepair())
            {
                repairService.TryNpcRepair();
            }
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
                        CurrentState = EngineState.Queueing;
                        StatusMessage = $"Auto-queueing: {dutyTracker?.GetCurrentDutyName() ?? "Unknown"}";
                        log.Information($"[Engine] Auto-queueing for next run: {dutyTracker?.GetCurrentDutyName() ?? "Unknown"}");
                        try
                        {
                            dutyQueue?.TryQueue(isPrae);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[Engine] Queue failed: {ex.Message}");
                        }
                        requeueState = RequeueState.Queueing;
                        requeueStartTime = DateTime.UtcNow;
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
        // If we're now in duty, the state will change via OnEnteredDuty
        // Otherwise keep waiting
        if (!condition[34])
        {
            StatusMessage = $"Queueing: {dutyTracker.GetCurrentDutyName()} (waiting...)";
        }
    }

    private void UpdateInDuty()
    {
        // Start AutoDuty if not already started (handles the case where we're already in duty)
        if (!autoDutyStartedInDuty)
        {
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

        // Force rotation refresh periodically
        rotationService.ForceRotation();

        StatusMessage = $"In Duty #{state.DutyCounter} - {state.TimeInDuty:F0}s";
    }

    private void UpdateRepairing()
    {
        // Wait for repair to complete, then re-enable queue
        outsideDutyTicks++;
        if (outsideDutyTicks > 5)
        {
            dutyQueue.EnableAutoQueueAfterRepair();
            CurrentState = EngineState.WaitingOutsideDuty;
            outsideDutyTicks = 0;
            StatusMessage = "Repair done, resuming";
        }
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
            TryClickLeaveDutyButton();
        });

        // Also try clicking Yes on any confirmation dialog that appears
        System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ => {
            if (GameHelpers.ClickYesIfVisible())
            {
                log.Information("[Engine] Successfully clicked Yes on leave duty confirmation");
            }
        });

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
                TryClickLeaveButton();
            });
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
                HandleLeaveConfirmation();
            });
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
        catch
        {
            return "Unknown";
        }
    }

    private void DetectPartyLeader()
    {
        try
        {
            if (config.IsCrossWorldParty)
            {
                state.IsPartyLeader = config.IsPartyLeader;
                log.Information($"[Engine] Cross-world party: IsLeader={state.IsPartyLeader} (from config)");
                return;
            }

            // Try to detect from party
            var party = Plugin.PartyList;
            if (party.Length <= 1)
            {
                state.IsPartyLeader = true;
                log.Information("[Engine] Solo or no party, treating as leader");
                return;
            }

            var leaderIndex = (int)party.PartyLeaderIndex;
            if (leaderIndex >= 0 && leaderIndex < party.Length)
            {
                var leader = party[leaderIndex];
                if (leader != null)
                {
                    var localContentId = (long)Plugin.PlayerState.ContentId;
                    var leaderContentId = (long)leader.ContentId;
                    state.IsPartyLeader = leaderContentId == localContentId;
                    log.Information($"[Engine] Party leader detection: IsLeader={state.IsPartyLeader}");
                    return;
                }
            }

            // Fallback to config
            state.IsPartyLeader = config.IsPartyLeader;
            log.Information($"[Engine] Fallback leader detection: IsLeader={state.IsPartyLeader}");
        }
        catch (Exception ex)
        {
            state.IsPartyLeader = config.IsPartyLeader;
            log.Warning($"[Engine] Leader detection failed, using config: {ex.Message}");
        }
    }
}
