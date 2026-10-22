using System.Collections.Generic;

namespace MOGTOME.Models;

/// <summary>Account-owned farming preparation and independent MGC shopping settings.</summary>
public sealed class KeyboundBrawlerSettings
{
    public bool RunLimitEnabled { get; set; } = true;
    public int RunLimit { get; set; } = 1;
    public bool WalletTargetEnabled { get; set; }
    public int WalletTarget { get; set; } = 100;
    public Dictionary<uint, int> PurchaseTargets { get; set; } = [];
    public bool HideOwned { get; set; }
    public bool ShopWhenFinished { get; set; }
    public bool PurchaseReviewRequired { get; set; }
    public BlundervilleEndingLocation EndingLocation { get; set; }
}
