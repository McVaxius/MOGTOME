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
        var lastReset = state.LastDailyReset ?? DateTime.MinValue;
        
        // Check if it's a new day (7 AM UTC reset time)
        var resetTimeToday = new DateTime(now.Year, now.Month, now.Day, 7, 0, 0, DateTimeKind.Utc);
        if (now < resetTimeToday)
        {
            resetTimeToday = resetTimeToday.AddDays(-1); // Yesterday's reset if before 7 AM today
        }
        
        if (lastReset < resetTimeToday)
        {
            // Reset daily counters
            var oldPrae = state.DutyCounter;
            var oldDecu = state.DecumanaCounter;
            
            // Update max daily Decu runs before reset
            if (state.DecumanaCounter > config.MaxDailyDecuRuns)
            {
                config.MaxDailyDecuRuns = state.DecumanaCounter;
            }
            
            state.DutyCounter = 0;
            state.DecumanaCounter = 0;
            state.LastDailyReset = now;
            
            config.DutyCounter = 0;
            
            // Reset daily Decumana stats
            config.DailyDecuRuns = 0;
            config.DailyDecuBestTime = float.MaxValue;
            config.DailyDecuLongestRun = 0;
            config.DailyDecuMogtomesEarned = 0;
            config.LastDailyDecuReset = now;
            
            config.Save();
            
            log.Information($"[DutyTracker] Daily reset! Prae: {oldPrae}→0, Daily Decu: {oldDecu}→0 (Max: {config.MaxDailyDecuRuns})");
            return true;
        }
        return false;
    }

    public string GetCurrentDutyName()
    {
        return ShouldRunPraetorium() ? "The Praetorium" : "The Porta Decumana";
    }
}
