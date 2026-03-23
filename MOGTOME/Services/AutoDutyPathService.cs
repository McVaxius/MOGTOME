using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MOGTOME.Services;

public class AutoDutyPathService
{
    private readonly IPluginLog log;

    private const string PathFileName = "(1044) The Praetorium - W2W 20250716 phecda.json";
    private const string PathUrl = "https://raw.githubusercontent.com/McVaxius/dhogsbreakfeast/refs/heads/main/Dungeons%20and%20Multiboxing/G.O.O.N/(1044)%20The%20Praetorium%20-%20W2W%2020250716%20phecda.json";
    private static readonly HttpClient httpClient = new();

    // Reflection constants
    private static readonly BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // Target values for path forcing
    private const string TargetDutyName = "The Praetorium";
    private const int TargetTerritoryType = 1044;
    private const string TargetPathName = "(1044) The Praetorium - W2W 20250716 phecda";

    // Last result for UI display
    public string LastForceResult { get; private set; } = "Not attempted";

    public AutoDutyPathService(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Force AutoDuty to select the phecda Praetorium path via reflection.
    /// Steps: Mode=Looping, DutyMode=Regular, Duty=Praetorium (1044), Path=phecda W2W
    /// Called before anything else on Start, for all party members, while not in duty.
    /// </summary>
    public bool ForcePathSelection()
    {
        try
        {
            log.Information("[AutoDutyPath] === FORCE PATH SELECTION START ===");

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
                var modeSet = TrySetModeValue(config, "mode", "Looping")
                    || TrySetModeValue(config, "Mode", "Looping")
                    || TrySetModeValue(config, "SelectedMode", "Looping")
                    || TrySetModeValue(config, "CurrentMode", "Looping");
                log.Information($"[AutoDutyPath] Set Mode=Looping: {modeSet}");

                // Step 3b: Set DutyMode to Regular
                var dutyModeSet = TrySetModeValue(config, "dutyMode", "Regular")
                    || TrySetModeValue(config, "DutyMode", "Regular")
                    || TrySetModeValue(config, "SelectedDutyMode", "Regular");
                log.Information($"[AutoDutyPath] Set DutyMode=Regular: {dutyModeSet}");
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

            // Try setting currentPath by finding the target path index dynamically
            var pathIndex = FindPathIndexByName(pluginInstance, TargetPathName);
            var pathSet = false;
            if (pathIndex >= 0)
            {
                pathSet = SetMemberValue(instanceType, pluginInstance, "currentPath", pathIndex);
                log.Information($"[AutoDutyPath] Set currentPath={pathIndex} for '{TargetPathName}': {pathSet}");
                if (pathSet)
                {
                    LastForceResult = $"OK: Territory={TargetTerritoryType}, Path={pathIndex} ({TargetPathName})";
                }
            }
            else
            {
                log.Warning($"[AutoDutyPath] Could not find path index for '{TargetPathName}'");
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
                                    
                                    // Set the W2W path to ALL jobs
                                    innerDict[PathFileName] = jobWithRoleAll;
                                    log.Information($"[AutoDutyPath] Set PathSelectionsByPath[{TargetTerritoryType}][{PathFileName}] = All jobs");
                                    
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
    /// Find the path index by searching through PathSelectionsByPath for the target path name.
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

    public async Task<bool> EnsurePathExists()
    {
        try
        {
            var autoDutyPathsFolder = GetAutoDutyPathsFolder();
            if (string.IsNullOrEmpty(autoDutyPathsFolder))
            {
                log.Warning("[AutoDutyPath] Could not determine AutoDuty paths folder");
                return false;
            }

            
            var targetPath = Path.Combine(autoDutyPathsFolder, PathFileName);

            if (File.Exists(targetPath))
            {
                log.Information($"[AutoDutyPath] Path file already exists: {targetPath}");
                return true;
            }

            // Ensure directory exists
            Directory.CreateDirectory(autoDutyPathsFolder);

            // Download from GitHub
            log.Information($"[AutoDutyPath] Downloading path file from: {PathUrl}");
            var response = await httpClient.GetAsync(PathUrl);
            if (!response.IsSuccessStatusCode)
            {
                log.Warning($"[AutoDutyPath] Failed to download path file: {response.StatusCode}");
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            await File.WriteAllTextAsync(targetPath, content);
            log.Information($"[AutoDutyPath] Downloaded path file to: {targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] EnsurePathExists failed: {ex.Message}");
            return false;
        }
    }

    public bool PathExists()
    {
        try
        {
            var autoDutyPathsFolder = GetAutoDutyPathsFolder();
            if (string.IsNullOrEmpty(autoDutyPathsFolder)) return false;

            var targetPath = Path.Combine(autoDutyPathsFolder, PathFileName);
            return File.Exists(targetPath);
        }
        catch
        {
            return false;
        }
    }

    // --- File-based path management ---

    private string? GetAutoDutyPathsFolder()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var autoDutyPaths = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "AutoDuty", "paths");

            // Also check alternate location
            if (!Directory.Exists(autoDutyPaths))
            {
                var altPath = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "AutoDuty", "Paths");
                if (Directory.Exists(altPath))
                    return altPath;
            }

            return autoDutyPaths;
        }
        catch (Exception ex)
        {
            log.Error($"[AutoDutyPath] GetAutoDutyPathsFolder failed: {ex.Message}");
            return null;
        }
    }
}
