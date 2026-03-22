using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Data.Sqlite;
using MOGTOME.Models;
using MOGTOME.Repositories;

namespace MOGTOME.Services;

/// <summary>
/// Service for managing per-account SQLite database storage for RunHistory
/// Each account gets its own SQLite database to prevent conflicts and handle large datasets
/// Uses proper SQLite with indexing for performance with thousands of records
/// </summary>
public class DatabaseService
{
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ConfigManager configManager;
    private readonly string databaseFolder;
    private readonly IRunRecordRepository repository;
    private readonly object initializationLock = new object();
    private volatile bool isInitialized = false;
    
    // Database version system
    private const int DATABASE_VERSION = 1;
    private const string VERSION_KEY = "DatabaseVersion";

    public DatabaseService(IPluginLog log, IDalamudPluginInterface pluginInterface, ConfigManager configManager)
    {
        this.log = log;
        this.pluginInterface = pluginInterface;
        this.configManager = configManager;
        this.databaseFolder = Path.Combine(pluginInterface.ConfigDirectory.FullName, "MOGTOME", "RunHistory");
        
        // Only create directory - defer database operations
        Directory.CreateDirectory(databaseFolder);
        
        // Initialize repository
        this.repository = new SqliteRunRecordRepository(log, databaseFolder);
        
        log.Information($"[DatabaseService] Initialized with SQLite database folder: {databaseFolder}");
    }

    /// <summary>
    /// Check database version and reset if needed
    /// </summary>
    private void CheckAndResetDatabase(string accountId)
    {
        var dbPath = GetDatabasePath(accountId);
        
        if (File.Exists(dbPath))
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};");
                connection.Open();
                
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                var version = Convert.ToInt32(cmd.ExecuteScalar());
                
                if (version < DATABASE_VERSION)
                {
                    log.Information($"[DatabaseService] Resetting database for account {accountId} (version {version} -> {DATABASE_VERSION})");
                    connection.Close();
                    File.Delete(dbPath);
                }
                else
                {
                    log.Debug($"[DatabaseService] Database version {version} is current for account {accountId}");
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[DatabaseService] Error checking database version for {accountId}, resetting");
                try
                {
                    File.Delete(dbPath);
                    log.Information($"[DatabaseService] Successfully reset database for account {accountId}");
                }
                catch (Exception deleteEx)
                {
                    log.Error(deleteEx, $"[DatabaseService] Failed to delete database file for account {accountId}");
                }
            }
        }
        else
        {
            log.Debug($"[DatabaseService] No existing database for account {accountId}, will create new");
        }
    }

    /// <summary>
    /// Initialize database service (call from main thread)
    /// </summary>
    public void Initialize()
    {
        if (isInitialized) return;
        
        lock (initializationLock)
        {
            if (isInitialized) return;
            
            try
            {
                // Reset databases with old schema before initialization
                var accounts = configManager.GetAllAccountIds();
                foreach (var accountId in accounts)
                {
                    CheckAndResetDatabase(accountId);
                }
                
                // Perform any heavy initialization here
                log.Information("[DatabaseService] Database service initialization completed");
                isInitialized = true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "[DatabaseService] Failed to initialize database service");
                throw;
            }
        }
    }

    /// <summary>
    /// Get the SQLite database file path for a specific account
    /// </summary>
    private string GetDatabasePath(string accountId)
    {
        return Path.Combine(databaseFolder, $"{accountId}.sqlite");
    }

    /// <summary>
    /// Load run records from the account-specific SQLite database
    /// </summary>
    public List<RunRecord> LoadRunRecords(string accountId)
    {
        // Ensure service is initialized
        if (!isInitialized)
        {
            throw new InvalidOperationException("DatabaseService not initialized. Call Initialize() first.");
        }

        try
        {
            return repository.LoadRunRecords(accountId);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Failed to load run records for account {accountId}");
            return new List<RunRecord>();
        }
    }

    /// <summary>
    /// Fallback in-memory storage for critical operations when database fails
    /// </summary>
    private readonly Dictionary<string, List<RunRecord>> fallbackStorage = new();
    
    /// <summary>
    /// Store run record with graceful degradation
    /// </summary>
    public void AddRunRecordWithFallback(string accountId, RunRecord record)
    {
        try
        {
            AddRunRecord(accountId, record);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Database operation failed, using fallback storage for account {accountId}");
            
            // Store in fallback storage
            lock (fallbackStorage)
            {
                if (!fallbackStorage.ContainsKey(accountId))
                {
                    fallbackStorage[accountId] = new List<RunRecord>();
                }
                fallbackStorage[accountId].Add(record);
            }
            
            log.Information($"[DatabaseService] Stored run record in fallback storage for account {accountId}");
        }
    }
    
    /// <summary>
    /// Load run records with fallback support
    /// </summary>
    public List<RunRecord> LoadRunRecordsWithFallback(string accountId)
    {
        try
        {
            var records = LoadRunRecords(accountId);
            
            // Add any records from fallback storage
            lock (fallbackStorage)
            {
                if (fallbackStorage.TryGetValue(accountId, out var fallbackRecords))
                {
                    records.AddRange(fallbackRecords);
                    log.Information($"[DatabaseService] Loaded {fallbackRecords.Count} records from fallback storage for account {accountId}");
                }
            }
            
            return records;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Database load failed, using fallback storage for account {accountId}");
            
            // Return only fallback storage
            lock (fallbackStorage)
            {
                return fallbackStorage.TryGetValue(accountId, out var fallbackRecords) 
                    ? fallbackRecords.ToList() 
                    : new List<RunRecord>();
            }
        }
    }
    public void AddRunRecord(string accountId, RunRecord record)
    {
        // Ensure service is initialized
        if (!isInitialized)
        {
            throw new InvalidOperationException("DatabaseService not initialized. Call Initialize() first.");
        }

        try
        {
            repository.AddRunRecord(accountId, record);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Failed to add run record for account {accountId}");
            throw;
        }
    }

    /// <summary>
    /// Clear all run records for an account
    /// </summary>
    public void ClearRunRecords(string accountId)
    {
        // Ensure service is initialized
        if (!isInitialized)
        {
            throw new InvalidOperationException("DatabaseService not initialized. Call Initialize() first.");
        }

        try
        {
            repository.ClearRunRecords(accountId);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Failed to clear run records for account {accountId}");
            throw;
        }
    }

    /// <summary>
    /// Migrate existing JSON data to SQLite
    /// </summary>
    public void MigrateFromJson(string accountId)
    {
        // Ensure service is initialized before proceeding
        if (!isInitialized)
        {
            log.Warning($"[DatabaseService] Migration called before initialization for account {accountId}");
            return; // Skip migration, will be called after initialization
        }

        try
        {
            var jsonPath = Path.Combine(databaseFolder, $"{accountId}_RunHistory.json");
            
            if (!File.Exists(jsonPath))
            {
                log.Debug($"[DatabaseService] No JSON file to migrate for account {accountId}");
                return;
            }

            log.Information($"[DatabaseService] Starting JSON to SQLite migration for account {accountId}");

            // Load existing JSON data
            var json = File.ReadAllText(jsonPath);
            var records = JsonSerializer.Deserialize<List<RunRecord>>(json) ?? new List<RunRecord>();

            if (records.Count == 0)
            {
                log.Debug($"[DatabaseService] No records to migrate for account {accountId}");
                return;
            }

            // Insert into SQLite using repository
            var inserted = 0;
            
            foreach (var record in records)
            {
                try
                {
                    repository.AddRunRecord(accountId, record);
                    inserted++;
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"[DatabaseService] Failed to migrate record for account {accountId}");
                }
            }

            // Backup and remove old JSON file
            var backupPath = jsonPath + $".migrated_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Move(jsonPath, backupPath);

            log.Information($"[DatabaseService] Migration complete: {inserted}/{records.Count} records migrated for account {accountId}");
            log.Information($"[DatabaseService] JSON file backed up to: {backupPath}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Migration failed for account {accountId}");
        }
    }
}
