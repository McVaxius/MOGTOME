using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MOGTOME.Localization;
using MOGTOME.UiDesign;
using AethertekUI;

namespace MOGTOME.Windows;

public sealed class BlundervilleWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;

    public BlundervilleWindow(Plugin plugin)
        : base(Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
        Size = new Vector2(399, 630);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 280),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        WindowName = Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville";
        Size = MogtomePresentation.Compact ? new Vector2(399, 611) : new Vector2(375, 630);
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.Appearance.PaintWindowTitle(WindowName); }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        var moogle = plugin.Appearance.OriginalIcon;
        var scale = ImGuiHelpers.GlobalScale;
        var unavailable = new Vector4(1f, .60f, .80f, 1f);
        MaterialIcons.Draw(MaterialIcon.Location, ImGui.GetCursorScreenPos(), 30 * scale, unavailable);
        ImGui.Dummy(new Vector2(30 * scale));
        ImGui.SameLine();
        using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactHeading : MogtomeFontRole.Heading))
            UiLayout.Wrapped(Ui.T("Window_BlundervilleFailer"));
        ImGui.Separator();
        DrawSection(() =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var width = UiLayout.AvailableWidth;
            ImGui.PushID("BlundervilleOpenMogtome");
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            var imageSize = new Vector2(MogtomePresentation.Compact ? 72 : 78) * scale;
            if (ImGui.ImageButton(moogle.Handle, imageSize, Vector4.Zero, Vector4.Zero)) plugin.MainWindow.IsOpen = true;
            MogtomeAppearance.DrawHeaderArt(ImGui.GetItemRectMin(), imageSize);
            ImGui.PopStyleVar();
            ImGui.PopID();
            if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("Blunderville_OpenMogtome"));
            var imageHeight = ImGui.GetItemRectSize().Y;
            ImGui.SetCursorScreenPos(origin + new Vector2(94 * scale, 0));
            ImGui.BeginGroup();
            using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, unavailable);
                UiLayout.Wrapped(Ui.T("Blunderville_FarmingHeading"));
                ImGui.PopStyleColor();
            }
            UiLayout.Wrapped(Ui.T("Blunderville_FarmingDetail"));
            ImGui.EndGroup();
            var textHeight = ImGui.GetItemRectSize().Y;
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, Math.Max(imageHeight, textHeight)));
            if (UiLayout.IconButton(Ui.L("Blunderville_OpenMogtome"), MaterialIcon.ExternalLink, new Vector2(UiLayout.AvailableWidth, (MogtomePresentation.Compact ? 42 : 46) * scale)))
                plugin.MainWindow.IsOpen = true;
        }, minimumHeight: (MogtomePresentation.Compact ? 176 : 196) * scale);
        ImGui.Separator();
        DrawSection(() =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var panelWidth = UiLayout.AvailableWidth;
            var list = ImGui.GetWindowDrawList();
            var ink = MaterialCanvas.Color(unavailable);
            list.AddLine(origin + new Vector2(17 * scale, 1 * scale), origin + new Vector2(1 * scale, 31 * scale), ink, 3 * scale);
            list.AddLine(origin + new Vector2(1 * scale, 31 * scale), origin + new Vector2(33 * scale, 31 * scale), ink, 3 * scale);
            list.AddLine(origin + new Vector2(33 * scale, 31 * scale), origin + new Vector2(17 * scale, 1 * scale), ink, 3 * scale);
            list.AddLine(origin + new Vector2(17 * scale, 11 * scale), origin + new Vector2(17 * scale, 21 * scale), ink, 3 * scale);
            list.AddCircleFilled(origin + new Vector2(17 * scale, 26 * scale), 1.8f * scale, ink, 12);
            ImGui.SetCursorScreenPos(origin + new Vector2(46 * scale, 0));
            ImGui.BeginGroup();
            using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, unavailable);
                UiLayout.Wrapped(Ui.T("Blunderville_PurchasesHeading"));
                ImGui.PopStyleColor();
            }
            UiLayout.Wrapped(Ui.T("Blunderville_PurchasesDetail"));
            ImGui.EndGroup();
            var height = Math.Max(34 * scale, ImGui.GetItemRectSize().Y);
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(panelWidth, height));
            ImGui.BeginDisabled();
            using var font = plugin.Appearance.Font(MogtomeFontRole.Strong);
            var width = (UiLayout.AvailableWidth - ImGui.GetStyle().ItemSpacing.X) / 2;
            var actionHeight = (MogtomePresentation.Compact ? 42 : 46) * scale;
            UiLayout.IconButton(Ui.L("Main_Start"), MaterialIcon.Play, new Vector2(width, actionHeight));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Blunderville_FarmingUnavailable"));
            UiLayout.SameLineIfFits(Math.Max(width, UiLayout.IconButtonWidth(Ui.T("Main_Stop"))));
            UiLayout.IconButton(Ui.L("Main_Stop"), MaterialIcon.Stop, new Vector2(width, actionHeight));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Blunderville_FarmingUnavailable"));
            UiLayout.IconButton(Ui.L("Blunderville_ConfigurePurchases"), MaterialIcon.Settings, new Vector2(UiLayout.AvailableWidth, actionHeight));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Blunderville_PurchasesUnavailable"));
            ImGui.EndDisabled();
        }, minimumHeight: (MogtomePresentation.Compact ? 183 : 201) * scale);
    }

    private static void DrawSection(Action draw, float minimumHeight)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = UiLayout.AvailableWidth;
        ImGui.BeginGroup();
        draw();
        ImGui.EndGroup();
        var height = Math.Max(minimumHeight, ImGui.GetItemRectSize().Y);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
    }
}
