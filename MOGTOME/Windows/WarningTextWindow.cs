using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MOGTOME.Windows;

public sealed class WarningTextWindow : Window, IDisposable
{
    public const int CurrentWarningVersion = 2;

    private static readonly string[] WarningLines =
    [
        "A few notes on MOGTOME version 0.0.0.x (not 1.x.x.x yet).",
        "its experimental and will explode occasionally.",
        "please provide logs so i can analyze the debris.",
        "Also if you have RSR -> UI -> Simulate the effet of pressing abilities.",
        "Turn that feature off.",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "also do not run dalamud from the same folder multiple times. it gets crashy when multiboxing",
        "AUTODUTY, RSR and navmesh sometimes will not like that and exit out.",
        "You will need to use multi install folders.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "there is no good way around it until i figure out a way to separate every instance of the game by account on the fly.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
        "im sorry for this. Did I forget to mention not to run dalamud from same folder multiple times? ok great.",
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
