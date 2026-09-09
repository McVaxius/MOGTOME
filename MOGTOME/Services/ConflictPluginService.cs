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
    private string? pendingWarningMessage;

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

    public async Task<(bool Ready, bool PreferBmr, string Reason)> EnsureBossModReadyAsync(Func<bool> isStarting)
    {
        var bmr = GetPluginStatus(MatchesBmr);
        var vbm = GetPluginStatus(MatchesVbm);
        if (!bmr.IsLoaded || !vbm.IsLoaded)
            return (true, false, string.Empty);

        if (!isStarting())
            return (false, false, "Startup was stopped before BossMod cleanup.");

        var disabledVbm = await EnsurePluginDisabledAsync("MOGTOME start", "VBM", "/xldisableplugin BossMod", MatchesVbm).ConfigureAwait(false);
        if (disabledVbm.FinalStatus.LoadState != "Unloaded")
            return (false, false, "VBM could not be disabled while both BossMod variants were loaded.");

        // VBM disposal unregisters the shared BossMod IPC gates. Reload BMR once to restore them.
        // Once VBM has unloaded, finish this cleanup even if Stop was pressed; BMR must remain usable.
        var disabledBmr = await EnsurePluginDisabledAsync("BossMod IPC recovery", "BMR", "/xldisableplugintemp BossModReborn", MatchesBmr).ConfigureAwait(false);
        if (disabledBmr.FinalStatus.LoadState != "Unloaded")
            return (false, false, "BMR could not be temporarily disabled to restore its shared IPC registrations.");

        // Finish the temporary reload even if Stop was pressed while BMR was unloading.
        if (!await GameHelpers.RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand("/xlenableplugintemp BossModReborn")).ConfigureAwait(false))
            return (false, false, "The native temporary BMR enable command was not handled.");

        var readyBmr = await WaitForPluginStateAsync(MatchesBmr, loaded: true).ConfigureAwait(false);
        if (readyBmr.LoadState != "Loaded" || GetPluginStatus(MatchesVbm).LoadState != "Unloaded")
            return (false, false, "BMR did not become ready after its reload, or VBM loaded again.");

        log.Information("[MOGTOME][Conflict] Disabled VBM and reloaded BMR to restore shared BossMod IPC");
        return (true, true, string.Empty);
    }

    private static bool MatchesBmr(string? internalName, string? displayName) => internalName == "BossModReborn";
    private static bool MatchesVbm(string? internalName, string? displayName) => internalName == "BossMod";

    public async Task<bool> EnsureTwistOfFayteDisabledAsync(string triggerSource, bool showPopup)
    {
        var result = await EnsurePluginDisabledAsync(
            triggerSource,
            TwistOfFayteDisplayName,
            TwistOfFayteDisableCommand,
            MatchesTwistOfFayte).ConfigureAwait(false);
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
            var successMessage = $"{TwistOfFayteDisplayName} was enabled and has been auto-disabled for MOGTOME.";
            Plugin.ChatGui.Print($"[MOGTOME] {successMessage}");
            log.Information($"[MOGTOME][Conflict] {successMessage} Match={DescribePluginStatus(result.InitialStatus)} DisableAttempted={result.DisableAttempted}");

            if (showPopup)
            {
                QueueWarning(
                    $"{successMessage}\n\nMOGTOME kept running. Click the warning window once to dismiss it, or use the disable button there if the plugin comes back.");
            }

            return true;
        }

        var failureMessage = $"{TwistOfFayteDisplayName} is still enabled. MOGTOME will keep running, but you should disable it with {TwistOfFayteDisableCommand}.";
        Plugin.ChatGui.Print($"[MOGTOME] {failureMessage}");
        log.Warning($"[MOGTOME][Conflict] {failureMessage} Match={DescribePluginStatus(result.FinalStatus)} DisableAttempted={result.DisableAttempted}");

        if (showPopup)
        {
            QueueWarning(
                $"{failureMessage}\n\nUse the warning window button to try disabling it again, or dismiss the warning and keep going.");
        }

        return false;
    }

    public async Task<bool> EnsureAutoDutyDisabledAsync(string triggerSource, bool showPopup)
    {
        var result = await EnsurePluginDisabledAsync(
            triggerSource,
            AutoDutyDisplayName,
            AutoDutyDisableCommand,
            MatchesAutoDuty).ConfigureAwait(false);
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
            var successMessage = $"{AutoDutyDisplayName} was enabled and has been auto-disabled for ADS mode.";
            Plugin.ChatGui.Print($"[MOGTOME] {successMessage}");
            log.Information($"[MOGTOME][Conflict] {successMessage} Match={DescribePluginStatus(result.InitialStatus)} DisableAttempted={result.DisableAttempted}");

            if (showPopup)
                QueueWarning(successMessage);

            return true;
        }

        var failureMessage = $"{AutoDutyDisplayName} is still enabled. ADS mode expects {AutoDutyDisableCommand}.";
        Plugin.ChatGui.Print($"[MOGTOME] {failureMessage}");
        log.Warning($"[MOGTOME][Conflict] {failureMessage} Match={DescribePluginStatus(result.FinalStatus)} DisableAttempted={result.DisableAttempted}");

        if (showPopup)
            QueueWarning(failureMessage);

        return false;
    }

    public bool TryTakePendingWarning(out string message)
    {
        lock (stateLock)
        {
            if (string.IsNullOrWhiteSpace(pendingWarningMessage))
            {
                message = string.Empty;
                return false;
            }

            message = pendingWarningMessage;
            pendingWarningMessage = null;
            return true;
        }
    }

    private void QueueWarning(string message)
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
        Func<string?, string?, bool> matcher)
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
            if (!await GameHelpers.RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand(disableCommand)).ConfigureAwait(false))
            {
                log.Error($"[MOGTOME][Conflict] Native plugin command was not handled: {disableCommand}");
                return new PluginDisableResult(initialStatus, initialStatus, true);
            }
        }
        else
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is still enabled during {triggerSource}; matched {DescribePluginStatus(initialStatus)}; waiting for recent disable attempt");
        }

        var finalStatus = await WaitForPluginStateAsync(matcher, loaded: false).ConfigureAwait(false);
        return new PluginDisableResult(initialStatus, finalStatus, shouldSendDisable);
    }

    private async Task<PluginStatus> WaitForPluginStateAsync(Func<string?, string?, bool> matcher, bool loaded)
    {
        var deadline = DateTime.UtcNow + DisableWaitTimeout;
        while (DateTime.UtcNow < deadline)
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
