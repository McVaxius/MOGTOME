using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

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
    private readonly record struct PluginStatus(bool IsInstalled, bool IsLoaded, string InternalName, string DisplayName);
    private readonly record struct PluginDisableResult(PluginStatus InitialStatus, PluginStatus FinalStatus, bool DisableAttempted);

    private DateTime lastDisableAttemptUtc = DateTime.MinValue;
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
            if (now - lastDisableAttemptUtc >= DisableAttemptCooldown)
            {
                lastDisableAttemptUtc = now;
                shouldSendDisable = true;
            }
        }

        if (shouldSendDisable)
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is enabled during {triggerSource}; matched {DescribePluginStatus(initialStatus)}; sending {disableCommand}");
            await GameHelpers.RunOnFrameworkThreadAsync(() => commandManager.ProcessCommand(disableCommand)).ConfigureAwait(false);
        }
        else
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is still enabled during {triggerSource}; matched {DescribePluginStatus(initialStatus)}; waiting for recent disable attempt");
        }

        var finalStatus = await WaitForPluginUnloadAsync(matcher).ConfigureAwait(false);
        return new PluginDisableResult(initialStatus, finalStatus, shouldSendDisable);
    }

    private async Task<PluginStatus> WaitForPluginUnloadAsync(Func<string?, string?, bool> matcher)
    {
        var deadline = DateTime.UtcNow + DisableWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = GetPluginStatus(matcher);
            if (!status.IsLoaded)
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

                return new PluginStatus(
                    true,
                    plugin.IsLoaded,
                    plugin.InternalName ?? string.Empty,
                    plugin.Name ?? string.Empty);
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
