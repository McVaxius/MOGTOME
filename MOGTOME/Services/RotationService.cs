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
    private readonly DutyState dutyState;
    private bool rotationEnableSentForDuty;
    private bool rotationDisableSentForDuty;
    private bool deathRearmPending;
    private bool rsrHealingRestored;
    private readonly HashSet<CombatProvider> enabledComponents = [];
    private CombatProvider? preparedBossMod;
    private Configuration? preparedConfig;
    private CombatProvider preparedProvider;
    private string preparedAccount = string.Empty;
    private string preparedCharacter = string.Empty;
    private bool settingsPending;
    private bool sessionEnded;
    private bool gmcSession;
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
        BossModIPC bossModIPC, DutyState dutyState)
    {
        this.log = log;
        this.configManager = configManager;
        this.bossModIPC = bossModIPC;
        this.dutyState = dutyState;
        configManager.ConfigurationChanged += OnConfigurationChanged;
    }

    public void Dispose()
    {
        configManager.ConfigurationChanged -= OnConfigurationChanged;
        DisableRotationForDutyEnd("rotation service disposal");
    }

    internal void SetAdsDutyIpcService(AdsDutyIpcService service) => bossModIPC.SetAdsDutyIpcService(service);
    internal bool IsVbmActivationDeferred => preparedBossMod == CombatProvider.Vbm && bossModIPC.IsVbmInteractionPauseActive;
    internal bool IsCombatActivationDeferred => IsVbmActivationDeferred || bossModIPC.IsOwnedCombatReturnPlayerMissing;
    internal bool UsesNativeAutorotation => gmcSession;
    internal bool OwnedCombatProviderDeparted { get; private set; }
    internal bool HasUsableAutomationHost(bool allowHostCleanup = false) => bossModIPC.HasUsableAutomationHost(allowHostCleanup);
    internal bool IsOwnedCombatHostCurrent => bossModIPC.IsOwnedProviderCurrent;
    internal bool SupportsBmrObstacleMaps => bossModIPC.SupportsBmrObstacleMaps;

    private string ReadCharacterIdentity()
    {
        var character = configManager.GetCurrentCharacterConfig();
        return Plugin.ClientState.IsLoggedIn && character.ContentId != 0 && Plugin.PlayerState.ContentId == character.ContentId
            ? character.ContentId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }

    private bool MatchesPreparedSession(Configuration config, bool allowMissingCharacter = false)
        => ReferenceEquals(preparedConfig, config) && preparedProvider == config.CombatProvider
            && preparedAccount == configManager.CurrentAccountId
            && (preparedCharacter == ReadCharacterIdentity()
                || allowMissingCharacter && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.ContentId == 0);

    internal void ObserveSessionDeparture(bool dutyTransition = false)
    {
        if (preparedConfig is null) return;
        // The engine owns duty-exit cleanup. Native player and follow state can
        // disappear before that cleanup runs, while the same provider remains loaded.
        var combatTransition = gmcSession && dutyTransition && (rotationEnableSentForDuty || deathRearmPending);
        var sessionMatches = MatchesPreparedSession(configManager.GetActiveConfig(), allowMissingCharacter: combatTransition);
        var providerDeparted = sessionMatches && (!bossModIPC.IsOwnedProviderCurrent
            || gmcSession && !bossModIPC.IsNativeAutorotationAvailable);
        if (!sessionMatches || providerDeparted)
        {
            OwnedCombatProviderDeparted = providerDeparted && preparedBossMod is not null
                && (rotationEnableSentForDuty || deathRearmPending) && !rotationDisableSentForDuty;
            DisableRotationForDutyEnd("account, character, provider or profile departure");
            preparedConfig = null;
            sessionEnded = true;
            Fail(Ui.M("Rotation_BossModSessionChanged"));
        }
        else if (!combatTransition && (rotationEnableSentForDuty || deathRearmPending) && !rotationDisableSentForDuty)
        {
            bossModIPC.ObserveOwnedCombatState();
            if (gmcSession && rotationEnableSentForDuty && !bossModIPC.UpdateGmcCombatState(!dutyState.IsPartyLeader))
            {
                DisableRotationForDutyEnd("GMC combat ownership or native state changed");
                Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            }
        }
    }

    internal void EndSessionForLogout()
    {
        if (preparedConfig is null) return;
        DisableRotationForDutyEnd("native logout transition", restoreAi: false);
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

    private string? GetGmcManualPreset((bool Manual, string Name)? settings)
        => bossModIPC.NativeHost == "BossModReborn" && preparedProvider is (CombatProvider.Bmr or CombatProvider.Vbm)
            && settings is { Manual: true } manual ? manual.Name : null;

    internal bool ValidateManualPresetSelection(Configuration config)
        => config.CombatProvider is not (CombatProvider.Bmr or CombatProvider.Vbm) || !config.UseManualBossModPreset
            || (!string.IsNullOrWhiteSpace(config.ManualBossModPresetName)
                && bossModIPC.IsExactRuntimePreset(config.ManualBossModPresetName));

    internal bool ApplyPendingCombatSettings()
    {
        if (!settingsPending || !rotationEnableSentForDuty || rotationDisableSentForDuty)
            return true;
        ObserveSessionDeparture();
        if (rotationDisableSentForDuty || preparedConfig is null)
            return false;
        var config = configManager.GetActiveConfig();
        var selected = CurrentPresetSettings(config);
        if (gmcSession)
        {
            settingsPending = false;
            var manual = GetGmcManualPreset(selected);
            var previousManual = GetGmcManualPreset(appliedPresetSettings);
            if (manual == previousManual) return true;
            if (!bossModIPC.UpdateGmcAutorotation(manual))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            appliedPresetSettings = selected;
            Failure = string.Empty;
            return true;
        }
        if (appliedPresetSettings == selected || config.CombatProvider is CombatProvider.Rsr or CombatProvider.Wrath)
        {
            appliedPresetSettings = selected;
            settingsPending = false;
            return true;
        }
        if (IsVbmActivationDeferred)
            return false;
        if (!ValidateManualPresetSelection(config) || preparedBossMod is null
            || !bossModIPC.PreparePresetForStart(preparedBossMod.Value, passive: false, config.UseManualBossModPreset, config.ManualBossModPresetName))
        {
            if (IsVbmActivationDeferred)
                return false;
            settingsPending = false;
            log.Warning($"[MOGTOME][Rotation] Live preset application was unconfirmed: {bossModIPC.LastSettingsStatus}");
            return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
        }
        appliedPresetSettings = CurrentPresetSettings(config);
        settingsPending = false;
        Failure = string.Empty;
        return true;
    }

    public bool Initialize(bool preferBmr = false, string? nativeHost = null)
    {
        if (!DisableEnabledComponents())
        {
            if (!bossModIPC.RetireDepartedOwnedBossModSettings(nativeHost, out var previousHostUnloaded))
                return Fail(Ui.M("Rotation_CouldNotDisableCombatComponentsFromThe"));
            preparedConfig = null;
            sessionEnded = true;
            rotationDisableSentForDuty = true;
            if (!previousHostUnloaded || enabledComponents.Count != 0)
                return Fail(Ui.M("Rotation_CouldNotDisableCombatComponentsFromThe"));
        }
        ResetDutyRotationState("engine start");
        preparedBossMod = null;
        preparedConfig = null;
        sessionEnded = false;
        OwnedCombatProviderDeparted = false;
        rsrHealingRestored = false;
        appliedPresetSettings = null;
        settingsPending = false;

        var config = configManager.GetActiveConfig();
        var host = nativeHost ?? bossModIPC.GetAutomationHost();
        gmcSession = host == "BossModReborn" && bossModIPC.IsGmcReady
            || host == "GodMod" && config.CombatProvider is (CombatProvider.Bmr or CombatProvider.Vbm)
                && !config.UseManualBossModPreset;
        var provider = gmcSession ? CombatProvider.Bmr : config.CombatProvider;
        if (provider != CombatProvider.Wrath)
        {
            if (!bossModIPC.ResolveReadyHost(provider, out var resolved, nativeHost)
                || gmcSession && resolved != CombatProvider.Bmr)
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            preparedBossMod = resolved;

            var character = ReadCharacterIdentity();
            if (string.IsNullOrEmpty(configManager.CurrentAccountId) || string.IsNullOrEmpty(character)
                || !bossModIPC.BeginOwnedBossModSettings(preparedBossMod.Value, configManager.CurrentAccountId, character, config))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
        }

        preparedConfig = config;
        preparedProvider = config.CombatProvider;
        preparedAccount = configManager.CurrentAccountId;
        preparedCharacter = ReadCharacterIdentity();

        Failure = string.Empty;
        log.Information($"[MOGTOME][Rotation] Initialized selected combat provider: {provider}; NativeAutorotation={gmcSession}");
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
        deathRearmPending = false;
        ResetRsrHealthState();
        rsrMapTransitionPending = false;
        log.Debug($"[MOGTOME][Rotation] Reset duty rotation lifecycle state ({reason})");
    }

    internal void InvalidateCombatActivation()
    {
        if (rotationDisableSentForDuty)
            return;

        if (rotationEnableSentForDuty)
        {
            bossModIPC.ObserveOwnedCombatDeath();
            deathRearmPending = true;
        }
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
        if (IsVbmActivationDeferred)
            return false;
        if (preparedConfig is not null && !MatchesPreparedSession(configManager.GetActiveConfig()))
        {
            ObserveSessionDeparture();
            return Fail(Ui.M("Rotation_BossModSessionChanged"));
        }

        var provider = gmcSession ? CombatProvider.Bmr : configManager.GetActiveConfig().CombatProvider;
        var recoveringDeath = deathRearmPending;
        deathRearmPending = false;
        try
        {
            var selected = CurrentPresetSettings(configManager.GetActiveConfig());
            var settingsMatch = gmcSession
                ? GetGmcManualPreset(appliedPresetSettings) == GetGmcManualPreset(selected)
                : appliedPresetSettings is null || appliedPresetSettings == selected;
            var deathRecovery = bossModIPC.RecoverOwnedCombatDeath(settingsMatch);
            if (deathRecovery is null)
            {
                deathRearmPending = recoveringDeath;
                return false;
            }
            if (deathRecovery == false || !EnableSelectedProvider() || rotationDisableSentForDuty)
            {
                if (!rotationDisableSentForDuty && IsCombatActivationDeferred)
                {
                    deathRearmPending = recoveringDeath;
                    return false;
                }
                if (recoveringDeath) DisableRotationForDutyEnd("death recovery ownership or native state changed");
                else DisableEnabledComponents();
                return false;
            }
        }
        catch (Exception ex)
        {
            if (recoveringDeath) DisableRotationForDutyEnd("death recovery activation failed");
            else DisableEnabledComponents();
            return Fail(Ui.M("Rotation_CombatActivationFailed", ex.Message));
        }
        rotationEnableSentForDuty = true;
        rotationDisableSentForDuty = false;
        bossModIPC.ObserveOwnedCombatState();
        // Full activation also restored RSR; suppress its independent Off probe
        // while the provider applies that command.
        rsrRecoveryCommandSuppressedUntilUtc = DateTime.UtcNow + RsrRecoveryCommandSuppression;
        Failure = string.Empty;
        log.Information($"[MOGTOME][Rotation] enabled selected combat provider once per duty: {provider} ({reason})");
        return true;
    }

    private bool rsrMapTransitionPending;
    private uint rsrMapTransitionTerritory;

    internal void ObserveRsrMapTransition(bool betweenAreas, bool inDuty, bool playerAlive, uint territoryId)
    {
        if (betweenAreas)
        {
            if (!rsrMapTransitionPending && rotationEnableSentForDuty && !rotationDisableSentForDuty &&
                !sessionEnded && !gmcSession && inDuty && configManager.GetActiveConfig().CombatProvider == CombatProvider.Rsr)
            {
                rsrMapTransitionPending = true;
                rsrMapTransitionTerritory = territoryId;
            }
            return;
        }

        if (!rsrMapTransitionPending) return;
        rsrMapTransitionPending = false;
        if (!inDuty || !playerAlive || territoryId != rsrMapTransitionTerritory ||
            !rotationEnableSentForDuty || rotationDisableSentForDuty || sessionEnded ||
            configManager.GetActiveConfig().CombatProvider != CombatProvider.Rsr)
            return;

        ReassertRsrCombat("internal map transition");
        rsrRecoveryCommandSuppressedUntilUtc = DateTime.UtcNow + RsrRecoveryCommandSuppression;
    }

    public bool DisableRotationForDutyEnd(string reason, bool restoreAi = true, bool keepSelectedHost = false)
    {
        // Terminal even when cleanup fails. A later Stop may retry cleanup,
        // but pending activation can never reopen this duty.
        rotationDisableSentForDuty = true;
        if (!DisableEnabledComponents(restoreAi, keepSelectedHost))
            return Fail(Ui.M("Rotation_CouldNotDisableAllMogTomeCombatComponents", reason));
        log.Information($"[MOGTOME][Rotation] MogTome combat components are disabled ({reason})");
        return true;
    }

    public void UpdateDutyRotationHealth(uint territoryId, bool backendStarted, bool dutyCompleted, bool localPlayerDead, string reason)
    {
        if (DutyState.IsMogtomeDutyTerritory(territoryId) && backendStarted && !dutyCompleted && !localPlayerDead)
            ApplyPendingCombatSettings();
        var provider = configManager.GetActiveConfig().CombatProvider;
        if (gmcSession && rotationEnableSentForDuty && !rotationDisableSentForDuty && !sessionEnded
            && DutyState.IsMogtomeDutyTerritory(territoryId) && backendStarted && !dutyCompleted && !localPlayerDead)
        {
            ObserveSessionDeparture();
            var manual = GetGmcManualPreset(appliedPresetSettings);
            if (!rotationDisableSentForDuty && !bossModIPC.UpdateGmcAutorotation(manual))
            {
                DisableRotationForDutyEnd("GMC combat ownership or native state changed");
                Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            }
            else if (!rotationDisableSentForDuty)
                bossModIPC.ObserveOwnedCombatState();
        }
        if (gmcSession || provider != CombatProvider.Rsr ||
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

        ReassertRsrCombat("periodic duty recovery");
        rsrRecoveryCommandSuppressedUntilUtc = now + RsrRecoveryCommandSuppression;
    }

    private bool DisableEnabledComponents(bool restoreAi = true, bool keepSelectedHost = false)
    {
        var account = configManager.CurrentAccountId;
        var character = ReadCharacterIdentity();
        var sameIdentity = preparedConfig is null || preparedAccount == account && preparedCharacter == character;
        var restored = bossModIPC.BeginOwnedBossModCleanup(account, character,
            restoreAi && preparedConfig is not null && MatchesPreparedSession(configManager.GetActiveConfig())
                && (!gmcSession || bossModIPC.IsNativeAutorotationAvailable));
        settingsPending = false;
        appliedPresetSettings = null;
        if (!sameIdentity) enabledComponents.Clear();
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
        try { presetCleared = bossModIPC.ClearActivePreset(releaseSession: enabledComponents.Count == 0, keepSelectedHost: keepSelectedHost); }
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
        if (gmcSession)
        {
            if (!bossModIPC.IsNativeAutorotationAvailable || preparedBossMod != CombatProvider.Bmr
                || !bossModIPC.IsOwnedProviderCurrent
                || !bossModIPC.BeginOwnedBossModSettings(CombatProvider.Bmr, configManager.CurrentAccountId, ReadCharacterIdentity(), config))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            var selected = CurrentPresetSettings(config);
            if (!bossModIPC.BeginGmcAutorotation(GetGmcManualPreset(selected), !dutyState.IsPartyLeader))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            appliedPresetSettings = selected;
            return true;
        }
        if (provider != CombatProvider.Wrath)
        {
            if (preparedBossMod is null || !bossModIPC.IsOwnedProviderCurrent)
                return Fail(Ui.M("Rotation_BossModAvailabilityOrSelectionChangedAfterStartup"));

            var character = ReadCharacterIdentity();
            if (string.IsNullOrEmpty(character) || !bossModIPC.BeginOwnedBossModSettings(preparedBossMod.Value,
                    configManager.CurrentAccountId, character, config))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));

            if (!ValidateManualPresetSelection(config) || !bossModIPC.PreparePresetForStart(preparedBossMod.Value, provider == CombatProvider.Rsr,
                    config.UseManualBossModPreset, config.ManualBossModPresetName))
            {
                if (IsVbmActivationDeferred)
                    return false;
                return Fail(Ui.M("Rotation_PresetPreparationFailedCombatWasNotEnabled", preparedBossMod));
            }
        }

        if (rotationDisableSentForDuty) return false;
        if (!rsrHealingRestored)
        {
            rsrHealingRestored = true;
            bossModIPC.RestoreRsrHealing();
        }

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
            if (!bossModIPC.ApplyOwnedAutomaticTargeting(command => bossModIPC.SendCommand(command, "enable BMR automatic targeting")))
                return Fail(Ui.M("Rotation_BossModSettingsUnconfirmed"));
            if (rotationDisableSentForDuty) return false;
            if (!bossModIPC.ApplyOwnedPreferredDistance(command => bossModIPC.SendCommand(command, "set BMR dodge clearance"), 1.5))
                log.Warning($"[MOGTOME][Rotation] Preferred distance retained: {bossModIPC.LastSettingsStatus}");
            if (rotationDisableSentForDuty) return false;
            bossModIPC.SendCommand("/bmrai forbidactions off", "allow BMR actions");
            if (rotationDisableSentForDuty) return false;
        }
        enabledComponents.Add(aiProvider);
        if (command.Length == 0 || !bossModIPC.SendCommand(command, $"enable {aiProvider}"))
        {
            if (IsVbmActivationDeferred)
                return false;
            return Fail(Ui.M("Rotation_CouldNotEnableUsing", aiProvider, command));
        }
        appliedPresetSettings = CurrentPresetSettings(config);
        settingsPending = false;
        return !rotationDisableSentForDuty;
    }

    private void ReassertRsrCombat(string reason)
    {
        // RSR and the owned BossMod AI are independent setters. A failed ON request
        // must never stop the other component or tear down the prepared preset.
        if (rotationDisableSentForDuty || sessionEnded ||
            configManager.GetActiveConfig().CombatProvider != CombatProvider.Rsr ||
            preparedConfig is null || !MatchesPreparedSession(configManager.GetActiveConfig()))
            return;

        var rsrEnabled = EnableRsr();
        var command = preparedBossMod switch
        {
            CombatProvider.Bmr => "/bmrai on",
            CombatProvider.Vbm => "/vbmai on",
            _ => null,
        };
        var aiEnabled = command is not null && bossModIPC.IsOwnedProviderCurrent &&
            (preparedBossMod != CombatProvider.Bmr || bossModIPC.ApplyOwnedAutomaticTargeting(
                targetingCommand => bossModIPC.SendCommand(targetingCommand, "reassert BMR automatic targeting"))) &&
            bossModIPC.SendCommand(command, $"reassert BossMod AI ({reason})");
        if (!rsrEnabled || !aiEnabled)
            log.Warning($"[MOGTOME][Rotation] Combat reassertion incomplete ({reason}): RSR={rsrEnabled}, BossModAI={aiEnabled}");
        else
            log.Information($"[MOGTOME][Rotation] Reissued RSR Auto and BossMod AI on ({reason})");
    }

    private bool EnableRsr()
    {
        if (rotationDisableSentForDuty) return false;
        enabledComponents.Add(CombatProvider.Rsr);
        var accepted = bossModIPC.TrySetRsrAutoViaIpc();
        return accepted && !rotationDisableSentForDuty;
    }

    private bool Fail(UiText reason)
    {
        Failure = reason;
        log.Error($"[MOGTOME][Rotation] {reason}");
        return false;
    }
}
