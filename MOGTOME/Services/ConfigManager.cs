using System;
using System.Collections.Generic;
using System.IO;
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
            
            SaveAccount(accountId);
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Error in EnsureAccountSelected: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Save the current account configuration
    /// </summary>
    public void SaveCurrentAccount()
    {
        if (!string.IsNullOrEmpty(CurrentAccountId) && accounts.ContainsKey(CurrentAccountId))
        {
            SaveAccount(CurrentAccountId);
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
        var account = new AccountConfig
        {
            AccountId = contentId.ToString(),
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow,
            Settings = new Configuration()
        };
        
        log.Information($"[ConfigManager] Created account for content ID: {contentId}");
        return account;
    }
    
    /// <summary>
    /// Load all account configurations from disk
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
            
            var files = Directory.GetFiles(configPath, "*_MOGTOME.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var account = JsonSerializer.Deserialize<AccountConfig>(json, GetJsonOptions());
                    if (account != null)
                    {
                        accounts[account.AccountId] = account;
                        log.Debug($"[ConfigManager] Loaded account: {account.AccountId}");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[ConfigManager] Failed to load account from {file}: {ex.Message}");
                }
            }
            
            log.Information($"[ConfigManager] Loaded {accounts.Count} account configurations");
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Error loading accounts: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Save a specific account configuration to disk
    /// </summary>
    private void SaveAccount(string accountId)
    {
        try
        {
            if (!accounts.ContainsKey(accountId))
                return;
            
            var account = accounts[accountId];
            var json = JsonSerializer.Serialize(account, GetJsonOptions());
            var fileName = $"{accountId}_MOGTOME.json";
            var filePath = Path.Combine(GetConfigFolderPath(), fileName);
            
            log.Information($"[ConfigManager] Saving account {accountId} to: {filePath}");
            File.WriteAllText(filePath, json);
            log.Debug($"[ConfigManager] Saved account: {accountId}");
        }
        catch (Exception ex)
        {
            log.Error($"[ConfigManager] Error saving account {accountId}: {ex.Message}");
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
