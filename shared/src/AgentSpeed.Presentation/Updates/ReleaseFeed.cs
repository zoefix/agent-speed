using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentSpeed.Presentation.Updates;

/// <summary>
/// Parses the GitHub "latest release" response (an object), or a release list (an array, newest offerable wins).
/// Malformed JSON throws; a release that must never be offered (draft, pre-release, unparseable tag) yields null.
/// </summary>
public static class ReleaseFeed
{
    public const int MaxFeedBytes = 2 * 1024 * 1024;
    public const int MaxReleases = 100;
    public const int MaxAssets = 200;
    public const int MaxNotesLength = 50_000;
    public const int MaxNameLength = 200;
    public const int MaxAssetNameLength = 255;
    public const int MaxUrlLength = 4096;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    /// <exception cref="UpdateException">The feed is too large or is not a release object or array.</exception>
    public static ReleaseInfo? Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxFeedBytes)
        {
            throw new UpdateException(UpdateFailure.Invalid, "The release feed is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), Options);
            return Select(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new UpdateException(UpdateFailure.Invalid, "The release feed is not valid JSON.", ex);
        }
    }

    public static ReleaseInfo? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    private static ReleaseInfo? Select(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                return ParseRelease(root) is { IsOfferable: true } release ? release : null;
            case JsonValueKind.Array:
                ReleaseInfo? best = null;
                var count = 0;
                foreach (var element in root.EnumerateArray())
                {
                    if (++count > MaxReleases)
                    {
                        break;
                    }

                    if (ParseRelease(element) is { IsOfferable: true } candidate && (best is null || candidate.Version > best.Version))
                    {
                        best = candidate;
                    }
                }

                return best;
            default:
                throw new UpdateException(UpdateFailure.Invalid, "The release feed must be an object or an array.");
        }
    }

    private static ReleaseInfo? ParseRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = String(element, "tag_name");
        if (tag is null || !SemanticVersion.TryParse(tag, out var version))
        {
            return null;
        }

        tag = tag.Trim();
        var name = Truncate(String(element, "name"), MaxNameLength);
        var notes = Truncate(String(element, "body"), MaxNotesLength) ?? string.Empty;
        var published = String(element, "published_at") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUniversalTime()
                : (DateTimeOffset?)null;

        return new ReleaseInfo(
            tag,
            version,
            string.IsNullOrWhiteSpace(name) ? tag : name.Trim(),
            notes,
            WebUrl(String(element, "html_url")),
            published,
            Assets(element),
            Bool(element, "draft"),
            Bool(element, "prerelease"));
    }

    private static ImmutableArray<ReleaseAsset> Assets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<ReleaseAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (builder.Count >= MaxAssets)
            {
                break;
            }

            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = String(asset, "name");
            var url = WebUrl(String(asset, "browser_download_url"));
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxAssetNameLength || url is null
                || !asset.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
                || !sizeElement.TryGetInt64(out var size) || size < 0)
            {
                continue;
            }

            builder.Add(new ReleaseAsset(name, url, size));
        }

        return builder.ToImmutable();
    }

    /// <summary>Absolute http(s) URL without credentials; whether its host may be used is decided by <see cref="UpdateSource"/>.</summary>
    internal static Uri? WebUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxUrlLength
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri;
    }

    private static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Truncate(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut];
    }
}
