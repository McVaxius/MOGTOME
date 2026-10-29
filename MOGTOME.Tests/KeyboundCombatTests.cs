using MOGTOME.Services.Keybound;
using MOGTOME.Services;
using MOGTOME.Models;

namespace MOGTOME.Tests;

public sealed class KeyboundCombatTests
{
    private static BattleSnapshot Board(params string[] phrases)
    {
        var snapshot = new BattleSnapshot { PhraseCount = phrases.Length, InputVisible = true, InputScreenY = 1000 };
        for (var i = 0; i < phrases.Length; i++)
        {
            snapshot.Phrases[i] = phrases[i]; snapshot.PhraseScale[i] = 1;
            snapshot.PhraseGauge[i] = BattleSnapshot.NoGauge; snapshot.PhraseCast[i] = BattleSnapshot.NoCast;
        }
        return snapshot;
    }

    [Fact]
    public void BossWithoutNearbyGaugeCannotBeMisclassifiedAsBarrier()
    {
        var s = Board("boss", "shield", "enemy");
        s.PhraseScreenX[0] = s.PhraseScreenX[1] = 100; s.PhraseScreenX[2] = 500;
        s.PhraseScreenY[0] = 100; s.PhraseScreenY[1] = 800; s.PhraseScreenY[2] = 300;
        s.GaugeCount = 1; s.GaugeScreenX[0] = 500; s.GaugeScreenY[0] = 350;
        KeyboundUi.LinkPhrases(s);
        Assert.False(s.PhraseOnBarrier[0]);
        Assert.True(s.PhraseOnBarrier[1]);
        Assert.False(s.PhraseOnBarrier[2]);
        Assert.Equal(1, TargetPicker.Choose(s, "boss", new(TargetOrder.Smart, 4, null)));
    }

    [Fact]
    public void CrowdedOrdinaryTargetsPreferShortestButBarrierAndInterruptStillWin()
    {
        var s = Board("verylong", "tiny", "x", "longer");
        var rules = new TargetRules(TargetOrder.Smart, 4, null);
        Assert.Equal(2, TargetPicker.Choose(s, "", rules));
        s.PhraseCast[0] = 0; s.CastCount = 1; s.CastHits[0] = 2;
        Assert.Equal(0, TargetPicker.Choose(s, "", rules));
        s.PhraseOnBarrier[3] = true;
        Assert.Equal(3, TargetPicker.Choose(s, "", rules));
        Assert.Equal(0, TargetPicker.Choose(s, "", rules with { Demoted = "longer" }));
    }

    [Fact]
    public void CursedGazeAndOtherFullCastsHoldPastTheirVanishedBars()
    {
        var s = Board("a"); s.CastCount = 1; s.CastAddons[0] = 123; s.CastNames[0] = "Cursed Gaze";
        var watch = new CastWatch(name => name == "Cursed Gaze");
        s.CastProgress[0] = 0.5f; watch.Update(s, 100); Assert.False(watch.Holding);
        s.CastProgress[0] = 0.95f; watch.Update(s, 200); Assert.True(watch.Holding);
        watch.Update(s, 2000); Assert.True(watch.Holding);
        s.CastCount = 0; watch.Update(s, 2100); Assert.True(watch.Holding);
        watch.Update(s, 3700); Assert.False(watch.Holding);
        s.CastCount = 1; s.CastNames[0] = "Fire"; s.CastProgress[0] = 0.5f;
        watch.Update(s, 3800); Assert.False(watch.Holding);
        s.CastProgress[0] = 0.98f; watch.Update(s, 3900); Assert.True(watch.Holding);
        s.CastCount = 0; watch.Update(s, 4000); Assert.True(watch.Holding);
        watch.Update(s, 4700); Assert.False(watch.Holding);
    }

    [Fact]
    public void ProgressSignatureTracksEnemyHpAndInterruptHits()
    {
        var s = Board("a"); s.PhraseGauge[0] = 0; s.GaugeSignature[0] = 123;
        Assert.Equal(123, s.ProgressSignature(0));
        s.PhraseCast[0] = 0; s.CastHits[0] = 2;
        Assert.Equal(20123, s.ProgressSignature(0));
        s.GaugeSignature[0] = 100;
        Assert.Equal(20100, s.ProgressSignature(0));
    }
    [Theory]
    [InlineData(1359u, true, true, true, 50u, 50u, 30f, true)]
    [InlineData(1360u, true, true, true, 50u, 50u, 30f, false)]
    [InlineData(1359u, false, true, true, 50u, 50u, 30f, false)]
    [InlineData(1359u, true, false, true, 50u, 50u, 30f, false)]
    [InlineData(1359u, true, true, false, 50u, 50u, 30f, false)]
    [InlineData(1359u, true, true, true, 50u, 51u, 30f, false)]
    [InlineData(1359u, true, true, true, 0u, 0u, 30f, false)]
    [InlineData(1359u, true, true, true, 50u, 50u, -1f, false)]
    [InlineData(1359u, true, true, true, 50u, 50u, float.NaN, false)]
    public void DedicatedIdleCounterRequiresShownHudWithDirectorAndMatchingInputAddon(uint territory, bool shown,
        bool director, bool sameHud, uint expected, uint actual, float idle, bool allowed)
        => Assert.Equal(allowed, KeyboundActivity.Matches(territory, shown, director, sameHud, expected, actual, idle));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void NativePhaseImageUsesOnlyTheThreePublishedStageIndices(int image, int stage)
        => Assert.Equal(stage, KeyboundActivity.StageFromImageIndex(image));

    [Fact]
    public void StagePacesAreCapturedAtStartAndSwitchWithoutSkippingEchoOrLeakingAcrossGames()
    {
        var stage = 1;
        var input = new KeyboundTypingTests.FakeInput("abcdefghijklmnopqrst")
        { Configure = snapshot => snapshot.Stage = stage };
        using var service = new KeyboundBrawlerService(input, (min, max) => (min + max - 1) / 2);
        service.Observe(true, false, 65, KeyboundEventLoop.Arena);
        var settings = new KeyboundBrawlerSettings
        { Stage12TypingWpm = 40, Stage3TypingWpm = 80, WalletTargetEnabled = false };
        Assert.True(service.Start(settings));
        settings.Stage12TypingWpm = 300; settings.Stage3TypingWpm = 5;

        service.Update(null, 0); service.Update(null, 325);
        Assert.Equal("a", input.Sent);
        stage = 3;
        service.Update(null, 400); // A faster stage never bypasses the previous character's echo.
        Assert.Equal("a", input.Sent);
        service.Update(null, 475); Assert.Equal("a", input.Sent); // Still waits when the next key is due.
        input.Text = "a"; input.AutoEcho = true;
        service.Update(null, 476); Assert.Equal("ab", input.Sent);
        service.Update(null, 550); Assert.Equal("ab", input.Sent);
        stage = 2;
        service.Update(null, 551); Assert.Equal("abc", input.Sent);
        service.Update(null, 700); Assert.Equal("abc", input.Sent);
        stage = 1;
        service.Update(null, 701); Assert.Equal("abcd", input.Sent);
        stage = 3;
        service.Update(null, 851); Assert.Equal("abcde", input.Sent);
        stage = 0;
        service.Update(null, 926); Assert.Equal("abcdef", input.Sent);
        service.Update(null, 1001); Assert.Equal("abcdef", input.Sent);
        service.Update(null, 1076); Assert.Equal("abcdefg", input.Sent);

        stage = 3; input.Visible = false;
        service.Update(null, 1080);
        stage = 0; input.Visible = true;
        service.Update(null, 1200); service.Update(null, 1525);
        Assert.Equal("abcdefgh", input.Sent);
        service.Update(null, 1600); Assert.Equal("abcdefgh", input.Sent);
        service.Update(null, 1675); Assert.Equal("abcdefghi", input.Sent);

        stage = 3;
        service.Update(null, 1825); Assert.Equal("abcdefghij", input.Sent);
        stage = 0; input.Text = input.Sent = "";
        service.ObserveNewRound(KeyboundEventLoop.Arena);
        service.Update(null, 2000); service.Update(null, 2325);
        Assert.Equal("a", input.Sent);
        service.Update(null, 2400); Assert.Equal("a", input.Sent);
        service.Update(null, 2475); Assert.Equal("ab", input.Sent);

        var snapshot = new BattleSnapshot { Stage = 3 };
        snapshot.Clear();
        Assert.Equal(0, snapshot.Stage);
    }
}
