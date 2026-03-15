using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MOGTOME.IPC;

public class AutoDutyIPC : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;

    private ICallGateSubscriber<string, string, object>? setConfig;

    public AutoDutyIPC(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;

        try
        {
            setConfig = Plugin.PluginInterface.GetIpcSubscriber<string, string, object>("AutoDuty.SetConfig");
            log.Information("[AutoDuty] IPC subscribers initialized");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoDuty] IPC init failed (plugin may not be loaded): {ex.Message}");
        }
    }

    public void SetConfig(string key, string value)
    {
        try
        {
            setConfig?.InvokeAction(key, value);
            log.Debug($"[AutoDuty] SetConfig: {key} = {value}");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoDuty] SetConfig failed for {key}: {ex.Message}");
        }
    }

    public void ConfigureForMogtome(bool isLeader)
    {
        SetConfig("AutoManageRotationPluginState", "true");
        SetConfig("AutoManageBossModAISettings", "true");
        SetConfig("BM_UpdatePresetsAutomatically", "true");
        SetConfig("maxDistanceToTargetRoleBased", "true");
        SetConfig("positionalRoleBased", "true");
        SetConfig("AutoExitDuty", isLeader ? "true" : "false");
        SetConfig("OnlyExitWhenDutyDone", "true");
        SetConfig("EnableTerminationActions", "false");
        SetConfig("Unsynced", "true");
        log.Information($"[AutoDuty] Configured for MOGTOME (leader={isLeader})");
    }

    public void SetUsingAlternativeRotation(bool useAlternative)
    {
        SetConfig("UsingAlternativeRotationPlugin", useAlternative ? "true" : "false");
    }

    public void SetPraetoriumPath()
    {
        try
        {
            // Set AutoDuty to use the Praetorium W2W path
            SetConfig("SelectedPathName", "(1044) The Praetorium - W2W 20250716 phecda");
            SetConfig("SelectedPathTerritoryType", "1044");
            SetConfig("SelectedMode", "Regular");
            log.Information("[AutoDuty] Set Praetorium W2W path via IPC");
        }
        catch (Exception ex)
        {
            log.Warning($"[AutoDuty] SetPraetoriumPath failed: {ex.Message}");
        }
    }

    public void QueueDuty(string dutyName)
    {
        try
        {
            var cmd = $"/ad queue {dutyName}";
            log.Information($"[AutoDuty] Queueing: {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDuty] Queue failed: {ex.Message}");
        }
    }

    public void StartDuty()
    {
        try
        {
            log.Information("[AutoDuty] Starting via /ad start");
            commandManager.ProcessCommand("/ad start");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDuty] Start failed: {ex.Message}");
        }
    }

    public void StopDuty()
    {
        try
        {
            log.Information("[AutoDuty] Stopping via /ad stop");
            commandManager.ProcessCommand("/ad stop");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDuty] Stop failed: {ex.Message}");
        }
    }

    public void Dispose() { }
}
