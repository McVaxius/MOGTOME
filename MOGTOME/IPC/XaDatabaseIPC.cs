using System;
using System.Collections.Generic;
using System.Linq;
using AethertekUI.Dalamud;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using MOGTOME.Services;

namespace MOGTOME.IPC;

internal sealed class XaDatabaseIPC
{
    private readonly ICallGateSubscriber<string, string> items;
    private readonly ICallGateSubscriber<string, string> storage;

    internal XaDatabaseIPC(IDalamudPluginInterface pluginInterface)
    {
        items = pluginInterface.GetIpcSubscriber<string, string>("XA.Database.SearchCurrentCharacterItemsJson");
        storage = pluginInterface.GetIpcSubscriber<string, string>("XA.Database.SearchCharacterStorageItemsJson");
    }

    internal Dictionary<uint, XaItemOwnershipState> ReadOwnership(IEnumerable<uint> itemIds)
        => XaItemOwnership.QueryStates(items.InvokeFunc, storage.InvokeFunc, itemIds,
            Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0, DateTimeOffset.UtcNow);

    internal Dictionary<uint, XaItemStock> ReadStocks(IEnumerable<uint> itemIds)
        => XaItemOwnership.QueryStocks(items.InvokeFunc, storage.InvokeFunc, itemIds,
            Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0, DateTimeOffset.UtcNow);

    internal bool TryCount(uint itemId, out int count)
        => TryCount(itemId, ReadStocks([itemId]).GetValueOrDefault(itemId), out count);

    internal bool TryCount(uint itemId, XaItemStock? stored, out int count)
    {
        count = 0;
        if (!BlundervilleGameAdapter.TryLiveInventoryCounts(itemId, out var live, out _))
        {
            if (stored == null || !Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0) return false;
            count = stored.CountWithLiveInventory(new Dictionary<string, int>());
            return true;
        }
        var containers = live.ToDictionary(pair => ContainerName(pair.Key), pair => pair.Value);
        count = stored?.CountWithLiveInventory(containers) ?? containers.Values.Sum();
        return true;
    }

    // Match XADB's recorded names so live reads replace the same locations, including empty ones.
    internal static string ContainerName(InventoryType type) => type switch
    {
        InventoryType.Inventory1 => "Inventory 1", InventoryType.Inventory2 => "Inventory 2",
        InventoryType.Inventory3 => "Inventory 3", InventoryType.Inventory4 => "Inventory 4",
        InventoryType.EquippedItems => "Equipped",
        InventoryType.ArmoryMainHand => "Armoury - Main Hand", InventoryType.ArmoryOffHand => "Armoury - Off Hand",
        InventoryType.ArmoryHead => "Armoury - Head", InventoryType.ArmoryBody => "Armoury - Body",
        InventoryType.ArmoryHands => "Armoury - Hands", InventoryType.ArmoryLegs => "Armoury - Legs",
        InventoryType.ArmoryFeets => "Armoury - Feet", InventoryType.ArmoryEar => "Armoury - Earring",
        InventoryType.ArmoryNeck => "Armoury - Necklace", InventoryType.ArmoryWrist => "Armoury - Bracelet",
        InventoryType.ArmoryRings => "Armoury - Ring", InventoryType.ArmorySoulCrystal => "Armoury - Soul Crystal",
        InventoryType.SaddleBag1 => "Saddlebag 1", InventoryType.SaddleBag2 => "Saddlebag 2",
        InventoryType.PremiumSaddleBag1 => "Premium Saddlebag 1", InventoryType.PremiumSaddleBag2 => "Premium Saddlebag 2",
        _ => type.ToString(),
    };
}
