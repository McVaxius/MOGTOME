using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.UiDesign;

namespace MOGTOME.Windows;

public sealed class KeyboundBrawlerWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private bool materialBackdropStylePushed;
    private readonly AethertekUI.Dalamud.MaterialWindowLock windowLock = new();
    private readonly Plugin plugin;
    private readonly EventShopTargetEditor shopEditor;
    private bool layoutSizeApplied;

    public KeyboundBrawlerWindow(Plugin plugin) : base(Ui.T("KB_Title") + "###MogtomeKeyboundBrawler")
    {
        this.plugin = plugin;
        shopEditor = new(plugin);
        Size = new Vector2(1000, 880);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new(560, 520), MaximumSize = new(1500, 1500) };
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Play, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.KeyboundBrawler.CanStart && !plugin.IsKeyboundShoppingTrip) plugin.StartKeyboundBrawler(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T("Main_Start")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Clock, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StopKeyboundAfterCurrentRound(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T(plugin.KeyboundLoop.StopAfterCurrentRoundArmed
                ? "KB_StopAfterNextArmed" : "KB_StopAfterNext") + "\n" + Ui.T("KB_StopAfterNextHelp")),
        });
    }

    public override void PreDraw()
    {
        var settings = plugin.Configuration.KeyboundBrawler;
        var goal = plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning
            ? plugin.KeyboundBrawler.WalletTarget : settings.WalletTargetEnabled ? (uint?)Math.Max(0, settings.WalletTarget) : null;
        var wallet = KeyboundBrawlerShop.TryWallet(out var balance) ? balance.ToString("N0") : "?";
        WindowName = Ui.T("KB_Title") + " | " + Ui.T("KB_TitleStatus", wallet, goal?.ToString("N0") ?? "—",
            plugin.KeyboundBrawler.CompletedRuns) + "###MogtomeKeyboundBrawler";
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(Math.Max(560, UiLayout.TitleMinimumWidth(this) / Math.Max(.01f, ImGuiHelpers.GlobalScale)), 520),
            MaximumSize = new(1500, 1500),
        };
        // Apply the approved shop-sized layout once; subsequent user resizes survive reopening.
        SizeCondition = layoutSizeApplied ? ImGuiCond.FirstUseEver : ImGuiCond.Always;
        layoutSizeApplied = true;
        if (MogtomePresentation.HasMaterial || plugin?.Appearance.BackgroundRequested == true)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
            materialBackdropStylePushed = true;
        }
        windowMotion.Prepare(this, reducedMotion: plugin?.Configuration.UiStylingEnabled == false,
            roundedCorners: plugin?.Configuration.UiStylingEnabled != false);
        windowLock.Apply(this, plugin?.Configuration.UiWindowsLocked == true);
    }

    public override void PostDraw()
    {
        windowLock.Restore(this);
        windowMotion.Restore(this);
        if (materialBackdropStylePushed)
        {
            ImGui.PopStyleColor();
            materialBackdropStylePushed = false;
        }
    }

    public override void Draw()
    {
        if (materialBackdropStylePushed)
        {
            ImGui.PopStyleColor();
            materialBackdropStylePushed = false;
        }
        plugin.Appearance.DrawBackdrop(this);
        using var structuralBackground = AethertekUI.MaterialSkin.StructuralBackgroundStyle();
        windowMotion.DrawChrome();
        if (AethertekUI.MaterialTheme.Current.IsLcars) AethertekUI.MaterialLcars.Header(string.Empty);
        using var controls = MaterialControls.Push(MogtomePresentation.Compact
            ? MaterialControlContext.Dense : MaterialControlContext.Toolbar);
        DrawNavigation();
        ImGui.Separator();
        var hasWallet = KeyboundBrawlerShop.TryWallet(out var wallet);
        DrawActions(hasWallet ? wallet : null);
        var settings = plugin.Configuration.KeyboundBrawler;
        UiLayout.Wrapped(Ui.T(settings.PartyRole == KeyboundPartyRole.Member ? "BV_MemberWait" : "KB_AutoLoopHelp"));
        if (settings.PurchaseReviewRequired)
        {
            UiLayout.Wrapped(Ui.T("Shop_ReviewRequired"));
            ImGui.BeginDisabled(!plugin.KeyboundShop.Ready || plugin.KeyboundShop.IsRunning || plugin.IsKeyboundShopActionQueued || plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning || plugin.IsKeyboundStartQueued || plugin.IsKeyboundShoppingTrip);
            if (UiLayout.Button(Ui.L("Shop_Reviewed")))
                GameHelpers.QueueFrameworkAction("Keybound shop", "Review purchase", TimeSpan.Zero,
                    plugin.KeyboundShop.AcknowledgePurchaseReview);
            ImGui.EndDisabled();
        }
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || plugin.KeyboundShop.IsRunning || plugin.IsKeyboundShopActionQueued || plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning || plugin.IsKeyboundStartQueued || plugin.IsKeyboundShoppingTrip);
        var changed = DrawSettings(settings);
        ImGui.Separator();
        changed |= shopEditor.DrawKeybound(settings, KeyboundBrawlerShop.Catalog, hasWallet ? wallet : null);
        if (changed) plugin.ConfigManager.SaveCurrentAccount();
        ImGui.EndDisabled();
    }

    private void DrawNavigation()
    {
        if (UiLayout.Button("MOGTOME###KeyboundOpenMain")) plugin.MainWindow.IsOpen = true;
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("Window_BlundervilleFailer")).X + 24 * ImGuiHelpers.GlobalScale);
        if (UiLayout.Button(Ui.L("Window_BlundervilleFailer"))) plugin.BlundervilleWindow.IsOpen = true;
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("KB_Title")).X + 24 * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Text, MaterialTheme.Current.Colors.Primary);
        UiLayout.Button(Ui.L("KB_Title"));
        ImGui.PopStyleColor();
        const string commands = "/keybound · /kbb";
        UiLayout.SameLineIfFits(MaterialText.Measure(commands).X + 20 * ImGuiHelpers.GlobalScale);
        UiLayout.TextDisabled(commands);
    }

    private void DrawActions(uint? wallet)
    {
        var service = plugin.KeyboundShop;
        var settings = plugin.Configuration.KeyboundBrawler;
        var conflicts = plugin.IsKeyboundShoppingTrip || plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning || plugin.IsKeyboundStartQueued || plugin.Engine?.IsRunning == true || plugin.IsEngineStartQueued ||
            plugin.Blunderville.IsRunning || plugin.IsBlundervilleActionQueued ||
            plugin.MoogleShop.IsRunning || plugin.IsMoogleShopActionQueued;
        var buyReady = service.Ready && !service.IsRunning && !plugin.IsKeyboundShopActionQueued && !conflicts &&
            !settings.PurchaseReviewRequired && wallet.HasValue && service.HasAffordablePurchase(wallet.Value);
        var labels = new[] { Ui.T("Main_Start"), Ui.T("Main_Stop"), Ui.T("BV_Buy"), Ui.T("Shop_Refresh") };
        var actionsWidth = labels.Sum(s => MaterialText.Measure(s).X + ImGui.GetStyle().FramePadding.X * 2) +
            ImGui.GetStyle().ItemSpacing.X * 3 + 8 * ImGuiHelpers.GlobalScale;
        var walletWidth = MaterialText.Measure(Ui.T("KB_Wallet", "999,999,999")).X + 28 * ImGuiHelpers.GlobalScale;
        var status = plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning || plugin.IsKeyboundStartQueued ||
            service.Status.Key == "BV_Idle" && plugin.KeyboundBrawler.Status.Key != "KB_Idle"
            ? (plugin.KeyboundLoop.IsActive ? plugin.KeyboundLoop.Status.Render() : plugin.KeyboundBrawler.Status.Render()) : service.Status.Render();
        if (UiLayout.AvailableWidth >= actionsWidth + walletWidth + 100 * ImGuiHelpers.GlobalScale &&
            ImGui.BeginTable("###KeyboundActions", 3, ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("###Buttons", ImGuiTableColumnFlags.WidthFixed, actionsWidth);
                ImGui.TableSetupColumn("###Status", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("###Wallet", ImGuiTableColumnFlags.WidthFixed, walletWidth);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawActionButtons(buyReady);
                ImGui.TableSetColumnIndex(1);
                ImGui.AlignTextToFramePadding();
                UiLayout.SingleLine(status);
                if (ImGui.IsItemHovered()) UiLayout.SetTooltip(status);
                ImGui.TableSetColumnIndex(2);
                ImGui.AlignTextToFramePadding();
                DrawWallet(wallet);
            }
            finally { ImGui.EndTable(); }
        }
        else
        {
            DrawActionButtons(buyReady);
            UiLayout.SameLineIfFits(walletWidth);
            DrawWallet(wallet);
            UiLayout.Wrapped(status);
        }
    }

    private void DrawActionButtons(bool buyReady)
    {
        ImGui.BeginDisabled(!plugin.KeyboundBrawler.CanStart || plugin.KeyboundLoop.IsActive || plugin.IsKeyboundStartQueued || plugin.IsKeyboundShoppingTrip);
        if (UiLayout.Button(Ui.L("Main_Start"))) plugin.StartKeyboundBrawler();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(plugin.KeyboundBrawler.UnavailableReason.Render());
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("Main_Stop")).X + 24 * ImGuiHelpers.GlobalScale);
        ImGui.BeginDisabled(!plugin.KeyboundShop.IsRunning && !plugin.IsKeyboundShopActionQueued && !plugin.KeyboundLoop.IsActive && !plugin.KeyboundBrawler.IsRunning && !plugin.IsKeyboundStartQueued && !plugin.IsKeyboundShoppingTrip && !plugin.KeyboundBrawler.Finished);
        if (UiLayout.Button(Ui.L("Main_Stop"))) { plugin.StopKeyboundBrawler(); plugin.StopKeyboundShop(); }
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("BV_Buy")).X + 24 * ImGuiHelpers.GlobalScale);
        ImGui.BeginDisabled(!buyReady);
        if (UiLayout.Button(Ui.L("BV_Buy"))) plugin.RequestKeyboundShopBuy();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiLayout.SetTooltip(Ui.T("Shop_Help"));
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("Shop_Refresh")).X + 24 * ImGuiHelpers.GlobalScale);
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || plugin.KeyboundShop.IsRunning || plugin.IsKeyboundShopActionQueued || plugin.KeyboundLoop.IsActive || plugin.KeyboundBrawler.IsRunning || plugin.IsKeyboundStartQueued || plugin.IsKeyboundShoppingTrip);
        if (UiLayout.Button(Ui.L("Shop_Refresh"))) plugin.RefreshKeyboundShop();
        ImGui.EndDisabled();
    }

    private static void DrawWallet(uint? wallet)
    {
        if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(KeyboundBrawlerShop.CurrencyItemId, out var item))
        {
            var texture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.Icon)).GetWrapOrEmpty();
            ImGui.Image(texture.Handle, new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
        }
        UiLayout.SingleLine(Ui.T("KB_Wallet", wallet?.ToString(Ui.Culture) ?? "?"));
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("KB_CurrencyName"));
    }

    private static bool DrawSettings(KeyboundBrawlerSettings settings)
    {
        var changed = false;
        var columns = UiLayout.AvailableWidth >= 680 * ImGuiHelpers.GlobalScale ? 2 : 1;
        if (!ImGui.BeginTable("###KeyboundSettings", columns, ImGuiTableFlags.SizingStretchSame)) return false;
        try
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var runEnabled = settings.RunLimitEnabled;
            var runs = settings.RunLimit;
            if (DrawLimit("BV_RunLimit", "###KeyboundRunLimit", ref runEnabled, ref runs, 1, Ui.T("BV_Runs")))
            { settings.RunLimitEnabled = runEnabled; settings.RunLimit = runs; changed = true; }
            if (columns == 1) ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(columns - 1);
            var walletEnabled = settings.WalletTargetEnabled;
            var wallet = settings.WalletTarget;
            if (DrawLimit("KB_WalletTarget", "###KeyboundWalletTarget", ref walletEnabled, ref wallet, 0, "MGC"))
            { settings.WalletTargetEnabled = walletEnabled; settings.WalletTarget = wallet; changed = true; }
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var automatic = settings.ShopWhenFinished;
            if (UiLayout.Checkbox(Ui.L("BV_AutoShop"), ref automatic)) { settings.ShopWhenFinished = automatic; changed = true; }
            if (columns == 1) ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(columns - 1);
            UiLayout.SingleLine(Ui.T("KB_AfterShopping"));
            UiLayout.SameLineIfFits(150 * ImGuiHelpers.GlobalScale);
            var endings = Enum.GetValues<BlundervilleEndingLocation>();
            var selected = (int)settings.EndingLocation;
            ImGui.SetNextItemWidth(Math.Min(UiLayout.AvailableWidth, 200 * ImGuiHelpers.GlobalScale));
            if (UiLayout.Combo("###KeyboundEndingLocation", ref selected,
                endings.Select(e => Ui.T("BV_Inn" + e)).ToArray(), endings.Length))
            { settings.EndingLocation = endings[selected]; changed = true; }
        }
        finally { ImGui.EndTable(); }
        var role = (int)settings.PartyRole;
        ImGui.SetNextItemWidth(Math.Min(UiLayout.AvailableWidth, 180 * ImGuiHelpers.GlobalScale));
        if (UiLayout.Combo(Ui.L("KB_PartyRole") + "###KeyboundPartyRole", ref role,
            [Ui.T("KB_PartyLeader"), Ui.T("KB_PartyMember")], 2))
        { settings.PartyRole = (KeyboundPartyRole)role; changed = true; }
        var stage12Wpm = settings.Stage12TypingWpm;
        if (DrawTypingSpeed("KB_Stage12TypingSpeed", "###KeyboundStage12TypingWpm", ref stage12Wpm))
        { settings.Stage12TypingWpm = stage12Wpm; changed = true; }
        var stage3Wpm = settings.Stage3TypingWpm;
        if (DrawTypingSpeed("KB_Stage3TypingSpeed", "###KeyboundStage3TypingWpm", ref stage3Wpm))
        { settings.Stage3TypingWpm = stage3Wpm; changed = true; }
        UiLayout.TextDisabled(Ui.T("KB_FinishRule"));
        return changed;
    }

    private static bool DrawTypingSpeed(string label, string inputId, ref double value)
    {
        UiLayout.SingleLine(Ui.T(label));
        UiLayout.SameLineIfFits(160 * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(Math.Min(UiLayout.AvailableWidth, 160 * ImGuiHelpers.GlobalScale));
        bool changed;
        using (MaterialSkin.NativeFieldStyle()) changed = ImGui.InputDouble(inputId, ref value, .5, 5, "%.1f");
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("KB_TypingSpeedHelp"));
        return changed;
    }

    private static bool DrawLimit(string labelKey, string inputId, ref bool enabled, ref int value, int minimum, string unit)
    {
        var changed = UiLayout.Checkbox(Ui.L(labelKey), ref enabled);
        var width = Math.Max(60 * ImGuiHelpers.GlobalScale, ImGui.CalcTextSize("999999").X + ImGui.GetStyle().FramePadding.X * 2);
        UiLayout.SameLineIfFits(width + MaterialText.Measure(unit).X + ImGui.GetStyle().ItemSpacing.X);
        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(Math.Min(UiLayout.AvailableWidth, width));
        var edited = value;
        if (UiLayout.InputIntLabel(inputId, ref edited)) { value = Math.Max(minimum, edited); changed = true; }
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(MaterialText.Measure(unit).X);
        UiLayout.TextDisabled(unit);
        return changed;
    }

    public void Dispose() { }
}
