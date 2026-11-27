using System.Collections;
using System.Globalization;
using System.Resources;

namespace FFXIVMomoTalk;

internal static class Loc
{
    private static readonly ResourceManager Resources = new("FFXIVMomoTalk.Localization.Strings", typeof(Loc).Assembly);

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("en", "English"),
        ("ja", "日本語"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
    ];

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return Culture.Name;

        var trimmed = languageCode.Trim();
        foreach (var (code, _) in Languages)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(code, trimmed))
                return code;
        }

        return trimmed;
    }

    public static bool IsSameLanguage(string? left, string? right) =>
        StringComparer.OrdinalIgnoreCase.Equals(NormalizeLanguageCode(left), NormalizeLanguageCode(right));

    public static string PromptLanguageName(string? languageCode) => NormalizeLanguageCode(languageCode) switch
    {
        "en" => "English",
        "ja" => "Japanese",
        "de" => "German",
        "fr" => "French",
        "zh-Hans" => "Simplified Chinese",
        "zh-Hant" => "Traditional Chinese",
        var code => CultureInfo.GetCultureInfo(code).EnglishName,
    };

    public static void SetLanguage(string clientLanguage, string? selectedLanguage)
    {
        var code = selectedLanguage is { } selected && Languages.Any(language => language.Code == selected)
            ? selected
            : clientLanguage;
        Culture = CultureInfo.GetCultureInfo(code);
    }

    public static string Text(string key) => TextFor(Culture, key);

    public static string TextFor(CultureInfo culture, string key) =>
        Resources.GetString(key, culture) ?? throw new MissingManifestResourceException($"Missing translation for '{key}'.");

    public static string Format(string key, params object[] arguments) =>
        string.Format(Culture, Text(key), arguments);

    // Saved rejection reasons may contain text supplied by the game or recorded before localization.
    public static string Reason(string reason) => Resources.GetString(reason, Culture) ?? reason;

    public static string ChineseGlyphs()
    {
        var text = Languages.Where(language => language.Code.StartsWith("zh-", StringComparison.Ordinal))
            .SelectMany(language => Resources.GetResourceSet(CultureInfo.GetCultureInfo(language.Code), true, false)!
                .Cast<DictionaryEntry>().Select(entry => (string)entry.Value!));
        return new string(text.Concat(Languages.Select(language => language.Name)).SelectMany(value => value)
            .Where(character => character >= '\u3000').Distinct().ToArray());
    }
}
