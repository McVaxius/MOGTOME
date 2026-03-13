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
        state.CalculateTimeouts(config.LoopInterval);
    }

    public void OnDutyStarted()
    {
        state.DutyCounter++;
        state.HasEnteredDuty = true;
        state.DutyStartTime = DateTime.UtcNow;
        state.MaxContentTime = 0;
        state.TimeInDuty = 0;
        state.StuckTickCount = 0;

        config.DutyCounter = state.DutyCounter;
        config.Save();

        if (config.EchoLevel < 4)
            log.Information($"[DutyTracker] Duty started -> counter: {state.DutyCounter}");
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
        // Daily reset is at 7 AM UTC (3 AM EST / 12 AM PST)
        if (now.Hour == 7 && state.DutyCounter > 20)
        {
            state.DutyCounter = 0;
            state.DecumanaCounter = 0;
            config.DutyCounter = 0;
            config.Save();
            log.Information("[DutyTracker] Daily reset detected! Counter reset to 0");
            return true;
        }
        return false;
    }

    public string GetCurrentDutyName()
    {
        return ShouldRunPraetorium() ? "The Praetorium" : "The Porta Decumana";
    }
}
