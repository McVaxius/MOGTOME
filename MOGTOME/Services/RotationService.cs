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
    private static readonly TimeSpan RsrHealthProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RsrReviveProbeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RsrRecoveryCommandSuppression = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RsrReflectionFailureLogInterval = TimeSpan.FromSeconds(60);
    private DateTime lastRsrHealthProbeUtc = DateTime.MinValue;
    private DateTime rsrRecoveryCommandSuppressedUntilUtc = DateTime.MinValue;
    private DateTime rsrLocalPlayerRevivedUtc = DateTime.MinValue;
    private DateTime lastRsrReflectionFailureLogUtc = DateTime.MinValue;
    private bool rsrLocalPlayerWasDead;
    private bool rsrReviveRecoveryPending;
    private bool rsrFallbackUnavailableLoggedForDuty;
    private bool rsrFallbackRecoverySentForDuty;

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
        ResetRsrHealthState();
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

    public void UpdateDutyRotationHealth(uint territoryId, bool backendStarted, bool dutyCompleted, bool localPlayerDead, string reason)
    {
        var provider = configManager.GetActiveConfig().CombatProvider;
        if (provider != CombatProvider.Rsr ||
            !DutyState.IsMogtomeDutyTerritory(territoryId) ||
            !backendStarted ||
            dutyCompleted ||
            rotationDisableSentForDuty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (localPlayerDead)
        {
            rsrLocalPlayerWasDead = true;
            rsrLocalPlayerRevivedUtc = DateTime.MinValue;
            return;
        }

        if (rsrLocalPlayerWasDead)
        {
            rsrLocalPlayerWasDead = false;
            rsrReviveRecoveryPending = true;
            rsrLocalPlayerRevivedUtc = now;
        }

        if (rsrLocalPlayerRevivedUtc != DateTime.MinValue &&
            now - rsrLocalPlayerRevivedUtc < RsrReviveProbeDelay)
        {
            return;
        }

        if (lastRsrHealthProbeUtc != DateTime.MinValue &&
            now - lastRsrHealthProbeUtc < RsrHealthProbeInterval)
        {
            return;
        }

        lastRsrHealthProbeUtc = now;

        if (!bossModIPC.TryGetRsrOperatingMode(out var mode, out var detail))
        {
            LogRsrReflectionFailure(detail, now, reason);
            TrySendRsrReviveFallbackRecovery(now, reason, detail);
            return;
        }

        if (mode != RsrOperatingMode.Off)
        {
            rsrReviveRecoveryPending = false;
            return;
        }

        if (now < rsrRecoveryCommandSuppressedUntilUtc)
            return;

        EnableSelectedProvider();
        rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
        rsrReviveRecoveryPending = false;
        log.Warning($"[MOGTOME][Rotation] RSR health check recovered confirmed Off state: {detail} ({reason})");
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

    private void TrySendRsrReviveFallbackRecovery(DateTime now, string reason, string detail)
    {
        if (!rsrReviveRecoveryPending ||
            rsrFallbackRecoverySentForDuty ||
            now < rsrRecoveryCommandSuppressedUntilUtc)
        {
            return;
        }

        EnableSelectedProvider();
        rsrFallbackRecoverySentForDuty = true;
        rsrReviveRecoveryPending = false;
        rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
        log.Warning($"[MOGTOME][Rotation] RSR state reflection unavailable; sent one revive fallback recovery ({reason}). Detail: {detail}");
    }

    private void LogRsrReflectionFailure(string detail, DateTime now, string reason)
    {
        if (!rsrFallbackUnavailableLoggedForDuty ||
            lastRsrReflectionFailureLogUtc == DateTime.MinValue ||
            now - lastRsrReflectionFailureLogUtc >= RsrReflectionFailureLogInterval)
        {
            rsrFallbackUnavailableLoggedForDuty = true;
            lastRsrReflectionFailureLogUtc = now;
            log.Warning($"[MOGTOME][Rotation] RSR live state reflection unavailable; confirmed-off health recovery is degraded ({reason}). Detail: {detail}");
        }
    }

    private void ResetRsrHealthState()
    {
        lastRsrHealthProbeUtc = DateTime.MinValue;
        rsrRecoveryCommandSuppressedUntilUtc = DateTime.MinValue;
        rsrLocalPlayerRevivedUtc = DateTime.MinValue;
        lastRsrReflectionFailureLogUtc = DateTime.MinValue;
        rsrLocalPlayerWasDead = false;
        rsrReviveRecoveryPending = false;
        rsrFallbackUnavailableLoggedForDuty = false;
        rsrFallbackRecoverySentForDuty = false;
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
