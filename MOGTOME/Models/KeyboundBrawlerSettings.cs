using System.Collections.Generic;

namespace MOGTOME.Models;

/// <summary>Account-owned farming preparation and independent MGC shopping settings.</summary>
public sealed class KeyboundBrawlerSettings
{
    public int TypingWpm { get; set; } = 80;
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
