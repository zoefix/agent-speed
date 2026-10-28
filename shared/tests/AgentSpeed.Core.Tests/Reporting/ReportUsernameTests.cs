using System.Text.Json;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>REPORTING 4.3 and DESIGN 11 usernames against report-vectors.json <c>usernames</c>.</summary>
public sealed class ReportUsernameTests
{
    private static JsonElement Usernames => ReportSpec.Vector("usernames");

    private static ReportUsernameRules Rules(string name)
    {
        var rule = Usernames.GetProperty("rules").GetProperty(name);
        return new ReportUsernameRules(
            rule.GetProperty("minLength").GetInt32(),
            rule.GetProperty("maxLength").GetInt32(),
            rule.Text("charset") == "ascii" ? ReportUsernameCharset.Ascii : ReportUsernameCharset.Unicode,
            rule.GetProperty("spaces").GetBoolean());
    }

    private static string? Reason(ReportUsernameProblem? problem) => problem switch
    {
        ReportUsernameProblem.Characters => "characters",
        ReportUsernameProblem.Length => "length",
        ReportUsernameProblem.Spaces => "spaces",
        ReportUsernameProblem.Start => "start",
        _ => null,
    };

    private static string Points(string? text) =>
        text is null ? "null" : string.Join(' ', text.EnumerateRunes().Select(r => r.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void RulesVectorsMatchTheClampedDefaults()
    {
        Assert.Equal(ReportUsernameRules.Standard, Rules("default"));
        Assert.Equal(new ReportUsernameRules(3, 16, ReportUsernameCharset.Ascii, spaces: false), Rules("strict"));
    }

    [Fact]
    public void ValidateVectors()
    {
        var cases = Usernames.GetProperty("validate").EnumerateArray().ToList();
        Assert.True(cases.Count >= 20);
        foreach (var item in cases)
        {
            var name = item.Text("name");
            var expected = item.OptionalText("expect");
            var got = Reason(ReportUsername.Validate(name, Rules(item.Text("rules"))));
            Assert.True(expected == got, $"{Points(name)}: expected {expected ?? "valid"}, got {got ?? "valid"}");
        }
    }

    [Fact]
    public void FitVectors()
    {
        var cases = Usernames.GetProperty("fit").EnumerateArray().ToList();
        Assert.True(cases.Count >= 13);
        foreach (var item in cases)
        {
            var computerName = item.Text("computerName");
            var expected = item.OptionalText("expect");
            var fitted = ReportUsername.Fit(computerName, Rules(item.Text("rules")));
            Assert.True(Points(expected) == Points(fitted), $"{computerName} → {fitted ?? "null"}, expected {expected ?? "null"}");
        }
    }

    [Fact]
    public void KeyVectors()
    {
        var cases = Usernames.GetProperty("keys").EnumerateArray().ToList();
        Assert.True(cases.Count >= 4);
        foreach (var item in cases)
        {
            var a = item.Text("a");
            var b = item.Text("b");
            var same = item.GetProperty("same").GetBoolean();
            Assert.True(same == ReportUsername.IsSameName(a, b), $"{a} / {b}");
            Assert.Equal(same, ReportUsername.ComparisonKey(a) == ReportUsername.ComparisonKey(b));
        }
    }

    [Fact]
    public void ReferenceAgreesOnTheVectorsAndMore()
    {
        if (!ReferencePython.Available)
        {
            return;
        }

        string[] names = ["zoe-mbp", "Zoë Studio", "x", "-zoe", "zoe ", "zoe  mbp", "zoe\tmbp", "zoe🚀", "ｚｏｅ", "zoe\u200Bmbp",
            "مرحبا", "Zoe MBP", "小明的家", "racer-7k3m9q", "zoe\u3000x", "zoe\u2028x", ".zoe", "\u0301zoe", "Zoe\u0308", "a\u0301\u0302\u0303\u0304\u0305"];
        var script = """
            import json, sys
            sys.path.insert(0, sys.argv[1])
            import report_reference as r
            names = json.loads(sys.argv[2])
            out = []
            for rules in (r.username_rules(None), r.username_rules({"minLength": 3, "maxLength": 16, "charset": "ascii", "spaces": False})):
                out.append([r.validate_username(n, rules) for n in names])
                out.append([r.fit_username(n, rules) for n in names])
            out.append([r.username_key(n) for n in names])
            print(json.dumps(out))
            """;
        var output = JsonDocument.Parse(ReferencePython.Run(script, ReportSpec.Tools, JsonSerializer.Serialize(names))).RootElement;
        var rules = new[] { ReportUsernameRules.Standard, new ReportUsernameRules(3, 16, ReportUsernameCharset.Ascii, false) };
        for (var r = 0; r < rules.Length; r++)
        {
            var validated = output[r * 2].EnumerateArray().ToList();
            var fitted = output[r * 2 + 1].EnumerateArray().ToList();
            for (var i = 0; i < names.Length; i++)
            {
                Assert.True(validated[i].GetString() == Reason(ReportUsername.Validate(names[i], rules[r])), $"validate {Points(names[i])} rules {r}");
                Assert.True(Points(fitted[i].GetString()) == Points(ReportUsername.Fit(names[i], rules[r])), $"fit {Points(names[i])} rules {r}");
            }
        }

        var keys = output[4].EnumerateArray().ToList();
        for (var i = 0; i < names.Length; i++)
        {
            Assert.True(Points(keys[i].GetString()) == Points(ReportUsername.ComparisonKey(names[i])), $"key {Points(names[i])}");
        }
    }

    [Fact]
    public void NormalFormIsCheckedByCodePoints()
    {
        Assert.Null(ReportUsername.Validate("Zoë", ReportUsernameRules.Standard));
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("Zoe\u0308", ReportUsernameRules.Standard));
        Assert.Equal("Zoë", ReportUsername.Normalized("Zoe\u0308"));
        Assert.Equal("zoe", ReportUsername.Normalized("ｚｏｅ"));
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("zo\uD800e", ReportUsernameRules.Standard));
    }

    [Fact]
    public void ChecksFollowTheOrderOfTheSpec()
    {
        var standard = ReportUsernameRules.Standard;
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("x\u200B", standard));
        Assert.Equal(ReportUsernameProblem.Length, ReportUsername.Validate("x", standard));
        Assert.Equal(ReportUsernameProblem.Start, ReportUsername.Validate(" zoe", standard));
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("zoe\u3000x", standard));
        Assert.Equal(ReportUsernameProblem.Spaces, ReportUsername.Validate("zoe\u2028x", standard));
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("zoe@ x", standard));
        Assert.Equal(ReportUsernameProblem.Start, ReportUsername.Validate(".zoe", standard));
        Assert.Equal(ReportUsernameProblem.Start, ReportUsername.Validate("\u0301zoe", standard));
        Assert.Equal(ReportUsernameProblem.Characters, ReportUsername.Validate("Zoë", new ReportUsernameRules(charset: ReportUsernameCharset.Ascii)));
    }

    [Fact]
    public void CaseFoldingFollowsUnicodeCaseFolding()
    {
        Assert.Equal("strasse", ReportUsername.ComparisonKey("Straße"));
        Assert.Equal("ss", ReportUsername.ComparisonKey("ẞ"));
        Assert.Equal("0069 0307", Points(ReportUsername.ComparisonKey("İ")));
        Assert.Equal("13A0 13A0 13F0", Points(ReportUsername.ComparisonKey("ꭰᎠᏸ")));
        Assert.Equal("0432 A64B", Points(ReportUsername.ComparisonKey("ᲀᲈ")));
        Assert.Equal(Points("σασ σ"), Points(ReportUsername.ComparisonKey("ΣΑΣ ς")));
        Assert.Equal("fix", ReportUsername.ComparisonKey("ﬁx"));
        Assert.True(ReportUsername.IsSameName("ＺＯＥ", "zoe"));
        Assert.False(ReportUsername.IsSameName("zoe", "zoë"));
    }

    [Fact]
    public void RulesAreClamped()
    {
        var low = new ReportUsernameRules(0, 1);
        Assert.Equal((1, 12), (low.MinLength, low.MaxLength));
        var high = new ReportUsernameRules(40, 99);
        Assert.Equal((12, 32), (high.MinLength, high.MaxLength));
    }

    [Fact]
    public void FitDropsWhatTheRulesForbid()
    {
        var strict = new ReportUsernameRules(3, 16, ReportUsernameCharset.Ascii, spaces: false);
        Assert.Equal("Zoës-Mac", ReportUsername.Fit("Zoë's  --  Mac", ReportUsernameRules.Standard));
        Assert.Equal("Zoe", ReportUsername.Fit("__Zoe__", ReportUsernameRules.Standard));
        Assert.Null(ReportUsername.Fit("Ab", strict));
        Assert.Null(ReportUsername.Fit("", ReportUsernameRules.Standard));
        Assert.Equal("Zoe Mac", ReportUsername.Fit("Zoe\u200B\u202EMac", ReportUsernameRules.Standard));
        Assert.Equal("DESKTOP-7Q2K1LM", ReportUsername.Fit("DESKTOP-7Q2K1LM", ReportUsernameRules.Standard));
    }

    [Fact]
    public void RandomNamesUseTheSafeAlphabet()
    {
        const string alphabet = "23456789abcdefghjkmnpqrstuvwxyz";
        var strict = new ReportUsernameRules(3, 16, ReportUsernameCharset.Ascii, spaces: false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            var name = ReportUsername.Random();
            Assert.StartsWith("racer-", name, StringComparison.Ordinal);
            Assert.Equal(12, name.Length);
            Assert.All(name[6..], c => Assert.Contains(c, alphabet));
            Assert.Null(ReportUsername.Validate(name, ReportUsernameRules.Standard));
            Assert.Null(ReportUsername.Validate(name, strict));
            names.Add(name);
        }

        Assert.True(names.Count > 190);
    }
}
