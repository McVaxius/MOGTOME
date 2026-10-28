using System;

namespace MOGTOME.Services.Keybound;

/// <summary>Standard WPM uses five characters per word. Delays vary around that target.</summary>
internal sealed class KeyboundTypingPace(int wordsPerMinute, Func<int, int, int>? draw = null, bool halfSpeed = false)
{
    private readonly Func<int, int, int> random = draw ?? Random.Shared.Next;
    internal int WordsPerMinute { get; } = Math.Clamp(wordsPerMinute, 10, 300);
    internal double EffectiveWordsPerMinute => WordsPerMinute / (halfSpeed ? 2d : 1d);
    internal int CharacterDelay()
    {
        var interval = 60_000 / (WordsPerMinute * 5) * (halfSpeed ? 2 : 1);
        return random(Math.Max(1, interval * 80 / 100), interval * 120 / 100 + 1);
    }
    internal int ReactionDelay() => random(200, 451);
    internal int SubmitDelay() => random(120, 281);
}
