using MOGTOME.Localization;
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
    Func<UiText> combatFailure,
    Func<string, bool> processCommand,
    Func<float> getDutyRemainingTime,
    Action<string> logInformation,
    Action<string> logWarning,
    Action? invalidateCombat = null)
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
    private bool exitRequested;
    private int generation;

    internal AdsDutySession DutySession { get; private set; } = new();
    internal bool CombatActivated { get; private set; }
    internal bool BackendConfirmed { get; private set; }
    internal bool IsConfirmed => CombatActivated && BackendConfirmed;
    internal string StatusText => Status.English;
    internal UiText Status { get; private set; } = Ui.M("Startup_WaitingForDutyReadiness");

    internal void ResetSession(bool resumedInsideDuty = false)
    {
        ++generation;
        DutySession = new AdsDutySession { ResumedInsideDuty = resumedInsideDuty };
        cancelled = false;
        exitRequested = false;
        ResetDutyTracking();
    }

    internal void Cancel()
    {
        ++generation;
        cancelled = true;
        ResetHandoff();
        nextCombatAttemptUtc = DateTime.MinValue;
    }

    internal void HoldForExit()
    {
        ++generation;
        exitRequested = true;
        ResetHandoff();
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

    internal bool OnDutyCompleted(uint territoryId, DateTime nowUtc)
    {
        if (cancelled || !DutySession.Complete(territoryId, nowUtc))
            return false;
        ++generation;
        ResetHandoff();
        nextCombatAttemptUtc = DateTime.MinValue;
        return true;
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

        if (cancelled || exitRequested || !inDuty || DutySession.IsCompleted)
            ResetHandoff();
        else
        {
            // Missing LocalPlayer during loading is not evidence of death.
            if (CombatActivated && conditions.IsLoggedIn &&
                (conditions.IsUnconscious || conditions.HasLocalPlayer && !conditions.IsPlayerAlive))
            {
                CombatActivated = false;
                handoffState.ResetCountdown();
                invalidateCombat?.Invoke();
                Pending(Ui.M("Startup_CombatRecoveryAfterDeathWaitingForContinuous"));
            }
            handoffState.ObserveReadiness(conditions);
        }

        return ended && conditions.IsLoggedIn && !inDuty;
    }

    internal DutyStartupResult Update(bool inDuty,
        (uint TerritoryTypeId, uint ContentFinderConditionId) identity,
        AdsHandoffReadinessConditions conditions, DateTime now, bool openerActive = false,
        Func<bool>? tryStartOpener = null)
    {
        ObserveReadiness(inDuty, identity, conditions, now);
        var operation = generation;
        if (cancelled || exitRequested || DutySession.IsCompleted)
        {
            Status = Ui.M("Startup_DutyStartupCancelled");
            return DutyStartupResult.Cancelled;
        }

        if (!inDuty)
            return Pending(Ui.M("Startup_WaitingForDuty"));

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
            return Pending(Ui.M("Startup_ExperimentalOpenerActive"));
        }

        if (!DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
        {
            ResetHandoff();
            return Pending(Ui.M("Startup_WaitingForLiveDutyTerritoryCFCIdentity"));
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
                return Pending(Ui.M("Startup_ADSIsNotLoaded"));
            }

            var entry = adsDutyIpcService.CurrentDuty;
            if (entry is null || !entry.MatchesIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
            {
                ResetHandoff();
                return Pending(adsDutyIpcService.CurrentDutyMessage);
            }

            // Bind FrenRider's default four-player policy without adding settings.
            if (!IsSnapshotReady(entry))
            {
                ResetHandoff();
                return Pending(Ui.M("Startup_HasADSClearanceMWaitingForFour", Ui.Duty(entry.TerritoryTypeId, entry.DutyName), entry.ClearanceStatus, entry.ClearanceLevel));
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
            return Pending(countdown.BlockerMessage!);
        if (!countdown.IsReady)
            return Pending(Ui.M("Startup_StartupInSOfContinuousReadiness", Math.Max(0, countdown.Remaining.TotalSeconds)));

        // Existing ADS ownership bypasses only the backend timer seam, as in
        // FrenRider. Combat always waits for continuous player readiness above.
        if (!BackendConfirmed && !ownership.IsOwned && !IsReadyToStartInsideDuty(identity.TerritoryTypeId, now))
            return Pending(Ui.M("Startup_WaitingForDutyStartSeam"));

        if (!BackendConfirmed && tryStartOpener?.Invoke() == true)
        {
            handoffState.ResetCountdown();
            return Pending(Ui.M("Startup_ExperimentalOpenerActive"));
        }

        if (ads || BackendConfirmed)
        {
            if (!TryActivateCombat(now))
                return DutyStartupResult.Pending;
            if (BackendConfirmed)
                return DutyStartupResult.Confirmed;
        }

        if (ownership.IsOwned)
            return Pending(Ui.M("Startup_WaitingForReadableADSOwnershipConfirmation"));

        if (AdsIntegrationPolicy.IsHandoffConfirmationPending(handoffRequestedAtUtc, now))
        {
            var remaining = AdsIntegrationPolicy.HandoffConfirmationTimeout - (now - handoffRequestedAtUtc);
            return Pending(Ui.M("Startup_WaitingSForADSOwnershipConfirmation", Math.Max(0, remaining.TotalSeconds)));
        }

        if (!AdsIntegrationPolicy.CanAttemptHandoff(handoffRequestedAtUtc, nextHandoffAttemptUtc, now))
            return Pending(Ui.M("Startup_HandoffRetryBackoffUntil", nextHandoffAttemptUtc));

        handoffRequestedAtUtc = DateTime.MinValue;
        try
        {
            if (!ads)
            {
                var accepted = startAutoDuty();
                if (!IsCurrent(operation)) return DutyStartupResult.Cancelled;
                if (!accepted)
                    return BackoffFailedHandoff(now, Ui.M("Startup_AdStartWasNotHandled"));

                // AutoDuty's existing command adapter has no ownership endpoint.
                BackendConfirmed = true;
                return TryActivateCombat(now) ? DutyStartupResult.Confirmed : DutyStartupResult.Pending;
            }

            var request = adsDutyIpcService.RequestStartDutyFromInside();
            if (!IsCurrent(operation)) return DutyStartupResult.Cancelled;
            if (request.EndpointAvailable)
            {
                if (request.Accepted)
                    return AwaitHandoffConfirmation(now, Ui.M("Startup_ADSStartDutyFromInsideAccepted"));

                return BackoffFailedHandoff(now, Ui.M("Startup_ADSStartDutyFromInsideRejectedCommandFallbackSuppressed"));
            }

            var handled = processCommand("/ads inside");
            if (!IsCurrent(operation)) return DutyStartupResult.Cancelled;
            if (handled)
                return AwaitHandoffConfirmation(now, Ui.M("Startup_TypedEndpointUnavailableSentAdsInsideFallback"));

            return BackoffFailedHandoff(now, Ui.M("Startup_TypedEndpointUnavailableAndAdsInsideFallback"));
        }
        catch (Exception ex)
        {
            if (!IsCurrent(operation)) return DutyStartupResult.Cancelled;
            return BackoffFailedHandoff(now, Ui.M("Startup_DutyStartupFailed", ex.Message));
        }
    }

    private bool TryActivateCombat(DateTime now)
    {
        var operation = generation;
        if (!IsCurrent(operation)) return false;
        if (CombatActivated)
            return true;
        if (now < nextCombatAttemptUtc)
        {
            Pending(Ui.M("Startup_CombatRecoveryBackoffUntil", nextCombatAttemptUtc));
            return false;
        }

        UiText failure;
        try
        {
            var activated = enableCombat();
            if (!IsCurrent(operation)) return false;
            if (activated)
            {
                CombatActivated = true;
                return true;
            }
            failure = combatFailure();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(operation)) return false;
            failure = ex.Message;
        }

        nextCombatAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        handoffState.ResetCountdown();
        Pending(Ui.M("Startup_CombatActivationFailedRetryingAfterSAnd", failure));
        logWarning($"[MOGTOME][Startup] {StatusText}");
        return false;
    }

    private bool IsCurrent(int operation)
        => operation == generation && !cancelled && !exitRequested && !DutySession.IsCompleted;

    private DutyStartupResult AwaitHandoffConfirmation(DateTime now, UiText reason)
    {
        handoffRequestedAtUtc = now;
        nextHandoffAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        logInformation($"[MOGTOME][Startup] {reason}; waiting for authoritative ownership.");
        return Pending(Ui.M("Startup_WaitingForAuthoritativeOwnership", reason));
    }

    private DutyStartupResult BackoffFailedHandoff(DateTime now, UiText reason)
    {
        handoffRequestedAtUtc = DateTime.MinValue;
        nextHandoffAttemptUtc = now + AdsIntegrationPolicy.HandoffConfirmationTimeout;
        handoffState.ResetCountdown();
        logWarning($"[MOGTOME][Startup] {reason}.");
        return Pending(Ui.M("Startup_RestartingReadinessDelayWithSRetryBackoff", reason));
    }

    private DutyStartupResult Pending(UiText reason)
    {
        Status = Ui.M("Startup_DutyStartupPending", reason);
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
            Plugin.Condition[ConditionFlag.BetweenAreas51],
            localPlayer?.ClassJob.RowId > 0);
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
