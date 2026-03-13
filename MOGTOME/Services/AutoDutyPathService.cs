using System;
using System.IO;
using Dalamud.Plugin.Services;

namespace MOGTOME.Services;

public class AutoDutyPathService
{
    private readonly IPluginLog log;

    private const string PathFileName = "(1044) The Praetorium - W2W 20250716 phecda.json";
    private const string SourcePath = @"D:\temp\dhogsbreakfeast\Dungeons and Multiboxing\G.O.O.N\(1044) The Praetorium - W2W 20250716 phecda.json";

    public AutoDutyPathService(IPluginLog log)
    {
        this.log = log;
    }

    public bool EnsurePathExists()
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

            if (!File.Exists(SourcePath))
            {
                log.Warning($"[AutoDutyPath] Source path file not found: {SourcePath}");
                return false;
            }

            // Ensure directory exists
            Directory.CreateDirectory(autoDutyPathsFolder);

            // Copy the file
            File.Copy(SourcePath, targetPath);
            log.Information($"[AutoDutyPath] Copied path file to: {targetPath}");
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
