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

    public RotationService(
        IPluginLog log, Configuration config, DutyState state,
        BossModIPC bossModIPC)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.bossModIPC = bossModIPC;
    }

    public void Initialize()
    {
        bossModIPC.DetectBossMod();
        state.WhichBossMod = bossModIPC.WhichBossMod;

        // We use RSR for rotations
        // autoDutyIPC.SetUsingAlternativeRotation(false); // Removed to avoid circular dependency

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
        // Do not disable RSR or BossMod AI - let them continue running
        log.Debug("[Rotation] DisableRotation called - no action taken (RSR/BossMod left enabled)");
    }
}
