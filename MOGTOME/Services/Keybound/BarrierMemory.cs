// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Collections.Generic;


namespace MOGTOME.Services.Keybound;

// A barrier phrase appears over the barrier with no enemy gauge under it, then a gauge shows up beneath it and stays
// for the rest of the Barrier Buster, so every later phrase at that spot looks like an enemy's from its first frame.
// Two memories keep them apart: a phrase recognised as a barrier phrase in its first moment stays one while it is on
// screen, and the spots barrier phrases were seen at keep marking new phrases there as barrier phrases for a while.
internal sealed class BarrierMemory
{
    private const int Capacity = BattleSnapshot.MaxPhrases;
    private const int SpotCapacity = 8;

    private readonly string[] phrases = new string[Capacity];
    private readonly bool[] onBarrier = new bool[Capacity];
    private readonly bool[] barrierZone = new bool[Capacity];
    private readonly long[] firstSeenAt = new long[Capacity];
    private readonly float[] spotX = new float[SpotCapacity];
    private readonly float[] spotY = new float[SpotCapacity];
    private readonly long[] spotSeenAt = new long[SpotCapacity];
    private int count;
    private int spotCount;

    public void Apply(BattleSnapshot snapshot, long now)
    {
        ForgetPhrasesOffScreen(snapshot);
        ForgetStaleSpots(now);
        for (var index = 0; index < snapshot.PhraseCount; index++)
        {
            var phrase = snapshot.Phrases[index];
            var atSpot = AtSpot(snapshot, index);
            var zone = KeyboundUi.InBarrierZone(snapshot, index);
            var known = IndexOf(phrase, zone);
            if (known < 0)
            {
                Remember(phrase, snapshot.PhraseOnBarrier[index] || atSpot, zone, now);

                snapshot.PhraseOnBarrier[index] |= atSpot;
                continue;
            }

            var learned = snapshot.PhraseOnBarrier[index] && now - firstSeenAt[known] < 500;
            if (!onBarrier[known] && (learned || atSpot))
            {
                onBarrier[known] = true;

            }

            snapshot.PhraseOnBarrier[index] = onBarrier[known];
        }

        for (var index = 0; index < snapshot.PhraseCount; index++)
        {
            if (snapshot.PhraseOnBarrier[index])
            {
                MarkSpot(snapshot.PhraseScreenX[index], snapshot.PhraseScreenY[index], snapshot.PhraseScale[index], now);
            }
        }
    }

    public void Clear()
    {
        Array.Clear(phrases);
        Array.Clear(onBarrier);
        Array.Clear(barrierZone);
        Array.Clear(firstSeenAt);
        count = 0;
        spotCount = 0;
    }

    private bool AtSpot(BattleSnapshot snapshot, int index)
    {
        if (!KeyboundUi.InBarrierZone(snapshot, index))
        {
            return false;
        }

        var scale = snapshot.PhraseScale[index];
        for (var spot = 0; spot < spotCount; spot++)
        {
            if (MathF.Abs(snapshot.PhraseScreenX[index] - spotX[spot]) <= 120f * scale
                && MathF.Abs(snapshot.PhraseScreenY[index] - spotY[spot]) <= 40f * scale)
            {
                return true;
            }
        }

        return false;
    }

    private void MarkSpot(float x, float y, float scale, long now)
    {
        var oldest = 0;
        for (var spot = 0; spot < spotCount; spot++)
        {
            if (MathF.Abs(x - spotX[spot]) <= 120f * scale && MathF.Abs(y - spotY[spot]) <= 40f * scale)
            {
                spotX[spot] = x;
                spotY[spot] = y;
                spotSeenAt[spot] = now;
                return;
            }

            if (spotSeenAt[spot] < spotSeenAt[oldest])
            {
                oldest = spot;
            }
        }

        var slot = spotCount < SpotCapacity ? spotCount++ : oldest;
        spotX[slot] = x;
        spotY[slot] = y;
        spotSeenAt[slot] = now;
    }

    private void ForgetStaleSpots(long now)
    {
        for (var spot = spotCount - 1; spot >= 0; spot--)
        {
            if (now - spotSeenAt[spot] <= 8000)
            {
                continue;
            }

            spotCount--;
            spotX[spot] = spotX[spotCount];
            spotY[spot] = spotY[spotCount];
            spotSeenAt[spot] = spotSeenAt[spotCount];
        }
    }

    private void ForgetPhrasesOffScreen(BattleSnapshot snapshot)
    {
        for (var index = count - 1; index >= 0; index--)
        {
            if (IsOnScreen(snapshot, phrases[index], barrierZone[index]))
            {
                continue;
            }

            count--;
            phrases[index] = phrases[count];
            onBarrier[index] = onBarrier[count];
            barrierZone[index] = barrierZone[count];
            firstSeenAt[index] = firstSeenAt[count];
            phrases[count] = string.Empty;
            onBarrier[count] = false;
        }
    }

    private void Remember(string phrase, bool barrier, bool zone, long now)
    {
        if (count == Capacity)
        {
            return;
        }

        phrases[count] = phrase;
        onBarrier[count] = barrier;
        barrierZone[count] = zone;
        firstSeenAt[count] = now;
        count++;
    }

    private static bool IsOnScreen(BattleSnapshot snapshot, string phrase, bool zone)
    {
        for (var index = 0; index < snapshot.PhraseCount; index++)
        {
            if (string.Equals(snapshot.Phrases[index], phrase, StringComparison.Ordinal) &&
                KeyboundUi.InBarrierZone(snapshot, index) == zone)
            {
                return true;
            }
        }

        return false;
    }

    private int IndexOf(string phrase, bool zone)
    {
        for (var index = 0; index < count; index++)
        {
            if (string.Equals(phrases[index], phrase, StringComparison.Ordinal) && barrierZone[index] == zone)
            {
                return index;
            }
        }

        return -1;
    }
}
