using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class StuckDetectionService
{
    private readonly IPluginLog log;
    private readonly ConfigManager configManager;
    private readonly DutyState state;
    private readonly VNavIPC vNavIPC;
    private readonly ICondition condition;

    private const float StuckDistanceThreshold = 1.0f;
    public StuckDetectionService(
        IPluginLog log, ConfigManager configManager, DutyState state,
        VNavIPC vNavIPC, ICondition condition)
    {
        this.log = log;
        this.configManager = configManager;
        this.state = state;
        this.vNavIPC = vNavIPC;
        this.condition = condition;
    }

    public void Update()
    {
        if (!state.IsInDuty)
            return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var pos = player.Position;
        var dx = pos.X - state.LastPositionX;
        var dy = pos.Y - state.LastPositionY;
        var dz = pos.Z - state.LastPositionZ;
        var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < StuckDistanceThreshold)
        {
            state.StuckTickCount++;
        }
        else
        {
            state.StuckTickCount = 0;
        }

        state.LastPositionX = pos.X;
        state.LastPositionY = pos.Y;
        state.LastPositionZ = pos.Z;

        // Only check stuck in Praetorium
        if (state.CurrentTerritory == DutyState.PraetoriumTerritoryId)
        {
            if (state.StuckTickCount >= state.MaxJiggle)
            {
                HandleStuck();
            }
        }

        // Bailout check
        CheckBailout();
    }

    private void HandleStuck()
    {
        // Condition[26] = InCombat
        if (condition[26]) return;

        log.Information($"[MOGTOME][StuckDetection] Stuck detected ({state.StuckTickCount} ticks), attempting recovery");
        state.StuckTickCount = 0;

        try
        {
            vNavIPC.Rebuild();
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][StuckDetection] Recovery failed: {ex.Message}");
        }
    }

    private void CheckBailout()
    {
        if (!state.DutyStartTime.HasValue) return;

        var elapsed = (float)(DateTime.UtcNow - state.DutyStartTime.Value).TotalSeconds;
        state.TimeInDuty = elapsed;
        var config = configManager.GetActiveConfig();

        // Condition[26] = InCombat
        if (elapsed > config.BailoutTimeout && !condition[26] && !state.BailoutRequested)
        {
            state.BailoutElapsedTime = elapsed;
            state.BailoutReason = $"Bailout triggered after {elapsed:F0}s (configured: {config.BailoutTimeout}s)";
            state.BailoutRequested = true;
            log.Warning($"[MOGTOME][StuckDetection] Queued bailout request: {state.BailoutReason}");
        }
    }
}
