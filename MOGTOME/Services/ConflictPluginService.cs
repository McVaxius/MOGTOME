using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace MOGTOME.Services;

public sealed class ConflictPluginService
{
    private const string TwistOfFayteDisplayName = "Twist of Fayte";
    private const string TwistOfFayteShortName = "twistofffayte";
    private const string TwistOfFayteDisableCommand = "/xldisableplugin twistofffayte";

    private static readonly TimeSpan DisableAttemptCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisableWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisablePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private readonly object stateLock = new();

    private DateTime lastDisableAttemptUtc = DateTime.MinValue;
    private string? pendingPopupMessage;

    public ConflictPluginService(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;
    }

    public (bool IsInstalled, bool IsLoaded) GetTwistOfFayteStatus()
    {
        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                if (!MatchesTwistOfFayte(plugin.InternalName, plugin.GetType().GetProperty("Name")?.GetValue(plugin)?.ToString()))
                    continue;

                return (true, plugin.IsLoaded);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[Conflict] Failed to inspect installed plugins: {ex.Message}");
        }

        return (false, false);
    }

    public async Task<bool> EnsureTwistOfFayteDisabledAsync(string triggerSource, bool showPopup)
    {
        var status = GetTwistOfFayteStatus();
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
            log.Warning($"[Conflict] {TwistOfFayteDisplayName} is enabled during {triggerSource}; sending {TwistOfFayteDisableCommand}");
            commandManager.ProcessCommand(TwistOfFayteDisableCommand);
        }
        else
        {
            log.Warning($"[Conflict] {TwistOfFayteDisplayName} is still enabled during {triggerSource}; waiting for recent disable attempt");
        }

        var unloaded = await WaitForTwistOfFayteUnloadAsync().ConfigureAwait(false);
        if (unloaded)
        {
            var successMessage = $"{TwistOfFayteDisplayName} was enabled and has been auto-disabled for MOGTOME.";
            Plugin.ChatGui.Print($"[MOGTOME] {successMessage}");
            log.Information($"[Conflict] {successMessage}");

            if (showPopup)
            {
                QueuePopup(
                    $"{successMessage}\n\nIf you need it again later, re-enable it after your MOGTOME run.");
            }

            return true;
        }

        var failureMessage = $"{TwistOfFayteDisplayName} is still enabled. Disable it manually with {TwistOfFayteDisableCommand} before starting MOGTOME.";
        Plugin.ChatGui.Print($"[MOGTOME] {failureMessage}");
        log.Warning($"[Conflict] {failureMessage}");

        if (showPopup)
        {
            QueuePopup(
                $"{failureMessage}\n\nMOGTOME will stay stopped until the conflicting plugin is disabled.");
        }

        return false;
    }

    public bool TryTakePendingPopup(out string message)
    {
        lock (stateLock)
        {
            if (string.IsNullOrWhiteSpace(pendingPopupMessage))
            {
                message = string.Empty;
                return false;
            }

            message = pendingPopupMessage;
            pendingPopupMessage = null;
            return true;
        }
    }

    private void QueuePopup(string message)
    {
        lock (stateLock)
        {
            pendingPopupMessage = message;
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

    private static bool MatchesTwistOfFayte(string? internalName, string? displayName)
    {
        var normalizedInternalName = NormalizePluginToken(internalName);
        var normalizedDisplayName = NormalizePluginToken(displayName);

        return normalizedInternalName == "twistofffayte" ||
               normalizedInternalName == "twistoffayte" ||
               normalizedDisplayName == "twistoffayte" ||
               normalizedDisplayName == "twistofffayte";
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
