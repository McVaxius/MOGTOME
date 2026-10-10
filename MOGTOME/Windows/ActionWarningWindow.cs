using MOGTOME.Localization;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;

namespace MOGTOME.Windows;

public sealed class ActionWarningWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly Plugin? plugin;
    private UiText warningTitle = Ui.M("Warning_Title");
    private UiText warningMessage = string.Empty;
    private UiText? primaryLabel;
    private UiText dismissLabel = Ui.M("Warning_Dismiss");
    private UiText? acknowledgeLabel;
    private Action? primaryAction;
    private Func<bool>? primaryAvailability;
    private Action? dismissAction;
    private Action? acknowledgementAction;
    private bool requireExplicitChoice;
    private bool choiceMade;

    public ActionWarningWindow()
        : base(Ui.T("Warning_Title") + "###MOGTOMEActionWarning", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 180f),
            MaximumSize = new Vector2(760f, 500f),
        };
    }

    public void Dispose()
    {
    }

    public ActionWarningWindow(Plugin plugin) : this() => this.plugin = plugin;

    public void ShowWarning(
        UiText title,
        UiText message,
        UiText? primaryButtonLabel = null,
        Action? onPrimary = null,
        UiText? dismissButtonLabel = null,
        Action? onDismiss = null,
        UiText? acknowledgeButtonLabel = null,
        Action? onAcknowledged = null,
        bool explicitChoiceRequired = false,
        Func<bool>? primaryAvailable = null)
    {
        warningTitle = title;
        warningMessage = message;
        primaryLabel = primaryButtonLabel;
        primaryAction = onPrimary;
        primaryAvailability = primaryAvailable;
        dismissLabel = dismissButtonLabel ?? Ui.M("Warning_Dismiss");
        dismissAction = onDismiss;
        acknowledgeLabel = acknowledgeButtonLabel;
        acknowledgementAction = onAcknowledged;
        requireExplicitChoice = explicitChoiceRequired;
        choiceMade = false;
        WindowName = warningTitle.Render() + "###MOGTOMEActionWarning";
        IsOpen = true;
    }

    public override void PreDraw()
    {
        WindowName = warningTitle.Render() + "###MOGTOMEActionWarning";
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 0));
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin?.Appearance.PaintWindowTitle(WindowName); }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        var scale = AethertekUI.MaterialTheme.Metrics.Scale;
        if (ImGui.IsWindowAppearing())
        {
            var viewport = ImGui.GetMainViewport();
            var posX = (viewport.WorkSize.X - 460f) / 2f;
            var posY = (viewport.WorkSize.Y - 220f) / 2f;
            ImGui.SetWindowPos(new Vector2(MathF.Max(1f, posX), MathF.Max(1f, posY)));
        }

        ImGui.PushTextWrapPos(0);
        AethertekUI.MaterialText.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), warningTitle.Render());
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        AethertekUI.MaterialText.TextWrapped(warningMessage.Render());
        ImGui.Spacing();

        if (primaryLabel != null && primaryAction != null && primaryAvailability?.Invoke() != false)
        {
            if (UiLayout.Button(primaryLabel.Render() + "###primaryLabel", new Vector2(170f * scale, 0)))
                primaryAction();
            ImGui.Spacing();
        }

        if (acknowledgeLabel != null && acknowledgementAction != null)
        {
            if (UiLayout.Button(acknowledgeLabel.Render() + "###acknowledgeLabel", new Vector2(170f * scale, 0)))
            {
                choiceMade = true;
                IsOpen = false;
                acknowledgementAction();
            }
            ImGui.Spacing();
        }

        if (UiLayout.Button(dismissLabel.Render() + "###dismissLabel", new Vector2(150f * scale, 0)))
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
