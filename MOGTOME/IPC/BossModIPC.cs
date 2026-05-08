using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MOGTOME.Services;

namespace MOGTOME.IPC;

public class BossModIPC : IDisposable
{
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

    public string WhichBossMod { get; private set; } = "vbm";
    public bool HasRotationSolver { get; private set; }

    public BossModIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commandManager = commandManager;
    }

    public void PreparePassivePresetForStart()
    {
        try
        {
            var createdCount = InstallPassivePresets(forceRecreate: true);
            var presetName = SelectPassivePresetForCurrentJob();

            SetActivePresetViaIpc(presetName);
            SendPassivePresetCommands(presetName);

            log.Information($"[MOGTOME][BossMod] Passive preset startup prep complete: preset='{presetName}', installed={createdCount}/{PassivePresetNames.Length}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Passive preset startup prep failed; continuing start: {ex.Message}");
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

    private void SendPassivePresetCommands(string presetName)
    {
        SendPassivePresetCommand($"/bmrai setpresetname {presetName}");
        SendPassivePresetCommand($"/vbm ar set {presetName}");
    }

    private void SendPassivePresetCommand(string command)
    {
        try
        {
            log.Information($"[MOGTOME][BossMod] Sending passive preset command: {command}");
            GameHelpers.SendCommand(command);
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][BossMod] Passive preset command failed ({command}); continuing start: {ex.Message}");
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

    public void DetectBossMod()
    {
        try
        {
            var installed = pluginInterface.InstalledPlugins;
            WhichBossMod = "vbm";
            HasRotationSolver = false;
            foreach (var plugin in installed)
            {
                if (plugin.IsLoaded &&
                    (plugin.InternalName == "RotationSolver" || plugin.InternalName == "RotationSolverReborn"))
                {
                    HasRotationSolver = true;
                }

                if (plugin.IsLoaded && plugin.InternalName == "BossModReborn")
                {
                    WhichBossMod = "bmr";
                }
            }

            if (WhichBossMod == "bmr")
                log.Information("[MOGTOME][BossMod] Detected BossModReborn (bmr)");
            else
                log.Information("[MOGTOME][BossMod] Using default VBM");
        }
        catch (Exception ex)
        {
            WhichBossMod = "vbm";
            HasRotationSolver = false;
            log.Warning($"[MOGTOME][BossMod] Detection failed, defaulting to vbm: {ex.Message}");
        }
    }

    public void SetPreset(string presetName)
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai preset {presetName}";
            log.Information($"[MOGTOME][BossMod] Setting preset: {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] SetPreset failed: {ex.Message}");
        }
    }

    public void EnableAI()
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai on";
            log.Debug($"[MOGTOME][BossMod] {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] EnableAI failed: {ex.Message}");
        }
    }

    public void DisableAI()
    {
        try
        {
            var cmd = $"/{WhichBossMod}ai off";
            log.Debug($"[MOGTOME][BossMod] {cmd}");
            commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] DisableAI failed: {ex.Message}");
        }
    }

    public void ForceRotation(string preset)
    {
        if (preset == "none")
        {
            // Using RSR instead of BossMod
            EnableRSR();
        }
        else
        {
            // Keep RSR enabled, don't disable it
            // DisableRSR(); // REMOVED - never disable RSR
            SetPreset(preset);
            EnableAI();
        }
    }

    public void EnableRSR()
    {
        try
        {
            commandManager.ProcessCommand("/rotation auto");
            //log.Debug("[MOGTOME][BossMod] RSR auto rotation enabled");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] EnableRSR failed: {ex.Message}");
        }
    }

    public void DisableRSR()
    {
        try
        {
            commandManager.ProcessCommand("/rotation cancel");
            log.Debug("[MOGTOME][BossMod] RSR rotation cancelled");
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] DisableRSR failed (may not be installed): {ex.Message}");
        }
    }

    public void DisableKeyboardNoise()
    {
        if (!HasRotationSolver)
            return;

        try
        {
            const string cmd = "/rotation Settings KeyBoardNoise false";
            //const string cmd2 = "/bmrai setpresetname AutoDuty Passive";
            //const string cmd3 = "/vbm ar set AutoDuty Passive";
            const string cmd4 = "/rotation Settings AutoOffBetweenArea False";
            const string cmd5 = "/rotation Settings AutoOffCutScene False";
            const string cmd6 = "/rotation Settings AutoOffSwitchClass False";
            const string cmd7 = "/rotation Settings AutoOffWhenDead False";
            const string cmd8 = "/rotation Settings AutoOffWhenDutyCompleted False";
            const string cmd9 = "/rotation Settings AutoOffAfterCombatTime 6942069";
            const string cmd10 = "/rotation Settings ToggleAuto False";
            const string cmd11 = "/rotation Settings ToggleManual False";
            const string cmd12 = "/rotation Auto";
            const string cmd13 = "/fr off";
            GameHelpers.SendCommand(cmd);
            //GameHelpers.SendCommand(cmd2);
            //GameHelpers.SendCommand(cmd3);
            GameHelpers.SendCommand(cmd4);
            GameHelpers.SendCommand(cmd5);
            GameHelpers.SendCommand(cmd6);
            GameHelpers.SendCommand(cmd7);
            GameHelpers.SendCommand(cmd8);
            GameHelpers.SendCommand(cmd9);
            GameHelpers.SendCommand(cmd10);
            GameHelpers.SendCommand(cmd11);
            GameHelpers.SendCommand(cmd12);
            GameHelpers.SendCommand(cmd13);
            log.Information("[MOGTOME][MOGTOME][BossMod][RotationSolverReborn] Requested RSR and both bossmod shenanigans to calm down");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] DisableKeyboardNoise failed: {ex.Message}");
        }
    }

    public void Dispose() { }
}
