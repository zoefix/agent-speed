using System.Text.Json.Nodes;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>DESIGN 11 dashboard invitation and REPORTING 3.3 one-click consent.</summary>
public sealed class ReportInviteTests
{
    private static ReportDiscovery Document(string service, string api, string mode = "none", JsonObject? username = null)
    {
        var json = new JsonObject
        {
            ["protocol"] = "agent-speed-report",
            ["versions"] = new JsonArray(1),
            ["service"] = new JsonObject { ["name"] = "Service", ["terms"] = "https://example.com/t", ["privacy"] = "https://example.com/p" },
            ["api"] = api,
            ["apiKey"] = new JsonObject { ["mode"] = mode },
        };
        if (username is not null)
        {
            json["username"] = username;
        }

        return ReportDiscovery.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), ReportServiceUrl.Parse(service)!, out var problem)
            ?? throw new Xunit.Sdk.XunitException("not parsed: " + problem);
    }

    [Fact]
    public void OfferedOnlyWhileUnansweredOffOnlineOnThePresetAndUnregistered()
    {
        Assert.True(ReportInvite.IsOffered(null, false, true, true, false));
        Assert.False(ReportInvite.IsOffered(ReportInviteAnswer.Accepted, false, true, true, false));
        Assert.False(ReportInvite.IsOffered(ReportInviteAnswer.Declined, false, true, true, false));
        Assert.False(ReportInvite.IsOffered(null, true, true, true, false));
        Assert.False(ReportInvite.IsOffered(null, false, false, true, false));
        Assert.False(ReportInvite.IsOffered(null, false, true, false, false));
        Assert.False(ReportInvite.IsOffered(null, false, true, true, true));
    }

    [Fact]
    public void TheInvitationCoversTheServiceHostAndItsSubdomains()
    {
        var service = ReportServiceUrl.Parse("https://example.com")!;
        Assert.Null(ReportInvite.Gap(Document("https://example.com", "https://example.com/agent-speed"), service, "zoe"));
        Assert.Null(ReportInvite.Gap(Document("https://example.com", "https://api.example.com"), service, "zoe"));
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(Document("https://example.com", "https://collect.example.org"), service, "zoe"));
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(Document("https://example.com", "https://notexample.com"), service, "zoe"));
        Assert.Equal(new ReportInviteGap.ApiKey(), ReportInvite.Gap(Document("https://example.com", "https://api.example.com", "required"), service, "zoe"));
        Assert.Null(ReportInvite.Gap(Document("https://example.com", "https://api.example.com", "optional"), service, "zoe"));
        var ascii = Document("https://example.com", "https://api.example.com", username: new JsonObject { ["charset"] = "ascii", ["spaces"] = false });
        Assert.Equal(new ReportInviteGap.Username(ReportUsernameProblem.Spaces), ReportInvite.Gap(ascii, service, "Zoe MBP"));
        Assert.Equal(new ReportInviteGap.Username(ReportUsernameProblem.Characters), ReportInvite.Gap(ascii, service, "小明的Mac"));
        Assert.Null(ReportInvite.Gap(ascii, service, "Zoes-MacBook"));
    }

    [Fact]
    public void ThePresetDomainCoversItsSiblingApiHost()
    {
        var deng = ReportServiceUrl.Parse("https://deng.codexradar.com")!;
        var document = Document(deng.Text, "https://speed-api.codexradar.com/api/agent-speed");
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(document, deng, "zoe"));
        Assert.Null(ReportInvite.Gap(document, deng, "zoe", "codexradar.com"));
        var other = ReportServiceUrl.Parse("https://example.com")!;
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(document, other, "zoe", "codexradar.com"));
        var foreign = Document(deng.Text, "https://speed-api.example.net");
        Assert.Equal(new ReportInviteGap.ApiHost(), ReportInvite.Gap(foreign, deng, "zoe", "codexradar.com"));
    }

    [Fact]
    public void SubdomainsOfAnIpAddressDoNotExist()
    {
        Assert.True(ReportInvite.Covers("127.0.0.1", "127.0.0.1"));
        Assert.False(ReportInvite.Covers("1.127.0.0.1", "127.0.0.1"));
        Assert.False(ReportInvite.Covers("x.[::1]", "[::1]"));
        Assert.True(ReportInvite.Covers("API.Example.com", "example.com"));
        Assert.False(ReportInvite.Covers("example.com", "api.example.com"));
        Assert.False(ReportInvite.Covers("api.example.com", string.Empty));
    }
}
