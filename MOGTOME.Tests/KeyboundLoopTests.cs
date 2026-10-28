using MOGTOME.Services;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed class KeyboundLoopTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly FakeGame Game = new();
        internal readonly KeyboundBrawlerService Typer = new(new KeyboundTypingTests.FakeInput("a"), (_, _) => 0);
        internal readonly KeyboundEventLoop Loop;
        internal Fixture(int limit = 0, MOGTOME.Models.KeyboundPartyRole role = MOGTOME.Models.KeyboundPartyRole.Leader)
        {
            Loop = new(Game, Typer);
            Typer.Observe(true, false, 23, KeyboundEventLoop.Lobby);
            Assert.True(Typer.Start(new() { WalletTargetEnabled = false, RunLimitEnabled = limit > 0, RunLimit = limit }));
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
        f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true }; f.Tick(400);
        Assert.Equal(1, f.Game.Leaves);
        Assert.Equal(1, f.Typer.CompletedRuns);
        f.Game.Frame = new(129, false, false, false, false, false, false, false, false, false,
            MOGTOME.Models.BlundervilleRole.Member);
        f.Tick(500);
        Assert.Equal(limit == 0, f.Loop.IsActive);
        Assert.Equal(limit != 0, f.Typer.Finished);
        if (limit == 0)
        {
            Assert.Equal("BV_MemberWait", f.Loop.Status.Key);
            f.Game.Frame = f.Game.Frame with { Pop = true, PopMatches = true }; f.Tick(600);
            Assert.Equal(2, f.Game.Commences);
        }
        Assert.Equal(0, f.Game.Registers + f.Game.Approaches + f.Game.LobbyTrips + f.Game.Cancels);
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
        f.Game.Frame = f.Game.Frame with { Results = true, ResultsReady = true }; f.Tick(800); f.Tick(900);
        Assert.Equal(1, f.Game.Leaves);
        Assert.Equal(1, f.Typer.CompletedRuns);
        f.Game.Frame = new(KeyboundEventLoop.Lobby, false, false, false, false, false, false, false, false, false); f.Tick(1000);
        Assert.Equal(limit == 0, f.Loop.IsActive);
        Assert.Equal(limit != 0, f.Typer.Finished);
        if (limit == 0)
        {
            f.Game.Frame = f.Game.Frame with { Entry = true }; f.Tick(1100);
            Assert.Equal(2, f.Game.Registers);
        }
        else Assert.Equal(1, f.Game.Registers);
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
    public void AfkActivityIsScopedToArenaAndThrottledWithoutTypingKeys()
    {
        using var f = new Fixture();
        f.Tick(0); Assert.Equal(0, f.Game.Heartbeats);
        f.Game.Frame = f.Game.Frame with { Territory = KeyboundEventLoop.Arena };
        f.Tick(100); f.Tick(200); f.Tick(30_100);
        Assert.Equal(2, f.Game.Heartbeats);
        f.Loop.Stop(); f.Tick(60_100);
        Assert.Equal(2, f.Game.Heartbeats);
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
