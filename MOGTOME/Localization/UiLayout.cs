using System;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AethertekUI;
using MOGTOME.UiDesign;

namespace MOGTOME.Localization;

internal static class UiLayout
{
    private static float? panelRight;
    internal static float AvailableWidth => Math.Max(1, (panelRight ?? (ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X)) - ImGui.GetCursorScreenPos().X);
    internal static bool Button(string label, Vector2 size = default)
    {
        var visible = label.Split("##", 2)[0];
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        var padding = ImGui.GetStyle().FramePadding;
        using var height = MaterialText.PushLineHeight(visible);
        var material = MogtomePresentation.HasMaterial;
        var radius = ImGui.GetStyle().FrameRounding;
        var alpha = ImGui.GetStyle().Alpha;
        using var nativeButtons = material ? MaterialSkin.NativeButtonStyle() : MaterialLcars.NativeButtonStyle();
        using var rounding = new MaterialStyleScope();
        if (material) rounding.Style(ImGuiStyleVar.FrameRounding, radius);
        size.X = MaterialLayout.FitNextItemWidth(size.X, MaterialText.Measure(visible).X + ImGui.GetStyle().FramePadding.X * 2);
        var measured = MaterialText.Measure(visible);
        size.Y = Math.Max(size.Y, Math.Max(ImGui.GetTextLineHeight(), measured.Y) + padding.Y * 2);
        if (!material && !MaterialText.RequiresShaping(visible))
        {
            var pressed = ImGui.Button(label, size);
            MaterialLcars.Feedback(pressed);
            return pressed;
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool clicked;
        try
        {
            using var face = material ? MaterialSkin.TransparentButtonFace() : new MaterialStyleScope();
            clicked = ImGui.Button(label, size);
        }
        finally { ImGui.PopStyleColor(); }
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        if (material) MaterialSkin.PaintButton(min, max, alpha, radius);
        var list = ImGui.GetWindowDrawList(); list.PushClipRect(min, max, true);
        try { MaterialText.AddText(list, min + new Vector2((max.X-min.X-measured.X)*.5f, (max.Y-min.Y-measured.Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), visible); }
        finally { list.PopClipRect(); }
        MaterialLcars.Feedback(clicked);
        return clicked;
    }

    internal static bool SmallButton(string label)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0));
        try { return Button(label); }
        finally { ImGui.PopStyleVar(); }
    }

    internal static bool InputInt(string key, ref int value)
    {
        var caption = Ui.T(key);
        using var height = MaterialText.PushLineHeight(caption);
        var style = ImGui.GetStyle();
        var minimum = MathF.Ceiling(ImGui.CalcTextSize("-2147483648").X + style.FramePadding.X * 2);
        var captionWidth = MaterialText.Measure(caption).X + style.ItemInnerSpacing.X + 2 * MaterialTheme.Metrics.Scale;
        var available = AvailableWidth;
        if (minimum + captionWidth > available)
        {
            SingleLine(caption);
            ImGui.SetNextItemWidth(Math.Min(available, Math.Max(minimum, available * .65f)));
            using var compactField = MaterialSkin.NativeFieldStyle();
            return ImGui.InputInt("###" + key, ref value);
        }
        ImGui.SetNextItemWidth(MathF.Ceiling(Math.Max(minimum, Math.Min(available * .65f, available - captionWidth))));
        using var paintedCaption = new FieldCaption(Ui.L(key));
        using var field = MaterialSkin.NativeFieldStyle();
        return ImGui.InputInt(Ui.L(key), ref value);
    }

    internal static float IconButtonWidth(string label) => MaterialText.Measure(label.Split("##", 2)[0]).X + 32 * MaterialTheme.Metrics.Scale
        + 2 * MaterialControlMetrics.Measure(MaterialTheme.Metrics, ImGui.GetTextLineHeight(), MaterialControlContext.Toolbar).NativePadding.X;
    internal static float IconButtonHeight(string label, float width, float minimum)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var text = label.Split("##", 2)[0];
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        var paddingY = ImGui.GetStyle().FramePadding.Y;
        using var height = MaterialText.PushLineHeight(text);
        return Math.Max(minimum, Math.Max(MaterialControls.Metrics.Height,
            Math.Max(MaterialText.Measure(text).Y, 22 * scale) + 2 * paddingY));
    }
    internal static void SameLineIfFits(float width)
    {
        var right = panelRight ?? (ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X);
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }
    internal static bool IconButton(string raw, MaterialIcon icon, Vector2 size = default, string? display = null)
    {
        var text = display ?? raw.Split("##", 2)[0];
        var scale = MaterialTheme.Metrics.Scale;
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        var paddingY = ImGui.GetStyle().FramePadding.Y;
        using var height = MaterialText.PushLineHeight(text);
        var material = MogtomePresentation.HasMaterial;
        var radius = ImGui.GetStyle().FrameRounding;
        var alpha = ImGui.GetStyle().Alpha;
        using var nativeButtons = material ? MaterialSkin.NativeButtonStyle() : MaterialLcars.NativeButtonStyle();
        using var rounding = new MaterialStyleScope();
        if (material) rounding.Style(ImGuiStyleVar.FrameRounding, radius);
        size.X = MaterialLayout.FitNextItemWidth(size.X, IconButtonWidth(text));
        var textSize = MaterialText.Measure(text);
        size.Y = Math.Max(size.Y, Math.Max(MaterialControls.Metrics.Height,
            Math.Max(textSize.Y, 22 * scale) + 2 * paddingY));
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool clicked;
        try
        {
            using var face = material ? MaterialSkin.TransparentButtonFace() : new MaterialStyleScope();
            clicked = ImGui.Button(raw, size);
        }
        finally { ImGui.PopStyleColor(); }
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var list = ImGui.GetWindowDrawList();
        if (material) MaterialSkin.PaintButton(min, max, alpha, radius);
        else list.AddRect(min, max, MaterialCanvas.Color(MaterialTheme.Current.Colors.OutlineVariant), 4 * scale);
        var groupWidth = textSize.X + 32 * scale;
        var left = min.X + Math.Max(ImGui.GetStyle().FramePadding.X, (max.X - min.X - groupWidth) * .5f);
        list.PushClipRect(min, max, true);
        try
        {
            MaterialIcons.Draw(icon, new(left, min.Y + (max.Y - min.Y - 22 * scale) * .5f), 22 * scale, foreground);
            MaterialText.AddText(list, ImGui.GetFont(), ImGui.GetFontSize(), new(left + 32 * scale, min.Y + (max.Y - min.Y - textSize.Y) * .5f), MaterialCanvas.Color(foreground), text);
        }
        finally { list.PopClipRect(); }
        MaterialLcars.Feedback(clicked);
        return clicked;
    }
    internal static Vector2 Panel(Action draw, float width = 0, float minimumHeight = 0)
    {
        var scale = MaterialTheme.Metrics.Scale; var padding = (MaterialTheme.Current.IsLcars ? 18 : MogtomePresentation.Compact ? 12 : 16) * scale;
        if (width <= 0) width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos(); var list = ImGui.GetWindowDrawList();
        var previousRight = panelRight;
        panelRight = origin.X + width - padding;
        list.ChannelsSplit(2); list.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(origin + new Vector2(padding));
        ImGui.BeginGroup();
        ImGui.PushItemWidth(Math.Max(1, width - padding * 2));
        var height = minimumHeight;
        try
        {
            try { draw(); }
            finally
            {
                ImGui.PopItemWidth();
                ImGui.EndGroup();
            }
            height = Math.Max(minimumHeight, ImGui.GetItemRectSize().Y + padding * 2);
            list.ChannelsSetCurrent(0);
            var colors = MaterialTheme.Current.Colors;
            if (MaterialTheme.Current.IsLcars || MogtomePresentation.HasMaterial) MaterialSkin.PanelFrame(origin, origin + new Vector2(width, height), 1);
            else
            {
                MaterialCanvas.Surface(origin, origin + new Vector2(width, height),
                    MaterialSkin.BackgroundFill(colors.SurfaceContainer), MaterialSkin.BackgroundFill(colors.Surface), 4 * scale);
                list.AddRect(origin, origin + new Vector2(width, height), MaterialCanvas.Color(colors.OutlineVariant), 4 * scale);
            }
        }
        finally
        {
            list.ChannelsMerge();
            panelRight = previousRight;
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width, height));
        return new Vector2(width, height);
    }
    internal static void Wrapped(string text)
    {
        if (ImGuiP.GetCurrentWindow().DC.TextWrapPos >= 0) { MaterialText.Text(text); return; }
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(80 * MaterialTheme.Metrics.Scale, AvailableWidth));
        MaterialText.Text(text);
        ImGui.PopTextWrapPos();
    }
    internal static void SingleLine(string text)
    { ImGui.PushTextWrapPos(-1); MaterialText.Text(text); ImGui.PopTextWrapPos(); }

    internal static void SetTooltip(string text)
    {
        using (MaterialSkin.BackdropStyle(popup: true)) ImGui.BeginTooltip();
        try
        {
            MaterialSkin.Backdrop();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40);
            try { MaterialText.Text(text); }
            finally { ImGui.PopTextWrapPos(); }
        }
        finally { ImGui.EndTooltip(); }
    }

    internal static void TextDisabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        Wrapped(text);
        ImGui.PopStyleColor();
    }

    internal static bool Checkbox(string label, ref bool value)
    {
        var text = label.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(text);
        var shaped = MaterialText.RequiresShaping(text);
        var material = MogtomePresentation.HasMaterial;
        if (!material && !shaped)
        {
            var pressed = ImGui.Checkbox(label, ref value);
            MaterialLcars.Feedback(pressed, MaterialUiSoundCue.Toggle);
            return pressed;
        }
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        var radius = ImGui.GetStyle().FrameRounding;
        var alpha = ImGui.GetStyle().Alpha;
        var list = ImGui.GetWindowDrawList();
        var splitter = material ? ImGui.ImDrawListSplitter() : default;
        bool changed;
        try
        {
            if (material) { splitter.Split(list, 2); splitter.SetCurrentChannel(list, 1); }
            using (var face = new MaterialStyleScope())
            {
                face.Color(ImGuiCol.Text, Vector4.Zero);
                if (shaped) face.Style(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X + Math.Max(0, MaterialText.Measure(text).X-ImGui.CalcTextSize(text).X), gap.Y));
                if (material)
                {
                    face.Color(ImGuiCol.FrameBg, Vector4.Zero);
                    face.Color(ImGuiCol.FrameBgHovered, Vector4.Zero);
                    face.Color(ImGuiCol.FrameBgActive, Vector4.Zero);
                    face.Color(ImGuiCol.Border, Vector4.Zero);
                }
                changed = ImGui.Checkbox(label, ref value);
            }
            if (material)
            {
                splitter.SetCurrentChannel(list, 0);
                using var skin = MaterialSkin.NativeButtonStyle();
                using var face = new MaterialStyleScope();
                face.Color(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
                face.Color(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]);
                face.Color(ImGuiCol.ButtonActive, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgActive]);
                var min = ImGui.GetItemRectMin();
                MaterialSkin.PaintButton(min, min + new Vector2(ImGui.GetFrameHeight()), alpha, radius);
            }
        }
        finally { if (material) { splitter.Merge(list); splitter.Destroy(); } }
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.GetFrameHeight()+gap.X, (ImGui.GetItemRectSize().Y-MaterialText.Measure(text).Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), text);
        MaterialLcars.Feedback(changed, MaterialUiSoundCue.Toggle);
        return changed;
    }
    internal static bool RadioButton(string label, bool active)
    {
        var text = label.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(text);
        var shaped = MaterialText.RequiresShaping(text);
        var material = MogtomePresentation.HasMaterial;
        if (!material && !shaped)
        {
            var pressed = ImGui.RadioButton(label, active);
            MaterialLcars.Feedback(pressed, MaterialUiSoundCue.Toggle);
            return pressed;
        }
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        var alpha = ImGui.GetStyle().Alpha;
        var list = ImGui.GetWindowDrawList();
        var splitter = material ? ImGui.ImDrawListSplitter() : default;
        bool changed;
        try
        {
            if (material) { splitter.Split(list, 2); splitter.SetCurrentChannel(list, 1); }
            using (var face = new MaterialStyleScope())
            {
                face.Color(ImGuiCol.Text, Vector4.Zero);
                if (shaped) face.Style(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X+Math.Max(0,MaterialText.Measure(text).X-ImGui.CalcTextSize(text).X),gap.Y));
                if (material)
                {
                    face.Color(ImGuiCol.FrameBg, Vector4.Zero);
                    face.Color(ImGuiCol.FrameBgHovered, Vector4.Zero);
                    face.Color(ImGuiCol.FrameBgActive, Vector4.Zero);
                    face.Color(ImGuiCol.Border, Vector4.Zero);
                }
                changed = ImGui.RadioButton(label, active);
            }
            if (material)
            {
                splitter.SetCurrentChannel(list, 0);
                using var skin = MaterialSkin.NativeButtonStyle();
                using var face = new MaterialStyleScope();
                face.Color(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
                face.Color(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]);
                face.Color(ImGuiCol.ButtonActive, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgActive]);
                var min = ImGui.GetItemRectMin(); var side = ImGui.GetFrameHeight();
                MaterialSkin.PaintButton(min, min + new Vector2(side), alpha, side * .5f);
            }
        }
        finally { if (material) { splitter.Merge(list); splitter.Destroy(); } }
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin()+new Vector2(ImGui.GetFrameHeight()+gap.X,(ImGui.GetItemRectSize().Y-MaterialText.Measure(text).Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), text);
        MaterialLcars.Feedback(changed, MaterialUiSoundCue.Toggle);
        return changed;
    }
    internal static bool RadioButton(string label, ref int selected, int value)
    {
        if (!RadioButton(label, selected == value)) return false;
        selected = value;
        return true;
    }
    internal static bool InputText(string label, ref string value, int maximum, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var height = MaterialText.PushLineHeight(value, label.Split("##",2)[0]);
        using var caption = new FieldCaption(label);
        using var field = MaterialSkin.NativeFieldStyle();
        return MaterialShapedInput.SingleLine(label, "", ref value, maximum, flags);
    }
    internal static bool SliderInt(string label, ref int value, int minimum, int maximum, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new FieldCaption(label);
        using var field = MaterialSkin.NativeFieldStyle();
        return ImGui.SliderInt(label, ref value, minimum, maximum, format, flags);
    }
    internal static bool InputIntLabel(string label, ref int value)
    {
        using var caption = new FieldCaption(label);
        using var field = MaterialSkin.NativeFieldStyle();
        return ImGui.InputInt(label, ref value);
    }
    private readonly ref struct FieldCaption
    {
        private readonly MaterialStyleScope height;
        private readonly string text;
        private readonly Vector2 min;
        private readonly float width;
        private readonly ImDrawListPtr list;
        private readonly Vector2 previousMax;
        private readonly bool shaped;
        internal FieldCaption(string label)
        {
            text = label.Split("##",2)[0]; height = MaterialText.PushLineHeight(text);
            min = ImGui.GetCursorScreenPos(); width = ImGui.CalcItemWidth(); list = ImGui.GetWindowDrawList();
            previousMax = ImGuiP.GetCurrentWindow().DC.CursorMaxPos; shaped = MaterialText.RequiresShaping(text);
            if (shaped) list.PushClipRect(new Vector2(min.X,ImGui.GetWindowPos().Y),new Vector2(min.X+width,ImGui.GetWindowPos().Y+ImGui.GetWindowSize().Y),true);
        }
        public void Dispose()
        {
            if (shaped) list.PopClipRect();
            try
            {
                if (!shaped) return;
                var p = min+new Vector2(width+ImGui.GetStyle().ItemInnerSpacing.X,(ImGui.GetFrameHeight()-MaterialText.Measure(text).Y)*.5f);
                MaterialText.AddText(list,p,ImGui.GetColorU32(ImGuiCol.Text),text);
                var window = ImGuiP.GetCurrentWindow();
                window.DC.CursorMaxPos = new Vector2(Math.Max(previousMax.X,p.X+MaterialText.Measure(text).X),window.DC.CursorMaxPos.Y);
                window.DC.CursorPosPrevLine = new Vector2(p.X+MaterialText.Measure(text).X,window.DC.CursorPosPrevLine.Y);
            }
            finally { height.Dispose(); }
        }
    }
    internal static bool BeginCombo(string label, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        if (!MogtomePresentation.HasMaterial) return MaterialText.BeginCombo(label, preview, flags);
        var text = preview.Split("##", 2)[0]; var caption = label.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(text, caption);
        var origin = ImGui.GetCursorScreenPos(); var list = ImGui.GetWindowDrawList();
        var window = ImGuiP.GetCurrentWindow(); var previousMax = window.DC.CursorMaxPos;
        var frameHeight = ImGui.GetFrameHeight(); var padding = ImGui.GetStyle().FramePadding;
        var width = (flags & ImGuiComboFlags.NoPreview) != 0 ? frameHeight : ImGui.CalcItemWidth();
        var font = ImGui.GetFont(); var fontSize = ImGui.GetFontSize(); var color = ImGui.GetColorU32(ImGuiCol.Text);
        var open = false;
        if (caption.Length > 0) list.PushClipRect(new(origin.X, ImGui.GetWindowPos().Y), new(origin.X + width, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y), true);
        try
        {
            try
            {
                using var field = (flags & ImGuiComboFlags.NoPreview) == 0 ? MaterialSkin.NativeFieldStyle() : new MaterialStyleScope();
                using var backdrop = MaterialSkin.BackdropStyle(popup: true);
                open = ImGui.BeginCombo(label, "", flags);
            }
            finally { if (caption.Length > 0) list.PopClipRect(); }
            if (open) MaterialSkin.Backdrop();
            if ((flags & ImGuiComboFlags.NoPreview) == 0)
            {
                var right = (flags & ImGuiComboFlags.NoArrowButton) == 0 ? frameHeight : padding.X;
                list.PushClipRect(origin, origin + new Vector2(Math.Max(padding.X, width - right), frameHeight), true);
                try { MaterialText.AddText(list, font, fontSize, origin + new Vector2(padding.X, (frameHeight - MaterialText.Measure(text).Y) * .5f), color, text); }
                finally { list.PopClipRect(); }
            }
            if (caption.Length > 0)
            {
                var position = origin + new Vector2(width + ImGui.GetStyle().ItemInnerSpacing.X, (frameHeight - MaterialText.Measure(caption).Y) * .5f);
                MaterialText.AddText(list, font, fontSize, position, color, caption);
                var right = position.X + MaterialText.Measure(caption).X;
                window.DC.CursorMaxPos = new(Math.Max(previousMax.X, right), window.DC.CursorMaxPos.Y);
                window.DC.CursorPosPrevLine = new(right, window.DC.CursorPosPrevLine.Y);
            }
            return open;
        }
        catch { if (open) ImGui.EndCombo(); throw; }
    }
    internal static bool Combo(string label, ref int selected, string[] options, int count,
        int disabledIndex = -1, string? disabledDisplay = null)
    {
        var captions = options.Take(count).Select((option, index) => index == disabledIndex ? disabledDisplay ?? option : option).ToArray();
        using var height = MaterialText.PushLineHeight(captions);
        if (!BeginCombo(label, selected>=0 && selected<count ? captions[selected] : "")) return false;
        var changed = false;
        try
        {
            for (var index=0; index<count; index++)
            {
                // Native Combo hashes an int index; the managed PushID overload
                // hashes an IntPtr, which changes every retained option identity.
                ImGuiNative.PushID(index);
                try
                {
                    if (MaterialText.Selectable(options[index], selected == index,
                        flags: index == disabledIndex ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None,
                        display: captions[index])) { selected=index; changed=true; }
                    if (selected==index) ImGui.SetItemDefaultFocus();
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.EndCombo(); }
        return changed;
    }
    internal static bool BeginTabBar(string id, string[] captions, ImGuiTabBarFlags flags)
    {
        using var height = MaterialText.PushLineHeight(captions);
        return ImGui.BeginTabBar(id, flags);
    }
    internal static bool BeginTabItem(string label, ImGuiTabItemFlags flags)
        // The retained caller owns EndTabItem after this native Begin wrapper.
        => MaterialTabs.Item(label, flags).Visible;
    internal static void TableHeadersRow()
    {
        var captions = Enumerable.Range(0,ImGui.TableGetColumnCount()).Select(index=>ImGui.TableGetColumnName(index)).ToArray();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, captions.Any(MaterialText.RequiresShaping) ? captions.Select(text=>MaterialText.Measure(text).Y).Max() : 0);
        for (var index=0; index<captions.Length; index++)
        {
            if (!ImGui.TableSetColumnIndex(index)) continue;
            ImGui.PushID(index);
            try
            {
                if (!MaterialText.RequiresShaping(captions[index])) { ImGui.TableHeader(captions[index]); continue; }
                var origin=ImGui.GetCursorScreenPos(); ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
                ImGui.TableHeader(captions[index]); ImGui.PopStyleColor();
                MaterialText.AddText(ImGui.GetWindowDrawList(),origin,ImGui.GetColorU32(ImGuiCol.Text),captions[index]);
            }
            finally { ImGui.PopID(); }
        }
    }
    internal static float TitleMinimumWidth(Window owner)
    {
        var style = ImGui.GetStyle();
        var fontSize = ImGui.GetFontSize();
        var collapse = (owner.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0;
        var left = style.FramePadding.X + (collapse && style.WindowMenuButtonPosition == ImGuiDir.Left ? fontSize + style.ItemInnerSpacing.X : 0);
        var right = fontSize + style.FramePadding.X * 2 + TitleButtonCount(owner) * (fontSize + style.ItemInnerSpacing.X)
            + (collapse && style.WindowMenuButtonPosition == ImGuiDir.Right ? fontSize + style.ItemInnerSpacing.X : 0);
        return left + fontSize + style.ItemInnerSpacing.X
            + MaterialText.Measure(owner.WindowName.Split("##", 2)[0]).X + right + style.ItemInnerSpacing.X;
    }
    private static int TitleButtonCount(Window owner)
        => owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough)
            + (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur ? 1 : 0);
    internal static unsafe string FitWindowTitle(ImGuiWindowPtr window, string title, string retainedTitle,
        MaterialTitlebarStatus? status, MaterialTextRenderer renderer, float extraRightWidth, bool showCloseButton)
    {
        var style = ImGui.GetStyle();
        var height = window.InnerRect.Min.Y - window.Pos.Y;
        if ((window.Flags & ImGuiWindowFlags.MenuBar) != 0)
            height = (height - window.DC.MenuBarOffset.Y) * .5f;
        var fontSize = height - 2 * style.FramePadding.Y;
        if (fontSize <= 0) return string.Empty;
        var gap = style.ItemInnerSpacing.X;
        var collapse = (window.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0;
        var controls = (showCloseButton ? 1 : 0)
            + (collapse && style.WindowMenuButtonPosition is ImGuiDir.Left or ImGuiDir.Right ? 1 : 0);
        var width = window.Size.X - 2 * (window.WindowBorderSize + style.FramePadding.X)
            - controls * (fontSize + gap) - Math.Max(0, extraRightWidth) - fontSize - gap;
        if (status is { } summary && !string.IsNullOrWhiteSpace(summary.Text))
        {
            var text = string.IsNullOrWhiteSpace(summary.ShortText) ? summary.Text : summary.ShortText;
            width -= 3 * gap + fontSize * .26f + Measure(text.Replace('\r', ' ').Replace('\n', ' ').Trim());
        }
        return Measure(title) <= width ? title : status is null ? retainedTitle : string.Empty;

        float Measure(string text) => MaterialText.RequiresShaping(text)
            ? renderer.GetLayout(text, fontSize).Size.X
            : ImGui.CalcTextSize(text).X * (fontSize / ImGui.GetFontSize());
    }
    internal static void PaintWindowTitle(string name, MaterialTextRenderer renderer)
        => PaintTitle(name, null, renderer);
    internal static void PaintWindowTitleWithButtons(Window owner, MaterialTextRenderer renderer)
        => PaintTitle(owner.WindowName, owner, renderer);
    private static unsafe void PaintTitle(string name, Window? owner, MaterialTextRenderer renderer)
    {
        var text=name.Split("##",2)[0];
        if (!MaterialText.RequiresShaping(text)) return;
        var window=ImGuiP.FindWindowByName(name);
        if (window.Handle==null || (window.Flags&ImGuiWindowFlags.NoTitleBar)!=0) return;
        var list=window.DrawList; var min=window.Pos;
        var height=ImGuiP.TitleBarHeight(window); var max=min+new Vector2(window.Size.X,height); var font=ImGui.GetFont();
        for(var index=0; index+3<list.VtxBuffer.Size; index++)
        {
            var a=list.VtxBuffer[index]; var b=list.VtxBuffer[index+1]; var c=list.VtxBuffer[index+2]; var d=list.VtxBuffer[index+3];
            if(a.Uv.X>=c.Uv.X || a.Uv.Y>=c.Uv.Y || a.Uv.Y!=b.Uv.Y || b.Uv.X!=c.Uv.X || c.Uv.Y!=d.Uv.Y || d.Uv.X!=a.Uv.X
                || a.Pos.Y!=b.Pos.Y || b.Pos.X!=c.Pos.X || c.Pos.Y!=d.Pos.Y || d.Pos.X!=a.Pos.X
                || a.Pos.X<min.X-1 || c.Pos.X>max.X+1 || a.Pos.Y<min.Y-1 || c.Pos.Y>max.Y+1) continue;
            foreach(var character in text)
            {
                var glyph=ImGui.FindGlyphNoFallback(font,character).Handle; if(glyph==null) glyph=font.Handle->FallbackGlyph;
                if(glyph==null || glyph->U0>=glyph->U1 || glyph->V0>=glyph->V1) continue;
                if(a.Uv.X<glyph->U0-.00001f || a.Uv.Y<glyph->V0-.00001f || c.Uv.X>glyph->U1+.00001f || c.Uv.Y>glyph->V1+.00001f) continue;
                for(var offset=0; offset<4; offset++) list.Handle->VtxBuffer.Data[index+offset].Col&=0x00ffffff;
                index+=3; break;
            }
        }
        var style=ImGui.GetStyle(); var fontSize=font.FontSize*font.Scale*ImGui.GetIO().FontGlobalScale*window.FontWindowScale;
        var layout=renderer.GetLayout(text,fontSize); var measured=layout.Size;
        var collapse=(window.Flags&(ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0;
        var left=style.FramePadding.X+(collapse&&style.WindowMenuButtonPosition==ImGuiDir.Left?fontSize+style.ItemInnerSpacing.X:0);
        var right=style.FramePadding.X+(window.HasCloseButton?fontSize+style.ItemInnerSpacing.X:0)+(collapse&&style.WindowMenuButtonPosition==ImGuiDir.Right?fontSize+style.ItemInnerSpacing.X:0);
        if (owner is not null)
            right = fontSize + style.FramePadding.X * 2 + TitleButtonCount(owner) * (fontSize + style.ItemInnerSpacing.X)
                + (collapse && style.WindowMenuButtonPosition == ImGuiDir.Right ? fontSize + style.ItemInnerSpacing.X : 0);
        var p=min+new Vector2(left+Math.Max(0,window.Size.X-left-right-measured.X)*style.WindowTitleAlign.X-Math.Min(0,layout.Rasterize().Offset.X),(height-measured.Y)*.5f);
        list.PushClipRect(Vector2.Max(min+new Vector2(left,0),window.OuterRectClipped.Min),Vector2.Min(max-new Vector2(right,0),window.OuterRectClipped.Max),false);
        try { MaterialText.AddText(list,font,fontSize,p,ImGui.GetColorU32(ImGuiCol.Text),text); }
        finally { list.PopClipRect(); }
    }
}
