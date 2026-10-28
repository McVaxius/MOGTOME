// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. See docs/keybound-attribution.md.
using System;
using System.Collections.Generic;


namespace MOGTOME.Services.Keybound;

// Letterlock punishes any key pressed between a full cast bar and the attack landing. Spells known to
// be Letterlock (Cursed Gaze) hold the typer from a nearly full bar until the cast is gone; a cast whose
// name cannot be read is treated the same way for a shorter while; every other named cast never holds.
internal sealed class CastWatch(Func<string, bool>? isLetterlock = null)
{
    private const nint Free = 0;

    private readonly nint[] addons = new nint[BattleSnapshot.MaxCasts];
    private readonly string[] spells = new string[BattleSnapshot.MaxCasts];
    private readonly bool[] sawFilling = new bool[BattleSnapshot.MaxCasts];
    private readonly long[] fullSince = new long[BattleSnapshot.MaxCasts];
    private readonly bool[] seenThisFrame = new bool[BattleSnapshot.MaxCasts];

    public bool Holding { get; private set; }
    public string HoldingSpell { get; private set; } = string.Empty;

    public void Update(BattleSnapshot snapshot, long now)
    {
        Array.Clear(seenThisFrame);
        Holding = false;
        HoldingSpell = string.Empty;
        for (var cast = 0; cast < snapshot.CastCount; cast++)
        {
            var slot = SlotFor(snapshot, cast);
            if (slot < 0)
            {
                continue;
            }

            seenThisFrame[slot] = true;
            if (!snapshot.CastInterrupted[cast] && ShouldHold(slot, snapshot.CastProgress[cast], now))
            {
                Holding = true;
                HoldingSpell = spells[slot];
            }
        }

        for (var slot = 0; slot < addons.Length; slot++)
        {
            if (!seenThisFrame[slot])
            {
                Release(slot);
            }
        }
    }

    public void Reset()
    {
        for (var slot = 0; slot < addons.Length; slot++)
        {
            Release(slot);
        }

        Holding = false;
        HoldingSpell = string.Empty;
    }

    private bool ShouldHold(int slot, float progress, long now)
    {
        var spell = spells[slot];
        var letterlock = (isLetterlock ?? LetterlockSpells.Matches)(spell);
        if (!letterlock && spell.Length > 0)
        {
            return false;
        }

        var full = letterlock ? 0.95f : 0.97f;
        if (progress < 0.9f)
        {
            sawFilling[slot] = true;
        }

        // A known Letterlock bar that never reads as filling has a fill the reader cannot measure, so
        // the whole cast is held rather than risking the lockout.
        var reachedFull = progress >= full && sawFilling[slot];
        if (!reachedFull && !(letterlock && !sawFilling[slot]))
        {
            fullSince[slot] = 0;
            return false;
        }

        if (fullSince[slot] == 0)
        {
            fullSince[slot] = now;

        }

        var limit = letterlock ? 6_000 : 3_000;
        return now - fullSince[slot] < limit;
    }

    private int SlotFor(BattleSnapshot snapshot, int cast)
    {
        var addon = snapshot.CastAddons[cast];
        var spell = snapshot.CastNames[cast];
        var free = -1;
        for (var slot = 0; slot < addons.Length; slot++)
        {
            if (addons[slot] == addon)
            {
                if (!string.Equals(spells[slot], spell, StringComparison.Ordinal))
                {
                    Claim(slot, addon, snapshot, cast);
                }

                return slot;
            }

            if (free < 0 && addons[slot] == Free)
            {
                free = slot;
            }
        }

        if (free >= 0)
        {
            Claim(free, addon, snapshot, cast);
        }

        return free;
    }

    private void Claim(int slot, nint addon, BattleSnapshot snapshot, int cast)
    {
        addons[slot] = addon;
        spells[slot] = snapshot.CastNames[cast];
        sawFilling[slot] = false;
        fullSince[slot] = 0;

        var spell = spells[slot].Length > 0 ? spells[slot] : "an unnamed spell";
        var hits = snapshot.CastHits[cast];
        var note = (isLetterlock ?? LetterlockSpells.Matches)(spells[slot]) ? " (Letterlock: keys stop when it completes)"
            : hits > 0 ? $" (interrupt with {hits} hits)"
            : string.Empty;

    }

    private void Release(int slot)
    {
        addons[slot] = Free;
        spells[slot] = string.Empty;
        sawFilling[slot] = false;
        fullSince[slot] = 0;
    }
}
