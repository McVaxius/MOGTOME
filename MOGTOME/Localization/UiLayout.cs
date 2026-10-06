using System;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
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
        using var height = MaterialText.PushLineHeight(visible);
        size.X = MaterialLayout.FitNextItemWidth(size.X, MaterialText.Measure(visible).X + ImGui.GetStyle().FramePadding.X * 2);
        if (!MaterialText.RequiresShaping(visible)) return ImGui.Button(label, size);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var measured = MaterialText.Measure(visible);
        var list = ImGui.GetWindowDrawList(); list.PushClipRect(min, max, true);
        try { MaterialText.AddText(list, min + new Vector2((max.X-min.X-measured.X)*.5f, (max.Y-min.Y-measured.Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), visible); }
        finally { list.PopClipRect(); }
        return clicked;
    }

    internal static bool SmallButton(string label)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0));
        var clicked = Button(label);
        ImGui.PopStyleVar();
        return clicked;
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
            return ImGui.InputInt("###" + key, ref value);
        }
        ImGui.SetNextItemWidth(MathF.Ceiling(Math.Max(minimum, Math.Min(available * .65f, available - captionWidth))));
        using var paintedCaption = new FieldCaption(Ui.L(key));
        return ImGui.InputInt(Ui.L(key), ref value);
    }

    internal static float IconButtonWidth(string label) => MaterialText.Measure(label.Split("##", 2)[0]).X + 58 * MaterialTheme.Metrics.Scale;
    internal static float IconButtonHeight(string label, float width, float minimum)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var text = label.Split("##", 2)[0];
        return Math.Max(minimum, MaterialText.Measure(text).Y + (MogtomePresentation.Compact ? 16 : 22) * scale);
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
        size.X = MaterialLayout.FitNextItemWidth(size.X, IconButtonWidth(text));
        var textSize = MaterialText.Measure(text);
        size.Y = Math.Max(size.Y, textSize.Y + (MogtomePresentation.Compact ? 16 : 22) * scale);
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var clicked = ImGui.Button(raw, size);
        ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var list = ImGui.GetWindowDrawList();
        list.AddRect(min, max, MaterialCanvas.Color(MaterialTheme.Current.Colors.OutlineVariant), 4 * scale);
        var groupWidth = textSize.X + 32 * scale;
        var left = min.X + Math.Max(12 * scale, (max.X - min.X - groupWidth) * .5f);
        foreground.W *= ImGui.GetStyle().Alpha;
        list.PushClipRect(min, max, true);
        MaterialIcons.Draw(icon, new(left, min.Y + (max.Y - min.Y - 22 * scale) * .5f), 22 * scale, foreground);
        MaterialText.AddText(list, ImGui.GetFont(), ImGui.GetFontSize(), new(left + 32 * scale, min.Y + (max.Y - min.Y - textSize.Y) * .5f), MaterialCanvas.Color(foreground), text);
        list.PopClipRect(); return clicked;
    }
    internal static Vector2 Panel(Action draw, float width = 0, float minimumHeight = 0)
    {
        var scale = MaterialTheme.Metrics.Scale; var padding = (MogtomePresentation.Compact ? 12 : 16) * scale;
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
            MaterialCanvas.Surface(origin, origin + new Vector2(width, height), colors.SurfaceContainer, colors.Surface, 4 * scale);
            list.AddRect(origin, origin + new Vector2(width, height), MaterialCanvas.Color(colors.OutlineVariant), 4 * scale);
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
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40);
        MaterialText.Text(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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
        if (!MaterialText.RequiresShaping(text)) return ImGui.Checkbox(label, ref value);
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X + Math.Max(0, MaterialText.Measure(text).X-ImGui.CalcTextSize(text).X), gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var changed = ImGui.Checkbox(label, ref value); ImGui.PopStyleColor(); ImGui.PopStyleVar();
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.GetFrameHeight()+gap.X, (ImGui.GetItemRectSize().Y-MaterialText.Measure(text).Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), text);
        return changed;
    }
    internal static bool RadioButton(string label, bool active)
    {
        var text = label.Split("##", 2)[0];
        using var height = MaterialText.PushLineHeight(text);
        if (!MaterialText.RequiresShaping(text)) return ImGui.RadioButton(label, active);
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X+Math.Max(0,MaterialText.Measure(text).X-ImGui.CalcTextSize(text).X),gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var changed = ImGui.RadioButton(label, active); ImGui.PopStyleColor(); ImGui.PopStyleVar();
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin()+new Vector2(ImGui.GetFrameHeight()+gap.X,(ImGui.GetItemRectSize().Y-MaterialText.Measure(text).Y)*.5f), ImGui.GetColorU32(ImGuiCol.Text), text);
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
        return MaterialShapedInput.SingleLine(label, "", ref value, maximum, flags);
    }
    internal static bool SliderInt(string label, ref int value, int minimum, int maximum, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        using var caption = new FieldCaption(label);
        return ImGui.SliderInt(label, ref value, minimum, maximum, format, flags);
    }
    internal static bool InputIntLabel(string label, ref int value)
    {
        using var caption = new FieldCaption(label);
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
    internal static bool Combo(string label, ref int selected, string[] options, int count)
    {
        using var height = MaterialText.PushLineHeight(options.Take(count).ToArray());
        if (!MaterialText.BeginCombo(label, selected>=0 && selected<count ? options[selected] : "")) return false;
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
                    if (MaterialText.Selectable(options[index],selected==index)) { selected=index; changed=true; }
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
    internal static unsafe void PaintWindowTitle(string name, MaterialTextRenderer renderer)
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
        var p=min+new Vector2(left+Math.Max(0,window.Size.X-left-right-measured.X)*style.WindowTitleAlign.X-Math.Min(0,layout.Rasterize().Offset.X),(height-measured.Y)*.5f);
        list.PushClipRect(Vector2.Max(min+new Vector2(left,0),window.OuterRectClipped.Min),Vector2.Min(max-new Vector2(right,0),window.OuterRectClipped.Max),false);
        try { MaterialText.AddText(list,font,fontSize,p,ImGui.GetColorU32(ImGuiCol.Text),text); }
        finally { list.PopClipRect(); }
    }
}
