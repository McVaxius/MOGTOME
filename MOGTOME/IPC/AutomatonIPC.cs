using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MOGTOME.IPC;

public class AutomatonIPC : IDisposable
{
    private readonly IPluginLog log;

    private ICallGateSubscriber<string, bool>? isTweakEnabled;
    private ICallGateSubscriber<string, bool, object>? setTweakState;

    public AutomatonIPC(IPluginLog log)
    {
        this.log = log;

        try
        {
            isTweakEnabled = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Automaton.IsTweakEnabled");
            setTweakState = Plugin.PluginInterface.GetIpcSubscriber<string, bool, object>("Automaton.SetTweakState");
            log.Information("[MOGTOME][Automaton] IPC subscribers initialized");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Automaton] IPC init failed (plugin may not be loaded): {ex.Message}");
        }
    }

    public bool IsTweakEnabled(string tweakName)
    {
        try
        {
            return isTweakEnabled?.InvokeFunc(tweakName) ?? false;
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][Automaton] IsTweakEnabled({tweakName}) failed: {ex.Message}");
            return false;
        }
    }

    public void SetTweakState(string tweakName, bool enabled)
    {
        try
        {
            setTweakState?.InvokeAction(tweakName, enabled);
            log.Debug($"[MOGTOME][Automaton] SetTweakState: {tweakName} = {enabled}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Automaton] SetTweakState({tweakName}) failed: {ex.Message}");
        }
    }

    public void DisableAutoQueue()
    {
        if (IsTweakEnabled("AutoQueue"))
        {
            SetTweakState("AutoQueue", false);
            log.Information("[MOGTOME][Automaton] AutoQueue disabled");
        }
    }

    public void EnableAutoQueue()
    {
        if (!IsTweakEnabled("AutoQueue"))
        {
            SetTweakState("AutoQueue", true);
            log.Information("[MOGTOME][Automaton] AutoQueue enabled");
        }
    }

    public void Dispose() { }
}
