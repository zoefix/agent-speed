using System.Text.RegularExpressions;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Reporting;

public partial class ReportStringsTests
{
    /// <summary>The live reporting strings the Windows UI uses (DESIGN 11).</summary>
    public static readonly string[] UsedKeys =
    [
        "settings.report", "report.enabled", "report.enabled.hint",
        "report.service", "report.service.custom", "report.url", "report.url.placeholder", "report.url.invalid", "report.url.https",
        "report.url.loopback", "report.service.checking", "report.service.notSupported", "report.service.invalid",
        "report.service.unreachable", "report.service.insecure", "report.service.unavailable", "report.service.retry",
        "report.service.host", "report.service.terms", "report.service.privacy", "report.service.leaderboard",
        "report.service.retention", "report.service.changed", "report.service.review",
        "report.username", "report.username.public", "report.username.random", "report.username.updated", "report.username.taken",
        "report.username.invalid.length", "report.username.invalid.characters", "report.username.invalid.ascii",
        "report.username.invalid.start", "report.username.invalid.spaces", "report.username.invalid.noSpaces",
        "report.username.invalid.reserved", "report.username.transfer", "report.username.transfer.hint",
        "report.apiKey", "report.apiKey.optional", "report.apiKey.required", "report.apiKey.hint", "report.apiKey.get",
        "report.apiKey.clear",
        "report.sections", "report.sections.hint", "report.section.sources", "report.section.models", "report.section.cost",
        "report.section.traffic", "report.section.notAccepted", "report.section.trafficOff",
        "report.preview", "report.preview.title", "report.preview.hint", "report.preview.assumed", "report.preview.copy",
        "report.preview.next",
        "report.consent.title", "report.consent.body", "report.consent.never", "report.consent.honesty", "report.start",
        "report.status", "report.status.off", "report.status.never", "report.status.connecting", "report.status.registering",
        "report.status.reporting", "report.status.idle", "report.status.quiet", "report.status.paused",
        "report.status.pausedIndefinitely", "report.status.retrying", "report.status.registerFailed", "report.status.review",
        "report.status.usernameRejected", "report.status.rateLimited", "report.status.apiKey", "report.status.usernameTaken",
        "report.status.keyMismatch", "report.status.rejected", "report.status.forbidden", "report.status.upgrade",
        "report.status.gone", "report.status.rank",
        "report.error.unreachable", "report.error.insecure", "report.error.unavailable", "report.error.response", "report.error.key",
        "report.pause", "report.pause.hour", "report.pause.indefinite", "report.resume",
        "report.device", "report.device.detail", "report.device.profile", "report.device.fingerprint",
        "report.forget", "report.forget.confirm", "report.forget.detail", "report.forget.action", "report.forget.done",
        "report.forget.failed", "report.forget.localOnly", "report.forget.localOnly.detail", "report.reset", "report.reset.detail",
        "report.others", "report.menu.on", "report.menu.paused", "report.menu.settings", "report.indicator",
        "report.indicator.attention", "report.traffic.note",
        "report.invite.title", "report.invite.body", "report.invite.username", "report.invite.preview", "report.invite.accept",
        "report.invite.decline", "report.invite.done",
        "privacy.report", "privacy.statement.report", "privacy.traffic.report", "settings.network.hint.report",
        "settings.network.offHint", "time.secondsAgo", "history.cancel",
    ];

    /// <summary>Placeholders each key must carry, in every language (the Windows code formats them with these arguments).</summary>
    public static TheoryData<string, int, int> Formats => new()
    {
        { "report.service.host", 0, 1 },
        { "report.service.retention", 1, 0 },
        { "report.username.invalid.length", 2, 0 },
        { "report.preview.next", 1, 0 },
        { "report.consent.title", 0, 1 },
        { "report.consent.body", 1, 0 },
        { "report.status.reporting", 0, 1 },
        { "report.status.paused", 0, 1 },
        { "report.status.rateLimited", 0, 1 },
        { "report.status.rank", 1, 0 },
        { "report.device.detail", 0, 2 },
        { "report.forget.confirm", 0, 1 },
        { "report.forget.failed", 0, 1 },
        { "report.menu.on", 0, 1 },
        { "report.indicator", 0, 2 },
        { "report.traffic.note", 0, 1 },
        { "report.invite.body", 0, 2 },
        { "report.invite.username", 0, 1 },
        { "privacy.statement.report", 0, 1 },
        { "time.secondsAgo", 1, 0 },
    };

    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    [Fact]
    public void EveryUsedKeyHasAllFourLanguages()
    {
        foreach (var key in UsedKeys)
        {
            Assert.True(Table.Contains(key), $"missing '{key}'");
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(Table.TryGet(key, language, out var text), $"'{key}' lacks {language}");
                Assert.False(string.IsNullOrWhiteSpace(text), $"'{key}' is empty in {language}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void PlaceholdersMatchTheirArguments(string key, int integers, int texts)
    {
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Table.TryGet(key, language, out var text));
            Assert.True((integers, texts) == Placeholders(text), $"'{key}' [{language}] has {Placeholders(text)}, the code passes ({integers}, {texts})");
        }
    }

    [Fact]
    public void PlaceholderCountsMatchAcrossLanguagesForEveryReportKey()
    {
        foreach (var key in Table.Keys.Where(k => k.StartsWith("report.", StringComparison.Ordinal) || k.Contains(".report", StringComparison.Ordinal)))
        {
            Assert.True(Table.TryGet(key, LanguageCodes.En, out var english));
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(Table.TryGet(key, language, out var text));
                Assert.True(Placeholders(english) == Placeholders(text), $"'{key}' [{language}]");
            }
        }
    }

    [Fact]
    public void FormattingWithArgumentsLeavesNoPlaceholder()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = new Localizer(Table, language);
            Assert.DoesNotContain("%", loc.Format("report.invite.body", "Codex Radar", "codexradar.com"), StringComparison.Ordinal);
            Assert.DoesNotContain("%", loc.Format("report.username.invalid.length", 2, 32), StringComparison.Ordinal);
            Assert.DoesNotContain("%", loc.Format("report.device.detail", "zoe", "api.example.com"), StringComparison.Ordinal);
            Assert.DoesNotContain("%", loc.Format("report.consent.body", 5), StringComparison.Ordinal);
            Assert.Contains("codexradar.com", loc.Format("report.invite.body", "Codex Radar", "codexradar.com"), StringComparison.Ordinal);
        }
    }

    /// <summary>Every <c>report.*</c> key quoted in the Windows sources (C# and XAML) exists, so no label shows its key.</summary>
    [Fact]
    public void EveryReportKeyInTheWindowsSourcesExists()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(RepositoryPaths.SharedStringsFile()))!;
        var folders = new[] { "AgentSpeed", "AgentSpeed.Presentation", "AgentSpeed.Core" }.Select(p => Path.Combine(root, p == "AgentSpeed" ? "windows" : "shared", "src", p));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in QuotedKey().Matches(text))
                {
                    found.Add(match.Groups[1].Value);
                }

                foreach (Match match in LocKey().Matches(text))
                {
                    found.Add(match.Groups[1].Value);
                }
            }
        }

        Assert.NotEmpty(found);
        var missing = found.Where(k => !Table.Contains(k)).ToArray();
        Assert.True(missing.Length == 0, "keys without strings: " + string.Join(", ", missing));
        foreach (var key in found)
        {
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(Table.TryGet(key, language, out _), $"'{key}' lacks {language}");
            }
        }
    }

    [Fact]
    public void EveryUsedKeyAppearsInTheSources()
    {
        // Keeps UsedKeys honest: a key the code no longer uses should leave the list.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(RepositoryPaths.SharedStringsFile()))!;
        var sources = string.Concat(new[] { "AgentSpeed", "AgentSpeed.Presentation", "AgentSpeed.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p == "AgentSpeed" ? "windows" : "shared", "src", p), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var unused = UsedKeys.Where(k => !sources.Contains(k, StringComparison.Ordinal)).ToArray();
        Assert.True(unused.Length == 0, "listed but never used: " + string.Join(", ", unused));
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

    [GeneratedRegex("\"((?:report|privacy)\\.[A-Za-z]+(?:\\.[A-Za-z]+)*)\"")]
    private static partial Regex QuotedKey();

    [GeneratedRegex("\\{loc:Loc ((?:report|privacy)\\.[A-Za-z]+(?:\\.[A-Za-z]+)*)\\}")]
    private static partial Regex LocKey();
}
