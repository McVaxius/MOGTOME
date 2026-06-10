using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class DutyTrackerService
{	//DAILY RESET TIME MF!!!!!
    private const int DailyResetHourUtc = 7;

    private readonly IPluginLog log;
    private Configuration config;
    private readonly DutyState state;
    private readonly ConfigManager configManager;
    private readonly RunHistoryService runHistoryService;

    public DutyTrackerService(IPluginLog log, Configuration config, DutyState state, ConfigManager configManager, RunHistoryService runHistoryService)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.configManager = configManager;
        this.runHistoryService = runHistoryService;

        UpdateConfiguration(config);
    }

    public void UpdateConfiguration(Configuration newConfig)
    {
        config = newConfig;
        state.DutyCounter = config.DutyCounter;
        state.CalculateTimeouts(2.0f);

        if (state.NextResetTime == null)
            CalculateNextResetTime();

        log.Information($"[MOGTOME][DutyTracker] Active account configuration applied: Prae counter={state.DutyCounter}, threshold={config.PraetoriumThreshold}, maxRuns={config.MaxRuns}");
    }

    /// <summary>
    /// Sync state counter with config counter.
    /// Call this when config.DutyCounter is updated externally.
    /// </summary>
    public void SyncCounters()
    {
        state.DutyCounter = config.DutyCounter;
        log.Information($"[MOGTOME][DutyTracker] Synced counters: {state.DutyCounter}");
    }

    /// <summary>
    /// Calculate the next reset time (next 07:00 UTC that hasn't happened yet)
    /// </summary>
    private void CalculateNextResetTime()
    {
        var now = DateTime.UtcNow;

        var resetTime = new DateTime(now.Year, now.Month, now.Day, DailyResetHourUtc, 0, 0, DateTimeKind.Utc);
        if (now >= resetTime)
        {
            resetTime = resetTime.AddDays(1);
        }

        state.NextResetTime = resetTime;
        // Note: ConfigManager.SaveCurrentAccount() will be called by the caller
        // We don't save here to avoid multiple saves during initialization

        log.Information($"[MOGTOME][DutyTracker] Next reset time set to: {resetTime:yyyy-MM-dd HH:mm UTC}");
    }

    public void OnDutyStarted()
    {
        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;

        state.HasEnteredDuty = true;
        state.DutyStartTime = DateTime.UtcNow;
        state.MaxContentTime = 0;
        state.TimeInDuty = 0;
        state.StuckTickCount = 0;

        log.Information($"[MOGTOME][DutyTracker] {(isPrae ? "Praetorium" : "Decumana")} started; clear counters update only after confirmed successful completion");
    }

    public void ObserveRemainingTime()
    {
        var remaining = GameHelpers.GetDutyRemainingTime();
        if (!float.IsFinite(remaining) || remaining <= 0)
            return;

        if (remaining > state.MaxContentTime)
            state.MaxContentTime = remaining;
    }

    public void CaptureCompletionRemainingTime()
    {
        var remaining = GameHelpers.GetDutyRemainingTime();
        state.RemainingTimeAtCompletion = float.IsFinite(remaining) && remaining > 0
            ? remaining
            : 0;
    }

    /// <summary>
    /// Called when duty is completed
    /// Enhanced with detailed timing method logging
    /// </summary>
    public void OnDutyCompleted()
    {
        var now = DateTime.UtcNow;
        log.Information($"[MOGTOME][DutyTracker] OnDutyCompleted called - DutyStartTime: {state.DutyStartTime}, CurrentTime: {now}");

        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;
        if (isPrae)
        {
            state.DutyCounter++;
            config.DutyCounter = state.DutyCounter;
        }
        else
        {
            state.DecumanaCounter++;
        }
        
        if (state.DutyStartTime.HasValue)
        {
            var rawDuration = (float)(now - state.DutyStartTime.Value).TotalSeconds;
            var maxObservedRemaining = state.MaxContentTime;
            var completionRemaining = state.RemainingTimeAtCompletion;
            var observedDuration = maxObservedRemaining - completionRemaining;
            var observationsValid =
                float.IsFinite(maxObservedRemaining) &&
                float.IsFinite(completionRemaining) &&
                maxObservedRemaining > 0 &&
                completionRemaining > 0 &&
                observedDuration > 0 &&
                observedDuration <= 7200;

            var actualDuration = observationsValid ? observedDuration : rawDuration;
            var timingMethod = observationsValid ? "OBSERVED_REMAINING_DELTA" : "UTC_ELAPSED_FALLBACK";
            if (!float.IsFinite(actualDuration) || actualDuration <= 0 || actualDuration > 7200)
            {
                actualDuration = Math.Max(float.IsFinite(rawDuration) ? rawDuration : 0, 1);
                timingMethod = "UTC_ELAPSED_FINAL_FALLBACK";
            }

            state.LastCompletionDuration = actualDuration;
            state.LastCompletionTime = now;

            log.Information($"[MOGTOME][DutyTracker] Timing method={timingMethod}; maxObservedRemaining={maxObservedRemaining:F1}s; completionRemaining={completionRemaining:F1}s; utcElapsed={rawDuration:F1}s; storedDuration={actualDuration:F1}s");
            
            // Now that we have the correct completion time, record the run
            try
            {
                runHistoryService.RecordRun();
                SaveCurrentAccount("duty completion");
                log.Debug($"[MOGTOME][DutyTracker] Successfully called RecordRun() with completion time {state.LastCompletionDuration:F0}s");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[MOGTOME][DutyTracker] Failed to call RecordRun() after time calculation");
            }
        }
        else
        {
            log.Warning("[MOGTOME][DutyTracker] DutyStartTime is null, cannot calculate completion time");
            state.LastCompletionDuration = 0;
            state.LastCompletionTime = now;
        }

        state.Reset();
    }

    public bool ShouldRunPraetorium()
    {
        return state.DutyCounter < config.PraetoriumThreshold;
    }

    public bool ShouldRunDecumana()
    {
        return state.DutyCounter >= config.PraetoriumThreshold;
    }

    public bool ShouldQuit()
    {
        return state.DutyCounter >= config.MaxRuns;
    }

    public bool CheckDailyReset()
    {
        var now = DateTime.UtcNow;
        
        // If we have no next reset time, calculate it
        if (state.NextResetTime == null)
        {
            CalculateNextResetTime();
            return false;
        }
        
        // Check if we've passed the saved reset time
        if (now >= state.NextResetTime)
        {
            // Reset daily counters
            var oldPrae = state.DutyCounter;
            var oldDecu = state.DecumanaCounter;
            
            // Update max daily Decu runs before reset
            if (state.DecumanaCounter > config.MaxDailyDecuRuns)
            {
                config.MaxDailyDecuRuns = state.DecumanaCounter;
            }
            
            // Update all-time maximum if today's count beats the record
            if (state.DecumanaCounter > config.AllTimeMaxDailyDecu)
            {
                config.AllTimeMaxDailyDecu = state.DecumanaCounter;
                log.Information($"[MOGTOME][DutyTracker] NEW ALL-TIME RECORD: {config.AllTimeMaxDailyDecu} Decumana runs in one day!");
            }
            
            state.DutyCounter = 0;
            state.DecumanaCounter = 0;
            config.DutyCounter = 0;
            
            // Reset daily Decumana stats
            config.DailyDecuRuns = 0;
            config.DailyDecuBestTime = float.MaxValue;
            config.DailyDecuLongestRun = 0;
            config.DailyDecuMogtomesEarned = 0;
            config.LastDailyDecuReset = now;
            
            // Calculate next reset time
            CalculateNextResetTime();
            SaveCurrentAccount("daily reset");
            
            log.Information($"[MOGTOME][DutyTracker] Daily reset! Prae: {oldPrae}→0, Decu: {oldDecu}→0 (Max: {config.MaxDailyDecuRuns}, All-time: {config.AllTimeMaxDailyDecu})");
            return true;
        }
        
        return false;
    }

    public string GetCurrentDutyName()
    {
        return ShouldRunPraetorium() ? "The Praetorium" : "The Porta Decumana";
    }

    /// <summary>
    /// Get reset time display with countdown and local time
    /// </summary>
    public (string countdown, string localTime) GetResetTimeDisplay()
    {
        if (state.NextResetTime == null)
        {
            return ("Calculating...", "Unknown");
        }

        var now = DateTime.UtcNow;
        var timeUntilReset = state.NextResetTime.Value - now;
        
        // Format countdown
        string countdown;
        if (timeUntilReset.TotalHours > 24)
        {
            countdown = $"{(int)timeUntilReset.TotalDays}d {(int)timeUntilReset.Hours % 24}h";
        }
        else if (timeUntilReset.TotalHours > 1)
        {
            countdown = $"{(int)timeUntilReset.TotalHours}h {timeUntilReset.Minutes % 60}m";
        }
        else if (timeUntilReset.TotalMinutes > 1)
        {
            countdown = $"{(int)timeUntilReset.TotalMinutes}m {timeUntilReset.Seconds % 60}s";
        }
        else
        {
            countdown = $"{(int)timeUntilReset.TotalSeconds}s";
        }

        // Convert to local time for display
        var localResetTime = state.NextResetTime.Value.ToLocalTime();
        var localTime = localResetTime.ToString("yyyy-MM-dd HH:mm");

        return (countdown, localTime);
    }

    private void SaveCurrentAccount(string reason)
    {
        try
        {
            configManager.SaveCurrentAccount();
            log.Debug($"[MOGTOME][DutyTracker] Saved active account after {reason}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[MOGTOME][DutyTracker] Failed to save active account after {reason}");
        }
    }
}
