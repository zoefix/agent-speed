using System.Collections.Immutable;

namespace AgentSpeed.Presentation.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>The fields of a GitHub release that updates use (UPDATES section 1).</summary>
public sealed record ReleaseInfo(
    string Tag,
    SemanticVersion Version,
    string Name,
    string Notes,
    Uri? PageUrl,
    DateTimeOffset? PublishedAt,
    ImmutableArray<ReleaseAsset> Assets,
    bool IsDraft = false,
    bool IsPreRelease = false)
{
    /// <summary>Drafts and pre-releases (by GitHub flag or by a SemVer pre-release tag) are never offered.</summary>
    public bool IsOfferable => !IsDraft && !IsPreRelease && !Version.IsPreRelease;

    public string DisplayVersion => Version.ToString();

    public ReleaseAsset? FindAsset(string name)
    {
        foreach (var asset in Assets)
        {
            if (string.Equals(asset.Name, name, StringComparison.Ordinal))
            {
                return asset;
            }
        }

        return null;
    }
}
