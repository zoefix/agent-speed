using System.Globalization;

namespace AgentSpeed.Presentation.Localization;

public enum AppLanguage
{
    System,
    ZhHant,
    ZhHans,
    En,
    Ja,
}

public static class LanguageCodes
{
    public const string ZhHant = "zh-Hant";
    public const string ZhHans = "zh-Hans";
    public const string En = "en";
    public const string Ja = "ja";

    public static IReadOnlyList<string> All { get; } = [ZhHant, ZhHans, En, Ja];
}

/// <summary>Language selection and fallback (DESIGN section 0).</summary>
public static class LanguageResolver
{
    /// <summary>zh-HK/zh-MO/zh-TW (and zh-Hant-*) map to zh-Hant, other zh to zh-Hans, ja to ja, the rest to en.</summary>
    public static string Resolve(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return LanguageCodes.En;
        }

        var parts = cultureName.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return LanguageCodes.En;
        }

        var language = parts[0].ToLowerInvariant();
        if (language == "ja")
        {
            return LanguageCodes.Ja;
        }

        if (language != "zh")
        {
            return LanguageCodes.En;
        }

        foreach (var part in parts.Skip(1))
        {
            switch (part.ToLowerInvariant())
            {
                case "hant":
                case "cht":
                case "tw":
                case "hk":
                case "mo":
                    return LanguageCodes.ZhHant;
                case "hans":
                case "chs":
                case "cn":
                case "sg":
                    return LanguageCodes.ZhHans;
            }
        }

        return LanguageCodes.ZhHans;
    }

    public static string Resolve(AppLanguage language, CultureInfo systemCulture) => language switch
    {
        AppLanguage.ZhHant => LanguageCodes.ZhHant,
        AppLanguage.ZhHans => LanguageCodes.ZhHans,
        AppLanguage.En => LanguageCodes.En,
        AppLanguage.Ja => LanguageCodes.Ja,
        _ => ResolveCulture(systemCulture),
    };

    /// <summary>The culture used for number and date formatting and for CJK glyph selection.</summary>
    public static CultureInfo CultureFor(string languageCode) => languageCode switch
    {
        LanguageCodes.ZhHant => CultureInfo.GetCultureInfo("zh-TW"),
        LanguageCodes.ZhHans => CultureInfo.GetCultureInfo("zh-CN"),
        LanguageCodes.Ja => CultureInfo.GetCultureInfo("ja-JP"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

    private static string ResolveCulture(CultureInfo culture) => Resolve(culture.Name);
}
