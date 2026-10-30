import Foundation

/// Values only a config file holds (EXECUTION-MODES, Configured values). They reach an agent's current values and nothing
/// else. Files are regular, at most 1 MiB, UTF-8 (one leading U+FEFF removed); only the named key is taken, and nothing
/// else of a file is kept or logged.
enum ConfiguredModes {
    /// `<home>/.gemini/settings.json`: the effort of each model an override names.
    struct GeminiEfforts: Equatable, Sendable {
        var byModel: [String: ExecutionModes] = [:]

        func modes(model: String?) -> ExecutionModes { model.flatMap { byModel[$0] } ?? .empty }
    }

    static func codex(sessionsRoot: String) -> ExecutionModes {
        codex(path: codexConfigPath(sessionsRoot: sessionsRoot))
    }

    static func codexConfigPath(sessionsRoot: String) -> String {
        let directory = (sessionsRoot as NSString).lastPathComponent == "sessions"
            ? (sessionsRoot as NSString).deletingLastPathComponent : sessionsRoot
        return (directory as NSString).appendingPathComponent("config.toml")
    }

    static func codex(path: String) -> ExecutionModes {
        guard let data = contents(path), let text = String(data: data, encoding: .utf8),
              let tier = declaredTier(text) else { return .empty }
        return ExecutionModeAdapters.openAI(tier, evidence: .configured, scope: .configuration)
    }

    /// Only a literal root setting is claimed; this is not Codex's layered effective configuration.
    static func declaredTier(_ text: String) -> String? {
        var tier: String?
        var declared = false
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            if line.contains("\"\"\"") || line.contains("'''") { return nil }
            if line.hasPrefix("[") { break }
            guard let equals = line.firstIndex(of: "="),
                  line[..<equals].trimmingCharacters(in: .whitespaces) == "service_tier" else { continue }
            guard !declared else { return nil }
            declared = true
            let value = line[line.index(after: equals)...].trimmingCharacters(in: .whitespaces)
            guard let quote = value.first, quote == "\"" || quote == "'",
                  let end = value.dropFirst().firstIndex(of: quote) else { return nil }
            let tail = value[value.index(after: end)...].trimmingCharacters(in: .whitespaces)
            let native = String(value[value.index(after: value.startIndex)..<end])
            guard (tail.isEmpty || tail.hasPrefix("#")), ExecutionModeFact.isIdentifier(native, limit: 64) else { return nil }
            tier = native
        }
        return tier
    }

    /// `userSettings.vertexServiceTier` of `config/config.json` in the folder that holds an Antigravity store.
    static func antigravity(path: String) -> ExecutionModes {
        let tier = object(path)?["userSettings"]?["vertexServiceTier"]?.string
        return modes(ExecutionModeAdapters.serviceTier(tier, provider: "google", evidence: .configured, scope: .configuration))
    }

    /// `reasoningEffort` of a WorkBuddy data folder's `settings.json`.
    static func workBuddy(path: String) -> ExecutionModes {
        modes(ExecutionModeAdapters.effort(object(path)?["reasoningEffort"]?.string, evidence: .configured, scope: .configuration))
    }

    /// Of `modelConfigs.overrides` and then `customOverrides`, in order, an entry counts only when its `match` is exactly
    /// the one model. The last that names a level, or a budget of 0, decides; another budget names no level.
    static func gemini(path: String) -> GeminiEfforts {
        guard let configs = object(path)?["modelConfigs"] else { return GeminiEfforts() }
        var chosen: [String: String] = [:]
        for name in ["overrides", "customOverrides"] {
            for entry in configs[name]?.array ?? [] {
                guard let match = entry["match"]?.object, match.count == 1, let model = match["model"]?.string,
                      let thinking = entry["modelConfig"]?["generateContentConfig"]?["thinkingConfig"] else { continue }
                if let level = thinking["thinkingLevel"]?.string {
                    chosen[model] = level
                } else if case .number(let budget)? = thinking["thinkingBudget"], budget.double == 0 {
                    chosen[model] = "off"
                }
            }
        }
        return GeminiEfforts(byModel: chosen.mapValues {
            modes(ExecutionModeAdapters.effort($0, evidence: .configured, scope: .configuration))
        })
    }

    private static func modes(_ fact: ExecutionModeFact?) -> ExecutionModes {
        ExecutionModes([fact].compactMap { $0 })
    }

    private static func contents(_ path: String) -> Data? {
        guard var data = HarnessMetadata.bytes(path) else { return nil }
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { data.removeFirst(3) }
        return data
    }

    /// The JSON object of a config file.
    private static func object(_ path: String) -> JSONValue? {
        guard let data = contents(path), let json = try? JSONParser.parse(data), json.object != nil else { return nil }
        return json
    }
}
