// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Collections.Generic;
using Dalamud.Game;

using Lumina.Excel;
using System.Text;

namespace MOGTOME.Services.Keybound;

// What actually gets typed for a phrase. A Japanese client shows it in kanji or katakana but takes it
// typed as its hiragana reading, which the word sheet keeps beside it; every other client types the
// phrase as shown. Either way the punctuation the game marks as optional is left out.
internal static class PhraseReadings
{
    private const string SheetName = "KTGTypeWordTextData";
    private const int ShownColumn = 0;
    private const int ReadingColumn = 1;

    // The input rules require periods, commas and spaces but accept a phrase without question marks,
    // exclamation marks, hyphens or apostrophes, typographic and full-width forms included.
    private const string OptionalPunctuation = "?!-'’‘！？‐‑‒–";

    private static ClientLanguage? language;
    private static Dictionary<string, string>? readings;
    private static readonly Dictionary<string, string> typedForms = new(StringComparer.Ordinal);

    public static bool ClientIsJapanese => Plugin.ClientState.ClientLanguage == ClientLanguage.Japanese;

    public static string TypedForm(string shown)
    {
        var currentLanguage = Plugin.ClientState.ClientLanguage;
        if (language != currentLanguage) { language = currentLanguage; readings = null; typedForms.Clear(); }
        if (typedForms.TryGetValue(shown, out var typed))
        {
            return typed;
        }

        typed = TypedForm(shown, ClientIsJapanese ? readings ??= LoadReadings() : null);
        typedForms[shown] = typed;
        return typed;
    }

    internal static string TypedForm(string shown, IReadOnlyDictionary<string, string>? map)
        => WithoutOptionalPunctuation(map != null && map.TryGetValue(shown, out var reading) ? reading : shown);

    private static string WithoutOptionalPunctuation(string text)
    {
        if (text.AsSpan().IndexOfAny(OptionalPunctuation) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (OptionalPunctuation.IndexOf(text[index]) < 0)
            {
                builder.Append(text[index]);
            }
        }

        var stripped = builder.ToString().Trim();
        return stripped.Length == 0 ? text : stripped;
    }

    private static Dictionary<string, string> LoadReadings()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var sheet = Plugin.DataManager.Excel.GetSheet<RawRow>(name: SheetName);
            for (var index = 0; index < sheet.Count; index++)
            {
                var row = sheet.GetRowAt(index);
                var shown = row.ReadStringColumn(ShownColumn).ExtractText().Trim();
                var reading = row.ReadStringColumn(ReadingColumn).ExtractText().Trim();
                if (shown.Length == 0 || reading.Length == 0)
                {
                    continue;
                }

                map.TryAdd(shown, reading);
                map.TryAdd(reading, reading);
            }
        }
        catch (Exception exception)
        {
            Plugin.Log?.Warning(exception, $"could not read the {SheetName} sheet; phrases are typed as shown");
        }

        return map;
    }
}
