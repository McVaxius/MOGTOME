using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private const int ConnectionOpenAttempts = 3;
    private const int ConnectionRetryDelayMs = 100;
    private const int BusyTimeoutMs = 5000;

    private readonly IPluginLog log;
    private readonly string databaseFolder;
    private readonly object sqliteLock = new();
    
    public SqliteRunRecordRepository(IPluginLog log, string databaseFolder)
    {
        this.log = log;
        this.databaseFolder = databaseFolder;
    }
    
    private string GetDatabasePath(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account id is required for run-history SQLite storage.", nameof(accountId));

        return Path.Combine(databaseFolder, $"{accountId}.sqlite");
    }

    private SqliteConnection OpenConnection(string dbPath)
    {
        EnsureDatabaseDirectory(dbPath);

        for (var attempt = 1; attempt <= ConnectionOpenAttempts; attempt++)
        {
            var connection = new SqliteConnection($"Data Source={dbPath};");
            try
            {
                connection.Open();
                ApplyBusyTimeout(connection);
                return connection;
            }
            catch
            {
                connection.Dispose();
                if (attempt >= ConnectionOpenAttempts)
                    throw;

                Thread.Sleep(ConnectionRetryDelayMs * attempt);
            }
        }

        throw new InvalidOperationException($"Failed to open SQLite database: {dbPath}");
    }

    private static void EnsureDatabaseDirectory(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Invalid database path: {dbPath}");

        Directory.CreateDirectory(directory);
    }

    private static void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}";
        cmd.ExecuteNonQuery();
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
                PartyMembers TEXT NOT NULL,
                IsDebugRun INTEGER NOT NULL DEFAULT 0
            );
            
            CREATE INDEX IF NOT EXISTS idx_runrecords_timestamp ON RunRecords(Timestamp);
            CREATE INDEX IF NOT EXISTS idx_runrecords_contentid ON RunRecords(ContentId);
            
            PRAGMA user_version = 1;
        ";
        cmd.ExecuteNonQuery();
        EnsureColumnExists(connection, "RunRecords", "IsDebugRun", "INTEGER NOT NULL DEFAULT 0");
        RepairStoredPartySizes(connection);
    }

    private void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
        alterCmd.ExecuteNonQuery();
        log.Information($"[SqliteRepository] Added missing column {tableName}.{columnName}");
    }

    private void RepairStoredPartySizes(SqliteConnection connection)
    {
        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT rowid, PartySize, PartyMembers FROM RunRecords WHERE PartySize <= 0";

        using var reader = selectCmd.ExecuteReader();
        var repairs = new List<(long RowId, int PartySize)>();

        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            var storedPartySize = reader.GetInt32(1);
            var partyMembersJson = reader.GetString(2);
            var partyMembers = DeserializePartyMembers(partyMembersJson);
            var repairedPartySize = storedPartySize > 0 ? storedPartySize : partyMembers.Count;

            if (repairedPartySize > 0)
            {
                repairs.Add((rowId, repairedPartySize));
            }
        }

        foreach (var repair in repairs)
        {
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE RunRecords SET PartySize = @PartySize WHERE rowid = @RowId";
            updateCmd.Parameters.AddWithValue("@PartySize", repair.PartySize);
            updateCmd.Parameters.AddWithValue("@RowId", repair.RowId);
            updateCmd.ExecuteNonQuery();
        }

        if (repairs.Count > 0)
        {
            log.Information($"[SqliteRepository] Repaired {repairs.Count} run records with missing party sizes");
        }
    }
    
    public List<RunRecord> LoadRunRecords(string accountId)
    {
        try
        {
            lock (sqliteLock)
            {
                var dbPath = GetDatabasePath(accountId);
                var records = new List<RunRecord>();
                using var connection = OpenConnection(dbPath);

                InitializeDatabase(connection);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT ContentId, Timestamp, TerritoryId, CompletionTime, MogtomesEarned,
                           IsPraetorium, WasSuccessful, PartySize, PartyMembers, IsDebugRun
                    FROM RunRecords
                    ORDER BY Timestamp DESC
                    LIMIT 1000
                ";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var partyMembers = DeserializePartyMembers(reader.GetString(8));
                    var storedPartySize = reader.GetInt32(7);

                    var record = new RunRecord
                    {
                        ContentId = (ulong)reader.GetInt64(0),
                        Timestamp = DateTime.Parse(reader.GetString(1)),
                        TerritoryId = (ushort)reader.GetInt32(2),
                        CompletionTime = (float)reader.GetDouble(3),
                        MogtomesEarned = reader.GetInt32(4),
                        IsPraetorium = reader.GetBoolean(5),
                        WasSuccessful = reader.GetBoolean(6),
                        PartySize = (byte)Math.Clamp(storedPartySize > 0 ? storedPartySize : partyMembers.Count, 0, byte.MaxValue),
                        PartyMembers = partyMembers,
                        IsDebugRun = reader.GetBoolean(9)
                    };
                    records.Add(record);
                }

                log.Debug($"[SqliteRepository] Loaded {records.Count} run records for account {accountId}");
                return records;
            }
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
            lock (sqliteLock)
            {
                var dbPath = GetDatabasePath(accountId);
                using var connection = OpenConnection(dbPath);

                InitializeDatabase(connection);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO RunRecords (ContentId, Timestamp, TerritoryId, CompletionTime, MogtomesEarned, IsPraetorium, WasSuccessful, PartySize, PartyMembers, IsDebugRun)
                    VALUES (@ContentId, @Timestamp, @TerritoryId, @CompletionTime, @MogtomesEarned, @IsPraetorium, @WasSuccessful, @PartySize, @PartyMembers, @IsDebugRun)
                ";

                cmd.Parameters.AddWithValue("@ContentId", record.ContentId);
                cmd.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("@TerritoryId", record.TerritoryId);
                cmd.Parameters.AddWithValue("@CompletionTime", record.CompletionTime);
                cmd.Parameters.AddWithValue("@MogtomesEarned", record.MogtomesEarned);
                cmd.Parameters.AddWithValue("@IsPraetorium", record.IsPraetorium);
                cmd.Parameters.AddWithValue("@WasSuccessful", record.WasSuccessful);
                cmd.Parameters.AddWithValue("@PartySize", record.PartySize);
                cmd.Parameters.AddWithValue("@PartyMembers", JsonSerializer.Serialize(record.PartyMembers ?? []));
                cmd.Parameters.AddWithValue("@IsDebugRun", record.IsDebugRun);

                cmd.ExecuteNonQuery();
                log.Debug($"[SqliteRepository] Added run record for account {accountId}");
            }
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
            lock (sqliteLock)
            {
                var dbPath = GetDatabasePath(accountId);
                using var connection = OpenConnection(dbPath);

                InitializeDatabase(connection);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM RunRecords";
                cmd.ExecuteNonQuery();

                log.Information($"[SqliteRepository] Cleared all run records for account {accountId}");
            }
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
            lock (sqliteLock)
            {
                var testDbPath = Path.Combine(databaseFolder, "health_check.sqlite");
                using var connection = OpenConnection(testDbPath);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.ExecuteScalar();

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
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SqliteRepository] Repository health check failed");
            return false;
        }
    }

    private static List<string> DeserializePartyMembers(string partyMembersJson)
    {
        if (string.IsNullOrWhiteSpace(partyMembersJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(partyMembersJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
