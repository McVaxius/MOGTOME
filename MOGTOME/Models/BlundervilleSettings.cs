using System;
using System.Collections.Generic;

namespace MOGTOME.Models;

public enum BlundervilleReloadScenario { None, Start, Buy }
public enum BlundervilleEndingLocation { Stay, Uldah, Gridania, Limsa, Ishgard, Crystarium, Sharlayan, Tuliyollal }

public sealed class BlundervilleSettings
{
    public bool RunLimitEnabled { get; set; } = true;
    public int RunLimit { get; set; } = 1;
    public bool WalletTargetEnabled { get; set; }
    public int WalletTarget { get; set; } = 100;
    public Dictionary<uint, int> PurchaseTargets { get; set; } = [];
    public bool ShopWhenFinished { get; set; }
    public BlundervilleEndingLocation EndingLocation { get; set; }
    public BlundervilleReloadScenario ReloadScenario { get; set; }
    public bool PurchaseReviewRequired { get; set; }
}

internal enum BlundervilleRole { Unknown, Solo, Leader, Member }

// Session progress deliberately does not touch duty counters or run-history statistics.
internal sealed class BlundervilleProgress
{
    public int Cycles { get; private set; }
    public bool InArena { get; private set; }
    public bool RegistrationSubmitted { get; private set; }
    public bool CommenceSubmitted { get; private set; }
    private bool eliminated;
    private bool exitSubmitted;

    public void SubmitRegistration() => RegistrationSubmitted = true;
    public bool TrySubmitCommence()
    {
        if (CommenceSubmitted) return false;
        CommenceSubmitted = true;
        return true;
    }
    public void ResetEntry() => RegistrationSubmitted = CommenceSubmitted = false;
    public void Enter() { InArena = true; ResetEntry(); }
    public void ObserveElimination() { if (InArena) eliminated = true; }
    public void SubmitExit() { if (InArena && eliminated) exitSubmitted = true; }
    public bool Return()
    {
        if (!InArena) return false;
        var completed = eliminated && exitSubmitted;
        InArena = eliminated = exitSubmitted = false;
        ResetEntry();
        if (completed) Cycles++;
        return completed;
    }

    internal static BlundervilleRole Role(bool crossWorld, bool crossLeader, int count, ulong self, ulong leader)
        => self == 0 ? BlundervilleRole.Unknown : crossWorld
            ? (crossLeader ? BlundervilleRole.Leader : BlundervilleRole.Member)
            : count <= 1 ? BlundervilleRole.Solo
            : leader == 0 ? BlundervilleRole.Unknown
            : self == leader ? BlundervilleRole.Leader : BlundervilleRole.Member;

    internal static bool HasLimit(BlundervilleSettings settings)
        => (settings.RunLimitEnabled && settings.RunLimit > 0) ||
           (settings.WalletTargetEnabled && settings.WalletTarget >= 0);
    internal static bool WalletMet(BlundervilleSettings settings, uint wallet)
        => settings.WalletTargetEnabled && settings.WalletTarget >= 0 && wallet >= settings.WalletTarget;
    internal bool Finished(BlundervilleSettings settings, uint wallet)
        => (settings.RunLimitEnabled && settings.RunLimit > 0 && Cycles >= settings.RunLimit) || WalletMet(settings, wallet);
    internal static int Deficit(int desired, int current) => Math.Max(0, desired - current);
    internal static bool PurchaseVerified(int beforeCount, uint beforeWallet, uint received, uint price, int count, uint wallet)
        => (long)count == (long)beforeCount + received && (long)wallet + price == beforeWallet;
}

internal sealed class BlundervilleReloadAttempt
{
    private bool captured;
    private bool cancelled;
    private BlundervilleReloadScenario pending;
    public bool IsCancelled => cancelled;
    public void Capture(BlundervilleReloadScenario selected)
    {
        if (captured) return;
        captured = true;
        if (!cancelled && Enum.IsDefined(selected)) pending = selected;
    }
    public BlundervilleReloadScenario Consume(bool ready)
    {
        if (!ready || cancelled) return BlundervilleReloadScenario.None;
        var result = pending;
        pending = BlundervilleReloadScenario.None; // Consume before any cleanup or reentrant action.
        return result;
    }
    public void Cancel() { cancelled = true; pending = BlundervilleReloadScenario.None; }
}
