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

    internal bool IsGmcLoaded => IsPluginLoaded(GmcInternalName);

    private void ForgetGmcPlan()
    {
        gmcPlan = null;
        gmcPlanChoiceChanged = false;
        gmcGroupSource = gmcGroupRotation = null;
    }

    internal bool BeginGmcAutorotation()
    {
        if (!TryReadOwnedBossModSettings(out var before) || before.AiEnabled is null)
            return false;
        if (before.AiEnabled == true && (!SendCommand("/bmrai off", "GMC autorotation without BossMod AI")
            || !TryReadOwnedBossModSettings(out var after) || after.AiEnabled != false))
            return false;
        return UpdateGmcAutorotation();
    }

    internal bool UpdateGmcAutorotation()
    {
        if (!IsGmcLoaded || ownedIdentity.Provider != "BMR" || !TryReadOwnedBossModSettings(out var before)
            || before.AiEnabled != false || !ownedSettings!.CanChangeRuntime(before))
            return false;
        try
        {
            if (!TryGetLiveBossMod("BMR", out var instance, out _, out _)) return false;
            var manager = LiveMember(instance!, "_rotation");
            var player = LiveMember(manager!, "Player");
            var job = player is null ? null : LiveMember(player, "Class");
            if (manager is null) return false;
            if (job is null || Convert.ToUInt32(job) == 0) return true;
            var preset = SelectPresetForJob(Convert.ToUInt32(job), passive: false);
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
            var requested = new BossModRuntimePresetState(false, usePlan ? Array.Empty<string>() : new[] { preset });
            var ownership = ownedSettings!;
            if (ownership.Original.Runtime is not { } original || original.Names.Any(name => !IsExactRuntimePreset(name))
                || requested.Names.Any(name => !IsExactRuntimePreset(name))) return false;
            var rotation = usePlan ? null : GmcGroupRotation(manager!, preset);
            if (BossModRuntimePresetState.Matches(before.Runtime, requested)
                && ReferenceEquals(LiveMember(manager, "Preset"), rotation)) return true;
            if (usePlan) WriteRuntimePreset("BMR", requested);
            else manager!.GetType().GetProperty("Preset")!.SetValue(manager, rotation);
            if (!TryReadOwnedBossModSettings(out var applied) || !ReferenceEquals(ownedSettings, ownership)
                || !BossModRuntimePresetState.Matches(applied.Runtime, requested)) return false;
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
