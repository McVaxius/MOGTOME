using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using MOGTOME.Models;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace MOGTOME.Services;

internal static unsafe class ShopOfferEligibility
{
    internal static ShopOfferGate ReadGate(SpecialShop shop, SpecialShop.ItemStruct row)
        => new(shop.Quest.RowId, row.Quest.RowId, row.AchievementUnlock.RowId,
            shop.RequiredContentFinderCondition.RowId, shop.RequiredContentFinderConditionComplete,
            shop.RequiredFestival.RowId, shop.RequiredFestivalPhase);

    internal static ShopOfferAvailability Read(ShopOfferGate gate)
    {
        try { return gate.Resolve(Quest, AchievementComplete, Content, Festival); }
        catch { return ShopOfferAvailability.Unknown; }
    }

    // Use the existing catalog refresh lifecycle; native loading owns the request.
    internal static void RefreshAchievements(IEnumerable<ShopOfferGate> gates)
    {
        if (!Ready || !gates.Any(gate => gate.Achievement != 0)) return;
        try
        {
            var achievements = GameAchievement.Instance();
            if (achievements != null && !achievements->IsLoaded()) achievements->RequestCompletedAchievements();
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[MOGTOME][Shop] achievement eligibility unavailable"); }
    }

    private static bool Ready => Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded;
    private static bool? Quest(uint id) => Ready ? QuestManager.IsQuestComplete(id) : null;

    private static bool? AchievementComplete(uint id)
    {
        if (!Ready) return null;
        var achievements = GameAchievement.Instance();
        if (achievements == null) return null;
        if (achievements->IsItemBarterWarningAchievementComplete(id)) return true;
        // An empty/unloaded bitmap is not proof that the achievement is unfinished.
        return achievements->IsLoaded() ? achievements->IsComplete(checked((int)id)) : null;
    }

    private static bool? Content(uint id, bool completed)
    {
        if (!Ready || !Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(id, out var row)) return null;
        if (row.Content.Is<Lumina.Excel.Sheets.InstanceContent>()) return completed
            ? UIState.IsInstanceContentCompleted(row.Content.RowId) : UIState.IsInstanceContentUnlocked(row.Content.RowId);
        if (row.Content.Is<Lumina.Excel.Sheets.PublicContent>()) return completed
            ? UIState.IsPublicContentCompleted(row.Content.RowId) : UIState.IsPublicContentUnlocked(row.Content.RowId);
        return null;
    }

    private static bool? Festival(uint id, uint phase)
    {
        if (!Ready) return null;
        var game = GameMain.Instance();
        var player = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (game == null || player == null) return null;
        var active = new List<(uint Id, uint Phase)>();
        foreach (var entry in game->ActiveFestivals) active.Add((entry.Id, entry.Phase));
        for (var index = 0; index < player->ActiveFestivalIds.Length; index++)
            active.Add((player->ActiveFestivalIds[index], player->ActiveFestivalPhases[index]));
        return MoogleShopMath.FestivalEligible(id, phase, active);
    }
}
