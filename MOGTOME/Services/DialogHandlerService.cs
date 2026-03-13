using System;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;

namespace MOGTOME.Services;

public class DialogHandlerService
{
    private readonly IPluginLog log;
    private readonly YesAlreadyIPC yesAlreadyIPC;
    private readonly ICommandManager commandManager;
    private readonly IGameGui gameGui;

    private DateTime lastDialogCheck = DateTime.MinValue;
    private const float DialogCheckCooldown = 0.5f;

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
        yesAlreadyIPC.Pause();
        log.Information("[DialogHandler] Started - YesAlready paused");
    }

    public void Stop()
    {
        yesAlreadyIPC.Unpause();
        log.Information("[DialogHandler] Stopped - YesAlready unpaused");
    }

    public void Update()
    {
        var now = DateTime.UtcNow;
        if ((now - lastDialogCheck).TotalSeconds < DialogCheckCooldown) return;
        lastDialogCheck = now;

        try
        {
            // Check for SelectYesno addon
            CheckAndConfirmDialog("SelectYesno");
            // Check for ContentFinderConfirm addon
            CheckAndConfirmDialog("ContentsFinderConfirm");
        }
        catch (Exception ex)
        {
            log.Error($"[DialogHandler] Update failed: {ex.Message}");
        }
    }

    private void CheckAndConfirmDialog(string addonName)
    {
        try
        {
            var addon = gameGui.GetAddonByName(addonName);
            if (addon == nint.Zero) return;

            // Confirm the dialog via callback
            commandManager.ProcessCommand($"/callback {addonName} true 0");
            log.Debug($"[DialogHandler] Confirmed dialog: {addonName}");
        }
        catch (Exception ex)
        {
            log.Debug($"[DialogHandler] Dialog check failed for {addonName}: {ex.Message}");
        }
    }
}
