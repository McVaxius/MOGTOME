// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using Lumina.Text.ReadOnly;

namespace MOGTOME.Services.Keybound;

internal sealed class TextSlotCache
{
    private const int InitialBufferLength = 64;

    private readonly byte[][] rawBytes;
    private readonly int[] rawLengths;
    private readonly string[] decodedTexts;

    public TextSlotCache(int slotCount)
    {
        rawBytes = new byte[slotCount][];
        rawLengths = new int[slotCount];
        decodedTexts = new string[slotCount];
        for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            rawBytes[slotIndex] = new byte[InitialBufferLength];
            decodedTexts[slotIndex] = string.Empty;
        }
    }

    public string Decode(int slot, ReadOnlySpan<byte> bytes, bool singleLine)
    {
        if (bytes.SequenceEqual(rawBytes[slot].AsSpan(0, rawLengths[slot])))
        {
            return decodedTexts[slot];
        }

        if (rawBytes[slot].Length < bytes.Length)
        {
            rawBytes[slot] = new byte[Math.Max(bytes.Length, rawBytes[slot].Length * 2)];
        }

        bytes.CopyTo(rawBytes[slot]);
        rawLengths[slot] = bytes.Length;
        var text = bytes.IsEmpty ? string.Empty : new ReadOnlySeStringSpan(bytes).ExtractText();
        decodedTexts[slot] = singleLine ? ToSingleLine(text) : text;
        return decodedTexts[slot];
    }

    private static string ToSingleLine(string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return text.Trim();
        }

        return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
