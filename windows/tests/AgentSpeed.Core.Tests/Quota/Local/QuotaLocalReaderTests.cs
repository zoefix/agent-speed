using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using System.Runtime.Versioning;
using AgentSpeed.Core.Quota.Vendors;
using AgentSpeed.Core.Tests.Quota.Cookies;

namespace AgentSpeed.Core.Tests.Quota.Local;

/// <summary>The local routes (QUOTAS 9.2 log, 9.3) and the Antigravity rules (9.4) on Windows.</summary>
public sealed class QuotaLocalReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentspeed-quota-local-" + Guid.NewGuid().ToString("N"));

    public QuotaLocalReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheGrokLogsTailDropsItsCutLineAndKeepsTheNewestConfig()
    {
        var file = Path.Combine(_root, "logs", "unified.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var config = """{"ts":"2026-11-10T12:00:00Z","msg":"billing: fetched credits config","ctx":{"config":{"creditUsagePercent":40,"currentPeriod":{"type":"PERIOD_TYPE_MONTHLY","start":"2026-11-01T00:00:00Z","end":"2026-12-01T00:00:00Z"}}}}""";
        var filler = new StringBuilder();
        while (filler.Length < GrokLogReader.TailBytes + 1000)
        {
            filler.Append("""{"ts":"2026-11-10T09:00:00Z","msg":"noise"}""").Append('\n');
        }

        File.WriteAllText(file, filler + config + "\n");
        var reader = new GrokLogReader(file);
        Assert.True(reader.Refresh());
        Assert.Equal(1794312000000, reader.Snapshot!.ObservedMs);
        var window = Assert.Single(reader.Snapshot.Windows);
        Assert.Equal(QuotaWindowKind.Month, window.Kind);
        Assert.Equal(43200, window.Minutes);
        Assert.Equal(40, window.UsedPercent);
        Assert.False(reader.Refresh());

        var lines = GrokLogReader.TailLines(file)!;
        Assert.DoesNotContain(lines, l => !Encoding.UTF8.GetString(l.Span).StartsWith('{'));
        Assert.Equal("{\"ts\":\"2026-11-10T09:00:00Z\",\"msg\":\"noise\"}".Length, lines[0].Length);
    }

    [Fact]
    public void TheGrokLogFollowsGrokHome()
    {
        Assert.Equal(Path.Combine("D:\\grok", "logs", "unified.jsonl"), GrokLogReader.DefaultFile("C:\\Users\\u", n => n == "GROK_HOME" ? "D:\\grok" : null));
        Assert.Equal(Path.Combine("C:\\Users\\u", ".grok", "logs", "unified.jsonl"), GrokLogReader.DefaultFile("C:\\Users\\u", _ => null));
    }

    [Fact]
    public void DevinsPlanIsReadFromTheNewestFolderAndTimedByItsLaunch()
    {
        var devin = Path.Combine(_root, "Devin");
        var database = DevinPlanReader.Database(devin);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = SqliteConnection.Open(database, SqliteOpenMode.ReadWriteCreate))
        {
            connection.Execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)");
            using var insert = connection.Prepare("INSERT INTO ItemTable(key, value) VALUES (?, ?)");
            insert.Bind(1, "windsurf.reactSettings.cachedPlanInfoData:a")
                .Bind(2, """{"endTimestamp":1800000000,"dailyRemainingPercent":75,"dailyResetAtUnix":1794400000,"weeklyRemainingPercent":40,"weeklyResetAtUnix":1794800000,"accountIdentityText":"someone@example.com"}""")
                .Run();
        }

        Directory.CreateDirectory(Path.Combine(devin, "logs", "20261110T081500"));
        Directory.CreateDirectory(Path.Combine(devin, "logs", "20261109T230000"));
        Directory.CreateDirectory(Path.Combine(devin, "logs", "not-a-launch"));
        var reader = new DevinPlanReader([devin, Path.Combine(_root, "Windsurf")]);
        Assert.True(reader.Refresh(TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), reader.Snapshot!.ObservedMs);
        Assert.Equal([QuotaWindowKind.Other, QuotaWindowKind.SevenDay], reader.Snapshot.Windows.Select(w => w.Kind));
        Assert.Equal([25.0, 60.0], reader.Snapshot.Windows.Select(w => w.UsedPercent));
        Assert.DoesNotContain(reader.Snapshot.Windows, w => w.Name is not null);
    }

    [Fact]
    public void LaunchFoldersReadAsLocalTime()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone("Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 0, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), DevinPlan.LaunchMs("20261110T081500", taipei));
        Assert.Null(DevinPlan.LaunchMs("20261310T081500", taipei));
        Assert.Null(DevinPlan.LaunchMs("2026-11-10", taipei));
    }

    [Fact]
    public void LanguageServersAreRecognizedByTheirArgumentsOrFolder()
    {
        string[] args = ["--app_data_dir", "C:\\Users\\u\\.gemini\\antigravity", "--csrf_token", "c1", "--extension_server_port", "51000"];
        Assert.True(AntigravityRules.IsLanguageServer("C:\\x\\language_server_windows_x64.exe", args));
        Assert.False(AntigravityRules.IsLanguageServer("C:\\x\\language_server_windows_x64.exe", ["--csrf_token", "c1"]));
        Assert.True(AntigravityRules.IsLanguageServer("C:\\Users\\u\\AppData\\Local\\Programs\\Antigravity\\resources\\bin\\language_server_windows_x64.exe", []));
        Assert.False(AntigravityRules.IsLanguageServer("C:\\x\\node.exe", args));

        var server = AntigravityRules.LanguageServer(9, args, [51001, 51000]);
        Assert.Equal(["http:51000", "http:51001", "https:51001", "https:51000"], server.Endpoints.Select(e => e.ToString()));
        Assert.All(server.Endpoints, e => Assert.Equal("c1", e.Token));
        var own = AntigravityRules.LanguageServer(9, [.. args, "--extension_server_csrf_token=ext"], []);
        Assert.Equal("ext", Assert.Single(own.Endpoints).Token);
        Assert.DoesNotContain("c1", server.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DaemonFilesNameTheirServer()
    {
        var server = AntigravityRules.Daemon("""{"pid":1234,"csrfToken":"t","httpsPort":51002,"httpPort":51003}"""u8.ToArray())!;
        Assert.Equal(1234, server.Pid);
        Assert.Equal(["http:51003", "https:51002"], server.Endpoints.Select(e => e.ToString()));
        Assert.Null(AntigravityRules.Daemon("""{"pid":0,"httpPort":1}"""u8.ToArray()));
        Assert.Null(AntigravityRules.Daemon("""{"pid":5}"""u8.ToArray()));

        var folder = Path.Combine(_root, ".gemini", "antigravity", "daemon");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ls_1.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "other.json"), "{}");
        Assert.Equal([Path.Combine(folder, "ls_1.json")], AntigravityRules.DaemonFiles(_root));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void ListeningLoopbackPortsComeFromTheTcpTable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(port, TcpListeners.Of(Environment.ProcessId));
        Assert.DoesNotContain(port, TcpListeners.Of(4));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void ThisSessionHasNoAntigravityServer()
    {
        using var locator = new AntigravityProcessLocator(_root);
        Assert.Empty(locator.Servers());
    }

    [Fact]
    public void SignInFilesGiveOnlyTheirToken()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".kimi", "credentials"));
        File.WriteAllText(Path.Combine(home, ".kimi", "credentials", "kimi-code.json"), """{"refresh_token":"r","access_token":"legacy","expires_at":1794348000000}""");
        var kimi = QuotaCredentials.Kimi(home, _ => null)!;
        Assert.Equal("legacy", kimi.Value);
        Assert.Equal(1794348000000, kimi.ExpiresAtMs);
        Assert.Equal("QuotaToken", kimi.ToString());

        Directory.CreateDirectory(Path.Combine(home, ".kimi-code", "credentials"));
        File.WriteAllText(Path.Combine(home, ".kimi-code", "credentials", "kimi-code.json"), """{"access_token":"current","expires_at":1794348000}""");
        Assert.Equal(("current", 1794348000000L), (QuotaCredentials.Kimi(home, _ => null)!.Value, QuotaCredentials.Kimi(home, _ => null)!.ExpiresAtMs!.Value));

        var gemini = Path.Combine(_root, "gemini-home");
        Directory.CreateDirectory(Path.Combine(gemini, ".gemini"));
        File.WriteAllText(Path.Combine(gemini, ".gemini", "oauth_creds.json"), """{"access_token":"g","expiry_date":1794348000123.9}""");
        var token = QuotaCredentials.Gemini(home, n => n == "GEMINI_CLI_HOME" ? gemini : null)!;
        Assert.Equal(("g", 1794348000123L), (token.Value, token.ExpiresAtMs!.Value));

        Directory.CreateDirectory(Path.Combine(home, ".grok"));
        File.WriteAllText(Path.Combine(home, ".grok", "auth.json"), """{"https://a":{"access_token":"acc","expires_at":"2026-11-10T22:00:00Z"},"https://b":{"key":"other"}}""");
        var grok = QuotaCredentials.Grok(home, _ => null)!;
        Assert.Equal(("acc", 1794348000000L), (grok.Value, grok.ExpiresAtMs!.Value));
        File.WriteAllText(Path.Combine(home, ".grok", "auth.json"), """{"https://a":{"access_token":"acc","key":"k"}}""");
        Assert.Equal("k", QuotaCredentials.Grok(home, _ => null)!.Value);
        File.WriteAllText(Path.Combine(home, ".grok", "auth.json"), "[1]");
        Assert.Null(QuotaCredentials.Grok(home, _ => null));

        Assert.Null(QuotaCredentials.Droid(home, _ => null));
        Assert.Equal("fk", QuotaCredentials.Droid(home, n => n == "FACTORY_API_KEY" ? "fk" : null));
    }

    [Fact]
    public void ACredentialThatCouldBreakAHeaderIsNotUsed()
    {
        Assert.True(QuotaCredentials.IsHeaderSafe("abc-123_~"));
        Assert.False(QuotaCredentials.IsHeaderSafe("abc\r\nX-Evil: 1"));
        Assert.False(QuotaCredentials.IsHeaderSafe("ключ"));
        Assert.False(QuotaCredentials.IsHeaderSafe(""));
        Assert.Equal("zk-1", ZcodeKey.Cleaned("  zk-1 \n"));
        Assert.Null(ZcodeKey.Cleaned("two words"));
        Assert.Null(ZcodeKey.Cleaned("   "));
    }

    [Fact]
    public void FingerprintsAreTheFirstEightBytesOfTheSha256()
    {
        // SHA-256("abc") = ba7816bf8f01cfea…
        Assert.Equal(0xba7816bf8f01cfeaUL, QuotaFingerprint.Of("abc").Value);
        Assert.NotEqual(QuotaFingerprint.Of("a"), QuotaFingerprint.Of("b"));
    }

    [CredentialVaultFact]
    [SupportedOSPlatform("windows")]
    public void TheZcodeKeyLivesInCredentialManager()
    {
        var store = new CredentialManagerZcodeKeyStore("AgentSpeedTest:zcode-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(store.HasKey());
            Assert.Null(store.Read());
            Assert.True(store.Save("zk-test-1"), "CredWrite failed: " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            Assert.True(store.HasKey());
            Assert.Equal("zk-test-1", store.Read());
            Assert.True(store.Save("zk-test-2"));
            Assert.Equal("zk-test-2", store.Read());
        }
        finally
        {
            store.Remove();
        }

        Assert.False(store.HasKey());
    }
}

/// <summary>A fact that needs this logon session's Credential Manager vault (a key-based SSH logon has none: error 1312).</summary>
public sealed class CredentialVaultFactAttribute : FactAttribute
{
    public CredentialVaultFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows Credential Manager";
            return;
        }

        var probe = new CredentialManagerZcodeKeyStore("AgentSpeedTest:probe-" + Guid.NewGuid().ToString("N"));
        if (!probe.Save("probe"))
        {
            Skip = "Credential Manager is unavailable in this logon session (error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError() + ")";
            return;
        }

        probe.Remove();
    }
}
