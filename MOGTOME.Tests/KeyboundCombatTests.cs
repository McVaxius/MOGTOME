using MOGTOME.Services.Keybound;

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
    public void DedicatedIdleCounterRequiresMatchingActiveKbbAgentHudAndAddon(uint territory, bool input,
        bool hud, bool sameHud, uint expected, uint actual, float idle, bool allowed)
        => Assert.Equal(allowed, KeyboundActivity.Matches(territory, input, hud, sameHud, expected, actual, idle));
}
