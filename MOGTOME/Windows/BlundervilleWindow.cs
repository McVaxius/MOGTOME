using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private readonly Plugin plugin;
    public bool DebugVisible { get; set; }

    public BlundervilleWindow(Plugin plugin)
        : base(Ui.T("Window_BlundervilleFailer") + "###MogtomeBlunderville", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
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
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.Appearance.PaintBrandTitle(this); }

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
            UiLayout.Wrapped(Ui.T("BV_FarmHelp"));
            ImGui.EndGroup();
            var textHeight = ImGui.GetItemRectSize().Y;
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, Math.Max(imageHeight, textHeight)));
            if (UiLayout.IconButton(Ui.L("Blunderville_OpenMogtome"), MaterialIcon.ExternalLink, new Vector2(UiLayout.AvailableWidth, 0)))
                plugin.MainWindow.IsOpen = true;
        });
        ImGui.Separator();
        DrawControls();
    }

    private void DrawControls()
    {
        var service = plugin.Blunderville;
        UiLayout.Wrapped(service.Status.Render());
        var hasWallet = BlundervilleGameAdapter.TryWallet(out var wallet);
        UiLayout.Wrapped(Ui.T("BV_Progress", service.SessionCycles, hasWallet ? wallet.ToString(Ui.Culture) : "?"));
        var unavailable = !service.Ready || service.IsRunning || plugin.IsBlundervilleActionQueued ||
            plugin.IsEngineStartQueued || plugin.Engine?.IsRunning == true;
        ImGui.BeginDisabled(unavailable);
        if (UiLayout.Button(Ui.L("Main_Start"))) plugin.RequestBlundervilleAction(buy: false);
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("BV_Buy")));
        if (UiLayout.Button(Ui.L("BV_Buy"))) plugin.RequestBlundervilleAction(buy: true);
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
        changed |= DrawShop(settings, service.Catalog);
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
            if (ImGui.Combo("###BlundervilleReloadScenario", ref scenario, scenarios, scenarios.Length))
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
        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;
        var offers = catalog.GroupBy(o => o.ItemId).Select(g => g.First()).ToDictionary(o => o.ItemId);
        using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
            UiLayout.SingleLine(Ui.T("BV_ShopTargets"));
        var rows = offers.Keys.Concat(settings.PurchaseTargets.Keys).Distinct()
            .Select(id => (Id: id, Name: Ui.Item(id).Render()))
            .OrderBy(row => row.Name, StringComparer.Create(Ui.Culture, true)).ToArray();
        if (rows.Length == 0)
        {
            UiLayout.TextDisabled(Ui.T("BV_NoCatalog"));
            UiLayout.Wrapped(Ui.T("BV_TotalMgf", 0));
            return changed;
        }

        var captions = new[] { Ui.T("BV_Item"), "###Wanted", "###OnHand", "###UnitMgf", "###NeededMgf", "###Registration" };
        var tips = new[] { Ui.T("BV_Item"), Ui.T("BV_Wanted"), Ui.T("BV_OnHand"), Ui.T("BV_UnitMgf"), Ui.T("BV_NeededMgf"), Ui.T("BV_RegistrationHelp") };
        var controlRoot = ImGui.GetID(""); // Retain the count editor's original window/row identity inside the table.
        using var tableControls = MaterialTable.PushControls();
        var padding = ImGui.GetStyle().CellPadding;
        var widths = new[]
        {
            Math.Max(MaterialText.Measure(captions[0]).X, rows.Max(row => MaterialText.Measure(row.Name).X)),
            64 * scale, 58 * scale, 72 * scale, 82 * scale, 26 * scale,
        };
        var innerWidth = widths.Sum() + padding.X * 12 + 2;
        var scroll = innerWidth > UiLayout.AvailableWidth;
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), rows.Select(row => MaterialText.Measure(row.Name).Y).Max()) + padding.Y * 2;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.ScrollY;
        if (scroll) flags |= ImGuiTableFlags.ScrollX;
        var height = Math.Min(rowHeight * (rows.Length + 1) + (scroll ? ImGui.GetStyle().ScrollbarSize : 0) + padding.Y * 2,
            (MogtomePresentation.Compact ? 280 : 320) * scale);
        ulong total = 0;
        var totalKnown = true;
        if (ImGui.BeginTable("###BlundervillePurchaseGrid", 6, flags, new Vector2(UiLayout.AvailableWidth, height), scroll ? innerWidth : 0))
        {
            try
            {
                ImGui.TableSetupColumn(captions[0], ImGuiTableColumnFlags.WidthStretch, widths[0]);
                for (var column = 1; column < widths.Length; column++)
                    ImGui.TableSetupColumn(captions[column], ImGuiTableColumnFlags.WidthFixed, widths[column]);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers, ImGui.GetFrameHeight());
                for (var column = 0; column < captions.Length; column++)
                {
                    if (!ImGui.TableSetColumnIndex(column)) continue;
                    ImGui.PushID(column);
                    try
                    {
                        var ink = ImGui.GetColorU32(ImGuiCol.Text);
                        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
                        ImGui.TableHeader(captions[column]);
                        ImGui.PopStyleColor();
                        var min = ImGui.GetItemRectMin();
                        var max = ImGui.GetItemRectMax();
                        var icon = column == 1 ? MaterialIcon.Cart : column == 2 ? MaterialIcon.Backpack : MaterialIcon.None;
                        if (icon != MaterialIcon.None)
                        {
                            var side = ImGui.GetTextLineHeight();
                            MaterialIcons.Draw(icon, (min + max - new Vector2(side)) * .5f, side, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                        }
                        else
                        {
                            var text = column == 0 ? captions[0] : column == 3 ? "$" : column == 4 ? "$$" : "?";
                            var textSize = MaterialText.Measure(text);
                            MaterialText.AddText(ImGui.GetWindowDrawList(), new Vector2(column == 0 ? min.X : (min.X + max.X - textSize.X) * .5f,
                                (min.Y + max.Y - textSize.Y) * .5f), ink, text);
                        }
                        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(tips[column]);
                    }
                    finally { ImGui.PopID(); }
                }
                foreach (var (id, name) in rows)
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                    ImGuiP.PushOverrideID(controlRoot);
                    try
                    {
                        var desired = settings.PurchaseTargets.GetValueOrDefault(id);
                        var known = BlundervilleGameAdapter.TryInventory(id, out var current, out _);
                        offers.TryGetValue(id, out var offer);
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(name);
                        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(name);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.PushID((int)id);
                        try
                        {
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.InputInt("###DesiredInventoryCount", ref desired, 0, 0))
                            {
                                desired = Math.Max(0, desired);
                                if (desired == 0) settings.PurchaseTargets.Remove(id);
                                else settings.PurchaseTargets[id] = desired;
                                changed = true;
                            }
                        }
                        finally { ImGui.PopID(); }
                        ImGui.TableSetColumnIndex(2);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(known ? current.ToString(Ui.Culture) : "?");
                        ImGui.TableSetColumnIndex(3);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(offer?.ReceiveCount > 0 ? ((decimal)offer.Price / offer.ReceiveCount).ToString("0.##", Ui.Culture) : "?");
                        var deficit = known ? BlundervilleProgress.Deficit(desired, current) : 0;
                        var costKnown = desired == 0 || (known && (deficit == 0 || (offer?.ReceiveCount > 0 && deficit % offer.ReceiveCount == 0)));
                        var cost = costKnown && deficit > 0 ? (ulong)deficit / offer!.ReceiveCount * offer.Price : 0;
                        totalKnown &= costKnown;
                        total += cost;
                        ImGui.TableSetColumnIndex(4);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(costKnown ? cost.ToString(Ui.Culture) : "?");
                        ImGui.TableSetColumnIndex(5);
                        ImGui.AlignTextToFramePadding();
                        DrawRegistration(BlundervilleGameAdapter.Registration(id, known ? current : null), desired > (known ? current : 0));
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
        }
        else totalKnown = false;
        UiLayout.Wrapped(Ui.T("BV_TotalMgf", totalKnown ? total.ToString(Ui.Culture) : "?"));
        return changed;
    }

    private static string EndingLabel(BlundervilleEndingLocation location) => Ui.T("BV_Inn" + location);

    private static void DrawRegistration(BlundervilleRegistration registration, bool planned)
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
