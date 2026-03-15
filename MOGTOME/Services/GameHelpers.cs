using System;
using System.Text;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MOGTOME.Services;

/// <summary>
/// Static unsafe helpers for game interactions and UI callbacks.
/// Based on FrenRider GameHelpers pattern.
/// </summary>
public static class GameHelpers
{
    /// <summary>
    /// Click Yes on SelectYesno dialog if visible.
    /// Uses AtkUnitBase.FireCallback with proper AtkValue array.
    /// </summary>
    public static unsafe bool ClickYesIfVisible()
    {
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
                return false;

            // Create AtkValue array for Yes button (index 0)
            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[0].Int = 0; // Yes button index
            atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[1].Int = 0;

            addon->FireCallback(2, atkValues);
            Plugin.Log.Information("[YES/NO] Clicked Yes on SelectYesno dialog");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[YES/NO] ClickYesIfVisible failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fire a callback on an addon with parameters.
    /// Pattern from FrenRider GameHelpers.
    /// SND equivalent: /callback AddonName true/false arg1 arg2 ...
    /// </summary>
    public static unsafe void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
            {
                Plugin.Log.Warning($"[Callback] Addon '{addonName}' not found or not visible");
                return;
            }

            var atkValues = new AtkValue[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                atkValues[i] = args[i] switch
                {
                    int intVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = intVal },
                    uint uintVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = uintVal },
                    bool boolVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte = (byte)(boolVal ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = Convert.ToInt32(args[i]) },
                };
            }

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }

            Plugin.Log.Information($"[Callback] Fired on '{addonName}' with {args.Length} args, updateState={updateState}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Callback] Failed for '{addonName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        try
        {
            Plugin.Log.Information($"[INTERACT] Starting interaction with {obj.Name.TextValue} (Address: {obj.Address:X})");

            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                Plugin.Log.Error("[INTERACT] TargetSystem.Instance() is null");
                return false;
            }

            var gameObject = (GameObject*)obj.Address;
            if (gameObject == null)
            {
                Plugin.Log.Error("[INTERACT] GameObject pointer is null");
                return false;
            }

            ts->InteractWithObject(gameObject, true);
            Plugin.Log.Information($"[INTERACT] Successfully interacted with {obj.Name.TextValue}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[INTERACT] Failed to interact with {obj.Name.TextValue}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send chat command (like /dutyfinder) via UIModule.
    /// First tries CommandManager, falls back to ProcessChatBoxEntry.
    /// </summary>
    public static unsafe void SendCommand(string command)
    {
        try
        {
            Plugin.Log.Debug($"[CommandHelper] Sending command: {command}");
            
            if (Plugin.CommandManager.ProcessCommand(command))
            {
                Plugin.Log.Debug($"[CommandHelper] CommandManager processed: {command}");
                return;
            }

            var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
            if (uiModule == null)
            {
                Plugin.Log.Error("UIModule is null, cannot send command");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
            Plugin.Log.Debug($"[CommandHelper] Sent via UIModule: {command}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Command failed [{command}]: {ex.Message}");
        }
    }
}
