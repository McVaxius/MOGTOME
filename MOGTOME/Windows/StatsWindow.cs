using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Windows;

public class StatsWindow : Window, IDisposable
{
    private enum MainTab { Summary, Detailed }
    private enum DetailedSubTab { JobPerformance, PlayerStats, RecentRuns, Trends }

    private readonly Plugin plugin;

    private MainTab currentMainTab = MainTab.Summary;
    private DetailedSubTab currentDetailedTab = DetailedSubTab.JobPerformance;

    public StatsWindow(Plugin plugin)
        : base("MOGTOME - Statistics##MogtomeStats", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 500),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var state = plugin.State;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Duty Statistics");
        ImGui.Separator();

        // Main tab navigation
        if (ImGui.Button("Summary")) currentMainTab = MainTab.Summary;
        ImGui.SameLine();
        if (ImGui.Button("Detailed")) currentMainTab = MainTab.Detailed;

        ImGui.Spacing();

        // Render selected tab
        switch (currentMainTab)
        {
            case MainTab.Summary:
                DrawSummaryTab();
                break;
            case MainTab.Detailed:
                DrawDetailedTab();
                break;
        }
    }

    private void DrawSummaryTab()
    {
        var config = plugin.Configuration;
        var state = plugin.State;

        // Side-by-side stats layout
        if (ImGui.BeginTable("StatsTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Praetorium", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Porta Decumana", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            // Best Time
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyStats("Praetorium", 
                config.PraeBestTime, config.PraeBestTimeDate, config.PraeBestTimeParty,
                config.PraeLongestRun, config.PraeLongestRunDate, config.PraeLongestRunParty,
                config.PraeMostDeathsSelf, config.PraeMostDeathsOthers, config.PraeMostDeathsAll,
                config.PraeTotalDeathsSelf, config.PraeTotalDeathsOthers, config.PraeTotalDeathsAll,
                config.TotalPraes, config.PraeMogtomesEarned);
            
            ImGui.TableSetColumnIndex(1);
            DrawDutyStats("Porta Decumana",
                config.DecuBestTime, config.DecuBestTimeDate, config.DecuBestTimeParty,
                config.DecuLongestRun, config.DecuLongestRunDate, config.DecuLongestRunParty,
                config.DecuMostDeathsSelf, config.DecuMostDeathsOthers, config.DecuMostDeathsAll,
                config.DecuTotalDeathsSelf, config.DecuTotalDeathsOthers, config.DecuTotalDeathsAll,
                config.TotalDecus, config.DecuMogtomesEarned);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // Combined Stats
        if (ImGui.CollapsingHeader("Combined Stats", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"Total Runs: {config.TotalPraes + config.TotalDecus}");
            ImGui.Text($"Total Mogtomes: {config.TotalMogtomesEarned}");
            ImGui.Text($"Current Daily Counter: {state.DutyCounter}");
            ImGui.Text($"Daily Decumana: {state.DecumanaCounter} (Best: {config.AllTimeMaxDailyDecu})");
            
            // Reset time display
            var (countdown, localTime) = plugin.DutyTrackerService.GetResetTimeDisplay();
            ImGui.Text($"Next daily reset: {countdown} ({localTime})");
            
            // Daily Decumana stats (if any runs today)
            if (config.DailyDecuRuns > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.0f, 0.84f, 1.0f, 1.0f), "Today's Decumana Stats:");
                if (config.DailyDecuBestTime < float.MaxValue)
                    ImGui.Text($"  Best Today: {FormatTime(config.DailyDecuBestTime)}");
                if (config.DailyDecuLongestRun > 0)
                    ImGui.Text($"  Longest Today: {FormatTime(config.DailyDecuLongestRun)}");
                ImGui.Text($"  Runs Today: {config.DailyDecuRuns}");
                ImGui.Text($"  Mogtomes Today: {config.DailyDecuMogtomesEarned}");
            }
        }

        ImGui.Spacing();

        // Reset button
        if (ImGui.Button("Reset All Stats"))
        {
            ResetAllStats(config);
        }
    }

    private void DrawDetailedTab()
    {
        // Sub-tab navigation
        if (ImGui.Button("Job Performance")) currentDetailedTab = DetailedSubTab.JobPerformance;
        ImGui.SameLine();
        if (ImGui.Button("Player Stats")) currentDetailedTab = DetailedSubTab.PlayerStats;
        ImGui.SameLine();
        if (ImGui.Button("Recent Runs")) currentDetailedTab = DetailedSubTab.RecentRuns;
        ImGui.SameLine();
        if (ImGui.Button("Trends")) currentDetailedTab = DetailedSubTab.Trends;

        ImGui.Separator();

        // Render selected sub-tab
        switch (currentDetailedTab)
        {
            case DetailedSubTab.JobPerformance:
                DrawJobPerformance();
                break;
            case DetailedSubTab.PlayerStats:
                DrawPlayerStatistics();
                break;
            case DetailedSubTab.RecentRuns:
                DrawRecentRuns();
                break;
            case DetailedSubTab.Trends:
                DrawPerformanceTrends();
                break;
        }
    }

    private void DrawDutyStats(string dutyName, 
        float bestTime, string bestTimeDate, string bestTimeParty,
        float longestRun, string longestRunDate, string longestRunParty,
        int mostDeathsSelf, int mostDeathsOthers, int mostDeathsAll,
        int totalDeathsSelf, int totalDeathsOthers, int totalDeathsAll,
        int totalRuns, int mogtomesEarned)
    {
        ImGui.TextColored(new Vector4(0.0f, 0.84f, 1.0f, 1.0f), dutyName);
        ImGui.Separator();
        
        // Best Time
        if (bestTime < float.MaxValue)
        {
            ImGui.Text($"Best: {FormatTime(bestTime)}");
            ImGui.TextDisabled($"Date: {bestTimeDate}");
            ImGui.TextDisabled($"Party: {bestTimeParty}");
        }
        else
        {
            ImGui.TextDisabled("Best: No runs yet");
        }

        ImGui.Spacing();

        // Longest Run
        if (longestRun > 0)
        {
            ImGui.Text($"Longest: {FormatTime(longestRun)}");
            ImGui.TextDisabled($"Date: {longestRunDate}");
            ImGui.TextDisabled($"Party: {longestRunParty}");
        }
        else
        {
            ImGui.TextDisabled("Longest: No runs yet");
        }

        ImGui.Spacing();

        // Deaths
        ImGui.Text("Deaths (single run):");
        ImGui.TextDisabled($"Self: {mostDeathsSelf} | Others: {mostDeathsOthers} | All: {mostDeathsAll}");
        
        ImGui.Text("Deaths (total):");
        ImGui.TextDisabled($"Self: {totalDeathsSelf} | Others: {totalDeathsOthers} | All: {totalDeathsAll}");

        ImGui.Spacing();

        // Counts
        ImGui.Text($"Runs: {totalRuns}");
        ImGui.Text($"Mogtomes: {mogtomesEarned}");
    }

    private void ResetAllStats(Configuration config)
    {
        // Global stats
        config.BestTimeEver = float.MaxValue;
        config.BestTimeDate = "";
        config.BestTimeParty = "";
        config.LongestRunEver = 0;
        config.LongestRunDate = "";
        config.LongestRunParty = "";
        config.MostDeathsSelf = 0;
        config.MostDeathsOthers = 0;
        config.MostDeathsAll = 0;
        config.TotalDeathsSelf = 0;
        config.TotalDeathsOthers = 0;
        config.TotalDeathsAll = 0;
        
        // Praetorium stats
        config.PraeBestTime = float.MaxValue;
        config.PraeBestTimeDate = "";
        config.PraeBestTimeParty = "";
        config.PraeLongestRun = 0;
        config.PraeLongestRunDate = "";
        config.PraeLongestRunParty = "";
        config.PraeMostDeathsSelf = 0;
        config.PraeMostDeathsOthers = 0;
        config.PraeMostDeathsAll = 0;
        config.PraeTotalDeathsSelf = 0;
        config.PraeTotalDeathsOthers = 0;
        config.PraeTotalDeathsAll = 0;
        config.TotalPraes = 0;
        config.PraeMogtomesEarned = 0;
        
        // Decumana stats
        config.DecuBestTime = float.MaxValue;
        config.DecuBestTimeDate = "";
        config.DecuBestTimeParty = "";
        config.DecuLongestRun = 0;
        config.DecuLongestRunDate = "";
        config.DecuLongestRunParty = "";
        config.DecuMostDeathsSelf = 0;
        config.DecuMostDeathsOthers = 0;
        config.DecuMostDeathsAll = 0;
        config.DecuTotalDeathsSelf = 0;
        config.DecuTotalDeathsOthers = 0;
        config.DecuTotalDeathsAll = 0;
        config.TotalDecus = 0;
        config.DecuMogtomesEarned = 0;
        
        // Daily Decumana stats
        config.DailyDecuRuns = 0;
        config.DailyDecuBestTime = float.MaxValue;
        config.DailyDecuLongestRun = 0;
        config.DailyDecuMogtomesEarned = 0;
        config.MaxDailyDecuRuns = 0;
        config.AllTimeMaxDailyDecu = 0; // Reset all-time record
        config.LastDailyDecuReset = null;
        
        // Reset next reset time (will be recalculated on next check)
        plugin.State.NextResetTime = null;
        
        config.TotalMogtomesEarned = 0;
        config.Save();
    }

    private void DrawPartyTable(bool krangle)
    {
        var party = Plugin.PartyList;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;

        if (party.Length == 0 && localPlayer == null)
        {
            ImGui.TextDisabled("  Not logged in.");
            return;
        }

        if (ImGui.BeginTable("PartyTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            if (party.Length > 0)
            {
                // In a party - show all members
                for (var i = 0; i < party.Length; i++)
                {
                    var member = party[i];
                    if (member == null) continue;

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    var name = member.Name.ToString();
                    if (krangle && !string.IsNullOrEmpty(name))
                        name = KrangleService.KrangleName(name);
                    ImGui.Text(name);

                    ImGui.TableSetColumnIndex(1);
                    var jobAbbr = member.ClassJob.Value.Abbreviation.ToString();
                    ImGui.Text(jobAbbr);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(member.Level.ToString());
                }
            }
            else if (localPlayer != null)
            {
                // Solo - show own name
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                var name = localPlayer.Name.ToString();
                if (krangle && !string.IsNullOrEmpty(name))
                    name = KrangleService.KrangleName(name);
                ImGui.Text(name);

                ImGui.TableSetColumnIndex(1);
                var jobAbbr = localPlayer.ClassJob.Value.Abbreviation.ToString();
                ImGui.Text(jobAbbr);

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(localPlayer.Level.ToString());
            }

            ImGui.EndTable();
        }
    }

    private static string FormatTime(float seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s"
            : $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    public string GetPartyComposition()
    {
        var party = Plugin.PartyList;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (party.Length == 0 && localPlayer == null) return "None";

        var members = new System.Collections.Generic.List<string>();
        
        // Add party members
        for (var i = 0; i < party.Length; i++)
        {
            var member = party[i];
            if (member != null)
            {
                var name = member.Name.ToString();
                var job = member.ClassJob.Value.Abbreviation.ToString();
                var level = member.Level.ToString();
                members.Add($"{name}-{job}-{level}");
            }
        }
        
        // Add local player if solo
        if (party.Length == 0 && localPlayer != null)
        {
            var name = localPlayer.Name.ToString();
            var job = localPlayer.ClassJob.Value.Abbreviation.ToString();
            var level = localPlayer.Level.ToString();
            members.Add($"{name}-{job}-{level}");
        }
        
        return members.Count > 0 ? string.Join(", ", members) : "Unknown";
    }

    private void DrawJobPerformance()
    {
        ImGui.Text("Job Performance");
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.Configuration.RunHistory.Count == 0)
        {
            ImGui.TextDisabled("No run data available. Enable detailed tracking and complete some runs.");
            return;
        }

        var jobStats = plugin.RunHistoryService.GetJobStatistics();
        
        // Create job cards in a grid layout
        int columns = 3;
        int currentColumn = 0;
        
        foreach (var jobStat in jobStats.OrderByDescending(x => x.Value.TotalRuns))
        {
            DrawJobCard(jobStat.Key, jobStat.Value);
            
            currentColumn++;
            if (currentColumn < columns)
            {
                ImGui.SameLine();
            }
            else
            {
                currentColumn = 0;
            }
        }
    }

    private void DrawJobCard(byte jobId, JobStats stats)
    {
        var jobName = GetJobName(jobId);
        var role = GetJobRole(jobId);
        
        ImGui.BeginChild($"JobCard_{jobId}", new Vector2(180, 140), true);
        
        // Job header with role
        ImGui.Text($"{jobName} ({role})");
        ImGui.Separator();
        
        // Stats
        ImGui.Text($"Runs: {stats.TotalRuns}");
        ImGui.Text($"Avg: {FormatTime(stats.AverageTime)}");
        ImGui.Text($"Best: {FormatTime(stats.BestTime)}");
        ImGui.Text($"Deaths: {stats.TotalDeaths}");
        
        // Success rate with color coding
        var successRate = stats.TotalRuns > 0 ? (float)stats.SuccessfulRuns / stats.TotalRuns * 100 : 0f;
        var rateColor = successRate > 95 ? new Vector4(0, 1, 0, 1) : successRate > 90 ? new Vector4(1, 1, 0, 1) : new Vector4(1, 0, 0, 1);
        ImGui.TextColored(rateColor, $"Rate: {successRate:F1}%");
        
        ImGui.Text($"Mogtomes: {stats.TotalMogtomes}");
        
        ImGui.EndChild();
    }

    private void DrawPlayerStatistics()
    {
        ImGui.Text("Player Statistics");
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.Configuration.RunHistory.Count == 0)
        {
            ImGui.TextDisabled("No run data available. Enable detailed tracking and complete some runs.");
            return;
        }

        var playerStats = plugin.RunHistoryService.GetPlayerStatistics();
        
        foreach (var playerStat in playerStats.OrderByDescending(x => x.Value.TotalRuns))
        {
            DrawPlayerCard(playerStat.Key, playerStat.Value);
        }
    }

    private void DrawPlayerCard(ulong playerId, PlayerStats stats)
    {
        var displayName = plugin.Configuration.StatsKrangleNames ? "Player███" : stats.PlayerName;
        if (stats.IsLocalPlayer) displayName += " (You)";
        
        ImGui.BeginChild($"PlayerCard_{playerId}", new Vector2(250, 120), true);
        
        ImGui.Text($"{displayName}");
        if (!plugin.Configuration.StatsKrangleNames)
            ImGui.Text($"({stats.WorldName})");
        
        ImGui.Separator();
        
        ImGui.Text($"Total: {stats.TotalRuns}");
        ImGui.Text($"Prae: {stats.PraetoriumRuns} | Decu: {stats.DecumanaRuns}");
        ImGui.Text($"Avg: {FormatTime(stats.AverageTime)}");
        ImGui.Text($"Best: {FormatTime(stats.BestTime)}");
        ImGui.Text($"Streak: {stats.CurrentStreak} (Best: {stats.BestStreak})");
        ImGui.Text($"Job: {GetJobName(stats.MostPlayedJob)}");
        ImGui.Text($"Mogtomes: {stats.TotalMogtomes}");
        
        ImGui.EndChild();
    }

    private void DrawRecentRuns()
    {
        ImGui.Text("Recent Runs (Last 25)");
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.Configuration.RunHistory.Count == 0)
        {
            ImGui.TextDisabled("No run data available. Enable detailed tracking and complete some runs.");
            return;
        }

        var recentRuns = plugin.RunHistoryService.GetRecentRuns(25);
        
        if (ImGui.BeginTable("RecentRunsTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            // Headers
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Deaths", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();
            
            // Data rows
            foreach (var run in recentRuns)
            {
                ImGui.TableNextRow();
                
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(run.Timestamp.ToString("HH:mm"));
                
                ImGui.TableSetColumnIndex(1);
                var displayName = plugin.Configuration.StatsKrangleNames ? "Player███" : run.PlayerName;
                ImGui.Text(displayName);
                
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(GetJobName(run.JobId));
                
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(run.IsPraetorium ? "Prae" : "Decu");
                
                ImGui.TableSetColumnIndex(4);
                ImGui.Text(FormatTime(run.CompletionTime));
                
                ImGui.TableSetColumnIndex(5);
                ImGui.Text(run.DeathCount.ToString());
                
                ImGui.TableSetColumnIndex(6);
                var statusColor = run.WasSuccessful ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
                ImGui.TextColored(statusColor, run.WasSuccessful ? "Success" : "Failed");
            }
            
            ImGui.EndTable();
        }
    }

    private void DrawPerformanceTrends()
    {
        ImGui.Text("Performance Trends");
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.Configuration.RunHistory.Count == 0)
        {
            ImGui.TextDisabled("No run data available. Enable detailed tracking and complete some runs.");
            return;
        }

        var allRuns = plugin.Configuration.RunHistory;
        var last10 = allRuns.TakeLast(10);
        var last50 = allRuns.TakeLast(50);
        
        // Display metrics
        ImGui.Text($"Average Completion Time (Last 10): {FormatTime(last10.DefaultIfEmpty().Average(x => x.CompletionTime))}");
        ImGui.Text($"Average Completion Time (Last 50): {FormatTime(last50.DefaultIfEmpty().Average(x => x.CompletionTime))}");
        ImGui.Text($"Average Completion Time (All time): {FormatTime(allRuns.Average(x => x.CompletionTime))}");
        
        var deathRate10 = last10.Any() ? (float)last10.Count(x => x.DeathCount > 0) / last10.Count() * 100 : 0;
        var deathRateAll = (float)allRuns.Count(x => x.DeathCount > 0) / allRuns.Count * 100;
        
        ImGui.Text($"Death Rate (Last 10): {deathRate10:F1}%");
        ImGui.Text($"Death Rate (All time): {deathRateAll:F1}%");
        
        // Most efficient job
        var bestJob = allRuns
            .GroupBy(x => x.JobId)
            .Select(g => new { JobId = g.Key, AvgTime = g.Average(x => x.CompletionTime) })
            .OrderBy(x => x.AvgTime)
            .FirstOrDefault();
        
        if (bestJob != null)
        {
            ImGui.Text($"Most Efficient Job: {GetJobName(bestJob.JobId)} ({FormatTime(bestJob.AvgTime)} avg)");
        }
        
        // Recent performance trend
        ImGui.Spacing();
        ImGui.Text("Recent Performance Trend:");
        var recentRuns = allRuns.TakeLast(10).Reverse().ToList();
        for (int i = 0; i < recentRuns.Count; i++)
        {
            var run = recentRuns[i];
            var timeStr = FormatTime(run.CompletionTime);
            var statusStr = run.WasSuccessful ? "✓" : "✗";
            ImGui.Text($"  {run.Timestamp.ToString("MM/dd HH:mm")} - {GetJobName(run.JobId)} - {timeStr} {statusStr}");
        }
    }

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

    private string GetJobRole(byte jobId)
    {
        return jobId switch
        {
            1 or 3 or 20 or 22 or 31 => "Tank",
            6 or 27 or 30 or 32 or 37 => "Healer",
            _ => "DPS"
        };
    }
}
