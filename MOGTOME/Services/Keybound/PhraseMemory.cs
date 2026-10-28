// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Collections.Generic;
namespace MOGTOME.Services.Keybound;

// Cryptic Communications hides the phrases: the box stays up while its text is concealed, yet the
// hidden phrase can still be typed. The last text read from each box is kept to type it from.
internal sealed class PhraseMemory
{
    private const int Capacity = 32;

    private readonly nint[] boxes = new nint[Capacity];
    private readonly string[] texts = new string[Capacity];
    private int nextSlot;

    public void Remember(nint box, string text)
    {
        var slot = SlotOf(box);
        if (slot < 0)
        {
            slot = nextSlot;
            nextSlot = (nextSlot + 1) % Capacity;
            boxes[slot] = box;
        }

        texts[slot] = text;
    }

    public string? Recall(nint box)
    {
        var slot = SlotOf(box);
        return slot < 0 ? null : texts[slot];
    }

    public void Clear()
    {
        Array.Clear(boxes);
        Array.Clear(texts);
        nextSlot = 0;
    }

    private int SlotOf(nint box)
    {
        for (var slot = 0; slot < Capacity; slot++)
        {
            if (boxes[slot] == box)
            {
                return slot;
            }
        }

        return -1;
    }
}
