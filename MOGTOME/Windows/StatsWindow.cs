using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MOGTOME.Services;

namespace MOGTOME.Windows;

public class StatsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

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
            ImGui.Text($"Daily Decumana Runs: {state.DecumanaCounter}");
            
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

        ImGui.Separator();

        // Current Party
        if (ImGui.CollapsingHeader("Current Party", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var krangleNames = config.StatsKrangleNames;
            if (ImGui.Checkbox("Krangle Names", ref krangleNames))
            {
                config.StatsKrangleNames = krangleNames;
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Obfuscate player names");

            ImGui.Spacing();
            DrawPartyTable(krangleNames);
        }

        ImGui.Separator();

        // Reset Stats
        if (ImGui.Button("Reset All Stats"))
        {
            ImGui.OpenPopup("ConfirmResetStats");
        }

        if (ImGui.BeginPopup("ConfirmResetStats"))
        {
            ImGui.Text("Are you sure you want to reset ALL stats?");
            if (ImGui.Button("Yes, Reset"))
            {
                ResetAllStats(config);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
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
        config.LastDailyDecuReset = null;
        
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
}
