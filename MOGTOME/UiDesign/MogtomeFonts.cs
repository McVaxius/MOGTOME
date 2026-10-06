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
            size = MogtomePresentation.AtlasHeight((MogtomeFontRole)index);
            var config = new SafeFontConfig { SizePx = size, GlyphRanges = glyphs };
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            build.Font = build.AddFontFromFile(Path.Combine(fonts, MogtomePresentation.FontFile((MogtomeFontRole)index)), config);
            build.AddFontFromFile(Path.Combine(fonts, "seguisym.ttf"), new SafeFontConfig { SizePx = size, MergeFont = build.Font, GlyphRanges = glyphs });
            foreach (var locale in new[] { "ja", "ko", "zh-Hans" }.OrderBy(code => code == selected ? 0 : 1))
                build.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular, new SafeFontConfig
                {
                    SizePx = size, MergeFont = build.Font, GlyphRanges = glyphs,
                    // Verified host TTC faces: Japanese0, Korean1, Simplified Chinese2, Traditional Chinese3.
                    FontNo = locale switch { "ja" => 0, "ko" => 1, _ => 2 },
                });
            build.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = build.Font });
            build.AddGameSymbol(new SafeFontConfig { SizePx = size, MergeFont = build.Font });
        }))).ToArray();
        foreach (var handle in handles) handle.ImFontChanged += FontChanged;
    }
    private void FontChanged(IFontHandle handle, ILockedImFont font) => System.Threading.Interlocked.Increment(ref generation);
    internal bool Ready => handles.All(handle => handle.Available && handle.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;
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
