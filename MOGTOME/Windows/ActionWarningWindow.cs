using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MOGTOME.Windows;

public sealed class ActionWarningWindow : Window, IDisposable
{
    private string warningTitle = "MOGTOME Warning";
    private string warningMessage = string.Empty;
    private string? primaryLabel;
    private string dismissLabel = "Dismiss";
    private string? acknowledgeLabel;
    private Action? primaryAction;
    private Action? dismissAction;
    private Action? acknowledgementAction;
    private bool requireExplicitChoice;
    private bool choiceMade;

    public ActionWarningWindow()
        : base("MOGTOME Warning##MOGTOMEActionWarning", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
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

    public void ShowWarning(
        string title,
        string message,
        string? primaryButtonLabel = null,
        Action? onPrimary = null,
        string dismissButtonLabel = "Dismiss",
        Action? onDismiss = null,
        string? acknowledgeButtonLabel = null,
        Action? onAcknowledged = null,
        bool explicitChoiceRequired = false)
    {
        warningTitle = string.IsNullOrWhiteSpace(title) ? "MOGTOME Warning" : title.Trim();
        warningMessage = string.IsNullOrWhiteSpace(message) ? "MOGTOME requires your attention." : message.Trim();
        primaryLabel = primaryButtonLabel;
        primaryAction = onPrimary;
        dismissLabel = string.IsNullOrWhiteSpace(dismissButtonLabel) ? "Dismiss" : dismissButtonLabel;
        dismissAction = onDismiss;
        acknowledgeLabel = acknowledgeButtonLabel;
        acknowledgementAction = onAcknowledged;
        requireExplicitChoice = explicitChoiceRequired;
        choiceMade = false;
        WindowName = $"{warningTitle}##MOGTOMEActionWarning";
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

        ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), warningTitle);
        ImGui.Spacing();
        ImGui.TextWrapped(warningMessage);
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(primaryLabel) && primaryAction != null)
        {
            if (ImGui.Button(primaryLabel, new Vector2(170f, 30f)))
                primaryAction();
            ImGui.SameLine();
        }

        if (!string.IsNullOrWhiteSpace(acknowledgeLabel) && acknowledgementAction != null)
        {
            if (ImGui.Button(acknowledgeLabel, new Vector2(170f, 30f)))
            {
                choiceMade = true;
                IsOpen = false;
                acknowledgementAction();
            }
            ImGui.SameLine();
        }

        if (ImGui.Button(dismissLabel, new Vector2(150f, 30f)))
        {
            choiceMade = true;
            IsOpen = false;
            dismissAction?.Invoke();
        }
    }

    public override void OnClose()
    {
        if (requireExplicitChoice && !choiceMade)
            IsOpen = true;
    }
}
