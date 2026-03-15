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

        ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Run Statistics");
        ImGui.Separator();

        // Best Time Ever
        if (ImGui.CollapsingHeader("Best Time Ever", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (config.BestTimeEver < float.MaxValue)
            {
                ImGui.Text($"  Time: {FormatTime(config.BestTimeEver)}");
                ImGui.Text($"  Date: {config.BestTimeDate}");
                ImGui.Text($"  Party: {config.BestTimeParty}");
            }
            else
            {
                ImGui.TextDisabled("  No completed runs yet.");
            }
        }

        // Longest Run (non-bailout)
        if (ImGui.CollapsingHeader("Longest Run (non-bailout)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (config.LongestRunEver > 0)
            {
                ImGui.Text($"  Time: {FormatTime(config.LongestRunEver)}");
                ImGui.Text($"  Date: {config.LongestRunDate}");
                ImGui.Text($"  Party: {config.LongestRunParty}");
            }
            else
            {
                ImGui.TextDisabled("  No completed runs yet.");
            }
        }

        // Death Stats
        if (ImGui.CollapsingHeader("Deaths", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text("Most Deaths in a Single Run:");
            ImGui.Text($"  Self: {config.MostDeathsSelf}  |  Others: {config.MostDeathsOthers}  |  All: {config.MostDeathsAll}");
            ImGui.Spacing();
            ImGui.Text("Total Deaths (All Time):");
            ImGui.Text($"  Self: {config.TotalDeathsSelf}  |  Others: {config.TotalDeathsOthers}  |  All: {config.TotalDeathsAll}");
        }

        // Run Counts
        if (ImGui.CollapsingHeader("Run Counts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"  Praetoriums: {config.TotalPraes}");
            ImGui.Text($"  Decumanas: {config.TotalDecus}");
            ImGui.Text($"  Total: {config.TotalPraes + config.TotalDecus}");
        }

        // Mogtomes
        if (ImGui.CollapsingHeader("Mogtomes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Text($"  Total Earned: {config.TotalMogtomesEarned}");
            ImGui.TextDisabled("  (Expanded later with more mogtome types)");
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
                config.TotalPraes = 0;
                config.TotalDecus = 0;
                config.TotalMogtomesEarned = 0;
                config.Save();
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
