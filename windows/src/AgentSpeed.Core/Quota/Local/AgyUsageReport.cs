using System.Collections;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>What one usage report brought back: its windows (null for a failure) and when agy ended (epoch ms).</summary>
public sealed record AntigravityReport(IReadOnlyList<QuotaWindow>? Windows, long EndedMs);

/// <summary>The <c>agy</c> CLI's own usage report (QUOTAS 9.4 <i>CLI usage report</i>); agy asks Google with its own sign-in.</summary>
public interface IAntigravityReport
{
    /// <summary>An agy binary is there to run (QUOTAS 9.4 <i>Binary</i>); without one the report is never tried.</summary>
    bool CanRun();

    /// <summary>One report; every failure (no binary, an old version, a time limit, bad output) comes back without windows.</summary>
    Task<AntigravityReport> RunAsync(Func<long> now, CancellationToken cancellationToken = default);
}

/// <summary>Time limits and output caps of the version check and the report (QUOTAS 9.4).</summary>
internal sealed record AgyLimits(int VersionTimeoutMs = 3_000, int VersionCapBytes = 4 << 10, int ReportTimeoutMs = 100_000, int ReportCapBytes = 1 << 20);

/// <summary>
/// Runs <c>agy -p /usage --output-format json --print-timeout 90s</c> on Windows, only for agy 1.1.11 or later (its
/// version asked once per binary identity), in a new private folder that also holds the <c>open.cmd</c> and
/// <c>xdg-open.cmd</c> stubs (<c>@exit /b 1</c>, first on <c>PATH</c>, <c>BROWSER</c> naming the first; unverified: how
/// agy opens a browser on Windows), with auto update off, inside a job object (<see cref="JobProcess"/>). agy's sign-in
/// (its token file, the credential store) is never read. One report at a time: a second one meanwhile fails at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgyUsageReport : IAntigravityReport
{
    public static readonly IReadOnlyList<string> Arguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "90s"];

    private const string Stub = "@exit /b 1\r\n";
    private static readonly string[] StubNames = ["open.cmd", "xdg-open.cmd"];

    private readonly string _home;
    private readonly Func<string, string?> _variables;
    private readonly Func<IReadOnlyDictionary<string, string>> _environment;
    private readonly AgyLimits _limits;
    private int _running;
    private (AgyIdentity Binary, (int Major, int Minor, int Patch) Version)? _version;

    /// <param name="home">The user's home folder (<c>%USERPROFILE%</c>).</param>
    public AgyUsageReport(string home)
        : this(home, Environment.GetEnvironmentVariable, AppEnvironment, new AgyLimits())
    {
    }

    /// <param name="variables">The app's <c>PATH</c>, where agy is looked for.</param>
    /// <param name="environment">The app's environment, which agy inherits with its additions.</param>
    internal AgyUsageReport(string home, Func<string, string?> variables, Func<IReadOnlyDictionary<string, string>> environment, AgyLimits limits)
    {
        _home = home;
        _variables = variables;
        _environment = environment;
        _limits = limits;
    }

    /// <summary>How many times <c>--version</c> ran (tests).</summary>
    internal int VersionRuns { get; private set; }

    public bool CanRun() => AgyBinary.Find(_home, _variables("PATH")) is not null;

    public async Task<AntigravityReport> RunAsync(Func<long> now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return new AntigravityReport(null, now());
        }

        try
        {
            return await Task.Run(() => ReportAsync(now, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<AntigravityReport> ReportAsync(Func<long> now, CancellationToken cancellationToken)
    {
        if (AgyBinary.Find(_home, _variables("PATH")) is not { } found || CreatePrivateFolder() is not { } folder)
        {
            return new AntigravityReport(null, now());
        }

        var (binary, identity) = found;

        try
        {
            var environment = ChildEnvironment(folder);
            var version = await VersionAsync(binary, identity, environment, folder, now, cancellationToken).ConfigureAwait(false);
            if (version is not { } known || known.CompareTo(AntigravityCliReport.Minimum) < 0)
            {
                return new AntigravityReport(null, now());
            }

            var run = await JobProcess.RunAsync(binary, Arguments, environment, folder, _limits.ReportTimeoutMs, _limits.ReportCapBytes, now, cancellationToken)
                .ConfigureAwait(false);
            return new AntigravityReport(run.End == JobProcessEnd.Exited ? AntigravityCliReport.Windows(run.Output) : null, run.EndedMs);
        }
        finally
        {
            await RemoveAsync(folder).ConfigureAwait(false);
        }
    }

    /// <summary>The cached version while the binary's identity holds; a version that does not read is asked again next time.</summary>
    private async Task<(int Major, int Minor, int Patch)?> VersionAsync(string binary, AgyIdentity identity, IReadOnlyDictionary<string, string> environment,
        string folder, Func<long> now, CancellationToken cancellationToken)
    {
        if (_version is { } cached && cached.Binary == identity)
        {
            return cached.Version;
        }

        VersionRuns++;
        var run = await JobProcess.RunAsync(binary, ["--version"], environment, folder, _limits.VersionTimeoutMs, _limits.VersionCapBytes, now, cancellationToken)
            .ConfigureAwait(false);
        if (run.End != JobProcessEnd.Exited || AntigravityCliReport.Version(Encoding.UTF8.GetString(run.Output)) is not { } version)
        {
            return null;
        }

        _version = (identity, version);
        return version;
    }

    /// <summary>The app's environment with auto update off and the browser stubs first on <c>PATH</c>.</summary>
    private Dictionary<string, string> ChildEnvironment(string folder)
    {
        var environment = new Dictionary<string, string>(_environment(), StringComparer.OrdinalIgnoreCase)
        {
            ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true",
            ["BROWSER"] = Path.Combine(folder, StubNames[0]),
        };
        environment["PATH"] = environment.TryGetValue("PATH", out var path) && path.Length > 0 ? folder + ";" + path : folder;
        return environment;
    }

    private static IReadOnlyDictionary<string, string> AppEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string name, Value: string value })
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>A new folder in the temporary folder that only this user can open (0700), holding the stubs.</summary>
    private static string? CreatePrivateFolder()
    {
        try
        {
            string? user;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                user = identity.User?.Value;
            }

            if (user is null)
            {
                return null;
            }

            var folder = Path.Combine(Path.GetTempPath(), "agentspeed-agy-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
            if (Path.Exists(folder))
            {
                return null;
            }

            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm($"O:{user}D:P(A;OICI;FA;;;{user})");
            new DirectoryInfo(folder).Create(security);
            try
            {
                foreach (var name in StubNames)
                {
                    using var stub = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stub.Write(Encoding.ASCII.GetBytes(Stub));
                }
            }
            catch
            {
                Directory.Delete(folder, recursive: true);
                throw;
            }

            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task RemoveAsync(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
