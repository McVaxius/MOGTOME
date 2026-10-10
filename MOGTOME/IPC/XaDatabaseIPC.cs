using System;
using System.Collections.Generic;
using AethertekUI.Dalamud;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

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

    internal HashSet<uint> ReadOwned(IEnumerable<uint> itemIds)
        => XaItemOwnership.Query(items.InvokeFunc, storage.InvokeFunc, itemIds,
            Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0, DateTimeOffset.UtcNow);
}
