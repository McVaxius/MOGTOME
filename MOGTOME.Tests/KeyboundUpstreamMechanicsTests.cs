using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed class KeyboundUpstreamMechanicsTests
{
    private static BattleSnapshot Board(params string[] words)
    {
        var s = new BattleSnapshot { PhraseCount = words.Length, InputVisible = true, InputScreenY = 1000 };
        for (var i = 0; i < words.Length; i++)
        {
            s.Phrases[i] = words[i]; s.PhraseScale[i] = 1; s.PhraseScreenX[i] = 100; s.PhraseScreenY[i] = 800;
            s.PhraseGauge[i] = BattleSnapshot.NoGauge; s.PhraseCast[i] = BattleSnapshot.NoCast;
        }
        return s;
    }

    [Fact]
    public void ShieldPrioritySurvivesGaugeAppearanceAndPassesToTheNextWordAtItsSpotThenExpires()
    {
        var memory = new BarrierMemory();
        var s = Board("shield one"); s.PhraseOnBarrier[0] = true; memory.Apply(s, 0);
        s.PhraseOnBarrier[0] = false; s.PhraseGauge[0] = 0; memory.Apply(s, 500);
        Assert.True(s.PhraseOnBarrier[0]);
        s.Phrases[0] = "shield two"; s.PhraseScreenX[0] = 160; s.PhraseOnBarrier[0] = false;
        memory.Apply(s, 600); Assert.True(s.PhraseOnBarrier[0]);
        s.Phrases[0] = "boss"; s.PhraseScreenY[0] = 200; s.PhraseOnBarrier[0] = false;
        memory.Apply(s, 700); Assert.False(s.PhraseOnBarrier[0]);
        s.Phrases[0] = "ordinary"; s.PhraseScreenY[0] = 800; s.PhraseOnBarrier[0] = false;
        memory.Apply(s, 8601); Assert.False(s.PhraseOnBarrier[0]);
        memory.Clear(); s.Phrases[0] = "fresh"; memory.Apply(s, 8700); Assert.False(s.PhraseOnBarrier[0]);
    }

    [Fact]
    public void LateGaugeFlickerCannotTurnAnEstablishedEnemyIntoAShield()
    {
        var memory = new BarrierMemory(); var s = Board("enemy");
        memory.Apply(s, 0); s.PhraseOnBarrier[0] = true;
        memory.Apply(s, 600); Assert.False(s.PhraseOnBarrier[0]);
    }

    [Fact]
    public void IdenticalBossAndShieldWordsKeepSeparateBarrierClassifications()
    {
        var memory = new BarrierMemory(); var s = Board("same", "same");
        s.PhraseOnBarrier[0] = true; s.PhraseScreenY[1] = 200;
        memory.Apply(s, 0);
        Assert.True(s.PhraseOnBarrier[0]); Assert.False(s.PhraseOnBarrier[1]);
        s.PhraseOnBarrier[0] = false; memory.Apply(s, 600);
        Assert.True(s.PhraseOnBarrier[0]); Assert.False(s.PhraseOnBarrier[1]);
    }

    [Fact]
    public void EachCastLinksToOneEnemyAndNeverToTheBarrier()
    {
        var s = Board("near", "far", "shield");
        s.PhraseScreenX[1] = 350;
        s.PhraseScreenY[0] = s.PhraseScreenY[1] = 400;
        s.GaugeCount = 2; s.GaugeScreenX[0] = 100; s.GaugeScreenX[1] = 350;
        s.GaugeScreenY[0] = s.GaugeScreenY[1] = 450;
        s.CastCount = 1; s.CastScreenX[0] = 120; s.CastScreenY[0] = 460; s.CastHits[0] = 2;
        KeyboundUi.LinkPhrases(s);
        Assert.Equal(0, s.PhraseCast[0]); Assert.Equal(BattleSnapshot.NoCast, s.PhraseCast[1]);
        Assert.True(s.PhraseOnBarrier[2]); Assert.Equal(BattleSnapshot.NoCast, s.PhraseCast[2]);
    }

    [Theory]
    [InlineData("Queen's Blood, Jam Session.", "Queens Blood, Jam Session.")]
    [InlineData("Don't-panic!?", "Dontpanic")]
    [InlineData("Été！", "Été")]
    [InlineData("?!", "?!")]
    public void TypedFormsDropOnlyOptionalPunctuationAndKeepPeriodsCommasSpacesAndAccents(string shown, string typed)
        => Assert.Equal(typed, PhraseReadings.TypedForm(shown, null));

    [Fact]
    public void JapaneseReadingsAndTargetLengthsUseTheTypedForm()
    {
        var readings = new Dictionary<string, string> { ["戦闘！"] = "せんとう！" };
        Assert.Equal("せんとう", PhraseReadings.TypedForm("戦闘！", readings));
        Assert.Equal("missing", PhraseReadings.TypedForm("missing", readings));
        var s = Board("漢字", "English", "三", "four");
        s.PhraseTyped[0] = "とてもながいよみ"; s.PhraseTyped[1] = "go"; s.PhraseTyped[2] = "さん"; s.PhraseTyped[3] = "four";
        Assert.Equal(1, TargetPicker.Choose(s, "", new(TargetOrder.Smart, 4, null)));
        Assert.Equal(2, TargetPicker.Choose(s, "さん", new(TargetOrder.Smart, 4, null)));
    }

    [Fact]
    public void ConcealedPhraseMemoryIsBoundedAndClearedBetweenSessions()
    {
        var memory = new PhraseMemory();
        for (var i = 1; i <= 33; i++) memory.Remember(i, "word " + i);
        Assert.Null(memory.Recall(1)); Assert.Equal("word 33", memory.Recall(33));
        memory.Clear(); Assert.Null(memory.Recall(33));
    }

    private static KeyboundBrawlerService Arm(KeyboundTypingTests.FakeInput input)
    {
        var service = new KeyboundBrawlerService(input, (_, _) => 0);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        Assert.True(service.Start(new() { WalletTargetEnabled = false }));
        return service;
    }

    [Fact]
    public void WakeKeyHasItsOwnReleaseAndResumesFromObservedTextWithoutDuplicatingTheFirstLetter()
    {
        var input = new KeyboundTypingTests.FakeInput("word") { Focused = false, WakeAccepted = true };
        using var service = Arm(input);
        service.Update(null, 0); service.Update(null, 150);
        Assert.Equal(new[] { GameKey.Wake('w') }, input.WakeDown);
        Assert.Empty(input.Sent); Assert.Empty(input.Down);
        service.Update(null, 166); Assert.Empty(input.WakeUp);
        service.Update(null, 185); Assert.Single(input.WakeUp);
        input.Focused = true; input.Text = "w";
        service.Update(null, 200); Assert.Equal("o", input.Sent);
    }

    [Fact]
    public void StopRetriesOwnedSystemKeyReleaseBeforeAllowingAnotherStart()
    {
        var input = new KeyboundTypingTests.FakeInput("word") { Focused = false, WakeAccepted = true };
        using var service = Arm(input);
        service.Update(null, 0); service.Update(null, 150);
        input.ReleaseSucceeds = false; service.Stop();
        Assert.True(service.OwnsInput); Assert.False(service.CanStart);
        input.ReleaseSucceeds = true; service.Update(null, 200);
        Assert.Single(input.WakeUp); Assert.Empty(input.Up); Assert.False(service.OwnsInput); Assert.True(service.CanStart);
    }

    [Fact]
    public void RepeatedWakeKeysRespectEvenTheSlowestHalfSpeedThrowerPace()
    {
        var input = new KeyboundTypingTests.FakeInput("word") { Focused = false, WakeAccepted = true };
        using var service = new KeyboundBrawlerService(input, (_, max) => max - 1);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        Assert.True(service.Start(new() { WalletTargetEnabled = false, TypingWpm = 10, PlayMode = KeyboundPlayMode.Thrower }));
        service.Update(null, 0); service.Update(null, 150); service.Update(null, 185);
        for (var now = 300; now <= 3000; now += 150) service.Update(null, now);
        Assert.Single(input.WakeDown);
        service.Update(null, 3150); Assert.Equal(2, input.WakeDown.Count);
    }

    [Fact]
    public void KanaWakeUsesSpaceAndChatPreventsBothFocusAndSystemKeys()
    {
        var input = new KeyboundTypingTests.FakeInput("漢字")
        { Focused = false, WakeAccepted = true, Other = true, Configure = s => s.PhraseTyped[0] = "ひらがな" };
        using var service = Arm(input);
        service.Update(null, 0); service.Update(null, 150);
        Assert.Empty(input.WakeDown); Assert.Equal(0, input.FocusAttempts);
        input.Other = false; service.Update(null, 300); service.Update(null, 450);
        Assert.Equal(0x20, Assert.Single(input.WakeDown).VirtualKey);
    }

    [Fact]
    public void EvenTheSameVisibleWordGetsAReadingPauseAfterItsSubmissionCooldown()
    {
        var input = new KeyboundTypingTests.FakeInput("a") { AutoEcho = true };
        using var service = new KeyboundBrawlerService(input, (min, _) => min);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        Assert.True(service.Start(new() { WalletTargetEnabled = false }));
        service.Update(null, 0); service.Update(null, 200); service.Update(null, 320); service.Update(null, 355);
        service.Update(null, 1820); service.Update(null, 2019); Assert.Equal("a", input.Sent);
        service.Update(null, 2020); Assert.Equal("aa", input.Sent);
    }

    [Fact]
    public void AVisuallyTrimmedWordKeepsItsOriginalTypedFormWhileTheTargetIsRetained()
    {
        var words = new[] { "hello" };
        var input = new KeyboundTypingTests.FakeInput(words) { AutoEcho = true };
        using var service = Arm(input);
        service.Update(null, 0); service.Update(null, 100);
        Assert.Equal("he", input.Sent);
        words[0] = "llo";
        service.Update(null, 200);
        Assert.Equal("hel", input.Sent); Assert.Empty(input.Down);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(42f, true)]
    [InlineData(-1f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void NamedControllerMouseTimerResetsPreserveHistoriesAndInvalidSentinels(float value, bool reset)
    {
        var timers = new[] { new FFXIVClientStructs.FFXIV.Client.UI.Misc.InputTimerModule.InputTimerData
            { Timer = value, HistoryIndex = 3, TotalHistoryTime = 12 } };
        KeyboundActivity.ResetTimers(timers);
        Assert.Equal(reset ? 0 : value, timers[0].Timer);
        Assert.Equal(3, timers[0].HistoryIndex); Assert.Equal(12, timers[0].TotalHistoryTime);
    }
}
