using System;
using System.Collections.Generic;

namespace MOGTOME.Models;

/// <summary>
/// Per-character configuration within an account
/// </summary>
public class CharacterConfig
{
    public string CharacterName { get; set; } = "";
    public string WorldName { get; set; } = "";
    public ulong ContentId { get; set; } = 0;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    
    // Character-specific settings can go here
    // For now, most settings are account-wide
}

/// <summary>
/// Per-account configuration containing all settings and character configs
/// </summary>
public class AccountConfig
{
    public string AccountId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    
    // Main configuration (migrated from original Configuration.cs)
    public Configuration Settings { get; set; } = new Configuration();
    
    // Character-specific configurations
    public Dictionary<string, CharacterConfig> Characters { get; set; } = new Dictionary<string, CharacterConfig>();
    
    // Currently selected character
    public string SelectedCharacterKey { get; set; } = "";
    
    /// <summary>
    /// Get the current character configuration
    /// </summary>
    public CharacterConfig GetCurrentCharacter()
    {
        if (string.IsNullOrEmpty(SelectedCharacterKey) || !Characters.ContainsKey(SelectedCharacterKey))
        {
            return new CharacterConfig();
        }
        
        return Characters[SelectedCharacterKey];
    }
    
    /// <summary>
    /// Add or update a character configuration
    /// </summary>
    public void SetCharacter(CharacterConfig character)
    {
        var key = $"{character.WorldName}_{character.CharacterName}_{character.ContentId}";
        character.LastUsed = DateTime.UtcNow;
        Characters[key] = character;
        SelectedCharacterKey = key;
        LastUsed = DateTime.UtcNow;
    }
}
