using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace MOGTOME.UiDesign;

internal sealed class MogtomeFonts : IDisposable
{
    private readonly IFontAtlas atlas;
    private readonly Dictionary<MogtomeFontRole, IFontHandle> roles = [];
    private readonly List<IFontHandle> handles = [];
    private int generation;
    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal MaterialTextRenderer? ShapedText { get; set; }
    internal MogtomeFonts(IFontAtlas atlas, IEnumerable<string> required, string selected, string assetRoot,
        IEnumerable<MogtomeFontRole> selectedRoles)
    {
        this.atlas = atlas;
        atlas.BuildStepChange += MaterialFontAtlas.ReleaseRasterizedInputs;
        try
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
            var glyphCount = 0;
            for (var index = 0; index + 1 < glyphs.Length; index += 2)
                glyphCount += glyphs[index + 1] - glyphs[index] + 1;
            var pageSize = glyphCount > 1000 ? 2048 : 1024;
            var faces = new Dictionary<(MaterialFontWeight Weight, float Height), IFontHandle>();
            foreach (var role in selectedRoles)
            {
                var weight = MogtomePresentation.FontWeight(role);
                var height = MogtomePresentation.AtlasHeight(role);
                if (!faces.TryGetValue((weight, height), out var handle))
                {
                    handle = MaterialFontAtlas.CreateFont(atlas, weight, height, glyphs, selected, assetRoot, pageSize, includeHostLanguage: true);
                    faces.Add((weight, height), handle);
                    handles.Add(handle);
                    handle.ImFontChanged += FontChanged;
                }
                roles.Add(role, handle);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
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
        foreach (var (role, handle) in roles)
        {
            using var font = handle.Lock();
            ShapedText?.CheckGlyphs(strings, font.ImFont.FontSize);
            foreach (var text in strings)
                foreach (var rune in MaterialText.NativeGlyphText(text).EnumerateRunes().Where(r => !Rune.IsControl(r)))
                    if (rune.Value > ushort.MaxValue || ImGui.FindGlyphNoFallback(font.ImFont, (ushort)rune.Value).Handle == null)
                        throw new InvalidOperationException("Required UI glyph missing: U+" + rune.Value.ToString("X4") + " in " + role);
        }
    }
    internal IDisposable Push(MogtomeFontRole role) => roles.GetValueOrDefault(role, roles[MogtomeFontRole.Body]).Push();
    public void Dispose()
    {
        atlas.BuildStepChange -= MaterialFontAtlas.ReleaseRasterizedInputs;
        foreach (var handle in handles)
        {
            handle.ImFontChanged -= FontChanged;
            handle.Dispose();
        }
        atlas.Dispose();
    }
}
