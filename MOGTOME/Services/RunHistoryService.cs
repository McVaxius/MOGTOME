using Dalamud.Plugin.Services;
using MOGTOME.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MOGTOME.Services;

/// <summary>
/// Service for managing run history and detailed statistics
/// </summary>
public class RunHistoryService
{
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly DutyState state;
    private readonly IPlayerState playerState;

    public RunHistoryService(IPluginLog log, Configuration config, DutyState state, IPlayerState playerState)
    {
        this.log = log;
        this.config = config;
        this.state = state;
        this.playerState = playerState;
    }

    /// <summary>
    /// Record a completed duty run
    /// </summary>
    public void RecordRun()
    {
        if (!config.EnableDetailedTracking)
            return;

        try
        {
            var runRecord = CreateRunRecord();
            
            // Add to history
            config.RunHistory.Add(runRecord);
            
            // Maintain history size limit
            MaintainHistorySize();
            
            config.Save();
            
            log.Debug($"[RunHistory] Recorded run: {runRecord.PlayerName} ({GetJobName(runRecord.JobId)}) - {runRecord.CompletionTime:F1}s");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[RunHistory] Failed to record run");
        }
    }

    /// <summary>
    /// Create a RunRecord from current state
    /// </summary>
    private RunRecord CreateRunRecord()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var isPrae = state.CurrentTerritory == DutyState.PraetoriumTerritoryId;
        
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
            PartySize = (byte)Plugin.PartyList.Length
        };
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
    /// Maintain history size by removing oldest entries
    /// </summary>
    private void MaintainHistorySize()
    {
        if (config.RunHistory.Count > config.MaxHistorySize)
        {
            var excess = config.RunHistory.Count - config.MaxHistorySize;
            config.RunHistory.RemoveRange(0, excess);
            log.Debug($"[RunHistory] Removed {excess} old entries to maintain size limit");
        }
    }

    /// <summary>
    /// Get job statistics across all runs
    /// </summary>
    public Dictionary<byte, JobStats> GetJobStatistics()
    {
        return config.RunHistory
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
        return config.RunHistory
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
        return config.RunHistory.TakeLast(count).Reverse().ToList();
    }

    /// <summary>
    /// Clear all run history
    /// </summary>
    public void ClearHistory()
    {
        config.RunHistory.Clear();
        config.Save();
        log.Information("[RunHistory] Run history cleared");
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
