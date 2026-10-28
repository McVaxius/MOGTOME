// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Collections.Generic;


namespace MOGTOME.Services.Keybound;

// Letterlock punishes any key pressed from a full cast bar until the attack glows. The bar itself is
// gone the moment it fills, so the hold runs on a clock started when the bar is nearly full, not on the
// bar being visible: Cursed Gaze holds long enough to cover the glow, any other cast a moment.
internal sealed class CastWatch(Func<string, bool>? isLetterlock = null)
{
    private const nint Free = 0;

    private readonly nint[] castIds = new nint[BattleSnapshot.MaxCasts];
    private readonly string[] spells = new string[BattleSnapshot.MaxCasts];
    private readonly bool[] sawFilling = new bool[BattleSnapshot.MaxCasts];
    private readonly bool[] completed = new bool[BattleSnapshot.MaxCasts];
    private readonly bool[] seenThisFrame = new bool[BattleSnapshot.MaxCasts];

    private long holdFrom;
    private long holdUntil;
    private string holdSpell = string.Empty;

    public bool Holding { get; private set; }
    public string HoldingSpell { get; private set; } = string.Empty;
    public string LastCompletedSpell { get; private set; } = string.Empty;
    public long LastCompletedAt { get; private set; }

    // How much of the current hold is still to run, from 1 when it starts to 0 when keys are free again.
    public float HoldRemaining(long now)
    {
        var span = holdUntil - holdFrom;
        return span <= 0 ? 0f : Math.Clamp((holdUntil - now) / (float)span, 0f, 1f);
    }

    public void Update(BattleSnapshot snapshot, long now)
    {
        Array.Clear(seenThisFrame);
        for (var cast = 0; cast < snapshot.CastCount; cast++)
        {
            var slot = SlotFor(snapshot, cast);
            if (slot < 0)
            {
                continue;
            }

            seenThisFrame[slot] = true;
            if (!snapshot.CastInterrupted[cast])
            {
                Track(slot, snapshot.CastProgress[cast], now);
            }
        }

        for (var slot = 0; slot < castIds.Length; slot++)
        {
            if (!seenThisFrame[slot])
            {
                Release(slot);
            }
        }

        Holding = now < holdUntil;
        HoldingSpell = Holding ? holdSpell : string.Empty;
    }

    public void Reset()
    {
        for (var slot = 0; slot < castIds.Length; slot++)
        {
            Release(slot);
        }

        holdFrom = 0;
        holdUntil = 0;
        holdSpell = string.Empty;
        Holding = false;
        HoldingSpell = string.Empty;
    }

    private void Track(int slot, float progress, long now)
    {
        var spell = spells[slot];
        var letterlock = (isLetterlock ?? LetterlockSpells.Matches)(spell);
        if (progress < 0.9f)
        {
            sawFilling[slot] = true;
        }

        // A known Letterlock bar that never reads as filling has a fill the reader cannot measure, so
        // the whole cast and the time after it are held rather than risking the lockout.
        var unreadableLetterlock = letterlock && !sawFilling[slot];
        var full = letterlock ? 0.95f : 0.97f;
        if (!unreadableLetterlock && (progress < full || !sawFilling[slot]))
        {
            return;
        }

        var duration = letterlock ? 3500 : 800;
        if (unreadableLetterlock || !completed[slot])
        {
            HoldFor(spell, now, now + duration);
        }

        if (completed[slot])
        {
            return;
        }

        completed[slot] = true;
        LastCompletedSpell = spell;
        LastCompletedAt = now;
    }

    private void HoldFor(string spell, long now, long until)
    {
        if (until <= holdUntil)
        {
            return;
        }

        holdFrom = now;
        holdUntil = until;
        holdSpell = spell;
    }

    private int SlotFor(BattleSnapshot snapshot, int cast)
    {
        var id = snapshot.CastAddons[cast];
        var spell = snapshot.CastNames[cast];
        var free = -1;
        for (var slot = 0; slot < castIds.Length; slot++)
        {
            if (castIds[slot] == id)
            {
                if (!string.Equals(spells[slot], spell, StringComparison.Ordinal))
                {
                    Claim(slot, id, snapshot, cast);
                }

                return slot;
            }

            if (free < 0 && castIds[slot] == Free)
            {
                free = slot;
            }
        }

        if (free >= 0)
        {
            Claim(free, id, snapshot, cast);
        }

        return free;
    }

    private void Claim(int slot, nint id, BattleSnapshot snapshot, int cast)
    {
        castIds[slot] = id;
        spells[slot] = snapshot.CastNames[cast];
        sawFilling[slot] = false;
        completed[slot] = false;


    }

    private void Release(int slot)
    {
        castIds[slot] = Free;
        spells[slot] = string.Empty;
        sawFilling[slot] = false;
        completed[slot] = false;
    }
}
