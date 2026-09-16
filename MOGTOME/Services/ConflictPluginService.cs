using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;

namespace MOGTOME.Services;

public sealed class ConflictPluginService
{
    private const string TwistOfFayteDisplayName = "Twist of Fayte";
    private const string TwistOfFayteShortName = "twistoffayte";
    private const string TwistOfFayteLegacyShortName = "twistofffayte";
    private const string TwistOfFayteDisableCommand = "/xldisableplugin TwistOfFayte";
    private const string AutoDutyDisplayName = "AutoDuty";
    private const string AutoDutyShortName = "autoduty";
    private const string AutoDutyDisableCommand = "/xldisableplugin AutoDuty";

    private static readonly TimeSpan DisableAttemptCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisableWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisablePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private readonly object stateLock = new();
    private readonly record struct PluginStatus(bool IsInstalled, bool IsLoaded, string InternalName, string DisplayName, string? LoadState = null);
    private readonly record struct PluginDisableResult(PluginStatus InitialStatus, PluginStatus FinalStatus, bool DisableAttempted);

    private readonly Dictionary<string, DateTime> lastDisableAttemptUtc = new(StringComparer.Ordinal);
    private UiText? pendingWarningMessage;

    public ConflictPluginService(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;
    }

    public (bool IsInstalled, bool IsLoaded) GetTwistOfFayteStatus()
    {
        var status = GetPluginStatus(MatchesTwistOfFayte);
        return (status.IsInstalled, status.IsLoaded);
    }

    public (bool IsInstalled, bool IsLoaded) GetAutoDutyStatus()
    {
        var status = GetPluginStatus(MatchesAutoDuty);
        return (status.IsInstalled, status.IsLoaded);
    }

    public async Task<(bool Ready, bool PreferBmr, UiText Reason)> EnsureBossModReadyAsync(Func<bool> isStarting)
    {
        var bmr = GetPluginStatus(MatchesBmr);
        var vbm = GetPluginStatus(MatchesVbm);
        if (!bmr.IsLoaded || !vbm.IsLoaded)
            return (true, false, string.Empty);

        if (!isStarting())
            return (false, false, Ui.M("Conflict_StartupWasStoppedBeforeBossModCleanup"));

        var disabledVbm = await EnsurePluginDisabledAsync("MOGTOME start", "VBM", "/xldisableplugin BossMod", MatchesVbm, isStarting).ConfigureAwait(false);
        if (!isStarting()) return (false, false, Ui.M("Conflict_StartupCancelledDuringVBMCleanup"));
        if (disabledVbm.FinalStatus.LoadState != "Unloaded")
            return (false, false, Ui.M("Conflict_VBMCouldNotBeDisabledWhileBoth"));

        // VBM disposal unregisters the shared BossMod IPC gates. Reload BMR once to restore them.
        var disabledBmr = await EnsurePluginDisabledAsync("BossMod IPC recovery", "BMR", "/xldisableplugintemp BossModReborn", MatchesBmr, isStarting).ConfigureAwait(false);
        if (!isStarting()) return (false, false, Ui.M("Conflict_StartupCancelledDuringBMRCleanup"));
        if (disabledBmr.FinalStatus.LoadState != "Unloaded")
            return (false, false, Ui.M("Conflict_BMRCouldNotBeTemporarilyDisabledTo"));

        if (!await GameHelpers.RunOnFrameworkThreadAsync(() => isStarting() && commandManager.ProcessCommand("/xlenableplugintemp BossModReborn")).ConfigureAwait(false))
            return (false, false, Ui.M("Conflict_TheNativeTemporaryBMREnableCommandWas"));

        var readyBmr = await WaitForPluginStateAsync(MatchesBmr, loaded: true, isStarting).ConfigureAwait(false);
        if (!isStarting()) return (false, false, Ui.M("Conflict_StartupCancelledDuringBMRReadiness"));
        if (readyBmr.LoadState != "Loaded" || GetPluginStatus(MatchesVbm).LoadState != "Unloaded")
            return (false, false, Ui.M("Conflict_BMRDidNotBecomeReadyAfterIts"));

        log.Information("[MOGTOME][Conflict] Disabled VBM and reloaded BMR to restore shared BossMod IPC");
        return (true, true, string.Empty);
    }

    private static bool MatchesBmr(string? internalName, string? displayName) => internalName == "BossModReborn";
    private static bool MatchesVbm(string? internalName, string? displayName) => internalName == "BossMod";

    public async Task DisableStartupAutomationAsync(Func<bool> isCurrent)
    {
        // Use the normal disable flow and deliberately leave QSTCompanion disabled on Stop.
        var result = await EnsurePluginDisabledAsync(
            "MOGTOME start", "QSTCompanion", "/xldisableplugin QSTCompanion",
            static (internalName, _) => string.Equals(internalName, "QSTCompanion", StringComparison.OrdinalIgnoreCase),
            isCurrent).ConfigureAwait(false);
        if (!isCurrent()) return;
        if (result.FinalStatus.IsLoaded)
            log.Warning("[MOGTOME][Conflict] QSTCompanion is still loaded after the startup disable attempt.");

        await GameHelpers.RunOnFrameworkThreadAsync(() =>
        {
            if (!isCurrent() || !GetPluginStatus(static (internalName, _) =>
                    string.Equals(internalName, "Coppelia", StringComparison.OrdinalIgnoreCase)).IsLoaded)
                return;

            log.Information("[MOGTOME][Conflict] Sending /healbot off for loaded HealBot during startup.");
            GameHelpers.SendCommand("/healbot off");
        }).ConfigureAwait(false);
    }

    public async Task<bool> EnsureTwistOfFayteDisabledAsync(string triggerSource, bool showPopup, Func<bool>? isCurrent = null)
    {
        var result = await EnsurePluginDisabledAsync(
            triggerSource,
            TwistOfFayteDisplayName,
            TwistOfFayteDisableCommand,
            MatchesTwistOfFayte, isCurrent).ConfigureAwait(false);
        if (isCurrent?.Invoke() == false) return false;
        if (!result.InitialStatus.IsInstalled)
        {
            ClearPendingWarning();
            log.Information($"[MOGTOME][Conflict] {TwistOfFayteDisplayName} check during {triggerSource}: no matching installed plugin entry was found; popup suppressed.");
            return true;
        }

        if (!result.InitialStatus.IsLoaded)
        {
            ClearPendingWarning();
            log.Information($"[MOGTOME][Conflict] {TwistOfFayteDisplayName} check during {triggerSource}: {DescribePluginStatus(result.InitialStatus)}; popup suppressed because the plugin is already disabled.");
            return true;
        }

        if (!result.FinalStatus.IsLoaded)
        {
            var successMessage = Ui.M("Conflict_WasEnabledAndHasBeenAutoDisabled", TwistOfFayteDisplayName);
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", successMessage));
            log.Information($"[MOGTOME][Conflict] {successMessage} Match={DescribePluginStatus(result.InitialStatus)} DisableAttempted={result.DisableAttempted}");

            if (showPopup)
            {
                QueueWarning(
                    Ui.M("Conflict_MOGTOMEKeptRunningClickTheWarningWindow", successMessage));
            }

            return true;
        }

        var failureMessage = Ui.M("Conflict_IsStillEnabledMOGTOMEWillKeepRunning", TwistOfFayteDisplayName, TwistOfFayteDisableCommand);
        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", failureMessage));
        log.Warning($"[MOGTOME][Conflict] {failureMessage} Match={DescribePluginStatus(result.FinalStatus)} DisableAttempted={result.DisableAttempted}");

        if (showPopup)
        {
            QueueWarning(
                Ui.M("Conflict_UseTheWarningWindowButtonToTry", failureMessage));
        }

        return false;
    }

    public async Task<bool> EnsureAutoDutyDisabledAsync(string triggerSource, bool showPopup, Func<bool>? isCurrent = null)
    {
        var result = await EnsurePluginDisabledAsync(
            triggerSource,
            AutoDutyDisplayName,
            AutoDutyDisableCommand,
            MatchesAutoDuty, isCurrent).ConfigureAwait(false);
        if (isCurrent?.Invoke() == false) return false;
        if (!result.InitialStatus.IsInstalled)
        {
            log.Information($"[MOGTOME][Conflict] {AutoDutyDisplayName} check during {triggerSource}: no matching installed plugin entry was found; popup suppressed.");
            return true;
        }

        if (!result.InitialStatus.IsLoaded)
        {
            log.Information($"[MOGTOME][Conflict] {AutoDutyDisplayName} check during {triggerSource}: {DescribePluginStatus(result.InitialStatus)}; popup suppressed because the plugin is already disabled.");
            return true;
        }

        if (!result.FinalStatus.IsLoaded)
        {
            var successMessage = Ui.M("Conflict_WasEnabledAndHasBeenAutoDisabled2", AutoDutyDisplayName);
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", successMessage));
            log.Information($"[MOGTOME][Conflict] {successMessage} Match={DescribePluginStatus(result.InitialStatus)} DisableAttempted={result.DisableAttempted}");

            if (showPopup)
                QueueWarning(successMessage);

            return true;
        }

        var failureMessage = Ui.M("Conflict_IsStillEnabledADSModeExpects", AutoDutyDisplayName, AutoDutyDisableCommand);
        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", failureMessage));
        log.Warning($"[MOGTOME][Conflict] {failureMessage} Match={DescribePluginStatus(result.FinalStatus)} DisableAttempted={result.DisableAttempted}");

        if (showPopup)
            QueueWarning(failureMessage);

        return false;
    }

    public bool TryTakePendingWarning(out UiText message)
    {
        lock (stateLock)
        {
            if (pendingWarningMessage == null)
            {
                message = string.Empty;
                return false;
            }

            message = pendingWarningMessage;
            pendingWarningMessage = null;
            return true;
        }
    }

    private void QueueWarning(UiText message)
    {
        lock (stateLock)
        {
            pendingWarningMessage = message;
        }
    }

    private void ClearPendingWarning()
    {
        lock (stateLock)
        {
            pendingWarningMessage = null;
        }
    }

    private async Task<PluginDisableResult> EnsurePluginDisabledAsync(
        string triggerSource,
        string displayName,
        string disableCommand,
        Func<string?, string?, bool> matcher, Func<bool>? isCurrent = null)
    {
        var initialStatus = GetPluginStatus(matcher);
        if (!initialStatus.IsLoaded)
            return new PluginDisableResult(initialStatus, initialStatus, false);

        var shouldSendDisable = false;
        lock (stateLock)
        {
            var now = DateTime.UtcNow;
            if (!lastDisableAttemptUtc.TryGetValue(initialStatus.InternalName, out var lastAttempt) || now - lastAttempt >= DisableAttemptCooldown)
            {
                lastDisableAttemptUtc[initialStatus.InternalName] = now;
                shouldSendDisable = true;
            }
        }

        if (shouldSendDisable)
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is enabled during {triggerSource}; matched {DescribePluginStatus(initialStatus)}; sending {disableCommand}");
            if (!await GameHelpers.RunOnFrameworkThreadAsync(() => isCurrent?.Invoke() != false && commandManager.ProcessCommand(disableCommand)).ConfigureAwait(false))
            {
                log.Error($"[MOGTOME][Conflict] Native plugin command was not handled: {disableCommand}");
                return new PluginDisableResult(initialStatus, initialStatus, true);
            }
        }
        else
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is still enabled during {triggerSource}; matched {DescribePluginStatus(initialStatus)}; waiting for recent disable attempt");
        }

        var finalStatus = await WaitForPluginStateAsync(matcher, loaded: false, isCurrent).ConfigureAwait(false);
        return new PluginDisableResult(initialStatus, finalStatus, shouldSendDisable);
    }

    private async Task<PluginStatus> WaitForPluginStateAsync(Func<string?, string?, bool> matcher, bool loaded, Func<bool>? isCurrent = null)
    {
        var deadline = DateTime.UtcNow + DisableWaitTimeout;
        while (isCurrent?.Invoke() != false && DateTime.UtcNow < deadline)
        {
            var status = GetPluginStatus(matcher);
            var isBossMod = status.InternalName is "BossMod" or "BossModReborn";
            if (status.IsInstalled && (isBossMod
                    ? status.LoadState == (loaded ? "Loaded" : "Unloaded")
                    : status.IsLoaded == loaded))
                return status;

            await Task.Delay(DisablePollInterval).ConfigureAwait(false);
        }

        return GetPluginStatus(matcher);
    }

    private PluginStatus GetPluginStatus(Func<string?, string?, bool> matcher)
    {
        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                if (!matcher(plugin.InternalName, plugin.Name))
                    continue;

                var bossModPlugin = plugin.InternalName is "BossMod" or "BossModReborn"
                    ? BossModIPC.FindDalamudPlugin(plugin.InternalName, out _)
                    : null;
                return new PluginStatus(
                    true,
                    plugin.IsLoaded,
                    plugin.InternalName ?? string.Empty,
                    plugin.Name ?? string.Empty,
                    bossModPlugin?.GetType().GetProperty("State")?.GetValue(bossModPlugin)?.ToString());
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Conflict] Failed to inspect installed plugins: {ex.Message}");
        }

        return new PluginStatus(false, false, string.Empty, string.Empty);
    }

    private static bool MatchesTwistOfFayte(string? internalName, string? displayName)
    {
        var normalizedInternalName = NormalizePluginToken(internalName);
        var normalizedDisplayName = NormalizePluginToken(displayName);

        return normalizedInternalName == TwistOfFayteShortName ||
               normalizedInternalName == TwistOfFayteLegacyShortName ||
               normalizedDisplayName == TwistOfFayteShortName ||
               normalizedDisplayName == TwistOfFayteLegacyShortName;
    }

    private static bool MatchesAutoDuty(string? internalName, string? displayName)
    {
        var normalizedInternalName = NormalizePluginToken(internalName);
        var normalizedDisplayName = NormalizePluginToken(displayName);

        return normalizedInternalName == AutoDutyShortName ||
               normalizedDisplayName == AutoDutyShortName;
    }

    private static string NormalizePluginToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static string DescribePluginStatus(PluginStatus status)
    {
        if (!status.IsInstalled)
            return "InternalName='<missing>', Name='<missing>', IsLoaded=false";

        var internalName = string.IsNullOrWhiteSpace(status.InternalName) ? "<empty>" : status.InternalName;
        var displayName = string.IsNullOrWhiteSpace(status.DisplayName) ? "<empty>" : status.DisplayName;
        return $"InternalName='{internalName}', Name='{displayName}', IsLoaded={status.IsLoaded}";
    }
}
