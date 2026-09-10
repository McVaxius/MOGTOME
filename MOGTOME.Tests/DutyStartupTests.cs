using System.Text.Json;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class DutyStartupTests
{
    private static readonly AdsHandoffReadinessConditions Ready = new(true, true, true, false, false, false, false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Account1LoadingRegressionRecoversWithoutCancellingSession(bool ads)
    {
        var run = new Run(ads);
        var missingPlayer = Ready with { HasLocalPlayer = false, IsPlayerAlive = false, IsBetweenAreas = true };
        Assert.Equal(DutyStartupResult.Pending, run.Frame(0, missingPlayer));
        var entered = run.Startup.DutySession.EnteredAtUtc;
        run.Startup.OnDutyStarted(run.Territory, run.Now);
        Assert.Equal(DutyStartupResult.Pending, run.Frame(30, missingPlayer));
        Assert.Equal(0, run.CombatAttempts);
        Assert.Equal(0, run.BackendRequests);
        Assert.Equal(entered, run.Startup.DutySession.EnteredAtUtc);
        Assert.False(run.Startup.DutySession.IsCompleted);

        run.Frame(31);
        run.Frame(32.999);
        Assert.Equal(0, run.CombatAttempts);
        run.Frame(33);
        Assert.Equal(1, run.CombatAttempts);
        Assert.Equal(1, run.BackendRequests);
        Assert.Equal(!ads, run.Startup.IsConfirmed);
        run.Owned = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(34));
        run.Startup.OnDutyStarted(run.Territory, run.Now);
        run.Frame(40);
        Assert.Equal(1, run.CombatAttempts);
        Assert.Equal(1, run.BackendRequests);
        Assert.Equal(entered, run.Startup.DutySession.EnteredAtUtc);
    }

    [Theory]
    [InlineData("OwnedStartOutside")]
    [InlineData("OwnedStartInside")]
    [InlineData("OwnedResumeInside")]
    [InlineData("Leaving")]
    public void LeaderFollowerAndManualOwnershipNeverRestartAds(string ownershipMode)
    {
        var run = new Run { TypedOwnershipAvailable = false, Owned = true, OwnershipMode = ownershipMode };
        run.Frame(0, Ready with { HasLocalPlayer = false });
        Assert.True(run.Startup.BackendConfirmed);
        Assert.Equal(0, run.CombatAttempts);
        run.Frame(10);
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(12));
        run.Startup.OnDutyStarted(run.Territory, run.Now);
        run.Frame(20);
        run.Owned = false; // Explicit release must not cause a new automatic start.
        run.Frame(30);
        Assert.Equal(0, run.BackendRequests);
        Assert.Empty(run.Commands);
        Assert.Equal(1, run.CombatAttempts);
    }

    [Theory]
    [InlineData("missing player")]
    [InlineData("dead")]
    [InlineData("unconscious")]
    [InlineData("loading")]
    [InlineData("loading51")]
    [InlineData("cutscene")]
    [InlineData("cutscene78")]
    [InlineData("cutscene event")]
    [InlineData("logout")]
    public void OneFrameInterruptionRestartsContinuousReadiness(string blocker)
    {
        var run = new Run();
        run.Frame(0);
        var interrupted = Blocked(blocker);
        // Only observe: reproduce a frame skipped by the engine's normal throttle.
        run.Startup.ObserveReadiness(true, run.Identity, interrupted, run.Now.AddSeconds(1));
        run.Frame(2);
        run.Frame(3.999);
        Assert.Equal(0, run.CombatAttempts);
        run.Frame(4);
        Assert.Equal(1, run.BackendRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidJobOrCombatFailureRetriesWithoutRestartingConfirmedBackend(bool ads)
    {
        var run = new Run(ads) { Owned = ads, CombatSucceeds = false };
        run.Frame(0);
        run.Frame(2);
        Assert.Contains("job unavailable", run.Startup.StatusText);
        Assert.True(run.Startup.BackendConfirmed);
        Assert.False(run.Startup.IsConfirmed);
        run.Startup.OnDutyStarted(run.Territory, run.Now); // Delayed event preserves success.
        run.Frame(3);
        run.Frame(6.999);
        Assert.Equal(1, run.CombatAttempts);
        run.CombatSucceeds = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(7));
        run.Frame(20);
        Assert.Equal(2, run.CombatAttempts);
        Assert.Equal(ads ? 0 : 1, run.BackendRequests);
    }

    [Fact]
    public void CombatFailureBeforeAdsHandoffRetainsSessionAndRecovers()
    {
        var run = new Run { CombatThrows = true };
        run.Frame(0);
        run.Frame(2);
        Assert.Equal(0, run.BackendRequests);
        Assert.False(run.Startup.IsConfirmed);
        run.Frame(3);
        run.CombatThrows = false;
        run.Frame(7);
        Assert.Equal(1, run.BackendRequests);
        run.Owned = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(8));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectionUsesFiveSecondBackoffAndRetainsSuccessfulCombat(bool ads)
    {
        var run = new Run(ads) { AcceptStart = false };
        run.Frame(0);
        run.Frame(2);
        run.Frame(3);
        run.Frame(6.999);
        Assert.Equal(1, run.BackendRequests);
        Assert.Empty(run.Commands);
        run.AcceptStart = true;
        run.Frame(7);
        run.Owned = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(8));
        Assert.Equal(2, run.BackendRequests);
        Assert.Equal(1, run.CombatAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingTypedStartEndpointUsesCommandFallbackOnlyThen(bool commandAccepted)
    {
        var run = new Run { StartEndpointAvailable = false, CommandAccepted = commandAccepted };
        run.Frame(0);
        run.Frame(2);
        Assert.Equal(new[] { "/ads inside" }, run.Commands);
        Assert.False(run.Startup.BackendConfirmed);
        run.Frame(3);
        run.Frame(6.999);
        Assert.Single(run.Commands);
        if (commandAccepted)
        {
            run.Owned = true;
            // Preserve FrenRider's 250 ms ownership poll interval.
            Assert.Equal(DutyStartupResult.Confirmed, run.Frame(7.25));
            Assert.Single(run.Commands);
        }
        else
        {
            run.Frame(7);
            Assert.Equal(2, run.Commands.Count);
        }
    }

    [Fact]
    public void ConfirmationTimeoutRestartsTwoSecondReadinessBeforeRetry()
    {
        var run = new Run();
        run.Frame(0);
        run.Frame(2);
        run.Frame(6.999);
        Assert.Equal(1, run.BackendRequests);
        run.Frame(7);
        run.Frame(8.999);
        Assert.Equal(1, run.BackendRequests);
        run.Frame(9);
        Assert.Equal(2, run.BackendRequests);
        Assert.Equal(1, run.CombatAttempts);
        Assert.False(run.Startup.IsConfirmed);
    }

    [Fact]
    public void OwnershipConfirmationDuringLoadingDoesNotBypassCombatReadiness()
    {
        var run = new Run { Owned = true, CombatSucceeds = false };
        run.Frame(0);
        run.Frame(2);
        run.Frame(20, Ready with { IsBetweenAreas51 = true });
        run.CombatSucceeds = true;
        run.Frame(21);
        run.Frame(22.999);
        Assert.Equal(1, run.CombatAttempts);
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(23));
        Assert.Equal(0, run.BackendRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PraetoriumMissingTimerAllowanceIsFifteenSeconds(bool ads)
    {
        var run = new Run(ads) { Timer = 0 };
        run.Frame(0);
        run.Frame(2);
        run.Frame(14.999);
        Assert.Equal(0, run.BackendRequests);
        run.Frame(15);
        Assert.Equal(1, run.BackendRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PraetoriumFullTimerAndReadinessBlockersCannotBeBypassedByTime(bool ads)
    {
        var run = new Run(ads) { Timer = 7200 };
        run.Frame(0);
        run.Frame(60);
        Assert.Equal(0, run.BackendRequests);
        run.Timer = 0;
        run.Frame(120, Ready with { IsWatchingCutscene78 = true });
        Assert.Equal(0, run.BackendRequests);
        run.Frame(121);
        run.Frame(123);
        Assert.Equal(1, run.BackendRequests);
    }

    [Fact]
    public void DecumanaDoesNotUsePraetoriumTimerGate()
    {
        var run = new Run { Territory = 1048, Cfc = 830, Timer = 0 };
        run.Frame(0);
        run.Frame(2);
        Assert.Equal(1, run.BackendRequests);
    }

    [Fact]
    public void StaleAdsDutyIdentityBlocksStartupUntilValidated()
    {
        var run = new Run { AdsTerritoryOverride = 1048 };
        run.Frame(0);
        run.Frame(20);
        Assert.Equal(0, run.BackendRequests);
        run.AdsTerritoryOverride = null;
        run.Frame(21);
        run.Frame(23);
        Assert.Equal(1, run.BackendRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopOrCompletionCancelsPendingStartupBeforeAnyRetry(bool stop)
    {
        var run = new Run();
        run.Startup.DutySession.ResumedInsideDuty = true;
        run.Frame(0);
        run.Frame(2);
        if (stop)
            run.Startup.Cancel();
        else
            run.Startup.OnDutyCompleted(run.Territory, run.Now);

        run.Startup.OnDutyStarted(run.Territory, run.Now.AddSeconds(1));
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(20));
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(40));
        Assert.Equal(1, run.BackendRequests);
        Assert.Equal(1, run.CombatAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletionOrStopBeforeReadyDoesNotActivateCombat(bool stop)
    {
        var run = new Run();
        run.Startup.DutySession.ResumedInsideDuty = true;
        run.Frame(0, Ready with { HasLocalPlayer = false });
        if (stop)
            run.Startup.Cancel();
        else
            run.Startup.OnDutyCompleted(run.Territory, run.Now);
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(30));
        Assert.Equal(0, run.CombatAttempts);
        Assert.Equal(0, run.BackendRequests);
    }

    [Theory]
    [InlineData("missing player")]
    [InlineData("loading")]
    [InlineData("loading51")]
    [InlineData("cutscene")]
    [InlineData("cutscene78")]
    [InlineData("cutscene event")]
    public void MissingDutyInformationDuringLoadingIsNotAnExit(string blocker)
    {
        var run = new Run();
        run.Startup.DutySession.ResumedInsideDuty = true;
        run.Frame(0);
        run.Frame(2);
        run.Owned = true;
        run.Frame(3);
        run.Startup.OnDutyCompleted(run.Territory, run.Now);
        var entered = run.Startup.DutySession.EnteredAtUtc;
        var completed = run.Startup.DutySession.CompletedAtUtc;
        Assert.False(run.Startup.ObserveReadiness(false, (999, 0), Blocked(blocker), run.Now.AddSeconds(1)));
        Assert.False(run.Startup.ObserveReadiness(false, (0, 0), Ready, run.Now.AddSeconds(2)));
        Assert.False(run.Startup.ObserveReadiness(false, (run.Territory, 0), Ready, run.Now.AddSeconds(3)));
        Assert.Equal(completed, run.Startup.DutySession.CompletedAtUtc);
        Assert.Equal(entered, run.Startup.DutySession.EnteredAtUtc);
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(30));
        Assert.Equal(1, run.BackendRequests);
        Assert.True(run.Startup.ObserveReadiness(false, (999, 0), Ready, run.Now.AddSeconds(1)));
        Assert.False(run.Startup.DutySession.IsCompleted);
    }

    [Fact]
    public void ConsecutiveRunsStartOnceEachWithoutDutyStartedEvents()
    {
        var run = new Run();
        for (var index = 0; index < 3; index++)
        {
            var start = index * 100;
            run.Owned = false;
            run.Frame(start);
            run.Frame(start + 2);
            run.Owned = true;
            run.Frame(start + 3);
            run.Startup.OnDutyStarted(run.Territory, run.Now);
            Assert.Equal(DutyStartupResult.Confirmed, run.Frame(start + 10));
            run.Frame(start + 60);
            Assert.True(run.Startup.OnDutyCompleted(run.Territory, run.Now));
            Assert.Equal(DutyStartupResult.Cancelled, run.Frame(start + 70));
            Assert.True(run.Startup.ObserveReadiness(false, (999, 0), Ready, run.Now.AddSeconds(1)));
        }
        Assert.Equal(3, run.BackendRequests);
        Assert.Equal(3, run.CombatAttempts);
    }

    [Fact]
    public void ExperimentalOpenerKeepsExclusiveControlUntilHandoff()
    {
        var run = new Run();
        run.TryStartOpener = () => true;
        run.Frame(0);
        run.Frame(2);
        run.OpenerActive = true;
        run.Frame(20);
        Assert.Equal(0, run.CombatAttempts);
        Assert.Equal(0, run.BackendRequests);
        run.OpenerActive = false;
        run.TryStartOpener = () => false;
        run.Frame(21);
        run.Frame(23);
        Assert.Equal(1, run.BackendRequests);
        run.Owned = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(24));
    }

    [Fact]
    public void DutyFlagBeforeOverworldIdentitySettlesDoesNotBindSessionToOverworld()
    {
        var run = new Run();
        run.Startup.ObserveReadiness(true, (129, 0), Ready, run.Now);
        run.Frame(1);
        run.Frame(3);
        Assert.False(run.Startup.ObserveReadiness(false, (1044, 0), Ready, run.Now.AddSeconds(1)));
        Assert.True(run.Startup.ObserveReadiness(false, (129, 0), Ready, run.Now.AddSeconds(2)));
    }

    [Fact]
    public void ThrowingCommandAdapterUsesNonfatalBackoff()
    {
        var run = new Run { StartEndpointAvailable = false, CommandThrows = true };
        run.Frame(0);
        Assert.Equal(DutyStartupResult.Pending, run.Frame(2));
        Assert.Contains("command unavailable", run.Startup.StatusText);
        run.Frame(3);
        run.Frame(6.999);
        Assert.Single(run.Commands);
        run.CommandThrows = false;
        run.Frame(7);
        Assert.Equal(2, run.Commands.Count);
        run.Owned = true;
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(8));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedDeathRaiseAndEntranceRespawnRestoreCombatWithoutBackendRestart(bool ads)
    {
        var run = new Run(ads);
        run.Frame(0);
        run.Frame(2);
        run.Owned = true;
        run.Frame(3);
        var entered = run.Startup.DutySession.EnteredAtUtc;
        for (var death = 0; death < 3; death++)
        {
            var time = 10 + death * 20;
            // Observe only, as a death may be shorter than the engine throttle.
            run.Startup.ObserveReadiness(true, run.Identity, Ready with { IsUnconscious = true }, run.Now.AddSeconds(1));
            Assert.False(run.Startup.CombatActivated);
            Assert.True(run.Startup.BackendConfirmed);
            run.Frame(time, Ready with { IsBetweenAreas51 = true, HasLocalPlayer = false });
            run.Frame(time + 1);
            run.Frame(time + 2.999);
            Assert.Equal(death + 1, run.CombatAttempts);
            Assert.Equal(DutyStartupResult.Confirmed, run.Frame(time + 3));
            Assert.Equal(entered, run.Startup.DutySession.EnteredAtUtc);
            Assert.False(run.Startup.DutySession.IsCompleted);
        }
        Assert.Equal(4, run.CombatAttempts);
        Assert.Equal(3, run.CombatInvalidations);
        Assert.Equal(1, run.BackendRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingPlayerIsNotDeathAndFailedRestorationRetainsAcceptedBackend(bool ads)
    {
        var run = new Run(ads);
        run.Frame(0);
        run.Frame(2);
        run.Owned = true;
        run.Frame(3);
        run.Frame(4, Ready with { HasLocalPlayer = false, IsPlayerAlive = false });
        Assert.True(run.Startup.CombatActivated);
        Assert.Equal(0, run.CombatInvalidations);
        run.Frame(5, Ready with { IsPlayerAlive = false });
        run.Frame(6, Ready with { HasJob = false });
        Assert.Contains("job", run.Startup.StatusText);
        run.CombatSucceeds = false;
        run.Frame(7);
        run.Frame(9);
        Assert.Equal(2, run.CombatAttempts);
        run.Frame(10);
        run.Startup.ObserveReadiness(true, run.Identity, Ready with { IsWatchingCutscene78 = true }, run.Now.AddSeconds(1));
        run.Frame(12);
        run.CombatSucceeds = true;
        run.Frame(13.999);
        Assert.Equal(2, run.CombatAttempts);
        Assert.Equal(DutyStartupResult.Confirmed, run.Frame(14));
        Assert.Equal(1, run.BackendRequests);
        Assert.Equal(3, run.CombatAttempts);
    }

    [Fact]
    public void CompletionMustMatchSupportedAttemptAndFreshEntryGuardButAllowsResume()
    {
        var run = new Run();
        Assert.False(run.Startup.OnDutyCompleted(1044, run.Now));
        run.Frame(0);
        run.Frame(2);
        Assert.False(run.Startup.OnDutyCompleted(1044, run.Now));
        Assert.False(run.Startup.OnDutyCompleted(1048, run.Now.AddSeconds(100)));
        Assert.False(run.Startup.OnDutyCompleted(999, run.Now.AddSeconds(100)));
        Assert.True(run.Startup.CombatActivated);
        Assert.False(run.Startup.DutySession.IsCompleted);
        Assert.True(run.Startup.OnDutyCompleted(1044, run.Now.AddSeconds(60)));
        Assert.False(run.Startup.OnDutyCompleted(1044, run.Now.AddSeconds(61)));
        run.Startup.ResetSession(resumedInsideDuty: true);
        run.Frame(100);
        Assert.True(run.Startup.OnDutyCompleted(1044, run.Now.AddSeconds(1)));
    }

    [Fact]
    public void BailoutHoldsRecoveryButCanStillAcceptVerifiedCompletion()
    {
        var run = new Run();
        run.Frame(0);
        run.Frame(2);
        run.Startup.HoldForExit();
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(30, Ready with { IsPlayerAlive = false }));
        Assert.False(run.Startup.DutySession.IsCompleted);
        Assert.True(run.Startup.OnDutyCompleted(1044, run.Now.AddSeconds(60)));
        Assert.Equal(1, run.CombatAttempts);
        Assert.Equal(1, run.BackendRequests);
    }

    [Fact]
    public void LogoutDoesNotEraseAttemptOrEstablishSuccess()
    {
        var run = new Run();
        run.Frame(0);
        run.Frame(2);
        var entered = run.Startup.DutySession.EnteredAtUtc;
        Assert.False(run.Startup.ObserveReadiness(false, (0, 0), Ready with { IsLoggedIn = false }, run.Now.AddSeconds(10)));
        Assert.Equal(entered, run.Startup.DutySession.EnteredAtUtc);
        Assert.False(run.Startup.DutySession.IsCompleted);
        Assert.True(run.Startup.ObserveReadiness(false, (129, 0), Ready, run.Now.AddSeconds(20)));
    }

    [Fact]
    public void StopDuringCombatActivationCannotConfirmOrStartBackend()
    {
        var run = new Run();
        run.DuringCombat = () => run.Startup.Cancel();
        run.Frame(0);
        run.Frame(2);
        Assert.False(run.Startup.CombatActivated);
        Assert.Equal(0, run.BackendRequests);
        Assert.Equal(DutyStartupResult.Cancelled, run.Frame(20));
    }

    [Fact]
    public void StopDuringRecoveryPreventsLaterActivation()
    {
        var run = new Run { Owned = true };
        run.Frame(0);
        run.Frame(2);
        run.Frame(3, Ready with { IsPlayerAlive = false });
        run.Frame(4);
        run.Startup.Cancel();
        run.Frame(20);
        Assert.Equal(1, run.CombatAttempts);
        Assert.Equal(0, run.BackendRequests);
    }

    [Fact]
    public void SupportedTerritoryWithWrongCfcRemainsPending()
    {
        var run = new Run { Territory = 1044, Cfc = 830 };
        run.Frame(0);
        Assert.Equal(DutyStartupResult.Pending, run.Frame(200));
        Assert.Equal(0, run.CombatAttempts);
        Assert.Equal(0, run.BackendRequests);
        Assert.False(run.Startup.OnDutyCompleted(1044, run.Now));
    }

    private static AdsHandoffReadinessConditions Blocked(string blocker) => blocker switch
    {
        "missing player" => Ready with { HasLocalPlayer = false },
        "dead" => Ready with { IsPlayerAlive = false },
        "unconscious" => Ready with { IsUnconscious = true },
        "loading" => Ready with { IsBetweenAreas = true },
        "loading51" => Ready with { IsBetweenAreas51 = true },
        "cutscene" => Ready with { IsWatchingCutscene = true },
        "cutscene78" => Ready with { IsWatchingCutscene78 = true },
        "cutscene event" => Ready with { IsOccupiedInCutSceneEvent = true },
        "logout" => Ready with { IsLoggedIn = false },
        _ => throw new ArgumentOutOfRangeException(nameof(blocker)),
    };

    private sealed class Run
    {
        private readonly DateTime epoch = new(2026, 9, 9, 18, 6, 16, DateTimeKind.Utc);
        internal DateTime Now;
        internal uint Territory = 1044;
        internal uint Cfc = 16;
        internal uint? AdsTerritoryOverride;
        internal bool Owned;
        internal bool TypedOwnershipAvailable = true;
        internal string OwnershipMode = "OwnedStartOutside";
        internal bool StartEndpointAvailable = true;
        internal bool AcceptStart = true;
        internal bool CommandAccepted = true;
        internal bool CommandThrows;
        internal bool CombatSucceeds = true;
        internal bool CombatThrows;
        internal float Timer = 7190;
        internal bool OpenerActive;
        internal Func<bool>? TryStartOpener;
        internal int BackendRequests;
        internal int CombatAttempts;
        internal int CombatInvalidations;
        internal Action? DuringCombat;
        internal List<string> Commands = [];
        internal DutyStartupService Startup;
        internal (uint TerritoryTypeId, uint ContentFinderConditionId) Identity => (Territory, Cfc);

        internal Run(bool ads = true)
        {
            Now = epoch;
            var ipc = new AdsDutyIpcService(() => true,
                () => TypedOwnershipAvailable ? Owned : throw new InvalidOperationException("unavailable"),
                () => JsonSerializer.Serialize(new
                {
                    inInstancedDuty = true,
                    ownershipMode = Owned ? OwnershipMode : "Observing",
                    hasCatalogMetadata = true,
                    duty = "Test duty",
                    territoryTypeId = AdsTerritoryOverride ?? Territory,
                    contentFinderConditionId = Cfc,
                    dutyCategory = "FourMan",
                    supportLevel = "ActiveSupported",
                    clearanceStatus = "FourPlayerSyncCleared",
                }),
                () =>
                {
                    BackendRequests++;
                    return StartEndpointAvailable ? AcceptStart : throw new InvalidOperationException("missing endpoint");
                }, () => Now);
            Startup = new DutyStartupService(ipc, () => ads,
                () => { BackendRequests++; return AcceptStart; },
                () =>
                {
                    CombatAttempts++;
                    DuringCombat?.Invoke();
                    return CombatThrows ? throw new InvalidOperationException("combat unavailable") : CombatSucceeds;
                },
                () => "job unavailable",
                command =>
                {
                    Commands.Add(command);
                    return CommandThrows ? throw new InvalidOperationException("command unavailable") : CommandAccepted;
                },
                () => Timer, _ => { }, _ => { }, () => CombatInvalidations++);
        }

        internal DutyStartupResult Frame(double seconds, AdsHandoffReadinessConditions? conditions = null)
        {
            Now = epoch.AddSeconds(seconds);
            Startup.ObserveReadiness(true, Identity, conditions ?? Ready, Now);
            return Startup.Update(true, Identity, conditions ?? Ready, Now, OpenerActive, TryStartOpener);
        }
    }
}
