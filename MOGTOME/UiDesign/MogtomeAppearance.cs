using System;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using MOGTOME.Localization;

namespace MOGTOME.UiDesign;

internal sealed class MogtomeAppearance : IDisposable
{
    private readonly System.Collections.Generic.Dictionary<Dalamud.Interface.Windowing.IWindow, AethertekUI.MaterialWindowOpacity> windowOpacities = new();
    private readonly AethertekUI.MaterialWindowOpacity fontStatusOpacity = new();
    private readonly Plugin plugin;
    private readonly AethertekUI.Dalamud.MaterialTextHost shapedText;
    private static readonly string[] NativeLanguages = ["English", "Français", "Deutsch", "日本語", "Español", "Italiano", "Русский", "한국어", "简体中文", "Tiếng Việt", "Português (Brasil)", "Bahasa Indonesia", "Polski", "Türkçe", "हिन्दी"];
    private MogtomeFonts? fonts;
    private MaterialTheme? theme;
    private readonly MaterialWindowFold fontStatusFold = new();
    private readonly MaterialWindowDecorations fontStatusDecorations = new();
    private string appliedLanguage = "";
    private uint appliedAccent;
    private Vector3 accentDraft;
    private string[] required = [];
    private int checkedGeneration = -1;
    private bool fontIssueLogged;
    internal MogtomeAppearance(Plugin plugin)
    {
        this.plugin = plugin;
        shapedText = new(Plugin.TextureProvider);
    }
    internal const string OriginalIconResource = "MOGTOME.images.icon.png";
    // The host owns this shared texture and keeps it alive through render submission.
    internal IDalamudTextureWrap OriginalIcon => Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, OriginalIconResource).GetWrapOrEmpty();
    internal static void DrawHeaderArt(Vector2 origin, Vector2 size)
    {
        var list = ImGui.GetWindowDrawList();
        var scale = Math.Min(size.X, size.Y) / 80;
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
        var gold = MaterialCanvas.Color(new Vector4(.98f, .81f, .37f, 1));
        var ink = MaterialCanvas.Color(new Vector4(.20f, .14f, .19f, 1));
        var white = MaterialCanvas.Color(new Vector4(1, .98f, .94f, 1));
        var pink = MaterialCanvas.Color(new Vector4(.96f, .63f, .70f, 1));
        var red = MaterialCanvas.Color(new Vector4(.88f, .12f, .23f, 1));
        var purple = MaterialCanvas.Color(new Vector4(.37f, .23f, .47f, 1));
        void Outline(ReadOnlySpan<Vector2> points, uint color, float thickness)
        {
            foreach (var point in points) list.PathLineTo(P(point.X, point.Y));
            list.PathStroke(color, ImDrawFlags.Closed, thickness * scale);
        }
        void Shape(ReadOnlySpan<Vector2> points, uint color, bool border = true)
        {
            foreach (var point in points) list.PathLineTo(P(point.X, point.Y));
            list.PathFillConvex(color);
            if (!border) return;
            Outline(points, gold, 4.2f);
            Outline(points, ink, 2);
        }
        void Ellipse(float x, float y, float rx, float ry, uint color, bool border = true)
        {
            Span<Vector2> points = stackalloc Vector2[40];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = i * MathF.Tau / points.Length;
                points[i] = new Vector2(x + MathF.Cos(angle) * rx, y + MathF.Sin(angle) * ry);
            }
            Shape(points, color, border);
        }
        void Wing(bool mirrored)
        {
            ReadOnlySpan<Vector2> points = [new(3, 46), new(10, 48), new(15, 46), new(22, 51), new(19, 62), new(13, 60), new(9, 61), new(7, 56), new(3, 57), new(5, 52)];
            Vector2 W(Vector2 point) => P(mirrored ? 80 - point.X : point.X, point.Y);
            var flags = list.Flags;
            list.Flags &= ~ImDrawListFlags.AntiAliasedFill;
            for (var i = 0; i < points.Length; i++)
                list.AddTriangleFilled(W(new Vector2(18, 54)), W(points[i]), W(points[(i + 1) % points.Length]), purple);
            list.Flags = flags;
            foreach (var point in points) list.PathLineTo(W(point));
            list.PathStroke(gold, ImDrawFlags.Closed, 4.2f * scale);
            foreach (var point in points) list.PathLineTo(W(point));
            list.PathStroke(ink, ImDrawFlags.Closed, 2 * scale);
        }
        list.PushClipRect(origin, origin + size, true);
        Wing(false); Wing(true);
        list.AddLine(P(40, 18), P(40, 31), gold, 3.5f * scale);
        Shape([new(14, 20), new(31, 31), new(15, 44)], white);
        Shape([new(66, 20), new(65, 44), new(49, 31)], white);
        Shape([new(18, 26), new(27, 32), new(18, 39)], pink, false);
        Shape([new(62, 26), new(62, 39), new(53, 32)], pink, false);
        Ellipse(40, 52, 25.5f, 24, white);
        Ellipse(40, 11, 8.5f, 8.5f, red);
        list.AddCircleFilled(P(37, 8), 2.3f * scale, MaterialCanvas.Color(new Vector4(1, .43f, .49f, 1)), 16);
        list.PathLineTo(P(24, 51));
        list.PathBezierCubicCurveTo(P(26, 46), P(31, 46), P(33, 51));
        list.PathStroke(ink, ImDrawFlags.None, 2 * scale);
        list.PathLineTo(P(47, 51));
        list.PathBezierCubicCurveTo(P(49, 46), P(54, 46), P(56, 51));
        list.PathStroke(ink, ImDrawFlags.None, 2 * scale);
        Ellipse(40, 61, 3.7f, 2.8f, red, false);
        list.PathLineTo(P(36, 65));
        list.PathBezierCubicCurveTo(P(38, 68), P(42, 68), P(44, 65));
        list.PathStroke(ink, ImDrawFlags.None, 1.5f * scale);
        list.PopClipRect();
    }
    internal IDisposable Font(MogtomeFontRole role) => fonts!.Push(role);
    internal void PaintWindowTitle(string name) => UiLayout.PaintWindowTitle(name,shapedText.Renderer);
    internal void Draw(WindowSystem windows)
    {
        using var shaping = shapedText.Push();
        var config = plugin.Configuration;
        if (plugin.CanSelectUiLanguage && config.UiLanguage is { } saved) Ui.SetLanguage(saved);
        if (appliedLanguage != Ui.Code)
        {
            fonts?.Dispose();
            required = Ui.RequiredText;
            appliedLanguage = Ui.Code;
            fonts = new(Plugin.PluginInterface.UiBuilder.FontAtlas, required, appliedLanguage) { ShapedText = shapedText.Renderer };
            checkedGeneration = -1; fontIssueLogged = false;
        }
        if (theme is null || appliedAccent != (config.UiAccentRgb & 0xFFFFFF))
        {
            appliedAccent = config.UiAccentRgb & 0xFFFFFF;
            theme = MogtomePresentation.Theme(appliedAccent);
            var rgb = MogtomePresentation.Rgb(appliedAccent); accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        theme.Density = config.UiCompact ? MaterialDensity.Compact : MaterialDensity.Standard;
        if (!windows.Windows.Any(window => window.IsOpen)) return;
        if (fonts!.Ready && checkedGeneration != fonts.Generation)
        {
            try { var generation = fonts.Generation; fonts.CheckGlyphs(required); checkedGeneration = generation; }
            catch (Exception ex)
            {
                if (!fontIssueLogged) { Plugin.Log.Error(ex, "[MOGTOME] Required UI glyph coverage failed."); fontIssueLogged = true; }
            }
        }
        if (!fonts.Ready || checkedGeneration != fonts.Generation)
        {
            if (!fontIssueLogged && fonts.LoadException is { } error) { Plugin.Log.Error(error, "[MOGTOME] Required UI fonts failed to load."); fontIssueLogged = true; }
            using var statusPalette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
            using var statusChrome = MaterialWindowChrome.Push();
            ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
            fontStatusFold.PreDraw("MOGTOME##MogtomeFontStatus", null, null, reducedMotion: false,
                prepareDecorations: fontStatusDecorations.Prepare);
            try
            {
                if (ImGui.Begin("MOGTOME##MogtomeFontStatus", ImGuiWindowFlags.AlwaysAutoResize))
                {
                    fontStatusDecorations.Paint();
                    MaterialText.TextWrapped(Ui.T(fonts.LoadException is null && !fontIssueLogged ? "Ui_LoadingFonts" : "Ui_FontFailure"));
                }
            }
            finally
            {
                ImGui.End();
                fontStatusDecorations.Paint();
                fontStatusFold.PostDraw();
            ApplyWindowOpacity(fontStatusOpacity, "MOGTOME##MogtomeFontStatus");
            }
            return;
        }
        using var palette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
        using var style = new MaterialStyleScope();
        var scale = ImGuiHelpers.GlobalScale;
        style.Style(ImGuiStyleVar.WindowPadding, new Vector2(config.UiCompact ? 16 : 22) * scale);
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(config.UiCompact ? 10 : 12, config.UiCompact ? 8 : 12) * scale);
        style.Style(ImGuiStyleVar.FramePadding, new Vector2(config.UiCompact ? 10 : 12, config.UiCompact ? 5 : 7) * scale);
        style.Style(ImGuiStyleVar.CellPadding, new Vector2(config.UiCompact ? 8 : 12, config.UiCompact ? 5 : 7) * scale);
        style.Style(ImGuiStyleVar.FrameRounding, 4 * scale);
        style.Style(ImGuiStyleVar.ChildRounding, 4 * scale);
        var colors = theme.Colors;
        style.Color(ImGuiCol.Button, colors.SurfaceContainerHigh);
        style.Color(ImGuiCol.ButtonHovered, MaterialColor.Layer(colors.SurfaceContainerHigh, colors.OnSurface, .08f));
        style.Color(ImGuiCol.ButtonActive, MaterialColor.Layer(colors.SurfaceContainerHigh, colors.OnSurface, .14f));
        style.Color(ImGuiCol.FrameBg, colors.SurfaceContainerHigh);
        style.Color(ImGuiCol.FrameBgHovered, MaterialColor.Layer(colors.SurfaceContainerHigh, colors.OnSurface, .08f));
        style.Color(ImGuiCol.FrameBgActive, MaterialColor.Layer(colors.SurfaceContainerHigh, colors.OnSurface, .14f));
        using var body = fonts.Push(MogtomeFontRole.Body);
        using var chrome = MaterialWindowChrome.Push();
        windows.Draw();
        foreach (var window in windows.Windows)
        {
            if (!windowOpacities.TryGetValue(window, out var opacity))
                windowOpacities.Add(window, opacity = new());
            ApplyWindowOpacity(opacity, window.WindowName);
        }
    }
    internal void DrawSelector(string id = "appearance")
    {
        var language = (int)Ui.Language;
        using var controls = MaterialControls.Push(MogtomePresentation.Controls());
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        var changed = MaterialAppearanceSelector.Draw(id, ref accentDraft,
            new(Ui.T("Ui_Color"), Ui.T("Language_Label"), Ui.T("Ui_Teal"), Ui.T("Ui_Blue"), Ui.T("Ui_Pink"), Ui.T("Ui_CustomRgb")), () =>
            {
                ImGui.SetNextItemWidth(Math.Max(140 * ImGuiHelpers.GlobalScale,
                    MaterialText.Measure(NativeLanguages[language]).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2));
                // Retain the old native Combo's suffix and original option index/name IDs.
                return UiLayout.Combo("###Language_Label", ref language, NativeLanguages, NativeLanguages.Length);
            });
        ImGui.EndDisabled();
        if (changed.AccentChanged) SaveAccent();
        if (changed.LanguageChanged)
        {
            plugin.Configuration.UiLanguage = (UiLanguage)language;
            plugin.ConfigManager.SaveCurrentAccount();
        }
        if (!plugin.CanSelectUiLanguage && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Language_WaitForProfile"));
    }
    internal void DrawAccent(string id = "appearance")
    {
        using var controls = MaterialControls.Push(MogtomePresentation.Controls());
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        var changed = MaterialAppearanceSelector.DrawAccent(id, ref accentDraft,
            new(Ui.T("Ui_Color"), Ui.T("Language_Label"), Ui.T("Ui_Teal"), Ui.T("Ui_Blue"), Ui.T("Ui_Pink"), Ui.T("Ui_CustomRgb")), MogtomePresentation.Compact ? 36 : 42);
        ImGui.EndDisabled();
        if (changed) SaveAccent();
    }
    internal float LanguageWidth => Math.Max(140 * ImGuiHelpers.GlobalScale,
        MaterialText.Measure(NativeLanguages[(int)Ui.Language]).X + (MogtomePresentation.Compact ? 36 : 42) * ImGuiHelpers.GlobalScale + 24 * ImGuiHelpers.GlobalScale)
        + 38 * ImGuiHelpers.GlobalScale;
    internal void DrawLanguage(string id = "appearance")
    {
        var language = (int)Ui.Language;
        var scale = ImGuiHelpers.GlobalScale;
        using var controls = MaterialControls.Push(MogtomePresentation.Controls());
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        ImGui.PushID(id);
        MaterialButton.IconButton("globe", MaterialIcon.Globe, disabled: true, size: 28);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Language_Label"));
        ImGui.PopID();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(LanguageWidth - 38 * scale);
        // Retain the native Combo and its original indexed option identities at the window root.
        var changed = UiLayout.Combo("###Language_Label", ref language, NativeLanguages, NativeLanguages.Length);
        ImGui.EndDisabled();
        if (changed)
        {
            plugin.Configuration.UiLanguage = (UiLanguage)language;
            plugin.ConfigManager.SaveCurrentAccount();
        }
        if (!plugin.CanSelectUiLanguage && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Language_WaitForProfile"));
    }
    private void SaveAccent()
    {
        plugin.Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
            | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8)
            | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        plugin.ConfigManager.SaveCurrentAccount();
    }
    internal void DrawCompact(string id = "C")
    {
        var compact = plugin.Configuration.UiCompact;
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        if (UiLayout.Checkbox(id, ref compact)) { plugin.Configuration.UiCompact = compact; plugin.ConfigManager.SaveCurrentAccount(); }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Ui_CompactMode"));
    }
    public void Dispose() { fonts?.Dispose(); shapedText.Dispose(); }

    internal void ApplyWindowOpacity(AethertekUI.MaterialWindowOpacity opacity, string windowName)
    {
        var config = plugin.Configuration;
        opacity.Apply(windowName, config.UiWindowOpacityPercent / 100f, config.UiTransparencyEnabled,
            config.UiAutoFade, config.UiFadedOpacityPercent / 100f, config.UiUnfocusedDelaySeconds);
    }

    internal void DrawTransparencyToggle()
    {
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        if (UiLayout.Checkbox(Ui.T("Transparency") + "###window-transparency-main", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.ConfigManager.SaveCurrentAccount(); }
        ImGui.EndDisabled();
    }

    internal void DrawWindowSettings()
    {
        var config = plugin.Configuration;
        var changed = false;
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        var compactVisible = config.UiCompactVisibleOnMainWindow;
        if (UiLayout.Checkbox(Ui.T("Compact visible on main window") + "###window-compact-visible", ref compactVisible))
        { config.UiCompactVisibleOnMainWindow = compactVisible; changed = true; }
        var languageVisible = config.UiLanguageVisibleOnMainWindow;
        if (UiLayout.Checkbox(Ui.T("Language visible on main window") + "###window-language-visible", ref languageVisible))
        { config.UiLanguageVisibleOnMainWindow = languageVisible; changed = true; }
        var enabled = config.UiTransparencyEnabled;
        if (UiLayout.Checkbox(Ui.T("Transparency") + "###window-transparency", ref enabled))
        { config.UiTransparencyEnabled = enabled; changed = true; }
        ImGui.BeginDisabled(!config.UiTransparencyEnabled);
        ImGui.SetNextItemWidth(96 * AethertekUI.MaterialTheme.Metrics.Scale);
        var normal = config.UiWindowOpacityPercent;
        if (UiLayout.InputIntLabel(Ui.T("Opacity (%)") + "###window-opacity", ref normal))
        { config.UiWindowOpacityPercent = normal; changed = true; }
        var autoFade = config.UiAutoFade;
        if (UiLayout.Checkbox(Ui.T("Auto-fade when unfocused") + "###window-auto-fade", ref autoFade))
        { config.UiAutoFade = autoFade; changed = true; }
        ImGui.BeginDisabled(!config.UiAutoFade);
        ImGui.SetNextItemWidth(96 * AethertekUI.MaterialTheme.Metrics.Scale);
        var faded = config.UiFadedOpacityPercent;
        if (UiLayout.InputIntLabel(Ui.T("Unfocused opacity (%)") + "###window-faded-opacity", ref faded))
        { config.UiFadedOpacityPercent = faded; changed = true; }
        ImGui.SetNextItemWidth(96 * AethertekUI.MaterialTheme.Metrics.Scale);
        var delay = config.UiUnfocusedDelaySeconds;
        if (UiLayout.InputIntLabel(Ui.T("Unfocused delay (seconds)") + "###window-unfocused-delay", ref delay))
        { config.UiUnfocusedDelaySeconds = delay; changed = true; }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (changed) plugin.ConfigManager.SaveCurrentAccount();
    }
}
