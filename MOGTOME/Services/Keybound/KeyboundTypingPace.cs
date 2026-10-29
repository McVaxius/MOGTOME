using System;

namespace MOGTOME.Services.Keybound;

/// <summary>Attempts twice the configured WPM, using five characters per word and varied delays.</summary>
internal sealed class KeyboundTypingPace(double wordsPerMinute, Func<int, int, int>? draw = null)
{
    private readonly Func<int, int, int> random = draw ?? Random.Shared.Next;
    internal double WordsPerMinute { get; } = double.IsFinite(wordsPerMinute) ? Math.Clamp(wordsPerMinute, 5d, 300d) : 80d;
    internal double EffectiveWordsPerMinute => WordsPerMinute * 2d;
    internal int CharacterDelay()
    {
        var interval = 60_000 / (EffectiveWordsPerMinute * 5);
        return random(Math.Max(1, (int)(interval * 80 / 100)), (int)(interval * 120 / 100) + 1);
    }
    internal int ReactionDelay() => random(200, 451);
    internal int SubmitDelay() => random(120, 281);
}
