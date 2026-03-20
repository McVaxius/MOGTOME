using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using SQLite;

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
    private readonly string databaseFolder;
    private readonly Dictionary<string, SQLiteConnection> connections = new();

    public DatabaseService(IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.log = log;
        this.pluginInterface = pluginInterface;
        this.databaseFolder = Path.Combine(pluginInterface.ConfigDirectory.FullName, "MOGTOME", "RunHistory");
        
        // Ensure database folder exists
        Directory.CreateDirectory(databaseFolder);
        
        log.Information($"[DatabaseService] Initialized with SQLite database folder: {databaseFolder}");
    }

    /// <summary>
    /// Get the SQLite database file path for a specific account
    /// </summary>
    private string GetDatabasePath(string accountId)
    {
        return Path.Combine(databaseFolder, $"{accountId}.sqlite");
    }

    /// <summary>
    /// Get or create a SQLite connection for the specified account
    /// </summary>
    private SQLiteConnection GetConnection(string accountId)
    {
        if (connections.ContainsKey(accountId))
        {
            return connections[accountId];
        }

        var dbPath = GetDatabasePath(accountId);
        var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        
        // Create tables if they don't exist
        InitializeDatabase(connection);
        
        connections[accountId] = connection;
        log.Debug($"[DatabaseService] Created SQLite connection for account {accountId}");
        return connection;
    }

    /// <summary>
    /// Initialize database schema with proper indexing
    /// </summary>
    private void InitializeDatabase(SQLiteConnection connection)
    {
        try
        {
            // Create RunRecords table with indexes for performance
            connection.CreateTable<RunRecord>();

            // Create indexes for common queries
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_timestamp ON RunRecords(Timestamp)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_contentid ON RunRecords(ContentId)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_territory ON RunRecords(TerritoryId)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_completiontime ON RunRecords(CompletionTime)");
            
            log.Debug("[DatabaseService] Database schema initialized with indexes");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[DatabaseService] Failed to initialize database schema");
            throw;
        }
    }

    /// <summary>
    /// Load run records from the account-specific SQLite database
    /// </summary>
    public List<RunRecord> LoadRunRecords(string accountId)
    {
        try
        {
            var connection = GetConnection(accountId);
            
            var records = connection.Query<RunRecord>("SELECT * FROM RunRecords ORDER BY Timestamp DESC LIMIT 1000");

            log.Debug($"[DatabaseService] Loaded {records.Count} run records from SQLite for account {accountId}");
            return records.ToList();
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[DatabaseService] Failed to load run records for account {accountId}");
            return new List<RunRecord>();
        }
    }

    /// <summary>
    /// Add a single run record to the SQLite database
    /// </summary>
    public void AddRunRecord(string accountId, RunRecord record)
    {
        try
        {
            var connection = GetConnection(accountId);
            
            // Insert the new record
            connection.Insert(record);
            
            // Maintain size limit by deleting oldest records if needed
            var count = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM RunRecords");
            if (count > 1000)
            {
                var excess = count - 1000;
                connection.Execute("DELETE FROM RunRecords WHERE Id IN (SELECT Id FROM RunRecords ORDER BY Timestamp ASC LIMIT ?)", excess);
                
                log.Debug($"[DatabaseService] Deleted {excess} old records to maintain size limit");
            }
            
            log.Debug($"[DatabaseService] Added run record for account {accountId}");
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
        try
        {
            var connection = GetConnection(accountId);
            connection.Execute("DELETE FROM RunRecords");
            
            log.Information($"[DatabaseService] Cleared all run records for account {accountId}");
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

            // Insert into SQLite
            var connection = GetConnection(accountId);
            var inserted = 0;
            
            foreach (var record in records)
            {
                try
                {
                    connection.Insert(record);
                    inserted++;
                }
                catch (Exception ex)
                {
                    log.Warning($"[DatabaseService] Failed to migrate record {record.Timestamp}: {ex.Message}");
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

    /// <summary>
    /// Dispose of resources
    /// </summary>
    public void Dispose()
    {
        foreach (var connection in connections.Values)
        {
            try
            {
                connection.Close();
                connection.Dispose();
            }
            catch (Exception ex)
            {
                log.Warning($"[DatabaseService] Error closing connection: {ex.Message}");
            }
        }
        
        connections.Clear();
        log.Information("[DatabaseService] Disposed database service");
    }
}
