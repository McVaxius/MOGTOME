using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MOGTOME.Localization;
using MOGTOME.UiDesign;
using MOGTOME.Models;
using MOGTOME.Services;
using AethertekUI;

namespace MOGTOME.Windows;

public sealed class BlundervilleWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private bool materialBackdropStylePushed;
    private readonly Plugin plugin;
    private readonly EventShopTargetEditor shopEditor;
    public bool DebugVisible { get; set; }

    public BlundervilleWindow(Plugin plugin)
        : base(Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
        shopEditor = new(plugin);
        Size = new Vector2(740, 630);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 280),
            MaximumSize = new Vector2(1100, 900),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        WindowName = Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville";
        Size = MogtomePresentation.Compact ? new Vector2(720, 611) : new Vector2(740, 630);
        if (MogtomePresentation.HasMaterial)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
            materialBackdropStylePushed = true;
        }
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
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
        MaterialSkin.Backdrop();
        windowMotion.DrawChrome();
        if (AethertekUI.MaterialTheme.Current.IsLcars) AethertekUI.MaterialLcars.Header(string.Empty);
        var moogle = plugin.Appearance.OriginalIcon;
        var scale = ImGuiHelpers.GlobalScale;
        var unavailable = new Vector4(1f, .60f, .80f, 1f);
        var eventIcon = plugin.Appearance.BlundervilleIcon;
        MaterialCanvas.DrawImage(ImGui.GetWindowDrawList(), eventIcon.Handle, eventIcon.Size,
            ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(30 * scale));
        ImGui.Dummy(new Vector2(30 * scale));
        ImGui.SameLine();
        using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactHeading : MogtomeFontRole.Heading))
        {
            if (MogtomePresentation.HasMaterial) MaterialSkin.Heading(Ui.T("Window_BlundervilleFailer"));
            else UiLayout.Wrapped(Ui.T("Window_BlundervilleFailer"));
        }
        plugin.Appearance.DrawShopStorageNotice();
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
            UiLayout.Wrapped(Ui.T("BV_FarmHelp"));
            ImGui.EndGroup();
            var textHeight = ImGui.GetItemRectSize().Y;
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, Math.Max(imageHeight, textHeight)));
            if (UiLayout.IconButton(Ui.L("Blunderville_OpenMogtome"), MaterialIcon.ExternalLink, new Vector2(UiLayout.AvailableWidth, 0)))
                plugin.MainWindow.IsOpen = true;
        });
        UiLayout.Panel(() =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var width = UiLayout.AvailableWidth;
            var icon = plugin.Appearance.KeyboundIcon;
            MaterialCanvas.DrawImage(ImGui.GetWindowDrawList(), icon.Handle, icon.Size,
                origin, origin + new Vector2(36 * scale));
            ImGui.SetCursorScreenPos(origin + new Vector2(44 * scale, 0));
            ImGui.BeginGroup();
            using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
                UiLayout.Wrapped(Ui.T("KB_Title"));
            UiLayout.Wrapped(Ui.T("Main_Open") + ": " + Ui.T("KB_Title"));
            ImGui.EndGroup();
            var buttonWidth = UiLayout.IconButtonWidth(Ui.T("Main_Open"));
            if (width >= ImGui.GetItemRectSize().X + 44 * scale + buttonWidth + 16 * scale)
                ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonWidth, 0));
            if (UiLayout.IconButton(Ui.L("KB_Title"), MaterialIcon.ArrowRight,
                new Vector2(Math.Min(buttonWidth, UiLayout.AvailableWidth), (MogtomePresentation.Compact ? 36 : 42) * scale), Ui.T("Main_Open")))
                plugin.KeyboundBrawlerWindow.IsOpen = true;
        }, minimumHeight: (MogtomePresentation.Compact ? 60 : 74) * scale);
        ImGui.Separator();
        DrawControls();
    }

    private void DrawControls()
    {
        var service = plugin.Blunderville;
        UiLayout.Wrapped(service.Status.Render());
        var hasWallet = BlundervilleGameAdapter.TryWallet(out var wallet);
        UiLayout.Wrapped(Ui.T("BV_Progress", service.SessionCycles, hasWallet ? wallet.ToString(Ui.Culture) : "?"));
        var missingPlugins = GameHelpers.MissingXaDependencies();
        if (missingPlugins.Length > 0) UiLayout.Wrapped(Ui.T("Plugin_RequiredXaPlugins", missingPlugins));
        var unavailable = !service.Ready || service.IsRunning || plugin.IsBlundervilleActionQueued ||
            plugin.IsEngineStartQueued || plugin.Engine?.IsRunning == true || plugin.MoogleShop.IsRunning || plugin.IsMoogleShopActionQueued || missingPlugins.Length > 0;
        ImGui.BeginDisabled(unavailable);
        if (UiLayout.Button(Ui.L("Main_Start"))) plugin.RequestBlundervilleAction(buy: false);
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("BV_Buy")));
        ImGui.BeginDisabled(Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InDutyQueue]);
        if (UiLayout.Button(Ui.L("BV_Buy"))) plugin.RequestBlundervilleAction(buy: true);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Main_Stop")));
        if (UiLayout.Button(Ui.L("Main_Stop"))) plugin.StopBlunderville();
        ImGui.Separator();

        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || service.IsRunning || plugin.IsBlundervilleActionQueued);
        var settings = plugin.Configuration.Blunderville;
        if (settings.PurchaseReviewRequired)
        {
            UiLayout.Wrapped(Ui.T("BV_UncertainPurchase"));
            if (UiLayout.Button(Ui.L("BV_ReviewedPurchase")))
                GameHelpers.QueueFrameworkAction("Blunderville", "Review purchase", TimeSpan.Zero, service.AcknowledgePurchaseReview);
        }
        var changed = false;
        var runEnabled = settings.RunLimitEnabled;
        if (UiLayout.Checkbox(Ui.L("BV_RunLimit"), ref runEnabled)) { settings.RunLimitEnabled = runEnabled; changed = true; }
        if (runEnabled)
        {
            var runs = settings.RunLimit;
            if (UiLayout.InputInt("BV_Runs", ref runs)) { settings.RunLimit = Math.Max(1, runs); changed = true; }
        }
        var walletEnabled = settings.WalletTargetEnabled;
        if (UiLayout.Checkbox(Ui.L("BV_WalletTarget"), ref walletEnabled)) { settings.WalletTargetEnabled = walletEnabled; changed = true; }
        if (walletEnabled)
        {
            var target = settings.WalletTarget;
            if (UiLayout.InputInt("BV_TargetMgf", ref target)) { settings.WalletTarget = Math.Max(0, target); changed = true; }
        }
        if (!BlundervilleProgress.HasLimit(settings)) UiLayout.Wrapped(Ui.T("BV_NeedLimit"));
        ImGui.Separator();
        ImGui.EndDisabled();
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || service.IsShopping);
        changed |= DrawShop(settings, service.Catalog);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || service.IsRunning || plugin.IsBlundervilleActionQueued);
        var automatic = settings.ShopWhenFinished;
        if (UiLayout.Checkbox(Ui.L("BV_AutoShop"), ref automatic)) { settings.ShopWhenFinished = automatic; changed = true; }
        UiLayout.Wrapped(Ui.T("BV_EndingLocation"));
        var endings = Enum.GetValues<BlundervilleEndingLocation>();
        var selected = (int)settings.EndingLocation;
        ImGui.SetNextItemWidth(UiLayout.AvailableWidth);
        if (UiLayout.Combo("###BlundervilleEndingLocation", ref selected, endings.Select(EndingLabel).ToArray(), endings.Length))
        { settings.EndingLocation = endings[selected]; changed = true; }
        if (DebugVisible)
        {
            ImGui.Separator();
            UiLayout.Wrapped(Ui.T("BV_ReloadHelp"));
            var scenario = (int)settings.ReloadScenario;
            var scenarios = new[] { Ui.T("BV_ReloadNone"), Ui.T("Main_Start"), Ui.T("BV_Buy") };
            ImGui.SetNextItemWidth(UiLayout.AvailableWidth);
            if (MogtomePresentation.HasMaterial
                ? UiLayout.Combo("###BlundervilleReloadScenario", ref scenario, scenarios, scenarios.Length)
                : ImGui.Combo("###BlundervilleReloadScenario", ref scenario, scenarios, scenarios.Length))
            {
                settings.ReloadScenario = (BlundervilleReloadScenario)scenario;
                GameHelpers.QueueFrameworkAction("Blunderville", "Selection change", TimeSpan.Zero, service.ReloadSelectionChanged);
                changed = true;
            }
        }
        if (changed) plugin.ConfigManager.SaveCurrentAccount();
        ImGui.EndDisabled();
    }

    private bool DrawShop(BlundervilleSettings settings, IReadOnlyList<BlundervilleOffer> catalog)
    {
        var hideOwned = settings.HideOwned;
        var changed = shopEditor.Draw(settings.PurchaseTargets, ref hideOwned, catalog,
            "###BlundervillePurchaseGrid", "BV_UnitMgf", "BV_NeededMgf", "BV_TotalMgf");
        settings.HideOwned = hideOwned;
        return changed;
    }

    private static string EndingLabel(BlundervilleEndingLocation location) => Ui.T("BV_Inn" + location);

    internal static void DrawRegistration(BlundervilleRegistration registration, bool planned)
    {
        if (registration == BlundervilleRegistration.None) return;
        var size = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        origin.X += Math.Max(0, (ImGui.GetContentRegionAvail().X - size) * .5f);
        ImGui.SetCursorScreenPos(origin);
        var owned = registration == BlundervilleRegistration.Owned;
        var pending = !owned && planned;
        var unknown = registration == BlundervilleRegistration.Unknown && !pending;
        var color = owned ? MogtomePresentation.Rgb(0x77DA96) : pending ? MogtomePresentation.Rgb(0xFEDF79)
            : unknown ? MaterialTheme.Current.Colors.OnSurfaceVariant : MaterialTheme.Current.Colors.Error;
        if (pending || unknown)
        {
            var questionSize = MaterialText.Measure("?");
            MaterialText.AddText(ImGui.GetWindowDrawList(), origin + new Vector2((size - questionSize.X) * .5f, 0), ImGui.GetColorU32(color), "?");
        }
        else MaterialIcons.Draw(owned ? MaterialIcon.Check : MaterialIcon.Close, origin, size, color);
        ImGui.Dummy(new Vector2(size));
        if (registration != BlundervilleRegistration.None && ImGui.IsItemHovered())
            UiLayout.SetTooltip(Ui.T(owned ? "BV_Registered" : pending ? "BV_PlannedPurchase" : unknown ? "BV_RegistrationUnknown" : "BV_NotRegistered"));
    }

    private static void DrawSection(Action draw)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = UiLayout.AvailableWidth;
        ImGui.BeginGroup();
        draw();
        ImGui.EndGroup();
        var height = ImGui.GetItemRectSize().Y;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
    }
}
