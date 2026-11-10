using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Traffic.Engine;

/// <summary>The agent-process rows of METRICS 10.5: which source, if any, a process runs.</summary>
internal sealed class AgentProcessRules
{
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.Ordinal) { "node", "nodejs", "bun", "deno" };

    /// <summary>Hermes Agent's launchers and entry modules in a Python host's arguments (METRICS 10.5).</summary>
    private static readonly string[] HermesMarkers = ["hermes_bootstrap", "hermes_cli", "acp_adapter"];

    /// <summary>Hermes Agent's console scripts and Windows launcher, by file name without <c>.exe</c>.</summary>
    private static readonly HashSet<string> HermesScripts = new(StringComparer.Ordinal) { "hermes", "hermes-agent", "hermes-acp" };

    /// <summary>
    /// Windows desktop apps by location: an executable directly in the <c>app</c> folder of their packages. The tools a package
    /// bundles deeper (<c>app/resources/codex.exe</c>) follow the rows. Matched against normalized (lower-case) paths.
    /// </summary>
    private static readonly Regex DesktopPackageApp = new("/windowsapps/(claude_|openai\\.codex_|openai\\.chatgpt)[^/]*/app/[^/]+$",
        RegexOptions.CultureInvariant);
    /// <summary>Qoder IDE's agent core: <c>resources/bin/&lt;platform&gt;/Qoder</c> inside the IDE; the IDE's own executable is not (METRICS 10.5).</summary>
    private static readonly Regex QoderCore = new("/resources/bin/[^/]+/[^/]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] CodexTargets = ["x86_64", "aarch64", "apple-darwin", "linux", "windows"];
    private static readonly string[] OpenCodeTargets = ["darwin", "linux", "windows"];

    /// <summary>
    /// WorkBuddy runs its embedded CLI on the app's own executable (Electron as Node): macOS by bundle id, Windows by its
    /// (lower-case) name.
    /// The editions are resolved by their source names (METRICS 1), so a build without those sources skips the rows.
    /// </summary>
    private readonly Dictionary<string, SourceKind> _workBuddyBundles;
    private readonly Dictionary<string, SourceKind> _workBuddyNames;

    private const string WorkBuddyCli = "/app.asar.unpacked/cli/";

    private readonly TrafficPlatform _platform;
    private readonly string[] _claudeDesktopCode;
    private readonly string[] _claudeDesktopApp;
    private readonly string[] _desktopApps;
    private readonly string[] _deepseekApp;
    private readonly string[] _claudeVersions;

    /// <param name="sources">Resolves a source name (METRICS 1); null uses the app's own names.</param>
    public AgentProcessRules(TrafficPlatform platform, Func<string, SourceKind?>? sources = null)
    {
        _platform = platform;
        sources ??= History.SourceNames.Parse;
        _workBuddyBundles = Editions(sources, ("com.tencent.workbuddy.mac", "workbuddy"), ("com.workbuddy.workbuddy-ai", "workbuddyAi"));
        _workBuddyNames = Editions(sources, ("workbuddy", "workbuddy"), ("workbuddyai", "workbuddyAi"));
        _claudeDesktopCode = platform.IsWindows
            ? platform.Prefixes("%LOCALAPPDATA%/Claude/claude-code/", "%LOCALAPPDATA%/Claude-3p/claude-code/", "%APPDATA%/Claude/claude-code/")
            : platform.Prefixes("~/Library/Application Support/Claude/claude-code/");
        _claudeDesktopApp = platform.IsWindows
            ? platform.Prefixes("%LOCALAPPDATA%/AnthropicClaude/")
            : platform.Prefixes("/Applications/Claude.app/Contents/MacOS/Claude");
        // Devin Desktop's own executable is an app process (Windows: an assumed path); its `devin acp` agent follows the row. Qoder
        // IDE's own executable is one too; its agent core follows the row.
        _desktopApps = platform.IsWindows
            ? platform.Prefixes("%LOCALAPPDATA%/AnthropicClaude/", "%LOCALAPPDATA%/Programs/Devin/Devin.exe")
            : platform.Prefixes(
                "/Applications/Claude.app/Contents/MacOS/Claude",
                "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT",
                "/Applications/Codex.app/Contents/MacOS/Codex",
                "/Applications/OpenCode.app/Contents/MacOS/OpenCode",
                "/Applications/Antigravity.app/Contents/MacOS/Antigravity",
                "/Applications/Devin.app/Contents/MacOS/Devin",
                "/Applications/Qoder IDE.app/Contents/MacOS/Qoder");
        _deepseekApp = platform.IsWindows ? [] : platform.Prefixes("/Applications/DeepSeek Harness.app/Contents/MacOS/DeepSeek Harness");
        _claudeVersions = platform.Prefixes("~/.local/share/claude/versions");
    }

    /// <summary>
    /// The rules read a process's arguments only for script hosts, <c>claude</c> executables (the Chrome bridge check) and
    /// WorkBuddy's executables; everything else is decided by the path.
    /// </summary>
    public bool NeedsArguments(string executable, Func<string?> bundle)
    {
        var name = _platform.Name(executable);
        return IsScriptHostName(name) || IsPythonHostName(name) || name == "claude" || _platform.IsUnder(executable, _claudeVersions)
            || WorkBuddyEdition(name, bundle) is not null;
    }

    private static Dictionary<string, SourceKind> Editions(Func<string, SourceKind?> sources, params (string Key, string Source)[] pairs)
    {
        var map = new Dictionary<string, SourceKind>(StringComparer.Ordinal);
        foreach (var (key, source) in pairs)
        {
            if (sources(source) is { } kind)
            {
                map[key] = kind;
            }
        }

        return map;
    }

    private SourceKind? WorkBuddyEdition(string name, Func<string?> bundle) =>
        _platform.IsWindows
            ? _workBuddyNames.TryGetValue(name, out var byName) ? byName : null
            : _workBuddyBundles.Count > 0 && bundle() is { } id && _workBuddyBundles.TryGetValue(id, out var byBundle) ? byBundle : null;

    public bool IsScriptHost(string executable) => IsScriptHostName(_platform.Name(executable));

    private static bool IsScriptHostName(string name) => ScriptHosts.Contains(name.ToLowerInvariant());

    /// <summary><c>python</c>, <c>pythonw</c>, or <c>python</c> followed by digits and dots, compared case-insensitively (macOS <c>Python</c>).</summary>
    private static bool IsPythonHostName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".exe", StringComparison.Ordinal))
        {
            lower = lower[..^4];
        }

        if (!lower.StartsWith("python", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = lower.AsSpan(6);
        return rest.IsEmpty || rest is "w" || (char.IsAsciiDigit(rest[0]) && !rest.ContainsAnyExcept("0123456789."));
    }

    /// <summary>A Python host running Hermes Agent: a launcher bootstrap, <c>-m hermes_cli.main</c>, or a console script.</summary>
    private bool IsHermes(string name, Func<IReadOnlyList<string>?> arguments) =>
        IsPythonHostName(name) && arguments() is { Count: > 1 } args
        && args.Skip(1).Any(a => HermesMarkers.Any(m => a.Contains(m, StringComparison.Ordinal)) || HermesScripts.Contains(_platform.Name(a)));

    public bool IsClaudeDesktopApp(string executable)
    {
        if (_platform.IsUnder(executable, _claudeDesktopApp))
        {
            return true;
        }

        return _platform.IsWindows && _platform.Name(executable) == "claude"
            && DesktopPackageApp.Match(_platform.Normalize(executable)) is { Success: true } match && match.Groups[1].Value == "claude_";
    }

    public bool IsDesktopApp(string executable)
    {
        if (_platform.IsUnder(executable, _desktopApps))
        {
            return true;
        }

        if (!_platform.IsWindows)
        {
            return false;
        }

        return DesktopPackageApp.IsMatch(_platform.Normalize(executable));
    }

    private string ScriptArguments(string executable, IReadOnlyList<string>? arguments)
    {
        if (!IsScriptHost(executable) || arguments is null || arguments.Count < 2)
        {
            return string.Empty;
        }

        return string.Join(' ', arguments.Skip(1).Select(a => a.Contains('/') || a.Contains('\\') ? _platform.Normalize(a) : a));
    }

    /// <summary>The source whose row matches; <paramref name="ancestorExecutables"/> are the process's ancestors, nearest first.</summary>
    /// <param name="bundle">macOS: the bundle id of the executable's app, read only when a rule needs it.</param>
    public SourceKind? Source(string executable, Func<IReadOnlyList<string>?> arguments, IReadOnlyList<string> ancestorExecutables,
        Func<string?>? bundle = null)
    {
        if (IsDesktopApp(executable))
        {
            return null;
        }

        var name = _platform.Name(executable);
        if (name == "claude")
        {
            if (_platform.IsUnder(executable, _claudeDesktopCode) || ancestorExecutables.Any(IsClaudeDesktopApp))
            {
                return SourceKind.ClaudeDesktop;
            }

            return IsChromeBridge(arguments()) ? null : SourceKind.ClaudeCode;
        }

        if (_platform.IsUnder(executable, _claudeVersions))
        {
            return IsChromeBridge(arguments()) ? null : SourceKind.ClaudeCode;
        }

        var script = IsScriptHost(executable) ? ScriptArguments(executable, arguments()) : string.Empty;
        if (script.Contains("@anthropic-ai/claude-code/", StringComparison.Ordinal))
        {
            return SourceKind.ClaudeCode;
        }

        if (name == "codex" || (name.StartsWith("codex-", StringComparison.Ordinal) && ContainsAny(name, CodexTargets)))
        {
            return SourceKind.Codex;
        }

        if (script.Contains("@openai/codex/bin/codex.js", StringComparison.Ordinal))
        {
            return SourceKind.Codex;
        }

        if (name is "zcode" or "zcode-cli" || script.Contains("/@zcode/cli/", StringComparison.Ordinal))
        {
            return SourceKind.ZCode;
        }

        if (name is "kimi" or "kimi-code" || script.Contains("/@moonshot-ai/kimi-code/", StringComparison.Ordinal))
        {
            return SourceKind.Kimi;
        }

        if (name is "grok" or "grok-build" or "xai-grok-pager")
        {
            return SourceKind.GrokBuild;
        }

        if (name == "dsh" || _platform.IsUnder(executable, _deepseekApp) || (_platform.IsWindows && name == "deepseek harness"))
        {
            return SourceKind.DeepSeek;
        }

        if (name is "opencode" or "opencode-cli" || (name.StartsWith("opencode-", StringComparison.Ordinal) && ContainsAny(name, OpenCodeTargets)))
        {
            return SourceKind.OpenCode;
        }

        if (script.Contains("opencode-ai/bin/opencode", StringComparison.Ordinal))
        {
            return SourceKind.OpenCode;
        }

        if (script.Contains("/@google/gemini-cli/", StringComparison.Ordinal) || name == "gemini")
        {
            return SourceKind.Gemini;
        }

        if (name == "omp" || script.Contains("/@oh-my-pi/", StringComparison.Ordinal) || script.Contains("/oh-my-pi/", StringComparison.Ordinal))
        {
            return SourceKind.Omp;
        }

        if (name == "agy" || name.StartsWith("language_server_", StringComparison.Ordinal))
        {
            return SourceKind.Antigravity;
        }

        if (name == "pi" || script.Contains("/@earendil-works/pi-coding-agent/", StringComparison.Ordinal)
            || script.Contains("/@mariozechner/pi-coding-agent/", StringComparison.Ordinal))
        {
            return SourceKind.Pi;
        }

        // The Gateway sends OpenClaw's own requests; the agents it starts for delegated turns match their own rows.
        if (name == "openclaw" || script.Contains("/openclaw.mjs", StringComparison.Ordinal)
            || script.Contains("/openclaw/dist/", StringComparison.Ordinal))
        {
            return SourceKind.OpenClaw;
        }

        // The Python process sends Hermes's model requests; a launcher sends nothing (`hermes` is also another engine's tool).
        if (IsHermes(name, arguments))
        {
            return SourceKind.Hermes;
        }

        // Factory Droid's CLI binary, also as `droid exec` workers and Factory Desktop's daemon; its npm shim sends nothing.
        if (name == "droid")
        {
            return SourceKind.Droid;
        }

        // The Devin CLI, also `devin acp` started by Devin Desktop or another client as its local agent.
        if (name == "devin")
        {
            return SourceKind.Devin;
        }

        // Qoder IDE's agent core, which makes the model calls; the IDE and its Electron helpers are app processes.
        if (name == (_platform.IsWindows ? "qoder" : "Qoder") && QoderCore.IsMatch(_platform.Normalize(executable)))
        {
            return SourceKind.Qoder;
        }

        // Qoder CLI's native installers (both editions), or its npm bundle on a script host.
        if (name is "qodercli" or "qoderclicn" || script.Contains("/@qoder-ai/qodercli/", StringComparison.Ordinal)
            || script.Contains("/@qodercn-ai/qoderclicn/", StringComparison.Ordinal))
        {
            return SourceKind.QoderCli;
        }

        // WorkBuddy's embedded CLI, never its app, helper or daemon processes (the same executable without the CLI entry).
        if (WorkBuddyEdition(name, bundle ?? (() => null)) is { } edition && arguments() is { Count: > 1 } args
            && args.Skip(1).Any(a => _platform.Normalize(a).Contains(WorkBuddyCli, StringComparison.Ordinal)))
        {
            return edition;
        }

        return null;
    }

    private static bool IsChromeBridge(IReadOnlyList<string>? arguments) => arguments?.Contains("--chrome-native-host") ?? false;

    private static bool ContainsAny(string text, string[] needles) => needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
