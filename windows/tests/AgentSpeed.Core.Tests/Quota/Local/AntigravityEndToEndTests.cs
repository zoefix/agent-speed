using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Tests.Quota.Cookies;
using AgentSpeed.Core.Tests.Quota.Vendors;

namespace AgentSpeed.Core.Tests.Quota.Local;

/// <summary>
/// A stand-in language server (a copy of PowerShell named <c>language_server_windows_x64.exe</c> with Antigravity's
/// arguments, answering on a loopback port): the Windows locator finds it by its arguments and listening port, and the
/// vendor service asks it with its CSRF token over real HTTP (QUOTAS 9.4).
/// </summary>
public sealed class AntigravityEndToEndTests : IDisposable
{
    private const string Script = """
        param($PortFile, $BodyFile)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        Set-Content -Path $PortFile -Value $listener.LocalEndpoint.Port -Encoding ASCII
        $body = [IO.File]::ReadAllBytes($BodyFile)
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
            $client = $listener.AcceptTcpClient()
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
            $token = ''; $length = 0
            while (($line = $reader.ReadLine()) -ne '') {
                if ($line -match '^X-Codeium-Csrf-Token:\s*(.+)$') { $token = $Matches[1] }
                if ($line -match '^Content-Length:\s*(\d+)$') { $length = [int]$Matches[1] }
            }
            $buffer = New-Object char[] $length
            if ($length -gt 0) { [void]$reader.ReadBlock($buffer, 0, $length) }
            if ($token -eq 'tok-123') { $status = '200 OK'; $payload = $body } else { $status = '401 X'; $payload = [byte[]]@() }
            $head = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: $($payload.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($head, 0, $head.Length); $stream.Write($payload, 0, $payload.Length); $stream.Flush()
            $client.Close()
        }
        """;

    // The stand-in's first answer can take seconds on a busy runner (PowerShell compiles the loop), longer than the
    // 2 s loopback timeout under test, so one request primes it.
    private static async Task WarmUp(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/warm") { Content = new StringContent("{}") };
        request.Headers.Add("X-Codeium-Csrf-Token", "tok-123");
        using var response = await client.SendAsync(request);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentspeed-antigravity-" + Guid.NewGuid().ToString("N"));
    private Process? _server;

    public AntigravityEndToEndTests() => Directory.CreateDirectory(_root);

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task TheLocatorFindsTheServerAndTheServiceAsksItWithItsToken()
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var exe = Path.Combine(_root, "language_server_windows_x64.exe");
        File.Copy(powershell, exe);
        File.WriteAllText(Path.Combine(_root, "server.ps1"), Script);
        File.WriteAllText(Path.Combine(_root, "body.json"), QuotaVendorFixtureTestsBody());
        var portFile = Path.Combine(_root, "port.txt");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_root, "server.ps1"), portFile,
                     Path.Combine(_root, "body.json"), "--app_data_dir", Path.Combine(_root, ".gemini", "antigravity"), "--csrf_token", "tok-123" })
        {
            start.ArgumentList.Add(argument);
        }

        _server = Process.Start(start)!;
        var watch = Stopwatch.StartNew();
        while (!File.Exists(portFile) || new FileInfo(portFile).Length == 0)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the stand-in server did not start");
            await Task.Delay(100);
        }

        var port = int.Parse(File.ReadAllText(portFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WarmUp(port);
        using var locator = new AntigravityProcessLocator(_root);
        var server = Assert.Single(locator.Servers(), s => s.Pid == _server.Id);
        Assert.Contains(server.Endpoints, e => e.Port == port && e.Scheme == "http" && e.Token == "tok-123");

        using var transport = new QuotaHttpTransport();
        var service = new QuotaVendorService(new QuotaVendorEnvironment
        {
            Home = _root,
            AppData = _root,
            Variables = _ => null,
            Transport = transport,
            UserAgent = "AgentSpeed/9.9.9 (Windows)",
            Antigravity = new OnlyPid(locator, _server.Id),
        });
        var readings = await service.RefreshAsync(new QuotaVendorRound(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true,
            new HashSet<QuotaRoute> { QuotaRoute.Antigravity }, new HashSet<SourceKind> { SourceKind.Antigravity }, false));
        Assert.Equal(QuotaStatusKind.Ok, readings.Status(QuotaRoute.Antigravity)?.Kind);
        Assert.True(readings.SignedIn(SourceKind.Antigravity));
        Assert.True(readings.AntigravityRuns);
        QuotaVendorFixtureTests.AssertWindows(Routes.QuotaVendorHarness.Vendor("antigravity").GetProperty("expected"),
            readings.Snapshot(SourceKind.Antigravity)!.Windows);
    }

    private static string QuotaVendorFixtureTestsBody() => Routes.QuotaVendorHarness.Body("antigravity");

    /// <summary>The real locator, keeping only the stand-in (any other language server of this session is left alone).</summary>
    private sealed class OnlyPid(IAntigravityLocator inner, int pid) : IAntigravityLocator
    {
        public IReadOnlyList<AntigravityServer> Servers() => [.. inner.Servers().Where(s => s.Pid == pid)];
    }

    public void Dispose()
    {
        try
        {
            if (_server is { HasExited: false })
            {
                _server.Kill();
                _server.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _server?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
