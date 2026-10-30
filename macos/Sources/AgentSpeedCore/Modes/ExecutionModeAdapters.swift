import Foundation

enum ExecutionModeAdapters {
    /// Factory's catalog entries that change the request's tier (EXECUTION-MODES, Factory Droid). No suffix rule:
    /// `glm-5.2-fast` is a model of its own and `auto-fast` a router.
    private static let droidTiers: [String: (value: String, provider: String)] = {
        var tiers: [String: (value: String, provider: String)] = [:]
        for model in ["claude-opus-4-6-fast", "claude-opus-4-7-fast", "claude-opus-4-8-fast", "claude-opus-5-fast", "claude-opus-5-5-fast"] {
            tiers[model] = ("fast", "anthropic")
        }
        for model in ["gpt-5.3-codex-fast", "gpt-5.4-fast", "gpt-5.4-mini-fast", "gpt-5.5-fast", "gpt-5.6-fast", "gpt-5.6-sol-fast",
                      "gpt-6-sol-fast", "gpt-6-luna-fast"] {
            tiers[model] = ("fast", "openai")
        }
        for model in ["gpt-6-astra-ultrafast", "gpt-6.1-sol-ultrafast"] { tiers[model] = ("ultrafast", "openai") }
        for model in ["gpt-5.6-terra-flex", "gpt-5.6-luna-flex", "gpt-6-sol-flex", "gpt-6-astra-flex", "gpt-6-luna-flex"] {
            tiers[model] = ("flex", "openai")
        }
        return tiers
    }()

    private static let kimiHighspeedModels: Set<String> = ["k3.1-pro-highspeed", "kimi-for-coding-highspeed"]
    private static let zcodeHighspeedModels: Set<String> = [
        "GLM-5.1-Highspeed", "MiniMax-M2.7-highspeed", "MiniMax-M2.5-highspeed", "kimi-k2.7-code-highspeed",
        "kimi-for-coding-highspeed", "kimi-k3.1-pro-highspeed", "k3.1-pro-highspeed",
    ]
    private static let zcodeHighspeedProviders: Set<String> = ["account:zai-highspeed-card", "account:bigmodel-highspeed-card"]
    private static let ompFamilies: [String: String] = [
        "anthropic": "anthropic", "openai": "openai", "openai-codex": "openai", "azure": "openai",
        "azure-openai-responses": "openai", "google": "google", "google-gemini-cli": "google",
        "google-antigravity": "google", "google-vertex": "google",
    ]
    private static let hermesEfforts: Set<String> = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"]
    private static let hermesOff: Set<String> = ["none", "false", "disabled"]
    private static let hermesTiers: [String: String] = [
        "fast": "fast", "priority": "fast", "ultrafast": "ultrafast", "ultra": "ultrafast", "normal": "standard",
        "default": "standard", "standard": "standard", "off": "standard", "none": "standard",
    ]
    private static let unresolvedEfforts: Set<String> = [
        "auto", "default", "inherit", "inherited", "unknown", "thinking_level_unspecified",
    ]

    static func normalize(adapter: String, raw: String?, provider: String?, evidence: ExecutionModeEvidence,
                          scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard provider.map({ ExecutionModeFact.isIdentifier($0, limit: 32) }) ?? true else { return nil }
        switch adapter {
        case "serviceTier":
            return serviceTier(raw, provider: provider, evidence: evidence, scope: scope)
        case "claudeSpeed":
            return fact(kind: .serviceTier, raw: raw, evidence: evidence, provider: provider, scope: scope).facts.first
        case "kimiModel":
            return exactModel(raw, in: kimiHighspeedModels, kind: .modelVariant, value: "highspeed", evidence: evidence,
                              provider: provider, scope: scope)
        case "zcodeModel":
            return exactModel(raw, in: zcodeHighspeedModels, kind: .modelVariant, value: "highspeed", evidence: evidence,
                              provider: provider, scope: scope)
        case "zcodeProvider":
            return exactModel(raw, in: zcodeHighspeedProviders, kind: .accelerator, value: "highspeed", evidence: evidence,
                              provider: provider, scope: scope)
        case "droidModel":
            return droidModel(raw, evidence: evidence, scope: scope)
        case "ompTier":
            return ompTier(raw, provider: provider, evidence: evidence, scope: scope)
        case "hermesEffort":
            return hermesEffort(raw, evidence: evidence, scope: scope)
        case "hermesServiceTier":
            return hermesServiceTier(raw, evidence: evidence, scope: scope)
        case "routing":
            let value = ["fast-model": "fast", "balanced-model": "balanced", "deep-model": "deep", "auto": "auto"][raw ?? ""]
            guard let value else { return nil }
            return fact(kind: .routing, raw: raw, evidence: evidence, provider: provider, scope: scope).facts.first?.withCanonicalValue(value)
        case "workflow":
            guard ["fast", "planning"].contains(raw ?? "") else { return nil }
            return fact(kind: .workflow, raw: raw, evidence: evidence, provider: provider, scope: scope).facts.first
        case "effort":
            return effort(raw, evidence: evidence, scope: scope)
        default: return nil
        }
    }

    /// A reasoning effort token: `none` and `disabled` are `off`, `enabled` is `on`; automatic and inherited selections
    /// are no fact.
    static func effort(_ raw: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let raw, let fact = ExecutionModeFact(kind: .effort, rawValue: raw, evidence: evidence, scope: scope),
              !unresolvedEfforts.contains(fact.value) else { return nil }
        switch fact.value {
        case "none", "disabled": return fact.withCanonicalValue("off")
        case "enabled": return fact.withCanonicalValue("on")
        default: return fact
        }
    }

    /// A native service tier: OpenAI `default` is `standard`, `priority` is `fast` there and for Google, Google's
    /// `unspecified` says nothing, and Anthropic's capacity tier is never a speed.
    static func serviceTier(_ raw: String?, provider: String?, evidence: ExecutionModeEvidence,
                            scope: ExecutionModeScope) -> ExecutionModeFact? {
        let namespace = provider?.lowercased()
        guard namespace != "anthropic", let raw else { return nil }
        guard namespace != "google" || raw.lowercased() != "unspecified" else { return nil }
        let fact = fact(kind: .serviceTier, raw: raw, evidence: evidence, provider: provider, scope: scope).facts.first
        return namespace == "google" && fact?.value == "priority" ? fact?.withCanonicalValue("fast") : fact
    }

    static func claude(_ speed: String?) -> ExecutionModes {
        fact(kind: .serviceTier, raw: speed, evidence: .served, provider: "anthropic", scope: .response)
    }

    static func openAI(_ tier: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModes {
        fact(kind: .serviceTier, raw: tier, evidence: evidence, provider: "openai", scope: scope)
    }

    static func kimi(_ model: String?) -> ExecutionModes {
        ExecutionModes([exactModel(model, in: kimiHighspeedModels, kind: .modelVariant, value: "highspeed", evidence: .selected,
                                   provider: "kimi", scope: .request)].compactMap { $0 })
    }

    static func zcodeModel(_ model: String?) -> ExecutionModeFact? {
        exactModel(model, in: zcodeHighspeedModels, kind: .modelVariant, value: "highspeed", evidence: .selected,
                   provider: "zcode", scope: .request)
    }

    static func zcodeProvider(_ provider: String?) -> ExecutionModeFact? {
        exactModel(provider, in: zcodeHighspeedProviders, kind: .accelerator, value: "highspeed", evidence: .recorded,
                   provider: "zcode", scope: .request)
    }

    static func droidModel(_ model: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let model, let tier = droidTiers[model] else { return nil }
        return ExecutionModeFact(kind: .serviceTier, rawValue: model, evidence: evidence, provider: tier.provider, scope: scope)?
            .withCanonicalValue(tier.value)
    }

    static func routing(_ model: String?) -> ExecutionModes {
        guard let fact = normalize(adapter: "routing", raw: model, provider: nil, evidence: .selected, scope: .request) else { return .empty }
        return ExecutionModes([fact])
    }

    /// The token of an exact catalog id; nothing else (no substring or case-insensitive matching).
    private static func exactModel(_ raw: String?, in catalog: Set<String>, kind: ExecutionModeKind, value: String,
                                   evidence: ExecutionModeEvidence, provider: String?, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let raw, catalog.contains(raw) else { return nil }
        return fact(kind: kind, raw: raw, evidence: evidence, provider: provider, scope: scope).facts.first?.withCanonicalValue(value)
    }

    /// The tier family of an omp provider; nil for a provider without one.
    static func ompFamily(_ provider: String?) -> String? {
        provider.flatMap { ompFamilies[$0] }
    }

    static func ompTier(_ raw: String?, provider: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let family = ompFamily(provider?.lowercased()) else { return nil }
        let result = fact(kind: .serviceTier, raw: raw, evidence: evidence, provider: family, scope: scope).facts.first
        return family != "openai" && result?.value == "priority" ? result?.withCanonicalValue("fast") : result
    }

    static func hermesEffort(_ raw: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let raw, let fact = ExecutionModeFact(kind: .effort, rawValue: raw, evidence: evidence, scope: scope) else { return nil }
        if hermesOff.contains(fact.value) { return fact.withCanonicalValue("off") }
        return hermesEfforts.contains(fact.value) ? fact : nil
    }

    static func hermesServiceTier(_ raw: String?, evidence: ExecutionModeEvidence, scope: ExecutionModeScope) -> ExecutionModeFact? {
        guard let raw, let fact = ExecutionModeFact(kind: .serviceTier, rawValue: raw, evidence: evidence, scope: scope),
              let value = hermesTiers[fact.value] else { return nil }
        return fact.withCanonicalValue(value)
    }

    static func fact(kind: ExecutionModeKind, raw: String?, evidence: ExecutionModeEvidence,
                     provider: String?, scope: ExecutionModeScope) -> ExecutionModes {
        guard let raw, let fact = ExecutionModeFact(kind: kind, rawValue: raw, evidence: evidence,
                                                  provider: provider, scope: scope) else { return .empty }
        return ExecutionModes([fact])
    }
}
