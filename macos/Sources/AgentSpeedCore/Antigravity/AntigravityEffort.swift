import Foundation

/// A response's reasoning effort and speed (EXECUTION-MODES, Antigravity): effort is mostly a model variant, so only
/// exact catalog suffixes and display names count, and only for the model that actually answered.
enum AntigravityEffort {
    private static let modelSuffixes = ["-extra-low", "-low", "-medium", "-high", "-max"]
    private static let labelWords = ["Low", "Medium", "High", "Max"]
    private static let servedVariantSuffix = "-exp-b"
    private static let thinkingLevels: [Int64: String] = [1: "low", 2: "medium", 3: "high"]

    /// The base model and effort token of a catalog id that ends in exactly one effort suffix.
    static func split(model: String) -> (base: String, effort: String)? {
        for suffix in modelSuffixes where model.hasSuffix(suffix) && model.utf8.count > suffix.utf8.count {
            return (String(model.dropLast(suffix.count)), String(suffix.dropFirst()))
        }
        return nil
    }

    /// The effort word of a display name such as `Gemini 3.1 Pro (High)`.
    static func word(ofLabel label: String) -> String? {
        labelWords.first { word in
            let suffix = " (\(word))"
            return label.hasSuffix(suffix) && label.utf8.count > suffix.utf8.count
        }
    }

    /// The token of `completion_config_override.thinking_level`.
    static func token(thinkingLevel level: Int64) -> String? { thinkingLevels[level] }

    /// The display name first; else the executor's suffix or pass-through level, unless another model answered.
    static func fact(generation: AntigravityGeneration?, executor: AntigravityExecutor?) -> ExecutionModeFact? {
        if let word = generation?.label.flatMap(word(ofLabel:)) {
            return ExecutionModeAdapters.effort(word, evidence: .recorded, scope: .request)
        }
        guard let executor, let model = executor.model else { return nil }
        let split = split(model: model)
        if var answered = generation?.responseModel {
            if answered.hasSuffix(servedVariantSuffix) { answered.removeLast(servedVariantSuffix.count) }
            guard answered == model || answered == split?.base else { return nil }
        }
        return ExecutionModeAdapters.effort(split?.effort ?? executor.thinking, evidence: .recorded, scope: .turn)
    }

    static func modes(generation: AntigravityGeneration?, executor: AntigravityExecutor?, usage: AntigravityUsage) -> ExecutionModes {
        let tier = ExecutionModeAdapters.serviceTier(usage.tier, provider: "google", evidence: .served, scope: .response)
        return ExecutionModes([fact(generation: generation, executor: executor), tier].compactMap { $0 })
    }
}
