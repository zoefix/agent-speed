using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Tests.Quota.Network;

namespace AgentSpeed.Core.Tests.Quota.Routes;

/// <summary>A browser that holds one session per route's first site, or none; counts how often it is read.</summary>
internal sealed class FakeBrowser : IBrowserSessionReader
{
    public Dictionary<string, BrowserSession> Sessions { get; } = new(StringComparer.Ordinal);

    public BrowserSessionMiss Miss { get; set; } = BrowserSessionMiss.NoSession;

    public int Reads { get; private set; }

    public int Resets { get; private set; }

    public BrowserSession? Session(IReadOnlyList<string> sites, long nowMs, out BrowserSessionMiss miss)
    {
        Reads++;
        foreach (var site in sites)
        {
            if (Sessions.TryGetValue(site, out var session))
            {
                miss = BrowserSessionMiss.None;
                return session;
            }
        }

        miss = Miss;
        return null;
    }

    public void ResetRefusals() => Resets++;
}

internal sealed class FakeLocator : IAntigravityLocator
{
    public List<AntigravityServer> Found { get; } = [];

    public int Calls { get; private set; }

    public IReadOnlyList<AntigravityServer> Servers()
    {
        Calls++;
        return [.. Found];
    }
}

/// <summary>A temporary home with each agent's sign-in, a clock, and every outside seam of the vendor service counted.</summary>
internal sealed class QuotaVendorHarness : IDisposable
{
    public const long Start = 1794338773000;

    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-cases.json"))).RootElement.Clone());

    private static int _stamps;

    public QuotaVendorHarness(IQuotaHttpTransport? transport = null)
    {
        Home = Path.Combine(Path.GetTempPath(), "agentspeed-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Home);
        Transport = transport ?? Scripted;
        WriteKimi("kimi-tok", (Start / 1000) + 7200);
        WriteGemini("gem-tok", Start + 7_200_000);
        WriteGrok("grok-key", "2026-11-11T00:00:00Z");
        Write(".factory/.env", "OTHER=1\nFACTORY_API_KEY=\"fk-1\"\n");
        Service = new QuotaVendorService(new QuotaVendorEnvironment
        {
            Home = Home,
            AppData = Path.Combine(Home, "AppData"),
            Variables = name =>
            {
                VariableReads++;
                return Variables.GetValueOrDefault(name);
            },
            Transport = Transport,
            UserAgent = "AgentSpeed/9.9.9 (Windows)",
            TimeZone = () => TimeZoneInfo.Utc,
            ZcodeKey = Keys.Read,
            Browser = Browser,
            Antigravity = Locator,
            Now = () => Now,
        });
    }

    public string Home { get; }

    public long Now { get; set; } = Start;

    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    public int VariableReads { get; private set; }

    public ScriptedQuotaTransport Scripted { get; } = new();

    public IQuotaHttpTransport Transport { get; }

    public MemoryZcodeKeyStore Keys { get; } = new("zk-1");

    public FakeBrowser Browser { get; } = new();

    public FakeLocator Locator { get; } = new();

    public QuotaVendorService Service { get; }

    public static JsonElement Vendor(string name) => Cases.Value.GetProperty("vendors").GetProperty(name);

    public static string Body(string name) => Vendor(name).GetProperty("body").GetRawText();

    public void Write(string relative, string text)
    {
        var path = Path.Combine(Home, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref _stamps)));
    }

    public void WriteKimi(string token, long expiresAtS) =>
        Write(".kimi-code/credentials/kimi-code.json", $"{{\"access_token\":\"{token}\",\"refresh_token\":\"never-read\",\"expires_at\":{expiresAtS}}}");

    public void WriteGemini(string token, long expiryMs) =>
        Write(".gemini/oauth_creds.json", $"{{\"refresh_token\":\"never-read\",\"access_token\":\"{token}\",\"expiry_date\":{expiryMs}}}");

    public void WriteGrok(string key, string expires) =>
        Write(".grok/auth.json", $"{{\"https://auth.x.ai\":{{\"key\":\"{key}\",\"expires_at\":\"{expires}\"}}}}");

    public static IReadOnlySet<QuotaRoute> On(params QuotaRoute[] routes) => routes.ToHashSet();

    public static IReadOnlySet<QuotaRoute> AllRoutes => QuotaRoutes.All.ToHashSet();

    public static IReadOnlySet<SourceKind> Everything => Enum.GetValues<SourceKind>().ToHashSet();

    public Task<QuotaVendorReadings> RoundAsync(IReadOnlySet<QuotaRoute> routes, IReadOnlySet<SourceKind>? running = null, bool network = true,
        bool dashboard = false, ZaiRegion region = ZaiRegion.Global, bool local = false) =>
        Service.RefreshAsync(new QuotaVendorRound(Now, network, routes, running ?? Everything, dashboard, region, local));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
