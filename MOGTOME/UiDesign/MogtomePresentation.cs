using System;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace MOGTOME.UiDesign;

internal enum MogtomeFontRole { Body, Strong, Title, Heading, CompactTitle, CompactHeading, Brand, CompactBrand }

internal static class MogtomePresentation
{
    // Approved 1672x941 regular: Main (16,22)-(1244,918), header109;
    // content (39,148)-(1221,898), navigation74, status112, actions55;
    // paired duty/party cards260, settings122, debug44, gaps12/18.
    // Blunderville (1275,45)-(1650,675), content inset23, warning sections196/201.
    // Compact: Main (64,92)-(1161,869), header93, content1060x660;
    // navigation60, status75, actions44 in two rows, cards216, settings100, debug40.
    // Compact Blunderville (1201,95)-(1600,706), inset23, action46.
    // Measured gold sample at regular (91,375) is #FBCE5F; backgrounds #181D22,
    // panels #161A20/#15191E, controls #252A2F, outlines #343A40, ink #F3F5F7.
    internal const uint ReferenceAccent = 0xFBCE5F;
    internal static readonly float[] FontSizes = [16, 16, 34, 22, 28, 20, 34, 36];
    internal static float AtlasHeight(MogtomeFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal static string FontFile(MogtomeFontRole role) => role switch
    {
        MogtomeFontRole.Body => "segoeui.ttf",
        MogtomeFontRole.Brand => "georgiab.ttf",
        MogtomeFontRole.CompactBrand => "segoeuib.ttf",
        _ => "seguisb.ttf",
    };
    internal static bool Compact => MaterialTheme.Current.Density == MaterialDensity.Compact;
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var selected = Rgb(accent); var reference = Rgb(ReferenceAccent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var baseline = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(reference.X, reference.Y, reference.Z)));
        var hue = seed.Y < .001f ? 0 : seed.Z - baseline.Z;
        var chroma = seed.Y < .001f ? 0 : seed.Y / baseline.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chroma, lch.Z + hue), 1);
        }
        var background = Relative(0x181D22); var foreground = Relative(0xF3F5F7);
        var primary = Relative(ReferenceAccent);
        var palette = new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z));
        var colors = new MaterialColorScheme(palette)
        {
            Background = background, OnBackground = foreground,
            Surface = Relative(0x15191E), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x11171C), SurfaceContainerLow = Relative(0x15191E),
            SurfaceContainer = Relative(0x161A20), SurfaceContainerHigh = Relative(0x252A2F), SurfaceContainerHighest = Relative(0x30363C),
            SurfaceVariant = Relative(0x30363C), OnSurfaceVariant = Relative(0xBAC1C8),
            Outline = Relative(0x646D76), OutlineVariant = Relative(0x343A40),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x4A3E23), OnPrimaryContainer = foreground,
            Secondary = Relative(0x90BAC5), OnSecondary = background, SecondaryContainer = Relative(0x28353A), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xFEDF79), OnTertiary = background, TertiaryContainer = Relative(0x453C29), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0xBA8B27),
        };
        return new(colors, MaterialDensity.Standard) { SurfaceOpacity = 1 };
    }
    internal static MaterialControlMetrics Controls()
    {
        var scale = MaterialTheme.Metrics.Scale; var height = Compact ? 36 : 42;
        return new() { Height = height * scale, Padding = new(12 * scale, Math.Max(0, (height * scale - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * scale, IconSize = 22 * scale, Rounding = 4 * scale, ItemSpacing = new(10 * scale, 6 * scale), CellPadding = new(12 * scale, 6 * scale) };
    }
}
