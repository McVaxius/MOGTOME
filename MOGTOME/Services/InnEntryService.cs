using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;

namespace MOGTOME.Services;

public class InnEntryService
{
    private enum InnEntryState
    {
        Idle,
        MovingToNpc,
        WaitingForMenu,
        WaitingForZone,
    }

    private const float SearchRadiusYalms = 30.0f;
    private const float InteractRadiusYalms = 3.0f;
    private static readonly TimeSpan MoveRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MenuRetryCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ZoneWaitTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);
    // ENpcBase/ENpcResident: Antoinaut, Mytesyn, Otopa Pottopa, Bamponcet,
    // Ushitora, Manager of Suites, Ojika Tsunjika, Peshekwa (innkeeper spawns).
    internal static bool IsInnkeeper(uint baseId) => baseId is
        1000102 or 1000974 or 1001976 or 1011193 or 1018981 or 1027231 or 1037293 or 1048375;

    private readonly IPluginLog log;
    private readonly VNavIPC vNavIPC;
    private InnEntryState state = InnEntryState.Idle;
    private string targetNpcName = string.Empty;
    private ulong targetObjectId;
    private uint targetBaseId;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime stateStartedAtUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastInteractUtc = DateTime.MinValue;
    private DateTime lastMenuClickUtc = DateTime.MinValue;

    public bool IsRunning => state != InnEntryState.Idle;
    public string StatusMessage => Status.English;
    public UiText Status { get; private set; } = Ui.M("EngineState_Idle");

    public InnEntryService(IPluginLog log, VNavIPC vNavIPC)
    {
        this.log = log;
        this.vNavIPC = vNavIPC;
    }

    public void StartManualEntry()
    {
        StartEntry(Ui.M("Inn_MOGTOMEMogInnRequiresALoggedIn"), "manual restart", allowRestart: true);
    }

    public void StartRepairReturnEntry()
    {
        StartEntry(string.Empty, "repair return restart", allowRestart: true);
    }

    private void StartEntry(UiText missingCharacterMessage, string restartReason, bool allowRestart)
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            if (!string.IsNullOrWhiteSpace(missingCharacterMessage.English))
                Plugin.ChatGui.Print(missingCharacterMessage.Render());
            return;
        }

        if (IsRunning && allowRestart)
            Cancel(restartReason, notifyUser: false);

        if (GameHelpers.IsInnTerritory(Plugin.ClientState.TerritoryType))
        {
            var territoryName = GameHelpers.GetTerritoryName(Plugin.ClientState.TerritoryType);
            log.Information($"[MOGTOME][Inn] /mog inn skipped because the player is already inside inn territory {territoryName}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEAlreadyInsideInnTerritory", Ui.Duty(Plugin.ClientState.TerritoryType)));
            return;
        }

        var npc = FindNearbyInnNpc();
        if (npc == null)
        {
            log.Information($"[MOGTOME][Inn] /mog inn found no innkeeper within {SearchRadiusYalms:F0}y; treating as no-op success");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMENoInnkeeperFoundWithinYMog", SearchRadiusYalms));
            return;
        }

        targetNpcName = npc.Name.TextValue;
        targetObjectId = npc.GameObjectId;
        targetBaseId = npc.BaseId;
        startedAtUtc = DateTime.UtcNow;
        stateStartedAtUtc = startedAtUtc;
        lastMoveCommandUtc = DateTime.MinValue;
        lastInteractUtc = DateTime.MinValue;
        lastMenuClickUtc = DateTime.MinValue;

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            state = InnEntryState.WaitingForMenu;
            Status = Ui.M("Inn_InteractingWithInnkeeper", Ui.Npc(targetBaseId));
            log.Information($"[MOGTOME][Inn] /mog inn found {targetNpcName} at {distance:F1}y; interacting immediately");
            TryInteract(npc);
            return;
        }

        state = InnEntryState.MovingToNpc;
        Status = Ui.M("Inn_MovingToInnkeeper", Ui.Npc(targetBaseId));
        log.Information($"[MOGTOME][Inn] /mog inn found {targetNpcName} at {distance:F1}y; moving into interaction range");
        SendMoveCommand(npc, initial: true);
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (GameHelpers.IsInnTerritory(Plugin.ClientState.TerritoryType))
            {
                Complete(Ui.M("Inn_EnteredInnTerritorySuccessfully"));
                return;
            }

            if (DateTime.UtcNow - startedAtUtc > OverallTimeout)
            {
                Fail(Ui.M("Inn_TimedOutWhileTryingToEnterThe"));
                return;
            }

            switch (state)
            {
                case InnEntryState.MovingToNpc:
                    UpdateMovingToNpc();
                    break;
                case InnEntryState.WaitingForMenu:
                    UpdateWaitingForMenu();
                    break;
                case InnEntryState.WaitingForZone:
                    UpdateWaitingForZone();
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(Ui.M("Inn_InnEntryFailed", ex.Message));
        }
    }

    public void Cancel(string reason, bool notifyUser = true)
    {
        if (!IsRunning)
            return;

        vNavIPC.Stop();
        log.Warning($"[MOGTOME][Inn] /mog inn cancelled: {reason}");
        state = InnEntryState.Idle;
        Status = Ui.M("EngineState_Idle");
        targetNpcName = string.Empty;

        if (notifyUser)
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEMogInnCancelled", reason));
    }

    private void UpdateMovingToNpc()
    {
        if (TryAdvanceInnDialogs())
        {
            TransitionTo(InnEntryState.WaitingForZone, Ui.M("Inn_WaitingForInnZoneTransition"));
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail(Ui.M("Inn_InnkeeperIsNoLongerNearby", Ui.Npc(targetBaseId)));
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            vNavIPC.Stop();
            TransitionTo(InnEntryState.WaitingForMenu, Ui.M("Inn_InteractingWith", Ui.Npc(targetBaseId)));
            TryInteract(npc);
            return;
        }

        if (DateTime.UtcNow - lastMoveCommandUtc >= MoveRetryCooldown)
            SendMoveCommand(npc, initial: false);
    }

    private void UpdateWaitingForMenu()
    {
        if (TryAdvanceInnDialogs())
        {
            TransitionTo(InnEntryState.WaitingForZone, Ui.M("Inn_WaitingForInnZoneTransition"));
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail(Ui.M("Inn_InnkeeperIsNoLongerNearby", Ui.Npc(targetBaseId)));
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance > SearchRadiusYalms + 5.0f)
        {
            Fail(Ui.M("Inn_DriftedTooFarAwayFromWhileWaiting", Ui.Npc(targetBaseId)));
            return;
        }

        if (distance > InteractRadiusYalms)
        {
            TransitionTo(InnEntryState.MovingToNpc, Ui.M("Inn_RepositioningNear", Ui.Npc(targetBaseId)));
            SendMoveCommand(npc, initial: true);
            return;
        }

        if (DateTime.UtcNow - lastInteractUtc >= InteractRetryCooldown)
            TryInteract(npc);
    }

    private void UpdateWaitingForZone()
    {
        if (TryAdvanceInnDialogs())
            return;

        if (Plugin.Condition[ConditionFlag.BetweenAreas])
            return;

        if (DateTime.UtcNow - stateStartedAtUtc < ZoneWaitTimeout)
            return;

        log.Warning($"[MOGTOME][Inn] Zone transition did not start after selecting the inn option for {targetNpcName}; retrying interaction");
        TransitionTo(InnEntryState.WaitingForMenu, Ui.M("Inn_Retrying", Ui.Npc(targetBaseId)));
    }

    private void TransitionTo(InnEntryState nextState, UiText statusMessage)
    {
        state = nextState;
        stateStartedAtUtc = DateTime.UtcNow;
        Status = statusMessage;
    }

    private bool TryAdvanceInnDialogs()
    {
        var now = DateTime.UtcNow;
        if (now - lastMenuClickUtc < MenuRetryCooldown)
            return false;

        if (GameHelpers.IsAddonVisible("SelectString"))
        {
            log.Information($"[MOGTOME][Inn] Selecting the first SelectString option for {targetNpcName}");
            GameHelpers.FireAddonCallback("SelectString", true, 0);
            lastMenuClickUtc = now;
            return true;
        }

        if (GameHelpers.IsAddonVisible("SelectIconString"))
        {
            log.Information($"[MOGTOME][Inn] Selecting the first SelectIconString option for {targetNpcName}");
            GameHelpers.FireAddonCallback("SelectIconString", true, 0);
            lastMenuClickUtc = now;
            return true;
        }

        // Supported innkeepers use the entry menu above. An unrelated Yes/No
        // prompt is not evidence that an inn transition should be confirmed.
        return false;
    }

    private void TryInteract(IGameObject npc)
    {
        lastInteractUtc = DateTime.UtcNow;
        Plugin.TargetManager.Target = npc;
        if (GameHelpers.InteractWithObject(npc))
            log.Information($"[MOGTOME][Inn] Interacting with innkeeper {targetNpcName}");
    }

    private void SendMoveCommand(IGameObject npc, bool initial)
    {
        lastMoveCommandUtc = DateTime.UtcNow;
        var action = initial ? "starting movement" : "refreshing movement";
        log.Information($"[MOGTOME][Inn] {action} toward {targetNpcName} at {DistanceToLocalPlayer(npc):F1}y");
        vNavIPC.MoveTo(npc.Position);
    }

    private void Complete(UiText message)
    {
        vNavIPC.Stop();
        log.Information($"[MOGTOME][Inn] {message}");
        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
        state = InnEntryState.Idle;
        Status = Ui.M("EngineState_Idle");
        targetNpcName = string.Empty;
    }

    private void Fail(UiText message)
    {
        vNavIPC.Stop();
        log.Warning($"[MOGTOME][Inn] {message}");
        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
        state = InnEntryState.Idle;
        Status = Ui.M("EngineState_Idle");
        targetNpcName = string.Empty;
    }

    private static IGameObject? FindNearbyInnNpc()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            if (!IsInnkeeper(obj.BaseId))
                continue;

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance > SearchRadiusYalms || distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    private IGameObject? FindTargetNpc()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || targetObjectId == 0)
            return null;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            if (!IsSelectedInnkeeper(obj.GameObjectId, obj.BaseId, targetObjectId, targetBaseId))
                continue;

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    internal static bool IsSelectedInnkeeper(ulong objectId, uint baseId, ulong selectedObjectId, uint selectedBaseId)
        => selectedObjectId != 0 && objectId == selectedObjectId && baseId == selectedBaseId && IsInnkeeper(baseId);

    private static float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, obj.Position);
    }
}
