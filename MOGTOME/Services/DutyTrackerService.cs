using System;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class DutyTrackerService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;

    public DutyTrackerService(IPluginLog log, Configuration config, DutyState state)
    {
        this.log = log;
        this.config = config;
        this.state = state;

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
        config.Save();
        
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

        config.Save();

        log.Information($"[DutyTracker] {(isPrae ? "Praetorium" : "Decumana")} started -> Prae counter: {state.DutyCounter}, Daily Decu: {state.DecumanaCounter}");
    }

    public void OnDutyCompleted()
    {
        if (state.DutyStartTime.HasValue)
        {
            state.LastCompletionDuration = (float)(DateTime.UtcNow - state.DutyStartTime.Value).TotalSeconds;
            state.LastCompletionTime = DateTime.UtcNow;
            log.Information($"[DutyTracker] Duty completed in {state.LastCompletionDuration:F0}s -> counter: {state.DutyCounter}");
            
            // Record the run in history (will be done by MogtomeEngine)
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
            
            config.Save();
            
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
