using System;

namespace MOGTOME.Models;

internal enum ShopOfferAvailability { Unknown, Available, Locked }

internal sealed record ShopOfferGate(uint ShopQuest = 0, uint ItemQuest = 0, uint Achievement = 0,
    uint ContentFinderCondition = 0, bool ContentComplete = false, uint Festival = 0, uint FestivalPhase = 0)
{
    internal ShopOfferAvailability Resolve(Func<uint, bool?> quest, Func<uint, bool?> achievement,
        Func<uint, bool, bool?> content, Func<uint, uint, bool?> festival)
    {
        var checks = new[]
        {
            ShopQuest == 0 ? true : quest(ShopQuest), ItemQuest == 0 ? true : quest(ItemQuest),
            Achievement == 0 ? true : achievement(Achievement),
            ContentFinderCondition == 0 ? true : content(ContentFinderCondition, ContentComplete),
            Festival == 0 ? true : festival(Festival, FestivalPhase),
        };
        if (Array.Exists(checks, check => check == false)) return ShopOfferAvailability.Locked;
        return Array.Exists(checks, check => !check.HasValue) ? ShopOfferAvailability.Unknown : ShopOfferAvailability.Available;
    }
}
