using MOGTOME.Services;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed class KeyboundLoopTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly FakeGame Game = new();
        internal readonly KeyboundTypingTests.FakeInput Input = new("a");
        internal readonly KeyboundBrawlerService Typer;
        internal readonly KeyboundEventLoop Loop;
        internal Fixture(int limit = 0, MOGTOME.Models.KeyboundPartyRole role = MOGTOME.Models.KeyboundPartyRole.Leader)
        {
            Typer = new(Input, (_, _) => 0);
            Loop = new(Game, Typer);
            Typer.Observe(true, false, 23, KeyboundEventLoop.Lobby);
            Assert.True(Typer.Start(new() { PartyRole = role, WalletTargetEnabled = false, RunLimitEnabled = limit > 0, RunLimit = limit }));
            Loop.Start(0, role);
        }
        internal void Tick(long now)
        {
            Typer.Observe(true, false, Game.Frame.Territory == KeyboundEventLoop.Arena ? 65u : 23u, Game.Frame.Territory, Game.Frame.Transitioning);
            Loop.Update(now); Typer.Update(null, now);
        }
        public void Dispose() { Loop.Stop(); Typer.Dispose(); }
    }
    private sealed class FakeGame : IKeyboundLoopGame
    {
        internal KeyboundLoopFrame Frame = new(KeyboundEventLoop.Lobby, false, false, false, false, false, false, false, false, false);
        internal int Registers, Commences, Leaves, Approaches, Cancels, Heartbeats, Cleanups, LobbyTrips;
        internal bool RegisterAccepted = true;
        public KeyboundLoopFrame Read() => Frame;
        public bool EnsureLobby() { LobbyTrips++; return true; }
        public bool ApproachRegistrar() { Approaches++; return true; }
        public bool Register() { Registers++; return RegisterAccepted; }
        public bool Commence() { Commences++; return true; }
        public bool LeaveResults() { Leaves++; return true; }
        public void KeepActive() => Heartbeats++;
        public void CancelQueue() => Cancels++;
        public void StopMovement() => Cleanups++;
    }

    [Theory]
    [InlineData(129u)]
    [InlineData(KeyboundEventLoop.Lobby)]
    public void PremadeMemberWaitsInPlaceAndAcceptsEntryWithoutTravelRegistrationOrQueueCancellation(uint territory)
    {
        using var f = new Fixture(role: MOGTOME.Models.KeyboundPartyRole.Member);
        f.Game.Frame = f.Game.Frame with { Territory = territory, PartyRole = MOGTOME.Models.BlundervilleRole.Member };
        f.Tick(0); Assert.Equal(0, f.Game.Approaches);
        Assert.Equal("BV_MemberWait", f.Loop.Status.Key);
        f.Game.Frame = f.Game.Frame with { Entry = true }; f.Tick(100);
        Assert.Equal(0, f.Game.Registers);
        f.Game.Frame = f.Game.Frame with { Entry = false, Pop = true, PopMatches = true }; f.Tick(200);
        Assert.Equal(1, f.Game.Commences);
        f.Loop.Stop();
        Assert.Equal(0, f.Game.Cancels);
        Assert.Equal(0, f.Game.Approaches);
        Assert.Equal(0, f.Game.LobbyTrips);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MemberCompletesRoundReturnsToOriginalTerritoryAndWaitsForLeaderUntilLimit(int limit)
    {
        using var f = new Fixture(limit, MOGTOME.Models.KeyboundPartyRole.Member);
        f.Game.Frame = f.Game.Frame with { Territory = 129, PartyRole = MOGTOME.Models.BlundervilleRole.Member };
        f.Tick(0);
        f.Game.Frame = f.Game.Frame with { Queued = true, QueueMatches = true }; f.Tick(100);
        f.Game.Frame = f.Game.Frame with { Pop = true, PopMatches = true }; f.Tick(200);
        Assert.Equal(1, f.Game.Commences);
        f.Game.Frame = new(KeyboundEventLoop.Arena, false, false, false, false, false, false, false, false, false,
            MOGTOME.Models.BlundervilleRole.Member);
        f.Tick(300);
        f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true }; f.Tick(400); f.Tick(2400);
        Assert.Equal(1, f.Game.Leaves);
        Assert.Equal(1, f.Typer.CompletedRuns);
        f.Game.Frame = new(129, false, false, false, false, false, false, false, false, false,
            MOGTOME.Models.BlundervilleRole.Member);
        f.Tick(2500);
        Assert.Equal(limit == 0, f.Loop.IsActive);
        Assert.Equal(limit != 0, f.Typer.Finished);
        if (limit == 0)
        {
            Assert.Equal("BV_MemberWait", f.Loop.Status.Key);
            f.Game.Frame = f.Game.Frame with { Pop = true, PopMatches = true }; f.Tick(2600);
            Assert.Equal(2, f.Game.Commences);
        }
        Assert.Equal(0, f.Game.Registers + f.Game.Approaches + f.Game.LobbyTrips + f.Game.Cancels);
    }

    [Fact]
    public void PartyQueueAdmissionKeepsTypingAndActivityAliveAndStopAfterNextFinishesOnlyTheCurrentRound()
    {
        foreach (var role in new[] { MOGTOME.Models.KeyboundPartyRole.Leader, MOGTOME.Models.KeyboundPartyRole.Member })
        {
            using var f = new Fixture(role: role);
            f.Input.AutoEcho = true;
            f.Game.Frame = f.Game.Frame with
            {
                PartyRole = role == MOGTOME.Models.KeyboundPartyRole.Member
                    ? MOGTOME.Models.BlundervilleRole.Member : MOGTOME.Models.BlundervilleRole.Leader,
                Queued = true, QueueMatches = role == MOGTOME.Models.KeyboundPartyRole.Leader,
            };
            f.Tick(0); // Party members have no authoritative local registration entries.
            Assert.True(f.Loop.IsActive);
            Assert.True(f.Typer.IsRunning);
            Assert.Equal(0, f.Game.Registers + f.Game.Approaches + f.Game.Cancels);
            f.Game.Frame = f.Game.Frame with { Pop = true, PopReady = false, PopMatches = false };
            f.Tick(100); // The addon can appear before its popped-content identity is populated.
            Assert.True(f.Loop.IsActive);
            Assert.Equal(0, f.Game.Commences);
            f.Game.Frame = f.Game.Frame with { PopReady = true, PopMatches = true };
            f.Tick(200);
            Assert.Equal(1, f.Game.Commences);
            f.Game.Frame = new(KeyboundEventLoop.Arena, false, false, false, false, false, false, false, false, false);
            f.Tick(300);
            Assert.Equal("a", f.Input.Sent);
            Assert.Equal(1, f.Game.Heartbeats);
            f.Loop.StopAfterCurrentRound();
            Assert.True(f.Loop.StopAfterCurrentRoundArmed);
            f.Input.Visible = false;
            f.Tick(30_300);
            Assert.True(f.Typer.IsRunning);
            Assert.Equal(2, f.Game.Heartbeats);
            Assert.Equal(0, f.Game.Leaves);
            f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true };
            f.Tick(30_400); f.Tick(32_400);
            Assert.Equal(1, f.Game.Leaves);
            Assert.Equal(1, f.Typer.CompletedRuns);
            f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Lobby, Results = false, Entry = true };
            f.Tick(32_500);
            Assert.False(f.Loop.IsActive);
            Assert.False(f.Typer.IsRunning);
            Assert.False(f.Typer.Finished); // A manual stop must not hand off to automatic shopping.
            Assert.False(f.Loop.StopAfterCurrentRoundArmed);
            Assert.Equal(0, f.Game.Registers);
        }
        using var outside = new Fixture(role: MOGTOME.Models.KeyboundPartyRole.Member);
        outside.Loop.StopAfterCurrentRound();
        Assert.False(outside.Loop.IsActive);
        Assert.False(outside.Typer.IsRunning);
        Assert.Equal(0, outside.Game.Leaves + outside.Game.Registers + outside.Game.Cancels);

        using var foreign = new Fixture(role: MOGTOME.Models.KeyboundPartyRole.Member);
        foreign.Game.Frame = foreign.Game.Frame with
        {
            PartyRole = MOGTOME.Models.BlundervilleRole.Member, Queued = true, QueueMatches = false,
            Pop = true, PopReady = true, PopMatches = false,
        };
        foreign.Tick(0);
        Assert.False(foreign.Loop.IsActive);
        Assert.Equal(0, foreign.Game.Commences + foreign.Game.Registers + foreign.Game.Cancels);
    }

    [Fact]
    public void LatePartyReturnDefersLeaderRegistrationAndNativeNextDutyRearmsAMemberWithoutALobbyUpdate()
    {
        using (var leader = new Fixture())
        {
            leader.Game.Frame = leader.Game.Frame with { Entry = true, PartyReturned = false };
            leader.Tick(0); leader.Tick(30_000);
            Assert.True(leader.Loop.IsActive);
            Assert.Equal(0, leader.Game.Registers + leader.Game.Approaches);
            leader.Game.Frame = leader.Game.Frame with { PartyReturned = true };
            leader.Tick(30_100);
            Assert.Equal(1, leader.Game.Registers);
            leader.Game.Frame = leader.Game.Frame with { Entry = false, Queued = true, QueueMatches = true };
            leader.Tick(30_200);
            Assert.Equal(1, leader.Game.Registers);
        }
        using var member = new Fixture(role: MOGTOME.Models.KeyboundPartyRole.Member);
        member.Input.Visible = false;
        member.Game.Frame = new(KeyboundEventLoop.Arena, false, false, false, false, false, false, false, false, false,
            MOGTOME.Models.BlundervilleRole.Member);
        member.Tick(0);
        member.Game.Frame = member.Game.Frame with { Results = true, ResultsReady = true };
        member.Tick(100); member.Tick(2100);
        Assert.Equal(1, member.Typer.CompletedRuns);
        Assert.Equal(1, member.Game.Leaves);
        member.Game.Frame = member.Game.Frame with { Territory = KeyboundEventLoop.Lobby, Transitioning = true, Results = false };
        member.Tick(2200); // No stable lobby update: the leader's next queue is already entering.
        member.Typer.Observe(false, false, 23, KeyboundEventLoop.Lobby, transitioning: true);
        member.Loop.Update(2250); member.Typer.Update(null, 2250);
        Assert.True(member.Loop.IsActive);
        Assert.True(member.Typer.IsRunning); // A missing local player during loading is suspended, not a failed session.
        member.Loop.ObserveDutyStarted(KeyboundEventLoop.Arena, 2300);
        member.Input.Visible = member.Input.AutoEcho = true;
        member.Game.Frame = member.Game.Frame with { Territory = KeyboundEventLoop.Arena, Transitioning = false };
        member.Tick(2400);
        Assert.True(member.Loop.IsActive);
        Assert.True(member.Typer.IsRunning);
        Assert.Equal("a", member.Input.Sent);
        Assert.Equal(4, member.Game.Heartbeats);
        Assert.Equal(1, member.Typer.CompletedRuns);
        member.Loop.ObserveDutyStarted(KeyboundEventLoop.Arena, 2401); // Same active round is not rearmed again.
        member.Game.Frame = member.Game.Frame with { Results = true, ResultsReady = true };
        member.Tick(2500); member.Tick(4500);
        Assert.Equal(2, member.Typer.CompletedRuns);
        Assert.Equal(2, member.Game.Leaves);
        Assert.Equal(0, member.Game.Registers + member.Game.Approaches + member.Game.Cancels);
        member.Loop.Stop(); member.Typer.Stop();
        member.Loop.ObserveDutyStarted(KeyboundEventLoop.Arena, 5000);
        Assert.False(member.Loop.IsActive);
        Assert.False(member.Typer.IsRunning);
    }

    [Fact]
    public void SelectedLeaderCannotInitiateAQueueWhileActuallyAPartyMember()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { PartyRole = MOGTOME.Models.BlundervilleRole.Member, Entry = true };
        f.Tick(0);
        Assert.Equal(0, f.Game.Registers + f.Game.Approaches);
        Assert.Equal("KB_WaitPartyRole", f.Loop.Status.Key);
    }

    [Fact]
    public void MemberOutsideTheSquareRejectsUnrelatedQueuePopWithoutTravelOrCancellation()
    {
        using var f = new Fixture(role: MOGTOME.Models.KeyboundPartyRole.Member);
        f.Game.Frame = f.Game.Frame with
        {
            Territory = 129, PartyRole = MOGTOME.Models.BlundervilleRole.Member,
            Pop = true, PopMatches = false,
        };
        f.Tick(0);
        Assert.False(f.Loop.IsActive);
        Assert.Equal(0, f.Game.Commences + f.Game.Registers + f.Game.Approaches + f.Game.LobbyTrips + f.Game.Cancels);
    }

    [Fact]
    public void StartingWithVisibleRegistrationWaitsForReadinessThenSubmitsOnceWithoutNpcInteraction()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Entry = true, EntryReady = false };
        f.Tick(0); f.Tick(100);
        Assert.True(f.Loop.IsActive);
        Assert.Equal(0, f.Game.Registers + f.Game.Approaches);
        f.Game.Frame = f.Game.Frame with { EntryReady = true };
        f.Tick(200); f.Tick(300);
        Assert.True(f.Loop.IsActive);
        Assert.Equal(1, f.Game.Registers);
        Assert.Equal(0, f.Game.Approaches);
    }

    [Fact]
    public void RejectedRegistrationStopsWithoutReplayingItsCallback()
    {
        using var f = new Fixture();
        f.Game.RegisterAccepted = false;
        f.Game.Frame = f.Game.Frame with { Entry = true };
        f.Tick(0); f.Tick(100);
        Assert.False(f.Loop.IsActive);
        Assert.False(f.Typer.IsRunning);
        Assert.Equal("BV_UiMismatch", f.Typer.Status.Key);
        Assert.Equal(1, f.Game.Registers);
        Assert.Equal(0, f.Game.Commences + f.Game.Approaches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AutomaticallyRegistersAcceptsPlaysLeavesAndRequeuesUnlessLimitWasReached(int limit)
    {
        using var f = new Fixture(limit);
        f.Tick(0); Assert.Equal(1, f.Game.Approaches);
        f.Game.Frame = f.Game.Frame with { Entry = true }; f.Tick(100); f.Tick(200);
        Assert.Equal(1, f.Game.Registers);
        f.Game.Frame = f.Game.Frame with { Entry = false, Queued = true, QueueMatches = true }; f.Tick(300);
        f.Game.Frame = f.Game.Frame with { Pop = true, PopMatches = true }; f.Tick(400); f.Tick(500);
        Assert.Equal(1, f.Game.Commences);
        f.Game.Frame = new(KeyboundEventLoop.Arena, true, false, false, false, false, false, false, false, false); f.Tick(600);
        f.Game.Frame = f.Game.Frame with { Transitioning = false }; f.Tick(700);
        Assert.True(f.Typer.IsRunning);
        Assert.Equal(1, f.Game.Heartbeats);
        f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true }; f.Tick(800); f.Tick(900); f.Tick(2800);
        Assert.Equal(1, f.Game.Leaves);
        Assert.Equal(1, f.Typer.CompletedRuns);
        f.Game.Frame = new(KeyboundEventLoop.Lobby, false, false, false, false, false, false, false, false, false); f.Tick(3000);
        Assert.Equal(limit == 0, f.Loop.IsActive);
        Assert.Equal(limit != 0, f.Typer.Finished);
        if (limit == 0)
        {
            f.Game.Frame = f.Game.Frame with { Entry = true }; f.Tick(3100);
            Assert.Equal(2, f.Game.Registers);
        }
        else Assert.Equal(1, f.Game.Registers);
    }

    [Fact]
    public void MatchingPopWaitsForTheCommenceButtonBeforeConsumingItsSingleDispatch()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Pop = true, PopMatches = true, PopReady = false };
        f.Tick(0); f.Tick(100);
        Assert.True(f.Loop.IsActive);
        Assert.Equal(0, f.Game.Commences);
        Assert.Equal(0, f.Game.Cancels);
        f.Game.Frame = f.Game.Frame with { PopReady = true };
        f.Tick(200); f.Tick(300);
        Assert.Equal(1, f.Game.Commences);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AdmittedRoundsCountAndLeaveResultsAtTheLimitEvenIfTheInputReaderNeverBecomesReady(int limit)
    {
        using var f = new Fixture(limit);
        f.Input.Visible = false;
        for (var round = 1; round <= limit; round++)
        {
            var start = round * 10_000L;
            f.Game.Frame = new(KeyboundEventLoop.Arena, false, false, false, false, false, false, false, false, false);
            f.Tick(start);
            f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true };
            f.Tick(start + 100);
            f.Tick(start + 2099);
            Assert.Equal(round - 1, f.Game.Leaves);
            f.Tick(start + 2100);
            Assert.Equal(round, f.Game.Leaves);
            Assert.Equal(round, f.Typer.CompletedRuns);
            f.Game.Frame = new(KeyboundEventLoop.Lobby, false, false, false, false, false, false, false, false, false);
            f.Tick(start + 2200);
            Assert.Equal(round < limit, f.Loop.IsActive);
        }
        Assert.True(f.Typer.Finished);
    }

    [Fact]
    public void UnrelatedQueueIsNeverAcceptedCancelledOrOverwritten()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Queued = true, QueueMatches = false, Pop = true, PopMatches = false };
        f.Tick(0);
        Assert.False(f.Loop.IsActive);
        Assert.False(f.Typer.IsRunning);
        Assert.Equal(0, f.Game.Registers + f.Game.Commences + f.Game.Cancels);
    }

    [Fact]
    public void StopWinsBeforeQueuedRegistrationAndDoesNotAbandonAPlayedGame()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Entry = true };
        f.Typer.RequestStop(); f.Tick(0);
        Assert.Equal(0, f.Game.Registers);
        Assert.False(f.Loop.IsActive);
        Assert.Equal(0, f.Game.Leaves);
    }

    [Fact]
    public void AfkActivityContinuesEveryOwnedArenaUpdateEvenWhileInputIsUnavailable()
    {
        using var f = new Fixture();
        f.Tick(0); Assert.Equal(0, f.Game.Heartbeats);
        f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Arena };
        f.Tick(100); f.Tick(200); f.Tick(30_100);
        Assert.Equal(3, f.Game.Heartbeats);
        f.Loop.Stop(); f.Tick(60_100);
        Assert.Equal(3, f.Game.Heartbeats);
    }

    [Fact]
    public void ReturnWithoutResultEvidenceStopsWithoutClaimingACompletedRun()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Arena }; f.Tick(0);
        f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Lobby }; f.Tick(100);
        Assert.False(f.Loop.IsActive);
        Assert.Equal(0, f.Typer.CompletedRuns);
        Assert.Equal(0, f.Game.Leaves);
    }

    [Fact]
    public void UnreadyResultsWithholdInputAndCannotTriggerExitOrCount()
    {
        using var f = new Fixture();
        f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Arena, Results = true };
        f.Tick(0);
        Assert.Equal(0, f.Game.Leaves);
        Assert.Equal(0, f.Typer.CompletedRuns);
        Assert.True(f.Typer.SuppressGameplay);
    }
}
