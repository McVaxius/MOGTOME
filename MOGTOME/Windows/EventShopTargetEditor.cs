using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.UiDesign;

namespace MOGTOME.Windows;

internal sealed class EventShopTargetEditor
{
    private readonly Plugin plugin;
    private ShopMissingResult? missingResult;
    private string keyboundSearch = "";
    private int keyboundCategory;
    private static readonly string[] KeyboundCategories =
    [
        "KB_AllRewards", "KB_CategoryGlamour", "KB_CategoryFashionAccessory", "KB_CategoryMount",
        "KB_CategoryMinion", "KB_CategoryOrchestrion", "KB_CategoryFramerKit", "KB_CategoryEmote", "KB_CategoryCoffer", "KB_CategoryOther",
    ];

    internal EventShopTargetEditor(Plugin plugin) => this.plugin = plugin;

    internal bool Draw(Dictionary<uint, int> purchaseTargets, ref bool hideOwned, IReadOnlyList<BlundervilleOffer> catalog,
        string gridId, string unitPriceKey, string rowCostKey, string totalKey,
        string noCatalogKey = "BV_NoCatalog", bool prepareBeforeFestival = false)
    {
        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;
        using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
            UiLayout.SingleLine(Ui.T("BV_ShopTargets"));
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Shop_Clear")));
        ImGui.BeginDisabled(purchaseTargets.Count == 0);
        if (UiLayout.Button(Ui.L("Shop_Clear"))) { purchaseTargets.Clear(); missingResult = null; changed = true; }
        ImGui.EndDisabled();
        UiLayout.SameLineIfFits(MaterialText.Measure(Ui.T("Shop_HideOwned")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        var hideOwnedValue = hideOwned;
        if (UiLayout.Checkbox(Ui.L("Shop_HideOwned"), ref hideOwnedValue)) { hideOwned = hideOwnedValue; changed = true; }
        var data = ReadShopData(purchaseTargets, catalog, prepareBeforeFestival, nativeOrder: false);
        var offers = data.Offers;
        var allRows = data.Rows;
        var inventory = data.Inventory;
        var ownership = data.Ownership;
        var eligibility = data.Eligibility;
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth(Ui.T("Shop_SelectMissing")));
        if (UiLayout.Button(Ui.L("Shop_SelectMissing")))
        {
            missingResult = ShopMissingSelection.Apply(purchaseTargets,
                catalog.GroupBy(o => o.ItemId).Where(g => g.Count() == 1).Select(g => g.Single())
                    .Select(o => (o.ItemId, o.MissingKind,
                        BlundervilleGameAdapter.SelectionState(o.ItemId, inventory[o.ItemId], ownership[o.ItemId]), eligibility[o.ItemId])));
            changed |= missingResult.Value.Added > 0;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("Shop_SelectMissingHelp"));
        if (missingResult is { } result) UiLayout.Wrapped(Ui.T("Shop_SelectMissingResult",
            result.Added, result.UnknownOwnership, result.UnknownAcquisition, result.UnknownEligibility));
        var rows = allRows.Where(row => eligibility[row.Id] != ShopOfferAvailability.Locked &&
            (!hideOwnedValue || ownership[row.Id] != BlundervilleRegistration.Owned)).ToArray();
        if (allRows.Length == 0)
        {
            UiLayout.TextDisabled(Ui.T(noCatalogKey));
            UiLayout.Wrapped(Ui.T(totalKey, 0));
            return changed;
        }

        var captions = new[] { Ui.T("BV_Item"), "###Wanted", "###OnHand", "###UnitMgf", "###NeededMgf", "###Registration" };
        var tips = new[] { Ui.T("BV_Item"), Ui.T("BV_Wanted"), Ui.T("BV_OnHand"), Ui.T(unitPriceKey), Ui.T(rowCostKey), Ui.T("BV_RegistrationHelp") };
        var controlRoot = ImGui.GetID(""); // Retain the count editor's original window/row identity inside the table.
        using var tableControls = MaterialTable.PushControls();
        using var tightRows = MogtomePresentation.Compact ? MaterialTable.PushTightRows() : default;
        var padding = ImGui.GetStyle().CellPadding;
        var widths = new[]
        {
            Math.Max(MaterialText.Measure(captions[0]).X, rows.Select(row => MaterialText.Measure(row.Name).X).DefaultIfEmpty().Max()),
            64 * scale, 58 * scale, 72 * scale, 82 * scale, 26 * scale,
        };
        var innerWidth = widths.Sum() + padding.X * 12 + 2;
        var scroll = innerWidth > UiLayout.AvailableWidth;
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), rows.Select(row => MaterialText.Measure(row.Name).Y).DefaultIfEmpty().Max()) + padding.Y * 2;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.ScrollY;
        if (scroll) flags |= ImGuiTableFlags.ScrollX;
        var height = Math.Min(rowHeight * (rows.Length + 1) + (scroll ? ImGui.GetStyle().ScrollbarSize : 0) + padding.Y * 2,
            (MogtomePresentation.Compact ? 280 : 320) * scale);
        if (rows.Length == 0) UiLayout.TextDisabled(Ui.T("Shop_EmptyView"));
        else if (ImGui.BeginTable(gridId, 6, flags, new Vector2(UiLayout.AvailableWidth, height), scroll ? innerWidth : 0))
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
                        var desired = purchaseTargets.GetValueOrDefault(id);
                        var known = inventory[id].HasValue;
                        var current = inventory[id].GetValueOrDefault();
                        offers.TryGetValue(id, out var offer);
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(name);
                        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(name + (eligibility[id] == ShopOfferAvailability.Unknown
                            ? "\n" + Ui.T("Shop_EligibilityUnknown") : string.Empty));
                        ImGui.TableSetColumnIndex(1);
                        changed |= DrawTarget(id, purchaseTargets, ref desired);
                        ImGui.TableSetColumnIndex(2);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(known ? current.ToString(Ui.Culture) : "?");
                        ImGui.TableSetColumnIndex(3);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(offer?.ReceiveCount > 0 ? ((decimal)offer.Price / offer.ReceiveCount).ToString("0.##", Ui.Culture) : "?");
                        var deficit = known ? BlundervilleProgress.Deficit(desired, current) : 0;
                        var costKnown = desired == 0 || (known && (deficit == 0 || (offer?.ReceiveCount > 0 && deficit % offer.ReceiveCount == 0)));
                        var cost = costKnown && deficit > 0 ? (ulong)deficit / offer!.ReceiveCount * offer.Price : 0;
                        ImGui.TableSetColumnIndex(4);
                        ImGui.AlignTextToFramePadding();
                        UiLayout.SingleLine(costKnown ? cost.ToString(Ui.Culture) : "?");
                        ImGui.TableSetColumnIndex(5);
                        ImGui.AlignTextToFramePadding();
                        BlundervilleWindow.DrawRegistration(ownership[id], desired > (known ? current : 0));
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
        }
        var total = BlundervilleProgress.TotalMgf(purchaseTargets, offers, id => inventory.GetValueOrDefault(id), out var totalKnown);
        UiLayout.Wrapped(Ui.T(totalKey, totalKnown ? total.ToString(Ui.Culture) : "?"));
        return changed;
    }

    internal bool DrawKeybound(KeyboundBrawlerSettings settings, IReadOnlyList<BlundervilleOffer> catalog, uint? wallet)
    {
        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;
        var data = ReadShopData(settings.PurchaseTargets, catalog, prepareBeforeFestival: true, nativeOrder: true);
        foreach (var (id, _) in data.Rows)
        {
            // Repeatable rewards have no unlock flag; known copies still belong in
            // the Owned column/filter. This does not make them Select Missing targets.
            if (data.Ownership[id] == BlundervilleRegistration.None)
                data.Ownership[id] = data.Inventory[id] is { } count
                    ? count > 0 ? BlundervilleRegistration.Owned : BlundervilleRegistration.Missing
                    : BlundervilleRegistration.Unknown;
        }
        var visible = FilterKeyboundRows(data, settings.HideOwned);
        var headingOrigin = ImGui.GetCursorScreenPos();
        var headingWidth = UiLayout.AvailableWidth;
        using (plugin.Appearance.Font(MogtomeFontRole.CompactHeading))
            UiLayout.SingleLine(Ui.T("BV_ShopTargets"));
        var countText = Ui.T("KB_RewardCount", visible.Length, data.Rows.Length);
        UiLayout.SameLineIfFits(MaterialText.Measure(countText).X);
        UiLayout.TextDisabled(countText);
        var oneOfEachKnown = data.Offers.Count > 0 && data.Offers.Values.All(offer => offer.ReceiveCount > 0);
        var oneOfEach = oneOfEachKnown
            ? data.Offers.Values.Sum(offer => (decimal)offer.Price / offer.ReceiveCount).ToString("#,0.##", Ui.Culture) : "?";
        var catalogTotal = Ui.T("KB_OneOfEach", oneOfEach);
        var catalogTotalWidth = MaterialText.Measure(catalogTotal).X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + catalogTotalWidth <= headingOrigin.X + headingWidth)
            ImGui.SetCursorScreenPos(new(headingOrigin.X + headingWidth - catalogTotalWidth, headingOrigin.Y));
        UiLayout.SingleLine(catalogTotal);

        using (MaterialControls.Push(MaterialControlContext.Toolbar))
        {
            var categoryNames = KeyboundCategories.Select(key => Ui.T(key)).ToArray();
            var categoryWidth = categoryNames.Max(text => MaterialText.Measure(text).X)
                + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
            var hideWidth = MaterialText.Measure(Ui.T("Shop_HideOwned")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            var missingWidth = UiLayout.IconButtonWidth(Ui.T("Shop_SelectMissing"));
            var clearWidth = UiLayout.IconButtonWidth(Ui.T("Shop_Clear"));
            var width = UiLayout.AvailableWidth;
            var searchWidth = Math.Min(width, Math.Max(130 * scale,
                Math.Min(300 * scale, width - categoryWidth - hideWidth - missingWidth - clearWidth - ImGui.GetStyle().ItemSpacing.X * 4)));
            ImGui.SetNextItemWidth(searchWidth);
            using (MaterialText.PushLineHeight(keyboundSearch, Ui.T("KB_SearchRewards")))
                MaterialShapedInput.SingleLine("###KeyboundRewardSearch", Ui.T("KB_SearchRewards"), ref keyboundSearch, 256);
            UiLayout.SameLineIfFits(categoryWidth);
            ImGui.SetNextItemWidth(Math.Min(categoryWidth, UiLayout.AvailableWidth));
            UiLayout.Combo("###KeyboundRewardCategory", ref keyboundCategory, categoryNames, categoryNames.Length);
            UiLayout.SameLineIfFits(hideWidth);
            var hideOwned = settings.HideOwned;
            if (UiLayout.Checkbox(Ui.L("Shop_HideOwned"), ref hideOwned)) { settings.HideOwned = hideOwned; changed = true; }
            UiLayout.SameLineIfFits(missingWidth);
            if (UiLayout.Button(Ui.L("Shop_SelectMissing")))
            {
                missingResult = ShopMissingSelection.Apply(settings.PurchaseTargets,
                    catalog.GroupBy(offer => offer.ItemId).Where(group => group.Count() == 1).Select(group => group.Single())
                        .Where(offer => KeyboundBrawlerShop.IsSelectMissingReward(offer.ItemId))
                        .Select(offer => (offer.ItemId,
                            offer.MissingKind,
                            BlundervilleGameAdapter.SelectionState(offer.ItemId, data.Inventory[offer.ItemId], data.Ownership[offer.ItemId]),
                            data.Eligibility[offer.ItemId])));
                changed |= missingResult.Value.Added > 0;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiLayout.SetTooltip(Ui.T("KB_SelectMissingHelp"));
            UiLayout.SameLineIfFits(clearWidth);
            ImGui.BeginDisabled(settings.PurchaseTargets.Count == 0);
            try
            {
                if (UiLayout.Button(Ui.L("Shop_Clear"))) { settings.PurchaseTargets.Clear(); missingResult = null; changed = true; }
            }
            finally { ImGui.EndDisabled(); }
        }
        if (missingResult is { } result)
            UiLayout.Wrapped(Ui.T("Shop_SelectMissingResult", result.Added, result.UnknownOwnership, result.UnknownAcquisition, result.UnknownEligibility));
        visible = FilterKeyboundRows(data, settings.HideOwned);

        var ownedHelp = Ui.T("KB_OwnedHelp");
        var footer = KeyboundFooter(data, settings.PurchaseTargets, wallet);
        var footerHeight = FooterRowHeight(footer.Selection, footer.Total, UiLayout.AvailableWidth)
            + FooterRowHeight(ownedHelp, footer.Balance, UiLayout.AvailableWidth) + ImGui.GetStyle().ItemSpacing.Y * 3;
        var tableHeight = Math.Max(ImGui.GetFrameHeight() * 2, ImGui.GetContentRegionAvail().Y - footerHeight);
        if (data.Rows.Length == 0) UiLayout.TextDisabled(Ui.T("KB_NoCatalog"));
        changed |= DrawKeyboundTable(data, visible, settings.PurchaseTargets, tableHeight);

        footer = KeyboundFooter(data, settings.PurchaseTargets, wallet);
        ImGui.Separator();
        DrawKeyboundFooterRow(footer.Selection, footer.Total);
        DrawKeyboundFooterRow(ownedHelp, footer.Balance);
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("BV_RegistrationHelp"));
        return changed;
    }

    private (uint Id, string Name)[] FilterKeyboundRows(ShopData data, bool hideOwned)
    {
        var query = keyboundSearch.Trim();
        return data.Rows.Where(row => (keyboundCategory == 0 || KeyboundBrawlerShop.CategoryKey(row.Id) == KeyboundCategories[keyboundCategory])
            && (!hideOwned || data.Ownership[row.Id] != BlundervilleRegistration.Owned)
            && (query.Length == 0 || Ui.Culture.CompareInfo.IndexOf(row.Name, query, System.Globalization.CompareOptions.IgnoreCase) >= 0)).ToArray();
    }

    private bool DrawKeyboundTable(ShopData data, (uint Id, string Name)[] rows, Dictionary<uint, int> targets, float height)
    {
        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;
        var captions = new[] { Ui.T("KB_Reward"), Ui.T("KB_OnHand"), Ui.T("KB_Target"), Ui.T("KB_UnitMgc"), Ui.T("KB_NeedMgc"), Ui.T("KB_Owned") };
        var tips = new[] { Ui.T("KB_Reward"), Ui.T("BV_OnHand"), Ui.T("BV_Wanted"), Ui.T("Shop_UnitPrice"), Ui.T("Shop_RowPrice"), Ui.T("BV_RegistrationHelp") };
        var controlRoot = ImGui.GetID("");
        using var controls = MaterialTable.PushControls();
        using var tightRows = MaterialTable.PushTightRows();
        using var lineHeight = MaterialText.PushLineHeight(captions.Concat(rows.Select(row => row.Name)).ToArray());
        var padding = ImGui.GetStyle().CellPadding;
        var widths = new[] { 270 * scale, 64 * scale, 72 * scale, 76 * scale, 86 * scale, 58 * scale };
        for (var column = 1; column < widths.Length; column++)
            widths[column] = Math.Max(widths[column], MaterialText.Measure(captions[column]).X + ImGui.GetStyle().FramePadding.X * 2);
        var innerWidth = widths.Sum() + padding.X * 12 + ImGui.GetStyle().ScrollbarSize + 2;
        var scroll = innerWidth > UiLayout.AvailableWidth;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.ScrollY;
        if (scroll) flags |= ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("###KeyboundPurchaseGrid", captions.Length, flags, new(UiLayout.AvailableWidth, height), scroll ? innerWidth : 0)) return false;
        try
        {
            ImGui.TableSetupColumn(captions[0], ImGuiTableColumnFlags.WidthStretch, widths[0]);
            for (var column = 1; column < widths.Length; column++) ImGui.TableSetupColumn(captions[column], ImGuiTableColumnFlags.WidthFixed, widths[column]);
            ImGui.TableSetupScrollFreeze(0, 1);
            var headerTop = ImGui.GetCursorScreenPos().Y;
            UiLayout.TableHeadersRow();
            if (ImGui.IsWindowHovered() && ImGui.GetMousePos().Y >= headerTop
                && ImGui.GetMousePos().Y < ImGui.GetCursorScreenPos().Y)
                for (var column = 0; column < tips.Length; column++)
                    if ((ImGui.TableGetColumnFlags(column) & ImGuiTableColumnFlags.IsHovered) != 0)
                        UiLayout.SetTooltip(tips[column]);
            var rowHeight = Math.Max(ImGui.GetFrameHeight(), 24 * scale);
            if (rows.Length == 0)
            {
                ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                UiLayout.TextDisabled(Ui.T("Shop_EmptyView"));
            }
            foreach (var (id, name) in rows)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                ImGuiP.PushOverrideID(controlRoot);
                try
                {
                    var desired = targets.GetValueOrDefault(id);
                    if (desired > 0) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, MaterialCanvas.Color(MaterialColor.Alpha(MaterialTheme.Current.Colors.Primary, .05f)));
                    ImGui.TableSetColumnIndex(0);
                    DrawKeyboundReward(id, name, data.Eligibility[id]);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.AlignTextToFramePadding();
                    DrawRightAligned(data.Inventory[id]?.ToString(Ui.Culture) ?? "?");
                    ImGui.TableSetColumnIndex(2);
                    changed |= DrawTarget(id, targets, ref desired);
                    data.Offers.TryGetValue(id, out var offer);
                    ImGui.TableSetColumnIndex(3);
                    ImGui.AlignTextToFramePadding();
                    DrawRightAligned(offer?.ReceiveCount > 0 ? ((decimal)offer.Price / offer.ReceiveCount).ToString("0.##", Ui.Culture) : "?");
                    var count = data.Inventory[id];
                    var deficit = count is { } current ? BlundervilleProgress.Deficit(desired, current) : 0;
                    var costKnown = desired == 0 || count.HasValue && (deficit == 0 || offer?.ReceiveCount > 0 && deficit % offer.ReceiveCount == 0);
                    ImGui.TableSetColumnIndex(4);
                    ImGui.AlignTextToFramePadding();
                    DrawRightAligned(costKnown ? (deficit > 0 ? (ulong)deficit / offer!.ReceiveCount * offer.Price : 0).ToString(Ui.Culture) : "?");
                    ImGui.TableSetColumnIndex(5);
                    ImGui.AlignTextToFramePadding();
                    BlundervilleWindow.DrawRegistration(data.Ownership[id], planned: false);
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.EndTable(); }
        return changed;
    }

    private static void DrawKeyboundReward(uint id, string name, ShopOfferAvailability eligibility)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(id);
        if (item is { } row && row.Icon != 0)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(row.Icon)).GetWrapOrEmpty();
            MaterialCanvas.DrawImage(ImGui.GetWindowDrawList(), icon.Handle, icon.Size, origin, origin + new Vector2(24 * scale));
        }
        ImGui.Dummy(new(24 * scale));
        ImGui.SameLine(0, 8 * scale);
        ImGui.AlignTextToFramePadding();
        var textOrigin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var category = Ui.T(KeyboundBrawlerShop.CategoryKey(id));
        var categoryWidth = MaterialText.Measure(category).X;
        var size = MaterialText.Measure(name);
        var list = ImGui.GetWindowDrawList();
        list.PushClipRect(textOrigin, textOrigin + new Vector2(width, Math.Max(size.Y, MaterialText.Measure(category).Y)), true);
        try
        {
            MaterialText.AddText(list, textOrigin, ImGui.GetColorU32(ImGuiCol.Text), name);
            if (size.X + categoryWidth + 12 * scale <= width)
                MaterialText.AddText(list, new(textOrigin.X + width - categoryWidth, textOrigin.Y),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), category);
        }
        finally { list.PopClipRect(); }
        ImGui.Dummy(new(width, size.Y));
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(name + "\n" + category + (eligibility == ShopOfferAvailability.Unknown ? "\n" + Ui.T("Shop_EligibilityUnknown") : ""));
    }

    private static void DrawRightAligned(string text)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - MaterialText.Measure(text).X));
        UiLayout.SingleLine(text);
    }

    private static void DrawKeyboundFooterRow(string left, string right)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = UiLayout.AvailableWidth;
        var rightWidth = MaterialText.Measure(right).X;
        if (MaterialText.Measure(left).X + rightWidth + ImGui.GetStyle().ItemSpacing.X <= width)
        {
            UiLayout.SingleLine(left);
            ImGui.SetCursorScreenPos(new(origin.X + width - rightWidth, origin.Y));
            UiLayout.SingleLine(right);
        }
        else
        {
            UiLayout.Wrapped(left);
            UiLayout.Wrapped(right);
        }
    }

    private static float FooterRowHeight(string left, string right, float width)
        => MaterialText.Measure(left).X + MaterialText.Measure(right).X + ImGui.GetStyle().ItemSpacing.X <= width
            ? Math.Max(MaterialText.Measure(left).Y, MaterialText.Measure(right).Y) + ImGui.GetStyle().ItemSpacing.Y
            : MaterialText.Measure(left, wrapWidth: width).Y + MaterialText.Measure(right, wrapWidth: width).Y + ImGui.GetStyle().ItemSpacing.Y * 2;

    private static (string Selection, string Total, string Balance) KeyboundFooter(ShopData data, Dictionary<uint, int> targets, uint? wallet)
    {
        var total = BlundervilleProgress.TotalMgf(targets, data.Offers, id => data.Inventory.GetValueOrDefault(id), out var totalKnown);
        var selected = targets.Count(target => target.Value > 0);
        var copiesKnown = targets.Where(target => target.Value > 0).All(target => data.Inventory.GetValueOrDefault(target.Key).HasValue);
        var copies = targets.Where(target => target.Value > 0)
            .Sum(target => (long)BlundervilleProgress.Deficit(target.Value, data.Inventory.GetValueOrDefault(target.Key).GetValueOrDefault()));
        var balance = !totalKnown ? Ui.T("KB_CostUnknown") : wallet is null ? Ui.T("KB_WalletUnknown")
            : total > wallet.Value ? Ui.T("KB_Shortfall", (total - wallet.Value).ToString(Ui.Culture))
            : Ui.T("KB_AfterPurchases", (wallet.Value - total).ToString(Ui.Culture));
        return (Ui.T("KB_SelectedSummary", selected, copiesKnown ? copies.ToString(Ui.Culture) : "?"),
            Ui.T("KB_TotalCurrency", totalKnown ? total.ToString(Ui.Culture) : "?"), balance);
    }

    private ShopData ReadShopData(Dictionary<uint, int> targets, IReadOnlyList<BlundervilleOffer> catalog,
        bool prepareBeforeFestival, bool nativeOrder)
    {
        var offers = catalog.GroupBy(offer => offer.ItemId).Select(group => group.First()).ToDictionary(offer => offer.ItemId);
        var rows = catalog.Select(offer => offer.ItemId).Concat(targets.Keys).Distinct().Select(id => (Id: id, Name: Ui.Item(id).Render()));
        var allRows = (nativeOrder ? rows : rows.OrderBy(row => row.Name, StringComparer.Create(Ui.Culture, true))).ToArray();
        var storedStocks = plugin.XaDatabase.ReadStocks(allRows.Select(row => row.Id));
        var inventory = allRows.ToDictionary(row => row.Id, row =>
            plugin.XaDatabase.TryCount(row.Id, storedStocks.GetValueOrDefault(row.Id), out var count) ? (int?)count : null);
        var ownership = allRows.ToDictionary(row => row.Id, row => BlundervilleGameAdapter.Ownership(row.Id, inventory[row.Id],
            inventory[row.Id].HasValue ? AethertekUI.Dalamud.XaItemOwnershipState.Missing : AethertekUI.Dalamud.XaItemOwnershipState.Unknown));
        var eligibility = allRows.ToDictionary(row => row.Id, row => offers.TryGetValue(row.Id, out var offer)
            ? ShopOfferEligibility.Read(prepareBeforeFestival ? offer.Gate with { Festival = 0, FestivalPhase = 0 } : offer.Gate)
            : ShopOfferAvailability.Unknown);
        return new(offers, allRows, inventory, ownership, eligibility);
    }

    private static bool DrawTarget(uint id, Dictionary<uint, int> targets, ref int desired)
    {
        ImGui.PushID((int)id);
        try
        {
            ImGui.SetNextItemWidth(-1);
            if (!ImGui.InputInt("###DesiredInventoryCount", ref desired, 0, 0)) return false;
            desired = Math.Max(0, desired);
            if (desired == 0) targets.Remove(id);
            else targets[id] = desired;
            return true;
        }
        finally { ImGui.PopID(); }
    }

    private sealed record ShopData(Dictionary<uint, BlundervilleOffer> Offers, (uint Id, string Name)[] Rows,
        Dictionary<uint, int?> Inventory, Dictionary<uint, BlundervilleRegistration> Ownership,
        Dictionary<uint, ShopOfferAvailability> Eligibility);

}
