using System;
using System.Collections.Generic;
using System.IO;
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

    private static readonly string[] PassivePresetNames =
    [
        PassiveTankPreset,
        PassiveMeleePreset,
        PassiveRangedPreset,
    ];

    private static readonly HashSet<uint> TankJobRows = [1, 3, 19, 21, 32, 37];
    private static readonly HashSet<uint> MeleeJobRows = [2, 4, 20, 22, 29, 30, 34, 39, 41];
    private static readonly HashSet<uint> RangedJobRows = [5, 6, 7, 23, 24, 25, 26, 27, 28, 31, 33, 35, 36, 38, 40, 42];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private static readonly BindingFlags RsrStaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public BossModIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commandManager = commandManager;
    }

    public void PreparePresetForStart(CombatProvider provider, bool useManualPreset, string manualPresetName)
    {
        try
        {
            var manualPresetSelected = useManualPreset && !string.IsNullOrWhiteSpace(manualPresetName);
            var presetName = manualPresetSelected
                ? manualPresetName.Trim()
                : SelectPassivePresetForCurrentJob();

            if (!manualPresetSelected)
                InstallPassivePresets(forceRecreate: true);

            SetActivePresetViaIpc(presetName);
            SendProviderPresetCommand(provider, presetName);
            log.Information($"[MOGTOME][BossMod] Prepared {(manualPresetSelected ? "manual" : "role-based passive")} preset '{presetName}' for {provider}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Preset startup prep failed; continuing start: {ex.Message}");
        }
    }

    private void SendProviderPresetCommand(CombatProvider provider, string presetName)
    {
        switch (provider)
        {
            case CombatProvider.Bmr:
                SendCommand($"/bmrai setpresetname {presetName}", "set BMR preset");
                break;
            case CombatProvider.Vbm:
                SendCommand($"/vbm ar set {presetName}", "set VBM preset");
                break;
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

    public void SendCommand(string command, string purpose)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
                log.Debug($"[MOGTOME][Rotation] {purpose}: {command}");
            else
                log.Warning($"[MOGTOME][Rotation] Command was not handled while attempting to {purpose}: {command}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Rotation] Failed to {purpose} with {command}: {ex.Message}");
        }
    }

    private int InstallPassivePresets(bool forceRecreate)
    {
        var createdCount = 0;

        foreach (var presetName in PassivePresetNames)
        {
            var json = ReadPassivePresetJson(presetName);
            if (json == null)
                continue;

            if (TryCreatePreset(presetName, json, forceRecreate))
            {
                createdCount++;
                continue;
            }

            log.Warning($"[MOGTOME][BossMod] Failed to install passive preset '{presetName}' via BossMod-compatible IPC");
        }

        if (createdCount == 0)
            log.Warning("[MOGTOME][BossMod] No passive presets were installed; startup will continue");
        else
            log.Information($"[MOGTOME][BossMod] Installed {createdCount}/{PassivePresetNames.Length} passive presets");

        return createdCount;
    }

    private string? ReadPassivePresetJson(string presetName)
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
                log.Warning($"[MOGTOME][BossMod] Passive preset file missing: {path}");
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Failed to read passive preset '{presetName}': {ex.Message}");
            return null;
        }
    }

    private string SelectPassivePresetForCurrentJob()
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                log.Warning($"[MOGTOME][BossMod] LocalPlayer is null while choosing passive preset; defaulting to '{PassiveRangedPreset}'");
                return PassiveRangedPreset;
            }

            if (!player.ClassJob.IsValid)
            {
                log.Warning($"[MOGTOME][BossMod] LocalPlayer ClassJob is invalid while choosing passive preset; defaulting to '{PassiveRangedPreset}'");
                return PassiveRangedPreset;
            }

            var job = player.ClassJob.Value;
            var rowId = job.RowId;
            var abbreviation = job.Abbreviation.ToString();

            if (TankJobRows.Contains(rowId))
            {
                log.Information($"[MOGTOME][BossMod] Selected passive preset '{PassiveTankPreset}' for job {abbreviation} ({rowId})");
                return PassiveTankPreset;
            }

            if (MeleeJobRows.Contains(rowId))
            {
                log.Information($"[MOGTOME][BossMod] Selected passive preset '{PassiveMeleePreset}' for job {abbreviation} ({rowId})");
                return PassiveMeleePreset;
            }

            if (RangedJobRows.Contains(rowId))
            {
                log.Information($"[MOGTOME][BossMod] Selected passive preset '{PassiveRangedPreset}' for job {abbreviation} ({rowId})");
                return PassiveRangedPreset;
            }

            log.Warning($"[MOGTOME][BossMod] Unknown job {abbreviation} ({rowId}) while choosing passive preset; defaulting to '{PassiveRangedPreset}'");
            return PassiveRangedPreset;
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Failed to choose passive preset; defaulting to '{PassiveRangedPreset}': {ex.Message}");
            return PassiveRangedPreset;
        }
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
            return true;
        }

        legacyResult = TryStringIpc("BossModReborn.Presets.Create", json);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossModReborn.Presets.Create", name, legacyResult);
            return true;
        }

        return false;
    }

    private bool SetActivePresetViaIpc(string presetName)
    {
        var handled = false;

        var result = TryBoolIpc("BossMod.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossMod IPC");
                handled = true;
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossMod.Presets.SetActive returned false for preset '{presetName}'");
        }

        result = TryBoolIpc("BossModReborn.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossModReborn IPC");
                handled = true;
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossModReborn.Presets.SetActive returned false for preset '{presetName}'");
        }

        var legacyResult = TryStringIpc("BossMod.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossMod.Presets.ForceSet", presetName, legacyResult);
            handled = true;
        }

        legacyResult = TryStringIpc("BossModReborn.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossModReborn.Presets.ForceSet", presetName, legacyResult);
            handled = true;
        }

        if (!handled)
            log.Warning($"[MOGTOME][BossMod] No BossMod-compatible preset IPC responded while setting preset '{presetName}' active");

        return handled;
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

                var wrapperType = installedPlugin.GetType().Name == "LocalDevPlugin"
                    ? installedPlugin.GetType().BaseType
                    : installedPlugin.GetType();

                var instanceField = wrapperType?.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                var instance = instanceField?.GetValue(installedPlugin);
                if (instance == null)
                    detail = $"Dalamud plugin {internalName} was installed but its live instance was unavailable";
                return instance;
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
