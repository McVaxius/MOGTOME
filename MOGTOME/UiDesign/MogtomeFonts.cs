using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AethertekUI;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace MOGTOME.UiDesign;

internal sealed class MogtomeFonts : IDisposable
{
    private readonly IFontHandle[] handles;
    private int generation;
    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal MaterialTextRenderer? ShapedText { get; set; }
    internal MogtomeFonts(IFontAtlas atlas, IEnumerable<string> required, string selected)
    {
        var chars = required.Select(MaterialText.NativeGlyphText).SelectMany(text => text).Where(c => !char.IsControl(c))
            .Concat(Enumerable.Range(0x20, 0x024F - 0x20 + 1).Select(i => (char)i))
            .Concat(Enumerable.Range(0x0400, 0x052F - 0x0400 + 1).Select(i => (char)i)).Distinct().Order().ToArray();
        var ranges = new List<ushort>();
        for (var index = 0; index < chars.Length; index++)
        {
            var first = chars[index]; var last = first;
            while (index + 1 < chars.Length && chars[index + 1] == last + 1) last = chars[++index];
            ranges.Add(first); ranges.Add(last);
        }
        ranges.Add(0);
        var glyphs = ranges.ToArray();
        handles = MogtomePresentation.FontSizes.Select((size, index) => atlas.NewDelegateFontHandle(toolkit => toolkit.OnPreBuild(build =>
        {
            build.NewImAtlas.TexDesiredWidth = 4096;
            build.NewImAtlas.TexDesiredHeight = 4096;
            size = MogtomePresentation.AtlasHeight((MogtomeFontRole)index);
            var config = new SafeFontConfig { SizePx = size, GlyphRanges = glyphs };
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            build.Font = build.AddFontFromFile(Path.Combine(fonts, MogtomePresentation.FontFile((MogtomeFontRole)index)), config);
            build.AddFontFromFile(Path.Combine(fonts, "seguisym.ttf"), new SafeFontConfig { SizePx = size, MergeFont = build.Font, GlyphRanges = glyphs });
            // Verified bundled TTC faces: JP=0, KR=1, SC=2, TC=3.
            build.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular, new SafeFontConfig
            {
                SizePx = size, MergeFont = build.Font, GlyphRanges = glyphs,
                FontNo = selected switch { "ko" => 1, "zh-Hans" or "zh-CN" => 2, "zh-Hant" or "zh-TW" => 3, _ => 0 },
            });
            build.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = build.Font });
            build.AddGameSymbol(new SafeFontConfig { SizePx = size, MergeFont = build.Font });
        }))).ToArray();
        foreach (var handle in handles) handle.ImFontChanged += FontChanged;
    }
    private void FontChanged(IFontHandle handle, ILockedImFont font) => System.Threading.Interlocked.Increment(ref generation);
    internal bool Ready => handles.All(handle => handle.Available && handle.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;
    internal unsafe bool TryCheckHindiGlyphs()
    {
        var available = true;
        foreach (var handle in handles)
        {
            using var font = handle.Lock();
            available &= ShapedText is { } renderer && renderer.TryCheckGlyphs(["हिन्दी"], font.ImFont.FontSize, out _);
        }
        return available;
    }
    internal unsafe void CheckGlyphs(IEnumerable<string> strings)
    {
        for (var index = 0; index < handles.Length; index++)
        {
            using var font = handles[index].Lock();
            ShapedText?.CheckGlyphs(strings, font.ImFont.FontSize);
            foreach (var text in strings)
                foreach (var rune in MaterialText.NativeGlyphText(text).EnumerateRunes().Where(r => !Rune.IsControl(r)))
                    if (rune.Value > ushort.MaxValue || ImGui.FindGlyphNoFallback(font.ImFont, (ushort)rune.Value).Handle == null)
                        throw new InvalidOperationException("Required UI glyph missing: U+" + rune.Value.ToString("X4") + " in " + (MogtomeFontRole)index);
        }
    }
    internal IDisposable Push(MogtomeFontRole role) => handles[(int)role].Push();
    public void Dispose() { foreach (var handle in handles) { handle.ImFontChanged -= FontChanged; handle.Dispose(); } }
}
