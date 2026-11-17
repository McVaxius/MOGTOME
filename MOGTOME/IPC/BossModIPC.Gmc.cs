using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MOGTOME.Models;

namespace MOGTOME.IPC;

public partial class BossModIPC
{
    internal const string GmcInternalName = "BMRVariantLoader";
    // GMC installs plans. Only the native in-memory selection belongs to this run.
    private (object List, object? Original, object? Selected)? gmcPlan;
    private bool gmcPlanChoiceChanged;
    private object? gmcGroupSource, gmcGroupRotation;
    private object? gmcAiManager, gmcOriginalPreset, gmcOwnedBehavior, gmcAiPreset, gmcBehaviorPreset;
    private bool gmcOriginalAiActive;
    private int gmcOriginalSlot, gmcOwnedSlot;
    private ulong gmcOriginalLeader, gmcOwnedLeader;
    private object? gmcOwnedRotation, gmcRotationManager;
    private bool gmcRotationSelected;
    private bool gmcRestoreActiveAi, gmcKeepCleanupRotation;
    private object? gmcCleanupRotation;
    private object? beastmasterPreparation, beastmasterFuturePreset, beastmasterSource;
    private string? beastmasterSavedJson;
    private BossModSettingsOwnership? beastmasterOwnership;
    private WeakReference<object>? beastmasterProvider;
    private Type? beastmasterModuleType;
    private object? beastmasterManager, beastmasterPlayer;

    internal bool IsBeastmasterPreparationPending
        => LiveMember(beastmasterPreparation, "Status")?.ToString() == "Pending";

    internal bool IsOwnedCombatReturnPlayerMissing
        => ownedDeathObserved && ownedSettings is not null && ownedIdentity.Provider == "BMR"
            && !cleanupStarted && !cleanupFailed
            && TryGetLiveBossMod("BMR", out var instance, out _, out _)
            && ownedProvider is not null && ownedProvider.TryGetTarget(out var captured)
            && ReferenceEquals(captured, instance) && LiveMember(instance, "_rotation") is { } manager
            && LiveMember(manager, "Player") is null;

    internal bool CancelBeastmasterPreparation(bool allowUiActions)
    {
        try
        {
            if (allowUiActions && beastmasterPreparation is not null)
                allowUiActions = ownedSettings is not null && ReferenceEquals(beastmasterOwnership, ownedSettings)
                    && ReferenceEquals(beastmasterProvider, ownedProvider)
                    && ownedProvider is not null && ownedProvider.TryGetTarget(out var provider)
                    && ReferenceEquals(LiveMember(provider, "_rotation"), beastmasterManager)
                    && ReferenceEquals(LiveMember(beastmasterManager, "Player"), beastmasterPlayer)
                    && ReferenceEquals(RegisteredBeastmasterType(provider.GetType().Assembly), beastmasterModuleType)
                    && TryReadOwnedBossModSettings(out var current, allowCleanup: true)
                    && ownedSettings.CanChangeForbidActions(current.ForbidActions)
                    && (ownedSettings.CanChangeRuntime(current)
                        || gmcRotationSelected && HasOwnedGmcAi() && MatchesGmcRuntime(current)
                        || deathRecoveryExpected is { } death && BossModRuntimePresetState.Matches(death.Runtime, current.Runtime));
            beastmasterPreparation?.GetType().GetMethod("Dispose", [typeof(bool)])!
                .Invoke(beastmasterPreparation, [allowUiActions]);
            beastmasterPreparation = beastmasterFuturePreset = beastmasterSource = null;
            beastmasterSavedJson = null;
            beastmasterOwnership = null;
            beastmasterProvider = null;
            beastmasterModuleType = null;
            beastmasterManager = beastmasterPlayer = null;
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = "Regular Beastmaster preparation cleanup was incomplete: " + ex.GetBaseException().Message;
            log.Warning($"[MOGTOME][Rotation] {LastStatus}");
            return false;
        }
    }

    internal bool PrepareRegularBeastmaster(bool advance, string? manualPresetName = null)
    {
        try
        {
            if (NativeHost is not ("BossModReborn" or "GodMod") || ownedIdentity.Provider != "BMR")
                return CancelBeastmasterPreparation(false);
            if (ownedSettings is null || !TryGetLiveBossMod("BMR", out var instance, out var assembly, out _))
            {
                CancelBeastmasterPreparation(false);
                return false;
            }
            var manager = LiveMember(instance, "_rotation");
            var player = LiveMember(manager, "Player");
            if (player is null)
            {
                CancelBeastmasterPreparation(false);
                return false;
            }
            if (Convert.ToUInt32(LiveMember(player, "Class")) != 43)
                return CancelBeastmasterPreparation(false);
            var world = LiveMember(manager, "WorldState");
            if (LiveMember(world, "CurrentZone") is { } zone && Convert.ToUInt32(zone) is >= 1339 and <= 1343)
                return CancelBeastmasterPreparation(false);
            if (ownedDeathObserved) RefreshOwnedCombatDeath();
            if (!TryReadOwnedBossModSettings(out var before) || !ownedSettings.MatchesNativeAuthority(before)
                || !ownedSettings.CanChangeForbidActions(before.ForbidActions)
                || !(ownedSettings.CanChangeRuntime(before)
                    || gmcRotationSelected && HasOwnedGmcAi() && MatchesGmcRuntime(before)
                    || deathRecoveryExpected is { } death && BossModRuntimePresetState.Matches(death.Runtime, before.Runtime)))
            {
                CancelBeastmasterPreparation(false);
                LastStatus = "A newer or unreadable native combat selection was retained.";
                return false;
            }
            var registeredType = RegisteredBeastmasterType(assembly!);
            if (registeredType is null)
            {
                CancelBeastmasterPreparation(false);
                LastStatus = MissingGodModCompanionRotations + ": regular Beastmaster preparation is unavailable.";
                return false;
            }
            if (beastmasterPreparation is not null && (!ReferenceEquals(beastmasterOwnership, ownedSettings)
                || !ReferenceEquals(beastmasterProvider, ownedProvider) || !ReferenceEquals(beastmasterModuleType, registeredType)
                || !ReferenceEquals(beastmasterManager, manager) || !ReferenceEquals(beastmasterPlayer, player)))
                if (!CancelBeastmasterPreparation(false)) return false;
            var name = manualPresetName ?? ActiveMeleePreset;
            var presets = LiveMember(LiveMember(manager, "Database"), "Presets");
            var source = ((IEnumerable)LiveMember(presets, "AllPresets")!).Cast<object>()
                .SingleOrDefault(preset => Equals(LiveMember(preset, "Name"), name));
            var saved = pluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get").InvokeFunc(name);
            var future = beastmasterFuturePreset;
            if (future is null || !Equals(LiveMember(future, "Name"), name)
                || !ReferenceEquals(source, beastmasterSource) || saved != beastmasterSavedJson)
            {
                var json = manualPresetName is null
                    ? AdaptDhogBeastmasterPreset(ReadPackagedPresetJson(name)
                        ?? throw new InvalidOperationException("The packaged melee preset is unavailable."), saved)
                    : source is not null && saved is not null ? saved
                        : throw new InvalidOperationException("The selected manual Beastmaster preset is unavailable.");
                var type = assembly!.GetType("BossMod.Autorotation.Preset")
                    ?? throw new InvalidOperationException("The native preset type is unavailable.");
                var options = NativePresetSerializationOptions(assembly);
                future = JsonSerializer.Deserialize(json, type, options)
                    ?? throw new InvalidOperationException("The native Beastmaster preset could not be read.");
                if (!MatchesDhogBeastmasterPreset(json, JsonSerializer.Serialize(future, type, options), type, options))
                    throw new InvalidOperationException("The native host did not retain the saved Beastmaster settings.");
            }
            CopyBeastmasterTransientSettings(source, future);
            var modules = (IList)LiveMember(future, "Modules")!;
            var index = Enumerable.Range(0, modules.Count).Single(i => ReferenceEquals(LiveMember(modules[i], "Type"), registeredType));
            var registry = assembly!.GetType("BossMod.Autorotation.RotationModuleRegistry")!;
            var registrations = (IDictionary)registry.GetField("Modules", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            if (!ReferenceEquals(LiveMember(modules[index], "Definition"), LiveMember(registrations[registeredType], "Definition")))
                throw new InvalidOperationException("The selected Beastmaster definition is not the registered native definition.");
            var strategy = future.GetType().GetMethod("ActiveStrategyOverrides", [typeof(int)])!.Invoke(future, [index])!;
            const string owner = "MOGTOME";
            if (beastmasterPreparation is not null && beastmasterPreparation.GetType().GetMethod("MatchesRequest")!
                    .Invoke(beastmasterPreparation, [owner, manager, player, strategy]) is not true)
                if (!CancelBeastmasterPreparation(true)) return false;
            beastmasterPreparation ??= registeredType.GetMethod("CreatePreparation", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [owner, manager, player, strategy]);
            beastmasterOwnership = ownedSettings;
            beastmasterProvider = ownedProvider;
            beastmasterModuleType = registeredType;
            beastmasterManager = manager;
            beastmasterPlayer = player;
            beastmasterFuturePreset = future;
            beastmasterSource = source;
            beastmasterSavedJson = saved;
            var status = LiveMember(beastmasterPreparation, "Status")?.ToString();
            // Only the framework observer advances pending actions. Ready is
            // checked again without action permission before publication.
            if (advance || status == "Ready")
            {
                var permitted = advance && ownedSettings.CanChangeForbidActions(before.ForbidActions) && Plugin.ClientState.IsLoggedIn
                    && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                    && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
                    && !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.LoggingOut];
                status = beastmasterPreparation!.GetType().GetMethod("Step", [typeof(bool)])!
                    .Invoke(beastmasterPreparation, [permitted])?.ToString();
            }
            LastStatus = LiveMember(beastmasterPreparation, "Reason")?.ToString() ?? "Regular Beastmaster preparation is unavailable.";
            return status == "Ready";
        }
        catch (Exception ex)
        {
            CancelBeastmasterPreparation(false);
            LastStatus = "Regular Beastmaster preparation failed: " + ex.GetBaseException().Message;
            return false;
        }
    }

    internal bool IsGmcLoaded => IsPluginLoaded(GmcInternalName);

    private void ForgetGmcPlan()
    {
        CancelBeastmasterPreparation(false);
        gmcPlan = null;
        gmcPlanChoiceChanged = false;
        gmcGroupSource = gmcGroupRotation = gmcOwnedRotation = null;
        gmcAiManager = gmcOriginalPreset = gmcOwnedBehavior = gmcAiPreset = gmcBehaviorPreset = null;
        gmcRotationManager = null;
        gmcOriginalAiActive = gmcRotationSelected = false;
        gmcOriginalSlot = gmcOwnedSlot = 0;
        gmcOriginalLeader = gmcOwnedLeader = 0;
        gmcRestoreActiveAi = gmcKeepCleanupRotation = false;
        gmcCleanupRotation = null;
    }

    internal bool BeginGmcAutorotation()
    {
        if (!IsGmcLoaded || NativeHost is not ("BossModReborn" or "GodMod")
            || !TryReadOwnedBossModSettings(out var before) || before.AiEnabled is null
            || before.ForbidActions is null || before.ForbidMovement is null
            || !ownedSettings!.MatchesNativeAuthority(before) || !PrepareRegularBeastmaster(advance: false)) return false;
        if (gmcAiManager is null)
        {
            if (!ownedSettings.CanChangeRuntime(before)
                || !IsRestorableAiSelector(before.StoredAiSelector, ReadPresetCatalog("BMR"))
                || !TryGetLiveBossMod("BMR", out var instance, out var assembly, out _)) return false;
            var type = assembly!.GetType("BossMod.AI.AIManager");
            var ai = type?.GetProperty("Instance", RsrStaticFlags)?.GetValue(null)
                ?? type?.GetField("Instance", RsrStaticFlags)?.GetValue(null);
            var behavior = LiveMember(ai, "Beh");
            if (ai is null || LiveMember(ai, "MasterSlot") is not int slot
                || before.AiEnabled != (behavior is not null)) return false;
            var originalPreset = behavior is null ? null : LiveMember(behavior, "AIPreset");
            if (behavior is not null && !Equals(LiveMember(originalPreset, "Name"), before.StoredAiSelector)) return false;
            gmcAiManager = ai;
            gmcOriginalAiActive = behavior is not null;
            gmcOriginalSlot = gmcOwnedSlot = slot;
            gmcOriginalPreset = originalPreset;
            gmcOwnedBehavior = behavior;
            gmcAiPreset = LiveMember(ai, "AiPreset");
            gmcBehaviorPreset = originalPreset;
            gmcOriginalLeader = gmcOwnedLeader = behavior is null ? 0 : ReadGmcLeader(slot);
            gmcRotationManager = LiveMember(instance, "_rotation");
            gmcOwnedRotation = LiveMember(gmcRotationManager, "Preset");
            if (gmcOriginalAiActive && gmcOriginalLeader == 0) return false;
            if (!SetGmcAiPreset(null)) return false;
        }
        if (!HasOwnedGmcAi() || !ApplyOwnedAutomaticTargeting(command => SendCommand(command, "GMC automatic targeting"))
            || !ApplyGmcFlag("ForbidActions", false) || !ApplyGmcFlag("ForbidMovement", false)) return false;
        return UpdateGmcAutorotation() && UpdateGmcCombatState();
    }

    internal bool UpdateGmcAutorotation()
    {
        if (!IsGmcLoaded || NativeHost is not ("BossModReborn" or "GodMod")
            || ownedIdentity.Provider != "BMR" || !TryReadOwnedBossModSettings(out var before)
            || before.AiEnabled != (gmcOwnedBehavior is not null) || !HasOwnedGmcAi() || !ownedSettings!.MatchesNativeAuthority(before)
            || !MatchesGmcRuntime(before))
            return false;
        try
        {
            if (!TryGetLiveBossMod("BMR", out var instance, out _, out _)) return false;
            var manager = LiveMember(instance!, "_rotation");
            var player = LiveMember(manager!, "Player");
            var job = player is null ? null : LiveMember(player, "Class");
            if (manager is null) return false;
            if (job is null || Convert.ToUInt32(job) == 0) return false;
            var row = Convert.ToUInt32(job);
            if (!PrepareGodModCompanionHealers(row, CombatProvider.Bmr, manualPreset: false, warning =>
                {
                    log.Warning($"[MOGTOME][Rotation] {warning}");
                    Plugin.ChatGui?.Print(warning);
                })) return false;
            var preset = SelectPresetForJob(row, passive: false);
            object? module = null, list = null, plan = null;
            if (DutyState.IsMogtomeDutyTerritory(Plugin.ClientState.TerritoryType))
            {
                var bosses = LiveMember(manager, "Bossmods");
                module = LiveMember(bosses, "ActiveModule");
                if (module is null && LiveMember(bosses, "LoadedModules") is IEnumerable loaded)
                {
                    var pending = loaded.Cast<object>().Take(2).ToArray();
                    if (pending.Length == 1) module = pending[0];
                }
                var database = LiveMember(LiveMember(manager!, "Database")!, "Plans");
                if (module is not null && LiveMember(database!, "Plans") is IDictionary encounters
                    && encounters[module.GetType()] is IDictionary jobs)
                    list = jobs[job];
            }
            if (gmcPlan is { } owned)
            {
                var currentPlans = LiveMember(owned.List, "Plans") as IList;
                var currentIndex = LiveMember(owned.List, "SelectedIndex") as int?;
                if (currentPlans is null || currentIndex is null) return false;
                var currentPlan = currentIndex >= 0 && currentIndex < currentPlans.Count ? currentPlans[currentIndex.Value] : null;
                if (currentIndex < -1 || currentIndex >= currentPlans.Count || !ReferenceEquals(currentPlan, owned.Selected))
                {
                    // A later native user choice wins for the remainder of this run.
                    gmcPlanChoiceChanged = true;
                    gmcPlan = null;
                }
                else if (!ReferenceEquals(owned.List, list) && !RestoreGmcPlan()) return false;
            }
            if (list is not null && LiveMember(list, "Plans") is IList plans
                && LiveMember(list, "SelectedIndex") is int selected)
            {
                bool Matches(object? candidate) => candidate is not null
                    && Equals(LiveMember(candidate, "Class"), job)
                    && Equals(LiveMember(candidate, "Encounter"), module!.GetType())
                    && LiveMember(candidate, "Modules") is IList { Count: > 0 };
                var selectedPlan = selected >= 0 && selected < plans.Count && Matches(plans[selected]) ? plans[selected] : null;
                plan = selectedPlan;
                if (!gmcPlanChoiceChanged && LiveMember(player, "InCombat") is false)
                {
                    // BMR checks selected plans when the local player enters combat.
                    // Hold the selection until that native event has passed, preserving
                    // PlannedPullSafety without blocking an automated no-countdown pull.
                    if (selected >= 0 && !SelectGmcPlan(list, plans, -1)) return false;
                    plan = null;
                }
                else if (!gmcPlanChoiceChanged)
                {
                    var candidates = plans.Cast<object>().Where(candidate => Matches(candidate)
                        && LiveMember(candidate, "Name") is string name
                        && name.StartsWith($"BMRV-{GmcPlanBossName(module!.GetType())}-", StringComparison.Ordinal)
                        && name.EndsWith($"-{job}", StringComparison.Ordinal)).ToArray();
                    if (candidates.Length == 1)
                    {
                        plan = candidates[0];
                        if (!ReferenceEquals(plan, selectedPlan))
                        {
                            if (!SelectGmcPlan(list, plans, plans.IndexOf(plan))) return false;
                        }
                    }
                    else if (candidates.Length > 1 && !candidates.Contains(selectedPlan)) plan = null;
                }
            }
            // Keep the group rotation until BMR has bound the exact job/encounter plan.
            var planner = LiveMember(manager!, "Planner");
            var usePlan = plan is not null && ReferenceEquals(LiveMember(planner!, "Plan"), plan)
                && ReferenceEquals(LiveMember(planner!, "Module"), module);
            if (row == 43 && !usePlan)
            {
                var source = ((IEnumerable)LiveMember(LiveMember(LiveMember(manager, "Database"), "Presets"), "AllPresets")!)
                    .Cast<object>().SingleOrDefault(candidate => Equals(LiveMember(candidate, "Name"), preset));
                if ((!gmcRotationSelected || !Equals(lastAliveGmcSelection?.Job, job)
                        || !ReferenceEquals(gmcGroupSource, source))
                    && !PrepareRegularBeastmaster(advance: false)) return false;
            }
            if (usePlan && gmcPlan is null && !gmcPlanChoiceChanged)
                gmcPlan = (list!, plan, plan);
            var requested = new BossModRuntimePresetState(false, usePlan ? Array.Empty<string>() : new[] { preset });
            var ownership = ownedSettings!;
            if (ownership.Original.Runtime is not { } original || original.Names.Any(name => !IsExactRuntimePreset(name))
                || requested.Names.Any(name => !IsExactRuntimePreset(name))) return false;
            var rotation = usePlan ? null : GmcGroupRotation(manager!, preset);
            if (gmcRotationSelected && ReferenceEquals(gmcOwnedRotation, rotation) && ReferenceEquals(gmcAiPreset, rotation)
                && MatchesGmcRuntime(before)) return true;
            if (!SetGmcAiPreset(rotation)) return false;
            if (usePlan) WriteRuntimePreset("BMR", requested);
            else manager!.GetType().GetProperty("Preset")!.SetValue(manager, rotation);
            if (!TryReadOwnedBossModSettings(out var applied) || !ReferenceEquals(ownedSettings, ownership)
                || !BossModRuntimePresetState.Matches(applied.Runtime, requested)
                || !ReferenceEquals(LiveMember(manager, "Preset"), rotation)) return false;
            gmcOwnedRotation = rotation;
            gmcRotationSelected = true;
            ownership.OwnRuntime(requested);
            LastStatus = usePlan ? $"GMC boss plan confirmed for {job}." : $"GMC group rotation '{preset}' confirmed.";
            return CancelBeastmasterPreparation(allowUiActions: true);
        }
        catch (Exception ex)
        {
            LastStatus = $"GMC autorotation could not be confirmed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static string GmcPlanBossName(Type encounter) => encounter.FullName switch
    {
        "BossMod.RealmReborn.Dungeon.D15Praetorium.D151Colossus.D151Colossus" => "D141Colossus",
        "BossMod.RealmReborn.Dungeon.D15Praetorium.D152Nero.D152Nero" => "D142Nero",
        "BossMod.RealmReborn.Dungeon.D15Praetorium.D153Gaius.D153Gaius" => "D143Gaius",
        _ => encounter.Name,
    };

    private ulong ReadGmcLeader(int slot)
    {
        var party = LiveMember(LiveMember(gmcAiManager, "WorldState"), "Party");
        var actor = party?.GetType().GetProperty("Item", new[] { typeof(int) })?.GetValue(party, new object[] { slot });
        return LiveMember(actor, "InstanceID") is ulong id ? id : 0;
    }

    private bool HasOwnedGmcAi()
    {
        if (gmcAiManager is null || !TryGetLiveBossMod("BMR", out var instance, out _, out _)
            || !ReferenceEquals(LiveMember(instance, "_rotation"), gmcRotationManager)
            || !ReferenceEquals(LiveMember(gmcAiManager, "Beh"), gmcOwnedBehavior)
            || !Equals(LiveMember(gmcAiManager, "MasterSlot"), gmcOwnedSlot)
            || !ReferenceEquals(LiveMember(gmcAiManager, "AiPreset"), gmcAiPreset)) return false;
        var type = gmcAiManager.GetType();
        var live = type.GetProperty("Instance", RsrStaticFlags)?.GetValue(null)
            ?? type.GetField("Instance", RsrStaticFlags)?.GetValue(null);
        if (!ReferenceEquals(live, gmcAiManager)) return false;
        var leader = gmcOwnedBehavior is null ? 0 : ReadGmcLeader(gmcOwnedSlot);
        return gmcOwnedBehavior is null || ReferenceEquals(LiveMember(gmcOwnedBehavior, "AIPreset"), gmcBehaviorPreset)
            && (leader == 0 || leader == gmcOwnedLeader);
    }

    private bool MatchesGmcRuntime(BossModSettingsSnapshot current)
    {
        if (!HasOwnedGmcAi()) return false;
        var active = LiveMember(gmcRotationManager, "Preset");
        if (!gmcRotationSelected)
            return ownedSettings!.CanChangeRuntime(current) && ReferenceEquals(active, gmcOwnedRotation);
        if (current.Runtime is not { ForceDisabled: false } runtime) return false;
        // Frozen AI frames publish the captured preset with a target, or null
        // without one. Only our exact captured policy/behavior qualifies that clear.
        return ReferenceEquals(gmcAiPreset, gmcOwnedRotation)
            && (active is null && runtime.Names.Count == 0 && (gmcOwnedRotation is null || gmcOwnedBehavior is not null)
                || gmcOwnedRotation is not null && ReferenceEquals(active, gmcOwnedRotation)
                    && runtime.Names.Count == 1 && Equals(LiveMember(active, "Name"), runtime.Names[0]));
    }

    private bool SetGmcAiPreset(object? preset)
    {
        if (!HasOwnedGmcAi() || !TryReadOwnedBossModSettings(out var before, allowCleanup: cleanupStarted)) return false;
        var ownership = ownedSettings!;
        if (!ownership.CanChangeStoredSelector(before)) return false;
        try { gmcAiManager!.GetType().GetMethod("SetAIPreset")!.Invoke(gmcAiManager, new[] { preset }); }
        catch (Exception ex) { log.Debug($"GMC AI preset: {ex.GetBaseException().Message}"); }
        if (!ReferenceEquals(LiveMember(gmcAiManager, "AiPreset"), preset)
            || gmcOwnedBehavior is not null && !ReferenceEquals(LiveMember(gmcOwnedBehavior, "AIPreset"), preset)) return false;
        gmcAiPreset = gmcBehaviorPreset = preset;
        if (!TryReadOwnedBossModSettings(out var after, allowCleanup: cleanupStarted) || !ReferenceEquals(ownedSettings, ownership)
            || !after.StoredSelectorReadable || !Equals(after.StoredAiSelector, LiveMember(preset, "Name"))) return false;
        ownership.OwnStoredSelector(after.StoredAiSelector);
        return BossModRuntimePresetState.Matches(before.Runtime, after.Runtime);
    }

    private bool ApplyGmcFlag(string name, bool value)
    {
        if (!TryReadOwnedBossModSettings(out var before, allowCleanup: cleanupStarted)) return false;
        var ownership = ownedSettings!;
        var current = name == "ForbidActions" ? before.ForbidActions : before.ForbidMovement;
        if (!(name == "ForbidActions" ? ownership.CanChangeForbidActions(current) : ownership.CanChangeForbidMovement(current))) return false;
        if (current != value)
        {
            try
            {
                pluginInterface.GetIpcSubscriber<System.Collections.Generic.List<string>, bool, System.Collections.Generic.List<string>>("BossMod.Configuration")
                    .InvokeFunc(new() { "AIConfig", name, value.ToString() }, true);
            }
            catch (Exception ex) { log.Debug($"GMC {name}: {ex.GetBaseException().Message}"); }
        }
        if (!TryReadOwnedBossModSettings(out var after, allowCleanup: cleanupStarted) || !ReferenceEquals(ownedSettings, ownership)
            || (name == "ForbidActions" ? after.ForbidActions : after.ForbidMovement) != value) return false;
        if (name == "ForbidActions") ownership.OwnForbidActions(value);
        else ownership.OwnForbidMovement(value);
        return cleanupStarted || MatchesGmcRuntime(after);
    }

    private bool ChangeGmcAi(bool active, int slot)
    {
        if (!HasOwnedGmcAi() || !TryReadOwnedBossModSettings(out var before, allowCleanup: cleanupStarted)) return false;
        if (active && ReadGmcLeader(slot) == 0) return false;
        var policy = gmcAiPreset;
        try
        {
            if (active) gmcAiManager!.GetType().GetMethod("SwitchToFollow")!.Invoke(gmcAiManager, new object[] { slot });
            else gmcAiManager!.GetType().GetMethod("SwitchToIdle")!.Invoke(gmcAiManager, null);
        }
        catch (Exception ex) { log.Debug($"GMC AI transition: {ex.GetBaseException().Message}"); }
        var behavior = LiveMember(gmcAiManager, "Beh");
        if ((behavior is not null) != active || LiveMember(gmcAiManager, "MasterSlot") is not int actualSlot
            || actualSlot != (active ? slot : 0)) return false;
        // Follow resolves the saved name through the native database. Read its
        // result, then synchronize the exact temporary clone (or null plan).
        gmcOwnedBehavior = behavior;
        gmcOwnedSlot = actualSlot;
        gmcOwnedLeader = active ? ReadGmcLeader(actualSlot) : 0;
        gmcAiPreset = LiveMember(gmcAiManager, "AiPreset");
        gmcBehaviorPreset = LiveMember(behavior, "AIPreset");
        if (!TryReadOwnedBossModSettings(out var after, allowCleanup: cleanupStarted) || after.AiEnabled != active
            || after.Runtime is not { ForceDisabled: false, Names.Count: 0 }) return false;
        ownedSettings!.ObserveAiCommand(before, after, active);
        if (cleanupStarted) cleanupRuntimeExpected = after.Runtime;
        if (!SetGmcAiPreset(policy)) return false;
        if (!gmcRotationSelected)
            gmcOwnedRotation = LiveMember(gmcRotationManager, "Preset");
        else if (!cleanupStarted)
        {
            gmcRotationManager!.GetType().GetProperty("Preset")!.SetValue(gmcRotationManager, gmcOwnedRotation);
            var expected = new BossModRuntimePresetState(false,
                gmcOwnedRotation is null ? Array.Empty<string>() : new[] { (string)LiveMember(gmcOwnedRotation, "Name")! });
            if (!TryReadOwnedBossModSettings(out var applied) || !BossModRuntimePresetState.Matches(applied.Runtime, expected)
                || !ReferenceEquals(LiveMember(gmcRotationManager, "Preset"), gmcOwnedRotation)) return false;
            ownedSettings.OwnRuntime(expected);
        }
        return true;
    }

    internal bool UpdateGmcCombatState()
    {
        if (gmcAiManager is null || !HasOwnedGmcAi() || !TryReadOwnedBossModSettings(out var before)) return false;
        var player = LiveMember(gmcRotationManager, "Player");
        // Native dead frames pause the behavior; keep the death receipt intact.
        if (LiveMember(player, "IsDead") is true) return true;
        if (!ownedSettings!.MatchesNativeAuthority(before) || !MatchesGmcRuntime(before)) return false;
        if (LiveMember(player, "InCombat") is not bool fighting) return false;
        if (fighting == (gmcOwnedBehavior is not null)) return true;
        return ChangeGmcAi(fighting, 0);
    }

    private (object Selection, object Job, bool Plan)? ReadOwnedGmcSelection(bool requireBound, object? pendingJob = null)
    {
        if (!gmcRotationSelected || !HasOwnedGmcAi()) return null;
        var job = LiveMember(LiveMember(gmcRotationManager, "Player"), "Class");
        if (job is null || Convert.ToUInt32(job) == 0) job = pendingJob;
        if (job is null) return null;
        if (gmcOwnedRotation is { } rotation)
            return ReferenceEquals(gmcAiPreset, rotation) ? (rotation, job, false) : null;
        if (gmcPlan is not { } owned || owned.Selected is not { } selected
            || LiveMember(owned.List, "Plans") is not IList plans
            || LiveMember(owned.List, "SelectedIndex") is not int index || index < 0 || index >= plans.Count
            || !ReferenceEquals(plans[index], selected) || !Equals(LiveMember(selected, "Class"), job)) return null;
        if (!requireBound) return (selected, job, true);
        var planner = LiveMember(gmcRotationManager, "Planner");
        var module = LiveMember(planner, "Module");
        return module is not null && ReferenceEquals(LiveMember(planner, "Plan"), selected)
            && Equals(LiveMember(selected, "Encounter"), module.GetType()) ? (selected, job, true) : null;
    }

    private bool MatchesGmcSelection((object Selection, object Job, bool Plan) expected, bool allowMissingJob = false)
        => ReadOwnedGmcSelection(requireBound: false, pendingJob: allowMissingJob ? expected.Job : null) is { } current
            && ReferenceEquals(current.Selection, expected.Selection) && Equals(current.Job, expected.Job) && current.Plan == expected.Plan;

    private bool RestoreGmcCombatSettings()
    {
        if (!TryReadOwnedBossModSettings(out var current, allowCleanup: true)) return false;
        var ownership = ownedSettings!;
        foreach (var name in new[] { "ForbidActions", "ForbidMovement" })
        {
            var owned = name == "ForbidActions" ? ownership.OwnedForbidActions : ownership.OwnedForbidMovement;
            var original = name == "ForbidActions" ? ownership.Original.ForbidActions : ownership.Original.ForbidMovement;
            var value = name == "ForbidActions" ? current.ForbidActions : current.ForbidMovement;
            if (owned is not null && value is null) return false;
            if (owned is not null && value == owned && original is { } target && !ApplyGmcFlag(name, target)) return false;
            if (!TryReadOwnedBossModSettings(out current, allowCleanup: true)) return false;
        }
        if (gmcAiManager is null) return true;
        if (!HasOwnedGmcAi()) return false;
        if (gmcRestoreActiveAi)
        {
            if (ReadGmcLeader(gmcOriginalSlot) != gmcOriginalLeader) return false;
            if (gmcOwnedBehavior is null || gmcOwnedSlot != gmcOriginalSlot || gmcOwnedLeader != gmcOriginalLeader)
            {
                if (!ChangeGmcAi(true, gmcOriginalSlot)) return false;
            }
            else if (current.AiEnabled != true) return false;
        }
        else if (gmcOwnedBehavior is not null && !ChangeGmcAi(false, 0)) return false;
        if (gmcKeepCleanupRotation)
        {
            // A later runtime object wins even if it shares the generated name.
            gmcRotationManager!.GetType().GetProperty("Preset")!.SetValue(gmcRotationManager, gmcCleanupRotation);
            if (!TryReadOwnedBossModSettings(out var restored, allowCleanup: true)
                || !ReferenceEquals(LiveMember(gmcRotationManager, "Preset"), gmcCleanupRotation)
                || !BossModRuntimePresetState.Matches(restored.Runtime, cleanupRuntimeTarget)) return false;
            cleanupRuntimeExpected = restored.Runtime;
        }
        return true;
    }

    private static void SetGmcPlanIndex(object list, int index)
        => list.GetType().GetField("SelectedIndex", BindingFlags.Instance | BindingFlags.Public)!.SetValue(list, index);

    private object GmcGroupRotation(object manager, string name)
    {
        var presets = LiveMember(LiveMember(manager, "Database"), "Presets");
        var source = ((IEnumerable)LiveMember(presets, "AllPresets")!).Cast<object>()
            .Single(preset => Equals(LiveMember(preset, "Name"), name));
        if (!ReferenceEquals(source, gmcGroupSource))
        {
            var rotation = source.GetType().GetMethod("MakeClone", new[] { typeof(bool) })!.Invoke(source, new object[] { true })!;
            var modules = (IList)LiveMember(rotation, "Modules")!;
            if (modules.Cast<object>().Any(module => LiveMember(module, "Type") is Type type && type.FullName == DhogBeastmasterModule))
            {
                var options = NativePresetSerializationOptions(source.GetType().Assembly);
                var sourceModule = ((IEnumerable)LiveMember(source, "Modules")!).Cast<object>().Single(module =>
                    LiveMember(module, "Type") is Type type && type.FullName == DhogBeastmasterModule);
                var cloneModule = modules.Cast<object>().Single(module =>
                    LiveMember(module, "Type") is Type type && type.FullName == DhogBeastmasterModule);
                if (!MatchesDhogBeastmasterPreset(JsonSerializer.Serialize(source, source.GetType(), options),
                        JsonSerializer.Serialize(rotation, rotation.GetType(), options), source.GetType(), options)
                    || !((IEnumerable)LiveMember(sourceModule, "TransientSettings")!).Cast<object>()
                        .SequenceEqual(((IEnumerable)LiveMember(cloneModule, "TransientSettings")!).Cast<object>()))
                    throw new InvalidOperationException("The native clone did not retain the saved Beastmaster settings and transient overrides.");
            }
            for (var index = modules.Count - 1; index >= 0; --index)
                if (LiveMember(LiveMember(modules[index], "Definition"), "Order")?.ToString() == "Movement")
                    modules.RemoveAt(index);
            gmcGroupRotation = rotation;
            gmcGroupSource = source;
        }
        return gmcGroupRotation!;
    }

    private bool SelectGmcPlan(object list, IList plans, int index)
    {
        var previous = (int)LiveMember(list, "SelectedIndex")!;
        var original = gmcPlan is { } owned && ReferenceEquals(owned.List, list) ? owned.Original
            : previous >= 0 && previous < plans.Count ? plans[previous] : null;
        gmcPlan = (list, original, index >= 0 ? plans[index] : null);
        SetGmcPlanIndex(list, index);
        return Equals(LiveMember(list, "SelectedIndex"), index);
    }

    private bool RestoreGmcPlan()
    {
        if (gmcPlan is not { } owned) return true;
        if (LiveMember(owned.List, "Plans") is not IList plans || LiveMember(owned.List, "SelectedIndex") is not int index)
            return false;
        var currentPlan = index >= 0 && index < plans.Count ? plans[index] : null;
        if (index >= -1 && index < plans.Count && ReferenceEquals(currentPlan, owned.Selected))
        {
            var originalIndex = owned.Original is null ? -1 : plans.IndexOf(owned.Original);
            SetGmcPlanIndex(owned.List, originalIndex);
            if (!Equals(LiveMember(owned.List, "SelectedIndex"), originalIndex)) return false;
        }
        gmcPlan = null;
        return true;
    }
}
