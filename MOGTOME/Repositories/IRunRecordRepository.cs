using System.Collections.Generic;
using MOGTOME.Models;

namespace MOGTOME.Repositories;

/// <summary>
/// Repository interface for RunRecord data access
/// Following XA repository pattern for clean separation of concerns
/// </summary>
public interface IRunRecordRepository
{
    /// <summary>
    /// Load all run records for a specific account
    /// </summary>
    /// <param name="accountId">Account identifier</param>
    /// <returns>List of run records</returns>
    List<RunRecord> LoadRunRecords(string accountId);
    
    /// <summary>
    /// Add a new run record for a specific account
    /// </summary>
    /// <param name="accountId">Account identifier</param>
    /// <param name="record">Run record to add</param>
    void AddRunRecord(string accountId, RunRecord record);
    
    /// <summary>
    /// Clear all run records for a specific account
    /// </summary>
    /// <param name="accountId">Account identifier</param>
    void ClearRunRecords(string accountId);
    
    /// <summary>
    /// Check if the repository is healthy and ready for operations
    /// </summary>
    /// <returns>True if healthy, false otherwise</returns>
    bool IsHealthy();
}
