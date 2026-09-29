using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MOGTOME.Localization;

namespace MOGTOME.Windows;

public sealed class BlundervilleWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public BlundervilleWindow(Plugin plugin)
        : base(Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        Size = new Vector2(440, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 280),
            MaximumSize = new Vector2(700, 650),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        WindowName = Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville";
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.16f, 0.07f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.42f, 0.12f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.65f, 0.18f, 0.40f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.42f, 0.12f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.90f, 0.40f, 0.65f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.65f, 0.18f, 0.40f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.80f, 0.27f, 0.52f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.56f, 0.12f, 0.32f, 1f));
    }

    public override void PostDraw() => ImGui.PopStyleColor(8);

    public override void Draw()
    {
        // The texture provider owns this shared wrap; do not retain or dispose it.
        var moogle = Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, "MOGTOME.images.icon.png").GetWrapOrEmpty();
        ImGui.PushID("BlundervilleOpenMogtome");
        if (ImGui.ImageButton(moogle.Handle, new Vector2(40) * ImGuiHelpers.GlobalScale))
            plugin.MainWindow.IsOpen = true;
        ImGui.PopID();
        if (ImGui.IsItemHovered())
            UiLayout.SetTooltip(Ui.T("Blunderville_OpenMogtome"));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("Blunderville_OpenMogtome"));
        ImGui.Separator();

        ImGui.TextColored(new Vector4(1f, 0.60f, 0.80f, 1f), Ui.T("Window_BlundervilleFailer"));
        ImGui.TextWrapped(Ui.T("Blunderville_FarmingUnavailable"));
        ImGui.BeginDisabled();
        UiLayout.Button(Ui.L("Main_Start"), new Vector2(80, 30));
        ImGui.SameLine();
        UiLayout.Button(Ui.L("Main_Stop"), new Vector2(80, 30));
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped(Ui.T("Blunderville_PurchasesUnavailable"));
        ImGui.BeginDisabled();
        UiLayout.Button(Ui.L("Blunderville_ConfigurePurchases"));
        ImGui.EndDisabled();
    }
}
