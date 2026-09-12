using MOGTOME.Localization;
using System;
using MOGTOME.Models;

namespace MOGTOME.Services;

internal readonly record struct AdsHandoffReadinessConditions(
    bool IsLoggedIn,
    bool HasLocalPlayer,
    bool IsPlayerAlive,
    bool IsUnconscious,
    bool IsBetweenAreas,
    bool IsWatchingCutscene,
    bool IsOccupiedInCutSceneEvent,
    bool IsWatchingCutscene78 = false,
    bool IsBetweenAreas51 = false,
    bool HasJob = true);

internal readonly record struct AdsHandoffCountdownState(
    uint TerritoryTypeId,
    uint ContentFinderConditionId,
    DateTime ReadySinceUtc);

internal readonly record struct AdsHandoffCountdownResult(
    AdsHandoffCountdownState State,
    bool IsReady,
    TimeSpan Remaining,
    UiText? BlockerMessage)
{
    internal string? Blocker => BlockerMessage?.English;
}

internal sealed class AdsHandoffState
{
    private AdsHandoffCountdownState countdownState;

    public bool IsCombatHeld { get; private set; }

    public void Reset()
    {
        ResetCountdown();
        IsCombatHeld = false;
    }

    public void ResetCountdown() => countdownState = default;

    // Called even on framework frames that skip normal services during loading.
    public void ObserveReadiness(AdsHandoffReadinessConditions conditions)
    {
        if (AdsIntegrationPolicy.GetHandoffReadinessBlocker(conditions) is not null)
            ResetCountdown();
    }

    public AdsHandoffCountdownResult Update(
        uint territoryTypeId,
        uint contentFinderConditionId,
        DateTime nowUtc,
        int delaySeconds,
        AdsHandoffReadinessConditions conditions,
        bool automaticSoloHandoff,
        bool ownershipConfirmed)
    {
        var result = AdsIntegrationPolicy.EvaluateHandoffCountdown(
            countdownState, territoryTypeId, contentFinderConditionId, nowUtc, delaySeconds, conditions);
        countdownState = result.State;
        IsCombatHeld = automaticSoloHandoff && (!result.IsReady || !ownershipConfirmed);
        return result;
    }
}

public static class AdsIntegrationPolicy
{
    public static readonly TimeSpan HandoffConfirmationTimeout = TimeSpan.FromSeconds(5);

    internal static UiText? GetDutyLeaveMessage(uint activeTerritory, bool inDuty,
        (uint TerritoryTypeId, uint ContentFinderConditionId) identity,
        AdsHandoffReadinessConditions conditions, bool inCombat, bool occupied)
    {
        if (!conditions.IsLoggedIn || !inDuty || identity.TerritoryTypeId != activeTerritory
            || !DutyState.IsSupportedDutyIdentity(identity.TerritoryTypeId, identity.ContentFinderConditionId))
            return Ui.M("Blocker_WaitingForMatchingDutyIdentity");
        if (!conditions.HasLocalPlayer || conditions.IsBetweenAreas || conditions.IsBetweenAreas51)
            return Ui.M("Blocker_Loading");
        if (inCombat) return Ui.M("Blocker_Combat");
        if (conditions.IsWatchingCutscene || conditions.IsWatchingCutscene78 || conditions.IsOccupiedInCutSceneEvent)
            return Ui.M("Blocker_Cutscene");
        if (occupied) return Ui.M("Blocker_OccupiedTransition");
        return null;
    }

    internal static string? GetDutyLeaveBlocker(uint activeTerritory, bool inDuty,
        (uint TerritoryTypeId, uint ContentFinderConditionId) identity,
        AdsHandoffReadinessConditions conditions, bool inCombat, bool occupied)
        => GetDutyLeaveMessage(activeTerritory, inDuty, identity, conditions, inCombat, occupied)?.English;

    internal static string? GetHandoffReadinessBlocker(AdsHandoffReadinessConditions conditions)
        => GetHandoffReadinessMessage(conditions)?.English;

    public static bool ShouldPauseDutySystems(bool handoffPending, bool runtimeOwned, bool exitTakeoverActive)
        => handoffPending || runtimeOwned || exitTakeoverActive;

    public static bool ShouldPauseExitSystem(bool handoffPending, bool runtimeOwned, bool exitTakeoverActive)
        => ShouldPauseDutySystems(handoffPending, runtimeOwned, exitTakeoverActive) && !exitTakeoverActive;

    public static bool IsHandoffConfirmationPending(DateTime requestedAtUtc, DateTime nowUtc)
        => requestedAtUtc != DateTime.MinValue
           && nowUtc - requestedAtUtc < HandoffConfirmationTimeout;

    public static bool CanAttemptHandoff(DateTime requestedAtUtc, DateTime nextAttemptUtc, DateTime nowUtc)
        => !IsHandoffConfirmationPending(requestedAtUtc, nowUtc)
           && nowUtc >= nextAttemptUtc;

    internal static AdsHandoffCountdownResult EvaluateHandoffCountdown(
        AdsHandoffCountdownState previous,
        uint territoryTypeId,
        uint contentFinderConditionId,
        DateTime nowUtc,
        int delaySeconds,
        AdsHandoffReadinessConditions conditions)
    {
        var blocker = GetHandoffReadinessMessage(conditions);
        if (blocker is not null)
        {
            return new AdsHandoffCountdownResult(
                new AdsHandoffCountdownState(territoryTypeId, contentFinderConditionId, DateTime.MinValue),
                false,
                TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 2, 300)),
                blocker);
        }

        var identityChanged = previous.TerritoryTypeId != territoryTypeId
                              || previous.ContentFinderConditionId != contentFinderConditionId;
        var readySinceUtc = identityChanged
                            || previous.ReadySinceUtc == DateTime.MinValue
                            || nowUtc < previous.ReadySinceUtc
            ? nowUtc
            : previous.ReadySinceUtc;
        var delay = TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 2, 300));
        var remaining = delay - (nowUtc - readySinceUtc);
        var isReady = remaining <= TimeSpan.Zero;

        return new AdsHandoffCountdownResult(
            new AdsHandoffCountdownState(territoryTypeId, contentFinderConditionId, readySinceUtc),
            isReady,
            isReady ? TimeSpan.Zero : remaining,
            null);
    }

    internal static UiText? GetHandoffReadinessMessage(AdsHandoffReadinessConditions conditions)
    {
        if (!conditions.IsLoggedIn)
            return Ui.M("Blocker_WaitingForLogin");
        if (!conditions.HasLocalPlayer)
            return Ui.M("Blocker_WaitingForLocalPlayer");
        if (!conditions.HasJob)
            return Ui.M("Blocker_WaitingForLocalPlayerJob");
        if (conditions.IsUnconscious)
            return Ui.M("Blocker_WaitingForUnconsciousStateToClear");
        if (!conditions.IsPlayerAlive)
            return Ui.M("Blocker_WaitingForLocalPlayerToBeAlive");
        if (conditions.IsBetweenAreas || conditions.IsBetweenAreas51)
            return Ui.M("Blocker_WaitingForAreaTransitionToFinish");
        if (conditions.IsWatchingCutscene || conditions.IsWatchingCutscene78)
            return Ui.M("Blocker_WaitingForCutsceneToFinish");
        if (conditions.IsOccupiedInCutSceneEvent)
            return Ui.M("Blocker_WaitingForCutsceneEventToFinish");

        return null;
    }
}
