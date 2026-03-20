using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class DutyTrackerService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly RunHistoryService runHistoryService;

    public DutyTrackerService(IPluginLog log, Configuration config, DutyState state, RunHistoryService runHistoryService)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.runHistoryService = runHistoryService;

        state.DutyCounter = config.DutyCounter;
        state.CalculateTimeouts(2.0f);
        
        // Initialize next reset time if not set
        if (state.NextResetTime == null)
        {
            CalculateNextResetTime();
        }
    }

    /// <summary>
    /// Sync state counter with config counter.
    /// Call this when config.DutyCounter is updated externally.
    /// </summary>
    public void SyncCounters()
    {
        state.DutyCounter = config.DutyCounter;
        log.Information($"[DutyTracker] Synced counters: {state.DutyCounter}");
    }

    /// <summary>
    /// Calculate the next reset time (next 7 AM UTC that hasn't happened yet)
    /// </summary>
    private void CalculateNextResetTime()
    {
        var now = DateTime.UtcNow;
        
        // Find next 7 AM UTC (could be today or tomorrow)
        var resetTime = new DateTime(now.Year, now.Month, now.Day, 7, 0, 0, DateTimeKind.Utc);
        if (now >= resetTime)
        {
            resetTime = resetTime.AddDays(1); // Tomorrow's 7 AM UTC
        }
        
        state.NextResetTime = resetTime;
        // Note: ConfigManager.SaveCurrentAccount() will be called by the caller
        // We don't save here to avoid multiple saves during initialization
        
        log.Information($"[DutyTracker] Next reset time set to: {resetTime:yyyy-MM-dd HH:mm UTC}");
    }

    public void OnDutyStarted()
    {
        var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
        
        // Only increment duty counter for Praetorium runs
        if (isPrae)
        {
            state.DutyCounter++;
            config.DutyCounter = state.DutyCounter;
        }
        
        // Track daily Decumana runs
        if (!isPrae)
        {
            state.DecumanaCounter++;
        }
        
        state.HasEnteredDuty = true;
        state.DutyStartTime = DateTime.UtcNow;
        state.MaxContentTime = 0;
        state.TimeInDuty = 0;
        state.StuckTickCount = 0;

        // Note: ConfigManager.SaveCurrentAccount() will be called by the engine
        // We don't save here to avoid multiple saves during duty start

        log.Information($"[DutyTracker] {(isPrae ? "Praetorium" : "Decumana")} started -> Prae counter: {state.DutyCounter}, Daily Decu: {state.DecumanaCounter}");
    }

    /// <summary>
    /// Called when duty is completed
    /// Enhanced with detailed timing method logging
    /// </summary>
    public void OnDutyCompleted()
    {
        var now = DateTime.UtcNow;
        log.Information($"[DutyTracker] OnDutyCompleted called - DutyStartTime: {state.DutyStartTime}, CurrentTime: {now}");
        
        if (state.DutyStartTime.HasValue)
        {
            var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
            var timeLimit = isPrae ? DutyState.PraetoriumTimeLimit : DutyState.DecumanaTimeLimit;
            var rawDuration = (float)(now - state.DutyStartTime.Value).TotalSeconds;

            log.Debug($"[DutyTracker] Duty parameters - Territory: {state.CurrentTerritory}, IsPrae: {isPrae}, TimeLimit: {timeLimit:F0}s, RawDuration: {rawDuration:F0}s");

            var remainingTime = GameHelpers.GetDutyRemainingTime();
            log.Information($"[DutyTracker] Remaining time check - API returned: {remainingTime:F0}s");
            
            float actualDuration;
            string timingMethod;

            if (remainingTime > 0)
            {
                actualDuration = timeLimit - remainingTime;
                state.RemainingTimeAtCompletion = remainingTime;
                timingMethod = "API_METHOD";
                
                log.Information($"[DutyTracker] [{timingMethod}] Duty completed: {timeLimit:F0}s - {remainingTime:F0}s = {actualDuration:F0}s");
                log.Debug($"[DutyTracker] [{timingMethod}] TimeLimit: {timeLimit:F0}s, Remaining: {remainingTime:F0}s, Calculated: {actualDuration:F0}s");
                
                // Validate the calculated time
                if (actualDuration < 0 || actualDuration > timeLimit)
                {
                    log.Warning($"[DutyTracker] [{timingMethod}] Invalid calculated duration {actualDuration:F0}s, falling back to raw duration");
                    actualDuration = rawDuration;
                    timingMethod = "API_FALLBACK";
                }
            }
            else
            {
                actualDuration = rawDuration;
                state.RemainingTimeAtCompletion = 0;
                timingMethod = "FALLBACK_METHOD";
                
                log.Information($"[DutyTracker] [{timingMethod}] Duty completed: {actualDuration:F0}s (remaining time unavailable)");
                log.Debug($"[DutyTracker] [{timingMethod}] RawDuration: {rawDuration:F0}s, Reason: API returned {remainingTime:F0}s");
            }

            // Final validation
            if (actualDuration <= 0 || actualDuration > 7200) // 2 hours max sanity check
            {
                log.Error($"[DutyTracker] [{timingMethod}] Invalid final duration {actualDuration:F0}s, using fallback");
                actualDuration = Math.Max(rawDuration, 1); // Ensure at least 1 second
                timingMethod = "FINAL_FALLBACK";
            }

            state.LastCompletionDuration = actualDuration;
            state.LastCompletionTime = now;

            log.Information($"[DutyTracker] [{timingMethod}] FINAL: Duty completed in {state.LastCompletionDuration:F0}s -> counter: {state.DutyCounter}");
            log.Debug($"[DutyTracker] [{timingMethod}] Summary - TimeLimit: {timeLimit:F0}s, Territory: {state.CurrentTerritory}, IsPrae: {isPrae}, Remaining: {remainingTime:F0}s, Actual: {actualDuration:F0}s");
        }
        else
        {
            log.Warning("[DutyTracker] DutyStartTime is null, cannot calculate completion time");
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
                log.Information($"[DutyTracker] NEW ALL-TIME RECORD: {config.AllTimeMaxDailyDecu} Decumana runs in one day!");
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
            
            // Note: ConfigManager.SaveCurrentAccount() will be called by the caller
            // We don't save here to avoid multiple saves during reset
            
            log.Information($"[DutyTracker] Daily reset! Prae: {oldPrae}→0, Decu: {oldDecu}→0 (Max: {config.MaxDailyDecuRuns}, All-time: {config.AllTimeMaxDailyDecu})");
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
}
