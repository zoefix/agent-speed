using System.Globalization;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>Why a <c>destination</c> or <c>relayFolder</c> is refused (REMOTE 2.1). The editor shows it under the field.</summary>
public enum RemoteFieldIssue
{
    Empty,
    TooLong,
    LeadingDash,

    /// <summary>A byte outside the allowed set (shell metacharacters, spaces, quotes, non-ASCII, control characters).</summary>
    ForbiddenCharacter,

    /// <summary><c>relayFolder</c> only: neither <c>~/…</c> nor absolute.</summary>
    NotAbsolute,

    /// <summary><c>relayFolder</c> only: a <c>..</c> component or <c>//</c>.</summary>
    UnsafeComponent,
}

/// <summary>
/// The rules of REMOTE 2.1. Every value that reaches an <c>ssh</c> argument vector or a remote command passed these, so a
/// remote command can insert it unquoted.
/// </summary>
public static class RemoteHostValidation
{
    public const int DestinationLimit = 255;
    public const int FolderLimit = 200;
    public const int NameLimit = 32;

    private const string DestinationForbidden = "\"'`\\;&|$<>()*?!{}#";

    public static RemoteFieldIssue? DestinationIssue(string? destination)
    {
        if (string.IsNullOrEmpty(destination))
        {
            return RemoteFieldIssue.Empty;
        }

        if (Encoding.UTF8.GetByteCount(destination) > DestinationLimit)
        {
            return RemoteFieldIssue.TooLong;
        }

        foreach (var c in destination)
        {
            if (c < 0x21 || c > 0x7E || DestinationForbidden.Contains(c, StringComparison.Ordinal))
            {
                return RemoteFieldIssue.ForbiddenCharacter;
            }
        }

        return destination[0] == '-' ? RemoteFieldIssue.LeadingDash : null;
    }

    public static RemoteFieldIssue? FolderIssue(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return RemoteFieldIssue.Empty;
        }

        if (Encoding.UTF8.GetByteCount(folder) > FolderLimit)
        {
            return RemoteFieldIssue.TooLong;
        }

        if (!folder.All(IsFolderChar))
        {
            return RemoteFieldIssue.ForbiddenCharacter;
        }

        if (!folder.StartsWith('/') && !folder.StartsWith("~/", StringComparison.Ordinal))
        {
            return RemoteFieldIssue.NotAbsolute;
        }

        return folder.Contains("//", StringComparison.Ordinal) || folder.Split('/').Contains("..")
            ? RemoteFieldIssue.UnsafeComponent
            : null;
    }

    /// <summary><c>[A-Za-z0-9._/~-]</c>.</summary>
    internal static bool IsFolderChar(char c) => IsNameChar(c) || c is '/' or '~';

    /// <summary><c>[A-Za-z0-9._-]</c>: relay file names (REMOTE 4.1).</summary>
    internal static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    /// <summary>A valid folder without its trailing slash (<c>~/</c> stays <c>~</c>), so <c>&lt;folder&gt;/&lt;file&gt;</c> never doubles a slash.</summary>
    internal static string NormalizedFolder(string folder)
    {
        var end = folder.Length;
        while (end > 1 && folder[end - 1] == '/')
        {
            end--;
        }

        return folder[..end];
    }

    /// <summary>An upper-case UUID string (as <see cref="RemoteHost.NewId"/> makes them), never <c>local</c>.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 36 } && id != RemoteHost.LocalId && Guid.TryParseExact(id, "D", out var guid)
        && string.Equals(guid.ToString("D").ToUpperInvariant(), id, StringComparison.Ordinal);

    /// <summary>Trimmed, control characters removed, at most 32 characters; empty means automatic (null).</summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var visible = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (!IsControl(rune.Value))
            {
                visible.Append(rune.ToString());
            }
        }

        var trimmed = visible.ToString().Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var info = new StringInfo(trimmed);
        return info.LengthInTextElements <= NameLimit ? trimmed : info.SubstringByTextElements(0, NameLimit).Trim();
    }

    /// <summary>
    /// The host part of a destination: <c>ssh://</c> removed, then everything up to the last <c>@</c>, then a port
    /// (<c>:&lt;digits&gt;</c> of the <c>ssh://</c> form, brackets of an IPv6 literal). Falls back to the destination when
    /// nothing is left.
    /// </summary>
    public static string AutomaticName(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var rest = destination.AsSpan();
        var isUrl = destination.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        if (isUrl)
        {
            rest = rest[6..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                rest = rest[..slash];
            }
        }

        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            rest = rest[(at + 1)..];
        }

        var close = rest.IndexOf(']');
        if (rest.StartsWith("[") && close >= 0)
        {
            rest = rest[1..close];
        }
        else if (isUrl && rest.LastIndexOf(':') is var colon and >= 0 && IsAsciiDigits(rest[(colon + 1)..]))
        {
            rest = rest[..colon];
        }

        var name = Prefix(rest.ToString());
        return name.Length == 0 ? Prefix(destination) : name;
    }

    internal static bool IsControl(int scalar) => scalar < 0x20 || scalar is >= 0x7F and <= 0x9F;

    private static bool IsAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Prefix(string text) => text.Length <= NameLimit ? text : text[..NameLimit];
}
