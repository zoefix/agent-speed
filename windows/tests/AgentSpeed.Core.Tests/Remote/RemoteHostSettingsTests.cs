using System.Text.Json;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The settings value <c>remoteHosts</c> and its validation (REMOTE 2.1).</summary>
public sealed class RemoteHostSettingsTests
{
    private const string Id1 = "8C0D6B0A-1111-4222-8333-944455556666";
    private const string Id2 = "8C0D6B0A-1111-4222-8333-944455556667";

    public static TheoryData<string> AcceptedDestinations() =>
    [
        "devbox", "user@host", "ssh://host", "ssh://user@host:2222", "ssh://[::1]:22", "10.0.0.7", "a.b-c_d%e+f=g,h~i",
        "user@[fe80::1]", new string('h', 255),
    ];

    [Theory]
    [MemberData(nameof(AcceptedDestinations))]
    public void AcceptsDestinations(string destination) => Assert.Null(RemoteHostValidation.DestinationIssue(destination));

    [Fact]
    public void RefusesDestinations()
    {
        (string Destination, RemoteFieldIssue Issue)[] cases =
        [
            ("", RemoteFieldIssue.Empty),
            (new string('h', 256), RemoteFieldIssue.TooLong),
            ("-oProxyCommand=evil", RemoteFieldIssue.LeadingDash),
            ("-V", RemoteFieldIssue.LeadingDash),
            ("host;rm", RemoteFieldIssue.ForbiddenCharacter),
            ("$(x)", RemoteFieldIssue.ForbiddenCharacter),
            ("`id`", RemoteFieldIssue.ForbiddenCharacter),
            ("my host", RemoteFieldIssue.ForbiddenCharacter),
            ("host\ttab", RemoteFieldIssue.ForbiddenCharacter),
            ("host\n", RemoteFieldIssue.ForbiddenCharacter),
            ("a|b", RemoteFieldIssue.ForbiddenCharacter),
            ("a&b", RemoteFieldIssue.ForbiddenCharacter),
            ("a>b", RemoteFieldIssue.ForbiddenCharacter),
            ("a<b", RemoteFieldIssue.ForbiddenCharacter),
            ("a'b", RemoteFieldIssue.ForbiddenCharacter),
            ("a\"b", RemoteFieldIssue.ForbiddenCharacter),
            ("a\\b", RemoteFieldIssue.ForbiddenCharacter),
            ("a*", RemoteFieldIssue.ForbiddenCharacter),
            ("a?", RemoteFieldIssue.ForbiddenCharacter),
            ("a!", RemoteFieldIssue.ForbiddenCharacter),
            ("{a}", RemoteFieldIssue.ForbiddenCharacter),
            ("a#b", RemoteFieldIssue.ForbiddenCharacter),
            ("(a)", RemoteFieldIssue.ForbiddenCharacter),
            ("hôst", RemoteFieldIssue.ForbiddenCharacter),
            ("host\u007F", RemoteFieldIssue.ForbiddenCharacter),
            ("-a b", RemoteFieldIssue.ForbiddenCharacter),
            (new string('é', 128), RemoteFieldIssue.TooLong),
        ];
        foreach (var (destination, issue) in cases)
        {
            Assert.True(issue == RemoteHostValidation.DestinationIssue(destination), $"{destination}: {RemoteHostValidation.DestinationIssue(destination)}");
        }

        Assert.Equal(RemoteFieldIssue.Empty, RemoteHostValidation.DestinationIssue(null));
    }

    [Theory]
    [InlineData("~/.cache/agent-speed")]
    [InlineData("/opt/agent-speed")]
    [InlineData("/srv/a.b_c-d/~x")]
    [InlineData("~/x/")]
    [InlineData("/")]
    public void AcceptsFolders(string folder) => Assert.Null(RemoteHostValidation.FolderIssue(folder));

    [Fact]
    public void RefusesFolders()
    {
        (string Folder, RemoteFieldIssue Issue)[] cases =
        [
            ("", RemoteFieldIssue.Empty),
            ("/" + new string('a', 200), RemoteFieldIssue.TooLong),
            ("relative/path", RemoteFieldIssue.NotAbsolute),
            ("~", RemoteFieldIssue.NotAbsolute),
            ("~root/x", RemoteFieldIssue.NotAbsolute),
            ("-rf", RemoteFieldIssue.NotAbsolute),
            ("~/a/../b", RemoteFieldIssue.UnsafeComponent),
            ("/..", RemoteFieldIssue.UnsafeComponent),
            ("~/..", RemoteFieldIssue.UnsafeComponent),
            ("/a//b", RemoteFieldIssue.UnsafeComponent),
            ("//", RemoteFieldIssue.UnsafeComponent),
            ("~/my folder", RemoteFieldIssue.ForbiddenCharacter),
            ("~/$(x)", RemoteFieldIssue.ForbiddenCharacter),
            ("~/a;b", RemoteFieldIssue.ForbiddenCharacter),
            ("~/*", RemoteFieldIssue.ForbiddenCharacter),
            ("~/a\nb", RemoteFieldIssue.ForbiddenCharacter),
            ("~/ä", RemoteFieldIssue.ForbiddenCharacter),
            ("~/a'b", RemoteFieldIssue.ForbiddenCharacter),
            ("~/a\\b", RemoteFieldIssue.ForbiddenCharacter),
        ];
        foreach (var (folder, issue) in cases)
        {
            Assert.True(issue == RemoteHostValidation.FolderIssue(folder), $"{folder}: {RemoteHostValidation.FolderIssue(folder)}");
        }
    }

    [Fact]
    public void IdsAreUpperCaseUuidsAndNeverLocal()
    {
        Assert.True(RemoteHostValidation.IsValidId(Id1));
        Assert.True(RemoteHostValidation.IsValidId(RemoteHost.NewId()));
        Assert.False(RemoteHostValidation.IsValidId(Id1.ToLowerInvariant()));
        Assert.False(RemoteHostValidation.IsValidId("{" + Id1 + "}"));
        Assert.False(RemoteHostValidation.IsValidId(Id1.Replace("-", string.Empty, StringComparison.Ordinal)));
        Assert.False(RemoteHostValidation.IsValidId("local"));
        Assert.False(RemoteHostValidation.IsValidId("devbox"));
        Assert.False(RemoteHostValidation.IsValidId(""));
        Assert.False(RemoteHostValidation.IsValidId(null));
    }

    [Fact]
    public void NamesAreTrimmedAndLimited()
    {
        Assert.Equal("Dev Box", RemoteHostValidation.CleanName("  Dev Box  "));
        Assert.Null(RemoteHostValidation.CleanName("   "));
        Assert.Null(RemoteHostValidation.CleanName(null));
        Assert.Equal(32, RemoteHostValidation.CleanName(new string('x', 40))?.Length);
        Assert.Equal("abc", RemoteHostValidation.CleanName("a\0b\nc"));
        Assert.Equal("ab", RemoteHostValidation.CleanName("a\u0085b"));
        Assert.Null(new RemoteHost("devbox") { Name = " " }.Name);
        Assert.Equal("Lab", new RemoteHost("devbox") { Name = " Lab " }.Name);
    }

    [Fact]
    public void AutomaticNamesAreTheHostPart()
    {
        (string Destination, string Name)[] cases =
        [
            ("devbox", "devbox"),
            ("user@devbox", "devbox"),
            ("ssh://devbox", "devbox"),
            ("ssh://user@devbox.example.com:2222", "devbox.example.com"),
            ("ssh://[::1]:22", "::1"),
            ("SSH://user@host", "host"),
            ("a@b@host", "host"),
            ("ssh://", "ssh://"),
            ("user@[fe80::1]", "fe80::1"),
        ];
        foreach (var (destination, name) in cases)
        {
            Assert.True(name == RemoteHostValidation.AutomaticName(destination), $"{destination}: {RemoteHostValidation.AutomaticName(destination)}");
        }

        Assert.Equal("devbox", new RemoteHost("alice@devbox").AutomaticName);
        Assert.Equal(32, RemoteHostValidation.AutomaticName(new string('h', 100)).Length);
    }

    [Fact]
    public void DuplicateAutomaticNamesGetCounters()
    {
        var document = new RemoteHostsDocument(
        [
            new RemoteHost("devbox", Id1),
            new RemoteHost("alice@devbox", "8C0D6B0A-1111-4222-8333-000000000003"),
            new RemoteHost("ssh://bob@devbox:22", "8C0D6B0A-1111-4222-8333-000000000004"),
            new RemoteHost("other", "8C0D6B0A-1111-4222-8333-000000000005") { Name = "Build" },
            new RemoteHost("build", "8C0D6B0A-1111-4222-8333-000000000006"),
        ]);
        var names = document.DisplayNames();
        Assert.Equal("devbox", names[Id1]);
        Assert.Equal("devbox (2)", names["8C0D6B0A-1111-4222-8333-000000000003"]);
        Assert.Equal("devbox (3)", names["8C0D6B0A-1111-4222-8333-000000000004"]);
        Assert.Equal("Build", names["8C0D6B0A-1111-4222-8333-000000000005"]);
        Assert.Equal("build (2)", names["8C0D6B0A-1111-4222-8333-000000000006"]);
        Assert.Equal("devbox (3)", document.DisplayName("8C0D6B0A-1111-4222-8333-000000000004"));
        Assert.Null(document.DisplayName(Id2));
    }

    [Fact]
    public void AtMostEightHosts()
    {
        var document = RemoteHostsDocument.Empty;
        for (var i = 0; i < 8; i++)
        {
            document = document.Adding(new RemoteHost("devbox")) ?? throw new InvalidOperationException("host refused");
        }

        Assert.False(document.CanAdd);
        Assert.Null(document.Adding(new RemoteHost("devbox")));
        Assert.Equal(8, document.Hosts.Length);
        var many = new RemoteHostsDocument(Enumerable.Range(0, 12).Select(_ => new RemoteHost("x")));
        Assert.Equal(8, many.Hosts.Length);
    }

    [Fact]
    public void AddRefusesReservedAndDuplicateIds()
    {
        var document = RemoteHostsDocument.Empty;
        Assert.Null(document.Adding(new RemoteHost("devbox", "local")));
        Assert.Null(document.Adding(new RemoteHost("devbox", Id1.ToLowerInvariant())));
        document = document.Adding(new RemoteHost("devbox", Id1));
        Assert.NotNull(document);
        Assert.Null(document.Adding(new RemoteHost("other", Id1)));
        Assert.Null(document.Updating(new RemoteHost("other", Id2)));
        document = document.Updating(new RemoteHost("other", Id1) { Enabled = false });
        Assert.NotNull(document);
        Assert.Equal("other", document.Host(Id1)?.Destination);
        Assert.False(document.Host(Id1)?.Enabled);
        Assert.Same(document, document.Removing(Id2));
        Assert.Empty(document.Removing(Id1).Hosts);
    }

    [Fact]
    public void UpdatingKeepsTheHostsUnknownMembers()
    {
        var document = RemoteHostsJson.Parse($$"""{"version":1,"hosts":[{"id":"{{Id1}}","destination":"devbox","color":"teal"}]}""");
        var updated = document.Updating(new RemoteHost("other", Id1));
        Assert.NotNull(updated);
        Assert.Equal("teal", Object(RemoteHostsJson.ToJson(updated)).GetProperty("hosts")[0].GetProperty("color").GetString());
    }

    [Fact]
    public void RoundTripKeepsUnknownFields()
    {
        var json = $$"""
            {"version":1,"future":{"a":[1,2.5,"x",null,true]},"hosts":[
              {"id":"{{Id1}}","destination":"devbox","name":null,"enabled":true,"relayFolder":null,"color":"teal","n":12},
              {"id":"{{Id2}}","destination":"ssh://u@h:2","name":"Lab","enabled":false,"relayFolder":"/opt/as"}
            ]}
            """;
        var document = RemoteHostsJson.Parse(json);
        Assert.Equal(2, document.Hosts.Length);
        Assert.Equal("Lab", document.Hosts[1].Name);
        Assert.False(document.Hosts[1].Enabled);
        Assert.Equal("/opt/as", document.Hosts[1].RelayFolder);

        var written = Object(RemoteHostsJson.ToJson(document));
        Assert.Equal(1, written.GetProperty("version").GetInt32());
        Assert.True(JsonElement.DeepEquals(Object("""{"a":[1,2.5,"x",null,true]}"""), written.GetProperty("future")));
        var first = written.GetProperty("hosts")[0];
        Assert.Equal("teal", first.GetProperty("color").GetString());
        Assert.Equal(12, first.GetProperty("n").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("relayFolder").ValueKind);
        Assert.True(first.GetProperty("enabled").GetBoolean());
        Assert.False(written.GetProperty("hosts")[1].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void RoundTripIsStable()
    {
        var json = $$"""
            {"version":1,"future":1,"later":2,"hosts":[
              {"id":"{{Id1}}","destination":"devbox","color":"teal","n":12}
            ]}
            """;
        var document = RemoteHostsJson.Parse(json);
        var again = RemoteHostsJson.Parse(RemoteHostsJson.ToJson(document));
        Assert.Equal(document, again);
        Assert.Equal(RemoteHostsJson.ToJson(document), RemoteHostsJson.ToJson(again));
        var written = Object(RemoteHostsJson.ToJson(document));
        Assert.Equal(["future", "later"], written.EnumerateObject().Select(m => m.Name).Where(n => n is "future" or "later"));
        Assert.Equal(["color", "n"], written.GetProperty("hosts")[0].EnumerateObject().Select(m => m.Name).Where(n => n is "color" or "n"));
    }

    [Fact]
    public void RepeatedUnknownMembersKeepTheLast()
    {
        var document = RemoteHostsJson.Parse($$"""{"version":1,"x":1,"x":2,"hosts":[]}""");
        var written = Object(RemoteHostsJson.ToJson(document));
        Assert.Equal(2, written.GetProperty("x").GetInt32());
        Assert.Single(written.EnumerateObject(), m => m.Name == "x");
    }

    [Fact]
    public void DecodingSkipsInvalidHostsAndKeepsInvalidDestinations()
    {
        var json = $$"""
            {"version":1,"hosts":[
              {"id":"local","destination":"devbox"},
              {"id":"not-a-uuid","destination":"devbox"},
              {"destination":"devbox"},
              {"id":"{{Id1}}","destination":"-oProxyCommand=x","enabled":true},
              {"id":"{{Id1}}","destination":"again"},
              {"id":"{{Id2}}","destination":"ok","relayFolder":"~/../etc"},
              7
            ]}
            """;
        var document = RemoteHostsJson.Parse(json);
        Assert.Equal([Id1, Id2], document.Hosts.Select(h => h.Id));
        Assert.Equal("-oProxyCommand=x", document.Hosts[0].Destination);
        Assert.False(document.Hosts[0].IsConnectable);
        Assert.False(document.Hosts[1].IsConnectable);
        Assert.Empty(RemoteHostsJson.Parse("[]").Hosts);
        Assert.Empty(RemoteHostsJson.Parse("not json").Hosts);
        Assert.Empty(RemoteHostsJson.Parse("""{"version":1}""").Hosts);
        Assert.Empty(RemoteHostsJson.Parse("""{"version":1,"hosts":{}}""").Hosts);
    }

    [Fact]
    public void ReadingCutsAtEightHosts()
    {
        var hosts = string.Join(',', Enumerable.Range(0, 10).Select(_ => $$"""{"id":"{{RemoteHost.NewId()}}","destination":"x"}"""));
        Assert.Equal(8, RemoteHostsJson.Parse($$"""{"version":1,"hosts":[{{hosts}}]}""").Hosts.Length);
    }

    [Fact]
    public void EffectiveFolderDefaultsAndDropsTrailingSlash()
    {
        Assert.Equal("~/.cache/agent-speed", new RemoteHost("d").EffectiveRelayFolder);
        Assert.Equal("/opt/as", new RemoteHost("d") { RelayFolder = "/opt/as/" }.EffectiveRelayFolder);
        Assert.Equal("/opt/as", new RemoteHost("d") { RelayFolder = "/opt/as///" }.EffectiveRelayFolder);
        Assert.Equal("~", new RemoteHost("d") { RelayFolder = "~/" }.EffectiveRelayFolder);
        Assert.Equal("/", new RemoteHost("d") { RelayFolder = "/" }.EffectiveRelayFolder);
        Assert.Null(new RemoteHost("d") { RelayFolder = "" }.RelayFolder);
        Assert.True(new RemoteHost("d") { RelayFolder = "" }.IsConnectable);
        Assert.False(new RemoteHost("d") { RelayFolder = "relative" }.IsConnectable);
    }

    private static JsonElement Object(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
