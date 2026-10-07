using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class RotationService : IDisposable
{
    private readonly IPluginLog log;
    private readonly ConfigManager configManager;
    private readonly BossModIPC bossModIPC;
    private bool rotationEnableSentForDuty;
    private bool rotationDisableSentForDuty;
    private readonly HashSet<CombatProvider> enabledComponents = [];
    private CombatProvider? preparedBossMod;
    private Configuration? preparedConfig;
    private CombatProvider preparedProvider;
    private string preparedAccount = string.Empty;
    private string preparedCharacter = string.Empty;
    private bool settingsPending;
    private bool sessionEnded;
    private (bool Manual, string Name)? appliedPresetSettings;
    public string LastFailureReason => Failure.English;
    public UiText Failure { get; private set; } = string.Empty;
    private static readonly TimeSpan RsrHealthProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RsrRecoveryCommandSuppression = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RsrReflectionFailureLogInterval = TimeSpan.FromSeconds(60);
    private DateTime lastRsrHealthProbeUtc = DateTime.MinValue;
    private DateTime rsrRecoveryCommandSuppressedUntilUtc = DateTime.MinValue;
    private DateTime lastRsrReflectionFailureLogUtc = DateTime.MinValue;
    private bool rsrFallbackUnavailableLoggedForDuty;

    public RotationService(
        IPluginLog log, ConfigManager configManager,
        BossModIPC bossModIPC)
    {
        this.log = log;
        this.configManager = configManager;
        this.bossModIPC = bossModIPC;
        configManager.ConfigurationChanged += OnConfigurationChanged;
    }

    public void Dispose()
    {
        configManager.ConfigurationChanged -= OnConfigurationChanged;
        DisableRotationForDutyEnd("rotation service disposal");
    }

    private string ReadCharacterIdentity()
    {
        var character = configManager.GetCurrentCharacterConfig();
        return Plugin.ClientState.IsLoggedIn && character.ContentId != 0 && Plugin.PlayerState.ContentId == character.ContentId
            ? character.ContentId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }

    private bool MatchesPreparedSession(Configuration config)
        => ReferenceEquals(preparedConfig, config) && preparedProvider == config.CombatProvider
            && preparedAccount == configManager.CurrentAccountId && preparedCharacter == ReadCharacterIdentity();

    internal void ObserveSessionDeparture()
    {
        if (preparedConfig is null) return;
        if (!MatchesPreparedSession(configManager.GetActiveConfig()) || !bossModIPC.IsOwnedProviderCurrent)
        {
            DisableRotationForDutyEnd("account, character, provider or profile departure");
            preparedConfig = null;
            sessionEnded = true;
            Fail(Ui.M("Rotation_BossModSessionChanged"));
        }
    }

    internal void EndSessionForLogout()
    {
        if (preparedConfig is null) return;
        DisableRotationForDutyEnd("native logout transition");
        preparedConfig = null;
        sessionEnded = true;
    }

    private void OnConfigurationChanged(Configuration config)
    {
        if (preparedConfig is not null && !MatchesPreparedSession(config))
        {
            ObserveSessionDeparture();
            return;
        }
        settingsPending = true;
    }

    private static (bool Manual, string Name) CurrentPresetSettings(Configuration config)
        => (config.UseManualBossModPreset, config.UseManualBossModPreset ? config.ManualBossModPresetName : string.Empty);

    internal bool ValidateManualPresetSelection(Configuration config)
    {
        if (config.CombatProvider is not (CombatProvider.Bmr or CombatProvider.Vbm) || !config.UseManualBossModPreset)
            return true;
        var catalog = bossModIPC.ReadPresetCatalog(config.CombatProvider);
        var replacement = BossModIPC.ResolvePresetSelection(config.ManualBossModPresetName, catalog);
        if (replacement != config.ManualBossModPresetName)
        {
            config.ManualBossModPresetName = replacement;
            configManager.SaveCurrentAccount();
            configManager.NotifyConfigurationChanged();
        }
        return catalog.Readable && !string.IsNullOrWhiteSpace(config.ManualBossModPresetName)
            && catalog.DefinitionNames.Any(name => string.Equals(name, config.ManualBossModPresetName, StringComparison.CurrentCultureIgnoreCase));
    }

    internal bool ApplyPendingCombatSettings()
    {
        if (!settingsPending || !rotationEnableSentForDuty || rotationDisableSentForDuty)
            return true;
        ObserveSessionDeparture();
        if (rotationDisableSentForDuty || preparedConfig is null)
            return false;
        var config = configManager.GetActiveConfig();
        var selected = CurrentPresetSettings(config);
        if (appliedPresetSettings == selected || config.CombatProvider is CombatProvider.Rsr or CombatProvider.Wrath)
        {
            appliedPresetSettings = selected;
            settingsPending = false;
            return true;
        }
        if (!ValidateManualPresetSelection(config) || preparedBossMod is null
            || !bossModIPC.PreparePresetForStart(preparedBossMod.Value, passive: false, config.UseManualBossModPreset, config.ManualBossModPresetName))
        {
            settingsPending = false;
            log.Warning($"[MOGTOME][Rotation] Live preset application was unconfirmed: {bossModIPC.LastSettingsStatus}");
            return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
        }
        appliedPresetSettings = CurrentPresetSettings(config);
        settingsPending = false;
        Failure = string.Empty;
        return true;
    }

    public bool Initialize(bool preferBmr = false)
    {
        if (!DisableEnabledComponents())
            return Fail(Ui.M("Rotation_CouldNotDisableCombatComponentsFromThe"));
        ResetDutyRotationState("engine start");
        preparedBossMod = null;
        preparedConfig = null;
        sessionEnded = false;
        appliedPresetSettings = null;
        settingsPending = false;

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
            return Fail(Ui.M("Rotation_BothBossModVariantsAreStillLoadedConflict"));

        if (config.CombatProvider != CombatProvider.Wrath)
        {
            preparedBossMod = config.CombatProvider == CombatProvider.Rsr
                ? bmrLoaded ? CombatProvider.Bmr : vbmLoaded ? CombatProvider.Vbm : null
                : config.CombatProvider;
            if (preparedBossMod == null ||
                (preparedBossMod == CombatProvider.Bmr && !bmrLoaded) ||
                (preparedBossMod == CombatProvider.Vbm && !vbmLoaded))
                return Fail(Ui.M("Rotation_RequiresLoadedBossModSupport", config.CombatProvider, (config.CombatProvider == CombatProvider.Rsr ? Ui.M("Rotation_BMROrVBM") : (UiText)config.CombatProvider.ToString())));

            var character = ReadCharacterIdentity();
            if (string.IsNullOrEmpty(configManager.CurrentAccountId) || string.IsNullOrEmpty(character)
                || !bossModIPC.BeginOwnedBossModSettings(preparedBossMod.Value, configManager.CurrentAccountId, character, config))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            if (!bossModIPC.RefreshPackagedPresets())
            {
                DisableEnabledComponents();
                return Fail(Ui.M("Rotation_BossModPackagedPresetInstallationFailedCheckThat"));
            }
        }

        preparedConfig = config;
        preparedProvider = config.CombatProvider;
        preparedAccount = configManager.CurrentAccountId;
        preparedCharacter = ReadCharacterIdentity();

        Failure = string.Empty;
        log.Information($"[MOGTOME][Rotation] Initialized selected combat provider: {config.CombatProvider}");
        return true;
    }

    internal void RestoreRsrHealingForStart()
        => bossModIPC.RestoreRsrHealing();

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

    internal void InvalidateCombatActivation()
    {
        if (rotationDisableSentForDuty)
            return;

        rotationEnableSentForDuty = false;
        ResetRsrHealthState();
    }

    public bool EnableRotationOncePerDuty(string reason)
    {
        if (sessionEnded)
            return Fail(Ui.M("Rotation_BossModSessionChanged"));
        if (rotationDisableSentForDuty)
            return Fail(Ui.M("Rotation_CombatActivationWasRequestedAfterThisDuty", reason));
        if (rotationEnableSentForDuty)
        {
            log.Debug($"[MOGTOME][Rotation] Skipped selected combat provider enable; already enabled for this duty ({reason})");
            return true;
        }

        var provider = configManager.GetActiveConfig().CombatProvider;
        try
        {
            if (!EnableSelectedProvider() || rotationDisableSentForDuty)
            {
                DisableEnabledComponents();
                return false;
            }
        }
        catch (Exception ex)
        {
            DisableEnabledComponents();
            return Fail(Ui.M("Rotation_CombatActivationFailed", ex.Message));
        }
        rotationEnableSentForDuty = true;
        rotationDisableSentForDuty = false;
        // Full activation also restored RSR; suppress its independent Off probe
        // while the provider applies that command.
        rsrRecoveryCommandSuppressedUntilUtc = DateTime.UtcNow + RsrRecoveryCommandSuppression;
        Failure = string.Empty;
        log.Information($"[MOGTOME][Rotation] enabled selected combat provider once per duty: {provider} ({reason})");
        return true;
    }

    public bool DisableRotationForDutyEnd(string reason)
    {
        // Terminal even when cleanup fails. A later Stop may retry cleanup,
        // but pending activation can never reopen this duty.
        rotationDisableSentForDuty = true;
        if (!DisableEnabledComponents())
            return Fail(Ui.M("Rotation_CouldNotDisableAllMogTomeCombatComponents", reason));
        log.Information($"[MOGTOME][Rotation] MogTome combat components are disabled ({reason})");
        return true;
    }

    public void UpdateDutyRotationHealth(uint territoryId, bool backendStarted, bool dutyCompleted, bool localPlayerDead, string reason)
    {
        if (DutyState.IsMogtomeDutyTerritory(territoryId) && backendStarted && !dutyCompleted && !localPlayerDead)
            ApplyPendingCombatSettings();
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
        if (localPlayerDead || now < rsrRecoveryCommandSuppressedUntilUtc)
            return;

        if (lastRsrHealthProbeUtc != DateTime.MinValue &&
            now - lastRsrHealthProbeUtc < RsrHealthProbeInterval)
        {
            return;
        }

        lastRsrHealthProbeUtc = now;

        if (!bossModIPC.TryGetRsrOperatingMode(out var mode, out var detail))
        {
            LogRsrReflectionFailure(detail, now, reason);
            return;
        }

        if (mode != RsrOperatingMode.Off)
        {
            return;
        }

        if (now < rsrRecoveryCommandSuppressedUntilUtc)
            return;

        if (!EnableRsr())
        {
            Fail(Ui.M("Rotation_RSRHealthRecoveryFailed", reason));
            rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
            return;
        }
        rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
        log.Warning($"[MOGTOME][Rotation] RSR health check recovered confirmed Off state: {detail} ({reason})");
    }

    private bool DisableEnabledComponents()
    {
        var restored = bossModIPC.BeginOwnedBossModCleanup();
        settingsPending = false;
        appliedPresetSettings = null;
        foreach (var component in enabledComponents.ToArray())
        {
            try
            {
                if (component is CombatProvider.Bmr or CombatProvider.Vbm && !bossModIPC.IsOwnedProviderCurrent)
                {
                    log.Warning("[MOGTOME][Rotation] The old BossMod provider is unavailable; its replacement is not stopped or restored.");
                    enabledComponents.Remove(component);
                    restored = false;
                    continue;
                }
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
            catch (Exception ex)
            {
                log.Warning($"[MOGTOME][Rotation] Could not disable {component}: {ex.Message}");
            }
        }
        var presetCleared = false;
        try { presetCleared = bossModIPC.ClearActivePreset(releaseSession: enabledComponents.Count == 0); }
        catch (Exception ex) { log.Warning($"[MOGTOME][Rotation] Could not clear preset: {ex.Message}"); }
        return enabledComponents.Count == 0 && presetCleared && restored;
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
        lastRsrReflectionFailureLogUtc = DateTime.MinValue;
        rsrFallbackUnavailableLoggedForDuty = false;
    }

    private bool EnableSelectedProvider()
    {
        var config = configManager.GetActiveConfig();
        var provider = config.CombatProvider;
        if (preparedConfig is not null && !MatchesPreparedSession(config))
            return Fail(Ui.M("Rotation_BossModSessionChanged"));
        if (provider != CombatProvider.Wrath)
        {
            var bmrLoaded = bossModIPC.IsPluginLoaded("BossModReborn");
            var vbmLoaded = bossModIPC.IsPluginLoaded("BossMod");
            if ((bmrLoaded && vbmLoaded) || preparedBossMod == null ||
                (preparedBossMod == CombatProvider.Bmr && !bmrLoaded) ||
                (preparedBossMod == CombatProvider.Vbm && !vbmLoaded) ||
                (provider != CombatProvider.Rsr && provider != preparedBossMod))
                return Fail(Ui.M("Rotation_BossModAvailabilityOrSelectionChangedAfterStartup"));

            var character = ReadCharacterIdentity();
            if (string.IsNullOrEmpty(character) || !bossModIPC.BeginOwnedBossModSettings(preparedBossMod.Value,
                    configManager.CurrentAccountId, character, config))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));

            if (!ValidateManualPresetSelection(config) || !bossModIPC.PreparePresetForStart(preparedBossMod.Value, provider == CombatProvider.Rsr,
                    config.UseManualBossModPreset, config.ManualBossModPresetName))
                return Fail(Ui.M("Rotation_PresetPreparationFailedCombatWasNotEnabled", preparedBossMod));
        }

        if (rotationDisableSentForDuty) return false;

        if (provider == CombatProvider.Rsr && !EnableRsr())
            return Fail(Ui.M("Rotation_RSRAutoIPCAndRotationAutoBoth"));

        var aiProvider = provider == CombatProvider.Wrath ? provider : preparedBossMod!.Value;
        var command = aiProvider switch
        {
            CombatProvider.Bmr => "/bmrai on",
            CombatProvider.Vbm => "/vbmai on",
            CombatProvider.Wrath => "/wrath auto on",
            _ => string.Empty,
        };
        if (rotationDisableSentForDuty) return false;
        if (aiProvider == CombatProvider.Bmr)
        {
            if (!bossModIPC.ApplyOwnedPreferredDistance(command => bossModIPC.SendCommand(command, "set BMR dodge clearance"), 1.5))
                log.Warning($"[MOGTOME][Rotation] Preferred distance retained: {bossModIPC.LastSettingsStatus}");
            if (rotationDisableSentForDuty) return false;
            bossModIPC.SendCommand("/bmrai forbidactions off", "allow BMR actions");
            if (rotationDisableSentForDuty) return false;
        }
        enabledComponents.Add(aiProvider);
        if (command.Length == 0 || !bossModIPC.SendCommand(command, $"enable {aiProvider}"))
            return Fail(Ui.M("Rotation_CouldNotEnableUsing", aiProvider, command));
        appliedPresetSettings = CurrentPresetSettings(config);
        settingsPending = false;
        return !rotationDisableSentForDuty;
    }

    private bool EnableRsr()
    {
        if (rotationDisableSentForDuty) return false;
        enabledComponents.Add(CombatProvider.Rsr);
        var accepted = bossModIPC.TrySetRsrAutoViaIpc();
        if (rotationDisableSentForDuty) return false;
        if (!accepted && !bossModIPC.SendCommand("/rotation auto", "enable RSR fallback"))
            return false;
        return !rotationDisableSentForDuty;
    }

    private bool Fail(UiText reason)
    {
        Failure = reason;
        log.Error($"[MOGTOME][Rotation] {reason}");
        return false;
    }
}
