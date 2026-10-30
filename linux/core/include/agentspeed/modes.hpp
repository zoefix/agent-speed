#pragma once

#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Execution modes (spec/EXECUTION-MODES.md): facts about service tier, accelerator, model variant, routing, workflow
// and reasoning effort, and the effort and speed every agent row shows.
namespace agentspeed {

// Speed is never a fact: it names the breakdown derived from the tier, accelerator and highspeed model variant.
enum class ModeKind { ServiceTier, Accelerator, ModelVariant, Routing, Workflow, Effort, Speed };
enum class ModeEvidence { Configured, Selected, Recorded, Served };
enum class ModeScope { Configuration, Session, Turn, Request, Response };

struct ModeFact {
    ModeKind kind = ModeKind::Effort;
    std::string value;
    std::string raw;
    ModeEvidence evidence = ModeEvidence::Recorded;
    std::optional<std::string> provider;
    ModeScope scope = ModeScope::Response;

    // Validates and normalizes (OpenAI tiers: default → standard, priority → fast).
    static std::optional<ModeFact> create(ModeKind kind, const std::optional<std::string>& raw, ModeEvidence evidence,
                                          std::optional<std::string> provider = std::nullopt,
                                          ModeScope scope = ModeScope::Response);
    bool operator==(const ModeFact&) const = default;
};

struct ModeAttribution {
    ModeKind kind = ModeKind::Effort;
    std::string mode = "unknown";
    std::string evidence = "unknown";
    std::optional<std::string> provider;

    bool known() const { return mode != "unknown"; }
    static ModeAttribution unknown(ModeKind kind) { return {kind, "unknown", "unknown", std::nullopt}; }
    bool operator==(const ModeAttribution&) const = default;
};

class ExecutionModes {
public:
    static constexpr std::size_t kMaxFacts = 20;

    ExecutionModes() = default;
    explicit ExecutionModes(std::vector<ModeFact> facts);

    static ExecutionModes of(const std::optional<ModeFact>& fact);
    ExecutionModes merge(const ExecutionModes& newer) const;
    // Adds a fact, replacing the one with the same kind, evidence, provider and scope.
    ExecutionModes with(const std::optional<ModeFact>& fact) const;
    ExecutionModes without(const std::function<bool(const ModeFact&)>& match) const;
    ExecutionModes without(ModeEvidence evidence) const;
    ModeAttribution attribute(ModeKind kind) const;

    const std::vector<ModeFact>& facts() const { return facts_; }
    bool empty() const { return facts_.empty(); }
    bool operator==(const ExecutionModes&) const = default;

    static bool isIdentifier(std::string_view value, std::size_t max);
    static bool isFactKind(ModeKind kind) { return kind != ModeKind::Speed; }
    // A kind some report version carries (version 2 adds `effort`); `speed` never.
    static bool isReportKind(ModeKind kind) { return kind != ModeKind::Speed; }

private:
    ModeAttribution attributeSpeed() const;
    std::vector<ModeFact> facts_;
};

std::string_view modeKindName(ModeKind kind);
std::string_view modeEvidenceName(ModeEvidence evidence);
std::string_view modeScopeName(ModeScope scope);

// Native adapters (EXECUTION-MODES, per source).
namespace native {
ExecutionModes claudeSpeed(const std::optional<std::string>& speed);
ExecutionModes kimiModel(const std::optional<std::string>& model);
ExecutionModes routing(const std::optional<std::string>& model);
ExecutionModes openAiTier(const std::optional<std::string>& tier, ModeEvidence evidence = ModeEvidence::Served,
                          ModeScope scope = ModeScope::Response,
                          const std::optional<std::string>& provider = std::string("openai"));
// `none` is `off`; unresolved tokens give no fact.
std::optional<ModeFact> effort(const std::optional<std::string>& raw, ModeEvidence evidence, ModeScope scope);
ExecutionModes workflow(const std::optional<std::string>& workflow);
ExecutionModes ompTier(const std::optional<std::string>& tier, std::optional<std::string> provider, ModeEvidence evidence,
                       ModeScope scope);
}  // namespace native

// The two values agents show: levels, bolts and display order.
namespace effort_speed {
std::optional<ModeAttribution> effort(const ExecutionModes& modes);
std::optional<ModeAttribution> speed(const ExecutionModes& modes);
// Meter level 0–5; nullopt for a token without a level.
std::optional<int> level(std::string_view effort);
bool isTopRung(std::string_view effort);
int bolts(std::string_view speed);
// Known tokens by rank, other tokens ordinally, `unknown` last.
bool less(ModeKind kind, std::string_view a, std::string_view b);
}  // namespace effort_speed

// `service_tier` of a Codex root's config.toml, as a configured fact (cached by size and modification time).
ExecutionModes codexConfiguredModes(const std::string& root);

}  // namespace agentspeed
