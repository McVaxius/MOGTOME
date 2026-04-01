using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MOGTOME.Services;

public class AutoDutyPathService
{
    public sealed record PraetoriumPathOption(string FileName, string DisplayName);

    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface PluginInterface;
    private const string BundledPathsRelativeFolder = @"data\autoduty-paths";
    private const string DefaultPraetoriumPathFileName = "(1044) The Praetorium - W2W 20250716 phecda.json";
    private IReadOnlyList<PraetoriumPathOption>? bundledPraetoriumPaths;

    private const string PathFileName = "(1044) The Praetorium - W2W 20250716 phecda.json";

    // Reflection constants
    private static readonly BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // Target values for path forcing
    private const string TargetDutyName = "The Praetorium";
    private const int TargetTerritoryType = 1044;
    private const string TargetPathName = "(1044) The Praetorium - W2W 20250716 phecda";

    // Last result for UI display
    public string LastForceResult { get; private set; } = "Not attempted";

    public AutoDutyPathService(IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.log = log;
        this.PluginInterface = pluginInterface;
    }

    public IReadOnlyList<PraetoriumPathOption> GetPraetoriumPathOptions()
        => bundledPraetoriumPaths ??= LoadBundledPraetoriumPaths();

    public string GetDefaultPraetoriumPathFileName()
    {
        var options = GetPraetoriumPathOptions();
        var defaultOption = options.FirstOrDefault(option =>
            option.FileName.Equals(DefaultPraetoriumPathFileName, StringComparison.OrdinalIgnoreCase));
        return defaultOption?.FileName ?? options.FirstOrDefault()?.FileName ?? DefaultPraetoriumPathFileName;
    }

    public string ResolvePraetoriumPathFileName(string? configuredPathFileName)
    {
        var options = GetPraetoriumPathOptions();

        if (!string.IsNullOrWhiteSpace(configuredPathFileName))
        {
            var match = options.FirstOrDefault(path =>
                path.FileName.Equals(configuredPathFileName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.FileName;
        }

        return GetDefaultPraetoriumPathFileName();
    }

    public string GetPraetoriumPathDisplayName(string? configuredPathFileName)
    {
        var resolved = ResolvePraetoriumPathFileName(configuredPathFileName);
        return GetPraetoriumPathOptions()
            .FirstOrDefault(path => path.FileName.Equals(resolved, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? BuildPraetoriumPathDisplayName(resolved);
    }

    public bool IsAutoDutyInitialized(out string status)
    {
        try
        {
            var autoDutyPlugin = FindDalamudPluginInstance("AutoDuty");
            if (autoDutyPlugin == null)
            {
                status = "AutoDuty plugin not found or not loaded";
                return false;
            }

            var configMainType = autoDutyPlugin.GetType().Assembly.GetType("AutoDuty.Windows.ConfigurationMain");
            if (configMainType == null)
            {
                status = "AutoDuty.Windows.ConfigurationMain type not found";
                return false;
            }

            var configMain = GetMemberValue(configMainType, null, "Instance");
            if (configMain == null)
            {
                status = "ConfigurationMain.Instance is null";
                return false;
            }

            if (GetMemberValue(configMainType, configMain, "Initialized") is bool initialized && initialized)
            {
                status = "Ready";
                return true;
            }

            status = "AutoDuty profile initialization still in progress";
            return false;
        }
        catch (Exception ex)
        {
            status = $"Readiness check failed: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> WaitForAutoDutyInitializationAsync(TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastStatus = "Unknown";

        while (DateTime.UtcNow < deadline)
        {
            if (IsAutoDutyInitialized(out lastStatus))
            {
                return true;
            }

            await Task.Delay(pollInterval);
        }

        log.Warning($"[AutoDutyPath] AutoDuty readiness wait timed out after {timeout.TotalSeconds:F0}s: {lastStatus}");
        return false;
    }

    /// <summary>
    /// Force AutoDuty to select the configured bundled Praetorium path via reflection.
    /// Steps: Mode=Looping, DutyMode=Regular, Duty=Praetorium (1044), Path=configured W2W
    /// Called before anything else on Start, for all party members, while not in duty.
    /// </summary>
    public bool ForcePathSelection(string? configuredPathFileName)
    {
        try
        {
            var selectedPathFileName = ResolvePraetoriumPathFileName(configuredPathFileName);
            var selectedPathName = Path.GetFileNameWithoutExtension(selectedPathFileName) ?? selectedPathFileName;

            log.Information("[AutoDutyPath] === FORCE PATH SELECTION START ===");
            log.Information($"[AutoDutyPath] Selected Praetorium path: {selectedPathFileName}");

            // Step 1: Find AutoDuty plugin using Reflections.md pattern
            var autoDutyPlugin = FindDalamudPluginInstance("AutoDuty");
            
            if (autoDutyPlugin == null)
            {
                LastForceResult = "FAILED: AutoDuty plugin not found or not loaded";
                log.Error($"[AutoDutyPath] {LastForceResult}");
                return false;
            }
            
            var pluginType = autoDutyPlugin.GetType();
            var pluginAssembly = pluginType.Assembly;
            log.Information($"[AutoDutyPath] Found AutoDuty: Type={pluginType.FullName}, Assembly={pluginAssembly.GetName().Name}");

            // Step 2: Get the static plugin instance (AutoDuty uses 'Plugin' static singleton pattern)
            var pluginInstance = GetMemberValue(pluginType, autoDutyPlugin, "Plugin")
                ?? GetMemberValue(pluginType, autoDutyPlugin, "P")
                ?? GetMemberValue(pluginType, autoDutyPlugin, "Instance")
                ?? autoDutyPlugin;
            
            log.Information($"[AutoDutyPath] Plugin instance type: {pluginInstance.GetType().FullName}");

            // Step 3: Get the Configuration object
            var config = GetMemberValue(pluginInstance.GetType(), pluginInstance, "C")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "Config")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "Configuration")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "config");

            if (config != null)
            {
                log.Information($"[AutoDutyPath] Found config: Type={config.GetType().FullName}");
                
                // Step 3a: Set Mode to Looping (typically an enum or int)
                var modeSet = TrySetModeValue(config, "AutoDutyModeEnum", "Looping")
                    || TrySetModeValue(config, "autoDutyModeEnum", "Looping")
                    || TrySetModeValue(config, "mode", "Looping")
                    || TrySetModeValue(config, "Mode", "Looping")
                    || TrySetModeValue(config, "SelectedMode", "Looping")
                    || TrySetModeValue(config, "CurrentMode", "Looping");
                log.Information($"[AutoDutyPath] Set Mode=Looping: {modeSet}");
                if (!modeSet)
                    log.Warning("[AutoDutyPath] Failed to set AutoDuty mode to Looping.");

                // Step 3b: Set DutyMode to Regular
                var dutyModeSet = TrySetModeValue(config, "DutyModeEnum", "Regular")
                    || TrySetModeValue(config, "dutyModeEnum", "Regular")
                    || TrySetModeValue(config, "dutyMode", "Regular")
                    || TrySetModeValue(config, "DutyMode", "Regular")
                    || TrySetModeValue(config, "SelectedDutyMode", "Regular");
                log.Information($"[AutoDutyPath] Set DutyMode=Regular: {dutyModeSet}");
                if (!dutyModeSet)
                    log.Warning("[AutoDutyPath] Failed to set DutyMode=Regular. AutoDuty defaults to Support, so queue behavior may be wrong.");
            }
            else
            {
                log.Warning("[AutoDutyPath] Could not find config object - will try plugin-level members");
            }

            // Step 4: Try to set path-related properties directly on plugin instance
            // AutoDuty may store these at the plugin level rather than config
            var instanceType = pluginInstance.GetType();

            // Try setting currentTerritoryType (exact field name from log)
            var territorySet = SetMemberValue(instanceType, pluginInstance, "currentTerritoryType", (uint)TargetTerritoryType);
            log.Information($"[AutoDutyPath] Set currentTerritoryType={TargetTerritoryType}: {territorySet}");

            // Try setting currentPath by finding the target path index using the FILE DATE method
            var pathIndex = FindPathIndexFromDictionaryPaths(pluginInstance, selectedPathName);
            var pathSet = false;
            if (pathIndex >= 0)
            {
                pathSet = SetMemberValue(instanceType, pluginInstance, "currentPath", pathIndex);
                log.Information($"[AutoDutyPath] Set currentPath={pathIndex} for '{selectedPathName}' (DICTIONARY PATHS METHOD): {pathSet}");
                if (pathSet)
                {
                    LastForceResult = $"OK: Territory={TargetTerritoryType}, Path={pathIndex} ({selectedPathName}) [DictionaryPaths]";
                }
            }
            else
            {
                log.Warning($"[AutoDutyPath] Could not find path index for '{selectedPathName}' using DictionaryPaths; trying file date fallback");
                
                var fallbackIndex = FindPathIndexByFileDate(selectedPathFileName);
                if (fallbackIndex >= 0)
                {
                    pathSet = SetMemberValue(instanceType, pluginInstance, "currentPath", fallbackIndex);
                    log.Information($"[AutoDutyPath] Set currentPath={fallbackIndex} (FILE DATE FALLBACK) for '{selectedPathName}': {pathSet}");
                    if (pathSet)
                    {
                        LastForceResult = $"OK: Territory={TargetTerritoryType}, Path={fallbackIndex} ({selectedPathName}) [FileDateFallback]";
                    }
                }
                else
                {
                    log.Information("[AutoDutyPath] Trying final fallback method (PathSelectionsByPath)...");
                    fallbackIndex = FindPathIndexByName(pluginInstance, selectedPathName);
                    if (fallbackIndex >= 0)
                    {
                        pathSet = SetMemberValue(instanceType, pluginInstance, "currentPath", fallbackIndex);
                        log.Information($"[AutoDutyPath] Set currentPath={fallbackIndex} (PATHSELECTIONS FALLBACK) for '{selectedPathName}': {pathSet}");
                        if (pathSet)
                        {
                            LastForceResult = $"OK: Territory={TargetTerritoryType}, Path={fallbackIndex} ({selectedPathName}) [PathSelectionsFallback]";
                        }
                    }

                    if (!pathSet)
                        LastForceResult = $"FAILED: Could not find path index for '{selectedPathName}'";
                }
            }

            // Step 5: Get Content from ContentHelper.DictionaryContent (already populated by AutoDuty init)
            // CRITICAL: DictionaryContent is a static PROPERTY, not a field! Must use GetProperty().
            // CRITICAL: Do NOT call StartNavigation() - it uses Svc.ClientState.TerritoryType (real game territory, not our field)
            // CRITICAL: Do NOT call ClientState_TerritoryChanged() - it's for zone transitions
            // Instead: Get Content → Set CurrentTerritoryContent → Set pathFile → Call LoadPath()
            log.Information("[AutoDutyPath] === CONTENT & PATH SETUP (direct approach) ===");
            
            object? territoryContent = null;
            try
            {
                // Step 5a: Get ContentHelper.DictionaryContent (static PROPERTY)
                var contentHelperType = pluginAssembly.GetType("AutoDuty.Helpers.ContentHelper");
                if (contentHelperType == null)
                {
                    log.Error("[AutoDutyPath] ContentHelper type not found in assembly");
                }
                else
                {
                    log.Information($"[AutoDutyPath] Found ContentHelper type: {contentHelperType.FullName}");
                    
                    // Try as property first (correct), then as field (fallback), then backing field
                    var dictProp = contentHelperType.GetProperty("DictionaryContent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    object? dictObj = null;
                    
                    if (dictProp != null)
                    {
                        dictObj = dictProp.GetValue(null);
                        log.Information($"[AutoDutyPath] Got DictionaryContent via GetProperty: {dictObj?.GetType().Name ?? "null"}");
                    }
                    else
                    {
                        log.Information("[AutoDutyPath] DictionaryContent not found as property, trying field...");
                        var dictField = contentHelperType.GetField("DictionaryContent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (dictField != null)
                        {
                            dictObj = dictField.GetValue(null);
                            log.Information($"[AutoDutyPath] Got DictionaryContent via GetField: {dictObj?.GetType().Name ?? "null"}");
                        }
                        else
                        {
                            // Try backing field
                            var backingField = contentHelperType.GetField("<DictionaryContent>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
                            if (backingField != null)
                            {
                                dictObj = backingField.GetValue(null);
                                log.Information($"[AutoDutyPath] Got DictionaryContent via backing field: {dictObj?.GetType().Name ?? "null"}");
                            }
                            else
                            {
                                log.Error("[AutoDutyPath] DictionaryContent not found as property, field, or backing field!");
                            }
                        }
                    }
                    
                    if (dictObj != null)
                    {
                        var dictAsIDictionary = dictObj as IDictionary;
                        if (dictAsIDictionary != null)
                        {
                            log.Information($"[AutoDutyPath] DictionaryContent has {dictAsIDictionary.Count} entries");
                            
                            // Look up territory 1044
                            if (dictAsIDictionary.Contains((uint)TargetTerritoryType))
                            {
                                territoryContent = dictAsIDictionary[(uint)TargetTerritoryType];
                                log.Information($"[AutoDutyPath] Found Content for territory {TargetTerritoryType}: {territoryContent?.GetType().Name}");
                            }
                            else
                            {
                                log.Error($"[AutoDutyPath] Territory {TargetTerritoryType} NOT in DictionaryContent! Available keys sample:");
                                int count = 0;
                                foreach (var key in dictAsIDictionary.Keys)
                                {
                                    if (count < 10) log.Information($"[AutoDutyPath]   Key: {key}");
                                    count++;
                                }
                                log.Information($"[AutoDutyPath]   ... ({dictAsIDictionary.Count} total entries)");
                            }
                        }
                        else
                        {
                            log.Error($"[AutoDutyPath] DictionaryContent is not IDictionary, actual type: {dictObj.GetType().FullName}");
                        }
                    }
                }
                
                // Step 5b: Set CurrentTerritoryContent directly on plugin instance
                if (territoryContent != null)
                {
                    // Set via backing field (most reliable for properties with custom getters)
                    var ctcBackingField = instanceType.GetField("<CurrentTerritoryContent>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (ctcBackingField != null)
                    {
                        // Actually the backing field is 'currentTerritoryContent' (lowercase, private field)
                        var ctcField = instanceType.GetField("currentTerritoryContent", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (ctcField != null)
                        {
                            ctcField.SetValue(pluginInstance, territoryContent);
                            log.Information("[AutoDutyPath] Set currentTerritoryContent (private field) directly");
                        }
                        else
                        {
                            ctcBackingField.SetValue(pluginInstance, territoryContent);
                            log.Information("[AutoDutyPath] Set <CurrentTerritoryContent>k__BackingField directly");
                        }
                    }
                    else
                    {
                        // Try the private field directly
                        var ctcField = instanceType.GetField("currentTerritoryContent", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (ctcField != null)
                        {
                            ctcField.SetValue(pluginInstance, territoryContent);
                            log.Information("[AutoDutyPath] Set currentTerritoryContent (private field) directly");
                        }
                        else
                        {
                            // Try property setter
                            var ctcProp = instanceType.GetProperty("CurrentTerritoryContent", AllFlags);
                            if (ctcProp != null && ctcProp.CanWrite)
                            {
                                ctcProp.SetValue(pluginInstance, territoryContent);
                                log.Information("[AutoDutyPath] Set CurrentTerritoryContent via property setter");
                            }
                            else
                            {
                                log.Error("[AutoDutyPath] Could not find any way to set CurrentTerritoryContent!");
                            }
                        }
                    }
                    
                    // Step 5c: Set Config.PathSelectionsByPath[1044] to map W2W path to ALL jobs
                    // This is how AutoDuty persists path selection (MainTab.cs lines 102-118).
                    // When entering dungeon, ClientState_TerritoryChanged resets CurrentPath=-1,
                    // then LoadPath() calls SelectPath() which reads PathSelectionsByPath.
                    // DO NOT call LoadPath() here - it uses Svc.ClientState.TerritoryType and clears everything.
                    try
                    {
                        // Get Config.PathSelectionsByPath field
                        var configType = config!.GetType();
                        var pathSelectionsField = configType.GetField("PathSelectionsByPath", AllFlags);
                        
                        object? pathSelectionsObj = null;
                        if (pathSelectionsField != null)
                        {
                            pathSelectionsObj = pathSelectionsField.GetValue(config);
                            log.Information($"[AutoDutyPath] Got PathSelectionsByPath via field");
                        }
                        else
                        {
                            var pathSelectionsProp = configType.GetProperty("PathSelectionsByPath", AllFlags);
                            if (pathSelectionsProp != null)
                            {
                                pathSelectionsObj = pathSelectionsProp.GetValue(config);
                                log.Information($"[AutoDutyPath] Got PathSelectionsByPath via property");
                            }
                            else
                            {
                                log.Error("[AutoDutyPath] PathSelectionsByPath not found as field or property");
                            }
                        }
                        
                        if (pathSelectionsObj is IDictionary pathSelectionsDict)
                        {
                            log.Information($"[AutoDutyPath] Got PathSelectionsByPath: {pathSelectionsDict.Count} entries");
                            
                            // Get JobWithRole enum type and its "All" value
                            var jobWithRoleType = pluginAssembly.GetType("AutoDuty.Data.Enums+JobWithRole");
                            if (jobWithRoleType == null)
                            {
                                // Try alternative names
                                foreach (var t in pluginAssembly.GetTypes())
                                {
                                    if (t.Name == "JobWithRole")
                                    {
                                        jobWithRoleType = t;
                                        break;
                                    }
                                }
                            }
                            
                            if (jobWithRoleType != null)
                            {
                                log.Information($"[AutoDutyPath] Found JobWithRole type: {jobWithRoleType.FullName}");
                                
                                // Get "All" enum value
                                object? jobWithRoleAll = null;
                                try { jobWithRoleAll = Enum.Parse(jobWithRoleType, "All"); }
                                catch { }
                                
                                if (jobWithRoleAll == null)
                                {
                                    // Fallback: set all bits
                                    jobWithRoleAll = Enum.ToObject(jobWithRoleType, unchecked((long)-1));
                                }
                                log.Information($"[AutoDutyPath] JobWithRole.All = {jobWithRoleAll}");
                                
                                // Get or create the inner dictionary for territory 1044
                                // Structure: Dictionary<uint, Dictionary<string, JobWithRole>>
                                // We need to create the inner dict with the correct generic type
                                var innerDictType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), jobWithRoleType);
                                
                                IDictionary? innerDict = null;
                                if (pathSelectionsDict.Contains((uint)TargetTerritoryType))
                                {
                                    innerDict = pathSelectionsDict[(uint)TargetTerritoryType] as IDictionary;
                                    log.Information($"[AutoDutyPath] Existing PathSelections for {TargetTerritoryType}: {innerDict?.Count ?? 0} entries");
                                    
                                    // Log existing entries
                                    if (innerDict != null)
                                    {
                                        foreach (DictionaryEntry entry in innerDict)
                                            log.Information($"[AutoDutyPath]   {entry.Key} => {entry.Value}");
                                    }
                                }
                                else
                                {
                                    innerDict = Activator.CreateInstance(innerDictType) as IDictionary;
                                    if (innerDict != null)
                                    {
                                        pathSelectionsDict[(uint)TargetTerritoryType] = innerDict;
                                        log.Information($"[AutoDutyPath] Created new PathSelections entry for {TargetTerritoryType}");
                                    }
                                }
                                
                                if (innerDict != null)
                                {
                                    // Clear all job assignments from all paths for this territory
                                    var jobWithRoleNone = Enum.ToObject(jobWithRoleType, 0);
                                    var keysToUpdate = new System.Collections.Generic.List<string>();
                                    foreach (DictionaryEntry entry in innerDict)
                                        keysToUpdate.Add((string)entry.Key);
                                    foreach (var key in keysToUpdate)
                                        innerDict[key] = jobWithRoleNone;
                                    
                                    // Set the selected W2W path to ALL jobs
                                    innerDict[selectedPathFileName] = jobWithRoleAll;
                                    log.Information($"[AutoDutyPath] Set PathSelectionsByPath[{TargetTerritoryType}][{selectedPathFileName}] = All jobs");
                                    
                                    // Call Config.Save() to persist
                                    var saveMethod = configType.GetMethod("Save", AllFlags);
                                    if (saveMethod != null && saveMethod.GetParameters().Length == 0)
                                    {
                                        saveMethod.Invoke(config, null);
                                        log.Information("[AutoDutyPath] Called Config.Save() to persist path selection");
                                    }
                                    else
                                    {
                                        log.Warning("[AutoDutyPath] Config.Save() method not found");
                                    }
                                }
                            }
                            else
                            {
                                log.Error("[AutoDutyPath] JobWithRole enum type not found in assembly");
                            }
                        }
                        else
                        {
                            log.Error($"[AutoDutyPath] PathSelectionsByPath not found or not IDictionary: {pathSelectionsObj?.GetType().FullName ?? "null"}");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"[AutoDutyPath] PathSelectionsByPath setup failed: {ex.Message}");
                    }
                    
                    // Step 5d: Set MainTab.DutySelected and MainListClicked for UI
                    try
                    {
                        var contentPathsManagerType = pluginAssembly.GetType("AutoDuty.Managers.ContentPathsManager");
                        if (contentPathsManagerType != null)
                        {
                            var dictPathsField = contentPathsManagerType.GetField("DictionaryPaths", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                            if (dictPathsField != null)
                            {
                                var dictPathsObj = dictPathsField.GetValue(null) as IDictionary;
                                if (dictPathsObj != null && dictPathsObj.Contains((uint)TargetTerritoryType))
                                {
                                    var pathContainer = dictPathsObj[(uint)TargetTerritoryType];
                                    log.Information($"[AutoDutyPath] Found ContentPathContainer for territory {TargetTerritoryType}");
                                    
                                    // Set MainTab.DutySelected (static field)
                                    var mainTabType = pluginAssembly.GetType("AutoDuty.Windows.MainTab");
                                    if (mainTabType != null)
                                    {
                                        var dutySelectedField = mainTabType.GetField("DutySelected", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                                        if (dutySelectedField != null)
                                        {
                                            dutySelectedField.SetValue(null, pathContainer);
                                            log.Information("[AutoDutyPath] Set MainTab.DutySelected");
                                        }
                                    }
                                }
                            }
                        }
                        
                        // Set MainListClicked = true on plugin instance (triggers UI update)
                        var mlcSet = SetMemberValue(instanceType, pluginInstance, "MainListClicked", true);
                        log.Information($"[AutoDutyPath] Set MainListClicked=true: {mlcSet}");
                    }
                    catch (Exception ex)
                    {
                        log.Information($"[AutoDutyPath] UI setup failed (non-critical): {ex.Message}");
                    }
                    
                    // Step 5e: DO NOT call LoadPath() - it uses Svc.ClientState.TerritoryType
                    // and clears PathFile + Actions when not in dungeon.
                    // LoadPath() will be called automatically by AutoDuty when entering the dungeon.
                }
                else
                {
                    log.Error("[AutoDutyPath] Could not get Content for territory - cannot set CurrentTerritoryContent");
                }
                
                // Step 5f: Verify final state
                var finalContent = GetMemberValue(instanceType, pluginInstance, "CurrentTerritoryContent");
                var finalPath = GetMemberValue(instanceType, pluginInstance, "currentPath") ?? GetMemberValue(instanceType, pluginInstance, "CurrentPath");
                var finalPathFile = GetMemberValue(instanceType, pluginInstance, "pathFile") ?? GetMemberValue(instanceType, pluginInstance, "PathFile");
                var finalActions = GetMemberValue(instanceType, pluginInstance, "Actions");
                
                log.Information($"[AutoDutyPath] === VERIFICATION ===");
                log.Information($"[AutoDutyPath] CurrentTerritoryContent: {finalContent?.ToString() ?? "null"}");
                log.Information($"[AutoDutyPath] currentPath: {finalPath}");
                log.Information($"[AutoDutyPath] pathFile: {finalPathFile}");
                log.Information($"[AutoDutyPath] Actions: {finalActions?.GetType().Name} (Count={((finalActions as System.Collections.IList)?.Count ?? -1)})");
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoDutyPath] Content/path setup failed: {ex.Message}\n{ex.StackTrace}");
            }

            LastForceResult = $"OK: Reflection completed (territory={territorySet}, path={pathSet}, content={territoryContent != null})";
            log.Information($"[AutoDutyPath] === FORCE PATH SELECTION COMPLETE: {LastForceResult} ===");
            return true;
        }
        catch (Exception ex)
        {
            LastForceResult = $"EXCEPTION: {ex.Message}";
            log.Error($"[AutoDutyPath] ForcePathSelection failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Log the structure of AutoDuty's plugin instance and config for debugging.
    /// Only logs once per session to avoid spam.
    /// </summary>
    public void LogAutoDutyStructure(object? pluginInstance = null, object? config = null)
    {
        try
        {
            // If not provided, discover them using Reflections.md pattern
            if (pluginInstance == null)
            {
                pluginInstance = FindDalamudPluginInstance("AutoDuty");
                
                if (pluginInstance == null)
                {
                    log.Warning("[AutoDutyPath] Cannot log structure - AutoDuty not found");
                    return;
                }
            }

            var instanceType = pluginInstance.GetType();

            // Log fields
            log.Information("[AutoDutyPath] === AutoDuty Plugin Structure ===");
            foreach (var field in instanceType.GetFields(AllFlags).OrderBy(f => f.Name))
            {
                try
                {
                    var val = field.GetValue(field.IsStatic ? null : pluginInstance);
                    var valStr = val?.ToString() ?? "null";
                    if (valStr.Length > 100) valStr = valStr[..100] + "...";
                    log.Information($"[AutoDutyPath]   Field: {field.Name} ({field.FieldType.Name}) = {valStr}");
                }
                catch
                {
                    log.Information($"[AutoDutyPath]   Field: {field.Name} ({field.FieldType.Name}) = <error reading>");
                }
            }

            // Log properties
            foreach (var prop in instanceType.GetProperties(AllFlags).OrderBy(p => p.Name))
            {
                try
                {
                    var getter = prop.GetGetMethod(true);
                    if (getter == null) continue;
                    var val = prop.GetValue(getter.IsStatic ? null : pluginInstance);
                    var valStr = val?.ToString() ?? "null";
                    if (valStr.Length > 100) valStr = valStr[..100] + "...";
                    log.Information($"[AutoDutyPath]   Prop: {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
                }
                catch
                {
                    log.Information($"[AutoDutyPath]   Prop: {prop.Name} ({prop.PropertyType.Name}) = <error reading>");
                }
            }

            // Log config structure if available
            if (config != null)
            {
                var configType = config.GetType();
                log.Information($"[AutoDutyPath] === AutoDuty Config Structure ({configType.FullName}) ===");
                foreach (var field in configType.GetFields(AllFlags).OrderBy(f => f.Name))
                {
                    try
                    {
                        var val = field.GetValue(field.IsStatic ? null : config);
                        var valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 100) valStr = valStr[..100] + "...";
                        log.Information($"[AutoDutyPath]   CField: {field.Name} ({field.FieldType.Name}) = {valStr}");
                    }
                    catch
                    {
                        log.Information($"[AutoDutyPath]   CField: {field.Name} ({field.FieldType.Name}) = <error reading>");
                    }
                }
                foreach (var prop in configType.GetProperties(AllFlags).OrderBy(p => p.Name))
                {
                    try
                    {
                        var getter = prop.GetGetMethod(true);
                        if (getter == null) continue;
                        var val = prop.GetValue(getter.IsStatic ? null : config);
                        var valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 100) valStr = valStr[..100] + "...";
                        log.Information($"[AutoDutyPath]   CProp: {prop.Name} ({prop.PropertyType.Name}) = {valStr}");
                    }
                    catch
                    {
                        log.Information($"[AutoDutyPath]   CProp: {prop.Name} ({prop.PropertyType.Name}) = <error reading>");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogAutoDutyStructure failed: {ex.Message}");
        }
    }

    // --- Plugin Discovery (from Reflections.md) ---

    /// <summary>
    /// Find a Dalamud plugin instance using the pattern from Reflections.md.
    /// This handles wrapper objects and local development plugins correctly.
    /// </summary>
    public object? FindDalamudPluginInstance(string internalName)
    {
        try
        {
            var serviceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");

            if (serviceType == null || pluginManagerType == null)
            {
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
                return null;
            }

            foreach (var installedPlugin in installedPlugins)
            {
                if (installedPlugin == null)
                {
                    continue;
                }

                var discoveredInternalName = installedPlugin.GetType()
                    .GetProperty("InternalName")
                    ?.GetValue(installedPlugin)
                    ?.ToString();

                if (!string.Equals(discoveredInternalName, internalName, StringComparison.Ordinal))
                {
                    continue;
                }

                var wrapperType = installedPlugin.GetType().Name == "LocalDevPlugin"
                    ? installedPlugin.GetType().BaseType
                    : installedPlugin.GetType();

                var instanceField = wrapperType?.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                return instanceField?.GetValue(installedPlugin);
            }

            return null;
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] FindDalamudPluginInstance failed: {ex.Message}");
            return null;
        }
    }

    // --- Dynamic Path Discovery Helper ---

    /// <summary>
    /// Explore AutoDuty UI structure to find click/select methods and properties
    /// </summary>
    public void ExploreAutoDutyUI(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[ExploreUI] autoDutyPlugin is null");
                return;
            }

            var pluginType = autoDutyPlugin.GetType();
            var pluginAssembly = pluginType.Assembly;
            
            log.Information("[ExploreUI] === EXPLORING AUTODUTY UI STRUCTURE ===");
            
            // Get MainTab type
            var mainTabType = pluginAssembly.GetType("AutoDuty.Windows.MainTab");
            if (mainTabType == null)
            {
                log.Error("[ExploreUI] MainTab type not found in assembly");
                return;
            }
            
            log.Information($"[ExploreUI] Found MainTab: {mainTabType.FullName}");
            
            // Explore all methods
            var allMethods = mainTabType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            log.Information($"[ExploreUI] Total methods found: {allMethods.Length}");
            
            // Find click-related methods
            var clickMethods = allMethods.Where(m => 
                m.Name.Contains("Click") || 
                m.Name.Contains("Select") || 
                m.Name.Contains("DutyList") ||
                m.Name.Contains("OnList") ||
                m.Name.Contains("ListClick")).ToList();
            
            log.Information($"[ExploreUI] Click/Select methods ({clickMethods.Count}):");
            foreach (var method in clickMethods)
            {
                var paramsStr = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                log.Information($"[ExploreUI]   {method.Name}({paramsStr})");
            }
            
            // Find list-related methods
            var listMethods = allMethods.Where(m => 
                m.Name.Contains("List") && 
                (m.Name.Contains("Selected") || m.Name.Contains("Index") || m.Name.Contains("Current"))).ToList();
            
            log.Information($"[ExploreUI] List selection methods ({listMethods.Count}):");
            foreach (var method in listMethods)
            {
                var paramsStr = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                log.Information($"[ExploreUI]   {method.Name}({paramsStr})");
            }
            
            // Explore all fields/properties
            var allFields = mainTabType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var allProps = mainTabType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            
            // Find selection-related fields
            var selectionFields = allFields.Where(f => 
                f.Name.Contains("Selected") || 
                f.Name.Contains("Index") || 
                f.Name.Contains("Current") ||
                f.Name.Contains("List")).ToList();
            
            log.Information($"[ExploreUI] Selection-related fields ({selectionFields.Count}):");
            foreach (var field in selectionFields)
            {
                log.Information($"[ExploreUI]   {field.FieldType.Name} {field.Name}");
            }
            
            // Find selection-related properties
            var selectionProps = allProps.Where(p => 
                p.Name.Contains("Selected") || 
                p.Name.Contains("Index") || 
                p.Name.Contains("Current") ||
                p.Name.Contains("List")).ToList();
            
            log.Information($"[ExploreUI] Selection-related properties ({selectionProps.Count}):");
            foreach (var prop in selectionProps)
            {
                log.Information($"[ExploreUI]   {prop.PropertyType.Name} {prop.Name}");
            }
            
            // NEW: Look for MainTab instance access patterns
            log.Information("[ExploreUI] === MainTab INSTANCE ACCESS ===");
            
            // Check for static MainTab fields/properties
            var staticMainTabFields = mainTabType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.Name.Contains("Instance") || f.Name.Contains("Current") || f.Name == "Instance" || f.Name == "Current").ToList();
            
            log.Information($"[ExploreUI] Static MainTab access fields ({staticMainTabFields.Count}):");
            foreach (var field in staticMainTabFields)
            {
                try
                {
                    var value = field.GetValue(null);
                    log.Information($"[ExploreUI]   {field.FieldType.Name} {field.Name} = {value?.GetType().Name ?? "null"}");
                }
                catch (Exception ex)
                {
                    log.Information($"[ExploreUI]   {field.FieldType.Name} {field.Name} = ERROR: {ex.Message}");
                }
            }
            
            var staticMainTabProps = mainTabType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.Name.Contains("Instance") || p.Name.Contains("Current") || p.Name == "Instance" || p.Name == "Current").ToList();
            
            log.Information($"[ExploreUI] Static MainTab access properties ({staticMainTabProps.Count}):");
            foreach (var prop in staticMainTabProps)
            {
                try
                {
                    var value = prop.GetValue(null);
                    log.Information($"[ExploreUI]   {prop.PropertyType.Name} {prop.Name} = {value?.GetType().Name ?? "null"}");
                }
                catch (Exception ex)
                {
                    log.Information($"[ExploreUI]   {prop.PropertyType.Name} {prop.Name} = ERROR: {ex.Message}");
                }
            }
            
            // Check plugin instance for MainTab references
            var autoDutyPluginType = autoDutyPlugin.GetType();
            var pluginMainTabFields = autoDutyPluginType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.FieldType == mainTabType || f.FieldType.Name.Contains("MainTab") || f.Name.Contains("MainTab")).ToList();
            
            log.Information($"[ExploreUI] Plugin MainTab reference fields ({pluginMainTabFields.Count}):");
            foreach (var field in pluginMainTabFields)
            {
                try
                {
                    var value = field.GetValue(autoDutyPlugin);
                    log.Information($"[ExploreUI]   {field.FieldType.Name} {field.Name} = {value?.GetType().Name ?? "null"}");
                }
                catch (Exception ex)
                {
                    log.Information($"[ExploreUI]   {field.FieldType.Name} {field.Name} = ERROR: {ex.Message}");
                }
            }
            
            var pluginMainTabProps = autoDutyPluginType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => p.PropertyType == mainTabType || p.PropertyType.Name.Contains("MainTab") || p.Name.Contains("MainTab")).ToList();
            
            log.Information($"[ExploreUI] Plugin MainTab reference properties ({pluginMainTabProps.Count}):");
            foreach (var prop in pluginMainTabProps)
            {
                try
                {
                    var value = prop.GetValue(autoDutyPlugin);
                    log.Information($"[ExploreUI]   {prop.PropertyType.Name} {prop.Name} = {value?.GetType().Name ?? "null"}");
                }
                catch (Exception ex)
                {
                    log.Information($"[ExploreUI]   {prop.PropertyType.Name} {prop.Name} = ERROR: {ex.Message}");
                }
            }
            
            log.Information("[ExploreUI] === UI EXPLORATION COMPLETE ===");
        }
        catch (Exception ex)
        {
            log.Error($"[ExploreUI] Exploration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Simulate clicking on Praetorium in the AutoDuty UI
    /// </summary>
    public void SimulateUIClick(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[SimulateClick] autoDutyPlugin is null");
                return;
            }

            var pluginType = autoDutyPlugin.GetType();
            var pluginAssembly = pluginType.Assembly;
            
            log.Information("[SimulateClick] === SIMULATING PRAETORIUM CLICK ===");
            
            // Get MainTab type
            var mainTabType = pluginAssembly.GetType("AutoDuty.Windows.MainTab");
            if (mainTabType == null)
            {
                log.Error("[SimulateClick] MainTab type not found");
                return;
            }
            
            // Get the path index for Praetorium
            var pathIndex = FindPathIndexFromDictionaryPaths(autoDutyPlugin, TargetPathName);
            if (pathIndex < 0)
            {
                log.Warning("[SimulateClick] Could not find Praetorium path index, using fallback");
                pathIndex = FindPathIndexByName(autoDutyPlugin, TargetPathName);
            }
            
            if (pathIndex < 0)
            {
                log.Error("[SimulateClick] Could not determine path index");
                return;
            }
            
            log.Information($"[SimulateClick] Using path index: {pathIndex}");
            
            // Try different approaches to simulate the click
            
            // Approach 1: Try calling click/select methods
            var allMethods = mainTabType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var clickMethods = allMethods.Where(m => 
                m.Name.Contains("Click") || 
                m.Name.Contains("Select") || 
                m.Name.Contains("DutyList") ||
                m.Name.Contains("OnList")).ToList();
            
            log.Information($"[SimulateClick] Testing {clickMethods.Count} click methods...");
            
            foreach (var method in clickMethods)
            {
                try
                {
                    var parameters = method.GetParameters();
                    object?[] args;
                    
                    if (parameters.Length == 0)
                    {
                        args = new object?[] { };
                    }
                    else if (parameters.Length == 1)
                    {
                        if (parameters[0].ParameterType == typeof(int))
                            args = new object?[] { pathIndex };
                        else if (parameters[0].ParameterType == typeof(uint))
                            args = new object?[] { (uint)pathIndex };
                        else if (parameters[0].ParameterType == typeof(string))
                            args = new object?[] { TargetPathName };
                        else
                            continue; // Skip incompatible parameter types
                    }
                    else
                    {
                        continue; // Skip methods with multiple parameters
                    }
                    
                    // Try both static and instance invocation
                    object? target = method.IsStatic ? null : autoDutyPlugin;
                    
                    method.Invoke(target, args);
                    log.Information($"[SimulateClick] ✓ Called: {method.Name}({string.Join(", ", args)})");
                    break; // Success, stop trying other methods
                }
                catch (Exception ex)
                {
                    log.Debug($"[SimulateClick] ✗ Failed: {method.Name} - {ex.Message}");
                }
            }
            
            // Approach 2: Try setting selection properties
            var allFields = mainTabType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var selectionFields = allFields.Where(f => 
                f.Name.Contains("Selected") && f.Name.Contains("Index")).ToList();
            
            log.Information($"[SimulateClick] Testing {selectionFields.Count} selection fields...");
            
            foreach (var field in selectionFields)
            {
                try
                {
                    object? target = field.IsStatic ? null : autoDutyPlugin;
                    
                    if (field.FieldType == typeof(int))
                        field.SetValue(target, pathIndex);
                    else if (field.FieldType == typeof(uint))
                        field.SetValue(target, (uint)pathIndex);
                    else
                        continue;
                        
                    log.Information($"[SimulateClick] ✓ Set field: {field.Name} = {pathIndex}");
                }
                catch (Exception ex)
                {
                    log.Debug($"[SimulateClick] ✗ Failed to set {field.Name}: {ex.Message}");
                }
            }
            
            // Approach 3: Try to trigger UI refresh
            var refreshMethods = allMethods.Where(m => 
                m.Name.Contains("Refresh") || 
                m.Name.Contains("Update") || 
                m.Name.Contains("Draw") || 
                m.Name.Contains("Render")).Take(5).ToList();
            
            log.Information($"[SimulateClick] Testing {refreshMethods.Count} refresh methods...");
            
            foreach (var method in refreshMethods)
            {
                try
                {
                    if (method.GetParameters().Length == 0)
                    {
                        object? target = method.IsStatic ? null : autoDutyPlugin;
                        method.Invoke(target, new object?[] { });
                        log.Information($"[SimulateClick] ✓ Called refresh: {method.Name}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[SimulateClick] ✗ Failed refresh {method.Name}: {ex.Message}");
                }
            }
            
            log.Information("[SimulateClick] === CLICK SIMULATION COMPLETE ===");
        }
        catch (Exception ex)
        {
            log.Error($"[SimulateClick] Click simulation failed: {ex.Message}");
        }
    }
    public int FindPathIndexFromDictionaryPaths(object? autoDutyPlugin, string targetPathName)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null in FindPathIndexFromDictionaryPaths");
                return -1;
            }

            var pluginType = autoDutyPlugin.GetType();
            var pluginAssembly = pluginType.Assembly;
            
            // Get ContentPathsManager type
            var contentPathsManagerType = pluginAssembly.GetType("AutoDuty.Managers.ContentPathsManager");
            if (contentPathsManagerType == null)
            {
                log.Warning("[AutoDutyPath] ContentPathsManager type not found in assembly");
                return -1;
            }

            // Get DictionaryPaths static field
            var dictPathsField = contentPathsManagerType.GetField("DictionaryPaths", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (dictPathsField == null)
            {
                log.Warning("[AutoDutyPath] DictionaryPaths field not found in ContentPathsManager");
                return -1;
            }

            var dictPathsObj = dictPathsField.GetValue(null) as IDictionary;
            if (dictPathsObj == null)
            {
                log.Warning("[AutoDutyPath] DictionaryPaths is null or not a dictionary");
                return -1;
            }

            // Get Praetorium paths (territory 1044)
            var targetTerritory = (uint)1044;
            if (!dictPathsObj.Contains(targetTerritory))
            {
                log.Warning($"[AutoDutyPath] Territory {targetTerritory} not found in DictionaryPaths");
                return -1;
            }

            var pathContainer = dictPathsObj[targetTerritory];
            if (pathContainer == null)
            {
                log.Warning($"[AutoDutyPath] Path container for territory {targetTerritory} is null");
                return -1;
            }

            // Get the Paths list from the container
            var containerType = pathContainer.GetType();
            var pathsField = containerType.GetField("Paths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var pathsProperty = containerType.GetProperty("Paths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pathsField == null && pathsProperty == null)
            {
                log.Warning("[AutoDutyPath] Paths field/property not found in path container");
                return -1;
            }

            var pathsList =
                pathsProperty?.GetValue(pathContainer) as System.Collections.IList ??
                pathsField?.GetValue(pathContainer) as System.Collections.IList;
            if (pathsList == null)
            {
                log.Warning("[AutoDutyPath] Paths list is null or not an IList");
                return -1;
            }

            // Search through the Paths list to find our target path
            log.Information($"[AutoDutyPath] Searching through {pathsList.Count} paths for '{targetPathName}'");
            
            for (int i = 0; i < pathsList.Count; i++)
            {
                var pathObj = pathsList[i];
                if (pathObj != null)
                {
                    // Try to get the path name - check common properties
                    var pathType = pathObj.GetType();
                    var nameProp = pathType.GetProperty("Name") ?? pathType.GetProperty("PathName") ?? pathType.GetProperty("FileName");
                    
                    if (nameProp != null)
                    {
                        var pathName = nameProp.GetValue(pathObj)?.ToString();
                        if (!string.IsNullOrEmpty(pathName))
                        {
                            log.Information($"[AutoDutyPath] Path[{i}]: {pathName}");
                            
                            if (IsTargetPath(pathName, targetPathName))
                            {
                                log.Information($"[AutoDutyPath] *** FOUND TARGET PATH at index {i}: {pathName} ***");
                                return i;
                            }
                        }
                    }
                }
            }

            log.Warning($"[AutoDutyPath] Target path '{targetPathName}' not found in {pathsList.Count} paths");
            return -1;
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] FindPathIndexFromDictionaryPaths failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Legacy fallback: find the path index by sorting Praetorium files by modification date.
    /// DictionaryPaths is preferred and should be attempted first.
    /// </summary>
    public int FindPathIndexByFileDate(string targetPathName)
    {
        try
        {
            log.Information($"[AutoDutyPath] Finding path index by file date for: {targetPathName}");
            
            // Get AutoDuty plugin to find paths folder
            var autoDutyPlugin = FindDalamudPluginInstance("AutoDuty");
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] AutoDuty plugin not found for file date search");
                return -1;
            }
            
            var pathsDirectory = GetAutoDutyPathsFolder(autoDutyPlugin);
            if (string.IsNullOrEmpty(pathsDirectory) || !Directory.Exists(pathsDirectory))
            {
                log.Error($"[AutoDutyPath] Could not find AutoDuty paths directory. Checked: {string.Join(" | ", GetAutoDutyPathsFolderCandidates(autoDutyPlugin))}");
                return -1;
            }
            
            // Find all (1044)*.json files
            var praetoriumFiles = Directory.GetFiles(pathsDirectory, "(1044)*.json")
                .Where(file => File.Exists(file))
                .ToList();
            
            log.Information($"[AutoDutyPath] Found {praetoriumFiles.Count()} Praetorium files in {pathsDirectory}");
            
            if (praetoriumFiles.Count == 0)
            {
                log.Warning("[AutoDutyPath] No (1044)*.json files found in paths directory");
                return -1;
            }
            
            // Sort by Date Modified ascending (AutoDuty UI order)
            var sortedFiles = praetoriumFiles
                .OrderBy(file => File.GetLastWriteTime(file))
                .ToList();
            
            log.Information("[AutoDutyPath] Praetorium files sorted by Date Modified:");
            for (int i = 0; i < sortedFiles.Count; i++)
            {
                var fileName = Path.GetFileName(sortedFiles[i]);
                var date = File.GetLastWriteTime(sortedFiles[i]);
                log.Information($"[AutoDutyPath]   Index {i}: {fileName} (Modified: {date:yyyy-MM-dd HH:mm:ss})");
            }
            
            // Find the target file index
            var targetFileName = Path.GetFileName(targetPathName);
            var targetIndex = sortedFiles.FindIndex(file => 
                Path.GetFileName(file).Equals(targetFileName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Contains(targetFileName.Replace(".json", "")));
            
            if (targetIndex >= 0)
            {
                log.Information($"[AutoDutyPath] Found target file at index {targetIndex}: {Path.GetFileName(sortedFiles[targetIndex])}");
                return targetIndex;
            }
            else
            {
                log.Warning($"[AutoDutyPath] Target file '{targetPathName}' not found in sorted list");
                return -1;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] FindPathIndexByFileDate failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Find the path index by searching through PathSelectionsByPath for the target path name.
    /// NOTE: This method may return incorrect index as it counts dictionary keys, not actual path order.
    /// Use FindPathIndexFromDictionaryPaths() instead for correct results.
    /// </summary>
    public int FindPathIndexByName(object? autoDutyPlugin, string targetPathName)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null in FindPathIndexByName");
                return -1;
            }

            var pluginType = autoDutyPlugin.GetType();
            
            // Get the Configuration object
            var config = GetMemberValue(pluginType, autoDutyPlugin, "Configuration");
            if (config == null)
            {
                log.Warning("[AutoDutyPath] Could not get Configuration in FindPathIndexByName");
                return -1;
            }

            // Get PathSelectionsByPath dictionary
            var pathSelections = GetMemberValue(config.GetType(), config, "PathSelectionsByPath");
            if (pathSelections == null)
            {
                log.Warning("[AutoDutyPath] Could not get PathSelectionsByPath in FindPathIndexByName");
                return -1;
            }

            var dictType = pathSelections.GetType();
            
            // Get indexer to access territory 1044
            var indexerProp = dictType.GetProperty("Item");
            if (indexerProp == null)
            {
                log.Warning("[AutoDutyPath] Could not get indexer for PathSelectionsByPath");
                return -1;
            }

            // Get Praetorium paths (territory 1044)
            var targetTerritory = (uint)1044;
            var praetoriumPaths = indexerProp.GetValue(pathSelections, new object[] { targetTerritory });
            
            if (praetoriumPaths == null)
            {
                log.Warning($"[AutoDutyPath] No paths found for territory {targetTerritory}");
                return -1;
            }

            // Search through the inner dictionary for our target path
            var innerDictType = praetoriumPaths.GetType();
            var innerKeysProp = innerDictType.GetProperty("Keys");
            var innerKeys = (System.Collections.ICollection?)innerKeysProp?.GetValue(praetoriumPaths);
            
            if (innerKeys == null)
            {
                log.Warning("[AutoDutyPath] Could not get inner keys from Praetorium paths");
                return -1;
            }

            var pathIndex = 0;
            foreach (var key in innerKeys)
            {
                var pathName = key?.ToString();
                if (!string.IsNullOrEmpty(pathName))
                {
                    // Check if this matches our target (flexible matching)
                    if (IsTargetPath(pathName, targetPathName))
                    {
                        log.Information($"[AutoDutyPath] Found target path at index {pathIndex}: {pathName}");
                        return pathIndex;
                    }
                    pathIndex++;
                }
            }

            log.Warning($"[AutoDutyPath] Target path '{targetPathName}' not found in {pathIndex} available paths");
            return -1;
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] FindPathIndexByName failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Check if a path name matches our target (flexible matching).
    /// </summary>
    private bool IsTargetPath(string pathName, string targetPathName)
    {
        // Remove .json extension if present for comparison
        var cleanPathName = pathName.EndsWith(".json") ? pathName[..^5] : pathName;
        var cleanTargetName = targetPathName.EndsWith(".json") ? targetPathName[..^5] : targetPathName;
        
        // Exact match
        if (cleanPathName.Equals(cleanTargetName, StringComparison.OrdinalIgnoreCase))
            return true;
            
        // Check if it contains key identifiers
        if (cleanPathName.Contains("Praetorium") && 
            cleanPathName.Contains("W2W") && 
            cleanPathName.Contains("phecda"))
            return true;
            
        return false;
    }

    // --- Path Data Exploration Helper ---

    /// <summary>
    /// Explore different locations to find path data (Configuration, MainWindow, etc.).
    /// </summary>
    public void ExplorePathData(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null");
                return;
            }

            log.Information("[AutoDutyPath] === Exploring Path Data Locations ===");

            var pluginType = autoDutyPlugin.GetType();

            // 1. Check Configuration object for path-related fields
            var config = GetMemberValue(pluginType, autoDutyPlugin, "Configuration");
            if (config != null)
            {
                log.Information("[AutoDutyPath] Exploring Configuration object...");
                var configType = config.GetType();
                
                // Look for path-related fields/properties
                var pathMembers = configType.GetMembers(AllFlags)
                    .Where(m => m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("Duty", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("Territory", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("Content", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var member in pathMembers)
                {
                    try
                    {
                        object? value = null;
                        if (member.MemberType == System.Reflection.MemberTypes.Field)
                        {
                            value = ((FieldInfo)member).GetValue(config);
                        }
                        else if (member.MemberType == System.Reflection.MemberTypes.Property)
                        {
                            value = ((PropertyInfo)member).GetValue(config);
                        }
                        else
                        {
                            continue; // Skip methods, events, etc.
                        }
                        log.Information($"[AutoDutyPath] Config.{member.Name}: {value}");
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"[AutoDutyPath] Could not access Config.{member.Name}: {ex.Message}");
                    }
                }
            }

            // 2. Check MainWindow for path lists/dictionaries
            var mainWindow = GetMemberValue(pluginType, autoDutyPlugin, "MainWindow");
            if (mainWindow != null)
            {
                log.Information("[AutoDutyPath] Exploring MainWindow object...");
                var mwType = mainWindow.GetType();
                
                var pathMembers = mwType.GetMembers(AllFlags)
                    .Where(m => m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("Duty", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("List", StringComparison.OrdinalIgnoreCase) ||
                               m.Name.Contains("Dictionary", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var member in pathMembers)
                {
                    try
                    {
                        object? value = null;
                        if (member.MemberType == System.Reflection.MemberTypes.Field)
                        {
                            value = ((FieldInfo)member).GetValue(mainWindow);
                        }
                        else if (member.MemberType == System.Reflection.MemberTypes.Property)
                        {
                            value = ((PropertyInfo)member).GetValue(mainWindow);
                        }
                        else
                        {
                            continue; // Skip methods, events, etc.
                        }
                        log.Information($"[AutoDutyPath] MainWindow.{member.Name}: {value}");
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"[AutoDutyPath] Could not access MainWindow.{member.Name}: {ex.Message}");
                    }
                }
            }

            // 3. Check plugin-level path-related members
            log.Information("[AutoDutyPath] Exploring plugin-level members...");
            var pluginPathMembers = pluginType.GetMembers(AllFlags)
                .Where(m => m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                           m.Name.Contains("Duty", StringComparison.OrdinalIgnoreCase) ||
                           m.Name.Contains("Territory", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var member in pluginPathMembers)
            {
                try
                {
                    object? value = null;
                    if (member.MemberType == System.Reflection.MemberTypes.Field)
                    {
                        value = ((FieldInfo)member).GetValue(autoDutyPlugin);
                    }
                    else if (member.MemberType == System.Reflection.MemberTypes.Property)
                    {
                        value = ((PropertyInfo)member).GetValue(autoDutyPlugin);
                    }
                    else
                    {
                        continue; // Skip methods, events, etc.
                    }
                    log.Information($"[AutoDutyPath] Plugin.{member.Name}: {value}");
                }
                catch (Exception ex)
                {
                    log.Warning($"[AutoDutyPath] Could not access Plugin.{member.Name}: {ex.Message}");
                }
            }

            log.Information("[AutoDutyPath] === Path Data Exploration Complete ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] ExplorePathData failed: {ex.Message}");
        }
    }

    // --- Current Selection Logging Helper ---

    /// <summary>
    /// Check what path AutoDuty currently has selected.
    /// </summary>
    public void LogCurrentSelection(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null in LogCurrentSelection");
                return;
            }

            var pluginType = autoDutyPlugin.GetType();
            log.Information("[AutoDutyPath] === CURRENT AUTO DUTY SELECTION ===");

            // Check plugin-level values
            var currentTerritoryType = GetMemberValue(pluginType, autoDutyPlugin, "currentTerritoryType");
            var currentPath = GetMemberValue(pluginType, autoDutyPlugin, "currentPath");
            var pathFile = GetMemberValue(pluginType, autoDutyPlugin, "pathFile");
            
            log.Information($"[AutoDutyPath] Plugin currentTerritoryType: {currentTerritoryType}");
            log.Information($"[AutoDutyPath] Plugin currentPath: {currentPath}");
            log.Information($"[AutoDutyPath] Plugin pathFile: {pathFile}");

            // Check config-level values
            var config = GetMemberValue(pluginType, autoDutyPlugin, "Configuration");
            if (config != null)
            {
                var configType = config.GetType();
                
                var selectedPathName = GetMemberValue(configType, config, "SelectedPathName");
                var selectedTerritoryType = GetMemberValue(configType, config, "SelectedPathTerritoryType");
                var selectedMode = GetMemberValue(configType, config, "SelectedMode");
                
                log.Information($"[AutoDutyPath] Config SelectedPathName: {selectedPathName}");
                log.Information($"[AutoDutyPath] Config SelectedPathTerritoryType: {selectedTerritoryType}");
                log.Information($"[AutoDutyPath] Config SelectedMode: {selectedMode}");

                // Try to get the actual path file that would be loaded
                if (currentTerritoryType != null && currentPath != null)
                {
                    var territory = (uint)currentTerritoryType;
                    var pathIndex = (int)currentPath;
                    
                    // Get the path name from PathSelectionsByPath
                    var pathSelections = GetMemberValue(configType, config, "PathSelectionsByPath");
                    if (pathSelections != null)
                    {
                        var dictType = pathSelections.GetType();
                        var indexerProp = dictType.GetProperty("Item");
                        if (indexerProp != null)
                        {
                            var territoryPaths = indexerProp.GetValue(pathSelections, new object[] { territory });
                            if (territoryPaths != null)
                            {
                                var innerDictType = territoryPaths.GetType();
                                var innerKeysProp = innerDictType.GetProperty("Keys");
                                var innerKeys = (System.Collections.ICollection?)innerKeysProp?.GetValue(territoryPaths);
                                
                                if (innerKeys != null)
                                {
                                    var index = 0;
                                    foreach (var key in innerKeys)
                                    {
                                        if (index == pathIndex)
                                        {
                                            var pathName = key?.ToString();
                                            log.Information($"[AutoDutyPath] *** CURRENT PATH AT INDEX {pathIndex}: {pathName} ***");
                                            break;
                                        }
                                        index++;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Check Actions list to see what's actually loaded
            var actions = GetMemberValue(pluginType, autoDutyPlugin, "Actions");
            if (actions != null)
            {
                var actionsType = actions.GetType();
                var countProp = actionsType.GetProperty("Count");
                if (countProp != null)
                {
                    var count = countProp.GetValue(actions);
                    log.Information($"[AutoDutyPath] Actions list count: {count}");
                    
                    if (count is int actionCount && actionCount > 0)
                    {
                        // Try to get the first action to see what path is loaded
                        var indexerProp = actionsType.GetProperty("Item");
                        if (indexerProp != null)
                        {
                            var firstAction = indexerProp.GetValue(actions, new object[] { 0 });
                            log.Information($"[AutoDutyPath] First action: {firstAction}");
                        }
                    }
                }
            }

            log.Information("[AutoDutyPath] === CURRENT SELECTION COMPLETE ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogCurrentSelection failed: {ex.Message}");
        }
    }

    // --- Config Field Discovery Helper ---

    /// <summary>
    /// Find actual config field names for path selection.
    /// </summary>
    public void LogConfigFields(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null in LogConfigFields");
                return;
            }

            var pluginType = autoDutyPlugin.GetType();
            var config = GetMemberValue(pluginType, autoDutyPlugin, "Configuration");
            
            if (config == null)
            {
                log.Warning("[AutoDutyPath] Configuration not found in LogConfigFields");
                return;
            }

            var configType = config.GetType();
            log.Information("[AutoDutyPath] === CONFIG FIELD NAMES ===");

            // Look for path-related field names
            var pathKeywords = new[] { "path", "territory", "duty", "selected", "current", "mode" };
            
            var allMembers = configType.GetMembers(AllFlags);
            var pathMembers = allMembers
                .Where(m => pathKeywords.Any(keyword => m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Name)
                .ToArray();

            log.Information($"[AutoDutyPath] Found {pathMembers.Length} path-related fields:");

            foreach (var member in pathMembers)
            {
                try
                {
                    object? value = null;
                    if (member.MemberType == MemberTypes.Field)
                    {
                        value = ((FieldInfo)member).GetValue(config);
                    }
                    else if (member.MemberType == MemberTypes.Property)
                    {
                        value = ((PropertyInfo)member).GetValue(config);
                    }
                    
                    log.Information($"[AutoDutyPath]   {member.Name} ({member.MemberType}): {value}");
                }
                catch (Exception ex)
                {
                    log.Information($"[AutoDutyPath]   {member.Name} ({member.MemberType}): ERROR - {ex.Message}");
                }
            }

            // Also look for any fields that might contain our target values
            log.Information("[AutoDutyPath] === SEARCHING FOR TARGET VALUES ===");
            
            var targetTerritory = "1044";
            var targetPath = "(1044) The Praetorium - W2W 20250716 phecda";
            
            foreach (var member in allMembers.Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property))
            {
                try
                {
                    object? value = null;
                    if (member.MemberType == MemberTypes.Field)
                    {
                        value = ((FieldInfo)member).GetValue(config);
                    }
                    else if (member.MemberType == MemberTypes.Property)
                    {
                        value = ((PropertyInfo)member).GetValue(config);
                    }
                    
                    if (value != null)
                    {
                        var valueStr = value.ToString();
                        if (valueStr.Contains(targetTerritory) || valueStr.Contains("Praetorium") || valueStr.Contains("phecda"))
                        {
                            log.Information($"[AutoDutyPath] *** CONFIG MATCH: {member.Name} = {value}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip errors for this search
                }
            }

            // Now search plugin-level fields for path selection
            log.Information("[AutoDutyPath] === SEARCHING PLUGIN LEVEL ===");
            
            var pluginMembers = pluginType.GetMembers(AllFlags)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property)
                .ToArray();

            foreach (var member in pluginMembers)
            {
                try
                {
                    object? value = null;
                    if (member.MemberType == MemberTypes.Field)
                    {
                        value = ((FieldInfo)member).GetValue(autoDutyPlugin);
                    }
                    else if (member.MemberType == MemberTypes.Property)
                    {
                        value = ((PropertyInfo)member).GetValue(autoDutyPlugin);
                    }
                    
                    if (value != null)
                    {
                        var valueStr = value.ToString();
                        if (valueStr.Contains(targetTerritory) || valueStr.Contains("Praetorium") || valueStr.Contains("phecda"))
                        {
                            log.Information($"[AutoDutyPath] *** PLUGIN MATCH: {member.Name} = {value}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip errors for this search
                }
            }

            log.Information("[AutoDutyPath] === CONFIG FIELDS COMPLETE ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogConfigFields failed: {ex.Message}");
        }
    }

    // --- Method Discovery Helper ---

    /// <summary>
    /// Find Save/Apply/Load methods in AutoDuty.
    /// </summary>
    public void LogAutoDutyMethods(object? autoDutyPlugin)
    {
        try
        {
            if (autoDutyPlugin == null)
            {
                log.Warning("[AutoDutyPath] autoDutyPlugin is null in LogAutoDutyMethods");
                return;
            }

            var pluginType = autoDutyPlugin.GetType();
            log.Information("[AutoDutyPath] === AUTODUTY METHODS ===");

            // Look for common method names
            var methodNames = new[] { 
                "Save", "Apply", "Load", "Refresh", "Update", "Restart", "Rebuild",
                "SaveConfig", "ApplyConfig", "LoadConfig", "RefreshConfig",
                "SelectPath", "LoadPath", "SetPath", "ChangePath",
                "OnPathChanged", "OnTerritoryChanged", "OnConfigChanged"
            };

            foreach (var methodName in methodNames)
            {
                var methods = pluginType.GetMethods(AllFlags)
                    .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    log.Information($"[AutoDutyPath] Method: {method.Name}({paramStr}) -> {method.ReturnType.Name}");
                }
            }

            // Also check the Configuration object for methods
            var config = GetMemberValue(pluginType, autoDutyPlugin, "Configuration");
            if (config != null)
            {
                var configType = config.GetType();
                log.Information("[AutoDutyPath] === CONFIG METHODS ===");
                
                foreach (var methodName in methodNames)
                {
                    var methods = configType.GetMethods(AllFlags)
                        .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        log.Information($"[AutoDutyPath] Config Method: {method.Name}({paramStr}) -> {method.ReturnType.Name}");
                    }
                }
            }

            log.Information("[AutoDutyPath] === METHODS COMPLETE ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogAutoDutyMethods failed: {ex.Message}");
        }
    }

    // --- Path Selections Logging Helper ---

    /// <summary>
    /// Log the PathSelectionsByPath dictionary to find territory → path mappings.
    /// </summary>
    public void LogPathSelections(object? pathSelections)
    {
        try
        {
            if (pathSelections == null)
            {
                log.Warning("[AutoDutyPath] PathSelectionsByPath is null");
                return;
            }

            var dictType = pathSelections.GetType();
            log.Information($"[AutoDutyPath] === PathSelectionsByPath ({dictType.FullName}) ===");

            // Check if it's a dictionary
            if (!dictType.IsGenericType || dictType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.Dictionary<,>))
            {
                log.Warning("[AutoDutyPath] PathSelectionsByPath is not a Dictionary");
                return;
            }

            // Get the Keys collection (territory types)
            var keysProp = dictType.GetProperty("Keys");
            var valuesProp = dictType.GetProperty("Values");
            
            if (keysProp == null || valuesProp == null)
            {
                log.Warning("[AutoDutyPath] Could not get Keys/Values properties from PathSelectionsByPath");
                return;
            }

            var keys = (System.Collections.ICollection?)keysProp.GetValue(pathSelections);
            var values = (System.Collections.ICollection?)valuesProp.GetValue(pathSelections);

            if (keys == null || values == null)
            {
                log.Warning("[AutoDutyPath] Keys or Values collection is null");
                return;
            }

            log.Information($"[AutoDutyPath] Total territories: {keys.Count}");

            // Get indexer to access individual entries
            var indexerProp = dictType.GetProperty("Item");
            if (indexerProp == null)
            {
                log.Warning("[AutoDutyPath] Could not get indexer property from PathSelectionsByPath");
                return;
            }

            // Look for territory 1044 (Praetorium)
            var targetTerritory = (uint)1044;
            var praetoriumPaths = indexerProp.GetValue(pathSelections, new object[] { targetTerritory });

            if (praetoriumPaths != null)
            {
                log.Information($"[AutoDutyPath] *** FOUND PRAETORIUM (1044) PATHS ***");
                log.Information($"[AutoDutyPath] Praetorium paths: {praetoriumPaths}");
                
                // Try to explore the inner dictionary
                var innerDictType = praetoriumPaths.GetType();
                if (innerDictType.IsGenericType && innerDictType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.Dictionary<,>))
                {
                    var innerKeysProp = innerDictType.GetProperty("Keys");
                    var innerKeys = (System.Collections.ICollection?)innerKeysProp?.GetValue(praetoriumPaths);
                    
                    if (innerKeys != null)
                    {
                        log.Information($"[AutoDutyPath] Praetorium path count: {innerKeys.Count}");
                        
                        // Log all available paths for research first
                        log.Information($"[AutoDutyPath] Available Praetorium paths:");
                        var pathIndex = 0;
                        foreach (var key in innerKeys)
                        {
                            var pathName = key?.ToString();
                            if (!string.IsNullOrEmpty(pathName))
                            {
                                log.Information($"[AutoDutyPath]   [{pathIndex}] {pathName}");
                                
                                // Check if this is our target path
                                if (pathName.Contains("Praetorium") && pathName.Contains("W2W") && pathName.Contains("phecda"))
                                {
                                    log.Information($"[AutoDutyPath] *** FOUND TARGET PATH! ***");
                                    log.Information($"[AutoDutyPath] Target: {pathName}");
                                    log.Information($"[AutoDutyPath] Index: {pathIndex}");
                                    
                                    // Try to get the JobWithRole value
                                    try
                                    {
                                        var innerIndexerProp = innerDictType.GetProperty("Item");
                                        if (innerIndexerProp != null)
                                        {
                                            var jobWithRole = innerIndexerProp.GetValue(praetoriumPaths, new object[] { pathName });
                                            log.Information($"[AutoDutyPath] JobWithRole: {jobWithRole}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        log.Warning($"[AutoDutyPath] Could not get JobWithRole for target path: {ex.Message}");
                                    }
                                }
                                pathIndex++;
                            }
                        }
                    }
                }
            }
            else
            {
                log.Warning($"[AutoDutyPath] No paths found for territory {targetTerritory}");
            }

            log.Information($"[AutoDutyPath] === PathSelectionsByPath Complete ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogPathSelections failed: {ex.Message}");
        }
    }

    // --- Tuple Logging Helper ---

    /// <summary>
    /// Log the contents of each tuple in the actionsList for path research.
    /// </summary>
    public void LogActionsListTuples(object? actionsList)
    {
        try
        {
            if (actionsList == null)
            {
                log.Warning("[AutoDutyPath] actionsList is null");
                return;
            }

            var listType = actionsList.GetType();
            log.Information($"[AutoDutyPath] === actionsList Tuples ({listType.FullName}) ===");

            // Get the Count property
            var countProp = listType.GetProperty("Count");
            if (countProp == null)
            {
                log.Warning("[AutoDutyPath] Could not get Count property from actionsList");
                return;
            }

            var count = (int)countProp.GetValue(actionsList)!;
            log.Information($"[AutoDutyPath] Total tuples: {count}");

            // Get the indexer property to access individual tuples
            var indexerProp = listType.GetProperty("Item");
            if (indexerProp == null)
            {
                log.Warning("[AutoDutyPath] Could not get indexer property from actionsList");
                return;
            }

            // Iterate through each tuple
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var tuple = indexerProp.GetValue(actionsList, new object[] { i });
                    if (tuple != null)
                    {
                        var tupleType = tuple.GetType();
                        log.Information($"[AutoDutyPath] Tuple[{i}]: {tupleType.FullName} = {tuple}");
                        
                        // Try to access tuple fields
                        var fields = tupleType.GetFields();
                        for (int j = 0; j < fields.Length; j++)
                        {
                            var field = fields[j];
                            var value = field.GetValue(tuple);
                            log.Information($"[AutoDutyPath]   Field[{j}] {field.Name}: {value}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[AutoDutyPath] Error accessing tuple[{i}]: {ex.Message}");
                }
            }

            log.Information($"[AutoDutyPath] === actionsList Tuples Complete ===");
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] LogActionsListTuples failed: {ex.Message}");
        }
    }

    // --- Reflection Helpers (from Reflections.md) ---

    public static object? GetMemberValue(Type type, object? instance, string memberName)
    {
        var field = type.GetField(memberName, AllFlags);
        if (field != null)
        {
            return field.GetValue(field.IsStatic ? null : instance);
        }

        var property = type.GetProperty(memberName, AllFlags);
        if (property != null)
        {
            var getter = property.GetGetMethod(true);
            if (getter != null)
            {
                return property.GetValue(getter.IsStatic ? null : instance);
            }
        }

        return null;
    }

    private static bool SetMemberValue(Type type, object? instance, string memberName, object value)
    {
        var field = type.GetField(memberName, AllFlags);
        if (field != null)
        {
            field.SetValue(field.IsStatic ? null : instance, value);
            return true;
        }

        var property = type.GetProperty(memberName, AllFlags);
        if (property != null)
        {
            var setter = property.GetSetMethod(true);
            if (setter != null)
            {
                property.SetValue(setter.IsStatic ? null : instance, value);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Try to set a mode-like value that might be an enum, string, or int.
    /// AutoDuty uses enums for mode selections, so we need to resolve the enum value.
    /// </summary>
    private bool TrySetModeValue(object target, string memberName, string enumValueName)
    {
        var targetType = target.GetType();

        // Try field first
        var field = targetType.GetField(memberName, AllFlags);
        if (field != null)
        {
            return TrySetEnumOrString(field.FieldType, enumValueName, val =>
                field.SetValue(field.IsStatic ? null : target, val));
        }

        // Try property
        var prop = targetType.GetProperty(memberName, AllFlags);
        if (prop?.GetSetMethod(true) != null)
        {
            return TrySetEnumOrString(prop.PropertyType, enumValueName, val =>
                prop.SetValue(prop.GetSetMethod(true)!.IsStatic ? null : target, val));
        }

        return false;
    }

    private bool TrySetEnumOrString(Type memberType, string valueName, Action<object> setter)
    {
        try
        {
            if (memberType.IsEnum)
            {
                // Parse enum value by name
                var enumVal = Enum.Parse(memberType, valueName, ignoreCase: true);
                setter(enumVal);
                log.Debug($"[AutoDutyPath] Set enum {memberType.Name}.{valueName} successfully");
                return true;
            }
            else if (memberType == typeof(string))
            {
                setter(valueName);
                return true;
            }
            else if (memberType == typeof(int))
            {
                // Try to parse as int (some modes might be ints)
                if (int.TryParse(valueName, out var intVal))
                {
                    setter(intVal);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[AutoDutyPath] TrySetEnumOrString failed for {memberType.Name}.{valueName}: {ex.Message}");
        }

        return false;
    }

    public Task<bool> EnsurePathExists()
    {
        try
        {
            var autoDutyPathsFolder = GetAutoDutyPathsFolder();
            if (string.IsNullOrEmpty(autoDutyPathsFolder))
            {
                log.Warning("[AutoDutyPath] Could not determine AutoDuty paths folder");
                return Task.FromResult(false);
            }
            log.Information($"[AutoDutyPath] Using AutoDuty paths folder: {autoDutyPathsFolder}");

            var bundledPathOptions = GetPraetoriumPathOptions();
            var bundledPathsFolder = GetBundledPathsFolder();
            if (string.IsNullOrEmpty(bundledPathsFolder) || !Directory.Exists(bundledPathsFolder))
            {
                log.Warning($"[AutoDutyPath] Bundled path folder is missing. Checked: {string.Join(" | ", GetBundledPathsFolderCandidates())}");
                return Task.FromResult(false);
            }
            log.Information($"[AutoDutyPath] Using bundled Praetorium source folder: {bundledPathsFolder}");

            Directory.CreateDirectory(autoDutyPathsFolder);
            var installedCount = 0;

            foreach (var option in bundledPathOptions)
            {
                var sourcePath = Path.Combine(bundledPathsFolder, option.FileName);
                if (!File.Exists(sourcePath))
                {
                    log.Warning($"[AutoDutyPath] Bundled path missing from plugin data folder: {sourcePath}");
                    continue;
                }

                var targetPath = Path.Combine(autoDutyPathsFolder, option.FileName);
                var shouldCopy = !File.Exists(targetPath) ||
                                 new FileInfo(targetPath).Length != new FileInfo(sourcePath).Length ||
                                 File.GetLastWriteTimeUtc(targetPath) != File.GetLastWriteTimeUtc(sourcePath);

                if (shouldCopy)
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
                    log.Information($"[AutoDutyPath] Installed bundled path: {option.FileName}");
                }
                else
                {
                    log.Debug($"[AutoDutyPath] Bundled path already current: {option.FileName}");
                }

                installedCount++;
            }

            if (installedCount == 0)
            {
                log.Warning("[AutoDutyPath] No bundled Praetorium paths were installed");
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] EnsurePathExists failed: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public bool PathExists()
        => PathExists(PathFileName);

    public bool PathExists(string? configuredPathFileName)
    {
        try
        {
            var autoDutyPathsFolder = GetAutoDutyPathsFolder();
            if (string.IsNullOrEmpty(autoDutyPathsFolder)) return false;

            var targetPath = Path.Combine(autoDutyPathsFolder, ResolvePraetoriumPathFileName(configuredPathFileName));
            return File.Exists(targetPath);
        }
        catch
        {
            return false;
        }
    }

    // --- File-based path management ---

    private string? GetBundledPathsFolder()
    {
        try
        {
            return GetBundledPathsFolderCandidates().FirstOrDefault(Directory.Exists)
                ?? GetBundledPathsFolderCandidates().FirstOrDefault();
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] GetBundledPathsFolder failed: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<string> GetBundledPathsFolderCandidates()
    {
        var candidates = new List<string>();

        void AddCandidate(string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return;

            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, BundledPathsRelativeFolder));
            if (!candidates.Any(existing => existing.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(candidate);
        }

        AddCandidate(PluginInterface.AssemblyLocation.Directory?.FullName);
        AddCandidate(Path.GetDirectoryName(typeof(AutoDutyPathService).Assembly.Location));
        AddCandidate(AppContext.BaseDirectory);

        return candidates;
    }

    private IReadOnlyList<PraetoriumPathOption> LoadBundledPraetoriumPaths()
    {
        try
        {
            var bundledPathsFolder = GetBundledPathsFolder();
            if (!string.IsNullOrWhiteSpace(bundledPathsFolder) && Directory.Exists(bundledPathsFolder))
            {
                var options = Directory.EnumerateFiles(bundledPathsFolder, "(1044)*.json", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .Select(fileName => new PraetoriumPathOption(fileName!, BuildPraetoriumPathDisplayName(fileName!)))
                    .OrderBy(option => option.FileName.Equals(DefaultPraetoriumPathFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (options.Count > 0)
                {
                    log.Information($"[AutoDutyPath] Loaded {options.Count} bundled Praetorium path option(s) from {bundledPathsFolder}");
                    return options;
                }

                log.Warning($"[AutoDutyPath] No bundled Praetorium JSON files were found in {bundledPathsFolder}");
            }
            else
            {
                log.Warning($"[AutoDutyPath] Bundled Praetorium path folder could not be resolved. Checked: {string.Join(" | ", GetBundledPathsFolderCandidates())}");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] Failed to load bundled Praetorium paths: {ex.Message}");
        }

        return
        [
            new(DefaultPraetoriumPathFileName, BuildPraetoriumPathDisplayName(DefaultPraetoriumPathFileName)),
        ];
    }

    private static string BuildPraetoriumPathDisplayName(string fileName)
    {
        var match = Regex.Match(
            fileName,
            @"^\(1044\)\s+The Praetorium\s+-\s+(?<variant>.+?)\s+(?<date>\d{8})\s+(?<author>.+)\.json$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        if (!match.Success)
            return Path.GetFileNameWithoutExtension(fileName) ?? fileName;

        var variant = match.Groups["variant"].Value.Trim();
        var author = match.Groups["author"].Value.Trim();
        var rawDate = match.Groups["date"].Value;
        var formattedDate = rawDate.Length == 8
            ? $"{rawDate[..4]}-{rawDate[4..6]}-{rawDate[6..8]}"
            : rawDate;

        return $"{author} {variant} ({formattedDate})";
    }

    private string? GetAutoDutyPathsFolder(object? autoDutyPlugin = null)
    {
        try
        {
            var candidates = GetAutoDutyPathsFolderCandidates(autoDutyPlugin);
            return candidates.FirstOrDefault(Directory.Exists)
                ?? candidates.FirstOrDefault();
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] GetAutoDutyPathsFolder failed: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<string> GetAutoDutyPathsFolderCandidates(object? autoDutyPlugin = null)
    {
        var candidates = new List<string>();

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var normalized = Path.GetFullPath(candidate);
            if (!candidates.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(normalized);
        }

        autoDutyPlugin ??= FindDalamudPluginInstance("AutoDuty");
        if (autoDutyPlugin != null)
        {
            var pluginInstance = GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Plugin")
                ?? GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "P")
                ?? GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Instance")
                ?? autoDutyPlugin;

            AddCandidate(GetPathDirectoryMemberValue(pluginInstance));

            var config = GetMemberValue(pluginInstance.GetType(), pluginInstance, "C")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "Config")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "Configuration")
                ?? GetMemberValue(pluginInstance.GetType(), pluginInstance, "config");
            if (config != null)
                AddCandidate(GetPathDirectoryMemberValue(config));
        }

        var ourConfigDir = PluginInterface.ConfigDirectory.FullName;
        AddCandidate(Path.Combine(ourConfigDir, "..", "AutoDuty", "paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "AutoDuty", "Paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "AutoDuty", "paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "AutoDuty", "Paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "installed", "AutoDuty", "paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "installed", "AutoDuty", "Paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "..", "installed", "AutoDuty", "paths"));
        AddCandidate(Path.Combine(ourConfigDir, "..", "..", "..", "installed", "AutoDuty", "Paths"));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AddCandidate(Path.Combine(appData, "XIVLauncher", "pluginConfigs", "AutoDuty", "paths"));
        AddCandidate(Path.Combine(appData, "XIVLauncher", "pluginConfigs", "AutoDuty", "Paths"));

        return candidates;
    }

    private string? GetPathDirectoryMemberValue(object? instance)
    {
        if (instance == null)
            return null;

        var type = instance.GetType();
        foreach (var memberName in new[] { "PathsDirectory", "PathsFolder", "PathDirectory" })
        {
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                var resolved = ResolveDirectoryPath(property.GetValue(instance));
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var resolved = ResolveDirectoryPath(field.GetValue(instance));
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }
        }

        return null;
    }

    private static string? ResolveDirectoryPath(object? value)
    {
        return value switch
        {
            string stringPath when !string.IsNullOrWhiteSpace(stringPath) => stringPath,
            DirectoryInfo directoryInfo => directoryInfo.FullName,
            FileInfo fileInfo => fileInfo.Directory?.FullName,
            _ => null,
        };
    }
}
