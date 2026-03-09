using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MOGTOME.Services;

public class StuckDetection : IDisposable
{
    private readonly Configuration config;
    private Vector3 lastPosition;
    private DateTime lastMoveTime = DateTime.Now;
    private DateTime lastStuckCheck = DateTime.Now;
    private int stuckCounter = 0;
    private bool isStuck = false;

    public StuckDetection(Configuration config)
    {
        this.config = config;
        Service.Log.Info("StuckDetection initialized");
    }

    public bool IsStuck()
    {
        try
        {
            if (!config.StuckDetection)
            {
                return false;
            }

            var now = DateTime.Now;
            var localPlayer = Service.ClientState.LocalPlayer;
            
            if (localPlayer == null)
            {
                return false;
            }

            var currentPosition = localPlayer.Position;
            var distance = Vector3.Distance(currentPosition, lastPosition);

            // Check if we've moved significantly
            if (distance > 5.0f) // Moved more than 5 yalms
            {
                lastPosition = currentPosition;
                lastMoveTime = now;
                stuckCounter = 0;
                isStuck = false;
                return false;
            }

            // Check if we've been stationary for too long
            var stationaryTime = (now - lastMoveTime).TotalSeconds;
            
            if (stationaryTime >= config.StuckTimeout)
            {
                if (!isStuck)
                {
                    isStuck = true;
                    stuckCounter++;
                    Service.Log.Warning($"Stuck detected! Stationary for {stationaryTime:F1} seconds (stuck #{stuckCounter})");
                }
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in stuck detection: {ex.Message}");
            return false;
        }
    }

    public bool IsPositionStuck()
    {
        try
        {
            var localPlayer = Service.ClientState.LocalPlayer;
            if (localPlayer == null) return false;

            var currentPosition = localPlayer.Position;
            var distance = Vector3.Distance(currentPosition, lastPosition);
            
            // Check if position hasn't changed much
            return distance < 0.5f; // Less than 0.5 yalms movement
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking position stuck: {ex.Message}");
            return false;
        }
    }

    public bool ShouldAttemptRecovery()
    {
        return isStuck && stuckCounter >= 2; // Only attempt recovery after 2 stuck detections
    }

    public void UpdatePosition()
    {
        try
        {
            var localPlayer = Service.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            var currentPosition = localPlayer.Position;
            var distance = Vector3.Distance(currentPosition, lastPosition);
            
            // Update if we've moved significantly
            if (distance > 1.0f)
            {
                lastPosition = currentPosition;
                lastMoveTime = DateTime.Now;
                
                if (isStuck)
                {
                    Service.Log.Info("No longer stuck - movement detected");
                    isStuck = false;
                    stuckCounter = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error updating position: {ex.Message}");
        }
    }

    public void ResetStuckDetection()
    {
        try
        {
            var localPlayer = Service.ClientState.LocalPlayer;
            if (localPlayer != null)
            {
                lastPosition = localPlayer.Position;
            }
            
            lastMoveTime = DateTime.Now;
            stuckCounter = 0;
            isStuck = false;
            
            Service.Log.Debug("Stuck detection reset");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error resetting stuck detection: {ex.Message}");
        }
    }

    public StuckRecoveryAction GetRecoveryAction()
    {
        try
        {
            // Determine recovery action based on current situation
            var territory = Service.ClientState.TerritoryType;
            var inDuty = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty];
            var mounted = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted];
            var flying = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight];

            if (inDuty)
            {
                // In duty - use return command
                return StuckRecoveryAction.Return;
            }
            else if (mounted)
            {
                // Mounted - dismount
                return StuckRecoveryAction.Dismount;
            }
            else if (territory == 1044 || territory == 1048) // Praetorium or Porta
            {
                // In specific territories - use specific recovery
                return StuckRecoveryAction.RestartAutoDuty;
            }
            else
            {
                // General case - try movement
                return StuckRecoveryAction.Move;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error determining recovery action: {ex.Message}");
            return StuckRecoveryAction.None;
        }
    }

    public async Task<bool> ExecuteRecovery(StuckRecoveryAction action)
    {
        try
        {
            Service.Log.Info($"Executing stuck recovery: {action}");

            switch (action)
            {
                case StuckRecoveryAction.Return:
                    return await ExecuteReturnRecovery();
                
                case StuckRecoveryAction.Dismount:
                    return await ExecuteDismountRecovery();
                
                case StuckRecoveryAction.RestartAutoDuty:
                    return await ExecuteRestartAutoDutyRecovery();
                
                case StuckRecoveryAction.Move:
                    return await ExecuteMoveRecovery();
                
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error executing recovery: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteReturnRecovery()
    {
        try
        {
            Service.Chat.ExecuteCommand("/return");
            await Task.Delay(2000);

            // Handle confirmation dialog
            await ClickYesIfVisible();
            
            // Wait for return to complete
            await Task.Delay(12000); // 12 seconds for return animation
            
            ResetStuckDetection();
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in return recovery: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteDismountRecovery()
    {
        try
        {
            Service.Chat.ExecuteCommand("/dismount");
            await Task.Delay(2000);
            
            ResetStuckDetection();
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in dismount recovery: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteRestartAutoDutyRecovery()
    {
        try
        {
            Service.Chat.ExecuteCommand("/ad stop");
            await Task.Delay(2000);
            
            Service.Chat.ExecuteCommand("/return");
            await Task.Delay(2000);
            
            await ClickYesIfVisible();
            await Task.Delay(12000);
            
            Service.Chat.ExecuteCommand("/ad start");
            await Task.Delay(2000);
            
            ResetStuckDetection();
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in restart AutoDuty recovery: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteMoveRecovery()
    {
        try
        {
            // Try small movements in different directions
            var movements = new[] { "forward 1", "backward 1", "left 1", "right 1" };
            
            foreach (var movement in movements)
            {
                Service.Chat.ExecuteCommand($"/movement {movement}");
                await Task.Delay(500);
            }

            ResetStuckDetection();
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in move recovery: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ClickYesIfVisible()
    {
        try
        {
            var addonPtr = Service.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
            {
                return false;
            }

            unsafe
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr;
                if (!addon->IsVisible)
                {
                    return false;
                }

                var values = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[2];
                values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[0].Int = 0; // Yes button
                values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[1].Int = 0;

                addon->FireCallback(2, values);
                Service.Log.Debug("Clicked Yes on SelectYesno dialog (stuck recovery)");
                return true;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking Yes dialog: {ex.Message}");
            return false;
        }
    }

    public void Reset()
    {
        ResetStuckDetection();
        Service.Log.Info("StuckDetection reset");
    }

    public void Dispose()
    {
        Service.Log.Info("StuckDetection disposed");
    }
}

public enum StuckRecoveryAction
{
    None,
    Return,
    Dismount,
    RestartAutoDuty,
    Move
}
