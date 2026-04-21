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

    private DateTime lastDisableAttemptUtc = DateTime.MinValue;
    private string? pendingWarningMessage;

    public ConflictPluginService(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;
    }

    public (bool IsInstalled, bool IsLoaded) GetTwistOfFayteStatus()
    {
        return GetPluginStatus(MatchesTwistOfFayte);
    }

    public (bool IsInstalled, bool IsLoaded) GetAutoDutyStatus()
    {
        return GetPluginStatus(MatchesAutoDuty);
    }

    public async Task<bool> EnsureTwistOfFayteDisabledAsync(string triggerSource, bool showPopup)
    {
        var unloaded = await EnsurePluginDisabledAsync(
            triggerSource,
            TwistOfFayteDisplayName,
            TwistOfFayteDisableCommand,
            MatchesTwistOfFayte).ConfigureAwait(false);
        if (unloaded)
        {
            var successMessage = $"{TwistOfFayteDisplayName} was enabled and has been auto-disabled for MOGTOME.";
            Plugin.ChatGui.Print($"[MOGTOME] {successMessage}");
            log.Information($"[MOGTOME][Conflict] {successMessage}");

            if (showPopup)
            {
                QueueWarning(
                    $"{successMessage}\n\nMOGTOME kept running. Click the warning window once to dismiss it, or use the disable button there if the plugin comes back.");
            }

            return true;
        }

        var failureMessage = $"{TwistOfFayteDisplayName} is still enabled. MOGTOME will keep running, but you should disable it with {TwistOfFayteDisableCommand}.";
        Plugin.ChatGui.Print($"[MOGTOME] {failureMessage}");
        log.Warning($"[MOGTOME][Conflict] {failureMessage}");

        if (showPopup)
        {
            QueueWarning(
                $"{failureMessage}\n\nUse the warning window button to try disabling it again, or dismiss the warning and keep going.");
        }

        return true;
    }

    public async Task<bool> EnsureAutoDutyDisabledAsync(string triggerSource, bool showPopup)
    {
        var unloaded = await EnsurePluginDisabledAsync(
            triggerSource,
            AutoDutyDisplayName,
            AutoDutyDisableCommand,
            MatchesAutoDuty).ConfigureAwait(false);
        if (unloaded)
        {
            var successMessage = $"{AutoDutyDisplayName} was enabled and has been auto-disabled for ADS mode.";
            Plugin.ChatGui.Print($"[MOGTOME] {successMessage}");
            log.Information($"[MOGTOME][Conflict] {successMessage}");

            if (showPopup)
                QueueWarning(successMessage);

            return true;
        }

        var status = GetAutoDutyStatus();
        if (!status.IsLoaded)
            return true;

        var failureMessage = $"{AutoDutyDisplayName} is still enabled. ADS mode expects {AutoDutyDisableCommand}.";
        Plugin.ChatGui.Print($"[MOGTOME] {failureMessage}");
        log.Warning($"[MOGTOME][Conflict] {failureMessage}");

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

    private async Task<bool> WaitForTwistOfFayteUnloadAsync()
    {
        var deadline = DateTime.UtcNow + DisableWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!GetTwistOfFayteStatus().IsLoaded)
                return true;

            await Task.Delay(DisablePollInterval).ConfigureAwait(false);
        }

        return !GetTwistOfFayteStatus().IsLoaded;
    }

    private async Task<bool> EnsurePluginDisabledAsync(
        string triggerSource,
        string displayName,
        string disableCommand,
        Func<string?, string?, bool> matcher)
    {
        var status = GetPluginStatus(matcher);
        if (!status.IsLoaded)
            return true;

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
            log.Warning($"[MOGTOME][Conflict] {displayName} is enabled during {triggerSource}; sending {disableCommand}");
            commandManager.ProcessCommand(disableCommand);
        }
        else
        {
            log.Warning($"[MOGTOME][Conflict] {displayName} is still enabled during {triggerSource}; waiting for recent disable attempt");
        }

        return await WaitForPluginUnloadAsync(matcher).ConfigureAwait(false);
    }

    private async Task<bool> WaitForPluginUnloadAsync(Func<string?, string?, bool> matcher)
    {
        var deadline = DateTime.UtcNow + DisableWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!GetPluginStatus(matcher).IsLoaded)
                return true;

            await Task.Delay(DisablePollInterval).ConfigureAwait(false);
        }

        return !GetPluginStatus(matcher).IsLoaded;
    }

    private (bool IsInstalled, bool IsLoaded) GetPluginStatus(Func<string?, string?, bool> matcher)
    {
        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                if (!matcher(plugin.InternalName, plugin.Name))
                    continue;

                return (true, plugin.IsLoaded);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Conflict] Failed to inspect installed plugins: {ex.Message}");
        }

        return (false, false);
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
}
