using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MOGTOME.Windows;

public sealed class ConflictPluginWarningWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool warningAcknowledged;
    private string warningMessage = "Twist of Fayte was detected during a MOGTOME run.";

    public ConflictPluginWarningWindow(Plugin plugin)
        : base("Twist of Fayte Detected##MOGTOMEConflict", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 180f),
            MaximumSize = new Vector2(560f, 380f),
        };
    }

    public void Dispose()
    {
    }

    public void ShowWarning(string message)
    {
        warningMessage = string.IsNullOrWhiteSpace(message)
            ? "Twist of Fayte was detected during a MOGTOME run."
            : message.Trim();
        warningAcknowledged = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing())
        {
            var viewport = ImGui.GetMainViewport();
            var posX = (viewport.WorkSize.X - 460f) / 2f;
            var posY = (viewport.WorkSize.Y - 220f) / 2f;
            ImGui.SetWindowPos(new Vector2(MathF.Max(1f, posX), MathF.Max(1f, posY)));
        }

        var status = plugin.ConflictPluginService.GetTwistOfFayteStatus();
        var statusColor = status.IsLoaded
            ? new Vector4(1f, 0.35f, 0.35f, 1f)
            : status.IsInstalled
                ? new Vector4(0.45f, 0.9f, 0.55f, 1f)
                : new Vector4(0.7f, 0.7f, 0.7f, 1f);
        var statusText = status.IsLoaded
            ? "Current status: still enabled"
            : status.IsInstalled
                ? "Current status: installed but disabled"
                : "Current status: not installed";

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "Twist of Fayte conflict");
        ImGui.Spacing();
        ImGui.TextWrapped(warningMessage);
        ImGui.Spacing();
        ImGui.TextColored(statusColor, statusText);
        ImGui.TextDisabled("This warning does not stop MOGTOME. It only keeps nagging until you click a button.");
        ImGui.Spacing();

        if (ImGui.Button("Disable TwistOfFayte", new Vector2(170f, 30f)))
            _ = plugin.ConflictPluginService.EnsureTwistOfFayteDisabledAsync("Warning window", showPopup: false);

        ImGui.SameLine();
        if (ImGui.Button("Dismiss Warning", new Vector2(150f, 30f)))
        {
            warningAcknowledged = true;
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        if (!warningAcknowledged)
            IsOpen = true;
    }
}
