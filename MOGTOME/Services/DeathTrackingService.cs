using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

public readonly record struct DeathTrackingSnapshot(int SelfDeathCount, int OtherDeathCount)
{
    public int TotalDeathCount => SelfDeathCount + OtherDeathCount;
    public static DeathTrackingSnapshot Empty => new(0, 0);
}

public sealed class DeathTrackingService : IDisposable
{
    private const uint InvalidEntityId = 0xE0000000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(10);

    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly object stateLock = new();
    private readonly Dictionary<ActorKey, ActorDeathState> actors = new();

    private bool active;
    private uint activeTerritoryId;
    private DateTime lastPollUtc = DateTime.MinValue;
    private DateTime lastPollErrorUtc = DateTime.MinValue;
    private int selfDeathCount;
    private int otherDeathCount;

    public DeathTrackingService(
        IPluginLog log,
        IFramework framework,
        IObjectTable objectTable,
        IPartyList partyList,
        IPlayerState playerState,
        ICondition condition,
        IClientState clientState)
    {
        this.log = log;
        this.framework = framework;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.playerState = playerState;
        this.condition = condition;
        this.clientState = clientState;

        framework.Update += OnFrameworkUpdate;
    }

    public bool IsActive
    {
        get
        {
            lock (stateLock)
            {
                return active;
            }
        }
    }

    public DeathTrackingSnapshot CurrentSnapshot
    {
        get
        {
            lock (stateLock)
            {
                return new DeathTrackingSnapshot(selfDeathCount, otherDeathCount);
            }
        }
    }

    public void Start(uint territoryId)
    {
        if (!DutyState.IsMogtomeDutyTerritory(territoryId))
            return;

        lock (stateLock)
        {
            active = true;
            activeTerritoryId = territoryId;
            selfDeathCount = 0;
            otherDeathCount = 0;
            actors.Clear();
            lastPollUtc = DateTime.MinValue;
        }

        log.Information($"[MOGTOME][DeathTracking] Started for territory {territoryId}");
    }

    public DeathTrackingSnapshot CaptureSnapshot(string reason)
    {
        lock (stateLock)
        {
            var snapshot = new DeathTrackingSnapshot(selfDeathCount, otherDeathCount);
            log.Information($"[MOGTOME][DeathTracking] Snapshot for {reason}: self={snapshot.SelfDeathCount}, others={snapshot.OtherDeathCount}, total={snapshot.TotalDeathCount}, territory={activeTerritoryId}");
            return snapshot;
        }
    }

    public void Clear(string reason)
    {
        DeathTrackingSnapshot previous;
        uint territoryId;
        bool wasActive;

        lock (stateLock)
        {
            previous = new DeathTrackingSnapshot(selfDeathCount, otherDeathCount);
            territoryId = activeTerritoryId;
            wasActive = active || actors.Count > 0 || previous.TotalDeathCount > 0;

            active = false;
            activeTerritoryId = 0;
            selfDeathCount = 0;
            otherDeathCount = 0;
            actors.Clear();
            lastPollUtc = DateTime.MinValue;
        }

        if (wasActive)
        {
            log.Debug($"[MOGTOME][DeathTracking] Cleared for {reason}: self={previous.SelfDeathCount}, others={previous.OtherDeathCount}, total={previous.TotalDeathCount}, territory={territoryId}");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        Clear("dispose");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsActive)
            return;

        var now = DateTime.UtcNow;
        if (now - lastPollUtc < PollInterval)
            return;

        lastPollUtc = now;

        if (!clientState.IsLoggedIn || condition[ConditionFlag.BetweenAreas] || !condition[34])
            return;

        try
        {
            PollActors();
        }
        catch (Exception ex)
        {
            if (now - lastPollErrorUtc >= ErrorLogInterval)
            {
                lastPollErrorUtc = now;
                log.Warning(ex, "[MOGTOME][DeathTracking] Poll failed");
            }
        }
    }

    private void PollActors()
    {
        var localPlayer = objectTable.LocalPlayer;
        var localContentId = playerState.ContentId;

        if (localPlayer != null)
            ObserveGameObject(localPlayer, localContentId, isSelf: true);

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member == null)
                continue;

            ObservePartyMember(member, localContentId, localPlayer);
        }
    }

    private void ObserveGameObject(IGameObject gameObject, ulong contentId, bool isSelf)
    {
        if (!gameObject.IsValid())
            return;

        var key = BuildGameObjectKey(gameObject, contentId);
        if (key == null)
            return;

        var isDead = gameObject.IsDead;
        if (gameObject is ICharacter character)
            isDead = isDead || (character.MaxHp > 0 && character.CurrentHp == 0);

        ObserveActor(key.Value, gameObject.Name.ToString(), isDead, isSelf);
    }

    private void ObservePartyMember(IPartyMember member, ulong localContentId, IGameObject? localPlayer)
    {
        var key = BuildPartyMemberKey(member);
        if (key == null)
            return;

        var gameObject = member.GameObject;
        var isSelf = IsPartyMemberSelf(member, localContentId, gameObject, localPlayer);
        var isDead = gameObject?.IsDead == true || (member.MaxHP > 0 && member.CurrentHP == 0);

        ObserveActor(key.Value, member.Name.ToString(), isDead, isSelf);
    }

    private void ObserveActor(ActorKey key, string displayName, bool isDead, bool isSelf)
    {
        lock (stateLock)
        {
            if (!actors.TryGetValue(key, out var actor))
            {
                actors[key] = new ActorDeathState
                {
                    DisplayName = displayName,
                    HasSeenAlive = !isDead,
                    IsSelf = isSelf,
                    WasDead = isDead,
                };
                return;
            }

            actor.DisplayName = string.IsNullOrWhiteSpace(displayName) ? actor.DisplayName : displayName;
            actor.IsSelf |= isSelf;

            if (!isDead)
            {
                actor.HasSeenAlive = true;
                actor.WasDead = false;
                return;
            }

            if (!actor.WasDead && actor.HasSeenAlive)
            {
                if (actor.IsSelf)
                    selfDeathCount++;
                else
                    otherDeathCount++;

                var snapshot = new DeathTrackingSnapshot(selfDeathCount, otherDeathCount);
                log.Information($"[MOGTOME][DeathTracking] Counted {(actor.IsSelf ? "self" : "party")} death for {actor.DisplayName}: self={snapshot.SelfDeathCount}, others={snapshot.OtherDeathCount}, total={snapshot.TotalDeathCount}");
            }

            actor.WasDead = true;
        }
    }

    private static ActorKey? BuildPartyMemberKey(IPartyMember member)
    {
        if (member.ContentId != 0)
            return new ActorKey("content", member.ContentId);

        if (IsStableEntityId(member.EntityId))
            return new ActorKey("entity", member.EntityId);

        return member.GameObject != null
            ? BuildGameObjectKey(member.GameObject, 0)
            : null;
    }

    private static ActorKey? BuildGameObjectKey(IGameObject gameObject, ulong contentId)
    {
        if (contentId != 0)
            return new ActorKey("content", contentId);

        if (IsStableGameObjectId(gameObject.GameObjectId))
            return new ActorKey("gameobject", gameObject.GameObjectId);

        if (IsStableEntityId(gameObject.EntityId))
            return new ActorKey("entity", gameObject.EntityId);

        return null;
    }

    private static bool IsStableEntityId(uint entityId)
        => entityId != 0 && entityId != InvalidEntityId;

    private static bool IsStableGameObjectId(ulong gameObjectId)
        => gameObjectId != 0 && gameObjectId != InvalidEntityId;

    private static bool IsPartyMemberSelf(IPartyMember member, ulong localContentId, IGameObject? memberObject, IGameObject? localPlayer)
    {
        if (localContentId != 0 && member.ContentId == localContentId)
            return true;

        if (memberObject == null || localPlayer == null)
            return false;

        if (localPlayer.GameObjectId != 0 && memberObject.GameObjectId == localPlayer.GameObjectId)
            return true;

        if (localPlayer.EntityId != 0 && memberObject.EntityId == localPlayer.EntityId)
            return true;

        return localPlayer.Address != IntPtr.Zero && memberObject.Address == localPlayer.Address;
    }

    private readonly record struct ActorKey(string Source, ulong Value);

    private sealed class ActorDeathState
    {
        public string DisplayName { get; set; } = "Unknown";
        public bool HasSeenAlive { get; set; }
        public bool WasDead { get; set; }
        public bool IsSelf { get; set; }
    }
}
