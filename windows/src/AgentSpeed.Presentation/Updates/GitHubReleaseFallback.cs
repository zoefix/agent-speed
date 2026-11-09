using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentSpeed.Presentation.Updates;

/// <summary>Release metadata from GitHub's public latest-release redirect and Atom feed, without the REST API quota.</summary>
public static partial class GitHubReleaseFallback
{
    public static ReleaseInfo Parse(Uri page, byte[] atom)
    {
        var prefix = UpdateSource.GitHubReleasesUrl + "/tag/";
        if (!page.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal)
            || page.Query.Length != 0 || page.Fragment.Length != 0)
        {
            throw Invalid();
        }

        var tag = Uri.UnescapeDataString(page.AbsoluteUri[prefix.Length..]);
        if (!SemanticVersion.TryParse(tag, out var version) || version.IsPreRelease || atom.Length > ReleaseFeed.MaxFeedBytes)
        {
            throw Invalid();
        }

        try
        {
            using var input = new MemoryStream(atom, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = ReleaseFeed.MaxFeedBytes,
            });
            XNamespace ns = "http://www.w3.org/2005/Atom";
            var document = XDocument.Load(reader);
            var entry = document.Root?.Elements(ns + "entry").Take(ReleaseFeed.MaxReleases).FirstOrDefault(e =>
                e.Elements(ns + "link").Any(l => (string?)l.Attribute("rel") == "alternate"
                    && (string?)l.Attribute("href") == page.AbsoluteUri));
            if (document.Root?.Name != ns + "feed" || entry is null)
            {
                throw Invalid();
            }

            var name = ((string?)entry.Element(ns + "title") ?? tag).Trim();
            // Keep headings and bullets in the existing safe Markdown renderer. All other HTML is reduced to text
            // there; it never opens links or executes markup. Decode entities only after tags have been removed.
            var notes = NotesTag().Replace((string?)entry.Element(ns + "content") ?? string.Empty, match =>
                match.Value.StartsWith("<li", StringComparison.OrdinalIgnoreCase) ? "\n- " : "\n## ");
            var published = DateTimeOffset.TryParse((string?)entry.Element(ns + "updated"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : (DateTimeOffset?)null;
            return new ReleaseInfo(tag, version, name[..Math.Min(name.Length, ReleaseFeed.MaxNameLength)],
                notes[..Math.Min(notes.Length, ReleaseFeed.MaxNotesLength)], page, published, []);
        }
        catch (XmlException ex)
        {
            throw new UpdateException(UpdateFailure.Invalid, "The public release feed is not valid Atom.", ex);
        }
    }

    private static UpdateException Invalid() => new(UpdateFailure.Invalid, "The public feed does not identify the latest stable release.");

    [GeneratedRegex(@"<(?:li|h[1-6])(?:\s[^>]*)?>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex NotesTag();
}
