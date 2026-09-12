using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class BossHandlerService
{
    private readonly IPluginLog log;
    private Configuration config; // Remove readonly to allow updates
    private readonly DutyState state;
    private readonly VNavIPC vNavIPC;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;
    private readonly ConsumableInventoryService consumableInventoryService;

    // BNpcName row IDs are independent of the client language.
    internal const uint NeroNameId = 2135;
    internal const uint GaiusNameId = 2136;
    internal const uint PhantomGaiusNameId = 11285;
    internal const uint ColossusNameId = 2134;
    internal const uint UltimaNameId = 2137;

    // Potion cooldown
    private DateTime lastPotionUse = DateTime.MinValue;
    private const float PotionCooldown = 15.0f;

    public BossHandlerService(
        IPluginLog log, Configuration config, DutyState state,
        VNavIPC vNavIPC, ICommandManager commandManager, ICondition condition, ConsumableInventoryService consumableInventoryService)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.vNavIPC = vNavIPC;
        this.commandManager = commandManager;
        this.condition = condition;
        this.consumableInventoryService = consumableInventoryService;
    }

    // ADD EVENT HANDLER
    public void SubscribeToConfigChanges(ConfigManager configManager)
    {
        configManager.ConfigurationChanged += OnConfigurationChanged;
        log.Debug("[MOGTOME][BossHandler] Subscribed to configuration changes");
    }

    private void OnConfigurationChanged(Configuration newConfig)
    {
        this.config = newConfig;
        log.Information($"[MOGTOME][BossHandler] Configuration updated - PotionItemId: {config.PotionItemId}, PotionName: '{config.PotionItemName}', HQ={config.PotionUseHighQuality}");
    }

    public void Update()
    {
        if (!state.IsInDuty) return;
        // Condition[26] = InCombat
        if (!condition[26]) return;

        try
        {
            var target = Plugin.TargetManager.Target;
            if (target == null) return;

            var battleTarget = target as IBattleChara;
            var targetName = battleTarget?.NameId ?? 0;

            // Stop navigation during boss fights
            StopNavDuringBoss(targetName);

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
                UseBossActions(targetName, player.ClassJob.Value.Role,
                    (float)player.CurrentHp / player.MaxHp * 100f,
                    battleTarget == null ? float.NaN : (float)battleTarget.CurrentHp / battleTarget.MaxHp * 100f,
                    static (id, type) => { GameHelpers.TryUseCombatAction(id, type); });

            // Potion usage
            HandlePotions(targetName);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossHandler] Update failed: {ex.Message}");
        }
    }

    private void StopNavDuringBoss(uint targetName)
    {
        if (targetName == NeroNameId || targetName == GaiusNameId ||
            targetName == PhantomGaiusNameId || targetName == ColossusNameId)
        {
            vNavIPC.Stop();
        }
    }

    internal static void UseBossActions(uint nameId, byte role, float playerHpPct,
        float targetHpPct, Action<uint, ActionType> useAction)
    {
        if (role == 1 && nameId == GaiusNameId)
        {
            if (targetHpPct > 5 && targetHpPct < 95)
                useAction(7531, ActionType.Action); // Rampart
            if (targetHpPct > 5 && targetHpPct < 50)
                useAction(16140, ActionType.Action); // Camouflage
        }
        if (role != 2 && role != 3) return;
        if (playerHpPct < 50)
            useAction(7541, ActionType.Action); // Second Wind
        if (nameId == PhantomGaiusNameId || nameId == UltimaNameId && targetHpPct < 30)
            useAction(3, ActionType.GeneralAction); // Let the game select the Limit Break.
    }

    private void HandlePotions(uint targetName)
    {
        consumableInventoryService.Refresh();
        if (config.PotionItemId <= 0) return;

        if (!state.PotionsAvailable) return;

        var now = DateTime.UtcNow;
        if ((now - lastPotionUse).TotalSeconds < PotionCooldown) return;

        // Determine which boss to pot on
        var potTarget = config.PotionTarget == 0 ? GaiusNameId : PhantomGaiusNameId;

        // Pot on the configured target, Colossus, or Ultima
        if (targetName != potTarget && targetName != ColossusNameId && targetName != UltimaNameId) return;

        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target == null) return;
        var hpPct = (float)target.CurrentHp / target.MaxHp * 100f;

        // Don't pot if target is almost dead or full HP
        if (hpPct <= 20 || hpPct >= 100) return;

        // Special case: Ultima Weapon pot at 80-100% only
        if (targetName == UltimaNameId && (hpPct < 80 || hpPct >= 100)) return;

        try
        {
            var qualityLabel = config.PotionUseHighQuality ? "HQ" : "NQ";
            log.Information($"[MOGTOME][BossHandler] Using potion: {config.PotionItemName} [{qualityLabel}] on {target.Name.TextValue}");

            var result = GameHelpers.UseItem((uint)config.PotionItemId, config.PotionUseHighQuality);
            if (result)
            {
                log.Information($"[MOGTOME][BossHandler] Successfully used {config.PotionItemName} [{qualityLabel}] on {target.Name.TextValue}");
            }
            else
            {
                log.Warning($"[MOGTOME][BossHandler] Failed to use {config.PotionItemName} [{qualityLabel}] - UseItem returned false");
            }

            consumableInventoryService.Refresh(force: true);
            
            lastPotionUse = now;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossHandler] Potion use failed: {ex.Message}");
        }
    }
}
