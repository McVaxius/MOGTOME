// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
namespace MOGTOME.Services.Keybound;

internal sealed class BattleSnapshot
{
    public const int MaxPhrases = 16;
    public const int InputSlot = MaxPhrases;
    public const int TextSlotCount = MaxPhrases + 1;

    public readonly string[] Phrases = new string[MaxPhrases];
    public readonly float[] PhraseScreenY = new float[MaxPhrases];

    public int PhraseCount;
    public bool InputVisible;
    public nint InputIdentity;
    public bool InputFocused;
    public bool OtherInputFocused;
    public string InputText = string.Empty;

    public void Clear()
    {
        PhraseCount = 0;
        InputVisible = false;
        InputIdentity = 0;
        InputFocused = false;
        OtherInputFocused = false;
        InputText = string.Empty;
    }
}
