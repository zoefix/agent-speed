import Foundation

/// The agent-process rows of METRICS 10.5: which source, if any, a process runs.
struct AgentProcessRules: Sendable {
    static let scriptHosts: Set<String> = ["node", "nodejs", "bun", "deno"]
    /// Arguments of a Python host that mark Hermes Agent: its launcher bootstrap, `hermes_cli` and its ACP adapter.
    static let hermesMarkers = ["hermes_bootstrap", "hermes_cli", "acp_adapter"]
    /// Console scripts that run Hermes Agent on a Python host.
    static let hermesScripts: Set<String> = ["hermes", "hermes-agent", "hermes-acp"]

    let platform: TrafficPlatform

    private var claudeDesktopCode: [String] {
        platform.isWindows
            ? ["%LOCALAPPDATA%/Claude/claude-code/", "%LOCALAPPDATA%/Claude-3p/claude-code/", "%APPDATA%/Claude/claude-code/"]
            : ["~/Library/Application Support/Claude/claude-code/"]
    }

    private var claudeDesktopApp: [String] {
        platform.isWindows ? ["%LOCALAPPDATA%/AnthropicClaude/"] : ["/Applications/Claude.app/Contents/MacOS/Claude"]
    }

    /// Desktop apps' own executables, never agent processes.
    private var desktopApps: [String] {
        platform.isWindows
            ? ["%LOCALAPPDATA%/AnthropicClaude/", "%LOCALAPPDATA%/Programs/Devin/Devin.exe"]
            : ["/Applications/Claude.app/Contents/MacOS/Claude", "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT",
               "/Applications/Codex.app/Contents/MacOS/Codex", "/Applications/OpenCode.app/Contents/MacOS/OpenCode",
               "/Applications/Antigravity.app/Contents/MacOS/Antigravity", "/Applications/Devin.app/Contents/MacOS/Devin",
               "/Applications/Qoder IDE.app/Contents/MacOS/Qoder"]
    }

    private static let desktopPackages = ["claude_", "openai.codex_", "openai.chatgpt"]

    /// Windows desktop apps by location: the package prefix when a normalized (lower-case) path lies directly in the `app`
    /// folder of one of their packages. The tools a package bundles deeper (`app/resources/codex.exe`) follow the rows.
    private static func desktopPackage(_ normalized: String) -> String? {
        let parts = normalized.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count >= 4, parts[parts.count - 2] == "app", parts[parts.count - 4] == "windowsapps" else { return nil }
        let package = parts[parts.count - 3]
        return desktopPackages.first { package.hasPrefix($0) }
    }

    private var deepseekApp: [String] {
        platform.isWindows ? [] : ["/Applications/DeepSeek Harness.app/Contents/MacOS/DeepSeek Harness"]
    }

    /// WorkBuddy runs its embedded CLI on the app's own executable: macOS by the app's bundle id, Windows by its (lower-case)
    /// name.
    private static let workbuddyBundles: [String: SourceKind] = [
        "com.tencent.workbuddy.mac": .workbuddy, "com.workbuddy.workbuddy-ai": .workbuddyAi,
    ]
    private static let workbuddyExecutables: [String: SourceKind] = ["workbuddy": .workbuddy, "workbuddyai": .workbuddyAi]
    private static let workbuddyCLI = "/app.asar.unpacked/cli/"

    /// The rules read a process's arguments only for script hosts, `claude` executables (the Chrome bridge check) and
    /// WorkBuddy's executables; everything else is decided by the path.
    func needsArguments(executable: String, bundleIdentifier: () -> String?) -> Bool {
        let name = platform.name(executable)
        return Self.scriptHosts.contains(name.lowercased()) || isPythonHost(executable) || name == "claude"
            || platform.isUnder(executable, ["~/.local/share/claude/versions"])
            || workbuddyEdition(executable: executable, bundleIdentifier: bundleIdentifier) != nil
    }

    /// The WorkBuddy edition whose executable this is.
    func workbuddyEdition(executable: String, bundleIdentifier: () -> String?) -> SourceKind? {
        if platform.isWindows { return Self.workbuddyExecutables[platform.name(executable)] }
        return bundleIdentifier().flatMap { Self.workbuddyBundles[$0] }
    }

    func isScriptHost(_ executable: String) -> Bool {
        Self.scriptHosts.contains(platform.name(executable).lowercased())
    }

    /// `python`, `pythonw` or `python` followed by digits and dots (`python3`, `python3.14`), with or without `.exe`,
    /// in any case (macOS framework builds run `Python`).
    func isPythonHost(_ executable: String) -> Bool {
        var name = platform.name(executable).lowercased()
        if name.hasSuffix(".exe") { name.removeLast(4) }
        guard name.hasPrefix("python") else { return false }
        let rest = name.utf8.dropFirst(6)
        if rest.isEmpty || rest.elementsEqual("w".utf8) { return true }
        guard let first = rest.first, (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(first) else { return false }
        return rest.allSatisfy { (UInt8(ascii: "0")...UInt8(ascii: "9")).contains($0) || $0 == UInt8(ascii: ".") }
    }

    /// A Python host running Hermes Agent: an argument after the first contains one of its module names, or names
    /// one of its console scripts.
    private func isHermes(executable: String, arguments: () -> [String]?) -> Bool {
        guard isPythonHost(executable) else { return false }
        return (arguments() ?? []).dropFirst().contains { argument in
            Self.hermesMarkers.contains(where: argument.contains) || Self.hermesScripts.contains(platform.name(argument))
        }
    }

    func isClaudeDesktopApp(_ executable: String) -> Bool {
        if platform.isUnder(executable, claudeDesktopApp) { return true }
        return platform.isWindows && platform.name(executable) == "claude"
            && Self.desktopPackage(platform.normalize(executable)) == "claude_"
    }

    func isDesktopApp(_ executable: String) -> Bool {
        if platform.isUnder(executable, desktopApps) { return true }
        guard platform.isWindows else { return false }
        return Self.desktopPackage(platform.normalize(executable)) != nil
    }

    private func scriptArguments(executable: String, arguments: [String]?) -> String {
        guard isScriptHost(executable) else { return "" }
        return (arguments ?? []).dropFirst().map { argument in
            argument.contains("/") || argument.contains("\\") ? platform.normalize(argument) : argument
        }.joined(separator: " ")
    }

    /// The source whose row matches; `ancestorExecutables` are the process's ancestors, nearest first.
    func source(
        executable: String,
        arguments: () -> [String]?,
        bundleIdentifier: () -> String? = { nil },
        ancestorExecutables: [String]
    ) -> SourceKind? {
        if isDesktopApp(executable) { return nil }
        let name = platform.name(executable)
        if name == "claude" {
            if platform.isUnder(executable, claudeDesktopCode) || ancestorExecutables.contains(where: isClaudeDesktopApp) {
                return .claudeDesktop
            }
            return (arguments() ?? []).contains("--chrome-native-host") ? nil : .claudeCode
        }
        if platform.isUnder(executable, ["~/.local/share/claude/versions"]) {
            return (arguments() ?? []).contains("--chrome-native-host") ? nil : .claudeCode
        }
        let script = isScriptHost(executable) ? scriptArguments(executable: executable, arguments: arguments()) : ""
        if script.contains("@anthropic-ai/claude-code/") { return .claudeCode }
        if name == "codex" || (name.hasPrefix("codex-") && Self.containsAny(name, ["x86_64", "aarch64", "apple-darwin", "linux", "windows"])) {
            return .codex
        }
        if script.contains("@openai/codex/bin/codex.js") { return .codex }
        if name == "dsh" || platform.isUnder(executable, deepseekApp) || (platform.isWindows && name == "deepseek harness") {
            return .deepseek
        }
        if name == "opencode" || name == "opencode-cli"
            || (name.hasPrefix("opencode-") && Self.containsAny(name, ["darwin", "linux", "windows"])) {
            return .opencode
        }
        if script.contains("opencode-ai/bin/opencode") { return .opencode }
        if script.contains("/@google/gemini-cli/") || name == "gemini" { return .gemini }
        if name == "omp" || script.contains("/@oh-my-pi/") || script.contains("/oh-my-pi/") { return .omp }
        if name == "agy" || name.hasPrefix("language_server_") { return .antigravity }
        if name == "zcode" || name == "zcode-cli" || script.contains("/@zcode/cli/") { return .zcode }
        if name == "kimi" || name == "kimi-code" || script.contains("/@moonshot-ai/kimi-code/") { return .kimi }
        if name == "grok" || name == "grok-build" { return .grokBuild }
        if name == "pi" || script.contains("/@earendil-works/pi-coding-agent/") || script.contains("/@mariozechner/pi-coding-agent/") {
            return .pi
        }
        if name == "openclaw" || script.contains("/openclaw.mjs") || script.contains("/openclaw/dist/") { return .openclaw }
        if isHermes(executable: executable, arguments: arguments) { return .hermes }
        // Factory Droid is one native executable for the CLI, its `droid exec` children and Factory Desktop's daemon.
        if name == "droid" { return .droid }
        // Devin CLI, interactive or `devin acp` (which Devin Desktop starts for its local agent).
        if name == "devin" { return .devin }
        // Qoder IDE's agent core, `resources/bin/<platform>/Qoder` inside the IDE; the IDE's own executable is not.
        if name == (platform.isWindows ? "qoder" : "Qoder"), Self.isQoderCore(platform.normalize(executable)) { return .qoder }
        if name == "qodercli" || name == "qoderclicn" || script.contains("/@qoder-ai/qodercli/")
            || script.contains("/@qodercn-ai/qoderclicn/") {
            return .qoderCli
        }
        // Only the embedded CodeBuddy Code CLI is the agent; the app, its helpers and its daemon are not.
        if let edition = workbuddyEdition(executable: executable, bundleIdentifier: bundleIdentifier),
           (arguments() ?? []).dropFirst().contains(where: { platform.normalize($0).contains(Self.workbuddyCLI) }) {
            return edition
        }
        return nil
    }

    /// A normalized path that ends in `/resources/bin/<folder>/<name>`, compared case-insensitively.
    static func isQoderCore(_ normalized: String) -> Bool {
        let parts = normalized.lowercased().split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count >= 5 else { return false }
        let count = parts.count
        return parts[count - 4] == "resources" && parts[count - 3] == "bin" && !parts[count - 2].isEmpty
            && !parts[count - 1].isEmpty
    }

    private static func containsAny(_ text: String, _ needles: [String]) -> Bool {
        needles.contains { text.contains($0) }
    }
}
