namespace AgentSpeed.Presentation.Remote;

/// <summary>
/// <i>Copy SSH command</i> (DESIGN 13): <c>ssh &lt;destination&gt;</c> for the first-connection fingerprint check, written
/// for PowerShell (Windows Terminal's default shell). A destination already passed REMOTE 2.1 (no quotes, spaces, <c>$</c>,
/// <c>`</c>, <c>;</c>, …); one made only of plain characters stays bare, so the same line also works in cmd, and anything
/// else (a leading <c>@</c> would splat, <c>,</c> makes an array, <c>[</c> a wildcard, a leading <c>~</c> may expand) goes in
/// single quotes, which PowerShell takes literally and which a valid destination never contains.
/// </summary>
public static class SshCommandText
{
    public static string Command(string destination) => "ssh " + Arguments(destination);

    /// <summary>The destination as one PowerShell argument.</summary>
    public static string Arguments(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return IsPlain(destination) ? destination : "'" + destination.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static bool IsPlain(string text)
    {
        if (text.Length == 0 || text[0] is '@' or '~' or '-')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }
}
