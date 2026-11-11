using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Quota.Routes;

/// <summary>Everything the vendor readers use from the outside world, replaceable in tests.</summary>
public sealed class QuotaVendorEnvironment
{
    /// <summary>The user's home folder (<c>%USERPROFILE%</c>).</summary>
    public required string Home { get; init; }

    /// <summary><c>%APPDATA%</c>, for Devin Desktop's folders.</summary>
    public required string AppData { get; init; }

    /// <summary>The app's environment (<c>KIMI_CODE_HOME</c>, <c>GEMINI_CLI_HOME</c>, <c>GROK_HOME</c>, <c>FACTORY_API_KEY</c>).</summary>
    public Func<string, string?> Variables { get; init; } = Environment.GetEnvironmentVariable;

    public required IQuotaHttpTransport Transport { get; init; }

    /// <summary><c>AgentSpeed/&lt;version&gt; (Windows)</c>.</summary>
    public required string UserAgent { get; init; }

    public Func<TimeZoneInfo> TimeZone { get; init; } = () => TimeZoneInfo.Local;

    /// <summary>The ZCode key from the secret store, read right before its request.</summary>
    public Func<string?> ZcodeKey { get; init; } = () => null;

    public IBrowserSessionReader? Browser { get; init; }

    public IAntigravityLocator? Antigravity { get; init; }

    public Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The log Grok Build writes; null takes <see cref="GrokLogReader.DefaultFile"/>.</summary>
    public string? GrokLogFile { get; init; }

    /// <summary>Devin Desktop's folders; null takes <see cref="DevinPlanReader.DefaultFolders"/>.</summary>
    public IReadOnlyList<string>? DevinFolders { get; init; }
}
