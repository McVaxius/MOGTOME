using System;
using System.Globalization;
using System.Linq;
using System.Resources;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace MOGTOME.Localization;

public enum UiLanguage { English = 0, French = 1, German = 2, Japanese = 3, Spanish = 4, Italian = 5, Russian = 6, Korean = 7, SimplifiedChinese = 8, Vietnamese = 9, BrazilianPortuguese = 10, Indonesian = 11, Polish = 12, Turkish = 13, Hindi = 14 }

/// <summary>A display message retains its identity across language changes. Raw external details stay raw.</summary>
public sealed record UiText(string? Key, object?[] Arguments, string? Raw = null)
{
    public string English => Render(UiLanguage.English);
    public string Render() => Render(Ui.Language);
    public string RenderGame() => Render(Ui.GameLanguage);
    public string Render(UiLanguage language) => Key == null ? Raw ?? string.Empty : Ui.Format(Key, language, Arguments);
    public override string ToString() => English;
    public static implicit operator UiText(string text) => new(null, [], text);
}

public static class Ui
{
    internal static readonly ResourceManager Resources = new("MOGTOME.Localization.Strings", typeof(Ui).Assembly);
    public static UiLanguage Language { get; private set; } = UiLanguage.English;
    public static CultureInfo Culture => CultureFor(Language);
    internal static readonly (UiLanguage Value, string Code, string Name)[] Languages =
    [
        (UiLanguage.English, "en", "English"), (UiLanguage.German, "de", "Deutsch"),
        (UiLanguage.French, "fr", "Français"), (UiLanguage.Spanish, "es", "Español"),
        (UiLanguage.Italian, "it", "Italiano"), (UiLanguage.Russian, "ru", "Русский"),
        (UiLanguage.Japanese, "ja", "日本語"), (UiLanguage.Korean, "ko", "한국어"),
        (UiLanguage.SimplifiedChinese, "zh-Hans", "简体中文"),
        (UiLanguage.Vietnamese, "vi", "Tiếng Việt"), (UiLanguage.BrazilianPortuguese, "pt-BR", "Português (Brasil)"),
        (UiLanguage.Indonesian, "id", "Bahasa Indonesia"), (UiLanguage.Polish, "pl", "Polski"),
        (UiLanguage.Turkish, "tr", "Türkçe"),
        (UiLanguage.Hindi, "hi", "हिन्दी"),
    ];
    internal static string Code => Languages.First(l => l.Value == Language).Code;
    internal static string[] RequiredText => Values(ResourcesFor(Language))
        .Concat(Values(ResourcesFor(UiLanguage.English)))
        .Concat(Languages.Select(l => l.Name)).Append("♡⚫—…·+?✓☕").Distinct().ToArray();
    private static System.Collections.Generic.IEnumerable<string> Values(ResourceSet resources)
        => resources.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Value!);
    private static ResourceSet ResourcesFor(UiLanguage language) => Resources.GetResourceSet(
        language == UiLanguage.English ? CultureInfo.InvariantCulture : CultureFor(language), true, false)
        ?? throw new MissingManifestResourceException("MOGTOME UI resource set: " + language);
    public static ClientLanguage SheetLanguage => ToClient(Language);
    public static void SetLanguage(UiLanguage language) => Language = Enum.IsDefined(language) ? language : UiLanguage.English;
    public static UiLanguage ResolveLanguage(UiLanguage? saved, ClientLanguage client)
        => saved.HasValue && Enum.IsDefined(saved.Value) ? saved.Value : FromClient(client);
    public static UiLanguage FromClient(ClientLanguage language) => language switch
    {
        ClientLanguage.French => UiLanguage.French,
        ClientLanguage.German => UiLanguage.German,
        ClientLanguage.Japanese => UiLanguage.Japanese,
        _ => UiLanguage.English,
    };
    public static ClientLanguage ToClient(UiLanguage language) => language switch
    {
        UiLanguage.French => ClientLanguage.French,
        UiLanguage.German => ClientLanguage.German,
        UiLanguage.Japanese => ClientLanguage.Japanese,
        _ => ClientLanguage.English,
    };
    public static CultureInfo CultureFor(UiLanguage language) => CultureInfo.GetCultureInfo(language switch
    {
        UiLanguage.French => "fr", UiLanguage.German => "de", UiLanguage.Japanese => "ja",
        UiLanguage.Spanish => "es", UiLanguage.Italian => "it", UiLanguage.Russian => "ru",
        UiLanguage.Korean => "ko", UiLanguage.SimplifiedChinese => "zh-Hans", UiLanguage.Vietnamese => "vi",
        UiLanguage.BrazilianPortuguese => "pt-BR", UiLanguage.Indonesian => "id", UiLanguage.Polish => "pl",
        UiLanguage.Turkish => "tr", UiLanguage.Hindi => "hi", _ => "en",
    });
    public static UiText M(string key, params object?[] arguments) => new(key, arguments);
    public static string T(string key, params object?[] arguments) => Format(key, Language, arguments);
    // Game-owned chat/toast surfaces cannot use the plugin's Hindi shaping renderer.
    internal static UiLanguage GameLanguage => Language == UiLanguage.Hindi ? UiLanguage.English : Language;
    public static string GameT(string key, params object?[] arguments) => Format(key, GameLanguage, arguments);
    public static string L(string key, params object?[] arguments) => T(key, arguments) + "###" + key;
    internal static string Format(string key, UiLanguage language, object?[] arguments)
    {
        var culture = CultureFor(language);
        var format = ResourcesFor(language).GetString(key) ?? throw new MissingManifestResourceException(key);
        return string.Format(culture, format, arguments.Select(arg => arg is UiText text ? text.Render(language) : arg is GameName name ? name.Render(language) : arg).ToArray());
    }
    public static string EnumLabel<TEnum>(TEnum value) where TEnum : struct, Enum => T(typeof(TEnum).Name + "_" + value);
    public static GameName Duty(uint territoryId, string? englishName = null) => new(GameNameKind.Duty, territoryId, englishName);
    public static GameName Item(uint itemId) => new(GameNameKind.Item, itemId);
    public static GameName Job(uint jobId) => new(GameNameKind.Job, jobId);
    public static GameName Npc(uint baseId) => new(GameNameKind.Npc, baseId);
    public static GameName Boss(uint nameId) => new(GameNameKind.Boss, nameId);
}

public enum GameNameKind { Duty, Item, Job, Npc, Boss }
public sealed record GameName(GameNameKind Kind, uint Id, string? EnglishName = null)
{
    public string Render() => Render(Ui.Language);
    public string Render(UiLanguage language)
    {
        if (language == UiLanguage.English && EnglishName != null) return EnglishName;
        var data = Plugin.DataManager;
        if (data == null) return Id.ToString(CultureInfo.InvariantCulture);
        var client = Ui.ToClient(language);
        return Kind switch
        {
            // TerritoryType has no localized data; following its RowRef uses the default
            // sheet language. Read PlaceName explicitly to honor the selected UI language.
            GameNameKind.Duty => data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Id) is { } territory
                ? data.GetExcelSheet<PlaceName>(client).GetRowOrDefault(territory.PlaceName.RowId)?.Name.ToString() ?? Id.ToString(CultureInfo.InvariantCulture)
                : Id.ToString(CultureInfo.InvariantCulture),
            GameNameKind.Item => data.GetExcelSheet<Item>(client).GetRowOrDefault(Id)?.Name.ToString() ?? Id.ToString(CultureInfo.InvariantCulture),
            GameNameKind.Job => data.GetExcelSheet<ClassJob>(client).GetRowOrDefault(Id)?.Abbreviation.ToString() ?? Id.ToString(CultureInfo.InvariantCulture),
            GameNameKind.Npc => data.GetExcelSheet<ENpcResident>(client).GetRowOrDefault(Id)?.Singular.ToString() ?? Id.ToString(CultureInfo.InvariantCulture),
            GameNameKind.Boss => data.GetExcelSheet<BNpcName>(client).GetRowOrDefault(Id)?.Singular.ToString() ?? Id.ToString(CultureInfo.InvariantCulture),
            _ => Id.ToString(CultureInfo.InvariantCulture),
        };
    }
    public override string ToString() => Render(UiLanguage.English);
}
