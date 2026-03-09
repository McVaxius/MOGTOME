using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Threading.Tasks;

namespace MOGTOME.Services;

public class MaintenanceManager : IDisposable
{
    private readonly Configuration config;
    private DateTime lastRepairCheck = DateTime.MinValue;
    private DateTime lastFoodCheck = DateTime.MinValue;
    private DateTime lastEquipCheck = DateTime.MinValue;
    private const int REPAIR_CHECK_INTERVAL = 30000; // 30 seconds
    private const int FOOD_CHECK_INTERVAL = 60000; // 1 minute
    private const int EQUIP_CHECK_INTERVAL = 120000; // 2 minutes

    public MaintenanceManager(Configuration config)
    {
        this.config = config;
        Service.Log.Info("MaintenanceManager initialized");
    }

    public async Task<bool> CheckAndPerformMaintenance()
    {
        try
        {
            if (!config.CanStartAutomation())
            {
                return false;
            }

            var maintenancePerformed = false;

            // Check repair needs
            if (config.AutoRepair && ShouldCheckRepair())
            {
                if (await CheckAndRepair())
                {
                    maintenancePerformed = true;
                }
            }

            // Check food needs
            if (config.AutoFood && ShouldCheckFood())
            {
                if (await CheckAndEat())
                {
                    maintenancePerformed = true;
                }
            }

            // Check gear needs
            if (config.AutoEquip && ShouldCheckEquip())
            {
                if (await CheckAndEquip())
                {
                    maintenancePerformed = true;
                }
            }

            return maintenancePerformed;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in maintenance check: {ex.Message}");
            return false;
        }
    }

    private bool ShouldCheckRepair()
    {
        return (DateTime.Now - lastRepairCheck).TotalMilliseconds >= REPAIR_CHECK_INTERVAL;
    }

    private bool ShouldCheckFood()
    {
        return (DateTime.Now - lastFoodCheck).TotalMilliseconds >= FOOD_CHECK_INTERVAL;
    }

    private bool ShouldCheckEquip()
    {
        return (DateTime.Now - lastEquipCheck).TotalMilliseconds >= EQUIP_CHECK_INTERVAL;
    }

    private async Task<bool> CheckAndRepair()
    {
        try
        {
            lastRepairCheck = DateTime.Now;

            if (!NeedsRepair())
            {
                Service.Log.Debug("No repair needed");
                return false;
            }

            Service.Log.Info($"Repair needed (threshold: {config.RepairThreshold}%)");

            // Try self-repair first
            if (await AttemptSelfRepair())
            {
                Service.Log.Info("Self-repair successful");
                return true;
            }

            // Fall back to NPC repair
            if (await AttemptNPCRepair())
            {
                Service.Log.Info("NPC repair successful");
                return true;
            }

            Service.Log.Warning("All repair attempts failed");
            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error during repair: {ex.Message}");
            return false;
        }
    }

    private bool NeedsRepair()
    {
        try
        {
            var localPlayer = Service.ClientState.LocalPlayer;
            if (localPlayer == null) return false;

            // Check all equipment slots for low durability
            foreach (var slot in Enum.GetValues<EquipmentSlot>())
            {
                var item = localPlayer.GetEquipment(slot);
                if (item == null) continue;

                var durabilityPercent = (item.Durability * 100) / item.MaxDurability;
                if (durabilityPercent <= config.RepairThreshold)
                {
                    Service.Log.Debug($"Equipment slot {slot} needs repair: {durabilityPercent}%");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking repair needs: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> AttemptSelfRepair()
    {
        try
        {
            // Check for dark matter
            var darkMatterId = 33916; // Grade 8 Dark Matter
            var inventory = Service.PluginInterface.GetFramework().GetUiModule()->GetRaptureModule()->GetModule<InventoryManager>();
            
            unsafe
            {
                var container = inventory->GetInventoryContainer(InventoryType.Inventory);
                var hasDarkMatter = false;
                
                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item->ItemID == darkMatterId && item->Quantity > 0)
                    {
                        hasDarkMatter = true;
                        break;
                    }
                }

                if (!hasDarkMatter)
                {
                    Service.Log.Debug("No dark matter available for self-repair");
                    return false;
                }
            }

            // Open repair dialog
            Service.Chat.ExecuteCommand("/generalaction repair");
            await Task.Delay(1000);

            // Click repair button
            var success = await ClickRepairButton(true);
            
            if (success)
            {
                await Task.Delay(2000); // Wait for repair animation
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error during self-repair: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> AttemptNPCRepair()
    {
        try
        {
            // Check if we're near an inn
            if (!IsNearInn())
            {
                Service.Log.Debug("Not near an inn for NPC repair");
                return false;
            }

            // Target innkeeper and interact
            var innkeeper = FindNearestInnkeeper();
            if (innkeeper == null)
            {
                Service.Log.Debug("No innkeeper found");
                return false;
            }

            Service.TargetManager.Target = innkeeper;
            await Task.Delay(500);

            Service.Chat.ExecuteCommand("/interact");
            await Task.Delay(1000);

            // Navigate repair dialog
            var success = await NavigateNPCRepairDialog();
            
            return success;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error during NPC repair: {ex.Message}");
            return false;
        }
    }

    private bool IsNearInn()
    {
        var territory = Service.ClientState.TerritoryType;
        return territory is 177 or 178 or 179; // Gridania, Limsa, Ul'dah inns
    }

    private IGameObject? FindNearestInnkeeper()
    {
        var innkeepers = new[] { "Antoinaut", "Mytesyn", "Otopa" }; // Gridania, Limsa, Ul'dah
        
        foreach (var name in innkeepers)
        {
            var objects = Service.ObjectTable.Where(obj => 
                obj.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) &&
                obj.IsTargetable);
            
            var nearest = objects.OrderBy(obj => Vector3.Distance(
                Service.ClientState.LocalPlayer?.Position ?? Vector3.Zero, 
                obj.Position)).FirstOrDefault();
            
            if (nearest != null) return nearest;
        }

        return null;
    }

    private async Task<bool> NavigateNPCRepairDialog()
    {
        try
        {
            // This would need to be implemented based on the actual dialog structure
            // For now, we'll use a placeholder
            
            await Task.Delay(1000);
            Service.Chat.ExecuteCommand("/callback _Notification true 0 17");
            await Task.Delay(500);
            Service.Chat.ExecuteCommand("/callback ContentsFinderConfirm true 9");
            await Task.Delay(1000);
            
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error navigating NPC repair dialog: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ClickRepairButton(bool selfRepair)
    {
        try
        {
            var addonPtr = Service.GameGui.GetAddonByName("Repair", 1);
            if (addonPtr == 0)
            {
                Service.Log.Warning("Repair addon not found");
                return false;
            }

            unsafe
            {
                var addon = (AtkUnitBase*)addonPtr;
                var values = stackalloc AtkValue[2];
                values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool;
                values[0].Byte = selfRepair ? (byte)1 : (byte)0;
                values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[1].Int = 0;
                
                addon->FireCallback(2, values);
                Service.Log.Debug($"Clicked repair button: selfRepair={selfRepair}");
                
                // Handle confirmation dialog
                await Task.Delay(1000);
                await ClickYesIfVisible();
                
                return true;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking repair button: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CheckAndEat()
    {
        try
        {
            lastFoodCheck = DateTime.Now;

            if (HasFoodBuff())
            {
                Service.Log.Debug("Food buff already active");
                return false;
            }

            if (!HasFoodItem())
            {
                Service.Log.Debug($"No {config.FoodItem} available");
                return false;
            }

            Service.Log.Info($"Eating {config.FoodItem}");

            Service.Chat.ExecuteCommand($"/item {config.FoodItem}");
            await Task.Delay(2000);

            Service.Log.Info($"Consumed {config.FoodItem}");
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error during food consumption: {ex.Message}");
            return false;
        }
    }

    private bool HasFoodBuff()
    {
        try
        {
            var localPlayer = Service.ClientState.LocalPlayer;
            if (localPlayer == null) return false;

            // Check for food buff status (48 is the food status effect)
            return localPlayer.StatusList.Any(status => status.StatusId == 48);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking food buff: {ex.Message}");
            return false;
        }
    }

    private bool HasFoodItem()
    {
        try
        {
            var inventory = Service.PluginInterface.GetFramework().GetUiModule()->GetRaptureModule()->GetModule<InventoryManager>();
            
            unsafe
            {
                var container = inventory->GetInventoryContainer(InventoryType.Inventory);
                
                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item->ItemID == config.FoodItemId && item->Quantity > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking food item: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CheckAndEquip()
    {
        try
        {
            lastEquipCheck = DateTime.Now;

            Service.Log.Info("Equipping recommended gear");

            Service.Chat.ExecuteCommand("/equiprecommended");
            await Task.Delay(2000);

            Service.Log.Info("Equipped recommended gear");
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error during gear equip: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ClickYesIfVisible()
    {
        try
        {
            var addonPtr = Service.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == 0)
            {
                return false;
            }

            unsafe
            {
                var addon = (AtkUnitBase*)addonPtr;
                if (!addon->IsVisible)
                {
                    return false;
                }

                var values = stackalloc AtkValue[2];
                values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[0].Int = 0; // Yes button
                values[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                values[1].Int = 0;

                addon->FireCallback(2, values);
                Service.Log.Debug("Clicked Yes on SelectYesno dialog");
                return true;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error clicking Yes dialog: {ex.Message}");
            return false;
        }
    }

    public void Reset()
    {
        lastRepairCheck = DateTime.MinValue;
        lastFoodCheck = DateTime.MinValue;
        lastEquipCheck = DateTime.MinValue;
        Service.Log.Info("MaintenanceManager reset");
    }

    public void Dispose()
    {
        Service.Log.Info("MaintenanceManager disposed");
    }
}
