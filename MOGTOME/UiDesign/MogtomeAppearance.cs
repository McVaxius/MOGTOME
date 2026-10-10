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
    private int checkedHindiGeneration = -1;
    private bool hindiAvailable;
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
        var icon = Plugin.TextureProvider.GetFromManifestResource(typeof(Plugin).Assembly, OriginalIconResource).GetWrapOrEmpty();
        MaterialCanvas.DrawImage(ImGui.GetWindowDrawList(), icon.Handle, icon.Size, origin, origin + size);
    }
    internal IDisposable Font(MogtomeFontRole role) => fonts!.Push(role);
    internal void PaintWindowTitle(string name) => UiLayout.PaintWindowTitle(name,shapedText.Renderer);
    internal void PaintWindowTitleWithButtons(Window owner) => UiLayout.PaintWindowTitleWithButtons(owner,shapedText.Renderer);
    internal unsafe void PaintBrandTitle(Window owner)
    {
        var window = ImGuiP.FindWindowByName(owner.WindowName);
        if (window.Handle == null) return;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        var extraRight = count * (ImGuiP.CalcFontSize(window) + ImGui.GetStyle().ItemInnerSpacing.X);
        var icon = OriginalIcon;
        using var font = Font(MogtomeFontRole.Body);
        MaterialWindowHeader.PaintTitle(window, owner.WindowName.Split("##", 2)[0], icon.Handle, icon.Size, extraRight, owner.ShowCloseButton);
    }
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
            checkedHindiGeneration = -1;
        }
        if (theme is null || appliedAccent != (config.UiAccentRgb & 0xFFFFFF))
        {
            appliedAccent = config.UiAccentRgb & 0xFFFFFF;
            theme = MogtomePresentation.Theme(appliedAccent);
            var rgb = MogtomePresentation.Rgb(appliedAccent); accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        theme.Density = config.UiCompact ? MaterialDensity.Compact : MaterialDensity.Standard;
        if (!windows.Windows.Any(window => window.IsOpen)) return;
        if (fonts!.Ready && checkedHindiGeneration != fonts.Generation)
        {
            var generation = fonts.Generation;
            hindiAvailable = fonts.TryCheckHindiGlyphs();
            checkedHindiGeneration = generation;
        }
        if (fonts.Ready && checkedGeneration != fonts.Generation)
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
                    var loading = fonts.LoadException is null && !fontIssueLogged;
                    if (Ui.Language == UiLanguage.Hindi)
                    {
                        ImGui.TextWrapped(loading ? "Loading Hindi UI fonts..." : "Hindi UI fonts are unavailable. See the plugin log.");
                        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
                        if (!loading && ImGui.Button("Use English"))
                        {
                            plugin.Configuration.UiLanguage = UiLanguage.English;
                            plugin.ConfigManager.SaveCurrentAccount();
                        }
                        ImGui.EndDisabled();
                    }
                    else MaterialText.TextWrapped(Ui.T(loading ? "Ui_LoadingFonts" : "Ui_FontFailure"));
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
                    MaterialText.Measure(LanguageLabel(language)).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2));
                // Retain the old native Combo's suffix and original option index/name IDs.
                return UiLayout.Combo("###Language_Label", ref language, NativeLanguages, NativeLanguages.Length,
                    hindiAvailable ? -1 : (int)UiLanguage.Hindi, "Hindi (unavailable)");
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
    private string LanguageLabel(int index) => index == (int)UiLanguage.Hindi && !hindiAvailable ? "Hindi (unavailable)" : NativeLanguages[index];
    internal float LanguageWidth => Math.Max(140 * ImGuiHelpers.GlobalScale,
        MaterialText.Measure(LanguageLabel((int)Ui.Language)).X + (MogtomePresentation.Compact ? 36 : 42) * ImGuiHelpers.GlobalScale + 24 * ImGuiHelpers.GlobalScale)
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
        var changed = UiLayout.Combo("###Language_Label", ref language, NativeLanguages, NativeLanguages.Length,
            hindiAvailable ? -1 : (int)UiLanguage.Hindi, "Hindi (unavailable)");
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
        var transparencyVisible = config.UiTransparencyVisibleOnMainWindow;
        if (UiLayout.Checkbox(Ui.T("Transparency visible on main window") + "###window-transparency-visible", ref transparencyVisible))
        { config.UiTransparencyVisibleOnMainWindow = transparencyVisible; changed = true; }
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
