using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MOGTOME.Models;

namespace MOGTOME.Services;

/// <summary>
/// Manages per-account configuration loading/saving
/// Based on FrenRider/HFH ConfigManager pattern
/// </summary>
public class ConfigManager
{
    private readonly IPluginLog log;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly IDalamudPluginInterface pluginInterface;
    
    private Dictionary<string, AccountConfig> accounts = new Dictionary<string, AccountConfig>();
    private string currentAccountId = "";
    private const string ConfigFolder = "MOGTOME";
    
    public string CurrentAccountId 
    { 
        get => currentAccountId;
        private set => currentAccountId = value;
    }
    
    public ConfigManager(IPluginLog log, IPlayerState playerState, IClientState clientState, IDalamudPluginInterface pluginInterface)
    {
        this.log = log;
        this.playerState = playerState;
        this.clientState = clientState;
        this.pluginInterface = pluginInterface;
        
        EnsureConfigFolderExists();
        
        // Check for migration first
        if (HasSharedConfigToMigrate())
        {
            MigrateSharedConfig();
        }
        
        LoadAllAccounts();
    }
    
    /// <summary>
    /// Get the current account configuration
    /// </summary>
    public AccountConfig GetCurrentAccount()
    {
        if (string.IsNullOrEmpty(CurrentAccountId) || !accounts.ContainsKey(CurrentAccountId))
        {
            return CreateDefaultAccount();
        }
        
        return accounts[CurrentAccountId];
    }
    
    /// <summary>
    /// Get the current active configuration (from current account)
    /// </summary>
    public Configuration GetActiveConfig()
    {
        return GetCurrentAccount().Settings;
    }
    
    /// <summary>
    /// Get the current character configuration
    /// </summary>
    public CharacterConfig GetCurrentCharacterConfig()
    {
        return GetCurrentAccount().GetCurrentCharacter();
    }
    
    /// <summary>
    /// Get all account IDs
    /// </summary>
    public List<string> GetAllAccountIds()
    {
        return accounts.Keys.ToList();
    }
    
    /// <summary>
    /// Ensure we have an account selected and current character tracked
    /// </summary>
    public void EnsureAccountSelected()
    {
        try
        {
            var contentId = playerState.ContentId;
            if (contentId == 0)
            {
                log.Warning("[ConfigManager] No local player, cannot select account");
                return;
            }
            
            var accountId = contentId.ToString();
            CurrentAccountId = accountId;
            
            // Create account if it doesn't exist
            if (!accounts.ContainsKey(accountId))
            {
                accounts[accountId] = CreateAccountForContentId(contentId);
            }
            
            var account = accounts[accountId];
            
            // Try to get current character info
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer != null)
            {
                var characterName = localPlayer.Name.ToString();
                var worldName = "Unknown";
                
                var character = new CharacterConfig
                {
                    CharacterName = characterName,
                    WorldName = worldName,
                    ContentId = contentId,
                    LastUsed = DateTime.UtcNow
                };
                
                account.SetCharacter(character);
                account.LastUsed = DateTime.UtcNow;
                
                log.Information($"[ConfigManager] Selected account: {accountId}, character: {characterName}@{worldName}");
            }
            else
            {
                log.Warning("[ConfigManager] Local player not available, using default character config");
            }
            
            SaveCurrentAccount();
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Error in EnsureAccountSelected: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Save the current account configuration to per-account JSON file
    /// </summary>
    public void SaveCurrentAccount()
    {
        try
        {
            var accountId = CurrentAccountId;
            if (string.IsNullOrEmpty(accountId))
            {
                log.Warning("[ConfigManager] Cannot save - no account selected");
                return;
            }

            var config = GetActiveConfig();
            var fileName = $"{accountId}_MOGTOME.json";
            var filePath = Path.Combine(GetConfigFolderPath(), fileName);
            
            config.SaveToFile(filePath);
            log.Debug($"[ConfigManager] Saved account {accountId} to: {filePath}");
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Failed to save current account: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Load configuration for a specific account from JSON file
    /// </summary>
    private Configuration LoadAccountConfig(string accountId)
    {
        try
        {
            var fileName = $"{accountId}_MOGTOME.json";
            var filePath = Path.Combine(GetConfigFolderPath(), fileName);
            
            var config = Configuration.LoadFromFile(filePath);
            log.Debug($"[ConfigManager] Loaded account {accountId} from: {filePath}");
            return config;
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Failed to load account {accountId}: {ex.Message}");
            return new Configuration();
        }
    }

    /// <summary>
    /// Check if shared config exists and needs migration
    /// </summary>
    private bool HasSharedConfigToMigrate()
    {
        var sharedConfigPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "MOGTOME.json");
        return File.Exists(sharedConfigPath);
    }

    /// <summary>
    /// Migrate shared config to per-account format
    /// </summary>
    private void MigrateSharedConfig()
    {
        try
        {
            var sharedConfigPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "MOGTOME.json");
            
            if (!File.Exists(sharedConfigPath))
            {
                log.Debug("[ConfigManager] No shared config to migrate");
                return;
            }

            log.Information("[ConfigManager] Starting migration from shared config to per-account format");

            // Load existing shared config
            var sharedConfig = Configuration.LoadFromFile(sharedConfigPath);
            
            // Save to current account's file
            var accountId = CurrentAccountId;
            if (!string.IsNullOrEmpty(accountId))
            {
                var fileName = $"{accountId}_MOGTOME.json";
                var filePath = Path.Combine(GetConfigFolderPath(), fileName);
                sharedConfig.SaveToFile(filePath);
                
                // Backup and remove old shared file
                var backupPath = sharedConfigPath + $".backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Move(sharedConfigPath, backupPath);
                
                log.Information($"[ConfigManager] Migrated shared config to account {accountId}");
                log.Information($"[ConfigManager] Shared config backed up to: {backupPath}");
                
                // Update current account to use migrated config
                if (accounts.ContainsKey(accountId))
                {
                    accounts[accountId].Settings = sharedConfig;
                }
            }
            else
            {
                log.Warning("[ConfigManager] Cannot migrate - no current account ID available");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Migration failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Create a default account configuration
    /// </summary>
    private AccountConfig CreateDefaultAccount()
    {
        var account = new AccountConfig
        {
            AccountId = "default",
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow,
            Settings = new Configuration()
        };
        
        accounts["default"] = account;
        CurrentAccountId = "default";
        
        log.Information("[ConfigManager] Created default account configuration");
        return account;
    }
    
    /// <summary>
    /// Create an account for a specific content ID
    /// </summary>
    private AccountConfig CreateAccountForContentId(ulong contentId)
    {
        var accountId = contentId.ToString();
        
        // Try to load existing config first
        var existingConfig = LoadAccountConfig(accountId);
        
        var account = new AccountConfig
        {
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow,
            Settings = existingConfig // Use loaded config or new if none exists
        };
        
        log.Information($"[ConfigManager] Created account for content ID: {contentId} (loaded existing: {existingConfig != null})");
        return account;
    }
    
    /// <summary>
    /// Load all account configurations from per-account JSON files
    /// </summary>
    private void LoadAllAccounts()
    {
        try
        {
            var configPath = GetConfigFolderPath();
            if (!Directory.Exists(configPath))
            {
                log.Information("[ConfigManager] Config folder does not exist, starting fresh");
                return;
            }
            
            // Look for per-account configuration files
            var files = Directory.GetFiles(configPath, "*_MOGTOME.json");
            int loadedCount = 0;
            
            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var accountId = fileName.Replace("_MOGTOME", "");
                    
                    if (ulong.TryParse(accountId, out var contentId))
                    {
                        var config = Configuration.LoadFromFile(file);
                        var account = new AccountConfig
                        {
                            AccountId = accountId,
                            CreatedAt = DateTime.UtcNow, // We don't track creation date in JSON
                            LastUsed = DateTime.UtcNow,
                            Settings = config
                        };
                        
                        accounts[accountId] = account;
                        loadedCount++;
                        log.Debug($"[ConfigManager] Loaded account: {accountId}");
                    }
                    else
                    {
                        log.Warning($"[ConfigManager] Invalid account ID in filename: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[ConfigManager] Failed to load account from {file}: {ex.Message}");
                }
            }
            
            log.Information($"[ConfigManager] Loaded {loadedCount} account configurations from per-account JSON files");
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Error loading accounts: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Get the configuration folder path
    /// </summary>
    private string GetConfigFolderPath()
    {
        var path = Path.Combine(pluginInterface.ConfigDirectory.FullName, ConfigFolder);
        log.Information($"[ConfigManager] Config folder path: {path}");
        return path;
    }
    
    /// <summary>
    /// Ensure the configuration folder exists
    /// </summary>
    private void EnsureConfigFolderExists()
    {
        var configPath = GetConfigFolderPath();
        if (!Directory.Exists(configPath))
        {
            Directory.CreateDirectory(configPath);
            log.Information($"[ConfigManager] Created config folder: {configPath}");
        }
    }
    
    /// <summary>
    /// Get JSON serializer options
    /// </summary>
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
    }
}
