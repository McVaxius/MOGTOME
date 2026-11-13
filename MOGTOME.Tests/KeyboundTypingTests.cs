using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed class KeyboundTypingTests
{
    private static KeyboundBrawlerService Arm(FakeInput input, KeyboundBrawlerSettings? settings = null)
    {
        var service = new KeyboundBrawlerService(input, (_, _) => 0);
        service.Observe(true, false, 65, 900);
        Assert.True(service.Start(settings ?? new() { WalletTargetEnabled = false }));
        return service;
    }

    [Fact]
    public void ReadsExactNativePhraseAndWaitsForEveryEchoBeforeEnter()
    {
        var input = new FakeInput("Été!");
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal("É", input.Sent);
        service.Update(null, 200);
        Assert.Equal("É", input.Sent); // WM_CHAR has not landed yet.
        foreach (var (echo, time) in new[] { ("É", 250), ("Ét", 350), ("Été", 450), ("Été!", 550) })
        { input.Text = echo; service.Update(null, time); }
        Assert.Equal("Été!", input.Sent);
        Assert.Equal(new[] { GameKey.Enter }, input.Down);
        Assert.Equal(0, service.SubmittedPhrases);
        input.Text = "";
        service.Update(null, 600);
        Assert.Equal(1, service.SubmittedPhrases);
        Assert.Equal(new[] { GameKey.Enter }, input.Up);
        Assert.Equal(0, service.CompletedRuns);
        service.Update(null, 800);
        Assert.Equal("Été!", input.Sent); // Retiring words remain visible briefly after submission.
        service.Update(null, 2100);
        Assert.Equal("Été!É", input.Sent);
    }

    [Fact]
    public void DroppedCharacterStopsWithoutSendingMoreOrSubmitting()
    {
        var input = new FakeInput("ab");
        using var service = Arm(input);
        service.Update(null, 0);
        service.Update(null, 751);
        Assert.False(service.IsRunning);
        Assert.Equal("KB_InputStalled", service.Status.Key);
        Assert.Equal("a", input.Sent);
        Assert.Empty(input.Down);
    }

    [Theory]
    [InlineData("", 0)] [InlineData("ab", 0)] [InlineData("ax", 0)]
    [InlineData("", 1)] [InlineData("ab", 1)] [InlineData("ax", 1)]
    [InlineData("", 2)] [InlineData("ab", 2)] [InlineData("ax", 2)]
    [InlineData("", 3)] [InlineData("ab", 3)] [InlineData("ax", 3)]
    public void EchoDeadlineStartsAtSuccessfulDispatchAndKeepsProtectiveStops(string initialText, int outcome)
    {
        var input = new FakeInput("ab")
        {
            Text = initialText, RejectSend = outcome == 1, ThrowOnSend = outcome == 2
        };
        input.Configure = _ => input.Now = 800;
        using var service = Arm(input);
        service.Update(null, 0);
        input.Configure = null;
        if (outcome is 1 or 2)
        {
            Assert.False(service.IsRunning);
            Assert.Equal("KB_InputStalled", service.Status.Key);
            Assert.False(service.OwnsInput);
            Assert.Empty(input.Sent);
            Assert.Empty(input.Down);
            Assert.Empty(input.Up);
            Assert.Equal(0, service.SubmittedPhrases);
            service.Update(null, 2000);
            Assert.False(service.IsRunning);
            Assert.Empty(input.Sent);
            Assert.Empty(input.Down);
            return;
        }
        var expectedSent = initialText.Length == 0 ? "a" : "";
        GameKey[] expectedKeys = initialText.Length == 0 ? []
            : [initialText == "ab" ? GameKey.Enter : GameKey.Backspace];
        Assert.True(service.IsRunning);
        Assert.Equal(expectedSent, input.Sent);
        Assert.Equal(expectedKeys, input.Down);
        Assert.Empty(input.Up);
        if (outcome == 3)
        {
            service.RequestStop();
            service.Update(null, 800);
            Assert.Equal("KB_Stopped", service.Status.Key);
        }
        else
        {
            service.Update(null, 801);
            Assert.True(service.IsRunning);
            Assert.Equal(expectedSent, input.Sent);
            Assert.Equal(expectedKeys, input.Down);
            Assert.Equal(expectedKeys, input.Up);
            service.Update(null, 1549);
            Assert.True(service.IsRunning);
            Assert.Equal(0, service.SubmittedPhrases);
            service.Update(null, 1550);
            Assert.Equal("KB_InputStalled", service.Status.Key);
        }
        Assert.False(service.IsRunning);
        Assert.False(service.OwnsInput);
        Assert.Equal(0, service.SubmittedPhrases);
        Assert.Equal(expectedSent, input.Sent);
        Assert.Equal(expectedKeys, input.Down);
        Assert.Equal(expectedKeys, input.Up);
        service.Update(null, 2000);
        Assert.False(service.IsRunning);
        Assert.Equal(expectedSent, input.Sent);
        Assert.Equal(expectedKeys, input.Down);
        Assert.Equal(expectedKeys, input.Up);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmittedPhraseEchoCountsOnceEvenWhenTheBarLosesFocus(bool chatFocused)
    {
        var input = new FakeInput("a") { Text = "a" };
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal(0, service.SubmittedPhrases);
        input.Text = "";
        input.Focused = false;
        input.Other = chatFocused;
        service.Update(null, 40);
        service.Update(null, 80);
        Assert.Equal(1, service.SubmittedPhrases);
        Assert.Single(input.Down);
        Assert.Empty(input.Sent);
        input.Focused = true;
        input.Other = false;
        service.Update(null, 200);
        Assert.Equal(1, service.SubmittedPhrases);
    }

    [Fact]
    public void AnUnrelatedRebuiltInputCannotAcknowledgeThePreviousSubmission()
    {
        var input = new FakeInput("a") { Text = "a" };
        using var service = Arm(input);
        service.Update(null, 0);
        input.Text = "";
        input.Configure = snapshot => snapshot.InputIdentity = 456;
        service.Update(null, 40);
        Assert.Equal(0, service.SubmittedPhrases);
    }

    [Theory]
    [InlineData(false, false, false)] // Disabled input, including Letterlock.
    [InlineData(true, false, true)] // Chat owns focus.
    public void DisabledInputAndChatNeverReceiveCharactersOrFocus(bool visible, bool focused, bool other)
    {
        var input = new FakeInput("word") { Visible = visible, Focused = focused, Other = other };
        using var service = Arm(input);
        service.Update(null, 0);
        service.Update(null, 1000);
        Assert.Empty(input.Sent);
        Assert.Empty(input.Down);
        Assert.Equal(0, input.FocusAttempts);
        Assert.Empty(input.WakeDown);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void RefocusIsThrottledAndDoesNotTypeUntilAcknowledged()
    {
        var input = new FakeInput("word") { Focused = false };
        using var service = Arm(input);
        service.Update(null, 0);
        service.Update(null, 10);
        service.Update(null, 150);
        Assert.Equal(2, input.FocusAttempts);
        Assert.Empty(input.Sent);
        input.Focused = true;
        service.Update(null, 200);
        Assert.Equal("w", input.Sent);
    }

    [Fact]
    public void StunAndRebuiltInputResumeFromNativeTextWithoutReplayingOrTypingIntoChat()
    {
        var input = new FakeInput("word") { AutoEcho = true };
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal("w", input.Sent);
        input.Visible = false;
        input.Focused = false;
        service.Update(null, 100);
        input.Visible = true;
        input.Other = true;
        input.Configure = s => s.InputIdentity = 456;
        service.Update(null, 200);
        Assert.Equal(0, input.FocusAttempts);
        Assert.Equal("w", input.Sent);
        input.Other = false;
        service.Update(null, 300);
        service.Update(null, 450); // Accepted focus attempts alone never authorize characters.
        Assert.Equal(2, input.FocusAttempts);
        Assert.Equal("w", input.Sent);
        input.Focused = true;
        service.Update(null, 600);
        Assert.Equal("wo", input.Sent);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void FocusRecoveryWaitsThroughLetterlockBeforeActivatingTheBar()
    {
        var input = new FakeInput("word") { Focused = false };
        var progress = 0.5f;
        input.Configure = s =>
        {
            s.CastCount = 1; s.CastAddons[0] = 123; s.CastNames[0] = "";
            s.CastProgress[0] = progress;
        };
        using var service = Arm(input);
        service.Update(null, 100);
        Assert.Equal(1, input.FocusAttempts);
        progress = 0.98f;
        service.Update(null, 300);
        service.Update(null, 500);
        Assert.Equal(1, input.FocusAttempts);
        Assert.Empty(input.Sent);
        Assert.Equal("KB_CastHold", service.Status.Key);
        input.Configure = null;
        service.Update(null, 1200);
        Assert.Equal(2, input.FocusAttempts);
        input.Focused = true;
        service.Update(null, 1400);
        Assert.Equal("w", input.Sent);
    }

    [Fact]
    public void StopReleasesOnlyOwnedKeyAndNeverEscapeOrResume()
    {
        var input = new FakeInput("a") { Text = "a" };
        using var service = Arm(input);
        service.Update(null, 0);
        service.RequestStop();
        service.Update(null, 5);
        Assert.Equal(new[] { GameKey.Enter }, input.Down);
        Assert.Equal(new[] { GameKey.Enter }, input.Up);
        Assert.False(service.IsRunning);
        Assert.Equal(0, service.SubmittedPhrases);
        service.Observe(true, false, 65, 900);
        service.Update(null, 1000);
        Assert.Single(input.Down);
    }

    [Fact]
    public void FailedKeyUpIsRetriedAfterStopAndBlocksNewStart()
    {
        var input = new FakeInput("a") { Text = "a", ReleaseSucceeds = false };
        using var service = Arm(input);
        service.Update(null, 0);
        service.Stop();
        Assert.False(service.CanStart);
        Assert.True(service.OwnsInput);
        input.ReleaseSucceeds = true;
        service.Update(null, 40);
        Assert.False(service.OwnsInput);
        Assert.True(service.CanStart);
    }

    [Fact]
    public void ConfirmedCompletionCountsOnceAndLimitsStopBeforeAnotherRound()
    {
        var input = new FakeInput("a");
        using var service = Arm(input, new() { RunLimitEnabled = true, RunLimit = 1, WalletTargetEnabled = false });
        service.ObserveCompletion(900); // No KBB input observed yet.
        Assert.Equal(0, service.CompletedRuns);
        service.Update(null, 0);
        service.ObserveCompletion(901); // Unrelated duty.
        Assert.Equal(0, service.CompletedRuns);
        service.ObserveCompletion(900);
        service.ObserveCompletion(900);
        Assert.Equal(1, service.CompletedRuns);
        service.Update(null, 100);
        Assert.True(service.Finished);
        Assert.False(service.IsRunning);
        Assert.Equal("a", input.Sent);
    }

    [Fact]
    public void InputDisappearanceOrDepartureNeverFabricatesCompletion()
    {
        var input = new FakeInput("a");
        using var service = Arm(input);
        service.Update(null, 0);
        input.Visible = false;
        service.Update(null, 100);
        service.Observe(true, false, 0, 901);
        service.Update(null, 200);
        service.ObserveCompletion(900);
        Assert.Equal(0, service.CompletedRuns);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void WalletLimitWaitsForCompletionAndUnknownWalletStops()
    {
        var input = new FakeInput("a");
        using var service = Arm(input, new() { WalletTargetEnabled = true, WalletTarget = 100 });
        service.Update(0, 0);
        service.Update(100, 100);
        Assert.True(service.IsRunning); // Do not abandon active gameplay.
        service.ObserveCompletion(900);
        service.Update(100, 200);
        Assert.True(service.Finished);
        using var missing = Arm(new("b"), new() { WalletTargetEnabled = true });
        missing.Update(null, 0);
        Assert.False(missing.IsRunning);
        Assert.Equal("KB_NoWallet", missing.Status.Key);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SessionLossOrConflictingOwnerStopsAndReleasesInput(bool ready, bool busy)
    {
        var input = new FakeInput("a") { Text = "a" };
        using var service = Arm(input);
        service.Update(null, 0);
        service.Observe(ready, busy, 65, 900);
        service.Update(null, 1);
        Assert.False(service.IsRunning);
        Assert.Equal(new[] { GameKey.Enter }, input.Up);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransitionSuspendsInputWithoutDisarmingAutomaticEntry(bool playerAvailable)
    {
        var input = new FakeInput("ab");
        using var service = Arm(input);
        service.Observe(playerAvailable, false, 65, 900, transitioning: true);
        service.Update(null, 0);
        Assert.True(service.IsRunning);
        Assert.Empty(input.Sent);
        service.Observe(true, false, 65, 900);
        service.Update(null, 100);
        Assert.Equal("a", input.Sent);
    }

    [Fact]
    public void MatchingPrefixWinsThenNearestEnemyThenShortestPhrase()
    {
        var input = new FakeInput("far", "near-long", "near") { Text = "fa" };
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal("r", input.Sent);
        var nextInput = new FakeInput("far", "near-long", "z");
        using var nearest = Arm(nextInput);
        nearest.Update(null, 0);
        Assert.Equal("z", nextInput.Sent);
    }

    [Fact]
    public void WrongInputBackspacesOneCharacterAndAwaitsExactCorrection()
    {
        var input = new FakeInput("ab") { Text = "ax" };
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal(new[] { GameKey.Backspace }, input.Down);
        service.Update(null, 50);
        Assert.Empty(input.Sent);
        input.Text = "a";
        service.Update(null, 100);
        Assert.Equal("b", input.Sent);
        Assert.Equal(new[] { GameKey.Backspace }, input.Up);
    }

    [Fact]
    public void SupplementaryUnicodeIsPostedTogetherBeforeEcho()
    {
        var input = new FakeInput("𠮷");
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal("𠮷", input.Sent);
        input.Text = "𠮷";
        service.Update(null, 100);
        Assert.Equal(new[] { GameKey.Enter }, input.Down);
    }

    [Fact]
    public void Configured80WpmAttempts75MsCharactersWithUnchangedReactionAndSubmitPauses()
    {
        var input = new FakeInput("abc");
        using var service = new KeyboundBrawlerService(input, (minimum, maximum) => (minimum + maximum - 1) / 2);
        service.Observe(true, false, 65, 900);
        Assert.True(service.Start(new() { Stage12TypingWpm = 80, WalletTargetEnabled = false }));
        service.Update(null, 0);
        service.Update(null, 324);
        Assert.Empty(input.Sent);
        service.Update(null, 325);
        Assert.Equal("a", input.Sent);
        input.Text = "a";
        service.Update(null, 399);
        Assert.Equal("a", input.Sent);
        service.Update(null, 400);
        Assert.Equal("ab", input.Sent);
        input.Text = "ab";
        service.Update(null, 474);
        Assert.Equal("ab", input.Sent);
        service.Update(null, 475);
        Assert.Equal("abc", input.Sent);
        input.Text = "abc";
        service.Update(null, 674);
        Assert.Empty(input.Down);
        service.Update(null, 675);
        Assert.Equal(new[] { GameKey.Enter }, input.Down);
        input.Text = "";
        service.Update(null, 709);
        Assert.Empty(input.Up);
        Assert.Equal(0, service.SubmittedPhrases);
        service.Update(null, 710);
        Assert.Equal(new[] { GameKey.Enter }, input.Up);
        Assert.Equal(1, service.SubmittedPhrases);
    }

    [Theory]
    [InlineData(80d, 80d, 60, 90)]
    [InlineData(40d, 40d, 120, 180)]
    [InlineData(160d, 160d, 30, 45)]
    [InlineData(30.5d, 30.5d, 157, 236)]
    [InlineData(10d, 10d, 480, 720)]
    [InlineData(5d, 5d, 960, 1440)]
    [InlineData(300d, 300d, 16, 24)]
    [InlineData(0d, 5d, 960, 1440)]
    [InlineData(double.MinValue, 5d, 960, 1440)]
    [InlineData(double.MaxValue, 300d, 16, 24)]
    [InlineData(double.NaN, 80d, 60, 90)]
    [InlineData(double.NegativeInfinity, 80d, 60, 90)]
    [InlineData(double.PositiveInfinity, 80d, 60, 90)]
    public void CharacterTimingVariesWithin20PercentOfDoubledClampedWpm(double wpm, double clampedWpm, int minimum, int maximum)
    {
        Assert.Equal(minimum, new KeyboundTypingPace(wpm, (min, max) => min).CharacterDelay());
        Assert.Equal(maximum, new KeyboundTypingPace(wpm, (min, max) => max - 1).CharacterDelay());
        var pace = new KeyboundTypingPace(wpm);
        Assert.Equal(clampedWpm, pace.WordsPerMinute);
        Assert.Equal(clampedWpm * 2d, pace.EffectiveWordsPerMinute);
    }

    [Fact]
    public void GameEditedEchoDoesNotReplayTheLastCharacter()
    {
        var input = new FakeInput("abc");
        using var service = Arm(input);
        service.Update(null, 0);
        Assert.Equal("a", input.Sent);
        input.Text = "ab"; // Native game edit overtook the expected one-character echo.
        service.Update(null, 100);
        Assert.Equal("ac", input.Sent);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void LingeringSubmittedWordsYieldToOtherPhrasesBeforeRetry()
    {
        var input = new FakeInput("a", "b") { AutoEcho = true, Configure = snapshot => snapshot.PhraseOnBarrier[0] = true };
        using var service = Arm(input);
        for (var now = 0; now < 5500; now += 50) service.Update(null, now);
        Assert.StartsWith("ab", input.Sent);
        Assert.Contains("b", input.Sent);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void ReactionAndSubmitDelayBoundsStayUnchanged()
    {
        var minimum = new KeyboundTypingPace(80, (min, _) => min);
        var maximum = new KeyboundTypingPace(80, (_, max) => max - 1);
        Assert.Equal(200, minimum.ReactionDelay());
        Assert.Equal(450, maximum.ReactionDelay());
        Assert.Equal(120, minimum.SubmitDelay());
        Assert.Equal(280, maximum.SubmitDelay());
    }

    internal sealed class FakeInput(params string[] phrases) : IKeyboundInput
    {
        internal string Text = "", Sent = "";
        internal long Now;
        internal bool RejectSend, ThrowOnSend;
        internal bool Visible = true, Focused = true, Other, ReleaseSucceeds = true, AutoEcho, WakeAccepted;
        internal Action<BattleSnapshot>? Configure;
        internal int FocusAttempts;
        internal readonly List<GameKey> Down = [], Up = [];
        internal readonly List<GameKey> WakeDown = [], WakeUp = [];
        public void Read(BattleSnapshot snapshot, long now)
        {
            Now = now;
            snapshot.Clear(); snapshot.InputVisible = Visible; snapshot.InputIdentity = 123;
            snapshot.InputFocused = Focused; snapshot.OtherInputFocused = Other; snapshot.InputText = Text;
            snapshot.PhraseCount = phrases.Length;
            for (var i = 0; i < phrases.Length; i++) { snapshot.Phrases[i] = phrases[i]; snapshot.PhraseScreenY[i] = i; snapshot.PhraseCast[i] = BattleSnapshot.NoCast; snapshot.PhraseGauge[i] = BattleSnapshot.NoGauge; }
            Configure?.Invoke(snapshot);
        }
        public bool Focus() { FocusAttempts++; return true; }
        public bool Character(char value, out long sentAt)
        {
            sentAt = 0;
            if (ThrowOnSend) throw new InvalidOperationException();
            if (RejectSend) return false;
            Sent += value;
            if (AutoEcho) Text += value;
            sentAt = Now;
            return true;
        }
        public bool KeyDown(GameKey key, out long sentAt)
        {
            sentAt = 0;
            if (ThrowOnSend) throw new InvalidOperationException();
            if (RejectSend) return false;
            Down.Add(key);
            if (AutoEcho && key == GameKey.Enter) Text = "";
            sentAt = Now;
            return true;
        }
        public bool KeyUp(GameKey key) { if (!ReleaseSucceeds) return false; Up.Add(key); return true; }
        public bool WakeKeyDown(GameKey key) { if (!WakeAccepted) return false; WakeDown.Add(key); return true; }
        public bool WakeKeyUp(GameKey key) { if (!ReleaseSucceeds) return false; WakeUp.Add(key); return true; }
    }
}
