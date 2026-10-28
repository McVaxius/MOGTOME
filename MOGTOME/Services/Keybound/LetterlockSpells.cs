// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. See docs/keybound-attribution.md.
using System;
using System.Collections.Generic;


namespace MOGTOME.Services.Keybound;

// The cast bar names the spell in the client's language, so the Letterlock spells are matched by
// the names their Action rows carry in that same language.
internal static class LetterlockSpells
{
    // Cursed Gaze and both Dynamic Sensory Jammer actions share the Letterlock key hold.
    // Cross-checked against XeldarAlz 9e8153252de88217ac4309cafc6b0cdbfefcca15.
    private static readonly uint[] actionIds = [49529, 49532, 49702];

    private static string[]? names;
    private static Dalamud.Game.ClientLanguage? language;

    public static bool Matches(string spell)
    {
        if (spell.Length == 0)
        {
            return false;
        }

        if (language != Plugin.ClientState.ClientLanguage) { language = Plugin.ClientState.ClientLanguage; names = null; }
        var known = names ??= LoadNames();
        for (var index = 0; index < known.Length; index++)
        {
            if (known[index].Equals(spell, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] LoadNames()
    {
        var actions = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var loaded = new List<string>(actionIds.Length);
        for (var index = 0; index < actionIds.Length; index++)
        {
            if (actions.GetRowOrDefault(actionIds[index]) is { } action && action.Name.ExtractText() is { Length: > 0 } name)
            {
                loaded.Add(name);
            }
        }

        return loaded.ToArray();
    }
}
