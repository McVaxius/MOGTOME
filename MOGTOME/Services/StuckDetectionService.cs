using System;
using System.Threading.Tasks;
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

        log.Information($"[StuckDetection] Stuck detected ({state.StuckTickCount} ticks), attempting recovery");
        state.StuckTickCount = 0;

        try
        {
            vNavIPC.Rebuild();
        }
        catch (Exception ex)
        {
            log.Error($"[StuckDetection] Recovery failed: {ex.Message}");
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
            log.Warning($"[StuckDetection] vnav rebuild before bailout leave failed: {ex.Message}");
        }

        log.Information($"[StuckDetection] Leave duty attempt #{leaveAttemptCount} - REASON: {leaveReason}");
        log.Information("[StuckDetection] Opening duty panel for bailout leave");
        GameHelpers.SendCommand("/dutyfinder");

        Task.Delay(500).ContinueWith(_ =>
        {
            try
            {
                TryClickLeaveDutyButton();
            }
            catch (Exception ex)
            {
                log.Error($"[StuckDetection] ContinueWith exception in TryClickLeaveDutyButton: {ex.Message}");
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        Task.Delay(1000).ContinueWith(_ =>
        {
            try
            {
                if (GameHelpers.ClickYesIfVisible())
                {
                    log.Information("[StuckDetection] Successfully clicked Yes on bailout leave confirmation");
                }
            }
            catch (Exception ex)
            {
                log.Error($"[StuckDetection] ContinueWith exception in ClickYesIfVisible: {ex.Message}");
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private unsafe void TryClickLeaveDutyButton()
    {
        try
        {
            log.Information("[StuckDetection] Opening ContentsFinderMenu with callback");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 0);

            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    TryClickLeaveButton();
                }
                catch (Exception ex)
                {
                    log.Error($"[StuckDetection] ContinueWith exception in TryClickLeaveButton: {ex.Message}");
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            log.Error($"[StuckDetection] Error trying to leave duty during bailout: {ex.Message}");
        }
    }

    private unsafe void TryClickLeaveButton()
    {
        try
        {
            log.Information("[StuckDetection] Clicking Leave button on ContentsFinderMenu");
            GameHelpers.FireAddonCallback("ContentsFinderMenu", true, 43);

            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    HandleLeaveConfirmation();
                }
                catch (Exception ex)
                {
                    log.Error($"[StuckDetection] ContinueWith exception in HandleLeaveConfirmation: {ex.Message}");
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            log.Error($"[StuckDetection] Error clicking Leave button during bailout: {ex.Message}");
        }
    }

    private void HandleLeaveConfirmation()
    {
        try
        {
            if (GameHelpers.ClickYesIfVisible())
            {
                log.Information("[StuckDetection] Clicked Yes on bailout leave confirmation dialog");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[StuckDetection] Error handling bailout leave confirmation: {ex.Message}");
        }
    }
}
