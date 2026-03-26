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
    private readonly List<RunRecord> recentRuns = new();
    private readonly object runHistoryLock = new object();
    
    // Party snapshot storage - capture at /ad start, use at completion
    private List<string> storedPartySnapshot = new List<string>();
    private bool hasPartySnapshot = false;
    private DateTime snapshotTimestamp = DateTime.MinValue;

    public RunHistoryService(IPluginLog log, Configuration config, DutyState state, IPlayerState playerState, ConfigManager configManager, DatabaseService databaseService)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.playerState = playerState;
        this.configManager = configManager;
        this.databaseService = databaseService;
        
        // Note: Database operations moved to Plugin.OnFrameworkUpdate after initialization
        // Constructor is now clean - no database operations here
    }

    /// <summary>
    /// Capture party snapshot when /ad start is executed (inside duty)
    /// This stores the party composition for later use when recording the run
    /// </summary>
    public void CapturePartySnapshot()
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            var partyList = Plugin.PartyList;
            
            log.Information($"[RunHistory] Capturing party snapshot: PartyList.Length={partyList.Length}, LocalPlayer={localPlayer?.Name}");
            
            // Clear previous snapshot
            storedPartySnapshot.Clear();
            hasPartySnapshot = false;
            snapshotTimestamp = DateTime.UtcNow;
            
            // Capture party members with full format
            for (int i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                log.Information($"[RunHistory] Snapshot processing PartyList[{i}]: {member?.Name}");
                
                if (member == null)
                {
                    log.Information($"[RunHistory] Snapshot skipping PartyList[{i}] - member is NULL");
                    continue;
                }
                
                if (string.IsNullOrEmpty(member.Name.ToString()))
                {
                    log.Information($"[RunHistory] Snapshot skipping PartyList[{i}] - member.Name is empty/null");
                    continue;
                }
                
                // Check ClassJob validity
                if (!member.ClassJob.IsValid)
                {
                    log.Information($"[RunHistory] Snapshot skipping PartyList[{i}] - ClassJob is invalid");
                    continue;
                }
                
                var job = member.ClassJob.Value.Abbreviation.ToString();
                var level = member.Level.ToString();
                var formatted = $"{member.Name} - {job} - {level}";
                
                storedPartySnapshot.Add(formatted);
                log.Information($"[RunHistory] SNAPSHOT CAPTURED PartyList[{i}]: {formatted}");
            }
            
            // Add local player if solo with full format
            if (partyList.Length == 0 && localPlayer != null)
            {
                var job = localPlayer.ClassJob.Value.Abbreviation.ToString();
                var level = localPlayer.Level.ToString();
                var formatted = $"{localPlayer.Name} - {job} - {level}";
                storedPartySnapshot.Add(formatted);
                log.Information($"[RunHistory] SNAPSHOT CAPTURED solo player: {formatted}");
            }
            
            hasPartySnapshot = storedPartySnapshot.Count > 0;
            log.Information($"[RunHistory] Party snapshot captured: {storedPartySnapshot.Count} members, hasSnapshot={hasPartySnapshot}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to capture party snapshot");
        }
    }

    /// <summary>
    /// Clear party snapshot (call when starting new duty)
    /// </summary>
    public void ClearPartySnapshot()
    {
        storedPartySnapshot.Clear();
        hasPartySnapshot = false;
        snapshotTimestamp = DateTime.MinValue;
        log.Debug("[RunHistory] Party snapshot cleared");
    }

    /// <summary>
    /// Get the current run history (read-only)
    /// </summary>
    public IReadOnlyList<RunRecord> RunHistory => GetVisibleRuns().AsReadOnly();

    /// <summary>
    /// Load run history from the account-specific database
    /// </summary>
    public void LoadRunHistoryFromDatabase(bool bypassValidation = false)
    {
        try
        {
            // Validate ContentId before database operations (unless bypassing for stats refresh)
            var contentId = playerState.ContentId;
            if (contentId == 0 && !bypassValidation)
            {
                log.Warning("[RunHistory] Skipping run history loading - ContentId not available yet (player not logged in)");
                return;
            }
            
            var accountId = contentId.ToString();
            var records = databaseService.LoadRunRecords(accountId);
            
            runHistory.Clear();
            runHistory.AddRange(records);
            
            var visibleRunCount = GetVisibleRuns(records).Count;
            log.Information($"[RunHistory] Loaded {records.Count} run records from database for account {accountId} ({visibleRunCount} visible in stats)");
            
            // Update JSON configuration stats from database records
            UpdateJsonStatsFromRecords(records);
            
            // Save configuration to persist JSON updates
            configManager.SaveCurrentAccount();
            
            log.Debug($"[RunHistory] Updated JSON stats from {records.Count} database records");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to load run history from database");
        }
    }

    /// <summary>
    /// Update JSON configuration stats from database records
    /// </summary>
    private void UpdateJsonStatsFromRecords(List<RunRecord> records)
    {
        try
        {
            var config = configManager.GetCurrentAccount().Settings;
            var visibleRecords = GetVisibleRuns(records);

            ResetSummaryStats(config);
            
            // Basic counters
            config.DutyCounter = visibleRecords.Count;
            config.TotalPraes = visibleRecords.Count(r => r.IsPraetorium);
            config.TotalDecus = visibleRecords.Count(r => !r.IsPraetorium);
            
            // Separate records by duty type
            var praeRecords = visibleRecords.Where(r => r.IsPraetorium).ToList();
            var decuRecords = visibleRecords.Where(r => !r.IsPraetorium).ToList();
            
            // Praetorium stats
            if (praeRecords.Any())
            {
                config.PraeBestTime = praeRecords.Min(r => r.CompletionTime);
                config.PraeLongestRun = praeRecords.Max(r => r.CompletionTime);
                config.PraeMogtomesEarned = praeRecords.Sum(r => r.MogtomesEarned);
                config.PraeTotalDeathsSelf = praeRecords.Sum(r => r.DeathCount);
                
                // Praetorium best run details
                var bestPraeRun = praeRecords.OrderBy(r => r.CompletionTime).First();
                config.PraeBestTimeDate = bestPraeRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.PraeBestTimeParty = string.Join(", ", bestPraeRun.PartyMembers ?? new List<string>());
                
                // Praetorium longest run details
                var longestPraeRun = praeRecords.OrderByDescending(r => r.CompletionTime).First();
                config.PraeLongestRunDate = longestPraeRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.PraeLongestRunParty = string.Join(", ", longestPraeRun.PartyMembers ?? new List<string>());
            }
            
            // Decumana stats
            if (decuRecords.Any())
            {
                config.DecuBestTime = decuRecords.Min(r => r.CompletionTime);
                config.DecuLongestRun = decuRecords.Max(r => r.CompletionTime);
                config.DecuMogtomesEarned = decuRecords.Sum(r => r.MogtomesEarned);
                config.DecuTotalDeathsSelf = decuRecords.Sum(r => r.DeathCount);
                
                // Decumana best run details
                var bestDecuRun = decuRecords.OrderBy(r => r.CompletionTime).First();
                config.DecuBestTimeDate = bestDecuRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.DecuBestTimeParty = string.Join(", ", bestDecuRun.PartyMembers ?? new List<string>());
                
                // Decumana longest run details
                var longestDecuRun = decuRecords.OrderByDescending(r => r.CompletionTime).First();
                config.DecuLongestRunDate = longestDecuRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.DecuLongestRunParty = string.Join(", ", longestDecuRun.PartyMembers ?? new List<string>());
            }
            
            // Overall stats
            if (visibleRecords.Any())
            {
                config.BestTimeEver = visibleRecords.Min(r => r.CompletionTime);
                config.LongestRunEver = visibleRecords.Max(r => r.CompletionTime);
                config.TotalMogtomesEarned = visibleRecords.Sum(r => r.MogtomesEarned);
                config.TotalDeathsSelf = visibleRecords.Sum(r => r.DeathCount);
                
                // Best time details
                var bestRun = visibleRecords.OrderBy(r => r.CompletionTime).First();
                config.BestTimeDate = bestRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.BestTimeParty = string.Join(", ", bestRun.PartyMembers ?? new List<string>());
                
                // Longest run details
                var longestRun = visibleRecords.OrderByDescending(r => r.CompletionTime).First();
                config.LongestRunDate = longestRun.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                config.LongestRunParty = string.Join(", ", longestRun.PartyMembers ?? new List<string>());
            }
            
            // Daily stats (reset tracking)
            UpdateDailyStats(visibleRecords);
            
            log.Debug($"[RunHistory] Updated JSON stats: {visibleRecords.Count} visible total, {config.TotalPraes} Prae, {config.TotalDecus} Decu");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to update JSON stats from records");
        }
    }

    private void ResetSummaryStats(Configuration config)
    {
        config.DutyCounter = 0;
        config.TotalPraes = 0;
        config.TotalDecus = 0;
        config.TotalMogtomesEarned = 0;
        config.TotalDeathsSelf = 0;
        config.TotalDeathsOthers = 0;
        config.TotalDeathsAll = 0;
        config.MostDeathsSelf = 0;
        config.MostDeathsOthers = 0;
        config.MostDeathsAll = 0;

        config.BestTimeEver = float.MaxValue;
        config.BestTimeDate = string.Empty;
        config.BestTimeParty = string.Empty;
        config.LongestRunEver = 0f;
        config.LongestRunDate = string.Empty;
        config.LongestRunParty = string.Empty;

        config.PraeBestTime = float.MaxValue;
        config.PraeBestTimeDate = string.Empty;
        config.PraeBestTimeParty = string.Empty;
        config.PraeLongestRun = 0f;
        config.PraeLongestRunDate = string.Empty;
        config.PraeLongestRunParty = string.Empty;
        config.PraeMostDeathsSelf = 0;
        config.PraeMostDeathsOthers = 0;
        config.PraeMostDeathsAll = 0;
        config.PraeTotalDeathsSelf = 0;
        config.PraeTotalDeathsOthers = 0;
        config.PraeTotalDeathsAll = 0;
        config.PraeMogtomesEarned = 0;

        config.DecuBestTime = float.MaxValue;
        config.DecuBestTimeDate = string.Empty;
        config.DecuBestTimeParty = string.Empty;
        config.DecuLongestRun = 0f;
        config.DecuLongestRunDate = string.Empty;
        config.DecuLongestRunParty = string.Empty;
        config.DecuMostDeathsSelf = 0;
        config.DecuMostDeathsOthers = 0;
        config.DecuMostDeathsAll = 0;
        config.DecuTotalDeathsSelf = 0;
        config.DecuTotalDeathsOthers = 0;
        config.DecuTotalDeathsAll = 0;
        config.DecuMogtomesEarned = 0;
    }
    
    /// <summary>
    /// Update daily statistics from records
    /// </summary>
    private void UpdateDailyStats(List<RunRecord> records)
    {
        try
        {
            var config = configManager.GetCurrentAccount().Settings;
            var today = DateTime.UtcNow.Date;

            config.DailyDecuRuns = 0;
            config.DailyDecuBestTime = float.MaxValue;
            config.DailyDecuLongestRun = 0f;
            config.DailyDecuMogtomesEarned = 0;
            
            // Get today's Decumana runs
            var todayDecuRuns = records.Where(r => !r.IsPraetorium && r.Timestamp.Date == today).ToList();
            
            config.DailyDecuRuns = todayDecuRuns.Count;
            
            if (todayDecuRuns.Any())
            {
                config.DailyDecuBestTime = todayDecuRuns.Min(r => r.CompletionTime);
                config.DailyDecuLongestRun = todayDecuRuns.Max(r => r.CompletionTime);
                config.DailyDecuMogtomesEarned = todayDecuRuns.Sum(r => r.MogtomesEarned);
            }
            
            // Update max daily runs tracking
            if (config.DailyDecuRuns > config.MaxDailyDecuRuns)
            {
                config.MaxDailyDecuRuns = config.DailyDecuRuns;
            }
            
            if (config.DailyDecuRuns > config.AllTimeMaxDailyDecu)
            {
                config.AllTimeMaxDailyDecu = config.DailyDecuRuns;
            }
            
            log.Debug($"[RunHistory] Updated daily stats: {config.DailyDecuRuns} Decu runs today");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to update daily stats");
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
                
                // Validate ContentId before database operations
                var contentId = playerState.ContentId;
                if (contentId == 0)
                {
                    log.Warning("[RunHistory] Skipping run recording - ContentId not available yet (player not logged in)");
                    return;
                }
                
                // Save to account-specific SQLite database with retry logic
                var accountId = contentId.ToString();
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
            
            // Validate ContentId before database operations
            var contentId = playerState.ContentId;
            if (contentId == 0)
            {
                log.Warning("[RunHistory] Skipping run history clearing - ContentId not available yet (player not logged in)");
                return;
            }
            
            // Clear from database
            var accountId = contentId.ToString();
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
        var isPrae = state.DutyStartTerritory == DutyState.PraetoriumTerritoryId;
        
        // Validate territory
        if (state.DutyStartTerritory != 1044 && state.DutyStartTerritory != 1048)
        {
            log.Warning($"[RunHistory] Invalid territory {state.DutyStartTerritory}, expected 1044 (Prae) or 1048 (Decu)");
            log.Debug($"[RunHistory] CurrentTerritory={state.CurrentTerritory}, DutyStartTerritory={state.DutyStartTerritory}");
        }
        
        // Capture party members - use stored snapshot if available, otherwise current PartyList
        var partyMembers = new List<string>();
        
        if (hasPartySnapshot)
        {
            // Use stored snapshot captured at /ad start
            partyMembers.AddRange(storedPartySnapshot);
            log.Information($"[RunHistory] Using stored party snapshot: {partyMembers.Count} members captured at {snapshotTimestamp:HH:mm:ss}");
            log.Information($"[RunHistory] Stored snapshot members: {string.Join(", ", partyMembers)}");
        }
        else
        {
            // Fallback: capture current PartyList (solo case or snapshot not taken)
            log.Information($"[RunHistory] No party snapshot available, using current PartyList as fallback");
            
            // Debug party detection - enhanced logging
            log.Information($"[RunHistory] Party detection: PartyList.Length={partyList.Length}, LocalPlayer={localPlayer?.Name}");
            log.Information($"[RunHistory] LocalPlayer Address: {localPlayer?.Address:X16}");
            
            for (int i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                if (member == null)
                {
                    log.Information($"[RunHistory] PartyList[{i}]: NULL");
                    continue;
                }
                
                var memberName = member.Name.ToString();
                var memberAddress = member.Address.ToString("X16");
                var isLocalPlayer = localPlayer != null && member.Address == localPlayer.Address;
                var classJob = member.ClassJob.IsValid ? member.ClassJob.Value.Abbreviation.ToString() : "INVALID";
                var level = member.Level;
                
                log.Information($"[RunHistory] PartyList[{i}]: Name={memberName}, Address={memberAddress}, IsLocalPlayer={isLocalPlayer}, Job={classJob}, Level={level}");
            }
            
            // Capture party members with full format
            for (int i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                log.Information($"[RunHistory] Processing PartyList[{i}] for capture: member={member?.Name}");
                
                if (member == null)
                {
                    log.Information($"[RunHistory] Skipping PartyList[{i}] - member is NULL");
                    continue;
                }
                
                if (string.IsNullOrEmpty(member.Name.ToString()))
                {
                    log.Information($"[RunHistory] Skipping PartyList[{i}] - member.Name is empty/null");
                    continue;
                }
                
                // Check ClassJob validity
                if (!member.ClassJob.IsValid)
                {
                    log.Information($"[RunHistory] Skipping PartyList[{i}] - ClassJob is invalid");
                    continue;
                }
                
                var job = member.ClassJob.Value.Abbreviation.ToString();
                var level = member.Level.ToString();
                var formatted = $"{member.Name} - {job} - {level}";
                
                partyMembers.Add(formatted);
                log.Information($"[RunHistory] CAPTURED PartyList[{i}]: {formatted}");
            }
            
            // Add local player if solo with full format
            if (partyList.Length == 0 && localPlayer != null)
            {
                var job = localPlayer.ClassJob.Value.Abbreviation.ToString();
                var level = localPlayer.Level.ToString();
                var formatted = $"{localPlayer.Name} - {job} - {level}";
                partyMembers.Add(formatted);
            }
        }
        
        log.Information($"[RunHistory] Captured {partyMembers.Count} party members: {string.Join(", ", partyMembers)}");
        var recordedPartySize = partyMembers.Count;
        if (recordedPartySize == 0)
        {
            recordedPartySize = partyList.Length;
        }

        if (recordedPartySize == 0 && localPlayer != null)
        {
            recordedPartySize = 1;
        }
        
        return new RunRecord
        {
            Timestamp = DateTime.UtcNow,
            PlayerName = localPlayer?.Name.ToString() ?? "Unknown",
            PlayerWorld = GetPlayerWorld(),
            ContentId = playerState.ContentId,
            JobId = localPlayer?.ClassJob.IsValid == true ? (byte)localPlayer.ClassJob.Value.RowId : (byte)0,
            Level = (byte)(localPlayer?.Level ?? 0),
            TerritoryId = state.DutyStartTerritory,
            CompletionTime = state.LastCompletionDuration,
            DeathCount = 0, // TODO: Implement death tracking
            MogtomesEarned = isPrae ? 7 : 3, // Prae=7, Decu=3
            IsPraetorium = isPrae,
            WasSuccessful = true,
            PartySize = (byte)Math.Clamp(recordedPartySize, 0, byte.MaxValue),
            PartyMembers = partyMembers,
            IsDebugRun = config.TestingModeUnsynced
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
        return GetVisibleRuns()
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
        return GetVisibleRuns()
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
        return GetVisibleRuns().TakeLast(count).Reverse().ToList();
    }

    private List<RunRecord> GetVisibleRuns()
    {
        return GetVisibleRuns(runHistory);
    }

    private List<RunRecord> GetVisibleRuns(IEnumerable<RunRecord> records)
    {
        var visibleRuns = config.ShowDebugRuns
            ? records
            : records.Where(r => !r.IsDebugRun);

        return visibleRuns
            .OrderBy(r => r.Timestamp)
            .ToList();
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
