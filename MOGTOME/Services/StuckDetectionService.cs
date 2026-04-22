using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class StuckDetectionService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly VNavIPC vNavIPC;
    private readonly ICondition condition;

    private const float StuckDistanceThreshold = 1.0f;
    private DateTime lastLeaveAttemptTime = DateTime.MinValue;
    private int leaveAttemptCount = 0;

    public StuckDetectionService(
        IPluginLog log, Configuration config, DutyState state,
        VNavIPC vNavIPC, ICondition condition)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.vNavIPC = vNavIPC;
        this.condition = condition;
    }

    public void Update()
    {
        if (!state.IsInDuty)
        {
            leaveAttemptCount = 0;
            lastLeaveAttemptTime = DateTime.MinValue;
            return;
        }

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

        // Condition[26] = InCombat
        if (elapsed > config.BailoutTimeout && !condition[26])
        {
            AttemptLeaveDuty(elapsed);
        }
    }

    private void AttemptLeaveDuty(float elapsed)
    {
        var now = DateTime.UtcNow;
        if ((now - lastLeaveAttemptTime).TotalSeconds < 5.0)
            return;

        lastLeaveAttemptTime = now;
        leaveAttemptCount++;
        var leaveReason = $"Bailout triggered after {elapsed:F0}s (configured: {config.BailoutTimeout}s)";

        try
        {
            vNavIPC.Rebuild();
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][StuckDetection] vnav rebuild before bailout leave failed: {ex.Message}");
        }

        log.Information($"[MOGTOME][StuckDetection] Leave duty attempt #{leaveAttemptCount} - REASON: {leaveReason}");
        log.Information("[MOGTOME][StuckDetection] Opening duty panel for bailout leave");
        GameHelpers.SendCommand("/dutyfinder");

        GameHelpers.QueueFrameworkAction("StuckDetection leave", "open leave duty button", TimeSpan.FromMilliseconds(500), () =>
        {
            TryClickLeaveDutyButton();
        });

        GameHelpers.QueueFrameworkAction("StuckDetection leave", "confirm leave duty", TimeSpan.FromMilliseconds(1000), () =>
        {
            if (GameHelpers.ClickYesIfVisible())
            {
                log.Information("[MOGTOME][StuckDetection] Successfully clicked Yes on bailout leave confirmation");
            }
        });
    }

    private unsafe void TryClickLeaveDutyButton()
    {
        try
        {
            log.Information("[MOGTOME][StuckDetection] Opening ContentsFinderMenu with callback");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 0);

            GameHelpers.QueueFrameworkAction("StuckDetection leave", "click leave button", TimeSpan.FromMilliseconds(500), TryClickLeaveButton);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][StuckDetection] Error trying to leave duty during bailout: {ex.Message}");
        }
    }

    private unsafe void TryClickLeaveButton()
    {
        try
        {
            log.Information("[MOGTOME][StuckDetection] Clicking Leave button on ContentsFinderMenu");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 43);

            GameHelpers.QueueFrameworkAction("StuckDetection leave", "handle leave confirmation", TimeSpan.FromMilliseconds(500), HandleLeaveConfirmation);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][StuckDetection] Error clicking Leave button during bailout: {ex.Message}");
        }
    }

    private void HandleLeaveConfirmation()
    {
        try
        {
            if (GameHelpers.ClickYesIfVisible())
            {
                log.Information("[MOGTOME][StuckDetection] Clicked Yes on bailout leave confirmation dialog");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][StuckDetection] Error handling bailout leave confirmation: {ex.Message}");
        }
    }
}
