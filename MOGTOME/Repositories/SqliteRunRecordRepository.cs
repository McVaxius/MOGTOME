using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Microsoft.Data.Sqlite;
using MOGTOME.Models;

namespace MOGTOME.Repositories;

/// <summary>
/// SQLite implementation of RunRecord repository
/// Following XA repository pattern with proper error handling and logging
/// </summary>
public class SqliteRunRecordRepository : IRunRecordRepository
{
    private readonly IPluginLog log;
    private readonly string databaseFolder;
    
    public SqliteRunRecordRepository(IPluginLog log, string databaseFolder)
    {
        this.log = log;
        this.databaseFolder = databaseFolder;
    }
    
    private string GetDatabasePath(string accountId)
    {
        return Path.Combine(databaseFolder, $"{accountId}.sqlite");
    }
    
    private void InitializeDatabase(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS RunRecords (
                ContentId INTEGER NOT NULL,
                Timestamp TEXT NOT NULL,
                TerritoryId INTEGER NOT NULL,
                CompletionTime REAL NOT NULL,
                MogtomesEarned INTEGER NOT NULL,
                IsPraetorium INTEGER NOT NULL,
                WasSuccessful INTEGER NOT NULL,
                PartySize INTEGER NOT NULL,
                PartyMembers TEXT NOT NULL
            );
            
            CREATE INDEX IF NOT EXISTS idx_runrecords_timestamp ON RunRecords(Timestamp);
            CREATE INDEX IF NOT EXISTS idx_runrecords_contentid ON RunRecords(ContentId);
            
            PRAGMA user_version = 1;
        ";
        cmd.ExecuteNonQuery();
        
        // Enable WAL mode for performance
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteNonQuery();
    }
    
    public List<RunRecord> LoadRunRecords(string accountId)
    {
        try
        {
            var dbPath = GetDatabasePath(accountId);
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var records = new List<RunRecord>();
            using var connection = new SqliteConnection($"Data Source={dbPath};");
            connection.Open();
            
            InitializeDatabase(connection);
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM RunRecords ORDER BY Timestamp DESC LIMIT 1000";
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var record = new RunRecord
                {
                    ContentId = (ulong)reader.GetInt64(0),
                    Timestamp = DateTime.Parse(reader.GetString(1)),
                    TerritoryId = (ushort)reader.GetInt32(2),
                    CompletionTime = (float)reader.GetDouble(3),
                    MogtomesEarned = reader.GetInt32(4),
                    IsPraetorium = reader.GetBoolean(5),
                    WasSuccessful = reader.GetBoolean(6),
                    PartySize = (byte)reader.GetInt32(7),
                    PartyMembers = JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? new List<string>()
                };
                records.Add(record);
            }
            
            log.Debug($"[SqliteRepository] Loaded {records.Count} run records for account {accountId}");
            return records;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[SqliteRepository] Failed to load run records for account {accountId}");
            return new List<RunRecord>();
        }
    }
    
    public void AddRunRecord(string accountId, RunRecord record)
    {
        try
        {
            var dbPath = GetDatabasePath(accountId);
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            using var connection = new SqliteConnection($"Data Source={dbPath};");
            connection.Open();
            
            InitializeDatabase(connection);
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO RunRecords (ContentId, Timestamp, TerritoryId, CompletionTime, MogtomesEarned, IsPraetorium, WasSuccessful, PartySize, PartyMembers)
                VALUES (@ContentId, @Timestamp, @TerritoryId, @CompletionTime, @MogtomesEarned, @IsPraetorium, @WasSuccessful, @PartySize, @PartyMembers)
            ";
            
            cmd.Parameters.AddWithValue("@ContentId", record.ContentId);
            cmd.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("@TerritoryId", record.TerritoryId);
            cmd.Parameters.AddWithValue("@CompletionTime", record.CompletionTime);
            cmd.Parameters.AddWithValue("@MogtomesEarned", record.MogtomesEarned);
            cmd.Parameters.AddWithValue("@IsPraetorium", record.IsPraetorium);
            cmd.Parameters.AddWithValue("@WasSuccessful", record.WasSuccessful);
            cmd.Parameters.AddWithValue("@PartySize", record.PartySize);
            cmd.Parameters.AddWithValue("@PartyMembers", JsonSerializer.Serialize(record.PartyMembers));
            
            cmd.ExecuteNonQuery();
            log.Debug($"[SqliteRepository] Added run record for account {accountId}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[SqliteRepository] Failed to add run record for account {accountId}");
            throw;
        }
    }
    
    public void ClearRunRecords(string accountId)
    {
        try
        {
            var dbPath = GetDatabasePath(accountId);
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            using var connection = new SqliteConnection($"Data Source={dbPath};");
            connection.Open();
            
            InitializeDatabase(connection);
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM RunRecords";
            cmd.ExecuteNonQuery();
            
            log.Information($"[SqliteRepository] Cleared all run records for account {accountId}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[SqliteRepository] Failed to clear run records for account {accountId}");
            throw;
        }
    }
    
    public bool IsHealthy()
    {
        try
        {
            // Test health by checking if we can create a temporary connection
            var testDbPath = Path.Combine(databaseFolder, "health_check.sqlite");
            var directory = Path.GetDirectoryName(testDbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            using var connection = new SqliteConnection($"Data Source={testDbPath};");
            connection.Open();
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
            
            // Clean up test database
            connection.Close();
            try
            {
                File.Delete(testDbPath);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[SqliteRepository] Failed to delete health check database");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SqliteRepository] Repository health check failed");
            return false;
        }
    }
}
