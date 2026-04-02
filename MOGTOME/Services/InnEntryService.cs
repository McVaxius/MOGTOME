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
    private static readonly HashSet<string> KnownInnNpcNames = new(StringComparer.Ordinal)
    {
        "Antoinaut",
        "Otopa Pottopa",
        "Mytesyn",
        "Bamponcet",
        "Ushitora",
        "Manager of Suites",
        "Ojika Tsunjika",
        "Peshekwa",
    };

    private readonly IPluginLog log;
    private readonly VNavIPC vNavIPC;
    private InnEntryState state = InnEntryState.Idle;
    private string targetNpcName = string.Empty;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime stateStartedAtUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastInteractUtc = DateTime.MinValue;
    private DateTime lastMenuClickUtc = DateTime.MinValue;

    public bool IsRunning => state != InnEntryState.Idle;
    public string StatusMessage { get; private set; } = "Idle";

    public InnEntryService(IPluginLog log, VNavIPC vNavIPC)
    {
        this.log = log;
        this.vNavIPC = vNavIPC;
    }

    public void StartManualEntry()
    {
        StartEntry("[MOGTOME] /mog inn requires a logged-in character.", "manual restart", allowRestart: true);
    }

    public void StartRepairReturnEntry()
    {
        StartEntry(string.Empty, "repair return restart", allowRestart: true);
    }

    private void StartEntry(string missingCharacterMessage, string restartReason, bool allowRestart)
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            if (!string.IsNullOrWhiteSpace(missingCharacterMessage))
                Plugin.ChatGui.Print(missingCharacterMessage);
            return;
        }

        if (IsRunning && allowRestart)
            Cancel(restartReason, notifyUser: false);

        if (GameHelpers.IsInnTerritory((ushort)Plugin.ClientState.TerritoryType))
        {
            var territoryName = GameHelpers.GetTerritoryName((ushort)Plugin.ClientState.TerritoryType);
            log.Information($"[Inn] /mog inn skipped because the player is already inside inn territory {territoryName}");
            Plugin.ChatGui.Print($"[MOGTOME] Already inside inn territory: {territoryName}.");
            return;
        }

        var npc = FindNearbyInnNpc();
        if (npc == null)
        {
            log.Information($"[Inn] /mog inn found no innkeeper within {SearchRadiusYalms:F0}y; treating as no-op success");
            Plugin.ChatGui.Print($"[MOGTOME] No innkeeper found within {SearchRadiusYalms:F0}y. /mog inn did nothing.");
            return;
        }

        targetNpcName = npc.Name.TextValue;
        startedAtUtc = DateTime.UtcNow;
        stateStartedAtUtc = startedAtUtc;
        lastMoveCommandUtc = DateTime.MinValue;
        lastInteractUtc = DateTime.MinValue;
        lastMenuClickUtc = DateTime.MinValue;

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            state = InnEntryState.WaitingForMenu;
            StatusMessage = $"Interacting with innkeeper {targetNpcName}";
            log.Information($"[Inn] /mog inn found {targetNpcName} at {distance:F1}y; interacting immediately");
            TryInteract(npc);
            return;
        }

        state = InnEntryState.MovingToNpc;
        StatusMessage = $"Moving to innkeeper {targetNpcName}";
        log.Information($"[Inn] /mog inn found {targetNpcName} at {distance:F1}y; moving into interaction range");
        SendMoveCommand(npc, initial: true);
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (GameHelpers.IsInnTerritory((ushort)Plugin.ClientState.TerritoryType))
            {
                Complete("Entered inn territory successfully.");
                return;
            }

            if (DateTime.UtcNow - startedAtUtc > OverallTimeout)
            {
                Fail("Timed out while trying to enter the inn.");
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
            Fail($"Inn entry failed: {ex.Message}");
        }
    }

    public void Cancel(string reason, bool notifyUser = true)
    {
        if (!IsRunning)
            return;

        vNavIPC.Stop();
        log.Warning($"[Inn] /mog inn cancelled: {reason}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;

        if (notifyUser)
            Plugin.ChatGui.Print($"[MOGTOME] /mog inn cancelled: {reason}");
    }

    private void UpdateMovingToNpc()
    {
        if (TryAdvanceInnDialogs())
        {
            TransitionTo(InnEntryState.WaitingForZone, "Waiting for inn zone transition");
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail($"Innkeeper {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            vNavIPC.Stop();
            TransitionTo(InnEntryState.WaitingForMenu, $"Interacting with {targetNpcName}");
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
            TransitionTo(InnEntryState.WaitingForZone, "Waiting for inn zone transition");
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail($"Innkeeper {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance > SearchRadiusYalms + 5.0f)
        {
            Fail($"Drifted too far away from {targetNpcName} while waiting to interact.");
            return;
        }

        if (distance > InteractRadiusYalms)
        {
            TransitionTo(InnEntryState.MovingToNpc, $"Repositioning near {targetNpcName}");
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

        log.Warning($"[Inn] Zone transition did not start after selecting the inn option for {targetNpcName}; retrying interaction");
        TransitionTo(InnEntryState.WaitingForMenu, $"Retrying {targetNpcName}");
    }

    private void TransitionTo(InnEntryState nextState, string statusMessage)
    {
        state = nextState;
        stateStartedAtUtc = DateTime.UtcNow;
        StatusMessage = statusMessage;
    }

    private bool TryAdvanceInnDialogs()
    {
        var now = DateTime.UtcNow;
        if (now - lastMenuClickUtc < MenuRetryCooldown)
            return false;

        if (GameHelpers.IsAddonVisible("SelectString"))
        {
            log.Information($"[Inn] Selecting the first SelectString option for {targetNpcName}");
            GameHelpers.FireAddonCallback("SelectString", true, 0);
            lastMenuClickUtc = now;
            return true;
        }

        if (GameHelpers.IsAddonVisible("SelectIconString"))
        {
            log.Information($"[Inn] Selecting the first SelectIconString option for {targetNpcName}");
            GameHelpers.FireAddonCallback("SelectIconString", true, 0);
            lastMenuClickUtc = now;
            return true;
        }

        if (GameHelpers.ClickYesIfVisible())
        {
            log.Information($"[Inn] Confirmed SelectYesno while entering the inn through {targetNpcName}");
            lastMenuClickUtc = now;
            return true;
        }

        return false;
    }

    private void TryInteract(IGameObject npc)
    {
        lastInteractUtc = DateTime.UtcNow;
        Plugin.TargetManager.Target = npc;
        if (GameHelpers.InteractWithObject(npc))
            log.Information($"[Inn] Interacting with innkeeper {targetNpcName}");
    }

    private void SendMoveCommand(IGameObject npc, bool initial)
    {
        lastMoveCommandUtc = DateTime.UtcNow;
        var action = initial ? "starting movement" : "refreshing movement";
        log.Information($"[Inn] {action} toward {targetNpcName} at {DistanceToLocalPlayer(npc):F1}y");
        vNavIPC.MoveTo(npc.Position);
    }

    private void Complete(string message)
    {
        vNavIPC.Stop();
        log.Information($"[Inn] {message}");
        Plugin.ChatGui.Print($"[MOGTOME] {message}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;
    }

    private void Fail(string message)
    {
        vNavIPC.Stop();
        log.Warning($"[Inn] {message}");
        Plugin.ChatGui.Print($"[MOGTOME] {message}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
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

            var name = obj.Name.TextValue;
            if (!KnownInnNpcNames.Contains(name))
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
        if (player == null || string.IsNullOrWhiteSpace(targetNpcName))
            return null;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            if (!string.Equals(obj.Name.TextValue, targetNpcName, StringComparison.Ordinal))
                continue;

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    private static float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, obj.Position);
    }
}
