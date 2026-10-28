using System;
using System.Linq;
using System.Threading;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services.Keybound;
using TerritoryKind = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace MOGTOME.Services;

/// <summary>Native KBB typing controller, owned by the automatic event loop.</summary>
public sealed class KeyboundBrawlerService : IDisposable
{
    private readonly IKeyboundInput input;
    private readonly BattleSnapshot snapshot = new();
    private readonly CastWatch castWatch = new();
    private readonly Func<int, int, int>? drawDelay;
    private KeyboundTypingPace pace = new(80);
    private bool disposed, ready, busy, suspended, inEvent, roundSeen, completedRound;
    private uint territory, roundTerritory;
    private int stopRequested;
    private string? target, expectedText, previousText, recentlySubmitted;
    private long echoDeadline, nextAction, releaseAt, focusRetryAt;
    private GameKey? heldKey;
    private nint inputIdentity;
    private bool submissionPending;
    private int repeatStreak;
    private int focusAttempts;
    private float streakSignature;
    private string? demotedPhrase;
    private long demotedUntil;
    private int runLimit;
    private uint? walletTarget;

    public KeyboundBrawlerService() : this(new KeyboundInput()) { }
    internal KeyboundBrawlerService(IKeyboundInput input, Func<int, int, int>? drawDelay = null)
    { this.input = input; this.drawDelay = drawDelay; }
    public UiText Status { get; private set; } = Ui.M("KB_Idle");
    public UiText UnavailableReason { get; private set; } = Ui.M("KB_ProfileUnavailable");
    public bool CanStart => ready && !busy && !suspended && !disposed && !IsRunning && heldKey == null;
    public bool IsRunning { get; private set; }
    internal bool StopRequested => Volatile.Read(ref stopRequested) != 0;
    internal bool CanContinueSession => !disposed && !busy && (ready || suspended) && !StopRequested;
    internal bool OwnsInput => IsRunning || heldKey.HasValue;
    public int CompletedRuns { get; private set; }
    public int SubmittedPhrases { get; private set; }
    public bool Finished { get; private set; }
    internal bool SuppressGameplay { get; set; }

    internal void Observe(bool ready, bool conflictingAutomation, uint? intendedUse, uint territoryId = 0, bool transitioning = false)
    {
        this.ready = ready && !disposed;
        busy = conflictingAutomation;
        suspended = transitioning;
        inEvent = intendedUse == (uint)TerritoryKind.KeyboundBrawler;
        territory = territoryId;
        UnavailableReason = Ui.M(!this.ready || suspended ? "KB_ProfileUnavailable"
            : busy ? "KB_AutomationBusy" : "KB_Queueing");
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
        pace = new(settings.TypingWpm, drawDelay, settings.PlayMode == KeyboundPlayMode.Thrower);
        runLimit = settings.RunLimitEnabled ? Math.Max(1, settings.RunLimit) : 0;
        walletTarget = settings.WalletTargetEnabled ? (uint)Math.Max(0, settings.WalletTarget) : null;
        CompletedRuns = SubmittedPhrases = 0;
        roundSeen = completedRound = Finished = false;
        roundTerritory = 0;
        recentlySubmitted = null;
        castWatch.Reset();
        repeatStreak = 0; demotedPhrase = null;
        focusRetryAt = 0; focusAttempts = 0;
        ResetTyping();
        IsRunning = true;
        Status = Ui.M("KB_Queueing");
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
            if (suspended && !busy) { ReleaseKey(); ResetTyping(); return; }
            if (!ready || busy) { Stop(); Status = UnavailableReason; return; }
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
            if (SuppressGameplay) { ReleaseKey(); ResetTyping(); return; }
            if (!inEvent || completedRound)
            { ReleaseKey(); ResetTyping(); Status = Ui.M("KB_Queueing"); return; }
            input.Read(snapshot);
            if (!snapshot.InputVisible)
            { ReleaseKey(); ResetTyping(); focusAttempts = 0; Status = Ui.M("KB_WaitInput"); return; }
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
            castWatch.Update(snapshot, now);
            if (castWatch.Holding)
            { ReleaseKey(); echoDeadline = now + 750; Status = Ui.M("KB_CastHold"); return; }
            if (!snapshot.InputFocused)
            {
                ReleaseKey(); ResetTyping(); Status = Ui.M("KB_WaitInput");
                if (!snapshot.OtherInputFocused && now >= focusRetryAt)
                {
                    input.Focus(); focusRetryAt = now + 150;
                    if (snapshot.PhraseCount > 0 && ++focusAttempts == 20)
                        Plugin.Log?.Warning("[MOGTOME][KBB] Input is visible but text entry has not activated after 20 focus attempts; no characters sent to an unconfirmed target.");
                }
                return;
            }
            focusAttempts = 0;
            if (heldKey.HasValue) return;
            if (expectedText != null)
            {
                if (snapshot.InputText == expectedText)
                {
                    expectedText = null;
                    if (submissionPending) { SubmittedPhrases++; submissionPending = false; }
                }
                else if (snapshot.InputText != previousText)
                {
                    // The game can edit/clear the bar when an enemy dies or a phrase is rejected.
                    // Treat the edit as acknowledgement, re-read the target, and never replay the key.
                    expectedText = target = null;
                    submissionPending = false;
                }
                else if (now >= echoDeadline)
                { Stop(); Status = Ui.M("KB_InputStalled"); return; }
                else return;
            }
            if (now < nextAction) return;
            var rules = new TargetRules(TargetOrder.Smart, 4, now < demotedUntil ? demotedPhrase : null);
            var current = target == null ? TargetPicker.NotShown : TargetPicker.IndexOf(snapshot, target);
            var best = TargetPicker.Choose(snapshot, snapshot.InputText, rules);
            if (current == TargetPicker.NotShown || best != TargetPicker.NotShown && TargetPicker.Rank(snapshot, best, rules) > TargetPicker.Rank(snapshot, current, rules))
            {
                target = best == TargetPicker.NotShown ? null : snapshot.Phrases[best];
                if (target != null && !target.Equals(recentlySubmitted, StringComparison.OrdinalIgnoreCase))
                { var reaction = pace.ReactionDelay(); nextAction = now + reaction; if (reaction > 0) return; }
            }
            if (string.IsNullOrEmpty(target)) { Status = Ui.M("KB_WaitInput"); return; }
            var text = snapshot.InputText;
            Status = Ui.M("KB_Typing", CompletedRuns, SubmittedPhrases);
            if (text.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                if (!Press(GameKey.Enter, now)) { FailInput(); return; }
                var index = TargetPicker.IndexOf(snapshot, target);
                var signature = index < 0 ? 0 : snapshot.ProgressSignature(index);
                repeatStreak = target.Equals(recentlySubmitted, StringComparison.OrdinalIgnoreCase) && MathF.Abs(signature - streakSignature) < 0.5f ? repeatStreak + 1 : 1;
                streakSignature = signature;
                if (repeatStreak >= 3) { demotedPhrase = target; demotedUntil = now + 4000; repeatStreak = 0; }
                recentlySubmitted = target;
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

    private void Expect(string text, long now) { previousText = snapshot.InputText; expectedText = text; echoDeadline = now + 750; }
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
        target = expectedText = previousText = null;
        inputIdentity = 0;
        submissionPending = false;
        nextAction = 0;
    }
    private void FailInput() { Stop(); Status = Ui.M("KB_InputStalled"); }
    private void Finish() { Stop(); Finished = true; Status = Ui.M("KB_Finished", CompletedRuns); }
    internal void FinishIfLimitMet(uint? wallet)
    {
        if (IsRunning && ready && !busy && (!roundSeen || completedRound) && (runLimit > 0 && CompletedRuns >= runLimit ||
            walletTarget.HasValue && wallet.HasValue && wallet >= walletTarget)) Finish();
    }
    internal void StopWithReason(string key) { Stop(); Status = Ui.M(key); }
    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);
    public void Stop()
    {
        IsRunning = Finished = false;
        Interlocked.Exchange(ref stopRequested, 0);
        ReleaseKey(); ResetTyping();
        roundSeen = completedRound = false;
        recentlySubmitted = null;
        castWatch.Reset();
        Status = Ui.M("KB_Stopped");
    }
    public void Dispose() { Stop(); disposed = true; }
}
