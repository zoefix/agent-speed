using System.Globalization;
using System.Text.Json;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class LocalizationTests
{
    private const string SmallTable = """
        {
          "_comment": "ignored",
          "greet": { "zh-Hant": "你好", "en": "Hello", "ja": "こんにちは" },
          "only.en": { "en": "English only" },
          "empty.ja": { "en": "Fallback", "ja": "" },
          "count": { "en": "%d items", "ja": "%d 件" },
          "not.an.object": "skipped",
          "mixed": { "en": "ok", "ja": 5 },
        }
        """;

    public static TheoryData<string> EmbeddedResourceNames => [Localizer.SharedResourceName, Localizer.WindowsResourceName];

    private static StringTable LoadResource(string name)
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        return StringTable.Parse(stream);
    }

    private static (int D, int S) Placeholders(string text)
    {
        var d = 0;
        var s = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '%')
            {
                continue;
            }

            switch (text[i + 1])
            {
                case '%':
                    i++;
                    break;
                case 'd':
                    d++;
                    i++;
                    break;
                case '@':
                    s++;
                    i++;
                    break;
            }
        }

        return (d, s);
    }

    [Theory]
    [InlineData("zh-TW", LanguageCodes.ZhHant)]
    [InlineData("zh-HK", LanguageCodes.ZhHant)]
    [InlineData("zh-MO", LanguageCodes.ZhHant)]
    [InlineData("zh-Hant", LanguageCodes.ZhHant)]
    [InlineData("zh-Hant-TW", LanguageCodes.ZhHant)]
    [InlineData("zh-Hant-CN", LanguageCodes.ZhHant)]
    [InlineData("zh-CHT", LanguageCodes.ZhHant)]
    [InlineData("zh_TW", LanguageCodes.ZhHant)]
    [InlineData("ZH-tw", LanguageCodes.ZhHant)]
    [InlineData("zh-CN", LanguageCodes.ZhHans)]
    [InlineData("zh-SG", LanguageCodes.ZhHans)]
    [InlineData("zh", LanguageCodes.ZhHans)]
    [InlineData("zh-Hans", LanguageCodes.ZhHans)]
    [InlineData("zh-Hans-HK", LanguageCodes.ZhHans)]
    [InlineData("zh-CHS", LanguageCodes.ZhHans)]
    [InlineData("ja-JP", LanguageCodes.Ja)]
    [InlineData("ja", LanguageCodes.Ja)]
    [InlineData("JA", LanguageCodes.Ja)]
    [InlineData("en-US", LanguageCodes.En)]
    [InlineData("en", LanguageCodes.En)]
    [InlineData("de-DE", LanguageCodes.En)]
    [InlineData("fr", LanguageCodes.En)]
    [InlineData("ko-KR", LanguageCodes.En)]
    [InlineData("", LanguageCodes.En)]
    [InlineData("   ", LanguageCodes.En)]
    [InlineData("--", LanguageCodes.En)]
    [InlineData(null, LanguageCodes.En)]
    public void ResolveCultureName(string? culture, string expected)
    {
        Assert.Equal(expected, LanguageResolver.Resolve(culture));
    }

    [Theory]
    [InlineData(AppLanguage.ZhHant, "en-US", LanguageCodes.ZhHant)]
    [InlineData(AppLanguage.ZhHans, "ja-JP", LanguageCodes.ZhHans)]
    [InlineData(AppLanguage.En, "zh-TW", LanguageCodes.En)]
    [InlineData(AppLanguage.Ja, "en-US", LanguageCodes.Ja)]
    [InlineData(AppLanguage.System, "zh-TW", LanguageCodes.ZhHant)]
    [InlineData(AppLanguage.System, "zh-HK", LanguageCodes.ZhHant)]
    [InlineData(AppLanguage.System, "zh-CN", LanguageCodes.ZhHans)]
    [InlineData(AppLanguage.System, "ja-JP", LanguageCodes.Ja)]
    [InlineData(AppLanguage.System, "de-DE", LanguageCodes.En)]
    [InlineData(AppLanguage.System, "", LanguageCodes.En)]
    public void ResolveSettingAgainstSystemCulture(AppLanguage setting, string systemCulture, string expected)
    {
        var culture = systemCulture.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(systemCulture);
        Assert.Equal(expected, LanguageResolver.Resolve(setting, culture));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "zh-TW")]
    [InlineData(LanguageCodes.ZhHans, "zh-CN")]
    [InlineData(LanguageCodes.Ja, "ja-JP")]
    [InlineData(LanguageCodes.En, "en-US")]
    [InlineData("xx", "en-US")]
    public void CultureForLanguage(string code, string culture)
    {
        Assert.Equal(culture, LanguageResolver.CultureFor(code).Name);
    }

    [Fact]
    public void LanguageCodesListsTheFourLanguages()
    {
        Assert.Equal(["zh-Hant", "zh-Hans", "en", "ja"], LanguageCodes.All);
    }

    [Fact]
    public void EmbeddedTableHasEveryLanguageForEveryKey()
    {
        var table = Localizer.LoadEmbeddedTable();
        Assert.NotEmpty(table.Keys);
        Assert.Empty(table.MissingTranslations(LanguageCodes.All));
    }

    [Theory]
    [MemberData(nameof(EmbeddedResourceNames))]
    public void EachEmbeddedTableIsCompleteAndMerged(string resourceName)
    {
        var part = LoadResource(resourceName);
        Assert.Empty(part.MissingTranslations(LanguageCodes.All));

        var merged = Localizer.LoadEmbeddedTable();
        foreach (var key in part.Keys)
        {
            Assert.True(merged.Contains(key), $"Merged table lacks '{key}' from {resourceName}");
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(part.TryGet(key, language, out var text), $"{resourceName}: '{key}' lacks {language}");
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
    }

    [Fact]
    public void WindowsTableDoesNotSilentlyOverrideSharedKeys()
    {
        var shared = LoadResource(Localizer.SharedResourceName);
        var windows = LoadResource(Localizer.WindowsResourceName);
        Assert.Empty(windows.Keys.Intersect(shared.Keys));
    }

    [Theory]
    [MemberData(nameof(EmbeddedResourceNames))]
    public void PlaceholderCountsMatchAcrossLanguages(string resourceName)
    {
        var table = LoadResource(resourceName);
        foreach (var key in table.Keys)
        {
            Assert.True(table.TryGet(key, LanguageCodes.En, out var english));
            var expected = Placeholders(english);
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(table.TryGet(key, language, out var text));
                Assert.True(expected == Placeholders(text),
                    $"{resourceName}: '{key}' [{language}] has %d/%@ = {Placeholders(text)} but en has {expected}");
            }
        }
    }

    [Fact]
    public void EmbeddedSharedTableMatchesSpecFileOnDisk()
    {
        var path = RepositoryPaths.SharedStringsFile();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var embedded = LoadResource(Localizer.SharedResourceName);

        var diskKeys = 0;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Name.StartsWith('_'))
            {
                continue;
            }

            diskKeys++;
            Assert.True(embedded.Contains(entry.Name), $"Embedded table lacks '{entry.Name}'");
            foreach (var translation in entry.Value.EnumerateObject())
            {
                Assert.True(embedded.TryGet(entry.Name, translation.Name, out var text));
                Assert.Equal(translation.Value.GetString(), text);
            }
        }

        Assert.Equal(diskKeys, embedded.Keys.Count);
    }

    [Fact]
    public void GetFallsBackToEnglishThenToTheKey()
    {
        var localizer = new Localizer(StringTable.Parse(SmallTable), LanguageCodes.Ja);
        Assert.Equal("こんにちは", localizer.Get("greet"));
        Assert.Equal("こんにちは", localizer["greet"]);
        Assert.Equal("English only", localizer.Get("only.en"));
        Assert.Equal("Fallback", localizer.Get("empty.ja"));
        Assert.Equal("ok", localizer.Get("mixed"));
        Assert.Equal("unknown.key", localizer.Get("unknown.key"));
        Assert.Equal("not.an.object", localizer.Get("not.an.object"));
        Assert.Throws<ArgumentNullException>(() => localizer.Get(null!));
    }

    [Fact]
    public void ParseSkipsMetadataAndNonObjectEntries()
    {
        var table = StringTable.Parse(SmallTable);
        Assert.False(table.Contains("_comment"));
        Assert.False(table.Contains("not.an.object"));
        Assert.True(table.Contains("mixed"));
        Assert.False(table.TryGet("mixed", LanguageCodes.Ja, out _));
        Assert.Equal(["count", "empty.ja", "greet", "mixed", "only.en"], table.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["count", "empty.ja", "greet", "mixed", "only.en"], table.MissingTranslations(LanguageCodes.All));
        Assert.Empty(table.MissingTranslations([LanguageCodes.En]));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void ParseRejectsNonObjectRoot(string json)
    {
        Assert.Throws<FormatException>(() => StringTable.Parse(json));
    }

    [Fact]
    public void MergeLetsLaterTablesOverride()
    {
        var first = StringTable.Parse("""{ "a": { "en": "one" }, "b": { "en": "two" } }""");
        var second = StringTable.Parse("""{ "b": { "en": "TWO" }, "c": { "en": "three" } }""");
        var localizer = new Localizer(StringTable.Merge(first, second));
        Assert.Equal("one", localizer["a"]);
        Assert.Equal("TWO", localizer["b"]);
        Assert.Equal("three", localizer["c"]);
        Assert.Empty(StringTable.Merge().Keys);
        Assert.Empty(StringTable.Empty.Keys);
    }

    [Theory]
    [InlineData("%d of %@", "3 of x")]
    [InlineData("100%%", "100%")]
    [InlineData("%%d", "%d")]
    [InlineData("50%", "50%")]
    [InlineData("%x", "%x")]
    [InlineData("plain", "plain")]
    public void FormatTemplateHandlesPlaceholders(string template, string expected)
    {
        Assert.Equal(expected, Localizer.FormatTemplate(template, CultureInfo.InvariantCulture, 3, "x"));
    }

    [Fact]
    public void FormatTemplateConsumesArgumentsInOrder()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("a=1 b=two c=3", Localizer.FormatTemplate("a=%d b=%@ c=%d", culture, 1, "two", 3L));
        Assert.Equal("0 and ", Localizer.FormatTemplate("%d and %@", culture));
        Assert.Equal("7 then 0", Localizer.FormatTemplate("%d then %d", culture, 7));
        Assert.Equal("3", Localizer.FormatTemplate("%d", culture, 2.6));
        Assert.Equal("4", Localizer.FormatTemplate("%d", culture, 3.5f));
        Assert.Equal("1234567", Localizer.FormatTemplate("%d", culture, 1234567));
        Assert.Equal("0", Localizer.FormatTemplate("%d", culture, [null]));
        Assert.Equal("", Localizer.FormatTemplate("%@", culture, [null]));
    }

    [Fact]
    public void FormatUsesTheTranslatedTemplate()
    {
        var localizer = new Localizer(StringTable.Parse(SmallTable), LanguageCodes.Ja);
        Assert.Equal("12 件", localizer.Format("count", 12));
        localizer.LanguageCode = LanguageCodes.ZhHant;
        Assert.Equal("12 items", localizer.Format("count", 12));
    }

    [Fact]
    public void LanguageChangedIsRaisedOnlyOnRealChange()
    {
        var localizer = new Localizer(StringTable.Empty, LanguageCodes.En);
        var raised = 0;
        localizer.LanguageChanged += (_, _) => raised++;

        localizer.LanguageCode = LanguageCodes.En;
        localizer.LanguageCode = "en-US";
        Assert.Equal(0, raised);

        localizer.LanguageCode = LanguageCodes.Ja;
        Assert.Equal(1, raised);
        localizer.LanguageCode = "ja-JP";
        Assert.Equal(1, raised);

        localizer.LanguageCode = "zh-TW";
        Assert.Equal(LanguageCodes.ZhHant, localizer.LanguageCode);
        Assert.Equal(2, raised);
        localizer.LanguageCode = "zh-HK";
        Assert.Equal(2, raised);

        localizer.LanguageCode = null!;
        Assert.Equal(LanguageCodes.En, localizer.LanguageCode);
        Assert.Equal(3, raised);
    }

    [Theory]
    [InlineData("zh-TW", LanguageCodes.ZhHant, false)]
    [InlineData("zh-CN", LanguageCodes.ZhHans, false)]
    [InlineData("ja", LanguageCodes.Ja, false)]
    [InlineData("en", LanguageCodes.En, true)]
    [InlineData("fr-FR", LanguageCodes.En, true)]
    public void ConstructorNormalizesLanguage(string input, string expected, bool latin)
    {
        var localizer = new Localizer(StringTable.Empty, input);
        Assert.Equal(expected, localizer.LanguageCode);
        Assert.Equal(latin, localizer.UsesLatinScript);
        Assert.Equal(LanguageResolver.CultureFor(expected), localizer.Culture);
    }

    [Fact]
    public void ConstructorRejectsNullTable()
    {
        Assert.Throws<ArgumentNullException>(() => new Localizer(null!));
    }
}
