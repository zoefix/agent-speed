using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Tests.Quota.Vendors;

/// <summary>The phase 2 response shapes of spec/fixtures/quota-cases.json <c>vendors</c> (QUOTAS 9, 10).</summary>
public sealed class QuotaVendorFixtureTests
{
    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-cases.json"))).RootElement.Clone());

    private static JsonElement Vendors => Cases.Value.GetProperty("vendors");

    private static JsonElement Case(string name) => Vendors.GetProperty(name);

    [Fact]
    public void GrokLogKeepsTheNewestConfig()
    {
        var c = Case("grokLog");
        var lines = c.GetProperty("lines").EnumerateArray().Select(l => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(l.GetString()!)).ToList();
        var found = GrokBilling.FromLog(lines);
        Assert.NotNull(found);
        Assert.Equal(c.GetProperty("expected").GetProperty("observedMs").GetInt64(), found!.Value.ObservedMs);
        AssertWindows(c.GetProperty("expected").GetProperty("windows"), found.Value.Windows);
    }

    [Fact]
    public void GrokApiAndUnknownPeriods()
    {
        var api = Case("grokApi");
        AssertWindows(api.GetProperty("expected"), GrokBilling.FromResponse(api.GetProperty("body")));
        var unknown = Case("grokUnknown");
        AssertWindows(unknown.GetProperty("expected"), GrokBilling.Windows(unknown.GetProperty("config")));
    }

    [Fact]
    public void DevinRowsKeepTheGreatestEnd()
    {
        var c = Case("devin");
        var rows = c.GetProperty("rows").EnumerateArray().Select(r => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(r[1].GetString()!)).ToList();
        AssertWindows(c.GetProperty("expected"), DevinPlan.Windows(rows));
    }

    [Theory]
    [InlineData("antigravity")]
    [InlineData("antigravityStatus")]
    public void AntigravityAnswers(string name)
    {
        var c = Case(name);
        var body = c.GetProperty("body");
        AssertWindows(c.GetProperty("expected"), name == "antigravity" ? AntigravitySummary.SummaryWindows(body) : AntigravitySummary.StatusWindows(body));
    }

    [Theory]
    [InlineData("kimiCurrent")]
    [InlineData("kimiLegacy")]
    public void KimiShapes(string name)
    {
        var c = Case(name);
        AssertWindows(c.GetProperty("expected"), KimiUsage.Windows(c.GetProperty("body")));
    }

    [Fact]
    public void GeminiProjectAndFamilies()
    {
        var project = Case("geminiProject");
        Assert.Equal(project.GetProperty("expected").GetString(), GeminiQuota.Project(project.GetProperty("body")));
        using (var text = JsonDocument.Parse("""{"cloudaicompanionProject":"plain-id"}"""))
        {
            Assert.Equal("plain-id", GeminiQuota.Project(text.RootElement));
        }

        var c = Case("gemini");
        AssertWindows(c.GetProperty("expected"), GeminiQuota.Windows(c.GetProperty("body")));
    }

    [Fact]
    public void DroidKeyAndLimits()
    {
        foreach (var pair in Case("droidEnv").EnumerateArray())
        {
            Assert.Equal(pair[1].ValueKind == JsonValueKind.Null ? null : pair[1].GetString(), DroidLimits.KeyFromEnvFile(pair[0].GetString()!));
        }

        var c = Case("droid");
        AssertWindows(c.GetProperty("expected"), DroidLimits.Windows(c.GetProperty("body"), c.GetProperty("nowMs").GetInt64()));
    }

    [Fact]
    public void ZaiLimitsAndFailure()
    {
        var c = Case("zcode");
        AssertWindows(c.GetProperty("expected"), ZaiLimits.Windows(c.GetProperty("body"), c.GetProperty("nowMs").GetInt64()));
        Assert.Null(ZaiLimits.Windows(Case("zcodeFailure").GetProperty("body"), 0));
    }

    [Theory]
    [InlineData("qoder")]
    [InlineData("qoderPercent")]
    public void QoderCredits(string name)
    {
        var c = Case(name);
        AssertWindows(c.GetProperty("expected"), BrowserSessionCredits.QoderWindows(c.GetProperty("body")));
    }

    [Fact]
    public void WorkBuddyPackages()
    {
        var c = Case("workbuddy");
        AssertWindows(c.GetProperty("expected"), BrowserSessionCredits.WorkBuddyWindows(c.GetProperty("body")));
        using var failure = JsonDocument.Parse("""{"code":1}""");
        Assert.Null(BrowserSessionCredits.WorkBuddyWindows(failure.RootElement));
    }

    [Fact]
    public void EveryVendorCaseIsCovered()
    {
        string[] covered = ["grokLog", "grokApi", "grokUnknown", "devin", "antigravity", "antigravityStatus", "kimiCurrent", "kimiLegacy",
            "geminiProject", "gemini", "droidEnv", "droid", "zcode", "zcodeFailure", "qoder", "qoderPercent", "workbuddy",
            "userAgent", "cookies", "cookieHeader"];
        Assert.Equal(covered.Order(StringComparer.Ordinal), Vendors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("2026-11-10T22:00:00Z", 1794348000000L)]
    [InlineData("2026-11-10T22:00:00.589678+00:00", 1794348000589L)]
    [InlineData("2026-12-01T00:00:00+08:00", 1796054400000L)]
    [InlineData("2026-11-10T22:00:00", 1794348000000L)]
    [InlineData("2026-11-10 22:00", 1794348000000L)]
    [InlineData("2026-11-11", 1794355200000L)]
    [InlineData("1794348000", 1794348000000L)]
    [InlineData("1794348000123", 1794348000123L)]
    public void TimesReadAsTheReference(string text, long expected)
    {
        using var document = JsonDocument.Parse("\"" + text + "\"");
        Assert.Equal(expected, VendorValue.TimeMs(document.RootElement));
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-11-10T25:00:00Z")]
    [InlineData("2026-11-10T22:00:00Zjunk")]
    public void BadTimesAreNull(string text)
    {
        using var document = JsonDocument.Parse("\"" + text + "\"");
        Assert.Null(VendorValue.TimeMs(document.RootElement));
    }

    [Fact]
    public void NumbersAndEpochs()
    {
        using var document = JsonDocument.Parse("""{"a":"150","b":" 2.5 ","c":true,"d":1794348000,"e":1794348000123,"f":"x"}""");
        var root = document.RootElement;
        Assert.Equal(150, VendorValue.Number(VendorValue.Get(root, "a")));
        Assert.Equal(2.5, VendorValue.Number(VendorValue.Get(root, "b")));
        Assert.Null(VendorValue.Number(VendorValue.Get(root, "c")));
        Assert.Null(VendorValue.Number(VendorValue.Get(root, "f")));
        Assert.Equal(1794348000000, VendorValue.TimeMs(VendorValue.Get(root, "d")));
        Assert.Equal(1794348000123, VendorValue.TimeMs(VendorValue.Get(root, "e")));
    }

    internal static void AssertWindows(JsonElement expected, IReadOnlyList<QuotaWindow>? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        var windows = expected.EnumerateArray().Select(QuotaFixtureTests.ReadWindow).ToList();
        Assert.True(windows.Count == actual!.Count, $"{windows.Count} windows expected, {actual.Count} found");
        for (var i = 0; i < windows.Count; i++)
        {
            var (want, got) = (windows[i], actual[i]);
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.Minutes, got.Minutes);
            Assert.Equal(want.ResetsAtMs, got.ResetsAtMs);
            Assert.True(Math.Abs(want.UsedPercent - got.UsedPercent) < 1e-5, $"window {i}: {want.UsedPercent} expected, {got.UsedPercent} found");
        }
    }
}
