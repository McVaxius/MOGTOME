using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MOGTOME.Services;

namespace MOGTOME.IPC;

public class AutoDutyIPC : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private readonly RunHistoryService runHistoryService;
    private readonly RotationService rotationService;

    private ICallGateSubscriber<string, string, object>? setConfig;

    public AutoDutyIPC(IPluginLog log, ICommandManager commandManager, RunHistoryService runHistoryService, RotationService rotationService)
    {
        this.log = log;
        this.commandManager = commandManager;
        this.runHistoryService = runHistoryService;
        this.rotationService = rotationService;

        try
        {
            setConfig = Plugin.PluginInterface.GetIpcSubscriber<string, string, object>("AutoDuty.SetConfig");
            log.Information("[MOGTOME][AutoDuty] IPC subscribers initialized");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][AutoDuty] IPC init failed (plugin may not be loaded): {ex.Message}");
        }
    }

    public void SetConfig(string key, string value)
    {
        try
        {
            setConfig?.InvokeAction(key, value);
            log.Debug($"[MOGTOME][AutoDuty] SetConfig: {key} = {value}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][AutoDuty] SetConfig failed for {key}: {ex.Message}");
        }
    }

    public void ConfigureForMogtome(bool isLeader)
    {
        SetConfig("AutoDutyModeEnum", "Looping");
        SetConfig("DutyModeEnum", "Regular");
        // MOGTOME owns the selected combat provider state.
        SetConfig("AutoManageRotationPluginState", "false");
        SetConfig("AutoManageBossModAISettings", "false");
        SetConfig("BM_UpdatePresetsAutomatically", "true");
        SetConfig("maxDistanceToTargetRoleBased", "true");
        SetConfig("positionalRoleBased", "true");
        SetConfig("AutoExitDuty", isLeader ? "false" : "true");
        SetConfig("OnlyExitWhenDutyDone", "true");
        SetConfig("EnableTerminationActions", "false");
        SetConfig("Unsynced", "true");
        // LevelSync is set by the engine based on TestingModeUnsynced config
        log.Information($"[MOGTOME][AutoDuty] Configured for MOGTOME (leader={isLeader}) - Unsync=ON, combat-provider auto-manage=OFF");
    }

    public void SetUsingAlternativeRotation(bool useAlternative)
    {
        SetConfig("UsingAlternativeRotationPlugin", useAlternative ? "true" : "false");
    }

    public void SetPraetoriumPath()
    {
        try
        {
            SetConfig("AutoDutyModeEnum", "Looping");
            SetConfig("DutyModeEnum", "Regular");
            log.Information("[MOGTOME][AutoDuty] Prepared AutoDuty Looping/Regular mode for Praetorium path selection");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][AutoDuty] SetPraetoriumPath failed: {ex.Message}");
        }
    }

    public void StartDuty()
    {
        try
        {
            log.Information("[MOGTOME][AutoDuty] Starting via /ad start");
            
            // Capture party snapshot before starting AutoDuty
            // This ensures we have the full party composition before anyone leaves
            try
            {
                runHistoryService.CapturePartySnapshot();
                log.Information("[MOGTOME][AutoDuty] Party snapshot captured before /ad start");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[MOGTOME][AutoDuty] Failed to capture party snapshot before /ad start");
            }
            
            commandManager.ProcessCommand("/ad start");
            
            // Refresh the combat stack after starting AutoDuty.
            try
            {
                rotationService.ForceRotation();
                log.Information("[MOGTOME][AutoDuty] Selected combat provider refreshed after /ad start");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[MOGTOME][AutoDuty] Failed to refresh selected combat provider after /ad start");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][AutoDuty] Start failed: {ex.Message}");
        }
    }

    public void StopDuty()
    {
        try
        {
            log.Information("[MOGTOME][AutoDuty] Stopping via /ad stop");
            commandManager.ProcessCommand("/ad stop");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][AutoDuty] Stop failed: {ex.Message}");
        }
    }

    public void Dispose() { }
}
