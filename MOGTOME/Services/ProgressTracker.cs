using System;
using System.IO;

namespace MOGTOME.Services;

public class ProgressTracker : IDisposable
{
    private readonly Configuration config;
    private readonly string statisticsPath;
    private DutyStatistics? statistics;

    public ProgressTracker(Configuration config)
    {
        this.config = config;
        
        // Set up statistics file path
        var pluginDir = Service.PluginInterface.ConfigDirectory;
        statisticsPath = Path.Combine(pluginDir, "statistics.json");
        
        LoadStatistics();
        Service.Log.Info("ProgressTracker initialized");
    }

    public int GetDailyProgress()
    {
        return config.DailyCounter;
    }

    public int GetDailyTarget()
    {
        return config.DailyTarget;
    }

    public double GetDailyProgressPercentage()
    {
        if (config.DailyTarget == 0) return 0;
        return (double)config.DailyCounter / config.DailyTarget * 100;
    }

    public DutyType GetCurrentDutyType()
    {
        return config.CurrentDuty;
    }

    public void IncrementCounter()
    {
        try
        {
            config.DailyCounter++;
            config.Save();
            
            // Update statistics
            if (statistics != null)
            {
                statistics.TotalDutiesCompleted++;
                statistics.TodayDutiesCompleted = config.DailyCounter;
                
                if (config.CurrentDuty == DutyType.Praetorium)
                {
                    statistics.TotalPraetoriumCompleted++;
                }
                else if (config.CurrentDuty == DutyType.PortaDecumana)
                {
                    statistics.TotalPortaDecumanaCompleted++;
                }
                
                SaveStatistics();
            }
            
            Service.Log.Info($"Duty counter incremented: {config.DailyCounter}/{config.DailyTarget}");
            Service.Chat.Print($"Duty completed! ({config.DailyCounter}/{config.DailyTarget})");
            
            // Check for milestones
            CheckMilestones();
            
            // Check for duty type switch
            CheckDutyTypeSwitch();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error incrementing counter: {ex.Message}");
        }
    }

    public void ResetDaily()
    {
        try
        {
            config.DailyCounter = 0;
            config.LastResetDate = DateTime.Today;
            config.Save();
            
            if (statistics != null)
            {
                statistics.TodayDutiesCompleted = 0;
                SaveStatistics();
            }
            
            Service.Log.Info("Daily counter reset");
            Service.Chat.Print("Daily counter reset to 0");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error resetting daily counter: {ex.Message}");
        }
    }

    public void ResetAll()
    {
        try
        {
            config.Reset();
            statistics = new DutyStatistics
            {
                StartDate = DateTime.Now,
                LastUpdated = DateTime.Now
            };
            SaveStatistics();
            
            Service.Log.Info("All progress reset");
            Service.Chat.Print("All progress has been reset");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error resetting all progress: {ex.Message}");
        }
    }

    public DutyStatistics GetStatistics()
    {
        return statistics ?? new DutyStatistics();
    }

    public void UpdateSessionTime()
    {
        try
        {
            if (statistics != null)
            {
                statistics.LastUpdated = DateTime.Now;
                statistics.TotalSessionTime = DateTime.Now - statistics.StartDate;
                SaveStatistics();
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error updating session time: {ex.Message}");
        }
    }

    public void RecordDutyCompletion(DutyType dutyType, TimeSpan completionTime)
    {
        try
        {
            if (statistics != null)
            {
                statistics.TotalDutiesCompleted++;
                statistics.TodayDutiesCompleted = config.DailyCounter;
                
                switch (dutyType)
                {
                    case DutyType.Praetorium:
                        statistics.TotalPraetoriumCompleted++;
                        break;
                    case DutyType.PortaDecumana:
                        statistics.TotalPortaDecumanaCompleted++;
                        break;
                }
                
                // Update average completion time
                var totalCompletionTime = statistics.AverageCompletionTime.TotalSeconds * (statistics.TotalDutiesCompleted - 1) + completionTime.TotalSeconds;
                statistics.AverageCompletionTime = TimeSpan.FromSeconds(totalCompletionTime / statistics.TotalDutiesCompleted);
                
                // Update best completion time
                if (statistics.BestCompletionTime == TimeSpan.Zero || completionTime < statistics.BestCompletionTime)
                {
                    statistics.BestCompletionTime = completionTime;
                }
                
                SaveStatistics();
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error recording duty completion: {ex.Message}");
        }
    }

    public void RecordStuckRecovery()
    {
        try
        {
            if (statistics != null)
            {
                statistics.TotalStuckRecoveries++;
                SaveStatistics();
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error recording stuck recovery: {ex.Message}");
        }
    }

    private void CheckMilestones()
    {
        try
        {
            var milestones = new[] { 10, 25, 50, 75, 100, 150, 200, 300, 500, 750, 1000 };
            
            foreach (var milestone in milestones)
            {
                if (config.DailyCounter == milestone)
                {
                    Service.Chat.Print($"🎉 Milestone reached: {milestone} duties completed!");
                    Service.Log.Info($"Milestone reached: {milestone} duties");
                }
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking milestones: {ex.Message}");
        }
    }

    private void CheckDutyTypeSwitch()
    {
        try
        {
            if (config.DailyCounter >= 99 && config.CurrentDuty == DutyType.Praetorium)
            {
                config.CurrentDuty = DutyType.PortaDecumana;
                config.Save();
                Service.Chat.Print("🔄 Switched to Porta Decumana (99 Praetorium runs completed)");
                Service.Log.Info("Switched to Porta Decumana after 99 Praetorium runs");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking duty type switch: {ex.Message}");
        }
    }

    private void LoadStatistics()
    {
        try
        {
            if (File.Exists(statisticsPath))
            {
                var json = File.ReadAllText(statisticsPath);
                statistics = System.Text.Json.JsonSerializer.Deserialize<DutyStatistics>(json);
                
                // Update session start time if this is a new session
                if (statistics != null && statistics.StartDate.Date != DateTime.Today)
                {
                    statistics.StartDate = DateTime.Now;
                    statistics.TodayDutiesCompleted = 0;
                }
            }
            else
            {
                statistics = new DutyStatistics
                {
                    StartDate = DateTime.Now,
                    LastUpdated = DateTime.Now
                };
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error loading statistics: {ex.Message}");
            statistics = new DutyStatistics
            {
                StartDate = DateTime.Now,
                LastUpdated = DateTime.Now
            };
        }
    }

    private void SaveStatistics()
    {
        try
        {
            if (statistics != null)
            {
                statistics.LastUpdated = DateTime.Now;
                var json = System.Text.Json.JsonSerializer.Serialize(statistics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(statisticsPath, json);
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error saving statistics: {ex.Message}");
        }
    }

    public void ExportStatistics(string filePath)
    {
        try
        {
            if (statistics != null)
            {
                var report = GenerateStatisticsReport();
                File.WriteAllText(filePath, report);
                Service.Log.Info($"Statistics exported to {filePath}");
                Service.Chat.Print($"Statistics exported to {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error exporting statistics: {ex.Message}");
        }
    }

    private string GenerateStatisticsReport()
    {
        try
        {
            if (statistics == null) return "No statistics available";

            var report = new System.Text.StringBuilder();
            
            report.AppendLine("M.O.G.T.O.M.E. Statistics Report");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();
            
            report.AppendLine("Session Information:");
            report.AppendLine($"  Start Date: {statistics.StartDate:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"  Session Duration: {statistics.TotalSessionTime:hh\\:mm\\:ss}");
            report.AppendLine($"  Last Updated: {statistics.LastUpdated:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();
            
            report.AppendLine("Daily Progress:");
            report.AppendLine($"  Today's Progress: {statistics.TodayDutiesCompleted}/{config.DailyTarget}");
            report.AppendLine($"  Progress Percentage: {GetDailyProgressPercentage():F1}%");
            report.AppendLine($"  Last Reset: {config.LastResetDate:yyyy-MM-dd}");
            report.AppendLine();
            
            report.AppendLine("Overall Statistics:");
            report.AppendLine($"  Total Duties Completed: {statistics.TotalDutiesCompleted}");
            report.AppendLine($"  Total Praetorium: {statistics.TotalPraetoriumCompleted}");
            report.AppendLine($"  Total Porta Decumana: {statistics.TotalPortaDecumanaCompleted}");
            report.AppendLine($"  Total Stuck Recoveries: {statistics.TotalStuckRecoveries}");
            report.AppendLine();
            
            report.AppendLine("Performance Metrics:");
            report.AppendLine($"  Average Completion Time: {statistics.AverageCompletionTime:mm\\:ss}");
            report.AppendLine($"  Best Completion Time: {statistics.BestCompletionTime:mm\\:ss}");
            report.AppendLine();
            
            if (statistics.TotalDutiesCompleted > 0)
            {
                var praetoriumPercentage = (double)statistics.TotalPraetoriumCompleted / statistics.TotalDutiesCompleted * 100;
                var portaPercentage = (double)statistics.TotalPortaDecumanaCompleted / statistics.TotalDutiesCompleted * 100;
                
                report.AppendLine("Duty Distribution:");
                report.AppendLine($"  Praetorium: {praetoriumPercentage:F1}%");
                report.AppendLine($"  Porta Decumana: {portaPercentage:F1}%");
            }

            return report.ToString();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error generating statistics report: {ex.Message}");
            return "Error generating report";
        }
    }

    public void Dispose()
    {
        try
        {
            SaveStatistics();
            Service.Log.Info("ProgressTracker disposed");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error disposing ProgressTracker: {ex.Message}");
        }
    }
}

public class DutyStatistics
{
    public DateTime StartDate { get; set; } = DateTime.Now;
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public TimeSpan TotalSessionTime { get; set; }
    public int TotalDutiesCompleted { get; set; }
    public int TodayDutiesCompleted { get; set; }
    public int TotalPraetoriumCompleted { get; set; }
    public int TotalPortaDecumanaCompleted { get; set; }
    public TimeSpan AverageCompletionTime { get; set; }
    public TimeSpan BestCompletionTime { get; set; }
    public int TotalStuckRecoveries { get; set; }
}
