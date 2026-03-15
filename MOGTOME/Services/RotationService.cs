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

        // We use RSR for rotations
        autoDutyIPC.SetUsingAlternativeRotation(false);

        log.Information($"[Rotation] Initialized: BossMod={state.WhichBossMod}, using RSR for rotation");
    }

    public void ForceRotation()
    {
        // Always use RSR
        bossModIPC.EnableRSR();
    }

    public void EnableRotation()
    {
        bossModIPC.EnableRSR();
    }

    public void DisableRotation()
    {
        bossModIPC.DisableAI();
        bossModIPC.DisableRSR();
    }
}
