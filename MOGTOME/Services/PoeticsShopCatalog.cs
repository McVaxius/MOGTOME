using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using MOGTOME.Models;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace MOGTOME.Services;

internal sealed record PoeticsShopVendor(uint NpcId, uint TerritoryId, Vector3 Position,
    IReadOnlyList<string> MenuLabels)
{
    internal PoeticsShopVendor? MatchCurrent(IEnumerable<PoeticsShopVendor> candidates)
        => candidates.FirstOrDefault(candidate => candidate.NpcId == NpcId &&
            candidate.TerritoryId == TerritoryId && candidate.Position == Position);
}

internal sealed unsafe class PoeticsShopCatalog
{
    internal const uint CurrencyItemId = 28;
    private readonly Dictionary<uint, IReadOnlyList<PoeticsShopVendor>> vendors = [];
    internal IReadOnlyList<PoeticsShopOffer> Offers { get; private set; } = [];

    internal void LoadCatalog()
    {
        Offers = [];
        vendors.Clear();
        // Small currency IDs are Tomestones identities, not Item row IDs.
        var currencies = Plugin.DataManager.GetExcelSheet<TomestonesItem>()
            .Where(row => row.Tomestones.RowId != 0 && row.Item.RowId != 0)
            .GroupBy(row => row.Tomestones.RowId)
            .Select(group => (Id: group.Key, Items: group.Select(row => row.Item.RowId).Distinct().ToArray()))
            .Where(group => group.Id != 0 && group.Items.Length == 1 && group.Items[0] != 0)
            .ToDictionary(group => group.Id, group => group.Items[0]);
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var rewards = Plugin.DataManager.GetExcelSheet<GCSupplyDutyReward>();
        var offers = new List<PoeticsShopOffer>();
        foreach (var shop in Plugin.DataManager.GetExcelSheet<SpecialShop>())
        {
            if (shop.UseCurrencyType is not (2 or 4 or 16)) continue;
            foreach (var row in shop.Item)
            {
                var received = row.ReceiveItems.Where(entry => entry.Item.RowId != 0).ToArray();
                var paid = row.ItemCosts.Where(entry => entry.ItemCost.RowId != 0 || entry.CurrencyCost != 0 ||
                    entry.CollectabilityCost != 0).ToArray();
                if (received.Length != 1 || received[0].Item.RowId == 0 || received[0].ReceiveCount != 1 ||
                    received[0].ReceiveHq || paid.Length != 1 || paid[0].ItemCost.RowId == 0 ||
                    paid[0].CurrencyCost == 0 || paid[0].CollectabilityCost != 0)
                    continue;
                var currency = PoeticsShopping.ResolveCurrency(shop.UseCurrencyType, paid[0].CostType,
                    paid[0].ItemCost.RowId, currencies);
                if (currency != CurrencyItemId || !items.TryGetRow(received[0].Item.RowId, out var item) ||
                    item.EquipSlotCategory.RowId == 0 || item.Desynth == 0 || item.Rarity is not (2 or 3 or 7) ||
                    !rewards.TryGetRow(item.LevelItem.RowId, out var reward) || reward.SealsExpertDelivery == 0)
                    continue;
                var offer = new MoogleShopOffer(shop.RowId, shop.Name.ToString(), item.RowId, 1,
                    CurrencyItemId, [new(CurrencyItemId, paid[0].CurrencyCost)])
                {
                    Gate = ShopOfferEligibility.ReadGate(shop, row),
                    MissingKind = ShopMissingKind.ShopEquipment,
                };
                offers.Add(new(offer, reward.SealsExpertDelivery));
            }
        }
        ReadVendors(offers.Select(entry => entry.Offer).GroupBy(offer => offer.ShopId)
            .ToDictionary(group => group.Key, group => group.First().ShopName));
        // Missing vendor evidence is Unknown, not proof that the offer is unavailable.
        Offers = offers.ToArray();
    }

    internal IReadOnlyList<PoeticsShopVendor> Vendors(MoogleShopOffer offer)
        => vendors.TryGetValue(offer.ShopId, out var result) ? result : [];

    internal ShopOfferAvailability ReadAvailability(MoogleShopOffer offer, Func<PoeticsShopVendor, bool> reachable)
    {
        var availability = ShopOfferEligibility.Read(offer.Gate);
        if (availability == ShopOfferAvailability.Locked) return availability;
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded) return ShopOfferAvailability.Unknown;
        var player = NativePlayerState.Instance();
        if (player == null) return ShopOfferAvailability.Unknown;
        if (player->GrandCompany == 0 || player->GetGrandCompanyRank() < 6) return ShopOfferAvailability.Locked;
        var locations = Vendors(offer);
        var reachableVendors = locations.Where(reachable).ToArray();
        if (locations.Count > 0 && reachableVendors.Length == 0) return ShopOfferAvailability.Locked;
        return PoeticsShopping.ResolveVendorAvailability(availability, reachableVendors.Select(ReadVendorAvailability));
    }

    internal static ShopOfferAvailability ReadVendorAvailability(PoeticsShopVendor vendor)
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded) return ShopOfferAvailability.Unknown;
        // Positive completed-quest conditions verified from each NPC's native Story listener.
        // LayerSet references and LGB VersionMask values are not quest identities.
        return vendor.NpcId switch
        {
            1008118 or 1008119 or 1012228 => ShopOfferAvailability.Available,
            1019450 => ShopOfferEligibility.Read(new(ShopQuest: 68089)),
            1027564 => ShopOfferEligibility.Read(new(ShopQuest: 69190)),
            1037301 => ShopOfferEligibility.Read(new(ShopQuest: 70000)),
            _ => ShopOfferAvailability.Unknown,
        };
    }

    private static uint VerifiedVendorTerritory(uint npcId) => npcId switch
    {
        1008118 or 1008119 => 156,
        1012228 => 478,
        1019450 => 635,
        1027564 => 820,
        1037301 => 963,
        _ => 0,
    };

    private static IReadOnlyList<(uint NpcId, uint TerritoryId, Vector3 Position)> ReadLocations()
    {
        var locations = new List<(uint NpcId, uint TerritoryId, Vector3 Position)>();
        foreach (var level in Plugin.DataManager.GetExcelSheet<Level>())
            if (level.Type == 8 && level.Object.RowId != 0 && level.Territory.RowId != 0 &&
                float.IsFinite(level.X) && float.IsFinite(level.Y) && float.IsFinite(level.Z))
                locations.Add((level.Object.RowId, level.Territory.RowId, new(level.X, level.Y, level.Z)));

        // Current qualified vendors were verified in these five native territory files.
        // Read actual 3D transforms; do not infer positions from 2D map markers.
        foreach (var territoryId in new uint[] { 156, 478, 635, 820, 963 })
        {
            var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);
            var bg = territory.Bg.ToString();
            var index = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (index < 0) continue;
            var file = Plugin.DataManager.GetFile<LgbFile>("bg/" + bg[..(index + 1)] + "level/planevent.lgb");
            if (file == null) continue;
            foreach (var layer in file.Layers)
            {
                if (layer.FestivalID != 0) continue;
                foreach (var entry in layer.InstanceObjects)
                {
                    if (entry.AssetType != LayerEntryType.EventNPC || entry.Object is not LayerCommon.ENPCInstanceObject npc ||
                        VerifiedVendorTerritory(npc.ParentData.ParentData.BaseId) != territoryId ||
                        npc.ParentData.PopWeather != 0 || npc.ParentData.PopTimeStart != 0 || npc.ParentData.PopTimeEnd != 0)
                        continue;
                    var position = entry.Transform.Translation;
                    if (float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
                        locations.Add((npc.ParentData.ParentData.BaseId, territoryId, new(position.X, position.Y, position.Z)));
                }
            }
        }
        return locations;
    }

    private void ReadVendors(IReadOnlyDictionary<uint, string> shops)
    {
        if (shops.Count == 0) return;
        var npcs = Plugin.DataManager.GetExcelSheet<ENpcBase>();
        var topics = Plugin.DataManager.GetExcelSheet<TopicSelect>();
        var preHandlers = Plugin.DataManager.GetExcelSheet<PreHandler>();
        var mapped = new Dictionary<uint, List<PoeticsShopVendor>>();
        var spawns = ReadLocations().Distinct().GroupBy(location => location.NpcId);
        foreach (var locations in spawns)
        {
            if (!npcs.TryGetRow(locations.Key, out var npc)) continue;
            var events = new Queue<(RowRef Handler, string[] Menus)>();
            foreach (var handler in npc.ENpcData) events.Enqueue((handler, []));
            var visited = new HashSet<uint>();
            while (events.TryDequeue(out var current))
            {
                var handler = current.Handler;
                if (handler.RowId == 0 || !visited.Add(handler.RowId)) continue;
                if (handler.Is<SpecialShop>() && shops.TryGetValue(handler.RowId, out var name))
                {
                    var labels = current.Menus.Append(name).Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (!mapped.TryGetValue(handler.RowId, out var shopVendors))
                        mapped[handler.RowId] = shopVendors = [];
                    foreach (var location in locations)
                    {
                        var vendor = new PoeticsShopVendor(npc.RowId, location.TerritoryId, location.Position, labels);
                        if (!shopVendors.Any(existing => existing.NpcId == vendor.NpcId &&
                            existing.TerritoryId == vendor.TerritoryId && existing.Position == vendor.Position &&
                            existing.MenuLabels.SequenceEqual(vendor.MenuLabels))) shopVendors.Add(vendor);
                    }
                }
                else if (handler.Is<TopicSelect>() && topics.TryGetRow(handler.RowId, out var topic))
                {
                    var menus = string.IsNullOrWhiteSpace(topic.Name.ToString()) ? current.Menus
                        : current.Menus.Append(topic.Name.ToString()).ToArray();
                    foreach (var child in topic.Shop) events.Enqueue((child, menus));
                }
                else if (handler.Is<PreHandler>() && preHandlers.TryGetRow(handler.RowId, out var preHandler))
                    events.Enqueue((preHandler.Target, current.Menus));
            }
        }
        foreach (var shop in mapped) vendors[shop.Key] = shop.Value.ToArray();
    }
}
