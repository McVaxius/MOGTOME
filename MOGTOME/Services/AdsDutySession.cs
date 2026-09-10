using System;
using MOGTOME.Models;

namespace MOGTOME.Services;

// Shared by ADS handoff and configured exit so dispatch cannot erase completion.
internal sealed class AdsDutySession
{
    private uint dutyTerritoryId;
    internal bool ResumedInsideDuty { get; set; }

    public DateTime EnteredAtUtc { get; private set; } = DateTime.MinValue;
    public DateTime CompletedAtUtc { get; private set; } = DateTime.MinValue;
    public bool IsCompleted => CompletedAtUtc != DateTime.MinValue;
    public bool HadAdsControl { get; private set; }
    public bool ExitTakeoverActive { get; private set; }
    public bool LeaveIssued { get; set; }

    public bool Observe(bool inDuty, uint territoryId, uint contentFinderConditionId,
        AdsHandoffReadinessConditions conditions, DateTime nowUtc)
    {
        // A dropped duty flag or missing identity during a cutscene is not an exit.
        var confirmedExit = !inDuty && territoryId != 0 && territoryId != dutyTerritoryId
                            && contentFinderConditionId == 0
                            && AdsIntegrationPolicy.GetHandoffReadinessBlocker(conditions) is null;
        if (!conditions.IsLoggedIn)
            return false;

        if (confirmedExit)
        {
            var hadSession = EnteredAtUtc != DateTime.MinValue || IsCompleted;
            Reset();
            return hadSession;
        }

        if (inDuty)
        {
            ObserveEntry(nowUtc);
            if (dutyTerritoryId == 0 && DutyState.IsSupportedDutyIdentity(territoryId, contentFinderConditionId))
                dutyTerritoryId = territoryId;
        }

        return false;
    }

    public void ObserveEntry(DateTime nowUtc)
    {
        if (EnteredAtUtc == DateTime.MinValue)
            EnteredAtUtc = nowUtc;
    }

    public void Start(uint territoryId, DateTime nowUtc)
    {
        Reset();
        dutyTerritoryId = territoryId;
        EnteredAtUtc = nowUtc;
    }

    public bool Complete(uint territoryId, DateTime nowUtc)
    {
        if (IsCompleted || EnteredAtUtc == DateTime.MinValue || territoryId != dutyTerritoryId
            || !DutyState.IsMogtomeDutyTerritory(territoryId)
            || (!ResumedInsideDuty && (nowUtc - EnteredAtUtc).TotalSeconds < 60))
            return false;

        CompletedAtUtc = nowUtc;
        return true;
    }

    public void ObserveAdsControl() => HadAdsControl = true;

    public bool TryTakeOverExit(bool configuredExit)
    {
        if (!configuredExit || !HadAdsControl || ExitTakeoverActive)
            return false;

        ExitTakeoverActive = true;
        return true;
    }

    private void Reset()
    {
        dutyTerritoryId = 0;
        EnteredAtUtc = DateTime.MinValue;
        CompletedAtUtc = DateTime.MinValue;
        HadAdsControl = false;
        ExitTakeoverActive = false;
        LeaveIssued = false;
        ResumedInsideDuty = false;
    }
}
