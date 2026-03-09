using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MOGTOME.Services;

public class DutyManager : IDisposable
{
    private readonly Configuration config;
    private DateTime lastQueueAttempt = DateTime.MinValue;
    private DateTime lastLeaveAttempt = DateTime.MinValue;
    private int queueAttempts = 0;
    private int leaveAttempts = 0;

    public DutyManager(Configuration config)
    {
        this.config = config;
        Service.Log.Info("DutyManager initialized");
    }

    public bool CanQueueForDuty()
    {
        return config.CanStartAutomation() &&
               config.CurrentState == FarmingState.Idle &&
               (DateTime.Now - lastQueueAttempt).TotalSeconds >= config.QueueDelay &&
               !Service.Condition[ConditionFlag.BoundByDuty] &&
               !Service.Condition[ConditionFlag.BetweenAreas] &&
               !Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] &&
               !Service.Condition[ConditionFlag.WatchingCutscene] &&
               !Service.Condition[ConditionFlag.WatchingCutscene78];
    }

    public async Task<bool> QueueForDuty()
    {
        try
        {
            if (!CanQueueForDuty())
            {
                Service.Log.Debug("Cannot queue for duty - conditions not met");
                return false;
            }

            config.CurrentState = FarmingState.Queueing;
            lastQueueAttempt = DateTime.Now;
            queueAttempts++;

            Service.Log.Info($"Attempting to queue for {config.CurrentDuty} (attempt #{queueAttempts})");

            // Open duty finder
            Service.Chat.ExecuteCommand("/dutyfinder");
            await Task.Delay(1000);

            // Try to select the duty and queue
            var success = await SelectAndQueueDuty();

            if (success)
            {
                Service.Log.Info($"Successfully queued for {config.CurrentDuty}");
                config.CurrentState = FarmingState.PreDutyCheck;
                queueAttempts = 0;
                return true;
            }
            else
            {
                Service.Log.Warning($"Failed to queue for {config.CurrentDuty} (attempt #{queueAttempts})");
                
                if (queueAttempts >= 3)
                {
                    Service.Log.Error("Max queue attempts reached, stopping automation");
                    config.CurrentState = FarmingState.Error;
                    queueAttempts = 0;
                }
                else
                {
                    config.CurrentState = FarmingState.Idle;
                }
                
                return false;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error queueing for duty: {ex.Message}");
            config.CurrentState = FarmingState.Error;
            return false;
        }
    }

    private async Task<bool> SelectAndQueueDuty()
    {
        try
        {
            // Wait for duty finder to be ready
            var attempts = 0;
            while (attempts < 10)
            {
                var addonPtr = Service.GameGui.GetAddonByName("ContentsFinder", 1);
                if (addonPtr != 0)
                {
                    break;
                }
                
                await Task.Delay(500);
                attempts++;
            }

            if (attempts >= 10)
            {
                Service.Log.Warning("Duty finder addon not found after waiting");
                return false;
            }

            // Clear any existing selection
            await ClickDutyFinderButton(12, 1); // Clear button
            await Task.Delay(500);

            // Select the appropriate duty
            var dutyIndex = config.CurrentDuty == DutyType.Praetorium ? 15 : 4; // Based on G.O.O.N. research
            await ClickDutyFinderButton(3, dutyIndex);
            await Task.Delay(500);

            // Join the duty
            await ClickDutyFinderButton(12, 0); // Join button
            
            Service.Log.Debug($"Duty selection completed for {config.CurrentDuty}");
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error selecting duty: {ex.Message}");
            return false;
        }
    }

    private async Task ClickDutyFinderButton(int param1, int param2)
    {
        try
        {
            var addonPtr = Service.GameGui.GetAddonByName("ContentsFinder", 1);
            if (addonPtr == 0)
            {
                Service.Log.Warning("ContentsFinder addon not found");
                return;
            }

            unsafe
            {
                var addon = (AtkUnitBase*)addonPtr;
                var values = stackalloc AtkValue[2];
                values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[0].Int = param1;
                values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[1].Int = param2;
                
                addon->FireCallback(2, values);
                Service.Log.Debug($"Clicked ContentsFinder button: {param1}, {param2}");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking duty finder button: {ex.Message}");
        }
    }

    public bool IsInDuty()
    {
        return Service.Condition[ConditionFlag.BoundByDuty];
    }

    public bool IsInSpecificDuty(uint territoryId)
    {
        return IsInDuty() && Service.ClientState.TerritoryType == territoryId;
    }

    public bool IsInPraetorium()
    {
        return IsInSpecificDuty(1044);
    }

    public bool IsInPortaDecumana()
    {
        return IsInSpecificDuty(1048);
    }

    public DutyType GetCurrentDutyType()
    {
        if (!IsInDuty()) return DutyType.Praetorium; // Default

        return Service.ClientState.TerritoryType switch
        {
            1044 => DutyType.Praetorium,
            1048 => DutyType.PortaDecumana,
            _ => DutyType.Praetorium
        };
    }

    public async Task<bool> LeaveDuty()
    {
        try
        {
            if (!IsInDuty())
            {
                Service.Log.Debug("Not in duty, cannot leave");
                return false;
            }

            if ((DateTime.Now - lastLeaveAttempt).TotalSeconds < 5)
            {
                Service.Log.Debug("Leave duty on cooldown");
                return false;
            }

            config.CurrentState = FarmingState.Completing;
            lastLeaveAttempt = DateTime.Now;
            leaveAttempts++;

            Service.Log.Info($"Attempting to leave duty (attempt #{leaveAttempts})");

            // Open duty panel to access Leave Duty button
            Service.Chat.ExecuteCommand("/dutyfinder");
            await Task.Delay(500);

            // Try to click Leave Duty button
            var success = await ClickLeaveDutyButton();

            if (success)
            {
                Service.Log.Info("Leave duty command sent successfully");
                
                // Wait for confirmation dialog and click Yes
                await Task.Delay(config.LeaveDutyDelay * 1000);
                await ClickYesIfVisible();
                
                return true;
            }
            else
            {
                Service.Log.Warning($"Failed to send leave duty command (attempt #{leaveAttempts})");
                
                if (leaveAttempts >= 3)
                {
                    Service.Log.Error("Max leave attempts reached");
                    config.CurrentState = FarmingState.Error;
                    leaveAttempts = 0;
                }
                
                return false;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error leaving duty: {ex.Message}");
            config.CurrentState = FarmingState.Error;
            return false;
        }
    }

    private async Task<bool> ClickLeaveDutyButton()
    {
        try
        {
            // Try to find and click the Leave Duty button
            // This would typically be in the duty finder or a separate addon
            // For now, we'll use the command approach
            
            Service.Chat.ExecuteCommand("/leaveduty");
            await Task.Delay(500);
            
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking leave duty button: {ex.Message}");
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
                var addon = (AtkUnitBase*)addonPtr;
                if (!addon->IsVisible)
                {
                    return false;
                }

                var values = stackalloc AtkValue[2];
                values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[0].Int = 0; // Yes button
                values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[1].Int = 0;

                addon->FireCallback(2, values);
                Service.Log.Debug("Clicked Yes on SelectYesno dialog");
                return true;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking Yes dialog: {ex.Message}");
            return false;
        }
    }

    public void HandleDutyCompleted()
    {
        try
        {
            Service.Log.Info("Duty completed detected");
            
            // Increment counter
            config.DailyCounter++;
            config.Save();
            
            Service.Chat.Print($"Duty completed! ({config.DailyCounter}/{config.DailyTarget})");

            // Check if we should switch duty types
            if (config.DailyCounter >= 99 && config.CurrentDuty == DutyType.Praetorium)
            {
                config.CurrentDuty = DutyType.PortaDecumana;
                config.Save();
                Service.Chat.Print("Switched to Porta Decumana (99 Praetorium runs completed)");
            }

            // Handle leaving duty if configured
            if (config.LeaveDutyAfterComplete)
            {
                config.CurrentState = FarmingState.Completing;
                Service.Log.Info("Configured to leave duty after completion");
            }
            else
            {
                config.CurrentState = FarmingState.PostDuty;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error handling duty completion: {ex.Message}");
            config.CurrentState = FarmingState.Error;
        }
    }

    public void Reset()
    {
        lastQueueAttempt = DateTime.MinValue;
        lastLeaveAttempt = DateTime.MinValue;
        queueAttempts = 0;
        leaveAttempts = 0;
        Service.Log.Info("DutyManager reset");
    }

    public void Dispose()
    {
        Service.Log.Info("DutyManager disposed");
    }
}
