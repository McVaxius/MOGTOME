using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using Lumina.Excel.Sheets;

namespace MOGTOME.Services;

/// <summary>
/// Static unsafe helpers for game interactions and UI callbacks.
/// Based on FrenRider GameHelpers pattern.
/// </summary>
public static class GameHelpers
{
    private static readonly HashSet<string> KnownInnTerritoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "The Mizzenmast",
        "Mizzenmast Inn",
        "The Roost",
        "The Hourglass",
        "The Forgotten Knight",
        "Cloud Nine",
        "Bokairo Inn",
        "The Pendants",
        "The Andron",
        "The Baldesion Annex",
        "The For'ard Cabins",
    };

    /// <summary>
    /// Run work on the framework thread immediately or on a future tick after a delay.
    /// </summary>
    public static Task RunOnFrameworkThreadAsync(System.Action action, TimeSpan delay = default)
    {
        if (delay <= TimeSpan.Zero && Plugin.Framework.IsInFrameworkUpdateThread)
        {
            action();
            return Task.CompletedTask;
        }

        return Plugin.Framework.RunOnTick(action, delay);
    }

    public static Task<T> RunOnFrameworkThreadAsync<T>(Func<T> func, TimeSpan delay = default)
    {
        if (delay <= TimeSpan.Zero && Plugin.Framework.IsInFrameworkUpdateThread)
            return Task.FromResult(func());

        return Plugin.Framework.RunOnTick(func, delay);
    }

    public static void QueueFrameworkAction(string owner, string stepName, TimeSpan delay, System.Action action)
    {
        _ = QueueFrameworkActionAsync(owner, stepName, delay, action);
    }

    private static async Task QueueFrameworkActionAsync(string owner, string stepName, TimeSpan delay, System.Action action)
    {
        try
        {
            await RunOnFrameworkThreadAsync(action, delay).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MOGTOME][FrameworkQueue] {Owner} step '{StepName}' failed", owner, stepName);
        }
    }

    /// <summary>
    /// Click Yes on SelectYesno dialog if visible.
    /// Uses AtkUnitBase.FireCallback with proper AtkValue array.
    /// </summary>
    public static unsafe bool ClickYesIfVisible()
    {
        const string addonName = "SelectYesno";
        const string callbackArgs = "0, 0";

        if (!EnsureFrameworkThreadForCallback(addonName, updateState: null, callbackArgs, adsOperationId: null))
            return false;

        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName(addonName, 1);
            if (addonPtr == 0)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
                return false;

            // Create AtkValue array for Yes button (index 0)
            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
            atkValues[0].Int = 0; // Yes button index
            atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int;
            atkValues[1].Int = 0;

            addon->FireCallback(2, atkValues);
            Plugin.Log.Information("[MOGTOME][YES/NO] Clicked Yes on SelectYesno dialog");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME][YES/NO] ClickYesIfVisible failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fire callback on addon with parameters.
    /// Pattern from FrenRider GameHelpers.
    /// SND equivalent: /callback AddonName true/false arg1 arg2 ...
    /// </summary>
    public static void FireAddonCallback(string addonName, bool updateState, params object[] args)
        => TryFireAddonCallback(addonName, updateState, args);

    public static bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
        => TryFireAddonCallbackInternal(addonName, updateState, adsOperationId: null, args);

    public static bool TryFireAdsAddonCallback(int operationId, string addonName, bool updateState, params object[] args)
        => TryFireAddonCallbackInternal(addonName, updateState, operationId, args);

    private static unsafe bool TryFireAddonCallbackInternal(string addonName, bool updateState, int? adsOperationId, params object[] args)
    {
        var argsText = string.Join(", ", args);

        if (!EnsureFrameworkThreadForCallback(addonName, updateState, argsText, adsOperationId))
            return false;

        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            var addonAddress = (nint)addon;
            var visible = addon != null && addon->IsVisible;

            if (!visible)
            {
                if (adsOperationId.HasValue)
                {
                    Plugin.Log.Warning($"[MOGTOME][DutyQueue] Callback operation {adsOperationId.Value}: addon={addonName}, ptr=0x{addonAddress:X}, visible={visible}, updateState={updateState}, args=[{argsText}], result=failure-not-visible");
                }
                else
                {
                    Plugin.Log.Warning($"[MOGTOME][Callback] Addon '{addonName}' not found or not visible");
                }

                return false;
            }

            var atkValues = new AtkValue[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                atkValues[i] = args[i] switch
                {
                    int intVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = intVal },
                    uint uintVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt, UInt = uintVal },
                    bool boolVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool, Byte = (byte)(boolVal ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = Convert.ToInt32(args[i]) },
                };
            }

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }

            if (adsOperationId.HasValue)
            {
                Plugin.Log.Information($"[MOGTOME][DutyQueue] Callback operation {adsOperationId.Value}: addon={addonName}, ptr=0x{addonAddress:X}, visible={visible}, updateState={updateState}, args=[{argsText}], result=success");
            }
            else
            {
                Plugin.Log.Information($"[MOGTOME][Callback] Fired callback on '{addonName}' with args: {argsText}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (adsOperationId.HasValue)
            {
                Plugin.Log.Error($"[MOGTOME][DutyQueue] Callback operation {adsOperationId.Value}: addon={addonName}, visible=unknown, updateState={updateState}, args=[{argsText}], result=failure-exception, error={ex.Message}");
            }
            else
            {
                Plugin.Log.Error($"[MOGTOME][Callback] Failed to fire callback on '{addonName}': {ex.Message}");
            }

            return false;
        }
    }

    private static bool EnsureFrameworkThreadForCallback(string addonName, bool? updateState, string argsText, int? adsOperationId)
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread)
            return true;

        var updateStateText = updateState.HasValue ? updateState.Value.ToString() : "n/a";
        if (adsOperationId.HasValue)
        {
            Plugin.Log.Warning($"[MOGTOME][DutyQueue] Callback operation {adsOperationId.Value}: addon={addonName}, updateState={updateStateText}, args=[{argsText}], result=blocked-off-thread");
        }
        else
        {
            Plugin.Log.Warning($"[MOGTOME][Callback] Blocked off-thread callback: addon={addonName}, updateState={updateStateText}, args=[{argsText}]");
        }

        return false;
    }

    /// <summary>
    /// Check if addon is visible.
    /// </summary>
    public static unsafe bool IsAddonVisible(string addonName)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
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
            Plugin.Log.Information($"[MOGTOME][INTERACT] Starting interaction with {obj.Name.TextValue} (Address: {obj.Address:X})");

            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null)
            {
                Plugin.Log.Error("[MOGTOME][INTERACT] TargetSystem.Instance() is null");
                return false;
            }

            var gameObject = (GameObject*)obj.Address;
            if (gameObject == null)
            {
                Plugin.Log.Error("[MOGTOME][INTERACT] GameObject pointer is null");
                return false;
            }

            ts->InteractWithObject(gameObject, true);
            Plugin.Log.Information($"[MOGTOME][INTERACT] Successfully interacted with {obj.Name.TextValue}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME][INTERACT] Failed to interact with {obj.Name.TextValue}: {ex.Message}");
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
            Plugin.Log.Debug($"[MOGTOME][CommandHelper] Sending command: {command}");
            
            if (Plugin.CommandManager.ProcessCommand(command))
            {
                Plugin.Log.Debug($"[MOGTOME][CommandHelper] CommandManager processed: {command}");
                return;
            }

            var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
            if (uiModule == null)
            {
                Plugin.Log.Error("[MOGTOME]UIModule is null, cannot send command");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
            Plugin.Log.Debug($"[MOGTOME][CommandHelper] Sent via UIModule: {command}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME]Command failed [{command}]: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a slash command exactly as if it were typed into chat.
    /// Use this for commands that should bypass CommandManager plugin routing.
    /// </summary>
    public static unsafe void SendChatCommand(string command)
    {
        try
        {
            Plugin.Log.Debug($"[MOGTOME][ChatCommandHelper] Sending chat command: {command}");

            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                Plugin.Log.Error("[MOGTOME]UIModule is null, cannot send chat command");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
            Plugin.Log.Debug($"[MOGTOME][ChatCommandHelper] Sent via UIModule: {command}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME]Chat command failed [{command}]: {ex.Message}");
        }
    }

    public static string GetTerritoryName(uint territoryId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryId, out var territory))
            {
                var placeName = territory.PlaceName.Value.Name.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(placeName))
                    return placeName;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[MOGTOME][GameHelpers] Failed to resolve territory name for {TerritoryId}", territoryId);
        }

        return $"Territory {territoryId}";
    }

    public static bool IsInnTerritory(uint territoryId)
    {
        var territoryName = GetTerritoryName(territoryId);
        if (territoryName.StartsWith("Territory ", StringComparison.OrdinalIgnoreCase))
            return false;

        return territoryName.Contains("Inn", StringComparison.OrdinalIgnoreCase)
            || KnownInnTerritoryNames.Contains(territoryName);
    }

    /// <summary>
    /// Get remaining time for current duty.
    /// Returns remaining time in seconds, 0 if not in duty or unavailable.
    /// Uses InstancedContent.ContentTimeLeft based on SND GetContentTimeLeft() pattern.
    /// </summary>
    public static unsafe float GetDutyRemainingTime()
    {
        try
        {
            var eventFramework = EventFramework.Instance();
            if (eventFramework == null)
                return 0f;

            var instanceContentDirector = eventFramework->GetInstanceContentDirector();
            if (instanceContentDirector == null || !instanceContentDirector->HasTimer())
                return 0f;

            return instanceContentDirector->ContentTimeLeft;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME]GetDutyRemainingTime failed: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// Set the Duty Finder Level Sync setting.
    /// Primary mechanism: AutoDuty IPC SetConfig("LevelSync", value) called by the engine.
    /// Direct manipulation via AgentContentsFinder is not available in this FFXIVClientStructs version
    /// (no LevelSync field exposed). If AutoDuty IPC doesn't work for level sync, this method
    /// will need to be updated with direct memory manipulation once the correct offset is found.
    /// Duty Finder settings positions (from top): 1st=JoinInProgress, 2nd=Unsync, 3rd=LevelSync
    /// </summary>
    public static unsafe void SetDutyFinderLevelSync(bool enable)
    {
        try
        {
            // AgentContentsFinder doesn't expose LevelSync as a named field in current FFXIVClientStructs.
            // Relying on AutoDuty IPC SetConfig("LevelSync", value) as primary mechanism.
            // If that doesn't work, we'll need to find the byte offset in the agent for direct manipulation.
            Plugin.Log.Information($"[MOGTOME][GameHelpers] SetDutyFinderLevelSync={enable} (via AutoDuty IPC, no direct agent access available)");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[MOGTOME][GameHelpers] SetDutyFinderLevelSync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Use an item from inventory by item ID.
    /// Mirrors AutoDuty's approach: uses extraParam 65535 and checks for casting/occupied state.
    /// Copied from FrenRider GameHelpers.
    /// </summary>
    public static unsafe bool UseItem(uint itemId)
        => UseItem(itemId, highQuality: false);

    /// <summary>
    /// Use an item from inventory by item ID, optionally as HQ.
    /// HQ item actions use the same base item row with the HQ action ID offset.
    /// </summary>
    public static unsafe bool UseItem(uint itemId, bool highQuality)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                Plugin.Log.Warning($"[MOGTOME]UseItem({itemId}): LocalPlayer is null");
                return false;
            }

            // Check if player is casting
            if (player.IsCasting)
            {
                Plugin.Log.Debug($"[MOGTOME]UseItem({itemId}): Player is casting, skipping");
                return false;
            }

            // Check if player is occupied (in cutscene, etc)
            if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied33] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied39])
            {
                Plugin.Log.Debug($"[MOGTOME]UseItem({itemId}): Player is occupied, skipping");
                return false;
            }

            var am = ActionManager.Instance();
            if (am == null)
            {
                Plugin.Log.Warning($"[MOGTOME]UseItem({itemId}): ActionManager is null");
                return false;
            }

            var actionItemId = highQuality ? itemId + 1_000_000u : itemId;

            // Check if the action is ready
            var status = am->GetActionStatus(ActionType.Item, actionItemId);
            if (status != 0)
            {
                Plugin.Log.Debug($"[MOGTOME]UseItem({itemId}, HQ={highQuality}): ActionStatus={status}, not ready");
                return false;
            }

            // Use item with extraParam 65535 (required for item usage)
            var result = am->UseAction(ActionType.Item, actionItemId, extraParam: 65535);
            Plugin.Log.Information($"[MOGTOME]UseItem({itemId}, HQ={highQuality}): UseAction result={result}");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME]UseItem({itemId}, HQ={highQuality}) failed: {ex.Message}");
            return false;
        }
    }

    public static unsafe int GetInventoryItemCount(uint itemId, bool highQuality)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
            {
                Plugin.Log.Warning($"[MOGTOME]GetInventoryItemCount({itemId}, HQ={highQuality}): InventoryManager is null");
                return 0;
            }

            return inventoryManager->GetInventoryItemCount(itemId, highQuality);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MOGTOME]GetInventoryItemCount({itemId}, HQ={highQuality}) failed: {ex.Message}");
            return 0;
        }
    }
}
