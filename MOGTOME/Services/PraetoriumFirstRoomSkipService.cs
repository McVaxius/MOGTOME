using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

/// <summary>
/// Local, opt-in Praetorium opener that hands duty control to ADS after terminal descent or a bounded failure.
/// </summary>
internal sealed class PraetoriumFirstRoomSkipService : IDisposable
{
    private enum SkipState
    {
        Idle,
        TankAcquirePack,
        TankUseInvulnerability,
        WaitingForNonTank,
        InteractingWithTerminal,
    }

    private readonly record struct TankActionMap(string Job, uint AreaActionId, uint InvulnerabilityActionId);

    private const uint TerminalDataId = 2012811;
    private static readonly Vector3 OpeningPackPosition = new(186.77065f, 185.99998f, -29.414112f);
    private static readonly Vector3 TerminalPosition = new(196.44f, 186f, -5.37f);
    private const float ArrivalDistance = 4f;
    private const float InteractDistance = 7f;
    private const float DescentDelta = 12f;
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MovementProgressTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TankActionDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan NonTankDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InteractionRetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly Dictionary<uint, TankActionMap> TankActions = new()
    {
        [19] = new("PLD", 7381, 30),     // Total Eclipse / Hallowed Ground
        [21] = new("WAR", 31, 43),       // Overpower / Holmgang
        [32] = new("DRK", 3621, 3638),   // Unleash / Living Dead
        [37] = new("GNB", 16137, 16152), // Demon Slice / Superbolide
    };

    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly VNavIPC vnav;
    private readonly Action<string> handoffToAds;
    private readonly Func<int> getSessionId;

    private SkipState state;
    private TankActionMap tankActions;
    private DateTime startedUtc;
    private DateTime nextActionUtc;
    private DateTime lastInteractionUtc;
    private DateTime lastMovementProgressUtc;
    private Vector3? movementTarget;
    private float lastMovementDistance;
    private float entryY;
    private bool active;

    public PraetoriumFirstRoomSkipService(
        IPluginLog log,
        IFramework framework,
        ICondition condition,
        IClientState clientState,
        IObjectTable objectTable,
        VNavIPC vnav,
        Action<string> handoffToAds, Func<int> getSessionId)
    {
        this.log = log;
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.vnav = vnav;
        this.handoffToAds = handoffToAds;
        this.getSessionId = getSessionId;
        framework.Update += OnFrameworkUpdate;
    }

    public bool IsActive => active;

    /// <summary>
    /// Starts only after the engine has already confirmed the ADS/Praetorium/toggle gate.
    /// A false return means the caller must hand control to ADS immediately.
    /// </summary>
    public bool TryStart()
    {
        if (active)
            return true;

        var player = objectTable.LocalPlayer;
        if (player == null || !player.ClassJob.IsValid)
        {
            log.Warning("[MOGTOME][FirstRoomSkip] Local job is unavailable; falling back to ADS");
            return false;
        }

        startedUtc = DateTime.UtcNow;
        nextActionUtc = startedUtc;
        lastInteractionUtc = DateTime.MinValue;
        entryY = player.Position.Y;
        movementTarget = null;
        lastMovementDistance = float.MaxValue;
        lastMovementProgressUtc = startedUtc;
        active = true;

        var jobRow = player.ClassJob.Value.RowId;
        if (TankActions.TryGetValue(jobRow, out tankActions))
        {
            state = SkipState.TankAcquirePack;
            if (RequestMove(OpeningPackPosition, "opening pack"))
            {
                log.Information($"[MOGTOME][FirstRoomSkip] Tank {tankActions.Job} starting local opening pull");
                return true;
            }

            return false;
        }

        state = SkipState.WaitingForNonTank;
        nextActionUtc = startedUtc + NonTankDelay;
        log.Information("[MOGTOME][FirstRoomSkip] Non-tank waiting one second before terminal movement");
        return true;
    }

    public void Cancel(string reason)
    {
        if (!active)
            return;

        active = false;
        state = SkipState.Idle;
        movementTarget = null;
        StopMovement($"cancelled: {reason}");
        log.Information($"[MOGTOME][FirstRoomSkip] Cancelled without ADS hand-off: {reason}");
    }

    public void Dispose()
    {
        Cancel("service dispose");
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!active)
            return;

        if (!clientState.IsLoggedIn ||
            clientState.TerritoryType != DutyState.PraetoriumTerritoryId ||
            !condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty])
        {
            Cancel("duty exit or engine context changed");
            return;
        }

        var now = DateTime.UtcNow;
        if (now - startedUtc >= TotalTimeout)
        {
            CompleteWithAdsHandoff("10 second experiment timeout");
            return;
        }

        if (!ObserveMovement(now))
            return;

        switch (state)
        {
            case SkipState.TankAcquirePack:
                if (!movementTarget.HasValue && now >= nextActionUtc)
                    UseTankAreaAction(now);
                break;
            case SkipState.TankUseInvulnerability:
                if (now >= nextActionUtc)
                    UseTankInvulnerability();
                break;
            case SkipState.WaitingForNonTank:
                if (now >= nextActionUtc)
                    MoveToTerminal();
                break;
            case SkipState.InteractingWithTerminal:
                TryInteractWithTerminal(now);
                break;
        }
    }

    private void UseTankAreaAction(DateTime now)
    {
        var target = FindOpeningPackTarget();
        if (target == null)
        {
            CompleteWithAdsHandoff("opening pack target unavailable");
            return;
        }

        Plugin.TargetManager.Target = target;
        if (!GameHelpers.TryUseCombatAction(tankActions.AreaActionId))
        {
            CompleteWithAdsHandoff($"{tankActions.Job} area action unavailable");
            return;
        }

        state = SkipState.TankUseInvulnerability;
        nextActionUtc = now + TankActionDelay;
    }

    private void UseTankInvulnerability()
    {
        if (!GameHelpers.TryUseCombatAction(tankActions.InvulnerabilityActionId))
        {
            CompleteWithAdsHandoff($"{tankActions.Job} invulnerability unavailable");
            return;
        }

        MoveToTerminal();
    }

    private void MoveToTerminal()
    {
        if (FindTerminal() == null)
        {
            CompleteWithAdsHandoff("Magitek Terminal 2012811 unavailable");
            return;
        }

        if (!RequestMove(TerminalPosition, "Magitek Terminal"))
            return;

        state = SkipState.InteractingWithTerminal;
    }

    private void TryInteractWithTerminal(DateTime now)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            CompleteWithAdsHandoff("local player unavailable at terminal");
            return;
        }

        if (player.Position.Y <= entryY - DescentDelta)
        {
            CompleteWithAdsHandoff("local terminal descent detected");
            return;
        }

        if (Vector3.Distance(player.Position, TerminalPosition) > InteractDistance ||
            now - lastInteractionUtc < InteractionRetryDelay)
        {
            return;
        }

        var terminal = FindTerminal();
        if (terminal == null)
        {
            CompleteWithAdsHandoff("Magitek Terminal 2012811 unavailable");
            return;
        }

        lastInteractionUtc = now;
        if (!GameHelpers.InteractWithObject(terminal))
            CompleteWithAdsHandoff("Magitek Terminal interaction unavailable");
    }

    private bool RequestMove(Vector3 destination, string label)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            CompleteWithAdsHandoff($"local player unavailable before movement to {label}");
            return false;
        }

        if (!vnav.MoveTo(destination))
        {
            CompleteWithAdsHandoff($"vnav movement to {label} unavailable");
            return false;
        }

        movementTarget = destination;
        lastMovementDistance = Vector3.Distance(player.Position, destination);
        lastMovementProgressUtc = DateTime.UtcNow;
        return true;
    }

    private bool ObserveMovement(DateTime now)
    {
        if (!movementTarget.HasValue)
            return true;

        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            CompleteWithAdsHandoff("local player unavailable while moving");
            return false;
        }

        var distance = Vector3.Distance(player.Position, movementTarget.Value);
        if (distance <= ArrivalDistance)
        {
            movementTarget = null;
            return true;
        }

        if (distance + 0.5f < lastMovementDistance)
        {
            lastMovementDistance = distance;
            lastMovementProgressUtc = now;
            return true;
        }

        if (now - lastMovementProgressUtc < MovementProgressTimeout)
            return true;

        CompleteWithAdsHandoff("vnav movement made no progress");
        return false;
    }

    private IGameObject? FindOpeningPackTarget()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return null;

        IGameObject? closest = null;
        var closestDistance = float.MaxValue;
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.BattleNpc)
                continue;

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance < closestDistance)
            {
                closest = obj;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private IGameObject? FindTerminal()
    {
        foreach (var obj in objectTable)
        {
            if (obj != null && obj.BaseId == TerminalDataId)
                return obj;
        }

        return null;
    }

    private void CompleteWithAdsHandoff(string reason)
    {
        if (!active)
            return;

        active = false;
        state = SkipState.Idle;
        movementTarget = null;
        StopMovement(reason);
        log.Information($"[MOGTOME][FirstRoomSkip] {reason}; handing off to ADS");
        handoffToAds(reason);
    }

    private void StopMovement(string reason)
    {
        if (framework.IsInFrameworkUpdateThread)
        {
            vnav.Stop();
            return;
        }

        var session = getSessionId();
        _ = framework.RunOnTick(() =>
        {
            if (session == getSessionId() && !active)
                vnav.Stop();
        });
        log.Debug($"[MOGTOME][FirstRoomSkip] Queued vnav stop: {reason}");
    }
}
