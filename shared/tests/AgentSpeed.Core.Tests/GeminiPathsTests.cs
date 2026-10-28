using System.Text;
using AgentSpeed.Core.Gemini;

namespace AgentSpeed.Core.Tests;

public sealed class GeminiPathsTests
{
    [Theory]
    [InlineData("7f3a2b10-5c1d-4e2f-9a3b-0c4d5e6f7a8b", "7f3a2b10-5c1d-4e2f-9a3b-0c4d5e6f7a8b")]
    [InlineData("Session_01-AZ", "Session_01-AZ")]
    [InlineData("a.b/c\\d e", "a_b_c_d_e")]
    [InlineData("caf\u00e9", "caf_")]
    [InlineData("x\U0001F600y", "x_y")]
    [InlineData("\u0661\uFF21", "__")]
    [InlineData("", "")]
    public void SanitizeReplacesEveryOtherCodePoint(string id, string expected) =>
        Assert.Equal(expected, GeminiPaths.Sanitize(id));

    [Fact]
    public void SanitizeReplacesEachLoneSurrogate()
    {
        Assert.Equal("x_y", GeminiPaths.Sanitize("x\uD800y"));
        Assert.Equal("__", GeminiPaths.Sanitize("\uDC00\uD800"));
        Assert.Equal("_", GeminiPaths.Sanitize("\uD83D\uDE00"));
    }

    [Fact]
    public void SanitizeReturnsCleanIdsUnchanged()
    {
        var id = new StringBuilder("abc-123").ToString();

        Assert.Same(id, GeminiPaths.Sanitize(id));
    }

    [Theory]
    [InlineData("/home/me/.gemini/tmp/proj/chats/s.jsonl", true, null, "/home/me/.gemini/tmp/proj")]
    [InlineData("/home/me/.gemini/tmp/proj/chats/parent-id/sub.jsonl", false, "parent-id", "/home/me/.gemini/tmp/proj")]
    [InlineData(@"C:\Users\me\.gemini\tmp\proj\chats\s.json", true, null, @"C:\Users\me\.gemini\tmp\proj")]
    [InlineData(@"C:\Users\me\.gemini\tmp\proj\chats\parent-id\sub.json", false, "parent-id", @"C:\Users\me\.gemini\tmp\proj")]
    [InlineData("/home/me/.gemini/tmp/proj/s.jsonl", false, null, null)]
    [InlineData("/home/me/.gemini/tmp/proj/chats/a/b/s.jsonl", false, null, null)]
    [InlineData("/home/me/.gemini/tmp/proj/Chats/s.jsonl", false, null, null)]
    [InlineData("/x/chats/chats/s.jsonl", true, "chats", "/x/chats")]
    [InlineData("chats/s.jsonl", true, null, null)]
    [InlineData("/chats/s.jsonl", true, null, null)]
    public void SessionLayout(string path, bool isMain, string? parentDirectory, string? projectDirectory)
    {
        Assert.Equal(isMain, GeminiPaths.IsMainSessionPath(path));
        Assert.Equal(parentDirectory, GeminiPaths.ParentDirectoryOf(path));
        Assert.Equal(projectDirectory, GeminiPaths.ProjectDirectoryOf(path));
    }
}
