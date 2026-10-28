// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. See docs/keybound-attribution.md.
using System;
using System.Collections.Generic;
namespace MOGTOME.Services.Keybound;

internal sealed class BattleSnapshot
{
    public const int MaxPhrases = 16;
    public const int MaxCasts = 8;
    public const int MaxGauges = 16;
    public const int InputSlot = MaxPhrases;
    public const int FirstCastSlot = InputSlot + 1;
    public const int TextSlotCount = FirstCastSlot + MaxCasts;
    public const int NoCast = -1;
    public const int NoGauge = -1;

    public readonly string[] Phrases = new string[MaxPhrases];
    public readonly string[] PhraseTyped = new string[MaxPhrases];
    public readonly float[] PhraseScreenX = new float[MaxPhrases];
    public readonly float[] PhraseScreenY = new float[MaxPhrases];
    public readonly float[] PhraseScale = new float[MaxPhrases];
    public readonly bool[] PhraseOnBarrier = new bool[MaxPhrases];
    public readonly int[] PhraseCast = new int[MaxPhrases];
    public readonly int[] PhraseGauge = new int[MaxPhrases];

    public readonly nint[] CastAddons = new nint[MaxCasts];
    public readonly string[] CastNames = new string[MaxCasts];
    public readonly float[] CastScreenX = new float[MaxCasts];
    public readonly float[] CastScreenY = new float[MaxCasts];
    public readonly float[] CastProgress = new float[MaxCasts];
    public readonly int[] CastHits = new int[MaxCasts];
    public readonly bool[] CastInterrupted = new bool[MaxCasts];

    public readonly float[] GaugeScreenX = new float[MaxGauges];
    public readonly float[] GaugeScreenY = new float[MaxGauges];
    public readonly float[] GaugeSignature = new float[MaxGauges];

    public int PhraseCount;
    public int CastCount;
    public int GaugeCount;
    public bool InputVisible;
    public nint InputIdentity;
    public float InputScreenY;
    public bool InputFocused;
    public bool OtherInputFocused;
    public string InputText = string.Empty;

    public string TypedPhrase(int index) => PhraseTyped[index] ?? Phrases[index];
    public bool IsCasting(int phrase) => PhraseCast[phrase] != NoCast && !CastInterrupted[PhraseCast[phrase]];

    // Any change in how the enemy's HP gauge is drawn after a hit means the hit landed, whichever of
    // its nodes carries the fill.
    public float ProgressSignature(int phrase)
    {
        var gauge = PhraseGauge[phrase];
        var cast = PhraseCast[phrase];
        var signature = gauge == NoGauge ? 0f : GaugeSignature[gauge];
        return cast == NoCast ? signature : signature + CastHits[cast] * 10_000f;
    }

    public bool IsInterruptible(int phrase)
    {
        var cast = PhraseCast[phrase];
        return cast != NoCast && CastHits[cast] > 0 && !CastInterrupted[cast];
    }

    public void Clear()
    {
        Array.Clear(PhraseTyped);
        PhraseCount = 0;
        CastCount = 0;
        GaugeCount = 0;
        InputVisible = false;
        InputIdentity = 0;
        InputFocused = false;
        OtherInputFocused = false;
        InputText = string.Empty;
    }
}
