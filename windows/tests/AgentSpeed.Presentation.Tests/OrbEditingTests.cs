using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class OrbEditingTests
{
    private static readonly SourceSet Three = SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini]);

    private static readonly OrbDefinition Template = new("t")
    {
        Name = "Main",
        Style = OrbStyle.Needle,
        Size = 196,
        Opacity = 0.7,
        AlwaysOnTop = false,
        ClickThrough = true,
        FadeWhenIdle = true,
        AllSpaces = false,
        RedlineOutput = 800,
    };

    private static Func<string> Ids(params string[] ids)
    {
        var queue = new Queue<string>(ids);
        return () => queue.Count > 0 ? queue.Dequeue() : Guid.NewGuid().ToString();
    }

    private static OrbsDocument Many(int count) =>
        new(Enumerable.Range(0, count).Select(i => new OrbDefinition($"o{i}") { Sources = OrbSources.Of((SourceKind)(i % 8)) }));

    private static Localizer Language(string code) => new(Localizer.LoadEmbeddedTable(), code);

    [Fact]
    public void AddCopiesTheTemplatesLookAndTracksAnUntrackedSource()
    {
        var doc = new OrbsDocument([new OrbDefinition("first"), Template]);
        var result = OrbEditing.Add(doc, "t", Three, Ids("new"))!;
        Assert.Equal(["new"], result.Created);
        Assert.False(result.Limited);
        Assert.Equal(["first", "t", "new"], result.Document.Orbs.Select(o => o.Id));

        var added = result.Document.Find("new")!;
        Assert.Equal(OrbSources.Of(SourceKind.Codex), added.Sources);
        Assert.Null(added.Name);
        Assert.Equal(OrbStyle.Needle, added.Style);
        Assert.Equal(196, added.Size);
        Assert.Equal(0.7, added.Opacity);
        Assert.False(added.AlwaysOnTop);
        Assert.True(added.ClickThrough);
        Assert.True(added.FadeWhenIdle);
        Assert.False(added.AllSpaces);
        Assert.True(added.Visible);
        Assert.Null(added.RedlineOutput);

        var next = OrbEditing.Add(result.Document, null, Three, Ids("next"))!;
        Assert.Equal(OrbSources.Of(SourceKind.ClaudeCode), next.Document.Find("next")!.Sources);
        Assert.Equal(["first", "next", "t", "new"], next.Document.Orbs.Select(o => o.Id));
        var third = OrbEditing.Add(next.Document, "next", Three, Ids("third"))!.Document;
        Assert.Equal(OrbSources.Of(SourceKind.Gemini), third.Find("third")!.Sources);
        var fourth = OrbEditing.Add(third, "third", Three, Ids("fourth"))!.Document;
        Assert.True(fourth.Find("fourth")!.Sources.IsAll);
    }

    [Fact]
    public void AddIsRefusedAtEightOrbs()
    {
        Assert.Null(OrbEditing.Add(Many(8), "o0", Three));
        Assert.NotNull(OrbEditing.Add(Many(7), "o0", Three));
        Assert.Null(OrbEditing.Duplicate(Many(8), "o0"));
        Assert.Null(OrbEditing.Split(Many(7) with { Orbs = [.. Many(7).Orbs, new OrbDefinition("all")] }, "all", Three));
        Assert.False(Many(8).CanAdd);
    }

    [Fact]
    public void NewIdsAreFreshUuids()
    {
        var doc = new OrbsDocument([Template]);
        var added = OrbEditing.Add(doc, "t", Three)!;
        Assert.True(Guid.TryParse(added.Created[0], out _));
        var reused = OrbEditing.Add(doc, "t", Three, Ids("t", "t", "unique"))!;
        Assert.Equal(["unique"], reused.Created);
    }

    [Fact]
    public void DuplicateIsAnExactCopyWithANewId()
    {
        var doc = new OrbsDocument([Template, new OrbDefinition("other")]);
        var result = OrbEditing.Duplicate(doc, "t", Ids("copy"))!;
        Assert.Equal(["t", "copy", "other"], result.Document.Orbs.Select(o => o.Id));
        Assert.Equal(Template with { Id = "copy", Name = "Main 2" }, result.Document.Find("copy"));

        var automatic = OrbEditing.Duplicate(doc, "other", Ids("copy"))!.Document.Find("copy")!;
        Assert.Null(automatic.Name);
        Assert.Null(OrbEditing.Duplicate(doc, "missing"));
        Assert.Equal("012345678901234567890123456789 2", OrbNaming.DuplicateName("0123456789012345678901234567890"));
        Assert.Equal(32, OrbNaming.DuplicateName(new string('a', 32))!.Length);
        Assert.Null(OrbNaming.DuplicateName("   "));
    }

    [Fact]
    public void RemoveNeverRemovesTheLastOrb()
    {
        var doc = new OrbsDocument([Template, new OrbDefinition("b")]);
        Assert.Equal(["b"], OrbEditing.Remove(doc, "t")!.Orbs.Select(o => o.Id));
        Assert.Null(OrbEditing.Remove(new OrbsDocument([Template]), "t"));
        Assert.Null(OrbEditing.Remove(doc, "missing"));
        Assert.False(new OrbsDocument([Template]).CanRemove);
    }

    [Fact]
    public void SplitMakesOneOrbPerAvailableSource()
    {
        var orb = Template with { Sources = OrbSources.All };
        var doc = new OrbsDocument([new OrbDefinition("before"), orb, new OrbDefinition("after")]);
        Assert.True(OrbEditing.CanSplit(doc, "t", Three));

        var result = OrbEditing.Split(doc, "t", Three, Ids("c1", "c2"))!;
        Assert.Equal(["c1", "c2"], result.Created);
        Assert.False(result.Limited);
        Assert.Equal(["before", "t", "c1", "c2", "after"], result.Document.Orbs.Select(o => o.Id));

        var first = result.Document.Find("t")!;
        Assert.Equal(OrbSources.Of(SourceKind.Codex), first.Sources);
        Assert.Null(first.Name);
        foreach (var (id, source) in new[] { ("c1", SourceKind.ClaudeCode), ("c2", SourceKind.Gemini) })
        {
            var child = result.Document.Find(id)!;
            Assert.Equal(orb with { Id = id, Name = null, Sources = OrbSources.Of(source) }, child);
        }

        Assert.Equal(orb, doc.Find("t"));
    }

    [Fact]
    public void SplitUsesOnlyTheAvailableSourcesOfAnExplicitSet()
    {
        var orb = new OrbDefinition("x") { Sources = OrbSources.FromKeys(["codex", "gemini", "omp", "future"]) };
        var available = SourceSet.Create([SourceKind.Codex, SourceKind.Omp, SourceKind.ClaudeCode]);
        var result = OrbEditing.Split(new OrbsDocument([orb]), "x", available, Ids("c1"))!;
        Assert.Equal(OrbSources.Of(SourceKind.Codex), result.Document.Find("x")!.Sources);
        Assert.Equal(OrbSources.Of(SourceKind.Omp), result.Document.Find("c1")!.Sources);
        Assert.Equal(2, result.Document.Count);

        var single = new OrbDefinition("y") { Sources = OrbSources.FromKeys(["codex", "gemini"]) };
        Assert.False(OrbEditing.CanSplit(new OrbsDocument([single]), "y", available));
        Assert.Null(OrbEditing.Split(new OrbsDocument([single]), "y", available));
        Assert.Null(OrbEditing.Split(new OrbsDocument([new OrbDefinition("z")]), "z", SourceSet.Of(SourceKind.Codex)));
        Assert.Null(OrbEditing.Split(new OrbsDocument([single]), "missing", available));
    }

    [Fact]
    public void SplitStopsAtEightOrbs()
    {
        var doc = Many(6) with { Orbs = [.. Many(6).Orbs.Take(5), new OrbDefinition("all")] };
        var result = OrbEditing.Split(doc, "all", SourceSet.Everything, Ids("c1", "c2", "c3"))!;
        Assert.True(result.Limited);
        Assert.Equal(8, result.Document.Count);
        Assert.Equal(["c1", "c2"], result.Created);
        Assert.Equal(
            [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop],
            new[] { "all", "c1", "c2" }.Select(id => result.Document.Find(id)!.Sources.Known.Single()));
    }

    [Fact]
    public void SplitBySourceReplacesEveryOrb()
    {
        var doc = new OrbsDocument([new OrbDefinition("first") { Size = 120 }, Template])
            .WithPosition("A", "first", new OrbPositionRecord("A", OrbAxis.Low(12), OrbAxis.Low(12)))
            .WithPosition("A", "t", new OrbPositionRecord("A", OrbAxis.High(12), OrbAxis.Low(12)));
        Assert.True(OrbEditing.CanSplitBySource(Three));
        var result = OrbEditing.SplitBySource(doc, "t", Three, Ids("s1", "t", "s2"))!;
        Assert.Equal(["first", "s1", "s2"], result.Document.Orbs.Select(o => o.Id));
        Assert.Equal(["s1", "s2"], result.Created);
        Assert.False(result.Limited);
        Assert.All(result.Document.Orbs, o =>
        {
            Assert.Equal(OrbStyle.Needle, o.Style);
            Assert.Equal(196, o.Size);
            Assert.Null(o.Name);
            Assert.True(o.Visible);
        });
        Assert.Equal(
            [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini],
            result.Document.Orbs.Select(o => o.Sources.Known.Single()));
        Assert.Equal(["first"], result.Document.LayoutFor("A")!.Positions.Keys);

        Assert.False(OrbEditing.CanSplitBySource(SourceSet.Of(SourceKind.Codex)));
        Assert.Null(OrbEditing.SplitBySource(doc, "t", SourceSet.Of(SourceKind.Codex)));
        var eight = SourceSet.From(Enum.GetValues<SourceKind>().Take(8));
        var everything = OrbEditing.SplitBySource(doc, null, eight)!;
        Assert.Equal(8, everything.Document.Count);
        Assert.False(everything.Limited);
        Assert.Equal(120, everything.Document.Orbs[7].Size);

        var ten = OrbEditing.SplitBySource(doc, null, SourceSet.Everything)!;
        Assert.Equal(8, ten.Document.Count);
        Assert.True(ten.Limited);
        Assert.Equal(eight.ToArray(), ten.Document.Orbs.Select(o => o.Sources.Known.Single()));
    }

    [Fact]
    public void MergeKeepsOneAllSourcesOrb()
    {
        var kept = Template with { Sources = OrbSources.Of(SourceKind.Codex), Visible = false };
        var doc = new OrbsDocument([new OrbDefinition("a"), kept, new OrbDefinition("b")])
            .WithPosition("A", "a", new OrbPositionRecord("A", OrbAxis.Low(12), OrbAxis.Low(12)))
            .WithPosition("A", "t", new OrbPositionRecord("A", OrbAxis.High(12), OrbAxis.Low(12)))
            .WithPosition("B", "b", new OrbPositionRecord("B", OrbAxis.High(12), OrbAxis.Low(12)));
        var merged = OrbEditing.Merge(doc, "t")!;
        var orb = Assert.Single(merged.Orbs);
        Assert.Equal(kept with { Name = null, Sources = OrbSources.All, Visible = true }, orb);
        Assert.Equal(["t"], merged.LayoutFor("A")!.Positions.Keys);
        Assert.Null(merged.LayoutFor("B"));
        Assert.Null(OrbEditing.Merge(new OrbsDocument([Template]), "t"));
        Assert.Null(OrbEditing.Merge(doc, "missing"));
    }

    [Fact]
    public void SourcesFollowTheMenuRules()
    {
        var doc = new OrbsDocument([new OrbDefinition("x")]);
        var single = OrbEditing.ToggleSource(doc, "x", SourceKind.Gemini)!;
        Assert.Equal(OrbSources.Of(SourceKind.Gemini), single.Find("x")!.Sources);
        Assert.Null(OrbEditing.ToggleSource(single, "x", SourceKind.Gemini));
        Assert.False(OrbEditing.CanUncheck(single.Find("x")!, SourceKind.Gemini));

        var pair = OrbEditing.ToggleSource(single, "x", SourceKind.Codex)!;
        Assert.Equal(["codex", "gemini"], pair.Find("x")!.Sources.ToKeys());
        Assert.True(OrbEditing.CanUncheck(pair.Find("x")!, SourceKind.Gemini));
        Assert.Equal(OrbSources.Of(SourceKind.Codex), OrbEditing.ToggleSource(pair, "x", SourceKind.Gemini)!.Find("x")!.Sources);
        Assert.True(OrbEditing.SetAllSources(pair, "x").Find("x")!.Sources.IsAll);

        Assert.Null(OrbEditing.SetSources(doc, "x", OrbSources.FromKeys(["future"])));
        Assert.Equal(OrbSources.Of(SourceKind.Omp), OrbEditing.SetSources(doc, "x", OrbSources.Of(SourceKind.Omp))!.Find("x")!.Sources);
        Assert.Null(OrbEditing.ToggleSource(doc, "missing", SourceKind.Omp));
    }

    [Fact]
    public void SingleOrbEditsAreClampedAndKeepTheId()
    {
        var doc = new OrbsDocument([new OrbDefinition("x") { Style = OrbStyle.Minimal, Size = 72 }]);
        Assert.Equal(96, OrbEditing.SetStyle(doc, "x", OrbStyle.Liquid).Find("x")!.Size);
        Assert.Equal(64, OrbEditing.SetSize(doc, "x", 10).Find("x")!.Size);
        Assert.Equal(0.4, OrbEditing.SetOpacity(doc, "x", 0.1).Find("x")!.Opacity);
        Assert.False(OrbEditing.SetAlwaysOnTop(doc, "x", false).Find("x")!.AlwaysOnTop);
        Assert.True(OrbEditing.SetClickThrough(doc, "x", true).Find("x")!.ClickThrough);
        Assert.True(OrbEditing.SetFadeWhenIdle(doc, "x", true).Find("x")!.FadeWhenIdle);
        Assert.False(OrbEditing.SetAllSpaces(doc, "x", false).Find("x")!.AllSpaces);
        Assert.False(OrbEditing.SetVisible(doc, "x", false).Find("x")!.Visible);
        Assert.Equal("Desk", OrbEditing.SetName(doc, "x", "  Desk ").Find("x")!.Name);
        Assert.Null(OrbEditing.SetName(doc, "x", "").Find("x")!.Name);
        Assert.Equal(1250, OrbEditing.SetRedline(doc, "x", SpeedMetric.Output, 1234).Find("x")!.RedlineOutput);
        Assert.Null(OrbEditing.SetRedline(doc, "x", SpeedMetric.Output, null).Find("x")!.RedlineOutput);
        Assert.Same(doc, OrbEditing.SetVisible(doc, "missing", false));
    }

    [Fact]
    public void ReorderingChangesOnlyTheListOrder()
    {
        var doc = Many(4).WithPosition("A", "o0", new OrbPositionRecord("A", OrbAxis.Low(12), OrbAxis.Low(12)));
        var moved = OrbEditing.Move(doc, "o0", 2);
        Assert.Equal(["o1", "o2", "o0", "o3"], moved.Orbs.Select(o => o.Id));
        Assert.Equal(doc.Layouts, moved.Layouts);
        Assert.Equal(["o3", "o0", "o1", "o2"], OrbEditing.Move(doc, "o3", -5).Orbs.Select(o => o.Id));
        Assert.Equal(["o1", "o2", "o3", "o0"], OrbEditing.Move(doc, "o0", 99).Orbs.Select(o => o.Id));
        Assert.Same(doc, OrbEditing.Move(doc, "o1", 1));
        Assert.Same(doc, OrbEditing.Move(doc, "missing", 0));
    }

    [Fact]
    public void ShowAllOnlyActsWhenEveryOrbIsHidden()
    {
        var hidden = new OrbsDocument([new OrbDefinition("a") { Visible = false }, new OrbDefinition("b") { Visible = false }]);
        Assert.True(hidden.AllHidden);
        Assert.All(OrbEditing.ShowAll(hidden).Orbs, o => Assert.True(o.Visible));
        var mixed = OrbEditing.SetVisible(hidden, "a", true);
        Assert.Same(mixed, OrbEditing.ShowAll(mixed));
    }

    [Fact]
    public void AutomaticNamesInEnglish()
    {
        var en = Language(LanguageCodes.En);
        Assert.Equal("All Sources", OrbNaming.AutomaticName(OrbSources.All, en));
        Assert.Equal("All Sources", OrbNaming.AutomaticName(OrbSources.FromKeys(["future"]), en));
        Assert.Equal("Claude Code", OrbNaming.AutomaticName(OrbSources.Of(SourceKind.ClaudeCode), en));
        Assert.Equal("Claude Code + Claude Desktop", OrbNaming.AutomaticName(OrbSources.FromKeys(["claudeDesktop", "claudeCode"]), en));
        Assert.Equal("Codex +2", OrbNaming.AutomaticName(OrbSources.Of(Three), en));
        Assert.Equal("Codex +17", OrbNaming.AutomaticName(OrbSources.Of(SourceSet.Everything), en));
        Assert.Equal("Main", OrbNaming.DisplayName(Template, en));
        Assert.Equal("Gemini CLI", OrbNaming.DisplayName(new OrbDefinition("x") { Sources = OrbSources.Of(SourceKind.Gemini) }, en));
        Assert.Equal("Codex, Claude Code, Gemini CLI", OrbNaming.SourceList(Three, en));
    }

    [Fact]
    public void AutomaticNamesInTraditionalChinese()
    {
        var zh = Language(LanguageCodes.ZhHant);
        Assert.Equal("全部來源", OrbNaming.AutomaticName(OrbSources.All, zh));
        Assert.Equal("Codex", OrbNaming.AutomaticName(OrbSources.Of(SourceKind.Codex), zh));
        Assert.Equal("Claude Code + Claude Desktop", OrbNaming.AutomaticName(OrbSources.Of(SourceSet.Create([SourceKind.ClaudeCode, SourceKind.ClaudeDesktop])), zh));
        Assert.Equal("Codex +2", OrbNaming.AutomaticName(OrbSources.Of(Three), zh));
        Assert.Equal("Codex、Claude Code、Gemini CLI", OrbNaming.SourceList(Three, zh));
        Assert.Equal("工作", OrbNaming.DisplayName(new OrbDefinition("x") { Name = "工作" }, zh));
    }

    [Fact]
    public void ThePlateSettingIsCopiedByAddDuplicateAndSplitAndKeptByMerge()
    {
        var shown = Template with { ShowName = true, Sources = OrbSources.Of(Three) };
        var hidden = new OrbDefinition("h") { ShowName = false };
        var doc = new OrbsDocument([hidden, shown]);

        Assert.True(OrbEditing.Add(doc, "t", Three, Ids("n"))!.Document.Find("n")!.ShowName);
        Assert.True(OrbEditing.Duplicate(doc, "t", Ids("d"))!.Document.Find("d")!.ShowName);

        var split = OrbEditing.Split(doc, "t", Three, Ids("c1", "c2"))!.Document;
        Assert.All(split.Orbs.Where(o => o.Id != "h"), o => Assert.True(o.ShowName));
        Assert.All(OrbEditing.SplitBySource(doc, "h", Three, Ids("s1", "s2"))!.Document.Orbs, o => Assert.False(o.ShowName));

        Assert.False(Assert.Single(OrbEditing.Merge(doc, "h")!.Orbs).ShowName);
        Assert.True(Assert.Single(OrbEditing.Merge(doc, "t")!.Orbs).ShowName);
        Assert.Null(Assert.Single(OrbEditing.Merge(new OrbsDocument([new OrbDefinition("x"), shown]), "x")!.Orbs).ShowName);
    }

    [Fact]
    public void SetShowNameStoresTheOverrideOrAutomatic()
    {
        var doc = new OrbsDocument([new OrbDefinition("a")]);
        Assert.True(OrbEditing.SetShowName(doc, "a", true).Find("a")!.ShowName);
        Assert.False(OrbEditing.SetShowName(doc, "a", false).Find("a")!.ShowName);
        Assert.Null(OrbEditing.SetShowName(OrbEditing.SetShowName(doc, "a", true), "a", null).Find("a")!.ShowName);
        Assert.Same(doc, OrbEditing.SetShowName(doc, "missing", true));
    }
}
