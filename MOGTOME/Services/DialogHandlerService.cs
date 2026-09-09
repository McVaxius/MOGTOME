using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using MOGTOME.IPC;

namespace MOGTOME.Services;

public class DialogHandlerService
{
    private readonly IPluginLog log;
    private readonly YesAlreadyIPC yesAlreadyIPC;
    private readonly ICommandManager commandManager;
    private readonly IGameGui gameGui;
    private static readonly IReadOnlyList<string> SealedAreaOfferPatterns =
    [
        "Move immediately to sealed area",
        "Move immediately to the sealed area",
    ];
    private static readonly IReadOnlyList<string> RaiseOfferPatterns =
    [
        "Would you like to be raised",
        "Accept Raise",
    ];
    private const string ReturnToStartPromptPattern = "Return to the starting point";
    private static readonly IReadOnlyList<string> ReturnToStartPatterns = [ReturnToStartPromptPattern];
    private static readonly TimeSpan ReturnPromptDelay = TimeSpan.FromSeconds(300);
    private string returnPromptText = string.Empty;
    private long? returnPromptFirstSeenAt;

    private DateTime lastDialogCheck = DateTime.MinValue;
    private const float DialogCheckCooldown = 0.5f;
    private string lastHandledDialog = string.Empty;
    private DateTime lastHandledDialogAt = DateTime.MinValue;
    private static readonly TimeSpan DialogHandleCooldown = TimeSpan.FromSeconds(2);

    public DialogHandlerService(
        IPluginLog log, YesAlreadyIPC yesAlreadyIPC,
        ICommandManager commandManager, IGameGui gameGui)
    {
        this.log = log;
        this.yesAlreadyIPC = yesAlreadyIPC;
        this.commandManager = commandManager;
        this.gameGui = gameGui;
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

        var promptSeString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(promptNode->NodeText.StringPtr));
        var dialogText = promptSeString.TextValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dialogText))
        {
            ResetReturnPromptWait();
            return;
        }

        var isReturnPrompt = returnToStartEligible &&
            dialogText.Contains(ReturnToStartPromptPattern, StringComparison.OrdinalIgnoreCase);
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

        if (TryAcceptPrompt(dialogText, now, RaiseOfferPatterns, "raise offer"))
        {
            return;
        }

        if (TryAcceptPrompt(dialogText, now, SealedAreaOfferPatterns, "sealed-area move"))
        {
            return;
        }

        if (isReturnPrompt && returnPromptFirstSeenAt is { } firstSeenAt &&
            Stopwatch.GetElapsedTime(firstSeenAt) >= ReturnPromptDelay)
        {
            TryAcceptPrompt(dialogText, now, ReturnToStartPatterns, "return to starting point");
        }
    }

    private bool TryAcceptPrompt(
        string dialogText,
        DateTime now,
        IReadOnlyList<string> patterns,
        string promptKind)
    {
        foreach (var pattern in patterns)
        {
            if (!dialogText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;

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

    private void CheckAndConfirmDialog(string addonName)
    {
        try
        {
            var addon = gameGui.GetAddonByName(addonName);
            if (addon == nint.Zero) return;

            // Confirm the dialog via callback
            commandManager.ProcessCommand($"/callback {addonName} true 0");
            log.Debug($"[MOGTOME][DialogHandler] Confirmed dialog: {addonName}");
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][DialogHandler] Dialog check failed for {addonName}: {ex.Message}");
        }
    }
}
