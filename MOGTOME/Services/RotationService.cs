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
    private bool rotationEnableSentForDuty;
    private bool rotationDisableSentForDuty;

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
        => DisableSelectedProvider();

    public void ResetDutyRotationState(string reason)
    {
        rotationEnableSentForDuty = false;
        rotationDisableSentForDuty = false;
        log.Debug($"[MOGTOME][Rotation] Reset duty rotation lifecycle state ({reason})");
    }

    public void EnableRotationOncePerDuty(string reason)
    {
        if (rotationEnableSentForDuty)
        {
            log.Debug($"[MOGTOME][Rotation] Skipped selected combat provider enable; already enabled for this duty ({reason})");
            return;
        }

        var provider = configManager.GetActiveConfig().CombatProvider;
        EnableSelectedProvider();
        rotationEnableSentForDuty = true;
        rotationDisableSentForDuty = false;
        log.Information($"[MOGTOME][Rotation] enabled selected combat provider once per duty: {provider} ({reason})");
    }

    public void DisableRotationForDutyEnd(string reason)
    {
        if (rotationDisableSentForDuty)
        {
            log.Debug($"[MOGTOME][Rotation] Skipped selected combat provider disable; already disabled for this duty ({reason})");
            return;
        }

        var provider = configManager.GetActiveConfig().CombatProvider;
        DisableSelectedProvider();
        rotationDisableSentForDuty = true;
        log.Information($"[MOGTOME][Rotation] disabled selected combat provider for duty end: {provider} ({reason})");
    }

    private void DisableSelectedProvider()
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
