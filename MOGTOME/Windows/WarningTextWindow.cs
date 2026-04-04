using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MOGTOME.Windows;

public sealed class WarningTextWindow : Window, IDisposable
{
    public const int CurrentWarningVersion = 0;

    private static readonly string[] WarningLines =
    [
        "Sorry to make you read this.",
        "-",
        "A few notes on MOGTOME version 0.0.0.x (not 1.x.x.x yet).",
        "it's experimental and will break occasionally.",
        "please provide logs so i can analyze !",
        "-",
        "Multiplayer guide, discord link and info at:",
        "-",
		"https://aethertek.io/",
        "-",
        "There are 3 ways MOGTOME is currently crashing clients. (its not mogtome)",
        "(1) If you have RSR -> UI -> Simulate the effect of pressing abilities? Turn that feature off.",
        "-",
        "(2) Do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "AUTODUTY/RSR/navmesh sometimes WILL have race/deadlock and CTD with(out) error.",
        "You will need to use multi install folders. to resolve this",
        "It will be the first thing I ask after what version of MOGTOME is it.",
        "-",
        "(3) if you don't have very much RAM. you will have some issues with many clients",
        "plan for 5GB free per client you want to run before loading any clients",
        "see the multiplayer guide on https://aethertek.io/  for some tips and tricks",
        "---------------------------------------------------------------------------------------------------",
        "-",
        "Now for some tips and tricks for MOGTOME.",
        "-",
        "1. If you are self repairing.  Set the % to max in AD . for some reason typing /ad repair for npc",
		"repair will always go and try to repair, but for self repair it won't always do it.",
        "-",
		"2. Make an AD profile just for this purpose and pick it before you hit start.  MOGTOME changes some",
		"settings so this is good idea to keep yoru leveling etc profiles safe", 
        "-",
		"3. Join the AutoParty discord -> https://discord.gg/KyfyAzG6", 
        "-",
        "4. When starting mogtome, make sure you start the non party leader first.  and you will have to",
		"configure WHO the party leader is by clicking refresh status on the party leader.",
        "I did this because its a seriously annoying logic puzzle to figure out who is actually the party leader.",
		"I have some commented out and retired methods for it but it was unreliable.",
    ];

    private readonly Plugin plugin;
    private bool warningAcknowledgedThisOpen;

    public WarningTextWindow(Plugin plugin)
        : base("MOGTOME Warning Text##MOGTOMEWarningText", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560f, 360f),
            MaximumSize = new Vector2(760f, 560f),
        };
    }

    public void Dispose()
    {
    }

    public void Show(bool force = false)
    {
        if (!force && !NeedsAcknowledgement())
            return;

        warningAcknowledgedThisOpen = false;
        IsOpen = true;
    }

    public void ShowIfNeeded()
    {
        if (!NeedsAcknowledgement())
            return;

        warningAcknowledgedThisOpen = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing())
        {
            var viewport = ImGui.GetMainViewport();
            var posX = (viewport.WorkSize.X - 600f) / 2f;
            var posY = (viewport.WorkSize.Y - 420f) / 2f;
            ImGui.SetWindowPos(new Vector2(MathF.Max(1f, posX), MathF.Max(1f, posY)));
        }

        ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.2f, 1.0f), "Read This Before Running MOGTOME");
        ImGui.SameLine();
        ImGui.TextDisabled($"warning v{CurrentWarningVersion}");
        ImGui.Spacing();

        foreach (var line in WarningLines)
        {
            ImGui.TextWrapped(line);
            ImGui.Spacing();
        }

        var buttonWidth = MathF.Max(260f, ImGui.GetContentRegionAvail().X);
        if (ImGui.Button("OK I READ IT", new Vector2(buttonWidth, 42f)))
        {
            plugin.Configuration.WarningPopupAcknowledgedVersion = CurrentWarningVersion;
            plugin.ConfigManager.SaveCurrentAccount();
            warningAcknowledgedThisOpen = true;
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        if (!warningAcknowledgedThisOpen && NeedsAcknowledgement())
            IsOpen = true;
    }

    private bool NeedsAcknowledgement()
        => plugin.Configuration.WarningPopupAcknowledgedVersion < CurrentWarningVersion;
}
