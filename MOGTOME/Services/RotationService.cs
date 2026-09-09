using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly HashSet<CombatProvider> enabledComponents = [];
    private CombatProvider? preparedBossMod;
    public string LastFailureReason { get; private set; } = string.Empty;
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

    public bool Initialize(bool preferBmr = false)
    {
        if (!DisableEnabledComponents())
            return Fail("Could not disable combat components from the previous run.");
        ResetDutyRotationState("engine start");
        preparedBossMod = null;

        var config = configManager.GetActiveConfig();
        if (preferBmr && config.CombatProvider == CombatProvider.Vbm)
        {
            config.CombatProvider = CombatProvider.Bmr;
            configManager.SaveCurrentAccount();
            configManager.NotifyConfigurationChanged(force: true);
        }
        var bmrLoaded = bossModIPC.IsPluginLoaded("BossModReborn");
        var vbmLoaded = bossModIPC.IsPluginLoaded("BossMod");
        if (bmrLoaded && vbmLoaded)
            return Fail("Both BossMod variants are still loaded; conflict cleanup must finish before preset preparation.");

        if (config.CombatProvider != CombatProvider.Wrath)
        {
            preparedBossMod = config.CombatProvider == CombatProvider.Rsr
                ? bmrLoaded ? CombatProvider.Bmr : vbmLoaded ? CombatProvider.Vbm : null
                : config.CombatProvider;
            if (preparedBossMod == null ||
                (preparedBossMod == CombatProvider.Bmr && !bmrLoaded) ||
                (preparedBossMod == CombatProvider.Vbm && !vbmLoaded))
                return Fail($"{config.CombatProvider} requires loaded BossMod support ({(config.CombatProvider == CombatProvider.Rsr ? "BMR or VBM" : config.CombatProvider)}).");

            if (!bossModIPC.RefreshPackagedPresets())
                return Fail("BossMod packaged preset installation failed; check that its preset IPC is ready and all six preset files are present.");
        }

        LastFailureReason = string.Empty;
        log.Information($"[MOGTOME][Rotation] Initialized selected combat provider: {config.CombatProvider}");
        return true;
    }

    public bool ForceRotation()
        => EnableRotationOncePerDuty("force rotation");

    public bool EnableRotation()
        => EnableRotationOncePerDuty("enable rotation");

    public bool DisableRotation()
        => DisableRotationForDutyEnd("disable rotation");

    public void ResetDutyRotationState(string reason)
    {
        rotationEnableSentForDuty = false;
        rotationDisableSentForDuty = false;
        ResetRsrHealthState();
        log.Debug($"[MOGTOME][Rotation] Reset duty rotation lifecycle state ({reason})");
    }

    public bool EnableRotationOncePerDuty(string reason)
    {
        if (rotationDisableSentForDuty)
            return Fail($"Combat activation was requested after this duty ended ({reason}).");
        if (rotationEnableSentForDuty)
        {
            log.Debug($"[MOGTOME][Rotation] Skipped selected combat provider enable; already enabled for this duty ({reason})");
            return true;
        }

        var provider = configManager.GetActiveConfig().CombatProvider;
        try
        {
            if (!EnableSelectedProvider())
            {
                DisableEnabledComponents();
                return false;
            }
        }
        catch (Exception ex)
        {
            DisableEnabledComponents();
            return Fail($"Combat activation failed: {ex.Message}");
        }
        rotationEnableSentForDuty = true;
        rotationDisableSentForDuty = false;
        LastFailureReason = string.Empty;
        log.Information($"[MOGTOME][Rotation] enabled selected combat provider once per duty: {provider} ({reason})");
        return true;
    }

    public bool DisableRotationForDutyEnd(string reason)
    {
        if (rotationDisableSentForDuty)
        {
            log.Debug($"[MOGTOME][Rotation] Skipped selected combat provider disable; already disabled for this duty ({reason})");
            return true;
        }

        if (!DisableEnabledComponents())
            return Fail($"Could not disable all MogTome combat components ({reason}); see the failed command in the log.");
        rotationDisableSentForDuty = true;
        log.Information($"[MOGTOME][Rotation] MogTome combat components are disabled ({reason})");
        return true;
    }

    public void UpdateDutyRotationHealth(uint territoryId, bool backendStarted, bool dutyCompleted, bool localPlayerDead, string reason)
    {
        var provider = configManager.GetActiveConfig().CombatProvider;
        if (provider != CombatProvider.Rsr ||
            !DutyState.IsMogtomeDutyTerritory(territoryId) ||
            !backendStarted ||
            dutyCompleted ||
            !rotationEnableSentForDuty ||
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

        if (!EnableRsr())
        {
            Fail($"RSR health recovery failed ({reason}).");
            rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
            return;
        }
        rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
        rsrReviveRecoveryPending = false;
        log.Warning($"[MOGTOME][Rotation] RSR health check recovered confirmed Off state: {detail} ({reason})");
    }

    private bool DisableEnabledComponents()
    {
        foreach (var component in enabledComponents.ToArray())
        {
            var command = component switch
            {
                CombatProvider.Rsr => "/rotation cancel",
                CombatProvider.Bmr => "/bmrai off",
                CombatProvider.Vbm => "/vbmai off",
                CombatProvider.Wrath => "/wrath auto off",
                _ => throw new InvalidOperationException($"Unknown combat component {component}"),
            };
            if (bossModIPC.SendCommand(command, $"disable {component}"))
                enabledComponents.Remove(component);
        }
        var presetCleared = bossModIPC.ClearActivePreset();
        return enabledComponents.Count == 0 && presetCleared;
    }

    private void TrySendRsrReviveFallbackRecovery(DateTime now, string reason, string detail)
    {
        if (!rsrReviveRecoveryPending ||
            rsrFallbackRecoverySentForDuty ||
            now < rsrRecoveryCommandSuppressedUntilUtc)
        {
            return;
        }

        if (!EnableRsr())
        {
            Fail($"RSR revive recovery failed ({reason}).");
            rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
            return;
        }
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

    private bool EnableSelectedProvider()
    {
        var config = configManager.GetActiveConfig();
        var provider = config.CombatProvider;
        if (provider != CombatProvider.Wrath)
        {
            var bmrLoaded = bossModIPC.IsPluginLoaded("BossModReborn");
            var vbmLoaded = bossModIPC.IsPluginLoaded("BossMod");
            if ((bmrLoaded && vbmLoaded) || preparedBossMod == null ||
                (preparedBossMod == CombatProvider.Bmr && !bmrLoaded) ||
                (preparedBossMod == CombatProvider.Vbm && !vbmLoaded) ||
                (provider != CombatProvider.Rsr && provider != preparedBossMod))
                return Fail("BossMod availability or selection changed after startup. Stop and Start MogTome to prepare it again.");

            if (!bossModIPC.PreparePresetForStart(preparedBossMod.Value, provider == CombatProvider.Rsr,
                    config.UseManualBossModPreset, config.ManualBossModPresetName))
                return Fail($"{preparedBossMod} preset preparation failed; combat was not enabled.");
        }

        if (provider == CombatProvider.Rsr && !EnableRsr())
            return Fail("RSR Auto IPC and /rotation auto both failed.");

        var aiProvider = provider == CombatProvider.Wrath ? provider : preparedBossMod!.Value;
        var command = aiProvider switch
        {
            CombatProvider.Bmr => "/bmrai on",
            CombatProvider.Vbm => "/vbmai on",
            CombatProvider.Wrath => "/wrath auto on",
            _ => string.Empty,
        };
        if (command.Length == 0 || !bossModIPC.SendCommand(command, $"enable {aiProvider}"))
            return Fail($"Could not enable {aiProvider} using {command}.");
        enabledComponents.Add(aiProvider);
        return true;
    }

    private bool EnableRsr()
    {
        if (!bossModIPC.TrySetRsrAutoViaIpc() && !bossModIPC.SendCommand("/rotation auto", "enable RSR fallback"))
            return false;
        enabledComponents.Add(CombatProvider.Rsr);
        return true;
    }

    private bool Fail(string reason)
    {
        LastFailureReason = reason;
        log.Error($"[MOGTOME][Rotation] {reason}");
        return false;
    }
}
