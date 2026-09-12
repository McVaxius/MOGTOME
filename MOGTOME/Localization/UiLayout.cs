using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MOGTOME.Localization;

internal static class UiLayout
{
    internal static bool Button(string label, Vector2 size = default)
    {
        var visible = label.Split("##", 2)[0];
        size.X = MathF.Max(size.X, ImGui.CalcTextSize(visible).X + ImGui.GetStyle().FramePadding.X * 2);
        return ImGui.Button(label, size);
    }

    internal static bool SmallButton(string label) => ImGui.SmallButton(label);

    internal static void SetTooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    internal static void TextDisabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
