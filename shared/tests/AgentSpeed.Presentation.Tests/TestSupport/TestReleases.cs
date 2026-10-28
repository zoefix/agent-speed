using System.Collections.Immutable;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests.TestSupport;

/// <summary>Builds release models the way the GitHub feed would describe them.</summary>
internal static class TestReleases
{
    public const string Repository = "https://github.com/zoefix/agent-speed";

    public static SemanticVersion Version(string text) =>
        SemanticVersion.ParseOrNull(text) ?? throw new ArgumentException($"'{text}' is not a version.", nameof(text));

    public static ReleaseAsset Asset(string name, long size = 1024, string? url = null) =>
        new(name, new Uri(url ?? $"{Repository}/releases/download/v1.2.0/{name}", UriKind.Absolute), size);

    /// <summary>The assets every release publishes (UPDATES section 1).</summary>
    public static ImmutableArray<ReleaseAsset> StandardAssets() =>
    [
        Asset("AgentSpeed-macOS.zip", 40_000_000),
        Asset("AgentSpeed-Windows-x64.zip", 70_000_000),
        Asset("AgentSpeed-Windows-arm64.zip", 68_000_000),
        Asset("SHA256SUMS.txt", 300),
    ];

    public static ReleaseInfo Release(
        string tag = "v1.2.0",
        IEnumerable<ReleaseAsset>? assets = null,
        bool isDraft = false,
        bool isPreRelease = false,
        string? pageUrl = null) =>
        new(
            tag,
            Version(tag),
            $"Agent Speed {tag}",
            "Release notes",
            new Uri(pageUrl ?? $"{Repository}/releases/tag/{tag}", UriKind.Absolute),
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            assets is null ? StandardAssets() : [.. assets],
            isDraft,
            isPreRelease);
}
