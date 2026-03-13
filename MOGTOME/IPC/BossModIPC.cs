using System;
using Dalamud.Plugin.Services;

namespace MOGTOME.IPC;

public class BossModIPC : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;

    public string WhichBossMod { get; private set; } = "vbm";

    public BossModIPC(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;
    }

    public void DetectBossMod()
    {
        try
        {
            var installed = Plugin.PluginInterface.InstalledPlugins;
            foreach (var plugin in installed)
            {
                if (plugin.IsLoaded && plugin.InternalName == "BossModReborn")
                {
                    WhichBossMod = "bmr";
                    log.Information("[BossMod] Detected BossModReborn (bmr)");
                    return;
                }
            }
            WhichBossMod = "vbm";
            log.Information("[BossMod] Using default VBM");
        }
        catch (Exception ex)
        {
            WhichBossMod = "vbm";
            log.Warning($"[BossMod] Detection failed, defaulting to vbm: {ex.Message}");
        }
    }

    public void SetPreset(string presetName)
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai preset {presetName}";
            log.Information($"[BossMod] Setting preset: {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[BossMod] SetPreset failed: {ex.Message}");
        }
    }

    public void EnableAI()
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai on";
            log.Debug($"[BossMod] {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[BossMod] EnableAI failed: {ex.Message}");
        }
    }

    public void DisableAI()
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai off";
            log.Debug($"[BossMod] {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[BossMod] DisableAI failed: {ex.Message}");
        }
    }

    public void ForceRotation(string preset)
    {
        if (preset == "none")
        {
            // Using RSR instead of BossMod
            EnableRSR();
        }
        else
        {
            DisableRSR();
            SetPreset(preset);
            EnableAI();
        }
    }

    public void EnableRSR()
    {
        try
        {
            commandManager.ProcessCommand("/rotation auto");
            log.Debug("[BossMod] RSR auto rotation enabled");
        }
        catch (Exception ex)
        {
            log.Error($"[BossMod] EnableRSR failed: {ex.Message}");
        }
    }

    public void DisableRSR()
    {
        try
        {
            commandManager.ProcessCommand("/rotation cancel");
            log.Debug("[BossMod] RSR rotation cancelled");
        }
        catch (Exception ex)
        {
            log.Debug($"[BossMod] DisableRSR failed (may not be installed): {ex.Message}");
        }
    }

    public void Dispose() { }
}
