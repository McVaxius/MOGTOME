using System;
using System.Linq;
using System.Threading;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services.Keybound;
using TerritoryKind = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace MOGTOME.Services;

/// <summary>Explicitly armed native KBB typing. Queueing and result dismissal remain manual.</summary>
public sealed class KeyboundBrawlerService : IDisposable
{
    private readonly IKeyboundInput input;
    private readonly BattleSnapshot snapshot = new();
    private readonly Func<int, int, int>? drawDelay;
    private KeyboundTypingPace pace = new(80);
    private bool disposed, ready, busy, suspended, inEvent, roundSeen, completedRound;
    private uint territory, roundTerritory;
    private int stopRequested;
    private string? target, expectedText, recentlySubmitted;
    private long echoDeadline, nextAction, releaseAt, skipUntil, focusRetryAt;
    private GameKey? heldKey;
    private nint inputIdentity;
    private bool submissionPending;
    private int runLimit;
    private uint? walletTarget;

    public KeyboundBrawlerService() : this(new KeyboundInput()) { }
    internal KeyboundBrawlerService(IKeyboundInput input, Func<int, int, int>? drawDelay = null)
    { this.input = input; this.drawDelay = drawDelay; }
    public UiText Status { get; private set; } = Ui.M("KB_Idle");
    public UiText UnavailableReason { get; private set; } = Ui.M("KB_ProfileUnavailable");
    public bool CanStart => ready && !busy && !suspended && !disposed && !IsRunning && heldKey == null;
    public bool IsRunning { get; private set; }
    internal bool OwnsInput => IsRunning || heldKey.HasValue;
    public int CompletedRuns { get; private set; }
    public int SubmittedPhrases { get; private set; }
    public bool Finished { get; private set; }

    internal void Observe(bool ready, bool conflictingAutomation, uint? intendedUse, uint territoryId = 0, bool transitioning = false)
    {
        this.ready = ready && !disposed;
        busy = conflictingAutomation;
        suspended = transitioning;
        inEvent = intendedUse == (uint)TerritoryKind.KeyboundBrawler;
        territory = territoryId;
        UnavailableReason = Ui.M(!this.ready || suspended ? "KB_ProfileUnavailable"
            : busy ? "KB_AutomationBusy" : "KB_ManualQueue");
    }

    internal static uint? ReadTerritoryIntendedUse()
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<TerritoryType>()
                .GetRowOrDefault(Plugin.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId;
        }
        catch { return null; }
    }

    public bool Start() => Start(new KeyboundBrawlerSettings());
    internal bool Start(KeyboundBrawlerSettings settings)
    {
        if (!CanStart) { if (!IsRunning) Status = UnavailableReason; return false; }
        Interlocked.Exchange(ref stopRequested, 0);
        pace = new(settings.TypingWpm, drawDelay);
        runLimit = settings.RunLimitEnabled ? Math.Max(1, settings.RunLimit) : 0;
        walletTarget = settings.WalletTargetEnabled ? (uint)Math.Max(0, settings.WalletTarget) : null;
        CompletedRuns = SubmittedPhrases = 0;
        roundSeen = completedRound = Finished = false;
        roundTerritory = 0;
        recentlySubmitted = null;
        ResetTyping();
        IsRunning = true;
        Status = Ui.M("KB_ManualQueue");
        return true;
    }

    internal void ObserveCompletion(uint completedTerritory)
    {
        // A vanished input bar or a submitted phrase is never a completed run.
        if (!IsRunning || !ready || busy || !roundSeen || completedRound ||
            completedTerritory == 0 || completedTerritory != roundTerritory || territory != completedTerritory) return;
        completedRound = true;
        CompletedRuns++;
        ResetTyping();
    }

    internal void Update(uint? wallet, long now)
    {
        if (Interlocked.Exchange(ref stopRequested, 0) != 0) Stop();
        // Retry an owned key-up after Stop if the window temporarily vanished.
        if (heldKey.HasValue && (!IsRunning || now >= releaseAt)) ReleaseKey();
        if (!IsRunning) return;
        try
        {
            if (!ready || busy) { Stop(); Status = UnavailableReason; return; }
            if (suspended) { ReleaseKey(); ResetTyping(); return; }
            if (!inEvent && roundSeen)
            {
                roundSeen = completedRound = false;
                roundTerritory = 0;
                ResetTyping();
            }
            if (walletTarget.HasValue && !wallet.HasValue) { Stop(); Status = Ui.M("KB_NoWallet"); return; }
            if ((!roundSeen || completedRound) && (runLimit > 0 && CompletedRuns >= runLimit ||
                walletTarget.HasValue && wallet >= walletTarget))
            { Finish(); return; }
            if (!inEvent || completedRound)
            { ReleaseKey(); ResetTyping(); Status = Ui.M("KB_ManualQueue"); return; }
            input.Read(snapshot);
            if (!snapshot.InputVisible)
            { ReleaseKey(); ResetTyping(); Status = Ui.M("KB_WaitInput"); return; }
            if (roundTerritory != territory)
            {
                roundTerritory = territory;
                roundSeen = true;
                completedRound = false;
            }
            if (inputIdentity != snapshot.InputIdentity)
            {
                ReleaseKey(); ResetTyping();
                inputIdentity = snapshot.InputIdentity;
            }
            if (!snapshot.InputFocused)
            {
                ReleaseKey(); ResetTyping(); Status = Ui.M("KB_WaitInput");
                if (!snapshot.OtherInputFocused && now >= focusRetryAt)
                { input.Focus(); focusRetryAt = now + 150; }
                return;
            }
            if (heldKey.HasValue) return;
            if (expectedText != null)
            {
                if (snapshot.InputText == expectedText)
                {
                    expectedText = null;
                    if (submissionPending) { SubmittedPhrases++; submissionPending = false; }
                }
                else if (now >= echoDeadline)
                { Stop(); Status = Ui.M("KB_InputStalled"); return; }
                else return;
            }
            if (now < nextAction) return;
            if (target == null || !snapshot.Phrases.Take(snapshot.PhraseCount).Any(p =>
                p.Equals(target, StringComparison.OrdinalIgnoreCase) || target.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                target = Enumerable.Range(0, snapshot.PhraseCount)
                    .Where(i => now >= skipUntil || !snapshot.Phrases[i].Equals(recentlySubmitted, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => snapshot.InputText.Length > 0 && snapshot.Phrases[i].StartsWith(snapshot.InputText, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(i => snapshot.PhraseScreenY[i]).ThenBy(i => snapshot.Phrases[i].Length)
                    .Select(i => snapshot.Phrases[i]).FirstOrDefault();
                if (target != null)
                { var reaction = pace.ReactionDelay(); nextAction = now + reaction; if (reaction > 0) return; }
            }
            if (string.IsNullOrEmpty(target)) { Status = Ui.M("KB_WaitInput"); return; }
            var text = snapshot.InputText;
            Status = Ui.M("KB_Typing", CompletedRuns, SubmittedPhrases);
            if (text.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                if (!Press(GameKey.Enter, now)) { FailInput(); return; }
                recentlySubmitted = target; skipUntil = now + 900;
                target = null; submissionPending = true;
                Expect(string.Empty, now); nextAction = now + 160;
            }
            else if (!target.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                if (!Press(GameKey.Backspace, now)) { FailInput(); return; }
                Expect(text[..^1], now); nextAction = now + pace.CharacterDelay();
            }
            else
            {
                var character = target[text.Length];
                if (!input.Character(character)) { FailInput(); return; }
                var added = character.ToString();
                if (char.IsHighSurrogate(character) && text.Length + 1 < target.Length && char.IsLowSurrogate(target[text.Length + 1]))
                {
                    var low = target[text.Length + 1];
                    if (!input.Character(low)) { FailInput(); return; }
                    added += low;
                }
                Expect(text + added, now); nextAction = now + (text.Length + added.Length == target.Length ? pace.SubmitDelay() : pace.CharacterDelay());
            }
        }
        catch (Exception ex)
        {
            Stop(); Status = Ui.M("KB_InputStalled");
            Plugin.Log?.Warning(ex, "[MOGTOME][KBB] native input stopped");
        }
    }

    private void Expect(string text, long now) { expectedText = text; echoDeadline = now + 750; }
    private bool Press(GameKey key, long now)
    {
        if (!input.KeyDown(key)) return false;
        heldKey = key; releaseAt = now + 35;
        return true;
    }
    private void ReleaseKey()
    {
        if (heldKey.HasValue && input.KeyUp(heldKey.Value)) heldKey = null;
    }
    private void ResetTyping()
    {
        target = expectedText = null;
        inputIdentity = 0;
        submissionPending = false;
        nextAction = 0;
    }
    private void FailInput() { Stop(); Status = Ui.M("KB_InputStalled"); }
    private void Finish() { Stop(); Finished = true; Status = Ui.M("KB_Finished", CompletedRuns); }
    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Stop()
    {
        IsRunning = Finished = false;
        Interlocked.Exchange(ref stopRequested, 0);
        ReleaseKey(); ResetTyping();
        roundSeen = completedRound = false;
        recentlySubmitted = null;
        Status = Ui.M("KB_Stopped");
    }
    public void Dispose() { Stop(); disposed = true; }
}
