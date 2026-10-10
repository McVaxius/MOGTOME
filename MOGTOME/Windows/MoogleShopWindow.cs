using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.UiDesign;

namespace MOGTOME.Windows;

public sealed class MoogleShopWindow : Window
{
    private readonly Plugin plugin;
    private ShopMissingResult? missingResult;
    private readonly AethertekUI.Dalamud.MaterialWindowMotion motion = new();
    public MoogleShopWindow(Plugin plugin) : base(Ui.T("Shop_Title") + "###MogtomeShop")
    {
        this.plugin = plugin;
        Size = new Vector2(820, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new Vector2(650, 330), MaximumSize = new Vector2(1600, 1000) };
    }
    public override void PreDraw()
    {
        WindowName = Ui.T("Shop_Title") + "###MogtomeShop";
        motion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }
    public override void PostDraw() { motion.Restore(this); plugin.Appearance.PaintBrandTitle(this); }
    public override void Draw()
    {
        motion.DrawChrome();
        var shop = plugin.MoogleShop;
        var settings = plugin.Configuration.MoogleShop;
        var scale = ImGuiHelpers.GlobalScale;
        var changed = false;
        using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading)) UiLayout.SingleLine(Ui.T("Shop_Title"));
        UiLayout.Wrapped(shop.Status.Render());
        ImGui.BeginDisabled(!shop.Ready || shop.IsRunning || plugin.IsMoogleShopActionQueued || plugin.IsEngineStartQueued ||
            plugin.Engine?.IsRunning == true || plugin.Blunderville.IsRunning || plugin.IsBlundervilleActionQueued ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InDutyQueue] || Plugin.Condition[ConditionFlag.InCombat]);
        if (UiLayout.IconButton(Ui.L("BV_Buy"), MaterialIcon.Cart)) plugin.RequestMoogleShopBuy();
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Main_Stop")));
        if (UiLayout.IconButton(Ui.L("Main_Stop"), MaterialIcon.Stop)) plugin.StopMoogleShop();
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Shop_Refresh")));
        ImGui.BeginDisabled(!shop.Ready || shop.IsRunning || plugin.IsMoogleShopActionQueued);
        if (UiLayout.IconButton(Ui.L("Shop_Refresh"), MaterialIcon.Refresh)) plugin.RefreshMoogleShop();
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Shop_Clear")));
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || shop.IsRunning || plugin.IsMoogleShopActionQueued ||
            settings.PurchaseTargets.Count == 0);
        if (UiLayout.Button(Ui.L("Shop_Clear"))) { settings.PurchaseTargets.Clear(); missingResult = null; changed = true; }
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.BeginDisabled(!plugin.CanSelectUiLanguage || shop.IsRunning || plugin.IsMoogleShopActionQueued);
        if (settings.PurchaseReviewRequired)
        {
            UiLayout.Wrapped(Ui.T("Shop_ReviewRequired"));
            if (UiLayout.Button(Ui.L("Shop_Reviewed")))
                GameHelpers.QueueFrameworkAction("Moogle shop", "Review", TimeSpan.Zero, shop.AcknowledgeReview);
        }
        UiLayout.SingleLine(Ui.T("Shop_City"));
        ImGui.SameLine();
        var city = (int)settings.City;
        ImGui.SetNextItemWidth(Math.Min(230 * scale, UiLayout.AvailableWidth));
        if (UiLayout.Combo("###MoogleShopCity", ref city, new[] { Ui.T("BV_InnLimsa"), Ui.T("BV_InnUldah"), Ui.T("BV_InnGridania") }, 3))
        { settings.City = (MoogleShopCity)city; changed = true; }
        var currencies = shop.Catalog.Select(o => o.TomestoneId).Distinct().ToArray();
        if (currencies.Length > 0)
        {
            UiLayout.SingleLine(Ui.T("Shop_Currency"));
            ImGui.SameLine();
            var selected = Math.Max(0, Array.IndexOf(currencies, shop.CurrencyId));
            ImGui.SetNextItemWidth(UiLayout.AvailableWidth);
            if (UiLayout.Combo("###MoogleShopCurrency", ref selected, currencies.Select(id => Ui.Item(id).Render()).ToArray(), currencies.Length))
                shop.CurrencyId = currencies[selected];
        }
        var hideOwned = settings.HideOwned;
        if (UiLayout.Checkbox(Ui.L("Shop_HideOwned"), ref hideOwned)) { settings.HideOwned = hideOwned; changed = true; }
        changed |= DrawGrid(settings, shop.SelectedCatalog, shop.Catalog);
        ImGui.EndDisabled();
        if (changed) plugin.ConfigManager.SaveCurrentAccount();
    }

    private bool DrawGrid(MoogleShopSettings settings, IReadOnlyList<MoogleShopOffer> catalog, IReadOnlyList<MoogleShopOffer> completeCatalog)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var offers = completeCatalog.GroupBy(o => o.ItemId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var allRows = catalog.Select(o => o.ItemId).Concat(settings.PurchaseTargets.Keys).Distinct()
            .Select(id => (Id: id, Name: Ui.Item(id).Render())).OrderBy(r => r.Name, StringComparer.Create(Ui.Culture, true)).ToArray();
        var storedOwnership = plugin.XaDatabase.ReadOwnership(allRows.Select(row => row.Id));
        var inventory = allRows.ToDictionary(row => row.Id, row =>
            BlundervilleGameAdapter.TryInventory(row.Id, out var count, out _) ? (int?)count : null);
        var ownership = allRows.ToDictionary(row => row.Id, row => BlundervilleGameAdapter.Ownership(row.Id, inventory[row.Id], storedOwnership.GetValueOrDefault(row.Id)));
        var eligibility = allRows.ToDictionary(row => row.Id, row => offers.TryGetValue(row.Id, out var offer)
            ? ShopOfferEligibility.Read(offer.Gate) : ShopOfferAvailability.Unknown);
        var rows = allRows.Where(row => eligibility[row.Id] != ShopOfferAvailability.Locked &&
            (!settings.HideOwned || ownership[row.Id] != BlundervilleRegistration.Owned)).ToArray();
        var changed = false;
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Shop_SelectMissing")));
        if (UiLayout.Button(Ui.L("Shop_SelectMissing")))
        {
            missingResult = ShopMissingSelection.Apply(settings.PurchaseTargets,
                catalog.GroupBy(o => o.ItemId).Where(g => g.Count() == 1).Select(g => g.Single())
                    .Select(o => (o.ItemId, o.MissingKind,
                        BlundervilleGameAdapter.SelectionState(o.ItemId, inventory[o.ItemId], ownership[o.ItemId]), eligibility[o.ItemId])));
            changed = missingResult.Value.Added > 0;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Shop_SelectMissingHelp"));
        if (missingResult is { } result) UiLayout.Wrapped(Ui.T("Shop_SelectMissingResult",
            result.Added, result.UnknownOwnership, result.UnknownAcquisition, result.UnknownEligibility));
        var captions = new[] { Ui.T("BV_Item"), "###Cart", "###Bag", "###Price", "###Total", "###Registration" };
        var tips = new[] { Ui.T("BV_Item"), Ui.T("BV_Wanted"), Ui.T("BV_OnHand"), Ui.T("Shop_UnitPrice"), Ui.T("Shop_RowPrice"), Ui.T("BV_RegistrationHelp") };
        using var controls = MaterialTable.PushControls();
        using var tightRows = MogtomePresentation.Compact ? MaterialTable.PushTightRows() : default;
        var pad = ImGui.GetStyle().CellPadding;
        var widths = new[] { Math.Max(120 * scale, rows.Select(r => MaterialText.Measure(r.Name).X).DefaultIfEmpty().Max()),
            64 * scale, 58 * scale, 88 * scale, 100 * scale, 26 * scale };
        var inner = widths.Sum() + pad.X * 12 + 2;
        var scroll = inner > UiLayout.AvailableWidth;
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), ImGui.GetTextLineHeight()) + pad.Y * 2;
        var footerLines = completeCatalog.SelectMany(o => o.Costs).Select(c => c.ItemId).Distinct().Count();
        var footerHeight = (Math.Max(1, footerLines) + 1) * (ImGui.GetTextLineHeightWithSpacing() * 2);
        var height = Math.Max(rowHeight * 2, Math.Min(rowHeight * (rows.Length + 1) + (scroll ? ImGui.GetStyle().ScrollbarSize : 0) + pad.Y * 2,
            ImGui.GetContentRegionAvail().Y - footerHeight));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.ScrollY;
        if (scroll) flags |= ImGuiTableFlags.ScrollX;
        if (rows.Length == 0) UiLayout.Wrapped(Ui.T(allRows.Length == 0 ? "Shop_NoCatalog" : "Shop_EmptyView"));
        else if (ImGui.BeginTable("###MoogleShopGrid", 6, flags, new Vector2(UiLayout.AvailableWidth, height), scroll ? inner : 0))
        {
            try
            {
                ImGui.TableSetupColumn(captions[0], ImGuiTableColumnFlags.WidthStretch, widths[0]);
                for (var col = 1; col < widths.Length; col++) ImGui.TableSetupColumn(captions[col], ImGuiTableColumnFlags.WidthFixed, widths[col]);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers, ImGui.GetFrameHeight());
                for (var col = 0; col < captions.Length; col++)
                {
                    if (!ImGui.TableSetColumnIndex(col)) continue;
                    ImGui.PushID(col);
                    try
                    {
                        var ink = ImGui.GetColorU32(ImGuiCol.Text);
                        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
                        ImGui.TableHeader(captions[col]);
                        ImGui.PopStyleColor();
                        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
                        var icon = col == 1 ? MaterialIcon.Cart : col == 2 ? MaterialIcon.Backpack : MaterialIcon.None;
                        if (icon != MaterialIcon.None)
                        {
                            var side = ImGui.GetTextLineHeight();
                            MaterialIcons.Draw(icon, (min + max - new Vector2(side)) * .5f, side, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                        }
                        else
                        {
                            var label = col == 0 ? captions[0] : col == 3 ? "$" : col == 4 ? "$$" : "?";
                            var size = MaterialText.Measure(label);
                            MaterialText.AddText(ImGui.GetWindowDrawList(), new Vector2(col == 0 ? min.X : (min.X + max.X - size.X) * .5f,
                                (min.Y + max.Y - size.Y) * .5f), ink, label);
                        }
                        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(tips[col]);
                    }
                    finally { ImGui.PopID(); }
                }
                foreach (var (id, name) in rows)
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                    ImGui.PushID((int)id);
                    try
                    {
                        var desired = settings.PurchaseTargets.GetValueOrDefault(id);
                        var known = inventory[id].HasValue;
                        var count = inventory[id].GetValueOrDefault();
                        offers.TryGetValue(id, out var offer);
                        ImGui.TableSetColumnIndex(0); ImGui.AlignTextToFramePadding(); UiLayout.SingleLine(name);
                        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(name + (eligibility[id] == ShopOfferAvailability.Unknown
                            ? "\n" + Ui.T("Shop_EligibilityUnknown") : string.Empty));
                        ImGui.TableSetColumnIndex(1); ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt("###Desired", ref desired, 0, 0))
                        {
                            desired = Math.Max(0, desired);
                            if (desired == 0) settings.PurchaseTargets.Remove(id); else settings.PurchaseTargets[id] = desired;
                            changed = true;
                        }
                        ImGui.TableSetColumnIndex(2); ImGui.AlignTextToFramePadding(); UiLayout.SingleLine(known ? count.ToString(Ui.Culture) : "?");
                        var transactions = offer != null && known ? MoogleShopMath.Transactions(desired, count, offer.ReceiveCount) : 0;
                        var costKnown = desired == 0 || known && (count >= desired || offer != null);
                        ImGui.TableSetColumnIndex(3); ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(offer == null ? "?" : string.Join(" + ", offer.Costs.Select(c => ((decimal)c.Count / offer.ReceiveCount).ToString("0.##", Ui.Culture))));
                        if (offer != null && ImGui.IsItemHovered()) UiLayout.SetTooltip(CostTooltip(offer, 1) + "\n" + Ui.T("Shop_Bundle", offer.ReceiveCount));
                        ImGui.TableSetColumnIndex(4); ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(!costKnown ? "?" : transactions == 0 || offer == null ? "0" :
                            string.Join(" + ", offer.Costs.Select(c => ((ulong)transactions * c.Count).ToString(Ui.Culture))));
                        if (offer != null && ImGui.IsItemHovered()) UiLayout.SetTooltip(CostTooltip(offer, (ulong)transactions));
                        ImGui.TableSetColumnIndex(5); ImGui.AlignTextToFramePadding();
                        BlundervilleWindow.DrawRegistration(ownership[id], desired > (known ? count : 0));
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
        }
        var costs = MoogleShopMath.TotalCosts(settings.PurchaseTargets, offers, id => inventory.GetValueOrDefault(id), out var totalsKnown);
        foreach (var id in completeCatalog.SelectMany(o => o.Costs).Select(c => c.ItemId).Distinct())
        {
            var known = BlundervilleGameAdapter.TryInventory(id, out var balance, out _);
            var total = costs.GetValueOrDefault(id);
            UiLayout.Wrapped(Ui.T("Shop_Balance", Ui.Item(id), known ? balance.ToString(Ui.Culture) : "?", totalsKnown ? total.ToString(Ui.Culture) : "?"));
        }
        UiLayout.TextDisabled(Ui.T("Shop_Help"));
        return changed;
    }
    private static string CostTooltip(MoogleShopOffer offer, ulong transactions)
        => string.Join('\n', offer.Costs.Select(c => $"{Ui.Item(c.ItemId).Render()}: {(transactions * c.Count).ToString(Ui.Culture)}"));
}
