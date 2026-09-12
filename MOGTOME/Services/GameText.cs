using System;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace MOGTOME.Services;

internal enum GamePrompt { Raise, Return, SealedArea, LeaveDuty }

/// <summary>Game recognition always uses the client language, independently of the plugin UI.</summary>
internal static class GameText
{
    // Dalamud's legacy SeString.TextValue drops <nbsp> payloads. Lumina preserves
    // their visible spacing, matching the evaluator's output in French prompts.
    internal static string ReadVisibleText(ReadOnlySpan<byte> encoded)
        => new ReadOnlySeString(encoded.ToArray()).ToString();

    internal static uint[] Rows(GamePrompt prompt) => prompt switch
    {
        GamePrompt.Raise => [112, 3787],
        GamePrompt.Return => [118, 119, 194, 197],
        GamePrompt.SealedArea => [102631],
        GamePrompt.LeaveDuty => [108, 109],
        _ => [],
    };

    internal static bool MatchesPrompt(string actual, GamePrompt prompt)
        => MatchesPrompt(actual, prompt, Plugin.ClientState.ClientLanguage, EvaluateAddon);

    internal static bool MatchesPrompt(string actual, GamePrompt prompt, ClientLanguage clientLanguage,
        Func<uint, ClientLanguage, string?> evaluate)
    {
        foreach (var row in Rows(prompt))
            if (MatchesEvaluated(actual, evaluate(row, clientLanguage))) return true;
        return false;
    }

    internal static bool MatchesEvaluated(string actual, string? evaluated)
        => !string.IsNullOrWhiteSpace(evaluated) && Normalize(actual) == Normalize(evaluated);

    internal static string Normalize(string value)
    {
        var result = new StringBuilder();
        var space = false;
        foreach (var c in value.Normalize(NormalizationForm.FormC))
        {
            // Soft hyphens and zero-width layout characters are not visible prompt content.
            if (c is '\u00ad' or '\u200b' or '\ufeff') continue;
            if (char.IsWhiteSpace(c)) { space = result.Length > 0; continue; }
            if (space) result.Append(' ');
            result.Append(c);
            space = false;
        }
        return result.ToString();
    }

    private static unsafe string? EvaluateAddon(uint row, ClientLanguage language)
    {
        try
        {
            var source = Plugin.DataManager.GetExcelSheet<Addon>(language).GetRow(row).Text;
            string? raiser = null;
            if (row == 3787)
            {
                var revive = AgentRevive.Instance();
                if (revive == null || revive->ResurrectingPlayerId == 0) return null;
                raiser = revive->ResurrectingPlayerName.ToString();
            }
            return EvaluateAddon(row, language, Plugin.SeStringEvaluator, source.ToMacroString(), raiser);
        }
        catch (Exception) { return null; }
    }

    internal static string? EvaluateAddon(uint row, ClientLanguage language, ISeStringEvaluator evaluator,
        string macro, string? raiser)
    {
        try
        {
            // Bindings verified in all four game sheets: 3787 uses lstr1 (raiser),
            // 118/119/194/197 use gstr56 (the game's current return destination).
            // Refuse changed/unknown bindings, including an empty global destination.
            if (!HasSupportedBindings(row, macro)) return null;
            SeStringParameter[] parameters = [];
            string? requiredValue = null;
            if (row == 3787)
            {
                requiredValue = raiser;
                if (string.IsNullOrWhiteSpace(requiredValue)) return null;
                parameters = [new ReadOnlySeString(Encoding.UTF8.GetBytes(requiredValue))];
            }
            else if (row is 118 or 119 or 194 or 197)
            {
                requiredValue = PlainResolved(evaluator.EvaluateMacroString("<string(gstr56)>", language: language));
                if (string.IsNullOrWhiteSpace(requiredValue)) return null;
            }
            var result = PlainResolved(evaluator.EvaluateFromAddon(row, parameters, language));
            return requiredValue != null && (result == null || !Normalize(result).Contains(Normalize(requiredValue), StringComparison.Ordinal))
                ? null : result;
        }
        catch (Exception)
        {
            // Missing sheets, parameters, or unavailable native context are never confirmation evidence.
            return null;
        }
    }

    internal static bool HasSupportedBindings(uint row, string macro)
    {
        if (row is not (108 or 109 or 112 or 118 or 119 or 194 or 197 or 3787 or 102631)) return false;
        var required = row switch { 3787 => "<string(lstr1)>", 118 or 119 or 194 or 197 => "<string(gstr56)>", _ => null };
        if (required != null)
        {
            if (!macro.Contains(required, StringComparison.Ordinal)) return false;
            macro = macro.Replace(required, string.Empty, StringComparison.Ordinal);
        }
        return !Regex.IsMatch(RemoveFormatting(macro), "<[^>]+>");
    }

    private static string RemoveFormatting(string macro)
        => Regex.Replace(macro, @"<(?:-|br|nbsp|shy|colortype\(\d+\)|edgecolortype\(\d+\)|color\([^)]*\)|edgecolor\([^)]*\))>", string.Empty);

    private static string? PlainResolved(ReadOnlySeString value)
        => Regex.IsMatch(RemoveFormatting(value.ToMacroString()), "<[^>]+>") ? null : value.ToString();

    internal static bool MatchesLogMessage(string actual, uint row)
    {
        if (row is not (877 or 880)) return false;
        try
        {
            return MatchesEvaluated(actual, PlainResolved(Plugin.SeStringEvaluator.EvaluateFromLogMessage(row, language: Plugin.ClientState.ClientLanguage)));
        }
        catch (Exception) { return false; }
    }
}
