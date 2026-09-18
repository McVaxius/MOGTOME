using System;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using MOGTOME.IPC;

namespace MOGTOME.Services;

public class DialogHandlerService
{
    private readonly IPluginLog log;
    private readonly YesAlreadyIPC yesAlreadyIPC;
    private readonly ICommandManager commandManager;
    private readonly IGameGui gameGui;
    private readonly ConfigManager configManager;
    private long? eligibleDeathStartedAt;
    private bool fullPartyWipeObserved;

    private DateTime lastDialogCheck = DateTime.MinValue;
    private const float DialogCheckCooldown = 0.5f;
    private string lastHandledDialog = string.Empty;
    private DateTime lastHandledDialogAt = DateTime.MinValue;
    private static readonly TimeSpan DialogHandleCooldown = TimeSpan.FromSeconds(2);

    public DialogHandlerService(
        IPluginLog log, YesAlreadyIPC yesAlreadyIPC,
        ICommandManager commandManager, IGameGui gameGui, ConfigManager configManager)
    {
        this.log = log;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.commandManager = commandManager;
        this.gameGui = gameGui;
        this.configManager = configManager;
    }

    public void Start()
    {
        ResetReturnPromptWait();
        yesAlreadyIPC.Pause();
        log.Information("[MOGTOME][DialogHandler] Started - YesAlready paused");
    }

    public void Stop()
    {
        ResetReturnPromptWait();
        yesAlreadyIPC.Unpause();
        log.Information("[MOGTOME][DialogHandler] Stopped - YesAlready unpaused");
    }

    public void ResetReturnPromptWait()
    {
        eligibleDeathStartedAt = null;
        fullPartyWipeObserved = false;
    }

    internal void ObservePartyDeaths(IPartyList party)
    {
        if (fullPartyWipeObserved)
            return;

        var partySize = party.Length;
        if (partySize == 0)
            return;

        for (var i = 0; i < partySize; i++)
        {
            if (party[i]?.GameObject?.IsDead != true)
                return;
        }

        // Retain a confirmed wipe for this death even if another member returns first.
        fullPartyWipeObserved = true;
    }

    public void Update(bool returnToStartEligible)
    {
        if (!returnToStartEligible)
            ResetReturnPromptWait();
        else
            eligibleDeathStartedAt ??= Stopwatch.GetTimestamp();

        var now = DateTime.UtcNow;
        if ((now - lastDialogCheck).TotalSeconds < DialogCheckCooldown) return;
        lastDialogCheck = now;

        try
        {
            TryAcceptRecognizedYesNoPrompt(returnToStartEligible);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][DialogHandler] Update failed: {ex.Message}");
        }
    }

    private void TryAcceptRecognizedYesNoPrompt(bool returnToStartEligible)
    {
        var (visible, dialogText) = ReadYesNoPrompt();
        var returnReady = returnToStartEligible && (fullPartyWipeObserved ||
            eligibleDeathStartedAt is { } startedAt &&
            ReturnDelayElapsed(Stopwatch.GetElapsedTime(startedAt), configManager.GetActiveConfig().ReturnToEntranceDelaySeconds));
        if (!visible)
        {
            if (returnReady && IsAddonVisible("_NotificationRevive"))
                TryFireAddonCallback("_Notification", true, 0, 1, 2);
            // Re-read and classify the restored prompt on the next update.
            return;
        }

        if (string.IsNullOrWhiteSpace(dialogText))
            return;

        var now = DateTime.UtcNow;
        if (string.Equals(dialogText, lastHandledDialog, StringComparison.OrdinalIgnoreCase) &&
            now - lastHandledDialogAt < DialogHandleCooldown)
        {
            return;
        }

        if (TryAcceptPrompt(dialogText, now, GamePrompt.Raise, "raise offer"))
        {
            return;
        }

        if (TryAcceptPrompt(dialogText, now, GamePrompt.SealedArea, "sealed-area move"))
        {
            return;
        }

        if (returnReady)
        {
            TryAcceptPrompt(dialogText, now, GamePrompt.Return, "return to starting point");
        }
    }

    private bool TryAcceptPrompt(
        string dialogText,
        DateTime now,
        GamePrompt prompt,
        string promptKind)
    {
        if (MatchesPrompt(dialogText, prompt))
        {

            if (ClickYesIfVisible())
            {
                lastHandledDialog = dialogText;
                lastHandledDialogAt = now;
                log.Information($"[MOGTOME][DialogHandler] Accepted {promptKind}: {dialogText}");
            }
            else
            {
                log.Warning($"[MOGTOME][DialogHandler] {promptKind} detected but Yes click failed: {dialogText}");
            }

            return true;
        }

        return false;
    }

    internal static bool ReturnDelayElapsed(TimeSpan elapsed, int delaySeconds)
        => elapsed >= TimeSpan.FromSeconds(Math.Max(1, delaySeconds));

    // Keep native reads/callbacks at the boundary so the flow can be exercised without a game client.
    internal virtual unsafe (bool Visible, string? Text) ReadYesNoPrompt()
    {
        var addon = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", 1).Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return (false, null);

        var promptNode = addon->PromptText;
        if (promptNode == null || !promptNode->NodeText.StringPtr.HasValue)
            return (true, null);

        return (true, GameText.Normalize(GameText.ReadVisibleText(promptNode->NodeText.AsSpan())));
    }

    internal virtual bool IsAddonVisible(string addonName) => GameHelpers.IsAddonVisible(addonName);
    internal virtual bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
        => GameHelpers.TryFireAddonCallback(addonName, updateState, args);
    internal virtual bool ClickYesIfVisible() => GameHelpers.ClickYesIfVisible();
    internal virtual bool MatchesPrompt(string text, GamePrompt prompt) => GameText.MatchesPrompt(text, prompt);
}
