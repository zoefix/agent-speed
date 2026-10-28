using System.Text;
using AgentSpeed.Core.Gemini;

namespace AgentSpeed.Core.Tests;

public sealed class GeminiProjectsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("/Users/dev/projects/nova", "nova")]
    [InlineData("/Users/dev/projects/nova/\n", "nova")]
    [InlineData("c:\\users\\dev\\projects\\orbit", "orbit")]
    [InlineData("C:\\Users\\dev\\projects\\orbit\\\r\n", "orbit")]
    [InlineData("\u001c\t /srv/app \u001f\u3000\n", "app")]
    [InlineData("relative-name", "relative-name")]
    public void ProjectRootFileNamesTheProject(string content, string expected)
    {
        var session = Session("slug", content);

        Assert.Equal(expected, GeminiProjects.ProjectOf(session));
    }

    [Fact]
    public void SubagentSessionsShareTheirProjectFolder()
    {
        Session("slug", "/work/agent-speed\n");

        Assert.Equal("agent-speed", GeminiProjects.ProjectOf(_temp.Combine("tmp", "slug", "chats", "parent-id", "sub.jsonl")));
    }

    [Fact]
    public void BlankProjectRootMeansNoProjectWithoutFallingBack()
    {
        _temp.WriteFile("projects.json", """{"projects":{"/work/agent-speed":"slug"}}""");
        var session = Session("slug", " \n\t");

        Assert.Null(GeminiProjects.ProjectOf(session));
    }

    [Fact]
    public void RegistryNamesTheProjectWhenTheFolderHasNoProjectRoot()
    {
        _temp.WriteFile("projects.json", """{"projects":{"/work/one":"other","c:\\work\\agent-speed":"slug","/work/two":"slug"}}""");

        Assert.Equal("agent-speed", GeminiProjects.ProjectOf(Session("slug", null)));
        Assert.Null(GeminiProjects.ProjectOf(Session("unknown", null)));
    }

    [Fact]
    public void ProjectRootIsReadOncePerFolder()
    {
        var session = Session("slug", "/work/first");
        Assert.Equal("first", GeminiProjects.ProjectOf(session));

        File.WriteAllText(_temp.Combine("tmp", "slug", ".project_root"), "/work/second");

        Assert.Equal("first", GeminiProjects.ProjectOf(session));
    }

    [Theory]
    [InlineData("/home/me/.gemini/tmp/proj/session.jsonl")]
    [InlineData("/home/me/.gemini/tmp/proj/chats/a/b/session.jsonl")]
    [InlineData("session.jsonl")]
    [InlineData("chats/session.jsonl")]
    public void PathsOutsideTheSessionLayoutHaveNoProject(string path) => Assert.Null(GeminiProjects.ProjectOf(path));

    [Theory]
    [InlineData("""{"projects":{"/a/x":"other","/b/y":"slug","/a/x":"slug"}}""", "/a/x")]
    [InlineData("""{"projects":{"/a/x":"slug"},"projects":{"/b/y":"slug"}}""", "/b/y")]
    [InlineData("""{"projects":{"/a/x":"slug"},"projects":null}""", null)]
    [InlineData("""{"projects":{"/a/x":5,"/b/y":"slug"}}""", "/b/y")]
    [InlineData("""{"projects":{"/a/x":"Slug"}}""", null)]
    [InlineData("""{"projects":["/a/x","slug"]}""", null)]
    [InlineData("""{"other":{"/a/x":"slug"}}""", null)]
    [InlineData("""["slug"]""", null)]
    [InlineData("""{"projects":{"/a/x":"slug"}""", null)]
    [InlineData("""{"projects":{"/a/x":"slug"}} []""", null)]
    [InlineData("", null)]
    public void RegistryLookupFollowsPythonJsonSemantics(string json, string? expected) =>
        Assert.Equal(expected, GeminiProjects.RegisteredRoot(Encoding.UTF8.GetBytes(json), "slug"));

    [Fact]
    public void RegistryWithAByteOrderMarkIsRejected()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"projects":{"/a/x":"slug"}}""")).ToArray();

        Assert.Null(GeminiProjects.RegisteredRoot(json, "slug"));
    }

    [Fact]
    public void FirstMatchingRegistryKeyWinsEvenWithoutABasename()
    {
        _temp.WriteFile("projects.json", """{"projects":{"/":"slug","/work/two":"slug"}}""");

        Assert.Null(GeminiProjects.ProjectOf(Session("slug", null)));
    }

    // <temp>/tmp/<slug>/chats/session.jsonl, with <temp>/projects.json as the registry.
    private string Session(string slug, string? projectRoot)
    {
        var session = _temp.WriteFile($"tmp/{slug}/chats/session.jsonl", "{}\n");
        if (projectRoot is not null)
        {
            File.WriteAllText(_temp.Combine("tmp", slug, ".project_root"), projectRoot);
        }

        return session;
    }
}
