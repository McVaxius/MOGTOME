using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class RotationService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly BossModIPC bossModIPC;
    private readonly AutoDutyIPC autoDutyIPC;

    public RotationService(
        IPluginLog log, Configuration config, DutyState state,
        BossModIPC bossModIPC, AutoDutyIPC autoDutyIPC)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.bossModIPC = bossModIPC;
        this.autoDutyIPC = autoDutyIPC;
    }

    public void Initialize()
    {
        bossModIPC.DetectBossMod();
        state.WhichBossMod = bossModIPC.WhichBossMod;

        // Configure AutoDuty based on rotation choice
        if (config.BossModPreset != "none")
        {
            autoDutyIPC.SetUsingAlternativeRotation(true);
        }
        else
        {
            autoDutyIPC.SetUsingAlternativeRotation(false);
        }

        log.Information($"[Rotation] Initialized: BossMod={state.WhichBossMod}, Preset={config.BossModPreset}");
    }

    public void ForceRotation()
    {
        bossModIPC.ForceRotation(config.BossModPreset);
        if (config.EchoLevel < 2)
            log.Debug($"[Rotation] Force rotation: preset={config.BossModPreset}");
    }

    public void EnableRotation()
    {
        if (config.BossModPreset != "none")
        {
            bossModIPC.SetPreset(config.BossModPreset);
            bossModIPC.EnableAI();
        }
        else
        {
            bossModIPC.EnableRSR();
        }
    }

    public void DisableRotation()
    {
        bossModIPC.DisableAI();
        bossModIPC.DisableRSR();
    }
}
