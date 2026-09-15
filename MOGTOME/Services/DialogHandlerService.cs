using System;
using System.Collections.Generic;
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
    private string returnPromptText = string.Empty;
    private long? returnPromptFirstSeenAt;

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
        returnPromptText = string.Empty;
        returnPromptFirstSeenAt = null;
    }

    public void Update(bool returnToStartEligible)
    {
        if (!returnToStartEligible)
            ResetReturnPromptWait();

        var now = DateTime.UtcNow;
        if ((now - lastDialogCheck).TotalSeconds < DialogCheckCooldown) return;
        lastDialogCheck = now;

        try
        {
            TryAcceptRecognizedYesNoPrompt(returnToStartEligible);
        }
        catch (Exception ex)
        {
            ResetReturnPromptWait();
            log.Error($"[MOGTOME][DialogHandler] Update failed: {ex.Message}");
        }
    }

    private unsafe void TryAcceptRecognizedYesNoPrompt(bool returnToStartEligible)
    {
        nint addonPtr = gameGui.GetAddonByName("SelectYesno", 1);
        if (addonPtr == 0)
        {
            ResetReturnPromptWait();
            return;
        }

        var addon = (AddonSelectYesno*)addonPtr;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            ResetReturnPromptWait();
            return;
        }

        var promptNode = addon->PromptText;
        if (promptNode == null || !promptNode->NodeText.StringPtr.HasValue)
        {
            ResetReturnPromptWait();
            return;
        }

        var dialogText = GameText.Normalize(GameText.ReadVisibleText(promptNode->NodeText.AsSpan()));
        if (string.IsNullOrWhiteSpace(dialogText))
        {
            ResetReturnPromptWait();
            return;
        }

        var isReturnPrompt = returnToStartEligible &&
            GameText.MatchesPrompt(dialogText, GamePrompt.Return);
        if (!isReturnPrompt)
        {
            ResetReturnPromptWait();
        }
        else if (!string.Equals(dialogText, returnPromptText, StringComparison.Ordinal))
        {
            returnPromptText = dialogText;
            returnPromptFirstSeenAt = Stopwatch.GetTimestamp();
        }

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

        if (isReturnPrompt && returnPromptFirstSeenAt is { } firstSeenAt &&
            ReturnDelayElapsed(Stopwatch.GetElapsedTime(firstSeenAt), configManager.GetActiveConfig().ReturnToEntranceDelaySeconds))
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
        if (GameText.MatchesPrompt(dialogText, prompt))
        {

            if (GameHelpers.ClickYesIfVisible())
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
}
