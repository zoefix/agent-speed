namespace AgentSpeed.Core.Remote;

/// <summary>
/// The POSIX <c>sh</c> scripts of the remote commands (REMOTE 4.2, 4.3), the same strings as the macOS app. Each is one
/// line without <c>'</c>, <c>\</c>, <c>!</c> or a newline, so it survives single quotes in sh, bash, zsh, dash, fish and
/// tcsh. Arguments arrive as <c>$1</c> …; every use is quoted.
/// </summary>
internal static class RemoteScripts
{
    /// <summary>Sets <c>h</c> to the first hash tool found, or to empty.</summary>
    public static readonly string SelectHashTool = string.Join("; ",
        "if command -v sha256sum >/dev/null 2>&1; then h=sha256sum",
        "elif command -v shasum >/dev/null 2>&1; then h=\"shasum -a 256\"",
        "elif command -v openssl >/dev/null 2>&1; then h=\"openssl dgst -sha256 -r\"",
        "else h=; fi");

    /// <summary>
    /// <c>$1</c> = folder. Prints <c>system &lt;uname -s -m&gt;</c>, <c>tool &lt;command&gt;</c> or <c>tool none</c>, one
    /// <c>hash &lt;hex&gt; &lt;name&gt;</c> per readable <c>agent-speed-relay-*</c> file, then <c>end</c>.
    /// </summary>
    public static readonly string Probe = string.Join("; ",
        "echo \"system $(uname -s -m)\"",
        SelectHashTool,
        "if [ -z \"$h\" ]; then echo \"tool none\"; echo end; exit 0; fi",
        "echo \"tool $h\"",
        "for f in \"$1\"/agent-speed-relay-*; do if [ -f \"$f\" ]; then "
            + "x=$($h < \"$f\") && echo \"hash ${x%% *} ${f##*/}\"; fi; done",
        "echo end");

    /// <summary>
    /// <c>$1</c> = folder, <c>$2</c> = final file name, <c>$3</c> = expected lowercase SHA-256. Writes stdin to
    /// <c>.upload-&lt;pid&gt;</c> in a folder made the user's own with mode 700, prints <c>hash &lt;hex&gt;</c>, renames it only
    /// when the hash matches (else deletes it and exits 3), then deletes every other relay and upload file.
    /// </summary>
    public static readonly string Upload = string.Join("; ",
        "umask 077",
        "mkdir -p \"$1\" || exit 4",
        "chmod 700 \"$1\" || exit 4",
        "t=\"$1/.upload-$$\"",
        "if cat > \"$t\" && chmod 700 \"$t\"; then :; else rm -f \"$t\"; exit 4; fi",
        SelectHashTool,
        "if [ -z \"$h\" ]; then rm -f \"$t\"; exit 5; fi",
        "x=$($h < \"$t\")",
        "x=${x%% *}",
        "echo \"hash $x\"",
        "if [ \"$x\" = \"$3\" ]; then :; else rm -f \"$t\"; exit 3; fi",
        "if mv -f \"$t\" \"$1/$2\"; then :; else rm -f \"$t\"; exit 4; fi",
        "for f in \"$1\"/agent-speed-relay-* \"$1\"/.upload-*; do "
            + "if [ \"$f\" = \"$1/$2\" ]; then :; elif [ -f \"$f\" ]; then rm -f \"$f\"; fi; done",
        "exit 0");

    /// <summary>
    /// <c>$1</c> = folder, <c>$2</c> = file name: the relay replaces the shell, so it is the SSH session's process. The folder
    /// and the file must be the user's own, so nobody else can swap the verified file.
    /// </summary>
    public const string Run = "chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"";

    /// <summary>Safe inside one pair of single quotes in every supported login shell.</summary>
    public static bool IsQuotable(string script) => script.IndexOfAny(['\'', '\\', '!', '\n', '\r']) < 0;
}
