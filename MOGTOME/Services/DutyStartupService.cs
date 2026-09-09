using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using MOGTOME.Models;

namespace MOGTOME.Services;

internal enum DutyStartupResult
{
    Pending,
    Confirmed,
    Cancelled,
}

// Handoff branches and timing ported from FrenRider 0ad6baa AdsIntegrationService.
// Mogtome supplies its two supported duties, selected combat provider and AutoDuty adapter.
internal sealed class DutyStartupService(
    AdsDutyIpcService adsDutyIpcService,
    Func<bool> useAds,
    Func<bool> startAutoDuty,
    Func<bool> enableCombat,
    Func<string> combatFailure,
    Func<string, bool> processCommand,
    Func<float> getDutyRemainingTime,
    Action<string> logInformation,
    Action<string> logWarning)
{
    private const double PraetoriumReadyFallbackSeconds = 15.0;
    private readonly AdsHandoffState handoffState = new();
    private DateTime dutyEnteredUtc = DateTime.MinValue;
    private DateTime lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
    private DateTime handoffRequestedAtUtc = DateTime.MinValue;
    private DateTime nextHandoffAttemptUtc = DateTime.MinValue;
    private DateTime nextCombatAttemptUtc = DateTime.MinValue;
    private uint trackedDutyTerritoryId;
    private uint trackedDutyContentFinderConditionId;
    private bool trackedInDuty;
    private bool cancelled;

    internal AdsDutySession DutySession { get; private set; } = new();
    internal bool CombatActivated { get; private set; }
    internal bool BackendConfirmed { get; private set; }
    internal bool IsConfirmed => CombatActivated && BackendConfirmed;
    internal string StatusText { get; private set; } = "Waiting for duty readiness.";

    internal void ResetSession()
    {
        DutySession = new AdsDutySession();
        cancelled = false;
        ResetDutyTracking();
    }

    internal void Cancel()
    {
        cancelled = true;
        ResetHandoff();
        nextCombatAttemptUtc = DateTime.MinValue;
    }

    internal void DeferFailure(DateTime now, string reason)
        => BackoffFailedHandoff(now, reason);

    internal void OnDutyStarted(uint territoryId, DateTime nowUtc)
    {
        // Observation may precede DutyStarted. A delayed/duplicate event must not
        // erase accepted startup stages or resurrect a completed session.
        if (cancelled || DutySession.EnteredAtUtc != DateTime.MinValue || DutySession.IsCompleted)
            return;

        DutySession.Start(territoryId, nowUtc);
    }

    internal void OnDutyCompleted(uint territoryId, DateTime nowUtc)
    {
        DutySession.Complete(territoryId, nowUtc);
        ResetHandoff();
        nextCombatAttemptUtc = DateTime.MinValue;
    }

    // Called on every framework frame, before Mogtome's two-second throttle.
    // Returns a confirmed exit for the engine to consume on its next update.
    internal bool ObserveReadiness(bool inDuty,
        (uint TerritoryTypeId, uint ContentFinderConditionId) identity,
        AdsHandoffReadinessConditions conditions, DateTime nowUtc)
    {
        // A duty flag may precede GameMain leaving the overworld. Do not bind
        // the session to that outgoing territory while its CFC is still zero.
        var sessionTerritory = inDuty && identity.ContentFinderConditionId == 0 ? 0 : identity.TerritoryTypeId;
        var ended = DutySession.Observe(inDuty, sessionTerritory,
            identity.ContentFinderConditionId, conditions, nowUtc);
        if (ended)
            ResetDutyTracking();

        if (!inDuty)
            trackedInDuty = false;

        if (cancelled || !inDuty || DutySession.IsCompleted)
            ResetHandoff();
        else
            handoffState.ObserveReadiness(conditions);

        return ended && conditions.IsLoggedIn && !inDuty;
    }

    internal DutyStartupResult Update(bool inDuty,
        (uint TerritoryTypeId, uint ContentFinderConditionId) identity,
        AdsHandoffReadinessConditions conditions, DateTime now, bool openerActive = false,
        Func<bool>? tryStartOpener = null)
    {
        ObserveReadiness(inDuty, identity, conditions, now);
        if (cancelled || DutySession.IsCompleted)
        {
            StatusText = "Duty startup cancelled.";
            return DutyStartupResult.Cancelled;
        }

        if (!inDuty)
            return Pending("waiting for duty");

        if (inDuty != trackedInDuty
            || identity.TerritoryTypeId != trackedDutyTerritoryId
            || identity.ContentFinderConditionId != trackedDutyContentFinderConditionId)
        {
            trackedInDuty = inDuty;
            trackedDutyTerritoryId = identity.TerritoryTypeId;
            trackedDutyContentFinderConditionId = identity.ContentFinderConditionId;
            dutyEnteredUtc = now;
            lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
            ResetHandoff();
        }

        if (openerActive)
        {
            handoffState.ResetCountdown();
            return Pending("experimental opener active");
        }

        if (!DutyState.IsMogtomeDutyTerritory(identity.TerritoryTypeId)
            || identity.ContentFinderConditionId == 0)
        {
            ResetHandoff();
            return Pending("waiting for live duty territory/CFC identity");
        }

        var ads = useAds();
        var ownership = ads
            ? adsDutyIpcService.Refresh(true, identity.TerritoryTypeId, identity.ContentFinderConditionId)
            : AdsDutyOwnershipSnapshot.Empty;

        // Existing /ads outside or manual ownership is authoritative too. Once
        // confirmed, combat recovery must never restart the accepted backend.
        if (ads && conditions.IsLoggedIn && ownership.IsOwned && ownership.StatusReadable)
        {
            BackendConfirmed = true;
            DutySession.ObserveAdsControl();
            handoffRequestedAtUtc = DateTime.MinValue;
            nextHandoffAttemptUtc = DateTime.MinValue;
        }

        if (IsConfirmed)
            return DutyStartupResult.Confirmed;

        if (ads && !BackendConfirmed && !ownership.IsOwned)
        {
            if (!ownership.AdsLoaded)
            {
                ResetHandoff();
                return Pending("ADS is not loaded");
            }

            var entry = adsDutyIpcService.CurrentDuty;
            if (entry is null || !entry.MatchesIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
            {
                ResetHandoff();
                return Pending(adsDutyIpcService.CurrentDutyDetail);
            }

            // Bind FrenRider's default four-player policy without adding settings.
            if (!IsSnapshotReady(entry))
            {
                ResetHandoff();
                return Pending($"{entry.DutyName} has ADS clearance {entry.ClearanceStatus} (M{entry.ClearanceLevel}); waiting for four-player M3 support");
            }
        }

        if (!BackendConfirmed && !ownership.IsOwned
            && handoffRequestedAtUtc != DateTime.MinValue
            && !AdsIntegrationPolicy.IsHandoffConfirmationPending(handoffRequestedAtUtc, now))
        {
            handoffRequestedAtUtc = DateTime.MinValue;
            handoffState.ResetCountdown();
        }

        var countdown = handoffState.Update(identity.TerritoryTypeId, identity.ContentFinderConditionId,
            now, 2, conditions, automaticSoloHandoff: false,
            ownershipConfirmed: BackendConfirmed);
        if (countdown.Blocker is not null)
            return Pending(countdown.Blocker);
        if (!countdown.IsReady)
            return Pending($"startup in {Math.Max(0, countdown.Remaining.TotalSeconds):F1}s of continuous readiness");

        // Existing ADS ownership bypasses only the backend timer seam, as in
        // FrenRider. Combat always waits for continuous player readiness above.
        if (!BackendConfirmed && !ownership.IsOwned && !IsReadyToStartInsideDuty(identity.TerritoryTypeId, now))
            return Pending("waiting for duty start seam");

        if (!BackendConfirmed && tryStartOpener?.Invoke() == true)
        {
            handoffState.ResetCountdown();
            return Pending("experimental opener active");
        }

        if (ads || BackendConfirmed)
        {
            if (!TryActivateCombat(now))
                return DutyStartupResult.Pending;
            if (BackendConfirmed)
                return DutyStartupResult.Confirmed;
        }

        if (ownership.IsOwned)
            return Pending("waiting for readable ADS ownership confirmation");

        if (AdsIntegrationPolicy.IsHandoffConfirmationPending(handoffRequestedAtUtc, now))
        {
            var remaining = AdsIntegrationPolicy.HandoffConfirmationTimeout - (now - handoffRequestedAtUtc);
            return Pending($"waiting {Math.Max(0, remaining.TotalSeconds):F1}s for ADS ownership confirmation");
        }

        if (!AdsIntegrationPolicy.CanAttemptHandoff(handoffRequestedAtUtc, nextHandoffAttemptUtc, now))
            return Pending($"handoff retry backoff until {nextHandoffAttemptUtc:HH:mm:ss}");

        handoffRequestedAtUtc = DateTime.MinValue;
        try
        {
            if (!ads)
            {
                if (!startAutoDuty())
                    return BackoffFailedHandoff(now, "/ad start was not handled");

                // AutoDuty's existing command adapter has no ownership endpoint.
                BackendConfirmed = true;
                return TryActivateCombat(now) ? DutyStartupResult.Confirmed : DutyStartupResult.Pending;
            }

            var request = adsDutyIpcService.RequestStartDutyFromInside();
            if (request.EndpointAvailable)
            {
                if (request.Accepted)
                    return AwaitHandoffConfirmation(now, "ADS.StartDutyFromInside accepted");

                return BackoffFailedHandoff(now, "ADS.StartDutyFromInside rejected; command fallback suppressed");
            }

            if (processCommand("/ads inside"))
                return AwaitHandoffConfirmation(now, "typed endpoint unavailable; sent /ads inside fallback");

            return BackoffFailedHandoff(now, "typed endpoint unavailable and /ads inside fallback failed");
        }
        catch (Exception ex)
        {
            return BackoffFailedHandoff(now, $"duty startup failed: {ex.Message}");
        }
    }

    private bool TryActivateCombat(DateTime now)
    {
        if (CombatActivated)
            return true;
        if (now < nextCombatAttemptUtc)
        {
            Pending($"combat recovery backoff until {nextCombatAttemptUtc:HH:mm:ss}");
            return false;
        }

        string failure;
        try
        {
            if (enableCombat())
            {
                CombatActivated = true;
                return true;
            }
            failure = combatFailure();
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        nextCombatAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        handoffState.ResetCountdown();
        Pending($"combat activation failed: {failure}; retrying after 5s and continuous readiness");
        logWarning($"[MOGTOME][Startup] {StatusText}");
        return false;
    }

    private DutyStartupResult AwaitHandoffConfirmation(DateTime now, string reason)
    {
        handoffRequestedAtUtc = now;
        nextHandoffAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        logInformation($"[MOGTOME][Startup] {reason}; waiting for authoritative ownership.");
        return Pending($"{reason}; waiting for authoritative ownership");
    }

    private DutyStartupResult BackoffFailedHandoff(DateTime now, string reason)
    {
        handoffRequestedAtUtc = DateTime.MinValue;
        nextHandoffAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        handoffState.ResetCountdown();
        logWarning($"[MOGTOME][Startup] {reason}.");
        return Pending($"{reason}; restarting readiness delay with 5s retry backoff");
    }

    private DutyStartupResult Pending(string reason)
    {
        StatusText = $"Duty startup pending: {reason}.";
        return DutyStartupResult.Pending;
    }

    private void ResetHandoff()
    {
        handoffRequestedAtUtc = DateTime.MinValue;
        nextHandoffAttemptUtc = DateTime.MinValue;
        handoffState.Reset();
    }

    private void ResetDutyTracking()
    {
        trackedInDuty = false;
        trackedDutyTerritoryId = 0;
        trackedDutyContentFinderConditionId = 0;
        dutyEnteredUtc = DateTime.MinValue;
        lastPraetoriumReadyWaitLogUtc = DateTime.MinValue;
        nextCombatAttemptUtc = DateTime.MinValue;
        CombatActivated = false;
        BackendConfirmed = false;
        ResetHandoff();
    }

    internal static bool IsSnapshotReady(AdsCurrentDutySnapshot snapshot)
        => snapshot.Category == AdsDutyCategory.FourMan && snapshot.ClearanceLevel >= 3;

    internal static bool IsInDuty()
        => Plugin.Condition[ConditionFlag.BoundByDuty]
            || Plugin.Condition[ConditionFlag.BoundByDuty56]
            || Plugin.Condition[ConditionFlag.BoundByDuty95];

    internal static AdsHandoffReadinessConditions ReadReadinessConditions()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        return new AdsHandoffReadinessConditions(
            Plugin.ClientState.IsLoggedIn,
            localPlayer is not null,
            localPlayer?.CurrentHp > 0,
            Plugin.Condition[ConditionFlag.Unconscious],
            Plugin.Condition[ConditionFlag.BetweenAreas],
            Plugin.Condition[ConditionFlag.WatchingCutscene],
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent],
            Plugin.Condition[ConditionFlag.WatchingCutscene78],
            Plugin.Condition[ConditionFlag.BetweenAreas51]);
    }

    internal static unsafe (uint TerritoryTypeId, uint ContentFinderConditionId) ReadLiveDutyIdentity()
    {
        try
        {
            var gameMain = GameMain.Instance();
            return gameMain is null
                ? (0, 0)
                : (gameMain->CurrentTerritoryTypeId, gameMain->CurrentContentFinderConditionId);
        }
        catch
        {
            return (0, 0);
        }
    }

    private bool IsReadyToStartInsideDuty(uint territoryTypeId, DateTime now)
    {
        var secondsSinceEnter = dutyEnteredUtc == DateTime.MinValue
            ? double.MaxValue
            : (now - dutyEnteredUtc).TotalSeconds;

        if (territoryTypeId != DutyState.PraetoriumTerritoryId)
            return true;

        var remainingTime = getDutyRemainingTime();
        if (remainingTime > 0f && remainingTime < DutyState.PraetoriumTimeLimit)
            return true;

        if (remainingTime > 0f)
        {
            if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
            {
                lastPraetoriumReadyWaitLogUtc = now;
                logInformation($"[MOGTOME][Startup] Praetorium entered but timer is still at {remainingTime:F0}s; waiting before starting backend.");
            }

            return false;
        }

        if (secondsSinceEnter < PraetoriumReadyFallbackSeconds)
            return false;

        if ((now - lastPraetoriumReadyWaitLogUtc).TotalSeconds >= 5.0)
        {
            lastPraetoriumReadyWaitLogUtc = now;
            logWarning("[MOGTOME][Startup] Praetorium timer never appeared; using fallback readiness window before starting backend.");
        }

        return true;
    }
}
