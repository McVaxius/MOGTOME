using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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

    internal bool IsGmcLoaded => IsPluginLoaded(GmcInternalName);

    private void ForgetGmcPlan()
    {
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
            || !ownedSettings!.MatchesNativeAuthority(before)) return false;
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
                        && name.StartsWith($"BMRV-{module!.GetType().Name}-", StringComparison.Ordinal)
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
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"GMC autorotation could not be confirmed: {ex.GetBaseException().Message}";
            return false;
        }
    }

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
