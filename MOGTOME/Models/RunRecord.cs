using System;

namespace MOGTOME.Models;

/// <summary>
/// Represents a single duty run record for detailed statistics tracking
/// </summary>
public class RunRecord
{
    /// <summary>
    /// When the run was completed
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Player name (may be anonymized with Krangle mode)
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;
    
    /// <summary>
    /// Player world/server name
    /// </summary>
    public string PlayerWorld { get; set; } = string.Empty;
    
    /// <summary>
    /// Unique player Content ID for identification
    /// </summary>
    public ulong ContentId { get; set; } = 0;
    
    /// <summary>
    /// Job ID (e.g., 1=GLA, 2=PGL, 3=MRD, etc.)
    /// </summary>
    public byte JobId { get; set; } = 0;
    
    /// <summary>
    /// Player level during the run
    /// </summary>
    public byte Level { get; set; } = 0;
    
    /// <summary>
    /// Territory type ID (1044=Praetorium, 1048=Decumana)
    /// </summary>
    public ushort TerritoryId { get; set; } = 0;
    
    /// <summary>
    /// Completion time in seconds
    /// </summary>
    public float CompletionTime { get; set; } = 0f;
    
    /// <summary>
    /// Number of deaths during the run
    /// </summary>
    public int DeathCount { get; set; } = 0;
    
    /// <summary>
    /// Number of mogtomes earned
    /// </summary>
    public int MogtomesEarned { get; set; } = 0;
    
    /// <summary>
    /// Whether this was a Praetorium run (true) or Decumana (false)
    /// </summary>
    public bool IsPraetorium { get; set; } = false;
    
    /// <summary>
    /// Whether the run was successful (completed without issues)
    /// </summary>
    public bool WasSuccessful { get; set; } = true;
    
    /// <summary>
    /// Item level during the run
    /// </summary>
    public ushort ItemLevel { get; set; } = 0;
    
    /// <summary>
    /// Party size during the run
    /// </summary>
    public byte PartySize { get; set; } = 1;
}

/// <summary>
/// Statistics for a specific job across all runs
/// </summary>
public class JobStats
{
    public int TotalRuns { get; set; } = 0;
    public float AverageTime { get; set; } = 0f;
    public float BestTime { get; set; } = float.MaxValue;
    public float WorstTime { get; set; } = 0f;
    public int TotalDeaths { get; set; } = 0;
    public int SuccessfulRuns { get; set; } = 0;
    public int PraetoriumRuns { get; set; } = 0;
    public int DecumanaRuns { get; set; } = 0;
    public int TotalMogtomes { get; set; } = 0;
    public DateTime LastRun { get; set; } = DateTime.MinValue;
}

/// <summary>
/// Statistics for a specific player across all runs
/// </summary>
public class PlayerStats
{
    public string PlayerName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public bool IsLocalPlayer { get; set; } = false;
    public int TotalRuns { get; set; } = 0;
    public int PraetoriumRuns { get; set; } = 0;
    public int DecumanaRuns { get; set; } = 0;
    public float AverageTime { get; set; } = 0f;
    public float BestTime { get; set; } = float.MaxValue;
    public int TotalDeaths { get; set; } = 0;
    public int BestStreak { get; set; } = 0;
    public int CurrentStreak { get; set; } = 0;
    public DateTime LastRun { get; set; } = DateTime.MinValue;
    public byte MostPlayedJob { get; set; } = 0;
    public int TotalMogtomes { get; set; } = 0;
}
