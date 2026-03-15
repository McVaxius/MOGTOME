using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace MOGTOME.Services;

public class AutoDutyPathService
{
    private readonly IPluginLog log;

    private const string PathFileName = "(1044) The Praetorium - W2W 20250716 phecda.json";
    private const string PathUrl = "https://raw.githubusercontent.com/McVaxius/dhogsbreakfeast/refs/heads/main/Dungeons%20and%20Multiboxing/G.O.O.N/(1044)%20The%20Praetorium%20-%20W2W%2020250716%20phecda.json";
    private static readonly HttpClient httpClient = new();

    public AutoDutyPathService(IPluginLog log)
    {
        this.log = log;
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
