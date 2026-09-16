using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.IPC;

internal enum RsrOperatingMode
{
    Unknown,
    Off,
    Auto,
    TargetOnly,
    Manual,
    AutoDuty,
    Henched,
    PvP,
    Active,
}

public class BossModIPC : IDisposable
{
    private enum RsrOtherCommandType : byte { Settings }

    private enum RsrStateCommandType : byte
    {
        Off,
        Auto,
        TargetOnly,
        Manual,
        AutoDuty,
        Henched,
        PvP,
    }

    private const string PassiveTankPreset = "passive - tank";
    private const string PassiveMeleePreset = "passive - melee";
    private const string PassiveRangedPreset = "passive - ranged";

    private const string ActiveTankPreset = "FRENRIDER - TANK";
    private const string ActiveMeleePreset = "FRENRIDER - MELEE";
    private const string ActiveRangedPreset = "FRENRIDER - RANGED";

    private static readonly string[] PackagedPresetNames =
    [
        PassiveTankPreset,
        PassiveMeleePreset,
        PassiveRangedPreset,
        ActiveTankPreset,
        ActiveMeleePreset,
        ActiveRangedPreset,
    ];

    private static readonly HashSet<uint> TankJobRows = [1, 3, 19, 21, 32, 37];
    private static readonly HashSet<uint> MeleeJobRows = [2, 4, 20, 22, 29, 30, 34, 39, 41, 43];
    private static readonly HashSet<uint> RangedJobRows = [5, 6, 7, 23, 24, 25, 26, 27, 28, 31, 33, 35, 36, 38, 40, 42];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private bool presetActivatedByMogtome;
    private static readonly BindingFlags RsrStaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public BossModIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commandManager = commandManager;
    }

    public bool IsPluginLoaded(string internalName)
        => pluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == internalName);

    internal bool RefreshPackagedPresets()
    {
        try
        {
            return InstallPackagedPresets(forceRecreate: true) == PackagedPresetNames.Length;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Packaged preset refresh failed: {ex.Message}");
            return false;
        }
    }

    public bool PreparePresetForStart(CombatProvider provider, bool passive, bool useManualPreset, string manualPresetName)
    {
        try
        {
            var manualPresetSelected = !passive && useManualPreset;
            if (manualPresetSelected && string.IsNullOrWhiteSpace(manualPresetName))
            {
                log.Error("[MOGTOME][BossMod] Manual preset is enabled but its name is empty");
                return false;
            }
            var presetName = manualPresetSelected
                ? manualPresetName.Trim()
                : SelectPresetForCurrentJob(passive);

            if (!SetActivePresetViaIpc(presetName))
            {
                log.Error($"[MOGTOME][BossMod] Could not prepare preset '{presetName}' for {provider}");
                return false;
            }
            presetActivatedByMogtome = true;
            if (!SendProviderPresetCommand(provider, presetName))
                return false;
            log.Information($"[MOGTOME][BossMod] Prepared {(manualPresetSelected ? "manual" : passive ? "role-based passive" : "role-based active")} preset '{presetName}' for {provider}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Preset startup prep failed: {ex.Message}");
            return false;
        }
    }

    public bool ClearActivePreset()
    {
        if (!presetActivatedByMogtome)
            return true;
        try
        {
            // False means it was already clear (for example, BMR cleared it while stopping AI).
            pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc();
            presetActivatedByMogtome = false;
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Could not clear the preset activated by MogTome: {ex.Message}");
            return false;
        }
    }

    private bool SendProviderPresetCommand(CombatProvider provider, string presetName)
    {
        switch (provider)
        {
            case CombatProvider.Bmr:
                return SendCommand($"/bmrai setpresetname {presetName}", "set BMR preset");
            case CombatProvider.Vbm:
                return SendCommand($"/vbm ar set {presetName}", "set VBM preset");
            default:
                return false;
        }
    }

    internal void RestoreRsrHealing()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<RsrOtherCommandType, string, object>("RotationSolverReborn.OtherCommand");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "AutoHeal true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "UseGroundBeneficialAbility true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealWhenNothingTodo true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaAbilityHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaSpellHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaAbility 0.90");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaSpell 0.80");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleAbilityHot 0.80");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleSpellHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleAbility 0.85");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleSpell 0.80");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Rotation] RSR healing settings dispatch failed; continuing startup: {ex.Message}");
        }
    }

    public bool TrySetRsrAutoViaIpc()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<RsrStateCommandType, object>("RotationSolverReborn.ChangeOperatingMode");
            subscriber.InvokeAction(RsrStateCommandType.Auto);
            log.Debug("[MOGTOME][Rotation] RSR Auto mode set via IPC");
            return true;
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][Rotation] RSR mode IPC unavailable; command fallback will be used: {ex.Message}");
            return false;
        }
    }

    internal bool TryGetRsrOperatingMode(out RsrOperatingMode mode, out string detail)
    {
        mode = RsrOperatingMode.Unknown;
        detail = string.Empty;

        try
        {
            if (!TryFindRsrPluginInstance(out var rsrPlugin, out var internalName, out detail) || rsrPlugin == null)
                return false;

            var dataCenterType = ResolveRsrDataCenterType(rsrPlugin.GetType().Assembly);
            if (dataCenterType == null)
            {
                detail = $"RSR DataCenter type was not found from {internalName}";
                return false;
            }

            if (!TryReadStaticBool(dataCenterType, "State", out var state, out var stateDetail))
            {
                detail = $"RSR DataCenter.State unavailable from {internalName}: {stateDetail}";
                return false;
            }

            if (!state)
            {
                mode = RsrOperatingMode.Off;
                detail = $"RSR {internalName} DataCenter.State=false";
                return true;
            }

            var readAnyModeFlag = false;
            if (TryReadStaticBool(dataCenterType, "IsAutoDuty", out var isAutoDuty, out _))
            {
                readAnyModeFlag = true;
                if (isAutoDuty)
                {
                    mode = RsrOperatingMode.AutoDuty;
                    detail = $"RSR {internalName} DataCenter.State=true, IsAutoDuty=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsHenched", out var isHenched, out _))
            {
                readAnyModeFlag = true;
                if (isHenched)
                {
                    mode = RsrOperatingMode.Henched;
                    detail = $"RSR {internalName} DataCenter.State=true, IsHenched=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsPvPStateEnabled", out var isPvp, out _))
            {
                readAnyModeFlag = true;
                if (isPvp)
                {
                    mode = RsrOperatingMode.PvP;
                    detail = $"RSR {internalName} DataCenter.State=true, IsPvPStateEnabled=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsManual", out var isManual, out _))
            {
                readAnyModeFlag = true;
                if (isManual)
                {
                    mode = RsrOperatingMode.Manual;
                    detail = $"RSR {internalName} DataCenter.State=true, IsManual=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsTargetOnly", out var isTargetOnly, out _))
            {
                readAnyModeFlag = true;
                if (isTargetOnly)
                {
                    mode = RsrOperatingMode.TargetOnly;
                    detail = $"RSR {internalName} DataCenter.State=true, IsTargetOnly=true";
                    return true;
                }
            }

            mode = readAnyModeFlag ? RsrOperatingMode.Auto : RsrOperatingMode.Active;
            detail = readAnyModeFlag
                ? $"RSR {internalName} DataCenter.State=true, mode flags clear"
                : $"RSR {internalName} DataCenter.State=true, mode flags unavailable";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"RSR operating mode probe failed: {ex.Message}";
            return false;
        }
    }

    public bool SendCommand(string command, string purpose)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
            {
                log.Debug($"[MOGTOME][Rotation] {purpose}: {command}");
                return true;
            }
            else
                log.Warning($"[MOGTOME][Rotation] Command was not handled while attempting to {purpose}: {command}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Rotation] Failed to {purpose} with {command}: {ex.Message}");
        }
        return false;
    }

    private int InstallPackagedPresets(bool forceRecreate)
    {
        var createdCount = 0;

        foreach (var presetName in PackagedPresetNames)
        {
            var json = ReadPackagedPresetJson(presetName);
            if (json == null)
                continue;

            if (TryCreatePreset(presetName, json, forceRecreate))
            {
                createdCount++;
                continue;
            }

            log.Warning($"[MOGTOME][BossMod] Failed to install packaged preset '{presetName}' via BossMod-compatible IPC");
        }

        log.Information($"[MOGTOME][BossMod] Installed {createdCount}/{PackagedPresetNames.Length} packaged presets");

        return createdCount;
    }

    private string? ReadPackagedPresetJson(string presetName)
    {
        try
        {
            var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                log.Warning($"[MOGTOME][BossMod] Could not resolve plugin directory for preset '{presetName}'");
                return null;
            }

            var path = Path.Combine(assemblyDirectory, "data", "bm", $"{presetName}.json");
            if (!File.Exists(path))
            {
                log.Warning($"[MOGTOME][BossMod] Packaged preset file missing: {path}");
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Failed to read packaged preset '{presetName}': {ex.Message}");
            return null;
        }
    }

    private string SelectPresetForCurrentJob(bool passive)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                throw new InvalidOperationException("LocalPlayer is unavailable while choosing the combat preset");
            }

            if (!player.ClassJob.IsValid)
            {
                throw new InvalidOperationException("LocalPlayer ClassJob is invalid while choosing the combat preset");
            }

            var job = player.ClassJob.Value;
            var rowId = job.RowId;
            var abbreviation = job.Abbreviation.ToString();

            var preset = SelectPresetForJob(rowId, passive);
            log.Information($"[MOGTOME][BossMod] Selected preset '{preset}' for job {abbreviation} ({rowId})");
            return preset;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Failed to choose combat preset: {ex.Message}");
            throw;
        }
    }

    internal static string SelectPresetForJob(uint rowId, bool passive)
    {
        if (TankJobRows.Contains(rowId))
            return passive ? PassiveTankPreset : ActiveTankPreset;
        if (MeleeJobRows.Contains(rowId))
            return passive ? PassiveMeleePreset : ActiveMeleePreset;
        if (RangedJobRows.Contains(rowId))
            return passive ? PassiveRangedPreset : ActiveRangedPreset;
        throw new InvalidOperationException($"Unsupported combat job {rowId}");
    }

    private bool TryCreatePreset(string name, string json, bool forceRecreate)
    {
        string? previouslyActivePreset = null;

        if (forceRecreate)
        {
            var existingPreset = TryStringIpc("BossMod.Presets.Get", name);
            if (existingPreset != null)
            {
                previouslyActivePreset = TryStringIpc("BossMod.Presets.GetActive");

                var deleteResult = TryBoolIpc("BossMod.Presets.Delete", name);
                if (deleteResult.HasValue)
                {
                    if (deleteResult.Value)
                        log.Information($"[MOGTOME][BossMod] Preset '{name}' deleted before recreate via BossMod-compatible IPC");
                    else
                        log.Warning($"[MOGTOME][BossMod] BossMod.Presets.Delete returned false for preset '{name}' before recreate");
                }
            }
        }

        var result = TryBoolIpc("BossMod.Presets.Create", json, true);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{name}' created via BossMod-compatible IPC");

                if (!string.IsNullOrEmpty(previouslyActivePreset) &&
                    string.Equals(previouslyActivePreset, name, StringComparison.OrdinalIgnoreCase))
                {
                    var reactivateResult = TryBoolIpc("BossMod.Presets.SetActive", name);
                    if (reactivateResult == true)
                        log.Information($"[MOGTOME][BossMod] Preset '{name}' restored as active after recreate");
                    else if (reactivateResult == false)
                        log.Warning($"[MOGTOME][BossMod] BossMod.Presets.SetActive returned false while restoring preset '{name}' after recreate");
                }

                return true;
            }

            log.Warning($"[MOGTOME][BossMod] BossMod.Presets.Create returned false for preset '{name}'");
            return false;
        }

        var legacyResult = TryStringIpc("BossMod.Presets.Create", json);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossMod.Presets.Create", name, legacyResult);
            return legacyResult.Length == 0;
        }

        legacyResult = TryStringIpc("BossModReborn.Presets.Create", json);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossModReborn.Presets.Create", name, legacyResult);
            return legacyResult.Length == 0;
        }

        return false;
    }

    private bool SetActivePresetViaIpc(string presetName)
    {
        var result = TryBoolIpc("BossMod.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossMod IPC");
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossMod.Presets.SetActive returned false for preset '{presetName}'");
            return result.Value;
        }

        result = TryBoolIpc("BossModReborn.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossModReborn IPC");
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossModReborn.Presets.SetActive returned false for preset '{presetName}'");
            return result.Value;
        }

        var legacyResult = TryStringIpc("BossMod.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossMod.Presets.ForceSet", presetName, legacyResult);
            return legacyResult.Length == 0;
        }

        legacyResult = TryStringIpc("BossModReborn.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossModReborn.Presets.ForceSet", presetName, legacyResult);
            return legacyResult.Length == 0;
        }

        log.Warning($"[MOGTOME][BossMod] No BossMod-compatible preset IPC responded while setting preset '{presetName}' active");
        return false;
    }

    private bool TryFindRsrPluginInstance(out object? pluginInstance, out string internalName, out string detail)
    {
        foreach (var candidate in new[] { "RotationSolverReborn", "RotationSolver" })
        {
            pluginInstance = FindDalamudPluginInstance(candidate, out detail);
            if (pluginInstance != null)
            {
                internalName = candidate;
                return true;
            }
        }

        pluginInstance = null;
        internalName = string.Empty;
        detail = "RSR plugin instance was not found (checked RotationSolverReborn, RotationSolver)";
        return false;
    }

    private object? FindDalamudPluginInstance(string internalName, out string detail)
    {
        var installedPlugin = FindDalamudPlugin(internalName, out detail);
        if (installedPlugin == null)
            return null;

        var wrapperType = installedPlugin.GetType().Name == "LocalDevPlugin"
            ? installedPlugin.GetType().BaseType
            : installedPlugin.GetType();
        var instance = wrapperType?.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(installedPlugin);
        if (instance == null)
            detail = $"Dalamud plugin {internalName} was installed but its live instance was unavailable";
        return instance;
    }

    internal static object? FindDalamudPlugin(string internalName, out string detail)
    {
        detail = string.Empty;

        try
        {
            var serviceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");

            if (serviceType == null || pluginManagerType == null)
            {
                detail = "Dalamud PluginManager reflection types were unavailable";
                return null;
            }

            var pluginManager = serviceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);

            var installedPlugins = pluginManager?.GetType()
                .GetProperty("InstalledPlugins")
                ?.GetValue(pluginManager) as System.Collections.IList;

            if (installedPlugins == null)
            {
                detail = "Dalamud installed plugin list was unavailable";
                return null;
            }

            foreach (var installedPlugin in installedPlugins)
            {
                if (installedPlugin == null)
                    continue;

                var discoveredInternalName = installedPlugin.GetType()
                    .GetProperty("InternalName")
                    ?.GetValue(installedPlugin)
                    ?.ToString();

                if (!string.Equals(discoveredInternalName, internalName, StringComparison.Ordinal))
                    continue;

                return installedPlugin;
            }

            detail = $"Dalamud plugin {internalName} was not installed";
            return null;
        }
        catch (Exception ex)
        {
            detail = $"FindDalamudPluginInstance({internalName}) failed: {ex.Message}";
            return null;
        }
    }

    private static Type? ResolveRsrDataCenterType(Assembly pluginAssembly)
    {
        const string dataCenterTypeName = "RotationSolver.Basic.DataCenter";

        var directType = pluginAssembly.GetType(dataCenterTypeName, throwOnError: false);
        if (directType != null)
            return directType;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName == null ||
                (!assemblyName.StartsWith("RotationSolver", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(assemblyName, "RotationSolver.Basic", StringComparison.OrdinalIgnoreCase)))
                continue;

            var type = assembly.GetType(dataCenterTypeName, throwOnError: false);
            if (type != null)
                return type;
        }

        return null;
    }

    private static bool TryReadStaticBool(Type type, string propertyName, out bool value, out string detail)
    {
        value = false;
        detail = string.Empty;

        try
        {
            var property = type.GetProperty(propertyName, RsrStaticFlags);
            if (property == null)
            {
                detail = $"property {propertyName} not found";
                return false;
            }

            if (property.PropertyType != typeof(bool))
            {
                detail = $"property {propertyName} was {property.PropertyType.FullName}, not bool";
                return false;
            }

            value = (bool)(property.GetValue(null) ?? false);
            return true;
        }
        catch (Exception ex)
        {
            detail = $"property {propertyName} read failed: {ex.Message}";
            return false;
        }
    }

    private bool? TryBoolIpc<TArg>(string channel, TArg arg)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<TArg, bool>(channel);
            return subscriber.InvokeFunc(arg);
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private bool? TryBoolIpc<TArg1, TArg2>(string channel, TArg1 arg1, TArg2 arg2)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<TArg1, TArg2, bool>(channel);
            return subscriber.InvokeFunc(arg1, arg2);
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private string? TryStringIpc<TArg>(string channel, TArg arg)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<TArg, string>(channel);
            return subscriber.InvokeFunc(arg);
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private string? TryStringIpc(string channel)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<string>(channel);
            return subscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private void LogLegacyPresetResult(string channel, string presetName, string result)
    {
        if (result.Length == 0)
            log.Information($"[MOGTOME][BossMod] Preset '{presetName}' handled via legacy IPC channel {channel}");
        else
            log.Warning($"[MOGTOME][BossMod] Legacy IPC {channel} returned '{result}' for preset '{presetName}'");
    }

    public void Dispose() { }
}
