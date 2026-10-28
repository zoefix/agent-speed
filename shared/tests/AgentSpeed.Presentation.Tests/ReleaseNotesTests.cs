using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class ReleaseNotesTests
{
    private static NoteBlock H(string text) => new(NoteBlockKind.Heading, text);

    private static NoteBlock P(string text) => new(NoteBlockKind.Paragraph, text);

    private static NoteBlock B(string text, string marker = "\u2022", int indent = 0) => new(NoteBlockKind.Bullet, text, marker, indent);

    private static void AssertBlocks(string markdown, params NoteBlock[] expected) =>
        Assert.Equal(expected, ReleaseNotes.Parse(markdown));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\r\n\t\n")]
    public void BlankNotesYieldNoBlocks(string? markdown)
    {
        Assert.Empty(ReleaseNotes.Parse(markdown));
    }

    [Fact]
    public void ParsesGitHubGeneratedNotes()
    {
        const string notes = "## What's Changed\r\n"
            + "* Faster gauge by @zoefix in https://github.com/zoefix/agent-speed/pull/12\r\n"
            + "* Fix **crash** on resume by @some_user in https://github.com/zoefix/agent-speed/pull/13\r\n"
            + "\r\n"
            + "## New Contributors\r\n"
            + "* @some_user made their first contribution in https://github.com/zoefix/agent-speed/pull/13\r\n"
            + "\r\n"
            + "**Full Changelog**: https://github.com/zoefix/agent-speed/compare/v1.1.0...v1.2.0";

        AssertBlocks(
            notes,
            H("What's Changed"),
            B("Faster gauge by @zoefix in https://github.com/zoefix/agent-speed/pull/12"),
            B("Fix crash on resume by @some_user in https://github.com/zoefix/agent-speed/pull/13"),
            H("New Contributors"),
            B("@some_user made their first contribution in https://github.com/zoefix/agent-speed/pull/13"),
            P("Full Changelog: https://github.com/zoefix/agent-speed/compare/v1.1.0...v1.2.0"));
    }

    [Fact]
    public void HeadingsOfEveryLevel()
    {
        AssertBlocks(
            "# One\n## Two ##\n### Three ###   \n#### Four\n##### Five\n###### Six\n  # Indented",
            H("One"),
            H("Two"),
            H("Three"),
            H("Four"),
            H("Five"),
            H("Six"),
            H("Indented"));
    }

    [Fact]
    public void NotHeadings()
    {
        AssertBlocks("####### Seven\n\n#hashtag\n\n#5 issue", P("####### Seven"), P("#hashtag"), P("#5 issue"));
    }

    [Fact]
    public void HeadingInlineMarkupIsReduced()
    {
        AssertBlocks("## **Bold** and `code` [docs](https://x.example)", H("Bold and code docs"));
    }

    [Fact]
    public void ParagraphsJoinLinesAndSplitOnBlankLines()
    {
        AssertBlocks(
            "First line\nsecond line\n\nNext paragraph\r\n   indented continuation\n \t \nLast\rone",
            P("First line second line"),
            P("Next paragraph indented continuation"),
            P("Last one"));
    }

    [Fact]
    public void BlocksInterruptParagraphs()
    {
        AssertBlocks("Intro:\n- a\n- b\nAfter\n# Head\nMore", P("Intro:"), B("a"), B("b"), P("After"), H("Head"), P("More"));
    }

    [Fact]
    public void BulletMarkers()
    {
        AssertBlocks("- dash\n* star\n+ plus", B("dash"), B("star"), B("plus"));
    }

    [Fact]
    public void OrderedMarkersBecomeNumbers()
    {
        AssertBlocks("1. first\n2) second\n10. tenth\n999) last", B("first", "1."), B("second", "2."), B("tenth", "10."), B("last", "999."));
    }

    [Fact]
    public void NotListItems()
    {
        AssertBlocks("-dash\n\n1.5 release\n\n*emphasis* first\n\n1000. too long", P("-dash"), P("1.5 release"), P("emphasis first"), P("1000. too long"));
    }

    [Fact]
    public void NestingLevelsAreCappedAtThree()
    {
        AssertBlocks(
            "- a\n  - b\n    - c\n      - d\n        - e\n\t- tab\n   1. odd indent",
            B("a"),
            B("b", indent: 1),
            B("c", indent: 2),
            B("d", indent: 3),
            B("e", indent: 3),
            B("tab", indent: 2),
            B("odd indent", "1.", 1));
    }

    [Fact]
    public void IndentedLinesContinueTheBullet()
    {
        AssertBlocks(
            "- first line\n  continues **here**\n    and here\n- second\nNot indented",
            B("first line continues here and here"),
            B("second"),
            P("Not indented"));
    }

    [Fact]
    public void TaskBoxesAreStripped()
    {
        AssertBlocks(
            "- [ ] todo\n- [x] done\n- [X] Done too\n- [link](https://x.example) not a box",
            B("todo"),
            B("done"),
            B("Done too"),
            B("link not a box"));
    }

    [Fact]
    public void HorizontalRulesAreSkipped()
    {
        AssertBlocks("Above\n\n---\n***\n___\n- - -\n* * *\nBelow\n-----\nEnd", P("Above"), P("Below"), P("End"));
    }

    [Fact]
    public void FencedCodeIsKeptAsPlainText()
    {
        AssertBlocks(
            "Intro\n```csharp\nvar x = **1**; // [a](https://b.example)\n\n   `tick` &amp; <b>\n```\n~~~\n# not a heading\n~~~\nAfter",
            P("Intro"),
            P("var x = **1**; // [a](https://b.example)"),
            P("`tick` &amp; <b>"),
            P("# not a heading"),
            P("After"));
    }

    [Fact]
    public void UnclosedFenceKeepsTheRestAsCode()
    {
        AssertBlocks("Before\n```\n- not a bullet\n**raw**", P("Before"), P("- not a bullet"), P("**raw**"));
    }

    [Fact]
    public void BlockquotesBecomeParagraphs()
    {
        AssertBlocks("> Quoted **text**\n> continues\n\n>> nested\n\n> - not a list", P("Quoted text continues"), P("nested"), P("- not a list"));
    }

    [Fact]
    public void HtmlCommentsAreRemovedAcrossLines()
    {
        AssertBlocks(
            "Visible\n<!-- hidden\n# hidden heading\nmulti-line -->\nAlso visible <!-- inline --> here\n<!---->",
            P("Visible"),
            P("Also visible here"));
    }

    [Theory]
    [InlineData("**bold**", "bold")]
    [InlineData("__bold__", "bold")]
    [InlineData("*em*", "em")]
    [InlineData("~~strike~~", "strike")]
    [InlineData("`code`", "code")]
    [InlineData("``double ticks``", "double ticks")]
    [InlineData("**bold *nested* text**", "bold nested text")]
    [InlineData("a **b** c *d* e", "a b c d e")]
    [InlineData("[link](https://example.com/page)", "link")]
    [InlineData("see [the docs](https://example.com \"title\") now", "see the docs now")]
    [InlineData("![alt text](https://example.com/x.png)", "alt text")]
    [InlineData("![](https://example.com/x.png)", "")]
    [InlineData("[![badge](https://img.example/b.svg)](https://ci.example)", "badge")]
    [InlineData("<https://example.com/x>", "https://example.com/x")]
    [InlineData("<b>bold</b> and <span class=\"x\">span</span>", "bold and span")]
    [InlineData("<script>alert(1)</script>", "alert(1)")]
    [InlineData("<img src=x onerror=alert(1)>", "")]
    [InlineData("<a href=\"https://evil.example\">click</a>", "click")]
    [InlineData("line<br/>break", "linebreak")]
    [InlineData("Tom &amp; Jerry &lt;3", "Tom & Jerry <3")]
    [InlineData("&quot;quoted&quot; &#39;single&#39; &#x2713;", "\"quoted\" 'single' \u2713")]
    [InlineData("&lt;b&gt;not bold&lt;/b&gt;", "<b>not bold</b>")]
    [InlineData("1\\. not a list", "1. not a list")]
    [InlineData("\\# not a heading", "# not a heading")]
    [InlineData("\\_not em\\_", "_not em_")]
    [InlineData("C:\\path\\file", "C:\\path\\file")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("a_b and c_d", "a_b and c_d")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("a < b > c", "a < b > c")]
    [InlineData("a   b\t\tc \u00a0 d", "a b c d")]
    [InlineData("  padded  ", "padded")]
    [InlineData("by @zoefix in https://github.com/zoefix/agent-speed/pull/1", "by @zoefix in https://github.com/zoefix/agent-speed/pull/1")]
    public void InlineMarkupIsReducedToText(string markdown, string expected)
    {
        Assert.Equal(expected, ReleaseNotes.Inline(markdown));
    }

    [Theory]
    [InlineData("\\*not emphasis\\*", "*not emphasis*")]
    [InlineData("\\[not a link\\](x)", "[not a link](x)")]
    [InlineData("use \\`raw\\` ticks", "use `raw` ticks")]
    public void EscapedMarkupStaysLiteral(string markdown, string expected)
    {
        Assert.Equal(expected, ReleaseNotes.Inline(markdown));
    }

    [Fact]
    public void HeadingKeepsHashThatIsPartOfAWord()
    {
        AssertBlocks("# C#", H("C#"));
        AssertBlocks("# C# #", H("C#"));
        AssertBlocks("### Title ###", H("Title"));
        AssertBlocks("## ##", []);
        AssertBlocks("## Support for C#", H("Support for C#"));
        AssertBlocks("## Support for F# ##", H("Support for F#"));
    }

    [Fact]
    public void BlockquoteStructureIsKept()
    {
        AssertBlocks("- item\n> quote", B("item"), P("quote"));
        AssertBlocks("> first\n>\n> second", P("first"), P("second"));
    }

    [Fact]
    public void InlineRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ReleaseNotes.Inline(null!));
    }

    [Fact]
    public void BlockCountIsCapped()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 300).Select(i => $"Paragraph {i}"));
        var blocks = ReleaseNotes.Parse(paragraphs);
        Assert.Equal(ReleaseNotes.MaxBlocks, blocks.Count);
        Assert.Equal(P("Paragraph 199"), blocks[^1]);

        var bullets = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"- item {i}"));
        Assert.Equal(ReleaseNotes.MaxBlocks, ReleaseNotes.Parse(bullets).Count);

        var code = "```\n" + string.Join("\n", Enumerable.Range(0, 300).Select(i => $"line {i}")) + "\n```";
        Assert.Equal(ReleaseNotes.MaxBlocks, ReleaseNotes.Parse(code).Count);
    }

    [Fact]
    public void LongBlocksAreClipped()
    {
        var expected = new string('a', ReleaseNotes.MaxBlockLength) + "\u2026";

        Assert.Equal([P(expected)], ReleaseNotes.Parse(new string('a', 5000)));
        Assert.Equal([B(expected)], ReleaseNotes.Parse("- " + new string('a', 5000)));
        Assert.Equal([H(expected)], ReleaseNotes.Parse("# " + new string('a', 5000)));
        Assert.Equal([P(expected)], ReleaseNotes.Parse("```\n" + new string('a', 5000) + "\n```"));
        Assert.Equal(expected, ReleaseNotes.Inline(new string('a', 5000)));

        var exact = new string('b', ReleaseNotes.MaxBlockLength);
        Assert.Equal(exact, ReleaseNotes.Inline(exact));
    }

    [Fact]
    public void ClippingDoesNotSplitSurrogatePairs()
    {
        var text = ReleaseNotes.Inline(new string('a', ReleaseNotes.MaxBlockLength - 1) + "\U0001F680 tail");
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public void NothingIsKeptAsALink()
    {
        const string notes = """
            # [Release](https://hidden.example/heading)

            Read [the guide](https://hidden.example/guide) or ![logo](https://hidden.example/logo.png).

            - <a href="https://hidden.example/a">anchor</a>
            - [ref][1] and <https://shown.example/auto>

            > [quoted](https://hidden.example/quote)
            """;

        var blocks = ReleaseNotes.Parse(notes);

        Assert.Equal(
            [H("Release"), P("Read the guide or logo."), B("anchor"), B("[ref][1] and https://shown.example/auto"), P("quoted")],
            blocks);
        Assert.All(blocks, block =>
        {
            Assert.DoesNotContain("hidden.example", block.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("](", block.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("<a", block.Text, StringComparison.Ordinal);
        });
    }
}
