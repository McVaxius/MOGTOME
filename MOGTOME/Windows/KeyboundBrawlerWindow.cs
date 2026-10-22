using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MOGTOME.Localization;
using MOGTOME.UiDesign;

namespace MOGTOME.Windows;

public sealed class KeyboundBrawlerWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;

    public KeyboundBrawlerWindow(Plugin plugin) : base(Ui.T("KB_Title") + "###MogtomeKeyboundBrawler")
    {
        this.plugin = plugin;
        Size = new Vector2(600, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(360, 240), MaximumSize = new(1100, 900) };
    }

    public override void PreDraw()
    {
        WindowName = Ui.T("KB_Title") + "###MogtomeKeyboundBrawler";
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw() => windowMotion.Restore(this);
    public override void Draw()
    {
        windowMotion.DrawChrome();
        using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactHeading : MogtomeFontRole.Heading))
            UiLayout.Wrapped(Ui.T("KB_Title"));
        UiLayout.Wrapped(Ui.T("KB_Help"));
        UiLayout.Panel(() =>
        {
            UiLayout.Wrapped(plugin.KeyboundBrawler.Status.Render());
            UiLayout.Wrapped(plugin.KeyboundBrawler.UnavailableReason.Render());
            UiLayout.TextDisabled(Ui.T("KB_Pending"));
        });
        ImGui.BeginDisabled(!plugin.KeyboundBrawler.CanStart);
        if (UiLayout.Button(Ui.L("Main_Start"))) plugin.StartKeyboundBrawler();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiLayout.SetTooltip(plugin.KeyboundBrawler.UnavailableReason.Render());
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Main_Stop")));
        ImGui.BeginDisabled(!plugin.KeyboundBrawler.IsRunning);
        if (UiLayout.Button(Ui.L("Main_Stop"))) plugin.KeyboundBrawler.Stop();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("KB_Idle"));
    }

    public void Dispose() { }
}
