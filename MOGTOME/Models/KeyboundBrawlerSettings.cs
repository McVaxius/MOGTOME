using System;
using System.Collections.Generic;

namespace MOGTOME.Models;

public enum KeyboundPartyRole { Leader, Member }

/// <summary>Account-owned farming preparation and independent MGC shopping settings.</summary>
public sealed class KeyboundBrawlerSettings
{
    private int legacyTypingWpm = 80;
    private bool legacyThrower;
    private double? stage12TypingWpm;
    private double? stage3TypingWpm;

    // Read older account settings without writing the removed mode/single speed back out.
    // Resolve missing stage values lazily so explicit stage settings win in any JSON order.
    public int TypingWpm { set => legacyTypingWpm = value; }
    public int PlayMode { set => legacyThrower = value == 1; }
    private double LegacySelectedTypingWpm => Math.Clamp(legacyTypingWpm, 10, 300) / (legacyThrower ? 2d : 1d);
    public double Stage12TypingWpm
    {
        get => stage12TypingWpm ?? LegacySelectedTypingWpm;
        set => stage12TypingWpm = double.IsFinite(value) ? Math.Clamp(value, 5, 300) : 80;
    }
    public double Stage3TypingWpm
    {
        get => stage3TypingWpm ?? LegacySelectedTypingWpm;
        set => stage3TypingWpm = double.IsFinite(value) ? Math.Clamp(value, 5, 300) : 80;
    }
    public KeyboundPartyRole PartyRole { get; set; }
    public bool RunLimitEnabled { get; set; }
    public int RunLimit { get; set; } = 1;
    public bool WalletTargetEnabled { get; set; } = true;
    public int WalletTarget { get; set; } = 5470;
    // One of each verified non-coffer reward. Deserialization replaces this list with saved targets.
    public Dictionary<uint, int> PurchaseTargets { get; set; } = new()
    {
        [52323] = 1, [52324] = 1, [52325] = 1, [52326] = 1, [52327] = 1,
        [52328] = 1, [52329] = 1, [52330] = 1, [52331] = 1, [52332] = 1,
        [52647] = 1, [52287] = 1, [52261] = 1, [52283] = 1,
        [52354] = 1, [52355] = 1, [52356] = 1, [51997] = 1, [52291] = 1,
    };
    public bool HideOwned { get; set; }
    public bool ShopWhenFinished { get; set; } = true;
    public bool PurchaseReviewRequired { get; set; }
    public BlundervilleEndingLocation EndingLocation { get; set; }
}
