import Foundation

/// The POSIX `sh` scripts of the remote commands (REMOTE 4.2, 4.3). Each is one line without `'`, `\`, `!` or a
/// newline, so it survives single quotes in sh, bash, zsh, dash, fish (backslash escapes) and tcsh (history, no
/// multi-line quotes). Arguments arrive as `$1` …; every use is quoted.
enum RemoteScripts {
    /// Sets `h` to the first hash tool found, or to empty.
    static let selectHashTool = [
        "if command -v sha256sum >/dev/null 2>&1; then h=sha256sum",
        "elif command -v shasum >/dev/null 2>&1; then h=\"shasum -a 256\"",
        "elif command -v openssl >/dev/null 2>&1; then h=\"openssl dgst -sha256 -r\"",
        "else h=; fi",
    ].joined(separator: "; ")

    /// `$1` = folder. Prints `system <uname -s -m>`, `tool <command>` or `tool none`, one `hash <hex> <name>` per
    /// readable `agent-speed-relay-*` file, then `end`. Hashes are read from stdin, so no file name reaches a tool.
    static let probe = [
        "echo \"system $(uname -s -m)\"",
        selectHashTool,
        "if [ -z \"$h\" ]; then echo \"tool none\"; echo end; exit 0; fi",
        "echo \"tool $h\"",
        "for f in \"$1\"/agent-speed-relay-*; do if [ -f \"$f\" ]; then "
            + "x=$($h < \"$f\") && echo \"hash ${x%% *} ${f##*/}\"; fi; done",
        "echo end",
    ].joined(separator: "; ")

    /// `$1` = folder, `$2` = final file name, `$3` = expected lowercase SHA-256. Writes stdin to `.upload-<pid>`
    /// (mode 700 in a folder made the user's own with mode 700; a folder of another user fails), prints `hash <hex>`, renames it only when the hash matches
    /// (else deletes it and exits 3), then deletes every other `agent-speed-relay-*` and `.upload-*` file.
    static let upload = [
        "umask 077",
        "mkdir -p \"$1\" || exit 4",
        "chmod 700 \"$1\" || exit 4",
        "t=\"$1/.upload-$$\"",
        "if cat > \"$t\" && chmod 700 \"$t\"; then :; else rm -f \"$t\"; exit 4; fi",
        selectHashTool,
        "if [ -z \"$h\" ]; then rm -f \"$t\"; exit 5; fi",
        "x=$($h < \"$t\")",
        "x=${x%% *}",
        "echo \"hash $x\"",
        "if [ \"$x\" = \"$3\" ]; then :; else rm -f \"$t\"; exit 3; fi",
        "if mv -f \"$t\" \"$1/$2\"; then :; else rm -f \"$t\"; exit 4; fi",
        "for f in \"$1\"/agent-speed-relay-* \"$1\"/.upload-*; do "
            + "if [ \"$f\" = \"$1/$2\" ]; then :; elif [ -f \"$f\" ]; then rm -f \"$f\"; fi; done",
        "exit 0",
    ].joined(separator: "; ")

    /// `$1` = folder, `$2` = file name: the relay replaces the shell, so it is the SSH session's process. The folder
    /// and the file must be the user's own (`chmod` fails otherwise), so nobody else can swap the verified file.
    static let run = "chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\""

    /// Safe inside one pair of single quotes in every supported login shell.
    static func isQuotable(_ script: String) -> Bool {
        !script.contains { "'\\!\n\r".contains($0) }
    }
}
