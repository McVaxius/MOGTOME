// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Collections.Generic;


namespace MOGTOME.Services.Keybound;

public enum TargetOrder : byte
{
    Smart,
    ClosestFirst,
    ShortestFirst,
}

internal readonly record struct TargetRules(TargetOrder Order, int CrowdedAt, string? Demoted, string? Skipped = null);

internal static class TargetPicker
{
    public const int NotShown = -1;

    public static readonly TargetOrder[] Orders = [TargetOrder.Smart, TargetOrder.ClosestFirst, TargetOrder.ShortestFirst];

    private const int RankDemoted = -1;
    private const int RankNormal = 0;
    private const int RankCasting = 1;
    private const int RankInterrupt = 2;
    private const int RankBarrier = 3;

    // In case the game trims typed letters off the enemy's phrase, a phrase that is now only the
    // untyped tail of the target still counts as the target being on screen.
    public static int IndexOf(BattleSnapshot snapshot, string target)
    {
        for (var index = 0; index < snapshot.PhraseCount; index++)
        {
            var phrase = snapshot.Phrases[index];
            if (phrase.Equals(target, StringComparison.OrdinalIgnoreCase) || target.EndsWith(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return NotShown;
    }

    // Barrier phrases charge the shield that blocks a Barrier Buster, a spell that can be interrupted
    // only stops if its enemy keeps getting hit, and any other caster is about to land something, so
    // all three outrank plain enemies. A phrase that kept being typed without any visible effect steps
    // behind all of them for a while.
    public static int Rank(BattleSnapshot snapshot, int index, in TargetRules rules)
    {
        if (rules.Demoted != null && snapshot.Phrases[index].Equals(rules.Demoted, StringComparison.OrdinalIgnoreCase))
        {
            return RankDemoted;
        }

        if (snapshot.PhraseOnBarrier[index])
        {
            return RankBarrier;
        }

        if (snapshot.IsInterruptible(index))
        {
            return RankInterrupt;
        }

        return snapshot.IsCasting(index) ? RankCasting : RankNormal;
    }

    public static bool IsPriority(BattleSnapshot snapshot, int index, in TargetRules rules) => Rank(snapshot, index, rules) > RankNormal;

    // Within a rank: the phrase that continues what is already typed, then the cast closest to going
    // off, then the target order: the enemy lowest on screen (closest to the barrier) or the shortest
    // phrase, Smart switching to shortest once the screen is crowded.
    public static int Choose(BattleSnapshot snapshot, string input, in TargetRules rules)
    {
        var shortestFirst = rules.Order == TargetOrder.ShortestFirst
            || (rules.Order == TargetOrder.Smart && snapshot.PhraseCount >= rules.CrowdedAt);
        var bestIndex = NotShown;
        for (var index = 0; index < snapshot.PhraseCount; index++)
        {
            if (rules.Skipped != null && snapshot.Phrases[index].Equals(rules.Skipped, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (bestIndex == NotShown || IsBetter(snapshot, input, rules, shortestFirst, index, bestIndex))
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static bool IsBetter(BattleSnapshot snapshot, string input, in TargetRules rules, bool shortestFirst, int index, int bestIndex)
    {
        var rank = Rank(snapshot, index, rules);
        var bestRank = Rank(snapshot, bestIndex, rules);
        if (rank != bestRank)
        {
            return rank > bestRank;
        }

        var continues = Continues(snapshot, input, index);
        if (continues != Continues(snapshot, input, bestIndex))
        {
            return continues;
        }

        if (rank is RankInterrupt or RankCasting)
        {
            var progress = snapshot.CastProgress[snapshot.PhraseCast[index]];
            var bestProgress = snapshot.CastProgress[snapshot.PhraseCast[bestIndex]];
            if (progress != bestProgress)
            {
                return progress > bestProgress;
            }
        }

        var length = snapshot.TypedPhrase(index).Length;
        var bestLength = snapshot.TypedPhrase(bestIndex).Length;
        if (shortestFirst && length != bestLength)
        {
            return length < bestLength;
        }

        var screenY = snapshot.PhraseScreenY[index];
        var bestScreenY = snapshot.PhraseScreenY[bestIndex];
        if (screenY != bestScreenY)
        {
            return screenY > bestScreenY;
        }

        return length < bestLength;
    }

    private static bool Continues(BattleSnapshot snapshot, string input, int index)
        => input.Length > 0 && snapshot.TypedPhrase(index).StartsWith(input, StringComparison.OrdinalIgnoreCase);
}
