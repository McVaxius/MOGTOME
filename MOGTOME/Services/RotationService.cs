using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class RotationService
{
    private readonly IPluginLog log;
    private readonly ConfigManager configManager;
    private readonly BossModIPC bossModIPC;

    public RotationService(
        IPluginLog log, ConfigManager configManager,
        BossModIPC bossModIPC)
    {
        this.log = log;
        this.configManager = configManager;
        this.bossModIPC = bossModIPC;
    }

    public void Initialize()
    {
        var config = configManager.GetActiveConfig();
        if (config.CombatProvider is CombatProvider.Bmr or CombatProvider.Vbm)
        {
            bossModIPC.PreparePresetForStart(
                config.CombatProvider,
                config.UseManualBossModPreset,
                config.ManualBossModPresetName);
        }

        log.Information($"[MOGTOME][Rotation] Initialized selected combat provider: {config.CombatProvider}");
    }

    public void ForceRotation()
        => EnableSelectedProvider();

    public void EnableRotation()
        => EnableSelectedProvider();

    public void DisableRotation()
    {
        var provider = configManager.GetActiveConfig().CombatProvider;
        switch (provider)
        {
            case CombatProvider.Rsr:
                bossModIPC.SendCommand("/rotation cancel", "disable RSR");
                break;
            case CombatProvider.Bmr:
                bossModIPC.SendCommand("/bmrai off", "disable BMR");
                break;
            case CombatProvider.Vbm:
                bossModIPC.SendCommand("/vbmai off", "disable VBM");
                break;
            case CombatProvider.Wrath:
                bossModIPC.SendCommand("/wrath auto off", "disable Wrath");
                break;
        }
    }

    private void EnableSelectedProvider()
    {
        var provider = configManager.GetActiveConfig().CombatProvider;
        switch (provider)
        {
            case CombatProvider.Rsr:
                if (!bossModIPC.TrySetRsrAutoViaIpc())
                    bossModIPC.SendCommand("/rotation auto", "enable RSR fallback");
                break;
            case CombatProvider.Bmr:
                bossModIPC.SendCommand("/bmrai on", "enable BMR");
                break;
            case CombatProvider.Vbm:
                bossModIPC.SendCommand("/vbmai on", "enable VBM");
                break;
            case CombatProvider.Wrath:
                bossModIPC.SendCommand("/wrath auto on", "enable Wrath");
                break;
        }
    }
}
