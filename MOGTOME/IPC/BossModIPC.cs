using System;
using Dalamud.Plugin.Services;
using MOGTOME.Services;

namespace MOGTOME.IPC;

public class BossModIPC : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;

    public string WhichBossMod { get; private set; } = "vbm";
    public bool HasRotationSolver { get; private set; }

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
            WhichBossMod = "vbm";
            HasRotationSolver = false;
            foreach (var plugin in installed)
            {
                if (plugin.IsLoaded &&
                    (plugin.InternalName == "RotationSolver" || plugin.InternalName == "RotationSolverReborn"))
                {
                    HasRotationSolver = true;
                }

                if (plugin.IsLoaded && plugin.InternalName == "BossModReborn")
                {
                    WhichBossMod = "bmr";
                }
            }

            if (WhichBossMod == "bmr")
                log.Information("[BossMod] Detected BossModReborn (bmr)");
            else
                log.Information("[BossMod] Using default VBM");
        }
        catch (Exception ex)
        {
            WhichBossMod = "vbm";
            HasRotationSolver = false;
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
            // Keep RSR enabled, don't disable it
            // DisableRSR(); // REMOVED - never disable RSR
            SetPreset(preset);
            EnableAI();
        }
    }

    public void EnableRSR()
    {
        try
        {
            commandManager.ProcessCommand("/rotation auto");
            //log.Debug("[BossMod] RSR auto rotation enabled");
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

    public void DisableKeyboardNoise()
    {
        if (!HasRotationSolver)
            return;

        try
        {
            const string cmd = "/rotation Settings KeyBoardNoise false";
            const string cmd2 = "/bmrai setpresetname AutoDuty Passive";
            const string cmd3 = "/vbm ar set AutoDuty Passive";
            GameHelpers.SendCommand(cmd);
            GameHelpers.SendCommand(cmd2);
            GameHelpers.SendCommand(cmd3);
            log.Information("[MOGTOME][BossMod] Requested RSR KeyBoardNoise=false and preconfigure both bossmods");
        }
        catch (Exception ex)
        {
            log.Error($"[BossMod] DisableKeyboardNoise failed: {ex.Message}");
        }
    }

    public void Dispose() { }
}
