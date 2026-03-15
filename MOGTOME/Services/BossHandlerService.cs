using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public class BossHandlerService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly VNavIPC vNavIPC;
    private readonly ICommandManager commandManager;
    private readonly ICondition condition;

    // Boss names
    private const string NeroName = "Nero tol Scaeva";
    private const string GaiusName = "Gaius van Baelsar";
    private const string PhantomGaiusName = "Phantom Gaius";
    private const string ColossusName = "Mark II Magitek Colossus";
    private const string UltimaName = "The Ultima Weapon";

    // Potion cooldown
    private DateTime lastPotionUse = DateTime.MinValue;
    private const float PotionCooldown = 15.0f;

    public BossHandlerService(
        IPluginLog log, Configuration config, DutyState state,
        VNavIPC vNavIPC, ICommandManager commandManager, ICondition condition)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.vNavIPC = vNavIPC;
        this.commandManager = commandManager;
        this.condition = condition;
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

            var targetName = target.Name.ToString();
            if (string.IsNullOrEmpty(targetName)) return;

            // Stop navigation during boss fights
            StopNavDuringBoss(targetName);

            // Tank mitigation on Gaius
            HandleTankMitigation(targetName);

            // DPS abilities
            HandleDpsAbilities(targetName);

            // Potion usage
            HandlePotions(targetName);
        }
        catch (Exception ex)
        {
            log.Error($"[BossHandler] Update failed: {ex.Message}");
        }
    }

    private void StopNavDuringBoss(string targetName)
    {
        if (targetName == NeroName || targetName == GaiusName ||
            targetName == PhantomGaiusName || targetName == ColossusName)
        {
            vNavIPC.Stop();
        }
    }

    private void HandleTankMitigation(string targetName)
    {
        if (targetName != GaiusName) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        // Check if tank via ClassJob
        var role = player.ClassJob.Value.Role;
        if (role != 1) return; // Role 1 = Tank

        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target == null) return;

        var hpPct = (float)target.CurrentHp / target.MaxHp * 100f;

        // Rampart at 5-95% HP
        if (hpPct > 5 && hpPct < 95)
        {
            commandManager.ProcessCommand("/ac Rampart");
        }

        // Camouflage at 5-50% HP (GNB specific, but safe to try)
        if (hpPct > 5 && hpPct < 50)
        {
            commandManager.ProcessCommand("/ac Camouflage");
        }
    }

    private void HandleDpsAbilities(string targetName)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var role = player.ClassJob.Value.Role;

        // Only for DPS (role 2 = melee, role 3 = ranged/caster)
        if (role != 2 && role != 3) return;

        // Second Wind when < 50% HP
        var hpPct = (float)player.CurrentHp / player.MaxHp * 100f;
        if (hpPct < 50)
        {
            commandManager.ProcessCommand("/ac \"Second Wind\"");
        }

        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target == null) return;
        var targetHpPct = (float)target.CurrentHp / target.MaxHp * 100f;

        // Limit Break on Phantom Gaius
        if (targetName == PhantomGaiusName)
        {
            commandManager.ProcessCommand("/ac \"Limit Break\"");
        }

        // Limit Break on Ultima Weapon < 30%
        if (targetName == UltimaName && targetHpPct < 30)
        {
            commandManager.ProcessCommand("/ac \"Limit Break\"");
        }
    }

    private void HandlePotions(string targetName)
    {
        if (config.PotionItemId <= 0) return;
        if (!state.PotionsAvailable) return;

        var now = DateTime.UtcNow;
        if ((now - lastPotionUse).TotalSeconds < PotionCooldown) return;

        // Determine which boss to pot on
        var potTarget = config.PotionTarget == 0 ? GaiusName : PhantomGaiusName;

        // Pot on the configured target, Colossus, or Ultima
        if (targetName != potTarget && targetName != ColossusName && targetName != UltimaName) return;

        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target == null) return;
        var hpPct = (float)target.CurrentHp / target.MaxHp * 100f;

        // Don't pot if target is almost dead or full HP
        if (hpPct <= 20 || hpPct >= 100) return;

        // Special case: Ultima Weapon pot at 80-100% only
        if (targetName == UltimaName && (hpPct < 80 || hpPct >= 100)) return;

        try
        {
            log.Information($"[BossHandler] Using potion: {config.PotionItemName} on {targetName}");

            Plugin.CommandManager.ProcessCommand($"/useitem {config.PotionItemId}");
            lastPotionUse = now;
        }
        catch (Exception ex)
        {
            log.Error($"[BossHandler] Potion use failed: {ex.Message}");
        }
    }
}
