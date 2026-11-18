using System;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.IPC;

internal enum RsrOperatingMode
{
    Unknown,
    Off,
    Auto,
    TargetOnly,
    Manual,
    AutoDuty,
    Henched,
    PvP,
    Active,
}

public partial class BossModIPC : IDisposable
{
    private enum RsrOtherCommandType : byte { Settings }

    private enum RsrStateCommandType : byte
    {
        Off,
        Auto,
        TargetOnly,
        Manual,
        AutoDuty,
        Henched,
        PvP,
    }

    private const string PassiveTankPreset = "passive - tank";
    private const string PassiveMeleePreset = "passive - melee";
    private const string PassiveRangedPreset = "passive - ranged";

    private const string ActiveTankPreset = "FRENRIDER - TANK";
    private const string ActiveMeleePreset = "FRENRIDER - MELEE";
    private const string ActiveRangedPreset = "FRENRIDER - RANGED";

    private static readonly string[] SuitePresetNames =
        [ActiveTankPreset, ActiveMeleePreset, ActiveRangedPreset, PassiveTankPreset, PassiveMeleePreset, PassiveRangedPreset];

    private static readonly HashSet<uint> TankJobRows = [1, 3, 19, 21, 32, 37];
    private static readonly HashSet<uint> MeleeJobRows = [2, 4, 20, 22, 29, 30, 34, 39, 41, 43];
    private static readonly HashSet<uint> RangedJobRows = [5, 6, 7, 23, 24, 25, 26, 27, 28, 31, 33, 35, 36, 38, 40, 42];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;
    private AdsDutyIpcService? adsDutyIpcService;
    private BossModSettingsOwnership? ownedSettings;
    private WeakReference<object>? ownedProvider;
    private string? selectedNativeHost;
    private WeakReference<object>? selectedProvider;
    private string? ownedNativeHost;
    private BossModSettingsSnapshot? lastAliveOwnedState;
    private BossModSettingsSnapshot? deathRecoveryExpected;
    private BossModRuntimePresetState? deathRecoveryTarget;
    private bool ownedDeathObserved;
    private bool deathNativeObserved, deathNativeClearAllowed;
    private (object Selection, object Job, bool Plan)? lastAliveGmcSelection, deathGmcSelection;
    private (string Account, string Character, Configuration Config, string Provider) ownedIdentity;
    private bool cleanupStarted;
    private bool cleanupFailed;
    private BossModRuntimePresetState? cleanupRuntimeExpected;
    private BossModRuntimePresetState? cleanupRuntimeTarget;
    public string LastSettingsStatus { get; private set; } = string.Empty;
    private string LastStatus { get => LastSettingsStatus; set => LastSettingsStatus = value; }
    private static readonly BindingFlags RsrStaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public BossModIPC(IDalamudPluginInterface pluginInterface, IPluginLog log, ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commandManager = commandManager;
    }

    public bool IsPluginLoaded(string internalName)
        => pluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == internalName);

    internal static bool IsBossModHost(string? name) => name is "GodMod" or "BossModReborn" or "BossMod";

    internal string? GetAutomationHost()
        => SelectAutomationHost(pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded).Select(plugin => plugin.InternalName), IsGmcLoaded);

    internal static string? SelectAutomationHost(IEnumerable<string> hosts, bool gmcLoaded)
    {
        if (hosts.Contains("GodMod")) return "GodMod";
        return gmcLoaded && hosts.Contains("BossModReborn") ? "BossModReborn" : null;
    }

    internal bool HasUsableAutomationHost(bool allowHostCleanup = false)
    {
        try
        {
            var hosts = pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded && IsBossModHost(plugin.InternalName)).ToArray();
            var host = SelectAutomationHost(hosts.Select(plugin => plugin.InternalName), IsGmcLoaded);
            if (host is null) return false;
            if (hosts.Length > 1) return allowHostCleanup;
            if (host == "BossModReborn" && !IsGmcReady) return false;

            var instance = LiveMember(hosts[0], "instance") ?? FindDalamudPluginInstance(host, out _);
            return instance is not null && LiveMember(instance, "_rotationDB") is not null
                && LiveMember(instance, "_rotation") is not null
                && ReadableBaseline("BMR", ReadBossModSettings("BMR", instance.GetType().Assembly))
                && (host != "GodMod" || HasGodModSuitePresets());
        }
        catch
        {
            return false;
        }
    }

    private bool HasGodModSuitePresets()
        => pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.ListAll").InvokeFunc() is { } names
            && SuitePresetNames.All(name => names.Contains(name, StringComparer.Ordinal));

    internal string? NativeHost => ownedNativeHost ?? selectedNativeHost;
    internal bool SupportsBmrObstacleMaps => NativeHost == "BossModReborn";

    internal bool ResolveReadyHost(CombatProvider preferred, out CombatProvider provider, string? expectedHost = null)
    {
        provider = preferred;
        selectedNativeHost = null;
        selectedProvider = null;
        var loaded = pluginInterface.InstalledPlugins.Where(p => p.IsLoaded && IsBossModHost(p.InternalName)).ToArray();
        if (loaded.Length != 1 || expectedHost is not null && loaded[0].InternalName != expectedHost)
        {
            LastStatus = loaded.Length > 1 ? "Multiple BossMod hosts are loaded; their shared IPC is ambiguous."
                : "The selected native BossMod host is not ready.";
            return false;
        }
        var name = loaded[0].InternalName;
        provider = name == "BossMod" ? CombatProvider.Vbm : CombatProvider.Bmr;
        var kind = provider == CombatProvider.Vbm ? "VBM" : "BMR";
        if (!TryGetLiveBossMod(kind, out var instance, out var assembly, out var detail)
            || name != "BossMod" && (LiveMember(instance, "_rotationDB") is null || LiveMember(instance, "_rotation") is null)
            || !ReadableBaseline(kind, ReadBossModSettings(kind, assembly!)))
        {
            LastStatus = string.IsNullOrEmpty(detail) ? $"{name} is still initializing." : detail;
            return false;
        }
        selectedNativeHost = name;
        selectedProvider = new WeakReference<object>(instance!);
        return true;
    }

    private static bool ReadableBaseline(string provider, BossModSettingsSnapshot baseline)
        => baseline.Runtime is not null && baseline.AiEnabled is not null
            && (provider != "BMR" || baseline.StoredSelectorReadable && baseline.ManualTarget is not null
                && baseline.PreferredDistance is { } distance && double.IsFinite(distance));

    private string ResolveProvider(string requested)
    {
        if (ownedSettings is not null) return ownedIdentity.Provider;
        var loaded = pluginInterface.InstalledPlugins.Where(p => p.IsLoaded && IsBossModHost(p.InternalName)).ToArray();
        return loaded.Length == 1 ? loaded[0].InternalName == "BossMod" ? "VBM" : "BMR" : GetBossModProvider(requested);
    }

    private string RouteNativeCommand(string command)
        => command.StartsWith("/bmrai ", StringComparison.Ordinal) && NativeHost == "GodMod"
            ? "/gmai " + command[7..] : command;

    public BossModPresetCatalog ReadPresetCatalog(CombatProvider provider)
        => ReadPresetCatalog(provider == CombatProvider.Vbm ? "VBM" : "BMR");

    internal bool BeginOwnedBossModSettings(CombatProvider provider, string account, string character, Configuration config)
        => PrepareOwnedBossModSettings(provider == CombatProvider.Vbm ? "VBM" : "BMR", account, character, config);

    internal bool RetireDepartedOwnedBossModSettings()
    {
        if (ownedSettings is null || !TryGetLiveBossMod(ownedIdentity.Provider, out var live, out _, out _)
            || ownedProvider!.TryGetTarget(out var captured) && ReferenceEquals(captured, live))
            return false;
        LastStatus = "BossMod provider reloaded; the departed session cannot be restored into its replacement.";
        log.Warning($"[MOGTOME] Owned BossMod settings cleanup was incomplete: {LastStatus}");
        ownedSettings = null;
        ownedProvider = null;
        ownedNativeHost = null;
        selectedNativeHost = null;
        selectedProvider = null;
        lastAliveOwnedState = deathRecoveryExpected = null;
        deathRecoveryTarget = null;
        ownedDeathObserved = false;
        deathNativeObserved = deathNativeClearAllowed = false;
        lastAliveGmcSelection = deathGmcSelection = null;
        ForgetGmcPlan();
        cleanupStarted = cleanupFailed = false;
        cleanupRuntimeExpected = cleanupRuntimeTarget = null;
        return true;
    }

    internal bool IsOwnedProviderCurrent => ownedSettings is null || TryReadOwnedBossModSettings(out _, allowCleanup: true);
    internal bool BeginOwnedBossModCleanup() => BeginOwnedBossModCleanup(ownedIdentity.Account, ownedIdentity.Character);
    internal void SetAdsDutyIpcService(AdsDutyIpcService service) => adsDutyIpcService = service;
    internal bool IsVbmInteractionPauseActive => adsDutyIpcService?.RefreshInteractionVbmPause() == true;

    private static object? GetInstanceMember(object root, string name)
        => TryGetInstanceMember(root, name, out var value) ? value : null;

    private static bool TryGetInstanceMember(object root, string name, out object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = root.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetProperty(name, flags) is { } property) { value = property.GetValue(root); return true; }
            if (type.GetField(name, flags) is { } field) { value = field.GetValue(root); return true; }
        }
        value = null;
        return false;
    }

    private static bool? ReadBmrAiActive(Assembly assembly)
    {
        var managerType = assembly.GetType("BossMod.AI.AIManager");
        var manager = managerType?.GetProperty("Instance", RsrStaticFlags)?.GetValue(null)
            ?? managerType?.GetField("Instance", RsrStaticFlags)?.GetValue(null);
        return manager != null && TryGetInstanceMember(manager, "Beh", out var behavior) ? behavior != null : null;
    }

    private BossModPresetCatalog ReadPresetCatalog(string rotationProvider)
    {
        var provider = ResolveProvider(rotationProvider);
        if (!TryGetLiveBossMod(provider, out var instance, out var assembly, out var detail))
            return BossModPresetCatalog.Unavailable(provider, detail);
        try
        {
            var rotationDatabase = ReadRotationDatabase(provider, instance!, assembly!);
            var presets = rotationDatabase is null ? null : LiveMember(rotationDatabase, "Presets");
            if (presets is null || LiveMember(presets, "AllPresets") is not IEnumerable visible
                || LiveMember(presets, "DefaultPresets") is not IEnumerable defaults
                || LiveMember(presets, "UserPresets") is not IEnumerable users)
                return BossModPresetCatalog.Unavailable(provider, "The running provider's complete preset database is unavailable.");
            return ReadPresetCatalog(provider, visible, defaults, users, exactGodModSuiteNames: IsPluginLoaded("GodMod"));
        }
        catch (Exception ex)
        {
            return BossModPresetCatalog.Unavailable(provider, ex.GetBaseException().Message);
        }
    }

    internal static BossModPresetCatalog ReadPresetCatalog(string provider, IEnumerable visible,
        IEnumerable defaults, IEnumerable users, bool exactGodModSuiteNames = false)
    {
        var displayed = new List<string>();
        var definitions = new List<string>();
        foreach (var collection in new[] { defaults, users })
        foreach (var preset in collection)
        {
            if (preset is null || LiveMember(preset, "Name") is not string name)
                return BossModPresetCatalog.Unavailable(provider, "A preset definition is unreadable.");
            definitions.Add(name);
        }
        var nativeNames = new List<string>();
        foreach (var preset in visible)
        {
            if (preset is null || LiveMember(preset, "Name") is not string name
                || LiveMember(preset, "HiddenByDefault") is not bool hidden)
                return BossModPresetCatalog.Unavailable(provider, "A displayed preset is unreadable.");
            nativeNames.Add(name);
            if (string.IsNullOrWhiteSpace(name) || provider == "VBM" && (hidden || name == "VBM Multibox"))
                continue;
            displayed.Add(name);
        }
        displayed.RemoveAll(name => !(exactGodModSuiteNames && IsSuitePresetName(name))
            && (!string.Equals(nativeNames.FirstOrDefault(candidate =>
                string.Equals(candidate, name, StringComparison.CurrentCultureIgnoreCase)), name, StringComparison.Ordinal)
                || provider == "BMR" && !string.Equals(nativeNames.FirstOrDefault(candidate =>
                    string.Equals(candidate.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)), name, StringComparison.Ordinal)));
        return new BossModPresetCatalog(provider, true, displayed.Distinct(StringComparer.Ordinal).ToArray(),
            definitions, nativeNames, string.Empty);
    }

    internal static string ResolvePresetSelection(string saved, BossModPresetCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(saved) || !catalog.Readable || catalog.DisplayedNames.Count == 0
            || catalog.DefinitionNames.Any(name => string.Equals(name, saved, StringComparison.CurrentCultureIgnoreCase)))
            return saved;
        return catalog.DisplayedNames[0];
    }

    internal static string GetBossModProvider(string rotationProvider)
        => string.Equals(rotationProvider, "VBM", StringComparison.OrdinalIgnoreCase) ? "VBM" : "BMR";

    private bool TryGetLiveBossMod(string provider, out object? instance, out Assembly? assembly, out string detail)
    {
        instance = null;
        assembly = null;
        try
        {
            var loaded = pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded
                && IsBossModHost(plugin.InternalName)).ToArray();
            var expectedName = (ownedSettings is not null ? ownedNativeHost : selectedNativeHost)
                ?? (loaded.Length == 1 ? loaded[0].InternalName : provider == "VBM" ? "BossMod" : "BossModReborn");
            if (loaded.Length != 1 || loaded[0].InternalName != expectedName)
            {
                detail = loaded.Length > 1 ? "Multiple BossMod hosts are loaded; their shared IPC is ambiguous."
                    : "The selected BossMod provider is not loaded.";
                return false;
            }
            instance = LiveMember(loaded[0], "instance");
            detail = string.Empty;
            if (instance is null) instance = FindDalamudPluginInstance(expectedName, out detail);
            assembly = instance?.GetType().Assembly;
            if (ownedSettings is null && selectedProvider is not null
                && (!selectedProvider.TryGetTarget(out var selected) || !ReferenceEquals(selected, instance)))
            {
                detail = "The ready native BossMod instance changed before settings acquisition.";
                return false;
            }
            return instance is not null && assembly is not null;
        }
        catch (Exception ex)
        {
            detail = ex.GetBaseException().Message;
            return false;
        }
    }

    internal static object? ReadRotationDatabase(string provider, object instance, Assembly assembly)
    {
        if (provider == "BMR")
            return LiveMember(instance, "_rotationDB");
        var tickType = assembly.GetType("BossMod.Services.TickService");
        var host = LiveMember(instance, "Host");
        if (tickType is null || host is null || LiveMember(host, "Services") is not IServiceProvider services)
            return null;
        // The released provider owns this field; the newer provider registers
        // RotationDatabase directly. Do not resolve a generated constructor field.
        if (tickType.GetField("_rotationDB", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null)
        {
            var tick = services.GetService(tickType);
            return tick?.GetType() == tickType ? LiveMember(tick, "_rotationDB") : null;
        }
        var databaseType = assembly.GetType("BossMod.Autorotation.RotationDatabase");
        var database = databaseType is null ? null : services.GetService(databaseType);
        return database?.GetType() == databaseType ? database : null;
    }

    private static object? LiveMember(object? root, string name)
        => root is null ? null : GetInstanceMember(root, name);

    internal bool PrepareOwnedBossModSettings(string rotationProvider, string account, string character,
        Configuration config)
    {
        var provider = ResolveProvider(rotationProvider);
        var identity = (Account: account, Character: character, Config: config, Provider: provider);
        if (!TryGetLiveBossMod(provider, out var live, out var assembly, out var detail))
        {
            LastStatus = detail;
            return false;
        }
        if (ownedSettings is not null && ownedIdentity.Provider == provider
            && (!ownedProvider!.TryGetTarget(out var captured) || !ReferenceEquals(captured, live)))
        {
            LastStatus = "BossMod provider reloaded; the departed session cannot be restored into its replacement.";
            log.Warning($"[MOGTOME] Owned BossMod settings cleanup was incomplete: {LastStatus}");
            ownedSettings = null;
            ownedProvider = null;
            cleanupStarted = cleanupFailed = false;
            cleanupRuntimeExpected = cleanupRuntimeTarget = null;
        }
        if (ownedSettings is not null && (cleanupStarted || cleanupFailed))
            return false;
        if (ownedSettings is not null && ownedIdentity != identity)
        {
            ReleaseOwnedBossModSettings(false);
            if (ownedSettings is not null)
                return false;
        }
        if (ownedSettings is null)
        {
            if (provider == "BMR" && (LiveMember(live, "_rotationDB") is null || LiveMember(live, "_rotation") is null))
            {
                LastStatus = "The native BossMod host is still initializing.";
                return false;
            }
            var baseline = ReadBossModSettings(provider, assembly!);
            if (!ReadableBaseline(provider, baseline))
            {
                LastStatus = "The native BossMod host has no complete readable settings baseline yet.";
                return false;
            }
            ownedIdentity = identity;
            ownedNativeHost = pluginInterface.InstalledPlugins.Single(p => p.IsLoaded && IsBossModHost(p.InternalName)).InternalName;
            ownedProvider = new WeakReference<object>(live!);
            ownedSettings = new BossModSettingsOwnership(provider, baseline);
            lastAliveOwnedState = deathRecoveryExpected = null;
            deathRecoveryTarget = null;
            ownedDeathObserved = false;
            deathNativeObserved = deathNativeClearAllowed = false;
            lastAliveGmcSelection = deathGmcSelection = null;
            ForgetGmcPlan();
        }
        return true;
    }

    private bool TryReadOwnedBossModSettings(out BossModSettingsSnapshot snapshot, bool allowCleanup = false)
    {
        snapshot = BossModSettingsSnapshot.Unavailable;
        if (!allowCleanup && (cleanupStarted || cleanupFailed))
            return false;
        if (ownedSettings is null)
        {
            LastStatus = "No owned BossMod settings session.";
            return false;
        }
        if (!TryGetLiveBossMod(ownedIdentity.Provider, out var live, out var assembly, out var detail))
        {
            LastStatus = detail;
            return false;
        }
        if (!ownedProvider!.TryGetTarget(out var captured) || !ReferenceEquals(captured, live))
        {
            LastStatus = "BossMod provider reloaded; the departed session cannot be restored into its replacement.";
            return false;
        }
        snapshot = ReadBossModSettings(ownedIdentity.Provider, assembly!);
        return true;
    }

    private BossModSettingsSnapshot ReadBossModSettings(string provider, Assembly assembly)
    {
        BossModRuntimePresetState? runtime = null;
        try
        {
            var forceDisabled = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc();
            var names = provider == "VBM"
                ? pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList").InvokeFunc()?.ToArray()
                : pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive").InvokeFunc() is { } active
                    ? new[] { active } : Array.Empty<string>();
            if (names is not null && names.All(name => name is not null))
                runtime = new BossModRuntimePresetState(forceDisabled, forceDisabled ? Array.Empty<string>() : names);
        }
        catch (Exception ex) { log.Debug($"BossMod runtime preset read unavailable: {ex.GetBaseException().Message}"); }
        var node = ReadAiConfig(assembly);
        var storedReadable = provider == "BMR" && node is not null
            && TryGetInstanceMember(node, "AIAutorotPresetName", out var value)
            && value is null or string;
        var stored = storedReadable ? LiveMember(node!, "AIAutorotPresetName") as string : null;
        var aiEnabled = provider == "BMR" ? ReadBmrAiActive(assembly)
            : node is null ? null : LiveMember(node, "Enabled") as bool?;
        var manualTarget = provider == "BMR" && node is not null
            ? LiveMember(node, "ManualTarget") as bool? : null;
        double? distance = null;
        if (provider == "BMR")
        {
            try
            {
                var values = pluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
                    .InvokeFunc(new List<string> { "AIConfig", "PreferredDistance" }, false);
                if (TryParsePreferredDistance(values, CultureInfo.CurrentCulture, out var parsed))
                    distance = parsed;
            }
            catch (Exception ex) { log.Debug($"BossMod preferred-distance read unavailable: {ex.GetBaseException().Message}"); }
        }
        var clearOnDeath = provider == "BMR"
            ? LiveMember(ReadConfigNode(assembly, "BossMod.Autorotation.AutorotationConfig"), "ClearPresetOnDeath") as bool? : null;
        return new BossModSettingsSnapshot(runtime, storedReadable, stored, distance, aiEnabled, manualTarget,
            clearOnDeath, provider == "BMR" ? LiveMember(node, "ForbidActions") as bool? : null,
            provider == "BMR" ? LiveMember(node, "ForbidMovement") as bool? : null);
    }

    private static object? ReadAiConfig(Assembly assembly)
        => ReadConfigNode(assembly, "BossMod.AI.AIConfig");

    private static object? ReadConfigNode(Assembly assembly, string nodeName)
    {
        try
        {
            var service = assembly.GetType("BossMod.Service");
            var rootType = assembly.GetType("BossMod.ConfigRoot");
            var aiConfigType = assembly.GetType(nodeName);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var root = service?.GetProperty("Config", flags)?.GetValue(null) ?? service?.GetField("Config", flags)?.GetValue(null);
            if (rootType is null || aiConfigType is null || rootType.Assembly != assembly
                || aiConfigType.Assembly != assembly || root?.GetType() != rootType)
                return null;
            var accessor = rootType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .SingleOrDefault(method => method.Name == "Get" && method.IsGenericMethodDefinition
                    && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
            var node = accessor?.MakeGenericMethod(aiConfigType).Invoke(root, null);
            return node?.GetType() == aiConfigType ? node : null;
        }
        catch { }
        return null;
    }

    private bool ReadNativeDeathState(bool? dead, bool requireCombat = false)
    {
        if (ownedIdentity.Provider != "BMR" || !TryGetLiveBossMod("BMR", out var instance, out var assembly, out _))
            return false;
        var player = LiveMember(LiveMember(instance, "_rotation"), "Player");
        return LiveMember(player, "IsDead") is bool isDead && (dead is null || isDead == dead)
            && (dead != false || LiveMember(player, "Class") is { } nativeJob && Convert.ToUInt32(nativeJob) != 0)
            && (!requireCombat || LiveMember(player, "InCombat") is true
                && LiveMember(ReadConfigNode(assembly!, "BossMod.Autorotation.AutorotationConfig"), "ClearPresetOnDeath") is true
                && (NativeHost != "GodMod" || LiveMember(LiveMember(LiveMember(instance, "_rotation"), "Hints"), "ScriptedDeath") is false))
            && LiveMember(ReadAiConfig(assembly!), "ForbidActions") is false;
    }

    private static bool SameNonRuntimeSettings(BossModSettingsSnapshot before, BossModSettingsSnapshot after)
        => before.StoredSelectorReadable == after.StoredSelectorReadable && before.StoredAiSelector == after.StoredAiSelector
            && before.PreferredDistance == after.PreferredDistance && before.AiEnabled == after.AiEnabled
            && before.ManualTarget == after.ManualTarget && before.ClearPresetOnDeath == after.ClearPresetOnDeath
            && before.ForbidActions == after.ForbidActions && before.ForbidMovement == after.ForbidMovement;

    internal void ObserveOwnedCombatState()
    {
        if (ownedDeathObserved)
        {
            RefreshOwnedCombatDeath();
            return;
        }
        if (ownedSettings is null || !ReadNativeDeathState(dead: false)) return;
        var selection = ReadOwnedGmcSelection(requireBound: true);
        lastAliveOwnedState = TryReadOwnedBossModSettings(out var current) && current.ClearPresetOnDeath is not null
            && current.Runtime is { ForceDisabled: false } runtime
            && (selection is not null ? MatchesGmcRuntime(current) : runtime.Names.Count > 0 && ownedSettings.MatchesOwnedRuntime(current))
            && ownedSettings.MatchesNativeAuthority(current) ? current : null;
        lastAliveGmcSelection = lastAliveOwnedState is not null ? selection : null;
    }

    internal void ObserveOwnedCombatDeath()
    {
        var previous = lastAliveOwnedState;
        var selection = lastAliveGmcSelection;
        lastAliveOwnedState = null;
        lastAliveGmcSelection = null;
        deathRecoveryExpected = null;
        deathRecoveryTarget = null;
        deathGmcSelection = null;
        deathNativeObserved = ReadNativeDeathState(dead: true);
        deathNativeClearAllowed = deathNativeObserved && ReadNativeDeathState(dead: true, requireCombat: true);
        ownedDeathObserved = ownedSettings is not null && ownedIdentity.Provider == "BMR";
        if (previous is null || previous.Runtime is not { ForceDisabled: false } target || !ownedDeathObserved
            || !(selection is { } selected ? MatchesGmcSelection(selected) : target.Names.Count > 0 && ownedSettings!.MatchesOwnedRuntime(previous))
            || !TryReadOwnedBossModSettings(out var current) || !SameNonRuntimeSettings(previous, current)
            || !ownedSettings!.MatchesNativeAuthority(current)
            || !(BossModRuntimePresetState.Matches(target, current.Runtime)
                || deathNativeClearAllowed && current.Runtime is { ForceDisabled: true, Names.Count: 0 }
                || selection is not null && MatchesGmcRuntime(current))) return;
        // HP0 can precede native death processing. Preserve this qualified receipt
        // through readiness/loading; a clear is not required for every native death.
        deathRecoveryExpected = current;
        deathRecoveryTarget = target;
        deathGmcSelection = selection;
    }

    private void RefreshOwnedCombatDeath()
    {
        if (deathRecoveryExpected is not { } expected || deathRecoveryTarget is not { } target) return;
        if (!deathNativeObserved && ReadNativeDeathState(dead: true))
        {
            deathNativeObserved = true;
            deathNativeClearAllowed = ReadNativeDeathState(dead: true, requireCombat: true);
        }
        if (ownedSettings is not { } ownership || !TryReadOwnedBossModSettings(out var current)
            || !SameNonRuntimeSettings(expected, current) || !ownership.MatchesNativeAuthority(current)
            || !(BossModRuntimePresetState.Matches(expected.Runtime, current.Runtime)
                || deathNativeClearAllowed && current.Runtime is { ForceDisabled: true, Names.Count: 0 }
                || deathGmcSelection is not null && MatchesGmcRuntime(current)))
        {
            deathRecoveryExpected = null;
            deathRecoveryTarget = null;
            deathGmcSelection = null;
            return;
        }
        // A temporarily missing native player or job during Return is readiness,
        // not permission to reclaim a different native selection.
        if (!TryGetLiveBossMod("BMR", out var instance, out _, out _)
            || LiveMember(LiveMember(instance, "_rotation"), "Player") is not { } player
            || LiveMember(player, "Class") is not { } job || Convert.ToUInt32(job) == 0) return;
        if (!(deathGmcSelection is { } selected ? MatchesGmcSelection(selected)
            : ownership.MatchesOwnedRuntime(expected with { Runtime = target })))
        {
            deathRecoveryExpected = null;
            deathRecoveryTarget = null;
            deathGmcSelection = null;
            return;
        }
        deathRecoveryExpected = current;
    }

    internal bool? RecoverOwnedCombatDeath(bool settingsUnchanged)
    {
        if (!ownedDeathObserved) return true;
        RefreshOwnedCombatDeath();
        if (settingsUnchanged && deathRecoveryExpected is { } pending && deathRecoveryTarget is not null)
        {
            if (!ReadNativeDeathState(dead: false)
                || pending.Runtime is { ForceDisabled: false } && !deathNativeObserved) return null;
        }
        ownedDeathObserved = false;
        var expected = deathRecoveryExpected;
        var target = deathRecoveryTarget;
        var selection = deathGmcSelection;
        deathRecoveryExpected = null;
        deathRecoveryTarget = null;
        deathGmcSelection = null;
        deathNativeObserved = deathNativeClearAllowed = false;
        if (!settingsUnchanged || expected is null || target is null || ownedSettings is not { } ownership
            || !ReadNativeDeathState(dead: false) || !TryReadOwnedBossModSettings(out var current)
            || !SameNonRuntimeSettings(expected, current) || !ownership.MatchesNativeAuthority(current)
            || !BossModRuntimePresetState.Matches(expected.Runtime, current.Runtime)
            || !(selection is { } selected ? MatchesGmcSelection(selected) : ownership.MatchesOwnedRuntime(expected with { Runtime = target }))
            || target.Names.Any(name => !IsExactRuntimePreset(name))) return false;
        // Admit only the verified native clear or unchanged owned selection.
        // Existing activation/readback owns the subsequent write, if one is needed.
        ownership.OwnRuntime(current.Runtime!);
        if (selection is not null)
        {
            // Re-enter activation from this verified native result, including
            // ForceDisable. It is not an unexplained later user selection.
            gmcOwnedRotation = LiveMember(gmcRotationManager, "Preset");
            gmcRotationSelected = false;
        }
        return true;
    }

    internal static bool TryParsePreferredDistance(IReadOnlyList<string>? values, CultureInfo culture, out double distance)
    {
        distance = 0;
        return values?.Count == 1 && double.TryParse(values[0], NumberStyles.Float, culture, out distance)
            && double.IsFinite(distance);
    }

    private bool IsExactRuntimePreset(string name)
        => string.Equals(ReadRuntimePresetName(name), name, StringComparison.Ordinal);

    private string? ReadRuntimePresetName(string name)
    {
        try
        {
            var json = pluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get").InvokeFunc(name);
            if (json is null)
                return null;
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("Name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch { return null; }
    }

    private static bool IsSuitePresetName(string name)
        => name.Equals(ActiveTankPreset, StringComparison.OrdinalIgnoreCase)
            || name.Equals(ActiveMeleePreset, StringComparison.OrdinalIgnoreCase)
            || name.Equals(ActiveRangedPreset, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PassiveTankPreset, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PassiveMeleePreset, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PassiveRangedPreset, StringComparison.OrdinalIgnoreCase);

    private bool IsExactAiSelector(string name, BossModPresetCatalog catalog)
        => !string.IsNullOrWhiteSpace(name) && catalog.Readable
            && (NativeHost == "GodMod" && IsSuitePresetName(name)
                ? catalog.NativeNames.Contains(name, StringComparer.Ordinal)
                : string.Equals(catalog.NativeNames.FirstOrDefault(candidate =>
                    string.Equals(candidate.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)), name, StringComparison.Ordinal));

    private bool IsRestorableAiSelector(string? name, BossModPresetCatalog catalog)
        => name is not null ? IsExactAiSelector(name, catalog)
            : catalog.Readable && !catalog.NativeNames.Any(string.IsNullOrWhiteSpace);

    internal bool ApplyOwnedBossModPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return true;
        if (!TryReadOwnedBossModSettings(out var before))
            return false;
        var ownership = ownedSettings!;
        var catalog = ReadPresetCatalog(ownedIdentity.Provider);
        if (!catalog.Readable || !IsExactRuntimePreset(presetName)
            || ownedSettings!.Original.Runtime is not { } original
            || original.Names.Any(name => !IsExactRuntimePreset(name))
            || !ownedSettings.CanChangeRuntime(before))
        {
            LastStatus = "Preset retained: its exact name, original runtime state or current ownership is unavailable.";
            return false;
        }
        if (ownedIdentity.Provider == "BMR")
        {
            var selector = ownedSettings.Original.StoredAiSelector;
            if (!ownedSettings.Original.StoredSelectorReadable
                || !IsRestorableAiSelector(selector, catalog) || !IsExactAiSelector(presetName, catalog)
                || !ownedSettings.CanChangeStoredSelector(before))
            {
                LastStatus = "BMR preset retained: the original saved AI selector has no verified exact restoration path.";
                return false;
            }
            WriteAiSelector(presetName);
            if (!TryReadOwnedBossModSettings(out var selectorApplied)
                || !ReferenceEquals(ownedSettings, ownership)
                || !selectorApplied.StoredSelectorReadable || selectorApplied.StoredAiSelector != presetName)
            {
                LastStatus = "BMR saved AI preset write could not be confirmed.";
                return false;
            }
            ownership.OwnStoredSelector(presetName);
            if (!ownership.CanChangeRuntime(selectorApplied))
            {
                LastStatus = "BossMod runtime changed during the selector callback; that external selection was retained.";
                return false;
            }
        }
        if (ownedIdentity.Provider == "VBM" && IsVbmInteractionPauseActive)
        {
            LastStatus = "VBM preset activation deferred while ADS holds an interaction.";
            return false;
        }
        var requested = new BossModRuntimePresetState(false, new[] { presetName });
        var submitted = WriteRuntimePreset(ownedIdentity.Provider, requested);
        if (TryReadOwnedBossModSettings(out var applied) && ReferenceEquals(ownedSettings, ownership)
            && BossModRuntimePresetState.Matches(applied.Runtime, requested))
        {
            ownership.OwnRuntime(requested);
            LastStatus = $"{ownedIdentity.Provider} preset '{presetName}' confirmed.";
            return true;
        }
        LastStatus = submitted ? "BossMod preset write was submitted but its exact readback was not confirmed."
            : "BossMod preset setter is unavailable or rejected the selected name.";
        return false;
    }

    private void WriteAiSelector(string name)
    {
        try
        {
            if (gmcAiManager is not null)
            {
                object? preset = null;
                if (!string.IsNullOrEmpty(name))
                {
                    preset = gmcOriginalAiActive && Equals(LiveMember(gmcOriginalPreset, "Name"), name)
                        ? gmcOriginalPreset
                        : ((IEnumerable)LiveMember(LiveMember(LiveMember(gmcRotationManager, "Database"), "Presets"), "AllPresets")!)
                            .Cast<object>().Single(candidate => Equals(LiveMember(candidate, "Name"), name));
                }
                SetGmcAiPreset(preset);
            }
            else
                pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction(name);
        }
        catch (Exception ex) { log.Debug($"BossMod AI preset setter unavailable: {ex.GetBaseException().Message}"); }
    }

    private bool WriteRuntimePreset(string provider, BossModRuntimePresetState state)
    {
        if (state.ForceDisabled)
            return TryBoolIpc("BossMod.Presets.SetForceDisabled") == true;
        if (provider == "VBM")
            return TryBoolIpc("BossMod.Presets.SetActiveList", state.Names.ToList()) == true;
        return state.Names.Count == 0 ? TryBoolIpc("BossMod.Presets.ClearActive") == true
            : state.Names.Count == 1 && TryBoolIpc("BossMod.Presets.SetActive", state.Names[0]) == true;
    }

    internal bool ApplyOwnedPreferredDistance(Func<string, bool> sendCommand, double requested)
    {
        if (ownedIdentity.Provider != "BMR" || !double.IsFinite(requested)
            || !TryReadOwnedBossModSettings(out var before)
            || !ownedSettings!.CanChangeDistance(before.PreferredDistance))
        {
            LastStatus = "Preferred distance retained: its original or current value is unavailable or owned elsewhere.";
            return false;
        }
        var submitted = sendCommand($"/bmrai prefdistance {requested.ToString("R", CultureInfo.InvariantCulture)}");
        if (TryReadOwnedBossModSettings(out var after) && after.PreferredDistance == requested)
        {
            ownedSettings!.OwnDistance(requested);
            return true;
        }
        LastStatus = submitted ? "Preferred-distance command was submitted but its readback was not confirmed."
            : "Preferred-distance command dispatch failed.";
        return false;
    }

    internal bool ApplyOwnedAutomaticTargeting(Func<string, bool> sendCommand)
    {
        if (ownedIdentity.Provider != "BMR" || !TryReadOwnedBossModSettings(out var before))
            return false;
        if (before.ManualTarget == false)
            return true;
        if (!ownedSettings!.CanChangeManualTarget(before.ManualTarget))
        {
            LastStatus = "BMR targeting retained: its original or current value is unavailable or owned elsewhere.";
            return false;
        }
        var ownership = ownedSettings!;
        var submitted = sendCommand("/bmrai manualtarget off");
        if (TryReadOwnedBossModSettings(out var after) && ReferenceEquals(ownedSettings, ownership)
            && after.ManualTarget == false)
        {
            ownership.OwnManualTarget(false);
            return true;
        }
        LastStatus = submitted ? "BMR automatic-targeting command was submitted but its readback was not confirmed."
            : "BMR automatic-targeting command dispatch failed.";
        return false;
    }

    internal bool RestoreOwnedAutomaticTargeting()
    {
        if (ownedSettings?.OwnedManualTarget is null)
            return true;
        if (!TryReadOwnedBossModSettings(out var current, allowCleanup: cleanupStarted))
            return false;
        if (current.ManualTarget is null)
        {
            LastStatus = "BMR targeting restoration retained: current state is unreadable.";
            return false;
        }
        var ownership = ownedSettings!;
        if (ownership.GetManualTargetCleanupTarget(current.ManualTarget) is { } originalManualTarget
            && current.ManualTarget != originalManualTarget)
        {
            try
            {
                pluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
                    .InvokeFunc(new List<string> { "AIConfig", "ManualTarget", originalManualTarget.ToString() }, true);
                if (!TryReadOwnedBossModSettings(out var after, allowCleanup: cleanupStarted)
                    || !ReferenceEquals(ownedSettings, ownership) || after.ManualTarget != originalManualTarget)
                {
                    LastStatus = "BMR targeting restoration could not be confirmed.";
                    return false;
                }
            }
            catch { LastStatus = "BMR targeting restoration endpoint is unavailable."; return false; }
        }
        ownership.ReleaseManualTarget();
        return true;
    }

    internal bool SendBossModAiCommand(string command, Func<string, bool> sendCommand)
    {
        if (command.StartsWith("/bmrai obstaclemaps", StringComparison.Ordinal) && !SupportsBmrObstacleMaps) return false;
        command = RouteNativeCommand(command);
        if (command.StartsWith("/gmai ", StringComparison.Ordinal) && NativeHost != "GodMod"
            || command.StartsWith("/bmrai ", StringComparison.Ordinal) && NativeHost is ("GodMod" or "BossMod")
            || command.StartsWith("/vbmai ", StringComparison.Ordinal) && NativeHost is ("GodMod" or "BossModReborn")) return false;
        var relevant = ownedSettings is not null
            && (command is "/bmrai on" or "/bmrai off" or "/gmai on" or "/gmai off" or "/vbmai on" or "/vbmai off"
                || command.StartsWith("/bmrai follow ", StringComparison.Ordinal)
                || command.StartsWith("/gmai follow ", StringComparison.Ordinal)
                || command.StartsWith("/vbmai follow ", StringComparison.Ordinal))
            && command.StartsWith(ownedIdentity.Provider == "VBM" ? "/vbmai " : NativeHost == "GodMod" ? "/gmai " : "/bmrai ", StringComparison.Ordinal);
        var before = BossModSettingsSnapshot.Unavailable;
        if (relevant && (cleanupStarted || cleanupFailed) && !command.EndsWith(" off", StringComparison.Ordinal))
            return false;
        var canObserve = relevant && TryReadOwnedBossModSettings(out before, allowCleanup: cleanupStarted);
        if (relevant && !canObserve)
            return false;
        if (command == "/vbmai on" && IsVbmInteractionPauseActive)
        {
            LastStatus = "VBM AI activation deferred while ADS holds an interaction.";
            return false;
        }
        var submitted = sendCommand(command);
        if (submitted && canObserve && TryReadOwnedBossModSettings(out var after, allowCleanup: cleanupStarted))
        {
            var enabled = !command.EndsWith(" off", StringComparison.Ordinal);
            if (cleanupStarted && BossModRuntimePresetState.Matches(before.Runtime, cleanupRuntimeExpected)
                && BossModSettingsOwnership.IsAiCommandRuntimeEffect(ownedIdentity.Provider, before, after, enabled))
                cleanupRuntimeExpected = after.Runtime;
            ownedSettings!.ObserveAiCommand(before, after, enabled);
        }
        return submitted;
    }

    internal bool BeginOwnedBossModCleanup(string account, string character, bool restoreAi = true)
    {
        if (!ownedDeathObserved && gmcAiManager is not null && ReadNativeDeathState(dead: true))
            ObserveOwnedCombatDeath();
        if (ownedDeathObserved) RefreshOwnedCombatDeath();
        cleanupStarted = ownedSettings is not null && ownedIdentity.Account == account && ownedIdentity.Character == character;
        cleanupRuntimeExpected = cleanupRuntimeTarget = null;
        if (!cleanupStarted)
        {
            if (ownedSettings is not null && !cleanupFailed)
            {
                // A different account/character cannot receive the old run's writes.
                ownedSettings = null;
                ownedProvider = null;
                ownedNativeHost = selectedNativeHost = null;
                selectedProvider = null;
                ForgetGmcPlan();
                cleanupFailed = false;
            }
            return !cleanupFailed;
        }
        cleanupFailed = false;
        if (!TryReadOwnedBossModSettings(out var current, allowCleanup: true))
        {
            cleanupFailed = true;
            return false;
        }
        if (gmcAiManager is not null)
        {
            if (!HasOwnedGmcAi())
            {
                // The native AI or its captured policy was replaced. Preserve that
                // newer runtime/selector while restoring other still-owned fields.
                if (!RestoreGmcPlan())
                {
                    cleanupFailed = true;
                    return false;
                }
                ownedSettings!.ReleaseRuntimeAndSelector();
                ForgetGmcPlan();
            }
            else
            {
                if (current.Runtime is null)
                {
                    cleanupFailed = true;
                    return false;
                }
                var knownRuntime = MatchesGmcRuntime(current);
                var knownDeath = deathRecoveryExpected is { } expected && deathRecoveryTarget is { } target
                    && SameNonRuntimeSettings(expected, current) && ownedSettings!.MatchesNativeAuthority(current)
                    && (BossModRuntimePresetState.Matches(expected.Runtime, current.Runtime)
                        || deathNativeClearAllowed && current.Runtime is { ForceDisabled: true, Names.Count: 0 })
                    && (deathGmcSelection is { } selection
                        ? MatchesGmcSelection(selection, allowMissingJob: true)
                        : ownedSettings.MatchesOwnedRuntime(expected with { Runtime = target }));
                if ((knownRuntime || knownDeath) && current.Runtime is { } runtime)
                    ownedSettings!.OwnRuntime(runtime);
                else
                {
                    gmcCleanupRotation = LiveMember(gmcRotationManager, "Preset");
                    gmcKeepCleanupRotation = true;
                    ownedSettings!.ReleaseRuntime();
                }
                gmcRestoreActiveAi = restoreAi && gmcOriginalAiActive && (knownRuntime || knownDeath)
                    && ownedSettings!.MatchesNativeAuthority(current);
            }
        }
        lastAliveOwnedState = deathRecoveryExpected = null;
        deathRecoveryTarget = null;
        ownedDeathObserved = deathNativeObserved = deathNativeClearAllowed = false;
        lastAliveGmcSelection = deathGmcSelection = null;
        cleanupRuntimeExpected = current.Runtime;
        cleanupRuntimeTarget = ownedSettings!.GetRuntimeCleanupTarget(current, turnEverythingOff: false);
        if (!RestoreGmcPlan())
            cleanupFailed = true;
        if (ownedSettings.OwnedRuntime is not null && current.Runtime is null)
        {
            LastStatus = "BossMod runtime preset restoration retained: current state is unreadable.";
            cleanupFailed = true;
        }
        if (ownedSettings.OwnsStoredSelector
            && current.StoredSelectorReadable && current.StoredAiSelector == ownedSettings.OwnedStoredSelector
            && ownedSettings.Original.StoredSelectorReadable)
        {
            var ownership = ownedSettings;
            var originalSelector = ownership.Original.StoredAiSelector;
            var originalAvailable = IsRestorableAiSelector(originalSelector, ReadPresetCatalog(ownedIdentity.Provider));
            if (!originalAvailable)
            {
                LastStatus = "BMR saved AI selector restoration could not be confirmed.";
                cleanupFailed = true;
            }
            else
            {
                WriteAiSelector(originalSelector ?? string.Empty);
                if (!TryReadOwnedBossModSettings(out var after, allowCleanup: true)
                    || !ReferenceEquals(ownedSettings, ownership))
                {
                    LastStatus = "BMR saved AI selector restoration could not be confirmed.";
                    cleanupFailed = true;
                    return false;
                }
                if (after.Runtime is not null && !BossModRuntimePresetState.Matches(cleanupRuntimeExpected, after.Runtime))
                {
                    cleanupRuntimeExpected = after.Runtime;
                    cleanupRuntimeTarget = ownership.GetRuntimeCleanupTarget(after, turnEverythingOff: false);
                }
                current = after;
                if (!after.StoredSelectorReadable || after.StoredAiSelector != originalSelector)
                {
                    LastStatus = "BMR saved AI selector restoration could not be confirmed.";
                    cleanupFailed = true;
                }
                else
                    ownership.OwnStoredSelector(originalSelector);
            }
        }
        else if (ownedSettings.OwnsStoredSelector && !current.StoredSelectorReadable)
        {
            LastStatus = "BMR saved AI selector restoration retained: current state is unreadable.";
            cleanupFailed = true;
        }
        if (ownedSettings.OwnedDistance is { } distance && current.PreferredDistance == distance
            && ownedSettings.Original.PreferredDistance is { } originalDistance)
        {
            try
            {
                pluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
                    .InvokeFunc(new List<string> { "AIConfig", "PreferredDistance", originalDistance.ToString("R", CultureInfo.CurrentCulture) }, true);
                if (!TryReadOwnedBossModSettings(out var after, allowCleanup: true) || after.PreferredDistance != originalDistance)
                {
                    LastStatus = "Preferred-distance restoration could not be confirmed.";
                    cleanupFailed = true;
                }
            }
            catch { LastStatus = "Preferred-distance restoration endpoint is unavailable."; cleanupFailed = true; }
        }
        else if (ownedSettings.OwnedDistance is not null && current.PreferredDistance is null)
        {
            LastStatus = "Preferred-distance restoration retained: current state is unreadable.";
            cleanupFailed = true;
        }
        if (!RestoreOwnedAutomaticTargeting())
            cleanupFailed = true;
        if (!RestoreGmcCombatSettings())
            cleanupFailed = true;
        return !cleanupFailed;
    }

    internal bool EndOwnedBossModCleanup(bool turnEverythingOff, bool releaseSession = true)
    {
        if (!cleanupStarted || ownedSettings is null)
            return !cleanupFailed;
        if (cleanupRuntimeExpected is not null)
        {
            if (!TryReadOwnedBossModSettings(out var current, allowCleanup: true) || current.Runtime is null)
                cleanupFailed = true;
            else if (BossModRuntimePresetState.Matches(current.Runtime, cleanupRuntimeExpected)
                || ownedSettings.MatchesKnownAiRuntimeEffect(cleanupRuntimeExpected, current))
            {
                var target = turnEverythingOff ? new BossModRuntimePresetState(true, Array.Empty<string>()) : cleanupRuntimeTarget;
                if (target is not null && !BossModRuntimePresetState.Matches(current.Runtime, target))
                {
                    var ownership = ownedSettings;
                    var namesAvailable = target.Names.All(IsExactRuntimePreset);
                    if (namesAvailable) WriteRuntimePreset(ownedIdentity.Provider, target);
                    if (!namesAvailable || !TryReadOwnedBossModSettings(out var restored, allowCleanup: true)
                        || !ReferenceEquals(ownedSettings, ownership) || !BossModRuntimePresetState.Matches(restored.Runtime, target))
                    {
                        LastStatus = "BossMod runtime preset cleanup could not be confirmed.";
                        cleanupFailed = true;
                    }
                }
            }
        }
        var succeeded = !cleanupFailed;
        if (releaseSession && succeeded)
        {
            ownedSettings = null;
            ownedProvider = null;
            ownedNativeHost = null;
            selectedNativeHost = null;
            selectedProvider = null;
            ForgetGmcPlan();
        }
        cleanupStarted = false;
        cleanupRuntimeExpected = null;
        cleanupRuntimeTarget = null;
        return succeeded;
    }

    internal void ReleaseOwnedBossModSettings(bool turnEverythingOff)
    {
        if (ownedSettings is null)
            return;
        var succeeded = BeginOwnedBossModCleanup(ownedIdentity.Account, ownedIdentity.Character);
        succeeded &= EndOwnedBossModCleanup(turnEverythingOff);
        if (!succeeded)
            log.Warning($"[MOGTOME] Owned BossMod settings cleanup was incomplete: {LastStatus}");
    }

    public bool PreparePresetForStart(CombatProvider provider, bool passive, bool useManualPreset, string manualPresetName)
    {
        try
        {
            var manualPresetSelected = !passive && useManualPreset;
            if (manualPresetSelected && string.IsNullOrWhiteSpace(manualPresetName))
            {
                log.Error("[MOGTOME][BossMod] Manual preset is enabled but its name is empty");
                return false;
            }
            var presetName = manualPresetSelected
                ? manualPresetName
                : SelectPresetForCurrentJob(passive);

            if (!ApplyOwnedBossModPreset(presetName))
            {
                log.Error($"[MOGTOME][BossMod] Could not prepare preset '{presetName}' for {provider}: {LastStatus}");
                return false;
            }
            log.Information($"[MOGTOME][BossMod] Prepared {(manualPresetSelected ? "manual" : passive ? "role-based passive" : "role-based active")} preset '{presetName}' for {provider}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Preset startup prep failed: {ex.Message}");
            return false;
        }
    }

    public bool ClearActivePreset(bool releaseSession = true)
    {
        var restored = EndOwnedBossModCleanup(turnEverythingOff: false, releaseSession);
        if (!restored) log.Warning($"[MOGTOME][BossMod] Owned settings restoration was incomplete: {LastStatus}");
        return restored;
    }

    internal void RestoreRsrHealing()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<RsrOtherCommandType, string, object>("RotationSolverReborn.OtherCommand");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "AutoHeal true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "UseGroundBeneficialAbility true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealWhenNothingTodo true");
            // RSR Settings parses numeric strings with the current culture.
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaAbilityHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaSpellHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaAbility {0.90f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaSpell {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleAbilityHot {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleSpellHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleAbility {0.85f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleSpell {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Rotation] RSR healing settings dispatch failed; continuing startup: {ex.Message}");
        }
    }

    public bool TrySetRsrAutoViaIpc()
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<RsrStateCommandType, object>("RotationSolverReborn.ChangeOperatingMode");
            subscriber.InvokeAction(RsrStateCommandType.Auto);
            log.Debug("[MOGTOME][Rotation] RSR Auto mode set via IPC");
            return true;
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][Rotation] RSR mode IPC unavailable: {ex.Message}");
            return false;
        }
    }

    internal bool TryGetRsrOperatingMode(out RsrOperatingMode mode, out string detail)
    {
        mode = RsrOperatingMode.Unknown;
        detail = string.Empty;

        try
        {
            if (!TryFindRsrPluginInstance(out var rsrPlugin, out var internalName, out detail) || rsrPlugin == null)
                return false;

            var dataCenterType = ResolveRsrDataCenterType(rsrPlugin.GetType().Assembly);
            if (dataCenterType == null)
            {
                detail = $"RSR DataCenter type was not found from {internalName}";
                return false;
            }

            if (!TryReadStaticBool(dataCenterType, "State", out var state, out var stateDetail))
            {
                detail = $"RSR DataCenter.State unavailable from {internalName}: {stateDetail}";
                return false;
            }

            if (!state)
            {
                mode = RsrOperatingMode.Off;
                detail = $"RSR {internalName} DataCenter.State=false";
                return true;
            }

            var readAnyModeFlag = false;
            if (TryReadStaticBool(dataCenterType, "IsAutoDuty", out var isAutoDuty, out _))
            {
                readAnyModeFlag = true;
                if (isAutoDuty)
                {
                    mode = RsrOperatingMode.AutoDuty;
                    detail = $"RSR {internalName} DataCenter.State=true, IsAutoDuty=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsHenched", out var isHenched, out _))
            {
                readAnyModeFlag = true;
                if (isHenched)
                {
                    mode = RsrOperatingMode.Henched;
                    detail = $"RSR {internalName} DataCenter.State=true, IsHenched=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsPvPStateEnabled", out var isPvp, out _))
            {
                readAnyModeFlag = true;
                if (isPvp)
                {
                    mode = RsrOperatingMode.PvP;
                    detail = $"RSR {internalName} DataCenter.State=true, IsPvPStateEnabled=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsManual", out var isManual, out _))
            {
                readAnyModeFlag = true;
                if (isManual)
                {
                    mode = RsrOperatingMode.Manual;
                    detail = $"RSR {internalName} DataCenter.State=true, IsManual=true";
                    return true;
                }
            }

            if (TryReadStaticBool(dataCenterType, "IsTargetOnly", out var isTargetOnly, out _))
            {
                readAnyModeFlag = true;
                if (isTargetOnly)
                {
                    mode = RsrOperatingMode.TargetOnly;
                    detail = $"RSR {internalName} DataCenter.State=true, IsTargetOnly=true";
                    return true;
                }
            }

            mode = readAnyModeFlag ? RsrOperatingMode.Auto : RsrOperatingMode.Active;
            detail = readAnyModeFlag
                ? $"RSR {internalName} DataCenter.State=true, mode flags clear"
                : $"RSR {internalName} DataCenter.State=true, mode flags unavailable";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"RSR operating mode probe failed: {ex.Message}";
            return false;
        }
    }

    public bool SendCommand(string command, string purpose)
        => SendBossModAiCommand(command, text => SendNativeCommand(text, purpose));

    private bool SendNativeCommand(string command, string purpose)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
            {
                log.Debug($"[MOGTOME][Rotation] {purpose}: {command}");
                return true;
            }
            else
                log.Warning($"[MOGTOME][Rotation] Command was not handled while attempting to {purpose}: {command}");
        }
        catch (Exception ex)
        {
            log.Warning($"[MOGTOME][Rotation] Failed to {purpose} with {command}: {ex.Message}");
        }
        return false;
    }

    private string SelectPresetForCurrentJob(bool passive)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                throw new InvalidOperationException("LocalPlayer is unavailable while choosing the combat preset");
            }

            if (!player.ClassJob.IsValid)
            {
                throw new InvalidOperationException("LocalPlayer ClassJob is invalid while choosing the combat preset");
            }

            var job = player.ClassJob.Value;
            var rowId = job.RowId;
            var abbreviation = job.Abbreviation.ToString();

            var preset = SelectPresetForJob(rowId, passive);
            log.Information($"[MOGTOME][BossMod] Selected preset '{preset}' for job {abbreviation} ({rowId})");
            return preset;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][BossMod] Failed to choose combat preset: {ex.Message}");
            throw;
        }
    }

    internal static string SelectPresetForJob(uint rowId, bool passive)
    {
        if (TankJobRows.Contains(rowId))
            return passive ? PassiveTankPreset : ActiveTankPreset;
        if (MeleeJobRows.Contains(rowId))
            return passive ? PassiveMeleePreset : ActiveMeleePreset;
        if (RangedJobRows.Contains(rowId))
            return passive ? PassiveRangedPreset : ActiveRangedPreset;
        throw new InvalidOperationException($"Unsupported combat job {rowId}");
    }

    private bool SetActivePresetViaIpc(string presetName)
    {
        var result = TryBoolIpc("BossMod.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossMod IPC");
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossMod.Presets.SetActive returned false for preset '{presetName}'");
            return result.Value;
        }

        result = TryBoolIpc("BossModReborn.Presets.SetActive", presetName);
        if (result.HasValue)
        {
            if (result.Value)
            {
                log.Information($"[MOGTOME][BossMod] Preset '{presetName}' set active via BossModReborn IPC");
            }
            else
                log.Warning($"[MOGTOME][BossMod] BossModReborn.Presets.SetActive returned false for preset '{presetName}'");
            return result.Value;
        }

        var legacyResult = TryStringIpc("BossMod.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossMod.Presets.ForceSet", presetName, legacyResult);
            return legacyResult.Length == 0;
        }

        legacyResult = TryStringIpc("BossModReborn.Presets.ForceSet", presetName);
        if (legacyResult != null)
        {
            LogLegacyPresetResult("BossModReborn.Presets.ForceSet", presetName, legacyResult);
            return legacyResult.Length == 0;
        }

        log.Warning($"[MOGTOME][BossMod] No BossMod-compatible preset IPC responded while setting preset '{presetName}' active");
        return false;
    }

    private bool TryFindRsrPluginInstance(out object? pluginInstance, out string internalName, out string detail)
    {
        foreach (var candidate in new[] { "RotationSolverReborn", "RotationSolver" })
        {
            pluginInstance = FindDalamudPluginInstance(candidate, out detail);
            if (pluginInstance != null)
            {
                internalName = candidate;
                return true;
            }
        }

        pluginInstance = null;
        internalName = string.Empty;
        detail = "RSR plugin instance was not found (checked RotationSolverReborn, RotationSolver)";
        return false;
    }

    private object? FindDalamudPluginInstance(string internalName, out string detail)
    {
        var installedPlugin = FindDalamudPlugin(internalName, out detail);
        if (installedPlugin == null)
            return null;

        var wrapperType = installedPlugin.GetType().Name == "LocalDevPlugin"
            ? installedPlugin.GetType().BaseType
            : installedPlugin.GetType();
        var instance = wrapperType?.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(installedPlugin);
        if (instance == null)
            detail = $"Dalamud plugin {internalName} was installed but its live instance was unavailable";
        return instance;
    }

    internal static object? FindDalamudPlugin(string internalName, out string detail)
    {
        detail = string.Empty;

        try
        {
            var serviceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");

            if (serviceType == null || pluginManagerType == null)
            {
                detail = "Dalamud PluginManager reflection types were unavailable";
                return null;
            }

            var pluginManager = serviceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);

            var installedPlugins = pluginManager?.GetType()
                .GetProperty("InstalledPlugins")
                ?.GetValue(pluginManager) as System.Collections.IList;

            if (installedPlugins == null)
            {
                detail = "Dalamud installed plugin list was unavailable";
                return null;
            }

            foreach (var installedPlugin in installedPlugins)
            {
                if (installedPlugin == null)
                    continue;

                var discoveredInternalName = installedPlugin.GetType()
                    .GetProperty("InternalName")
                    ?.GetValue(installedPlugin)
                    ?.ToString();

                if (!string.Equals(discoveredInternalName, internalName, StringComparison.Ordinal))
                    continue;

                return installedPlugin;
            }

            detail = $"Dalamud plugin {internalName} was not installed";
            return null;
        }
        catch (Exception ex)
        {
            detail = $"FindDalamudPluginInstance({internalName}) failed: {ex.Message}";
            return null;
        }
    }

    private static Type? ResolveRsrDataCenterType(Assembly pluginAssembly)
    {
        const string dataCenterTypeName = "RotationSolver.Basic.DataCenter";

        var directType = pluginAssembly.GetType(dataCenterTypeName, throwOnError: false);
        if (directType != null)
            return directType;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName == null ||
                (!assemblyName.StartsWith("RotationSolver", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(assemblyName, "RotationSolver.Basic", StringComparison.OrdinalIgnoreCase)))
                continue;

            var type = assembly.GetType(dataCenterTypeName, throwOnError: false);
            if (type != null)
                return type;
        }

        return null;
    }

    private static bool TryReadStaticBool(Type type, string propertyName, out bool value, out string detail)
    {
        value = false;
        detail = string.Empty;

        try
        {
            var property = type.GetProperty(propertyName, RsrStaticFlags);
            if (property == null)
            {
                detail = $"property {propertyName} not found";
                return false;
            }

            if (property.PropertyType != typeof(bool))
            {
                detail = $"property {propertyName} was {property.PropertyType.FullName}, not bool";
                return false;
            }

            value = (bool)(property.GetValue(null) ?? false);
            return true;
        }
        catch (Exception ex)
        {
            detail = $"property {propertyName} read failed: {ex.Message}";
            return false;
        }
    }

    private bool? TryBoolIpc(string channel)
    {
        try { return pluginInterface.GetIpcSubscriber<bool>(channel).InvokeFunc(); }
        catch (Exception ex) { log.Debug($"[MOGTOME][BossMod] IPC {channel} unavailable: {ex.GetBaseException().Message}"); return null; }
    }

    private bool? TryBoolIpc<TArg>(string channel, TArg arg)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<TArg, bool>(channel);
            return subscriber.InvokeFunc(arg);
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private string? TryStringIpc<TArg>(string channel, TArg arg)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<TArg, string>(channel);
            return subscriber.InvokeFunc(arg);
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private string? TryStringIpc(string channel)
    {
        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<string>(channel);
            return subscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][BossMod] IPC {channel} not available: {ex.Message}");
            return null;
        }
    }

    private void LogLegacyPresetResult(string channel, string presetName, string result)
    {
        if (result.Length == 0)
            log.Information($"[MOGTOME][BossMod] Preset '{presetName}' handled via legacy IPC channel {channel}");
        else
            log.Warning($"[MOGTOME][BossMod] Legacy IPC {channel} returned '{result}' for preset '{presetName}'");
    }

    public void Dispose() => ReleaseOwnedBossModSettings(turnEverythingOff: false);
}

public sealed record BossModPresetCatalog(string Provider, bool Readable,
    IReadOnlyList<string> DisplayedNames, IReadOnlyList<string> DefinitionNames,
    IReadOnlyList<string> NativeNames, string Detail)
{
    internal static BossModPresetCatalog Unavailable(string provider, string detail)
        => new(provider, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), detail);
}

internal sealed record BossModRuntimePresetState(bool ForceDisabled, IReadOnlyList<string> Names)
{
    internal static bool Matches(BossModRuntimePresetState? first, BossModRuntimePresetState? second)
        => first is not null && second is not null && first.ForceDisabled == second.ForceDisabled
            && first.Names.SequenceEqual(second.Names, StringComparer.Ordinal);
}

internal sealed record BossModSettingsSnapshot(BossModRuntimePresetState? Runtime,
    bool StoredSelectorReadable, string? StoredAiSelector, double? PreferredDistance, bool? AiEnabled,
    bool? ManualTarget = null, bool? ClearPresetOnDeath = null, bool? ForbidActions = null, bool? ForbidMovement = null)
{
    internal static BossModSettingsSnapshot Unavailable { get; } = new(null, false, null, null, null);
}

internal sealed class BossModSettingsOwnership(string provider, BossModSettingsSnapshot original)
{
    internal BossModSettingsSnapshot Original { get; } = original;
    internal BossModRuntimePresetState? OwnedRuntime { get; private set; }
    internal string? OwnedStoredSelector { get; private set; }
    internal bool OwnsStoredSelector { get; private set; }
    internal bool? OwnedForbidActions { get; private set; }
    internal bool? OwnedForbidMovement { get; private set; }
    internal double? OwnedDistance { get; private set; }
    internal bool? OwnedManualTarget { get; private set; }
    private bool? ownedAiEnabled;

    internal bool CanChangeRuntime(BossModSettingsSnapshot current)
        => Original.Runtime is not null && current.Runtime is not null
            && (OwnedRuntime is null ? BossModRuntimePresetState.Matches(current.Runtime, Original.Runtime)
                : MatchesOwnedRuntime(current));
    internal bool CanChangeStoredSelector(BossModSettingsSnapshot current)
        => Original.StoredSelectorReadable && current.StoredSelectorReadable
            && string.Equals(current.StoredAiSelector, OwnsStoredSelector ? OwnedStoredSelector : Original.StoredAiSelector, StringComparison.Ordinal);
    internal bool CanChangeDistance(double? current)
        => Original.PreferredDistance is { } originalDistance && double.IsFinite(originalDistance)
            && current == (OwnedDistance ?? originalDistance);
    internal bool CanChangeManualTarget(bool? current)
        => Original.ManualTarget is { } originalManualTarget
            && current == (OwnedManualTarget ?? originalManualTarget);
    internal bool? GetManualTargetCleanupTarget(bool? current)
        => OwnedManualTarget is { } owned && current == owned ? Original.ManualTarget : null;
    internal void OwnRuntime(BossModRuntimePresetState state) => OwnedRuntime = state;
    internal void ReleaseRuntime() => OwnedRuntime = null;
    internal void ReleaseRuntimeAndSelector()
    {
        OwnedRuntime = null;
        OwnedStoredSelector = null;
        OwnsStoredSelector = false;
    }
    internal bool MatchesAiAuthority(BossModSettingsSnapshot current)
        => current.AiEnabled == (ownedAiEnabled ?? Original.AiEnabled);
    internal bool MatchesNativeAuthority(BossModSettingsSnapshot current)
        => MatchesAiAuthority(current) && (provider != "BMR" || CanChangeStoredSelector(current)
            && current.PreferredDistance == (OwnedDistance ?? Original.PreferredDistance)
            && current.ManualTarget == (OwnedManualTarget ?? Original.ManualTarget)
            && (OwnedForbidActions is null || current.ForbidActions == OwnedForbidActions)
            && (OwnedForbidMovement is null || current.ForbidMovement == OwnedForbidMovement));
    internal void OwnStoredSelector(string? selector)
    {
        OwnedStoredSelector = selector;
        OwnsStoredSelector = true;
    }
    internal bool CanChangeForbidActions(bool? current) => current is not null && current == (OwnedForbidActions ?? Original.ForbidActions);
    internal bool CanChangeForbidMovement(bool? current) => current is not null && current == (OwnedForbidMovement ?? Original.ForbidMovement);
    internal void OwnForbidActions(bool value) => OwnedForbidActions = value;
    internal void OwnForbidMovement(bool value) => OwnedForbidMovement = value;
    internal void OwnDistance(double distance) => OwnedDistance = distance;
    internal void OwnManualTarget(bool manualTarget) => OwnedManualTarget = manualTarget;
    internal void ReleaseManualTarget() => OwnedManualTarget = null;
    internal bool MatchesOwnedRuntime(BossModSettingsSnapshot current)
    {
        if (BossModRuntimePresetState.Matches(current.Runtime, OwnedRuntime))
            return true;
        return OwnedRuntime is { } owned && MatchesKnownAiRuntimeEffect(owned, current);
    }
    internal BossModRuntimePresetState? GetRuntimeCleanupTarget(BossModSettingsSnapshot current, bool turnEverythingOff)
        => current.Runtime is null ? null : turnEverythingOff
            ? new BossModRuntimePresetState(true, Array.Empty<string>())
            : MatchesOwnedRuntime(current) ? Original.Runtime : current.Runtime;
    internal bool MatchesKnownAiRuntimeEffect(BossModRuntimePresetState previous, BossModSettingsSnapshot current)
    {
        if (ownedAiEnabled is null || current.AiEnabled != ownedAiEnabled || previous.ForceDisabled
            || current.Runtime is not { ForceDisabled: false } runtime)
            return false;
        if (provider == "BMR")
            return ownedAiEnabled == true && OwnedStoredSelector is { } selector
                && current.StoredSelectorReadable && current.StoredAiSelector == selector
                && runtime.Names.Count == 1 && runtime.Names[0] == selector;
        // VBM rebuilds only its hidden Multibox entry, appending it when AI is on.
        // The order and exact names of every other active preset must still match.
        var names = previous.Names.Where(name => name != "VBM Multibox");
        return runtime.Names.SequenceEqual(ownedAiEnabled == true ? names.Append("VBM Multibox") : names, StringComparer.Ordinal);
    }
    internal static bool IsAiCommandRuntimeEffect(string provider, BossModSettingsSnapshot before,
        BossModSettingsSnapshot after, bool enabled)
    {
        if (after.AiEnabled != enabled || after.Runtime is null || before.Runtime is null)
            return false;
        if (BossModRuntimePresetState.Matches(before.Runtime, after.Runtime))
            return true;
        return provider == "BMR" && after.Runtime is { ForceDisabled: false, Names.Count: 0 };
    }
    internal void ObserveAiCommand(BossModSettingsSnapshot before, BossModSettingsSnapshot after, bool enabled)
    {
        var matched = CanChangeRuntime(before);
        if (after.AiEnabled == enabled)
            ownedAiEnabled = enabled;
        if (!matched || !IsAiCommandRuntimeEffect(provider, before, after, enabled))
            return;
        OwnedRuntime = after.Runtime;
    }
}
