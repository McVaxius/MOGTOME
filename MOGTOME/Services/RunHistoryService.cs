using Dalamud.Plugin.Services;
using MOGTOME.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MOGTOME.Services;

/// <summary>
/// Service for managing run history and detailed statistics
/// Uses per-account SQLite databases to prevent multi-client conflicts
/// </summary>
public class RunHistoryService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly IPlayerState playerState;
    private readonly ConfigManager configManager;
    private readonly DatabaseService databaseService;
    private readonly List<RunRecord> runHistory = new();

    public RunHistoryService(IPluginLog log, Configuration config, DutyState state, IPlayerState playerState, ConfigManager configManager, DatabaseService databaseService)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.playerState = playerState;
        this.configManager = configManager;
        this.databaseService = databaseService;
        
        // Trigger migration from JSON to SQLite if needed
        var accountId = playerState.ContentId.ToString();
        databaseService.MigrateFromJson(accountId);
        
        // Load existing run records from SQLite database
        LoadRunHistoryFromDatabase();
    }

    /// <summary>
    /// Get the current run history (read-only)
    /// </summary>
    public IReadOnlyList<RunRecord> RunHistory => runHistory.AsReadOnly();

    /// <summary>
    /// Load run history from the account-specific database
    /// </summary>
    private void LoadRunHistoryFromDatabase()
    {
        try
        {
            var accountId = playerState.ContentId.ToString();
            var records = databaseService.LoadRunRecords(accountId);
            
            runHistory.Clear();
            runHistory.AddRange(records);
            
            log.Information($"[RunHistory] Loaded {records.Count} run records from database for account {accountId}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to load run history from database");
        }
    }

    /// <summary>
    /// Record a completed duty run
    /// Thread-safe implementation with retry logic
    /// </summary>
    public void RecordRun()
    {
        if (!config.EnableDetailedTracking)
            return;

        lock (runHistory) // Prevent concurrent access
        {
            try
            {
                var runRecord = CreateRunRecord();
                
                // Add to in-memory history
                runHistory.Add(runRecord);
                
                // Maintain history size limit
                MaintainHistorySize();
                
                // Save to account-specific SQLite database with retry logic
                var accountId = playerState.ContentId.ToString();
                SaveRunWithRetry(accountId, runRecord);
                
                log.Debug($"[RunHistory] Recorded run: {runRecord.PlayerName} ({GetJobName(runRecord.JobId)}) - {runRecord.CompletionTime:F1}s");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[RunHistory] Failed to record run");
                // Don't rethrow - we don't want to break the duty completion flow
            }
        }
    }

    /// <summary>
    /// Save run record with retry logic to handle database conflicts
    /// </summary>
    private void SaveRunWithRetry(string accountId, RunRecord runRecord)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                databaseService.AddRunRecord(accountId, runRecord);
                log.Debug($"[RunHistory] Save successful on attempt {attempt} for account {accountId}");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && (
                ex.Message.Contains("database is locked") || 
                ex.Message.Contains("used by another process") ||
                ex.Message.Contains("being used") ||
                ex.Message.Contains("file is in use")))
            {
                log.Warning($"[RunHistory] Save attempt {attempt} failed (database conflict), retrying in {retryDelayMs}ms...: {ex.Message}");
                System.Threading.Thread.Sleep(retryDelayMs);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[RunHistory] Save failed on attempt {attempt} for account {accountId}");
                if (attempt == maxRetries)
                    throw;
                System.Threading.Thread.Sleep(retryDelayMs);
            }
        }
        
        // Final attempt - let it throw if it fails
        try
        {
            databaseService.AddRunRecord(accountId, runRecord);
            log.Debug($"[RunHistory] Final save attempt successful for account {accountId}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[RunHistory] All save attempts failed for account {accountId}");
            throw;
        }
    }

    /// <summary>
    /// Clear all run history for the current account
    /// </summary>
    public void ClearRunHistory()
    {
        try
        {
            // Clear in-memory history
            runHistory.Clear();
            
            // Clear from database
            var accountId = playerState.ContentId.ToString();
            databaseService.ClearRunRecords(accountId);
            
            log.Information($"[RunHistory] Cleared all run history for account {accountId}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to clear run history");
        }
    }

    /// <summary>
    /// Maintain history size limit
    /// </summary>
    private void MaintainHistorySize()
    {
        if (runHistory.Count > config.MaxHistorySize)
        {
            var excessCount = runHistory.Count - config.MaxHistorySize;
            runHistory.RemoveRange(0, excessCount);
            log.Debug($"[RunHistory] Removed {excessCount} old records to maintain size limit");
        }
    }

    /// <summary>
    /// Create a RunRecord from current state
    /// </summary>
    private RunRecord CreateRunRecord()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var partyList = Plugin.PartyList;
        var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
        
        // Debug party detection
        log.Information($"[RunHistory] Party detection: PartyList.Length={partyList.Length}, LocalPlayer={localPlayer?.Name}");
        for (int i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            log.Information($"[RunHistory] Party member {i}: {member?.Name}");
        }
        
        // Capture party members
        var partyMembers = new List<string>();
        for (int i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member != null && !string.IsNullOrEmpty(member.Name.ToString()))
            {
                partyMembers.Add(member.Name.ToString());
            }
        }
        
        // Add local player if solo
        if (partyList.Length == 0 && localPlayer != null)
        {
            partyMembers.Add(localPlayer.Name.ToString());
        }
        
        log.Information($"[RunHistory] Captured {partyMembers.Count} party members: {string.Join(", ", partyMembers)}");
        
        return new RunRecord
        {
            Timestamp = DateTime.UtcNow,
            PlayerName = localPlayer?.Name.ToString() ?? "Unknown",
            PlayerWorld = GetPlayerWorld(),
            ContentId = playerState.ContentId,
            JobId = localPlayer?.ClassJob.IsValid == true ? (byte)localPlayer.ClassJob.Value.RowId : (byte)0,
            Level = (byte)(localPlayer?.Level ?? 0),
            TerritoryId = state.CurrentTerritory,
            CompletionTime = state.LastCompletionDuration,
            DeathCount = 0, // TODO: Implement death tracking
            MogtomesEarned = isPrae ? 15 : 20, // Prae=15, Decu=20
            IsPraetorium = isPrae,
            WasSuccessful = true,
            ItemLevel = (ushort)(localPlayer?.Level ?? 0), // Simplified for now
            PartySize = (byte)partyList.Length,
            PartyMembers = partyMembers
        };
    }

    /// <summary>
    /// Get party members for a specific run
    /// </summary>
    public List<string> GetPartyMembersForRun(RunRecord run)
    {
        return run.PartyMembers ?? new List<string>();
    }

    /// <summary>
    /// Get player world name
    /// </summary>
    private string GetPlayerWorld()
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer != null)
            {
                var homeWorld = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?
                    .GetRowOrDefault((uint)localPlayer.HomeWorld.RowId);
                return homeWorld?.Name.ToString() ?? "Unknown";
            }
        }
        catch
        {
            // Ignore errors
        }
        return "Unknown";
    }

    /// <summary>
    /// Get job statistics across all runs
    /// </summary>
    public Dictionary<byte, JobStats> GetJobStatistics()
    {
        return runHistory
            .GroupBy(x => x.JobId)
            .ToDictionary(
                g => g.Key,
                g => new JobStats
                {
                    TotalRuns = g.Count(),
                    AverageTime = g.Average(x => x.CompletionTime),
                    BestTime = g.Min(x => x.CompletionTime),
                    WorstTime = g.Max(x => x.CompletionTime),
                    TotalDeaths = g.Sum(x => x.DeathCount),
                    SuccessfulRuns = g.Count(x => x.WasSuccessful),
                    PraetoriumRuns = g.Count(x => x.IsPraetorium),
                    DecumanaRuns = g.Count(x => !x.IsPraetorium),
                    TotalMogtomes = g.Sum(x => x.MogtomesEarned),
                    LastRun = g.Max(x => x.Timestamp)
                }
            );
    }

    /// <summary>
    /// Get player statistics across all runs
    /// </summary>
    public Dictionary<ulong, PlayerStats> GetPlayerStatistics()
    {
        return runHistory
            .GroupBy(x => x.ContentId)
            .ToDictionary(
                g => g.Key,
                g => new PlayerStats
                {
                    PlayerName = g.First().PlayerName,
                    WorldName = g.First().PlayerWorld,
                    IsLocalPlayer = g.First().ContentId == playerState.ContentId,
                    TotalRuns = g.Count(),
                    PraetoriumRuns = g.Count(x => x.IsPraetorium),
                    DecumanaRuns = g.Count(x => !x.IsPraetorium),
                    AverageTime = g.Average(x => x.CompletionTime),
                    BestTime = g.Min(x => x.CompletionTime),
                    TotalDeaths = g.Sum(x => x.DeathCount),
                    BestStreak = CalculateBestStreak(g.ToList()),
                    CurrentStreak = CalculateCurrentStreak(g.ToList()),
                    LastRun = g.Max(x => x.Timestamp),
                    MostPlayedJob = g.GroupBy(x => x.JobId).OrderByDescending(x => x.Count()).First().Key,
                    TotalMogtomes = g.Sum(x => x.MogtomesEarned)
                }
            );
    }

    /// <summary>
    /// Calculate best streak of successful runs
    /// </summary>
    private int CalculateBestStreak(List<RunRecord> runs)
    {
        int bestStreak = 0;
        int currentStreak = 0;
        
        foreach (var run in runs.OrderByDescending(x => x.Timestamp))
        {
            if (run.WasSuccessful)
            {
                currentStreak++;
                bestStreak = Math.Max(bestStreak, currentStreak);
            }
            else
            {
                currentStreak = 0;
            }
        }
        
        return bestStreak;
    }

    /// <summary>
    /// Calculate current streak of successful runs
    /// </summary>
    private int CalculateCurrentStreak(List<RunRecord> runs)
    {
        int currentStreak = 0;
        
        foreach (var run in runs.OrderByDescending(x => x.Timestamp))
        {
            if (run.WasSuccessful)
            {
                currentStreak++;
            }
            else
            {
                break;
            }
        }
        
        return currentStreak;
    }

    /// <summary>
    /// Get recent runs
    /// </summary>
    public List<RunRecord> GetRecentRuns(int count = 25)
    {
        return runHistory.TakeLast(count).Reverse().ToList();
    }

    /// <summary>
    /// Get job name from ID
    /// </summary>
    private string GetJobName(byte jobId)
    {
        return jobId switch
        {
            1 => "GLA", 2 => "PGL", 3 => "MRD", 4 => "LNC", 5 => "ARC", 6 => "CNJ", 7 => "THM", 8 => "BLU",
            9 => "CRP", 10 => "BSM", 11 => "ARM", 12 => "GSM", 13 => "LTW", 14 => "WVR", 15 => "ALC", 16 => "CUL",
            17 => "MIN", 18 => "BTN", 19 => "FSH", 20 => "PLD", 21 => "MNK", 22 => "WAR", 23 => "DRG", 24 => "BRD",
            25 => "NIN", 26 => "SMN", 27 => "SCH", 28 => "RDM", 29 => "BLM", 30 => "WHM", 31 => "DRK", 32 => "AST", 33 => "SAM",
            34 => "MCH", 35 => "DNC", 36 => "RPR", 37 => "SGE", 38 => "VPR", 39 => "PCT",
            _ => "UNK"
        };
    }
}
