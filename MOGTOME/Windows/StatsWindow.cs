using MOGTOME.Localization;
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
    private Vector2? pendingWindowPosition;
    private bool pendingPositionConditionReset;

    private MainTab currentMainTab = MainTab.Summary;
    private DetailedSubTab currentDetailedTab = DetailedSubTab.JobPerformance;
    private DateTime lastRefresh = DateTime.MinValue;
    private const int REFRESH_INTERVAL_SECONDS = 10;

    public StatsWindow(Plugin plugin)
        : base(Ui.T("Window_MOGTOMEStatistics") + "###MogtomeStats", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 500),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public void Dispose() { }

    public void QueueResetToOrigin()
        => QueueWindowPosition(new Vector2(1f, 1f));

    public void QueueRandomVisibleJump()
        => QueueWindowPosition(GetRandomVisiblePosition());

    public override void PreDraw()
    {
        WindowName = Ui.T("Window_MOGTOMEStatistics") + "###MogtomeStats";
        if (pendingWindowPosition.HasValue)
        {
            Position = pendingWindowPosition.Value;
            PositionCondition = ImGuiCond.Always;
            pendingWindowPosition = null;
            pendingPositionConditionReset = true;
        }
    }

    public override void Draw()
    {
        // Auto-refresh every 10 seconds
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(REFRESH_INTERVAL_SECONDS))
        {
            try
            {
                plugin.RunHistoryService.LoadRunHistoryFromDatabase(bypassValidation: true);
                lastRefresh = DateTime.Now;
                Plugin.Log.Debug("[StatsWindow] Auto-refreshed data from database");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[StatsWindow] Failed to auto-refresh data");
            }
        }

        var config = plugin.Configuration;
        var state = plugin.State;

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), Ui.T("Stats_DutyStatistics"));
        ImGui.SameLine(ImGui.GetWindowWidth() - 240);
        if (UiLayout.Button(Ui.L("Stats_OpenConfig"), new Vector2(100, 0)))
        {
            try
            {
                var configPath = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "MOGTOME");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = configPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to open config folder: {ex.Message}");
            }
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Stats_OpenMOGTOMEConfigurationFolder"));
        }
        ImGui.SameLine();
        var krangleEnabled = plugin.Configuration.KrangleNames;
        var krangleText = krangleEnabled ? Ui.T("Main_UnKrangle") : Ui.T("Stats_KrangleNames");
        if (UiLayout.Button(krangleText + "###Krangle", new Vector2(120, 0)))
        {
            plugin.Configuration.KrangleNames = !krangleEnabled;
            plugin.ConfigManager.SaveCurrentAccount();
            KrangleService.ClearCache();
        }
        if (ImGui.IsItemHovered())
        {
            UiLayout.SetTooltip(Ui.T("Main_ObfuscateNamesWithMilitaryExerciseWordsUseful"));
        }
        ImGui.Separator();

        // Main tab navigation
        if (UiLayout.Button(Ui.L("Stats_Summary"))) currentMainTab = MainTab.Summary;
        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Stats_Detailed"))) currentMainTab = MainTab.Detailed;

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

        FinalizePendingWindowPlacement();
    }

    private void DrawSummaryTab()
    {
        var config = plugin.Configuration;
        var state = plugin.State;

        // Debug checkbox (only visible when debug mode is enabled)
        if (config.DebugModeEnabled)
        {
            var showDebugRuns = config.ShowDebugRuns;
            if (ImGui.Checkbox(Ui.L("Stats_ShowDebugRuns"), ref showDebugRuns))
            {
                config.ShowDebugRuns = showDebugRuns;
                plugin.ConfigManager.SaveCurrentAccount();
                plugin.RunHistoryService.LoadRunHistoryFromDatabase(bypassValidation: true);
                lastRefresh = DateTime.Now;
            }
            if (showDebugRuns)
            {
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), Ui.T("Stats_UnsyncedRunsAreNowIncludedInStatistics"));
            }
            else
            {
                UiLayout.TextDisabled(Ui.T("Stats_UnsyncedRunsAreHiddenFromStatistics"));
            }
            ImGui.Spacing();
        }

        // Side-by-side stats layout
        if (ImGui.BeginTable("StatsTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(Ui.Duty(1044).Render(), ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn(Ui.Duty(1048).Render(), ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            // Best Time
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawDutyStats(Ui.Duty(1044).Render(),
                config.PraeBestTime, config.PraeBestTimeDate, config.PraeBestTimeParty,
                config.PraeLongestRun, config.PraeLongestRunDate, config.PraeLongestRunParty,
                config.PraeMostDeathsSelf, config.PraeMostDeathsOthers, config.PraeMostDeathsAll,
                config.PraeTotalDeathsSelf, config.PraeTotalDeathsOthers, config.PraeTotalDeathsAll,
                config.TotalPraes, config.PraeMogtomesEarned);
            
            ImGui.TableSetColumnIndex(1);
            DrawDutyStats(Ui.Duty(1048).Render(),
                config.DecuBestTime, config.DecuBestTimeDate, config.DecuBestTimeParty,
                config.DecuLongestRun, config.DecuLongestRunDate, config.DecuLongestRunParty,
                config.DecuMostDeathsSelf, config.DecuMostDeathsOthers, config.DecuMostDeathsAll,
                config.DecuTotalDeathsSelf, config.DecuTotalDeathsOthers, config.DecuTotalDeathsAll,
                config.TotalDecus, config.DecuMogtomesEarned);

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // Combined Stats
        if (ImGui.CollapsingHeader(Ui.L("Stats_CombinedStats"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text(Ui.T("Stats_TotalRuns", config.TotalPraes + config.TotalDecus));
            ImGui.Text(Ui.T("Stats_TotalMogtomes", config.TotalMogtomesEarned));
            ImGui.Text(Ui.T("Stats_CurrentDailyCounter", state.DutyCounter));
            ImGui.Text(Ui.T("Stats_DailyDecumanaBest", state.DecumanaCounter, config.AllTimeMaxDailyDecu));
            
            // Reset time display
            var (countdown, localTime) = plugin.DutyTrackerService.GetResetTimeDisplay();
            ImGui.Text(Ui.T("Stats_NextDailyReset", countdown, localTime));
            
            // Daily Decumana stats (if any runs today)
            if (config.DailyDecuRuns > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.0f, 0.84f, 1.0f, 1.0f), Ui.T("Stats_TodaySDecumanaStats"));
                if (config.DailyDecuBestTime < float.MaxValue)
                    ImGui.Text(Ui.T("Stats_BestToday", FormatTime(config.DailyDecuBestTime)));
                if (config.DailyDecuLongestRun > 0)
                    ImGui.Text(Ui.T("Stats_LongestToday", FormatTime(config.DailyDecuLongestRun)));
                ImGui.Text(Ui.T("Stats_RunsToday", config.DailyDecuRuns));
                ImGui.Text(Ui.T("Stats_MogtomesToday", config.DailyDecuMogtomesEarned));
            }
        }

        ImGui.Spacing();

        // Reset button
        if (UiLayout.Button(Ui.L("Stats_ResetAllStats")))
        {
            ResetAllStats(config);
        }
    }

    private void DrawDetailedTab()
    {
        // Sub-tab navigation
        if (UiLayout.Button(Ui.L("Stats_JobPerformance"))) currentDetailedTab = DetailedSubTab.JobPerformance;
        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Stats_PlayerStats"))) currentDetailedTab = DetailedSubTab.PlayerStats;
        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Stats_RecentRuns"))) currentDetailedTab = DetailedSubTab.RecentRuns;
        ImGui.SameLine();
        if (UiLayout.Button(Ui.L("Stats_Trends"))) currentDetailedTab = DetailedSubTab.Trends;

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
            ImGui.Text(Ui.T("Stats_Best", FormatTime(bestTime)));
            UiLayout.TextDisabled(Ui.T("Stats_Date", bestTimeDate));
            
            // Party display - multi-line formatting
            if (!string.IsNullOrEmpty(bestTimeParty))
            {
                UiLayout.TextDisabled(Ui.T("Stats_Party"));
                var partyMembers = bestTimeParty.Split(", ");
                foreach (var member in partyMembers)
                {
                    var trimmedMember = member.Trim();
                    // Parse "Name - Job - Level" format
                    var parts = trimmedMember.Split(" - ");
                    if (parts.Length >= 3)
                    {
                        var name = parts[0];
                        var job = parts[1];
                        var level = parts[2];
                        
                        // Only krangle the name, not job/level
                        var krangledName = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(name) : name;
                        UiLayout.TextDisabled(string.Create(Ui.Culture, $"  {krangledName} - {job} - {level}"));
                    }
                    else
                    {
                        // Fallback for unexpected format
                        var krangledMember = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(trimmedMember) : trimmedMember;
                        UiLayout.TextDisabled(string.Create(Ui.Culture, $"  {krangledMember}"));
                    }
                }
            }
        }
        else
        {
            UiLayout.TextDisabled(Ui.T("Stats_BestNoRunsYet"));
        }

        ImGui.Spacing();

        // Longest Run
        if (longestRun > 0)
        {
            ImGui.Text(Ui.T("Stats_Longest", FormatTime(longestRun)));
            UiLayout.TextDisabled(Ui.T("Stats_Date", longestRunDate));
            
            // Party display - multi-line formatting
            if (!string.IsNullOrEmpty(longestRunParty))
            {
                UiLayout.TextDisabled(Ui.T("Stats_Party"));
                var partyMembers = longestRunParty.Split(", ");
                foreach (var member in partyMembers)
                {
                    var trimmedMember = member.Trim();
                    // Parse "Name - Job - Level" format
                    var parts = trimmedMember.Split(" - ");
                    if (parts.Length >= 3)
                    {
                        var name = parts[0];
                        var job = parts[1];
                        var level = parts[2];
                        
                        // Only krangle the name, not job/level
                        var krangledName = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(name) : name;
                        UiLayout.TextDisabled(string.Create(Ui.Culture, $"  {krangledName} - {job} - {level}"));
                    }
                    else
                    {
                        // Fallback for unexpected format
                        var krangledMember = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(trimmedMember) : trimmedMember;
                        UiLayout.TextDisabled(string.Create(Ui.Culture, $"  {krangledMember}"));
                    }
                }
            }
        }
        else
        {
            UiLayout.TextDisabled(Ui.T("Stats_LongestNoRunsYet"));
        }

        ImGui.Spacing();

        // Deaths
        ImGui.Text(Ui.T("Stats_DeathsSingleRun"));
        UiLayout.TextDisabled(Ui.T("Stats_SelfOthersAll", mostDeathsSelf, mostDeathsOthers, mostDeathsAll));
        
        ImGui.Text(Ui.T("Stats_DeathsTotal"));
        UiLayout.TextDisabled(Ui.T("Stats_SelfOthersAll", totalDeathsSelf, totalDeathsOthers, totalDeathsAll));

        ImGui.Spacing();

        // Counts
        ImGui.Text(Ui.T("Stats_Runs", totalRuns));
        ImGui.Text(Ui.T("Stats_Mogtomes", mogtomesEarned));
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
        
        // Clear run history - THIS WAS MISSING!
        plugin.RunHistoryService.ClearRunHistory();
        
        config.TotalMogtomesEarned = 0;
        plugin.ConfigManager.SaveCurrentAccount();
    }

    private void DrawPartyTable(bool krangle)
    {
        var party = Plugin.PartyList;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;

        if (party.Length == 0 && localPlayer == null)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NotLoggedIn"));
            return;
        }

        if (ImGui.BeginTable("PartyTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(Ui.T("Stats_Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Ui.T("Stats_Job"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(Ui.T("Stats_Level"), ImGuiTableColumnFlags.WidthFixed, 50);
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
                    var jobAbbr = Ui.Job(member.ClassJob.RowId).Render();
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
                var jobAbbr = Ui.Job(localPlayer.ClassJob.RowId).Render();
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
            ? Ui.T("Stats_HMS", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : Ui.T("Stats_MS", ts.Minutes, ts.Seconds);
    }

    private static int GetTotalDeaths(RunRecord run)
    {
        var splitTotal = run.SelfDeathCount + run.OtherDeathCount;
        return run.DeathCount > 0 ? run.DeathCount : splitTotal;
    }

    public string GetPartyComposition()
    {
        var party = Plugin.PartyList;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (party.Length == 0 && localPlayer == null) return Ui.T("Stats_None");

        var members = new System.Collections.Generic.List<string>();
        
        // Add party members
        for (var i = 0; i < party.Length; i++)
        {
            var member = party[i];
            if (member != null)
            {
                var name = member.Name.ToString();
                if (plugin.Configuration.KrangleNames && !string.IsNullOrEmpty(name))
                    name = KrangleService.KrangleName(name);
                var job = Ui.Job(member.ClassJob.RowId).Render();
                var level = member.Level.ToString();
                members.Add(string.Create(Ui.Culture, $"{name}-{job}-{level}"));
            }
        }
        
        // Add local player if solo
        if (party.Length == 0 && localPlayer != null)
        {
            var name = localPlayer.Name.ToString();
            if (plugin.Configuration.KrangleNames && !string.IsNullOrEmpty(name))
                name = KrangleService.KrangleName(name);
            var job = Ui.Job(localPlayer.ClassJob.RowId).Render();
            var level = localPlayer.Level.ToString();
            members.Add(string.Create(Ui.Culture, $"{name}-{job}-{level}"));
        }
        
        return members.Count > 0 ? string.Join(", ", members) : Ui.T("Time_Unknown");
    }

    private void DrawJobPerformance()
    {
        ImGui.Text(Ui.T("Stats_JobPerformance"));
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.RunHistoryService.RunHistory.Count == 0)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NoRunDataAvailableEnableDetailedTracking"));
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
        ImGui.Text(string.Create(Ui.Culture, $"{jobName} ({role})"));
        ImGui.Separator();
        
        // Stats
        ImGui.Text(Ui.T("Stats_Runs", stats.TotalRuns));
        ImGui.Text(Ui.T("Stats_Avg", FormatTime(stats.AverageTime)));
        ImGui.Text(Ui.T("Stats_Best", FormatTime(stats.BestTime)));
        ImGui.Text(Ui.T("Stats_Deaths", stats.TotalDeaths));
        
        // Success rate with color coding
        var successRate = stats.TotalRuns > 0 ? (float)stats.SuccessfulRuns / stats.TotalRuns * 100 : 0f;
        var rateColor = successRate > 95 ? new Vector4(0, 1, 0, 1) : successRate > 90 ? new Vector4(1, 1, 0, 1) : new Vector4(1, 0, 0, 1);
        ImGui.TextColored(rateColor, Ui.T("Stats_Rate", successRate));
        
        ImGui.Text(Ui.T("Stats_Mogtomes", stats.TotalMogtomes));
        
        ImGui.EndChild();
    }

    private void DrawPlayerStatistics()
    {
        ImGui.Text(Ui.T("Stats_PlayerStatistics"));
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.RunHistoryService.RunHistory.Count == 0)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NoRunDataAvailableEnableDetailedTracking"));
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
        var displayName = plugin.Configuration.StatsKrangleNames ? Ui.T("Stats_Player") : stats.PlayerName;
        if (stats.IsLocalPlayer) displayName += Ui.T("Stats_You");
        
        ImGui.BeginChild($"PlayerCard_{playerId}", new Vector2(250, 120), true);
        
        ImGui.Text(string.Create(Ui.Culture, $"{displayName}"));
        if (!plugin.Configuration.StatsKrangleNames)
            ImGui.Text(string.Create(Ui.Culture, $"({stats.WorldName})"));
        
        ImGui.Separator();
        
        ImGui.Text(Ui.T("Stats_Total", stats.TotalRuns));
        ImGui.Text(Ui.T("Stats_PraeDecu", stats.PraetoriumRuns, stats.DecumanaRuns));
        ImGui.Text(Ui.T("Stats_Avg", FormatTime(stats.AverageTime)));
        ImGui.Text(Ui.T("Stats_Best", FormatTime(stats.BestTime)));
        ImGui.Text(Ui.T("Stats_StreakBest", stats.CurrentStreak, stats.BestStreak));
        ImGui.Text(Ui.T("Stats_Job2", GetJobName(stats.MostPlayedJob)));
        ImGui.Text(Ui.T("Stats_Mogtomes", stats.TotalMogtomes));
        
        ImGui.EndChild();
    }

    private void DrawRecentRuns()
    {
        ImGui.Text(Ui.T("Stats_RecentRunsLast"));
        ImGui.Separator();
        
        // Auto-refresh every 10 seconds if window is open
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(REFRESH_INTERVAL_SECONDS))
        {
            try
            {
                plugin.RunHistoryService.LoadRunHistoryFromDatabase(bypassValidation: true);
                lastRefresh = DateTime.Now;
                Plugin.Log.Debug("[StatsWindow] Auto-refreshed run history");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[StatsWindow] Failed to auto-refresh run history");
            }
        }
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.RunHistoryService.RunHistory.Count == 0)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NoRunDataAvailableEnableDetailedTracking"));
            return;
        }

        var recentRuns = plugin.RunHistoryService.GetRecentRuns(25);
        
        if (ImGui.BeginTable("RecentRunsTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            // Headers
            ImGui.TableSetupColumn(Ui.T("Stats_Time"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(Ui.T("Stats_Player2"), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn(Ui.T("Stats_Job"), ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn(Ui.T("Config_Duty"), ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn(Ui.T("Stats_Time"), ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn(Ui.T("Config_Party"), ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn(Ui.T("Stats_Deaths2"), ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn(Ui.T("Stats_Status"), ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableHeadersRow();
            
            // Data rows
            foreach (var run in recentRuns)
            {
                ImGui.TableNextRow();
                
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(run.Timestamp.ToString("HH:mm", Ui.Culture));
                
                ImGui.TableSetColumnIndex(1);
                var displayName = plugin.Configuration.StatsKrangleNames ? Ui.T("Stats_Player") : run.PlayerName;
                ImGui.Text(displayName);
                
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(GetJobName(run.JobId));
                
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(Ui.Duty(run.TerritoryId).Render());
                
                ImGui.TableSetColumnIndex(4);
                ImGui.Text(FormatTime(run.CompletionTime));
                
                ImGui.TableSetColumnIndex(5);
                // Show party size and members on separate lines
                var partyMembers = plugin.RunHistoryService.GetPartyMembersForRun(run);
                if (partyMembers.Count > 0)
                {
                    ImGui.Text(string.Create(Ui.Culture, $"{partyMembers.Count}:"));
                    foreach (var member in partyMembers)
                    {
                        var displayMember = plugin.Configuration.StatsKrangleNames ? 
                            KrangleService.KrangleName(member) : member;
                        ImGui.Text(string.Create(Ui.Culture, $"   {displayMember}"));
                    }
                }
                else
                {
                    var fallbackPartyText = run.PartySize > 0
                        ? Ui.T("Stats_NotCaptured", run.PartySize)
                        : Ui.T("Stats_PartyDataNotCaptured");
                    ImGui.Text(fallbackPartyText);
                }
                
                ImGui.TableSetColumnIndex(6);
                ImGui.Text(string.Create(Ui.Culture, $"{run.SelfDeathCount}/{run.OtherDeathCount}/{GetTotalDeaths(run)}"));
                if (ImGui.IsItemHovered())
                    UiLayout.SetTooltip(Ui.T("Stats_SelfOthersAll2"));
                
                ImGui.TableSetColumnIndex(7);
                var successful = RunHistoryService.IsSuccessful(run);
                var statusColor = successful ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
                ImGui.TextColored(statusColor, successful
                    ? Ui.T("Stats_Success")
                    : Ui.T("Stats_Aborted", (string.IsNullOrWhiteSpace(run.AbortReason) ? Ui.T("Stats_NoReasonRecorded") : run.AbortReason)));
            }
            
            ImGui.EndTable();
        }
    }

    private void DrawPerformanceTrends()
    {
        ImGui.Text(Ui.T("Stats_PerformanceTrends"));
        ImGui.Separator();
        
        if (!plugin.Configuration.EnableDetailedTracking || plugin.RunHistoryService.RunHistory.Count == 0)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NoRunDataAvailableEnableDetailedTracking"));
            return;
        }

        var allRuns = plugin.RunHistoryService.SuccessfulRunHistory;
        if (allRuns.Count == 0)
        {
            UiLayout.TextDisabled(Ui.T("Stats_NoSuccessfulRunDataAvailable"));
            return;
        }
        var last10 = allRuns.TakeLast(10);
        var last50 = allRuns.TakeLast(50);
        
        // Display metrics
        ImGui.Text(Ui.T("Stats_AverageCompletionTimeLast", FormatTime(last10.DefaultIfEmpty().Average(x => x?.CompletionTime ?? 0f))));
        ImGui.Text(Ui.T("Stats_AverageCompletionTimeLast2", FormatTime(last50.DefaultIfEmpty().Average(x => x?.CompletionTime ?? 0f))));
        ImGui.Text(Ui.T("Stats_AverageCompletionTimeAllTime", FormatTime(allRuns.Average(x => x?.CompletionTime ?? 0f))));
        
        var deathRate10 = last10.Any() ? (float)last10.Count(x => GetTotalDeaths(x) > 0) / last10.Count() * 100 : 0;
        var deathRateAll = (float)allRuns.Count(x => GetTotalDeaths(x) > 0) / allRuns.Count * 100;
        
        ImGui.Text(Ui.T("Stats_DeathRateLast", deathRate10));
        ImGui.Text(Ui.T("Stats_DeathRateAllTime", deathRateAll));
        
        // Most efficient job
        var bestJob = allRuns
            .GroupBy(x => x.JobId)
            .Select(g => new { JobId = g.Key, AvgTime = g.Average(x => x.CompletionTime) })
            .OrderBy(x => x.AvgTime)
            .FirstOrDefault();
        
        if (bestJob != null)
        {
            ImGui.Text(Ui.T("Stats_MostEfficientJobAvg", GetJobName(bestJob.JobId), FormatTime(bestJob.AvgTime)));
        }
        
        // Recent performance trend
        ImGui.Spacing();
        ImGui.Text(Ui.T("Stats_RecentPerformanceTrend"));
        var recentRuns = allRuns.TakeLast(10).Reverse().ToList();
        for (int i = 0; i < recentRuns.Count; i++)
        {
            var run = recentRuns[i];
            var timeStr = FormatTime(run.CompletionTime);
            ImGui.Text(string.Create(Ui.Culture, $"  {run.Timestamp:MM/dd HH:mm} - {GetJobName(run.JobId)} - {timeStr}"));
        }
    }

    private string GetJobName(byte jobId) => Ui.Job(jobId).Render();

    private string GetJobRole(byte jobId)
    {
        var role = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(Ui.SheetLanguage).GetRowOrDefault(jobId)?.Role;
        return role switch { 1 => Ui.T("Role_Tank"), 4 => Ui.T("Role_Healer"), _ => Ui.T("Role_Dps") };
    }

    private void QueueWindowPosition(Vector2 position)
    {
        pendingWindowPosition = position;
    }

    private Vector2 GetRandomVisiblePosition()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var currentSize = Size ?? Vector2.Zero;
        var minimumSize = SizeConstraints?.MinimumSize ?? Vector2.Zero;
        var width = MathF.Max(currentSize.X, minimumSize.X);
        var height = MathF.Max(currentSize.Y, minimumSize.Y);
        var maxX = MathF.Max(1f, viewport.Size.X - width - 20f);
        var maxY = MathF.Max(1f, viewport.Size.Y - height - 20f);
        return new Vector2(1f + (Random.Shared.NextSingle() * maxX), 1f + (Random.Shared.NextSingle() * maxY));
    }

    private void FinalizePendingWindowPlacement()
    {
        if (!pendingPositionConditionReset)
            return;

        pendingPositionConditionReset = false;
        Position = null;
        PositionCondition = ImGuiCond.None;
    }
}
