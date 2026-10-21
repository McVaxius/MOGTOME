using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Windows;

namespace MOGTOME.UiDesign;

internal sealed class MogtomeAppearance : IDisposable
{
    private readonly System.Collections.Generic.Dictionary<Dalamud.Interface.Windowing.IWindow, AethertekUI.MaterialWindowOpacity> windowOpacities = new();
    private readonly AethertekUI.MaterialWindowOpacity fontStatusOpacity = new();
    private readonly System.Collections.Generic.Dictionary<Window, MaterialAppearanceTitleBar> titleBars = new();
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
    private MaterialColorSettings colorDraft = new();
    private Configuration? draftOwner;
    private MaterialColorSettings? appliedSavedColors;
    private MaterialColorSettings? appliedColors;
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
    internal void DrawShopStorageNotice()
    {
        var config = plugin.Configuration;
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        try
        {
            if (config.ShopStorageNoticeHidden)
            {
                UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(string.Empty));
                if (UiLayout.IconButton("###ShopStorageNoticeInfo", MaterialIcon.Info))
                {
                    config.ShopStorageNoticeHidden = false;
                    plugin.ConfigManager.SaveCurrentAccount();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiLayout.SetTooltip(Ui.T("Shop_ShowStorageNotice"));
                return;
            }
            if (!ImGui.BeginTable("ShopStorageNotice", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings)) return;
            try
            {
                ImGui.TableSetupColumn("###Notice", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("###Hide", ImGuiTableColumnFlags.WidthFixed,
                    MaterialText.Measure(Ui.T("Shop_HideStorageNotice")).X + ImGui.GetStyle().FramePadding.X * 2);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                UiLayout.Wrapped(Ui.T("Shop_StorageNotice"));
                ImGui.TableSetColumnIndex(1);
                if (UiLayout.Button(Ui.L("Shop_HideStorageNotice")))
                {
                    config.ShopStorageNoticeHidden = true;
                    plugin.ConfigManager.SaveCurrentAccount();
                }
            }
            finally { ImGui.EndTable(); }
        }
        finally { ImGui.EndDisabled(); }
    }
    internal void PaintWindowTitle(string name) => UiLayout.PaintWindowTitle(name,shapedText.Renderer);
    internal void PaintWindowTitleWithButtons(Window owner) => UiLayout.PaintWindowTitleWithButtons(owner,shapedText.Renderer);
    internal void PaintBrandTitle(Window owner)
        => PaintBrandTitleWithStatus(owner, plugin.Configuration.UiShowTitlebarStatus ? TitlebarStatus(owner) : null);

    private unsafe void PaintBrandTitleWithStatus(Window owner, MaterialTitlebarStatus? status)
    {
        var window = ImGuiP.FindWindowByName(owner.WindowName);
        if (window.Handle == null) return;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        var extraRight = count * (ImGuiP.CalcFontSize(window) + ImGui.GetStyle().ItemInnerSpacing.X);
        var icon = OriginalIcon;
        using var font = Font(MogtomeFontRole.Body);
        if (status is { } summary)
            MaterialWindowHeader.PaintTitleWithStatus(window, owner.WindowName.Split("##", 2)[0], icon.Handle, icon.Size,
                summary, extraRight, owner.ShowCloseButton);
        else MaterialWindowHeader.PaintTitle(window, owner.WindowName.Split("##", 2)[0], icon.Handle, icon.Size, extraRight, owner.ShowCloseButton);
    }

    private MaterialTitlebarStatus TitlebarStatus(Window owner)
    {
        var config = plugin.Configuration;
        var fields = new System.Collections.Generic.List<string>();
        string state, detail;
        bool active, blocked = false;
        if (owner is MoogleShopWindow || owner is not BlundervilleWindow && plugin.MoogleShop.IsRunning)
        {
            var shop = plugin.MoogleShop;
            active = shop.IsRunning;
            blocked = !active && shop.Status.Key is not ("BV_Idle" or "BV_ShopDone" or "BV_Stopped");
            state = Ui.T(active ? "Ui_TitlebarBuying" : blocked ? "Ui_TitlebarBlocked"
                : shop.Status.Key == "BV_Stopped" ? "Main_Stopped" : "BV_Idle");
            detail = shop.Status.Render();
            var targets = config.MoogleShop.PurchaseTargets;
            var stocks = plugin.XaDatabase.ReadStocks(targets.Keys);
            var counts = targets.Keys.ToDictionary(id => id,
                id => plugin.XaDatabase.TryCount(id, stocks.GetValueOrDefault(id), out var count) ? (int?)count : null);
            var offers = shop.SelectedCatalog.GroupBy(offer => offer.ItemId).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());
            var costs = MoogleShopMath.TotalCosts(targets, offers, id => counts.GetValueOrDefault(id), out var known);
            fields.Add(Ui.T("Ui_TitlebarItemsLeft", counts.Values.Any(count => !count.HasValue) ? "—"
                : targets.Sum(target => Math.Max(0L, (long)target.Value - counts.GetValueOrDefault(target.Key).GetValueOrDefault())).ToString(Ui.Culture)));
            foreach (var id in shop.SelectedCatalog.SelectMany(offer => offer.Costs).Select(cost => cost.ItemId)
                .Concat(shop.TomestoneBalances.Select(balance => balance.ItemId)).Distinct())
            {
                var owned = shop.TomestoneBalances.FirstOrDefault(balance => balance.ItemId == id);
                if (owned != null)
                {
                    fields.Add(costs.ContainsKey(id)
                        ? Ui.T("Tomes_TitleBalanceCost", Ui.Item(id), Quantity(owned.Total), Quantity(owned.Carried),
                            known ? costs[id].ToString(Ui.Culture) : "—")
                        : Ui.T("Tomes_TitleBalance", Ui.Item(id), Quantity(owned.Total)));
                    detail += "\n" + TomestoneBreakdown(owned);
                }
                else
                {
                    var balanceKnown = BlundervilleGameAdapter.TryBagInventory(id, out var balance, out _);
                    fields.Add(Ui.T("Shop_Balance", Ui.Item(id), balanceKnown ? balance.ToString(Ui.Culture) : "—",
                        known ? costs.GetValueOrDefault(id).ToString(Ui.Culture) : "—"));
                }
            }
        }
        else if (owner is BlundervilleWindow)
        {
            var service = plugin.Blunderville;
            var settings = config.Blunderville;
            active = service.IsRunning;
            blocked = !active && service.Status.Key is not ("BV_Idle" or "BV_FarmDone" or "BV_ShopDone" or "BV_Stopped");
            state = Ui.T(active ? service.IsShopping ? "Ui_TitlebarBuying" : "Ui_TitlebarFarming"
                : blocked ? "Ui_TitlebarBlocked" : service.Status.Key == "BV_Stopped" ? "Main_Stopped" : "BV_Idle");
            detail = service.Status.Render();
            var walletKnown = BlundervilleGameAdapter.TryWallet(out var wallet);
            fields.Add(settings.WalletTargetEnabled
                ? $"MGF {(walletKnown ? wallet.ToString(Ui.Culture) : "—")}/{settings.WalletTarget.ToString(Ui.Culture)}"
                : $"MGF {(walletKnown ? wallet.ToString(Ui.Culture) : "—")}");
            if (settings.RunLimitEnabled)
                fields.Add(Ui.T("Ui_TitlebarRuns", service.SessionCycles, settings.RunLimit, Math.Max(0, settings.RunLimit - service.SessionCycles)));
            else
                fields.Add(Ui.T("BV_Runs") + ": " + service.SessionCycles.ToString(Ui.Culture));
            var stocks = plugin.XaDatabase.ReadStocks(settings.PurchaseTargets.Keys);
            var counts = settings.PurchaseTargets.Keys.ToDictionary(id => id,
                id => plugin.XaDatabase.TryCount(id, stocks.GetValueOrDefault(id), out var count) ? (int?)count : null);
            var offers = service.Catalog.GroupBy(offer => offer.ItemId).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());
            var cost = BlundervilleProgress.TotalMgf(settings.PurchaseTargets, offers, id => counts.GetValueOrDefault(id), out var known);
            fields.Add(Ui.T("Ui_TitlebarItemsLeft", counts.Values.Any(count => !count.HasValue) ? "—"
                : settings.PurchaseTargets.Sum(target => Math.Max(0L, (long)target.Value - counts.GetValueOrDefault(target.Key).GetValueOrDefault())).ToString(Ui.Culture)));
            fields.Add(Ui.T("Ui_TitlebarCost", known ? cost.ToString(Ui.Culture) : "—"));
        }
        else
        {
            var engine = plugin.Engine;
            active = engine?.IsRunning == true;
            state = engine == null ? Ui.T("Main_StateInitializing") : Ui.EnumLabel(engine.CurrentState);
            detail = engine?.Status.Render() ?? Ui.T("Main_StatusWaitingForAccountAndEngineInitialization");
            foreach (var balance in plugin.MoogleShop.TomestoneBalances)
            {
                fields.Add(Ui.T("Tomes_TitleBalance", Ui.Item(balance.ItemId), Quantity(balance.Total)));
                detail += "\n" + TomestoneBreakdown(balance);
            }
            if (config.TomestoneTargetEnabled)
                fields.Add(Ui.T("Config_TomestoneTarget") + ": " + Ui.T("Tomes_Target", Quantity(plugin.MoogleShop.TomestoneBalances
                    .FirstOrDefault(balance => balance.ItemId == plugin.MoogleShop.CurrentTomestoneId)?.Total),
                    Math.Max(1, config.TomestoneTarget)));
            fields.Add($"P{plugin.State.DutyCounter.ToString(Ui.Culture)} D{plugin.State.DecumanaCounter.ToString(Ui.Culture)}");
            detail = Ui.T("Main_CounterPraeDailyPraetoriumLimit", plugin.State.DutyCounter, config.PraetoriumThreshold, config.MaxRuns)
                + "\n" + Ui.T("Main_DailyDecuHeading") + ": " + plugin.State.DecumanaCounter.ToString(Ui.Culture) + "\n" + detail;
        }
        var concise = string.Join(" · ", new[] { state }.Concat(fields));
        var tooltip = concise + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "\n" + detail);
        return new(config.UiDetailedTitlebarStatus && !string.IsNullOrWhiteSpace(detail) ? concise + " · " + detail : concise,
            concise, tooltip, blocked ? MaterialTheme.Current.Colors.Error
                : active ? MaterialTheme.Current.Colors.Primary : MaterialTheme.Current.Colors.OnSurfaceVariant);
    }
    private static string Quantity(int? quantity) => quantity?.ToString(Ui.Culture) ?? "—";

    private static string TomestoneBreakdown(MoogleTomestoneBalance balance)
        => balance.Total.HasValue
            ? Ui.T("Tomes_Breakdown", Ui.Item(balance.ItemId), Quantity(balance.Carried), Quantity(balance.Saddlebags),
                Quantity(balance.RetainerStorage), Quantity(balance.Other), Quantity(balance.Total))
            : Ui.T("Tomes_NoData");

    internal void DrawTomestoneBalances()
    {
        var balances = plugin.MoogleShop.TomestoneBalances;
        UiLayout.SingleLine(Ui.T("Tomes_Heading"));
        if (balances.Count == 0) { UiLayout.TextDisabled(Ui.T("Tomes_NoData")); return; }
        using var controls = MaterialTable.PushControls();
        using var rows = MaterialTable.PushTightRows();
        var scale = ImGuiHelpers.GlobalScale;
        var target = plugin.Configuration.TomestoneTargetEnabled;
        var headings = new[] { Ui.T("BV_Item"), Ui.T("Tomes_Owned"), Ui.T("Tomes_Carried"), Ui.T("Tomes_Storage"), Ui.T("Config_TomestoneTarget") };
        var countWidth = MathF.Max(MaterialText.Measure("9999999999").X,
            headings.Skip(1).Take(3).Max(heading => MaterialText.Measure(heading).X)) + 4 * scale;
        var targetWidth = MathF.Max(MaterialText.Measure("9999999999 / 999999999").X,
            MaterialText.Measure(headings[4]).X) + 4 * scale;
        var itemWidth = balances.Max(balance => MaterialText.Measure(Ui.Item(balance.ItemId).Render()).X) + 28 * scale;
        var innerWidth = itemWidth + countWidth * 3 + (target ? targetWidth : 0) + 12 * scale;
        var scrolling = innerWidth > UiLayout.AvailableWidth;
        var height = scrolling ? ImGui.GetTextLineHeight() + balances.Count * MathF.Max(24 * scale, ImGui.GetTextLineHeight())
            + ImGui.GetStyle().ScrollbarSize + 2 * ImGui.GetStyle().WindowPadding.Y + 4 * scale : 0;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoSavedSettings;
        if (scrolling) flags |= ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("MogtomeTomestones", target ? 5 : 4, flags, new Vector2(0, height), scrolling ? innerWidth : 0)) return;
        try
        {
            ImGui.TableSetupColumn(headings[0], ImGuiTableColumnFlags.WidthStretch);
            for (var column = 1; column < 4; column++)
                ImGui.TableSetupColumn(headings[column], ImGuiTableColumnFlags.WidthFixed, countWidth);
            if (target) ImGui.TableSetupColumn(headings[4], ImGuiTableColumnFlags.WidthFixed, targetWidth);
            UiLayout.TableHeadersRow();
            foreach (var balance in balances)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(balance.ItemId);
                if (item is { } row)
                {
                    var icon = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(row.Icon)).GetWrapOrEmpty();
                    MaterialCanvas.DrawImage(ImGui.GetWindowDrawList(), icon.Handle, icon.Size,
                        ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(24 * scale));
                    ImGui.Dummy(new Vector2(24 * scale));
                    ImGui.SameLine(0, 4 * scale);
                }
                UiLayout.SingleLine(Ui.Item(balance.ItemId).Render());
                if (ImGui.IsItemHovered()) UiLayout.SetTooltip(TomestoneBreakdown(balance));
                ImGui.TableSetColumnIndex(1); MaterialText.Text(Quantity(balance.Total));
                if (ImGui.IsItemHovered()) UiLayout.SetTooltip(TomestoneBreakdown(balance));
                ImGui.TableSetColumnIndex(2); MaterialText.Text(Quantity(balance.Carried));
                ImGui.TableSetColumnIndex(3); MaterialText.Text(Quantity(balance.Stored));
                if (target)
                {
                    ImGui.TableSetColumnIndex(4);
                    if (balance.ItemId == plugin.MoogleShop.CurrentTomestoneId)
                        MaterialText.Text(Ui.T("Tomes_Target", Quantity(balance.Total), Math.Max(1, plugin.Configuration.TomestoneTarget)));
                }
            }
        }
        finally { ImGui.EndTable(); }
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
        var savedColors = plugin.Configuration.UiColors ?? new MaterialColorSettings();
        var ownerChanged = !ReferenceEquals(draftOwner, config);
        if (ownerChanged || savedColors != appliedSavedColors)
        {
            draftOwner = config;
            colorDraft = savedColors;
            appliedSavedColors = savedColors;
        }
        if (ownerChanged || theme is null || appliedAccent != (plugin.Configuration.UiAccentRgb & 0xFFFFFF) || colorDraft != appliedColors)
        {
            appliedAccent = plugin.Configuration.UiAccentRgb & 0xFFFFFF;
            appliedColors = colorDraft;
            theme = MogtomePresentation.Theme(appliedAccent, colorDraft);
            var rgb = MogtomePresentation.Rgb(appliedAccent);
            accentDraft = new(rgb.X, rgb.Y, rgb.Z);
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
        foreach (var owner in windows.Windows.OfType<Window>())
        {
            if (!titleBars.TryGetValue(owner, out var titleBar))
            {
                titleBar = MaterialAppearanceTitleBar.Register(owner,
                    () => new(Ui.T("Language_Label"), Ui.T("Ui_CompactMode"), Ui.T("Transparency"), Ui.T("Ui_Color")),
                    () => DrawLanguage("titlebar-language"),
                    () =>
                    {
                        if (!plugin.CanSelectUiLanguage) return;
                        plugin.Configuration.UiCompact = !plugin.Configuration.UiCompact;
                        plugin.ConfigManager.SaveCurrentAccount();
                    },
                    () => DrawWindowSettings(includeVisibility: false), DrawTitlebarPalette,
                    ReferenceEquals(owner, plugin.MainWindow)
                        ? () => new(plugin.Configuration.UiLanguageVisibleOnMainWindow,
                            plugin.Configuration.UiCompactVisibleOnMainWindow,
                            plugin.Configuration.UiTransparencyVisibleOnMainWindow, true)
                        : null);
                titleBars.Add(owner, titleBar);
            }
            titleBar.RefreshVisibility();
        }
        windows.Draw();
        foreach (var titleBar in titleBars.Values) titleBar.DrawPopups();
        MaterialTitlebarStatus? mainStatus = null, moogleStatus = null, blundervilleStatus = null;
        foreach (var window in windows.Windows)
        {
            if (window.IsOpen && window is Window owner)
            {
                MaterialTitlebarStatus? status = null;
                if (config.UiShowTitlebarStatus)
                    status = owner is MoogleShopWindow || owner is not BlundervilleWindow && plugin.MoogleShop.IsRunning
                        ? moogleStatus ??= TitlebarStatus(owner)
                        : owner is BlundervilleWindow || plugin.Blunderville.IsRunning
                            ? blundervilleStatus ??= TitlebarStatus(owner) : mainStatus ??= TitlebarStatus(owner);
                PaintBrandTitleWithStatus(owner, status);
            }
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
        var colorChanged = DrawColourEditor(id);
        ImGui.SetNextItemWidth(Math.Max(140 * ImGuiHelpers.GlobalScale,
            MaterialText.Measure(LanguageLabel(language)).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2));
        // Retain the old native Combo's suffix and original option index/name IDs.
        var languageChanged = UiLayout.Combo("###Language_Label", ref language, NativeLanguages, NativeLanguages.Length,
            hindiAvailable ? -1 : (int)UiLanguage.Hindi, "Hindi (unavailable)");
        ImGui.EndDisabled();
        if (languageChanged)
        {
            plugin.Configuration.UiLanguage = (UiLanguage)language;
            plugin.ConfigManager.SaveCurrentAccount();
        }
        if (colorChanged && !languageChanged) plugin.ConfigManager.SaveCurrentAccount();
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
    private bool DrawColourEditor(string id)
    {
        var config = plugin.Configuration;
        if (!ReferenceEquals(draftOwner, config))
        {
            draftOwner = config;
            colorDraft = config.UiColors ?? new MaterialColorSettings();
            appliedSavedColors = colorDraft;
            var rgb = MogtomePresentation.Rgb(config.UiAccentRgb);
            accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        var changed = MaterialAppearanceSelector.DrawColors(id, ref colorDraft, ref accentDraft,
            new(Ui.T("Ui_Color"), Ui.T("Language_Label"), Ui.T("Ui_Teal"), Ui.T("Ui_Blue"), Ui.T("Ui_Pink"), Ui.T("Ui_CustomRgb")),
            new(Ui.T("Auto"), Ui.T("Picker"), Ui.T("Default"), Ui.T("Background, controls and text follow your theme."), Ui.T("Theme colour"), Ui.T("Choose each colour independently."), Ui.T("Background"), Ui.T("Controls"), Ui.T("Text"), Ui.T("Aa  Preview text"), Ui.T("Control"), Ui.T("Switch to Picker to set each colour yourself."), Ui.T("Switch back to Auto to use the automatic theme.")), plugin.Configuration.UiCompact ? 298 : 316);
        if (!changed) return false;
        config.UiColors = colorDraft;
        config.UiAccentRgb = MaterialColorSettings.ToRgb(accentDraft);
        return true;
    }

    private void DrawTitlebarPalette()
    {
        using var controls = MaterialControls.Push(MogtomePresentation.Controls());
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        try { if (DrawColourEditor("titlebar-colour")) plugin.ConfigManager.SaveCurrentAccount(); }
        finally { ImGui.EndDisabled(); }
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

    internal void DrawWindowSettings() => DrawWindowSettings(includeVisibility: true);
    private void DrawWindowSettings(bool includeVisibility)
    {
        var config = plugin.Configuration;
        var changed = false;
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage);
        if (includeVisibility)
        {
            var showStatus = config.UiShowTitlebarStatus;
            if (UiLayout.Checkbox(Ui.T("Ui_ShowTitlebarStatus") + "###window-titlebar-status", ref showStatus))
            { config.UiShowTitlebarStatus = showStatus; changed = true; }
            var detailedStatus = config.UiDetailedTitlebarStatus;
            if (UiLayout.Checkbox(Ui.T("Ui_DetailedTitlebarStatus") + "###window-titlebar-detail", ref detailedStatus))
            { config.UiDetailedTitlebarStatus = detailedStatus; changed = true; }
            var compactVisible = config.UiCompactVisibleOnMainWindow;
            if (UiLayout.Checkbox(Ui.T("Compact visible on main window") + "###window-compact-visible", ref compactVisible))
            { config.UiCompactVisibleOnMainWindow = compactVisible; changed = true; }
            var transparencyVisible = config.UiTransparencyVisibleOnMainWindow;
            if (UiLayout.Checkbox(Ui.T("Transparency visible on main window") + "###window-transparency-visible", ref transparencyVisible))
            { config.UiTransparencyVisibleOnMainWindow = transparencyVisible; changed = true; }
            var languageVisible = config.UiLanguageVisibleOnMainWindow;
            if (UiLayout.Checkbox(Ui.T("Language visible on main window") + "###window-language-visible", ref languageVisible))
            { config.UiLanguageVisibleOnMainWindow = languageVisible; changed = true; }
        }
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
