#include "agentspeed/modes.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <array>
#include <fstream>
#include <map>
#include <mutex>
#include <regex>
#include <tuple>

namespace agentspeed {

namespace {

std::string lower(std::string_view text) {
    std::string out(text);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

bool isAsciiAlnum(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); }

bool validFact(const ModeFact& f) {
    return ExecutionModes::isIdentifier(f.value, 64) && ExecutionModes::isIdentifier(f.raw, 64) &&
           (!f.provider || ExecutionModes::isIdentifier(*f.provider, 32)) && ExecutionModes::isFactKind(f.kind);
}

// Ordinal comparison with a missing provider first, as .NET's ordinal comparer orders null.
int compareProvider(const std::optional<std::string>& a, const std::optional<std::string>& b) {
    if (!a && !b) return 0;
    if (!a) return -1;
    if (!b) return 1;
    return a->compare(*b) < 0 ? -1 : (a->compare(*b) > 0 ? 1 : 0);
}

}  // namespace

std::optional<ModeFact> ModeFact::create(ModeKind kind, const std::optional<std::string>& raw, ModeEvidence evidence,
                                         std::optional<std::string> provider, ModeScope scope) {
    if (!raw || !ExecutionModes::isIdentifier(*raw, 64) || (provider && !ExecutionModes::isIdentifier(*provider, 32)) ||
        !ExecutionModes::isFactKind(kind)) {
        return std::nullopt;
    }
    std::string value = lower(*raw);
    if (kind == ModeKind::ServiceTier && provider == "openai") {
        if (value == "default") value = "standard";
        else if (value == "priority") value = "fast";
    }
    if (kind == ModeKind::ServiceTier && provider == "google" && value == "priority") value = "fast";
    return ModeFact{kind, value, *raw, evidence, std::move(provider), scope};
}

bool ExecutionModes::isIdentifier(std::string_view value, std::size_t max) {
    if (value.empty() || value.size() > max || !isAsciiAlnum(value[0])) return false;
    return std::all_of(value.begin(), value.end(),
                       [](char c) { return isAsciiAlnum(c) || c == '.' || c == '_' || c == ':' || c == '-'; });
}

ExecutionModes::ExecutionModes(std::vector<ModeFact> facts) {
    std::vector<ModeFact> valid;
    for (auto& f : facts) {
        if (!validFact(f)) continue;
        f.value = lower(f.value);
        valid.push_back(std::move(f));
    }
    // One fact per (kind, evidence, scope, provider): the last wins, in the position of its group's first.
    std::vector<ModeFact> grouped;
    for (const auto& f : valid) {
        auto same = std::find_if(grouped.begin(), grouped.end(), [&](const ModeFact& g) {
            return g.kind == f.kind && g.evidence == f.evidence && g.scope == f.scope && g.provider == f.provider;
        });
        if (same != grouped.end()) {
            *same = f;
        } else {
            grouped.push_back(f);
        }
    }
    std::stable_sort(grouped.begin(), grouped.end(), [](const ModeFact& a, const ModeFact& b) {
        if (a.evidence != b.evidence) return a.evidence > b.evidence;
        if (a.scope != b.scope) return a.scope > b.scope;
        if (a.kind != b.kind) return a.kind < b.kind;
        return compareProvider(a.provider, b.provider) < 0;
    });
    if (grouped.size() > kMaxFacts) grouped.resize(kMaxFacts);
    std::stable_sort(grouped.begin(), grouped.end(), [](const ModeFact& a, const ModeFact& b) {
        if (a.kind != b.kind) return a.kind < b.kind;
        if (a.evidence != b.evidence) return a.evidence < b.evidence;
        if (a.scope != b.scope) return a.scope < b.scope;
        return compareProvider(a.provider, b.provider) < 0;
    });
    facts_ = std::move(grouped);
}

ExecutionModes ExecutionModes::of(const std::optional<ModeFact>& fact) {
    return fact ? ExecutionModes(std::vector<ModeFact>{*fact}) : ExecutionModes();
}

ExecutionModes ExecutionModes::merge(const ExecutionModes& newer) const {
    if (newer.empty()) return *this;
    std::vector<ModeFact> all = facts_;
    all.insert(all.end(), newer.facts_.begin(), newer.facts_.end());
    return ExecutionModes(std::move(all));
}

ExecutionModes ExecutionModes::with(const std::optional<ModeFact>& fact) const {
    if (!fact) return *this;
    std::vector<ModeFact> all = facts_;
    all.push_back(*fact);
    return ExecutionModes(std::move(all));
}

ExecutionModes ExecutionModes::without(const std::function<bool(const ModeFact&)>& match) const {
    if (std::none_of(facts_.begin(), facts_.end(), match)) return *this;
    std::vector<ModeFact> kept;
    for (const auto& f : facts_) {
        if (!match(f)) kept.push_back(f);
    }
    return ExecutionModes(std::move(kept));
}

ExecutionModes ExecutionModes::without(ModeEvidence evidence) const {
    return without([evidence](const ModeFact& f) { return f.evidence == evidence; });
}

ModeAttribution ExecutionModes::attribute(ModeKind kind, ModeEvidence lowest, ModeEvidence highest) const {
    if (kind == ModeKind::Speed) return attributeSpeed(lowest, highest);
    const auto eligible = [&](const ModeFact& f) { return f.kind == kind && f.evidence >= lowest && f.evidence <= highest; };
    const ModeFact* best = nullptr;
    for (const auto& f : facts_) {
        if (!eligible(f)) continue;
        if (!best || f.evidence > best->evidence || (f.evidence == best->evidence && f.scope > best->scope)) best = &f;
    }
    if (!best || best->value == "unknown" || best->value == "none" || best->value == "auto" || best->value == "inherit" ||
        best->value == "inherited") {
        return ModeAttribution::unknown(kind);
    }
    for (const auto& f : facts_) {
        if (eligible(f) && f.evidence == best->evidence && f.scope == best->scope &&
            (f.value != best->value || f.provider != best->provider)) {
            return ModeAttribution::unknown(kind);
        }
    }
    return {kind, best->value, std::string(modeEvidenceName(best->evidence)), best->provider};
}

ModeAttribution ExecutionModes::attributeSpeed(ModeEvidence lowest, ModeEvidence highest) const {
    for (ModeKind kind : {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant}) {
        ModeAttribution value = attribute(kind, lowest, highest);
        if (value.known() && (kind != ModeKind::ModelVariant || value.mode == "highspeed")) {
            value.kind = ModeKind::Speed;
            return value;
        }
    }
    return ModeAttribution::unknown(ModeKind::Speed);
}

std::string_view modeKindName(ModeKind kind) {
    switch (kind) {
        case ModeKind::ServiceTier: return "serviceTier";
        case ModeKind::ModelVariant: return "modelVariant";
        case ModeKind::Accelerator: return "accelerator";
        case ModeKind::Routing: return "routing";
        case ModeKind::Effort: return "effort";
        case ModeKind::Speed: return "speed";
        case ModeKind::Workflow: break;
    }
    return "workflow";
}

std::string_view modeEvidenceName(ModeEvidence evidence) {
    switch (evidence) {
        case ModeEvidence::Configured: return "configured";
        case ModeEvidence::Selected: return "selected";
        case ModeEvidence::Recorded: return "recorded";
        case ModeEvidence::Served: break;
    }
    return "served";
}

std::string_view modeScopeName(ModeScope scope) {
    switch (scope) {
        case ModeScope::Configuration: return "configuration";
        case ModeScope::Session: return "session";
        case ModeScope::Turn: return "turn";
        case ModeScope::Request: return "request";
        case ModeScope::Response: break;
    }
    return "response";
}

namespace native {

ExecutionModes claudeSpeed(const std::optional<std::string>& speed) {
    if (speed != "fast" && speed != "standard") return {};
    return ExecutionModes::of(ModeFact::create(ModeKind::ServiceTier, speed, ModeEvidence::Served, "anthropic"));
}

ExecutionModes kimiModel(const std::optional<std::string>& model) {
    if (model != "k3.1-pro-highspeed" && model != "kimi-for-coding-highspeed") return {};
    return ExecutionModes::of(
        ModeFact{ModeKind::ModelVariant, "highspeed", *model, ModeEvidence::Selected, std::string("kimi"), ModeScope::Request});
}

ExecutionModes routing(const std::optional<std::string>& model) {
    if (model != "fast-model" && model != "balanced-model" && model != "deep-model" && model != "auto") return {};
    const std::string value = *model == "auto" ? *model : model->substr(0, model->size() - 6);
    return ExecutionModes::of(ModeFact{ModeKind::Routing, value, *model, ModeEvidence::Selected, std::nullopt, ModeScope::Request});
}

ExecutionModes openAiTier(const std::optional<std::string>& tier, ModeEvidence evidence, ModeScope scope,
                          const std::optional<std::string>& provider) {
    if (provider != "openai" && provider != "azure") return {};
    return ExecutionModes::of(ModeFact::create(ModeKind::ServiceTier, tier, evidence, std::string("openai"), scope));
}

std::optional<ModeFact> effort(const std::optional<std::string>& raw, ModeEvidence evidence, ModeScope scope) {
    auto fact = ModeFact::create(ModeKind::Effort, raw, evidence, std::nullopt, scope);
    if (!fact) return std::nullopt;
    const auto& v = fact->value;
    if (v == "auto" || v == "default" || v == "inherit" || v == "inherited" || v == "unknown" || v == "thinking_level_unspecified") {
        return std::nullopt;
    }
    if (v == "none" || v == "disabled") fact->value = "off";
    else if (v == "enabled") fact->value = "on";
    return fact;
}

ExecutionModes workflow(const std::optional<std::string>& workflow) {
    if (workflow != "fast" && workflow != "planning") return {};
    return ExecutionModes::of(ModeFact::create(ModeKind::Workflow, workflow, ModeEvidence::Selected, std::nullopt, ModeScope::Turn));
}

std::optional<std::string> ompFamily(const std::optional<std::string>& provider) {
    static const std::map<std::string, std::string, std::less<>> kFamilies = {
        {"anthropic", "anthropic"}, {"openai", "openai"}, {"openai-codex", "openai"}, {"azure", "openai"},
        {"azure-openai-responses", "openai"}, {"google", "google"}, {"google-gemini-cli", "google"},
        {"google-antigravity", "google"}, {"google-vertex", "google"}};
    if (!provider) return std::nullopt;
    const auto family = kFamilies.find(lower(*provider));
    if (family == kFamilies.end()) return std::nullopt;
    return family->second;
}

ExecutionModes ompTier(const std::optional<std::string>& tier, std::optional<std::string> provider, ModeEvidence evidence,
                       ModeScope scope) {
    const auto family = ompFamily(provider);
    if (!family) return {};
    auto fact = ModeFact::create(ModeKind::ServiceTier, tier, evidence, family, scope);
    if (fact && fact->value == "priority") fact->value = "fast";
    return ExecutionModes::of(fact);
}

ExecutionModes googleTier(const std::optional<std::string>& tier, ModeEvidence evidence, ModeScope scope) {
    auto fact = ModeFact::create(ModeKind::ServiceTier, tier, evidence, std::string("google"), scope);
    if (fact && fact->value == "unspecified") return {};
    return ExecutionModes::of(fact);
}

ExecutionModes zcodeModel(const std::optional<std::string>& model, ModeEvidence evidence, ModeScope scope) {
    static const std::array<std::string_view, 7> kModels = {
        "GLM-5.1-Highspeed", "MiniMax-M2.7-highspeed", "MiniMax-M2.5-highspeed", "kimi-k2.7-code-highspeed",
        "kimi-for-coding-highspeed", "kimi-k3.1-pro-highspeed", "k3.1-pro-highspeed"};
    if (!model || std::find(kModels.begin(), kModels.end(), *model) == kModels.end()) return {};
    return ExecutionModes::of(ModeFact{ModeKind::ModelVariant, "highspeed", *model, evidence, std::string("zcode"), scope});
}

ExecutionModes zcodeProvider(const std::optional<std::string>& account, ModeEvidence evidence, ModeScope scope) {
    if (account != "account:zai-highspeed-card" && account != "account:bigmodel-highspeed-card") return {};
    return ExecutionModes::of(ModeFact{ModeKind::Accelerator, "highspeed", *account, evidence, std::string("zcode"), scope});
}

ExecutionModes droidModel(const std::optional<std::string>& model, ModeEvidence evidence, ModeScope scope) {
    struct Tier {
        std::string_view id, tier, provider;
    };
    static constexpr std::array<Tier, 20> kTiers = {{
        {"claude-opus-4-6-fast", "fast", "anthropic"}, {"claude-opus-4-7-fast", "fast", "anthropic"},
        {"claude-opus-4-8-fast", "fast", "anthropic"}, {"claude-opus-5-fast", "fast", "anthropic"},
        {"claude-opus-5-5-fast", "fast", "anthropic"}, {"gpt-5.3-codex-fast", "fast", "openai"},
        {"gpt-5.4-fast", "fast", "openai"}, {"gpt-5.4-mini-fast", "fast", "openai"}, {"gpt-5.5-fast", "fast", "openai"},
        {"gpt-5.6-fast", "fast", "openai"}, {"gpt-5.6-sol-fast", "fast", "openai"}, {"gpt-6-sol-fast", "fast", "openai"},
        {"gpt-6-luna-fast", "fast", "openai"}, {"gpt-6-astra-ultrafast", "ultrafast", "openai"},
        {"gpt-6.1-sol-ultrafast", "ultrafast", "openai"}, {"gpt-5.6-terra-flex", "flex", "openai"},
        {"gpt-5.6-luna-flex", "flex", "openai"}, {"gpt-6-sol-flex", "flex", "openai"}, {"gpt-6-astra-flex", "flex", "openai"},
        {"gpt-6-luna-flex", "flex", "openai"},
    }};
    if (!model) return {};
    for (const auto& t : kTiers) {
        if (t.id == *model) {
            return ExecutionModes::of(
                ModeFact{ModeKind::ServiceTier, std::string(t.tier), *model, evidence, std::string(t.provider), scope});
        }
    }
    return {};
}

ExecutionModes hermesEffort(const std::optional<std::string>& raw, ModeEvidence evidence, ModeScope scope) {
    if (!raw || !ExecutionModes::isIdentifier(*raw, 64)) return {};
    const auto token = lower(*raw);
    const bool off = token == "none" || token == "false" || token == "disabled";
    if (!off && token != "minimal" && token != "low" && token != "medium" && token != "high" && token != "xhigh" &&
        token != "max" && token != "ultra") {
        return {};
    }
    return ExecutionModes::of(ModeFact{ModeKind::Effort, off ? "off" : token, *raw, evidence, std::nullopt, scope});
}

ExecutionModes hermesServiceTier(const std::optional<std::string>& raw, ModeEvidence evidence, ModeScope scope) {
    static const std::map<std::string, std::string, std::less<>> kTiers = {
        {"fast", "fast"}, {"priority", "fast"}, {"ultrafast", "ultrafast"}, {"ultra", "ultrafast"}, {"normal", "standard"},
        {"default", "standard"}, {"standard", "standard"}, {"off", "standard"}, {"none", "standard"}};
    if (!raw || !ExecutionModes::isIdentifier(*raw, 64)) return {};
    const auto tier = kTiers.find(lower(*raw));
    if (tier == kTiers.end()) return {};
    return ExecutionModes::of(ModeFact{ModeKind::ServiceTier, tier->second, *raw, evidence, std::nullopt, scope});
}

std::optional<std::pair<std::string, std::string>> antigravityModelEffort(std::string_view model) {
    for (std::string_view suffix : {"-extra-low", "-low", "-medium", "-high", "-max"}) {
        if (model.size() > suffix.size() && model.substr(model.size() - suffix.size()) == suffix) {
            return std::pair{std::string(model.substr(0, model.size() - suffix.size())), std::string(suffix.substr(1))};
        }
    }
    return std::nullopt;
}

std::optional<std::string> antigravityLabelEffort(std::string_view label) {
    for (std::string_view word : {"Low", "Medium", "High", "Max"}) {
        const std::string tail = " (" + std::string(word) + ")";
        if (label.size() > tail.size() && label.substr(label.size() - tail.size()) == tail) return std::string(word);
    }
    return std::nullopt;
}

}  // namespace native

namespace effort_speed {

namespace {

constexpr std::array<std::string_view, 9> kEffortOrder = {"ultracode", "ultra", "max",     "xhigh", "high",
                                                          "medium",    "low",   "minimal", "off"};
constexpr std::array<std::string_view, 4> kSpeedOrder = {"ultrafast", "fast", "highspeed", "standard"};

template <std::size_t N>
std::size_t rank(const std::array<std::string_view, N>& order, std::string_view value) {
    if (value == "unknown") return N + 1;
    for (std::size_t i = 0; i < N; ++i) {
        if (order[i] == value) return i;
    }
    return N;
}

}  // namespace

std::optional<ModeAttribution> effort(const ExecutionModes& modes) {
    auto a = modes.attribute(ModeKind::Effort);
    return a.known() ? std::optional<ModeAttribution>(a) : std::nullopt;
}

ModeAttribution current(ModeKind kind, const ExecutionModes& own, const ExecutionModes& session,
                        const ExecutionModes& configured) {
    if (auto value = own.attribute(kind); value.known()) return value;
    if (auto value = session.attribute(kind, ModeEvidence::Selected, ModeEvidence::Selected); value.known()) return value;
    if (auto value = configured.attribute(kind, ModeEvidence::Configured, ModeEvidence::Configured); value.known()) return value;
    return ModeAttribution::unknown(kind);
}

ModeAttribution ledgerEffort(const ModeAttribution& frozen, const ModeAttribution& current) {
    if (frozen.known() || !current.known() || current.evidence == "configured") return frozen;
    return {ModeKind::Effort, current.mode, current.evidence, std::nullopt};
}

std::optional<ModeAttribution> speed(const ExecutionModes& modes) {
    auto a = modes.attribute(ModeKind::Speed);
    return a.known() ? std::optional<ModeAttribution>(a) : std::nullopt;
}

std::optional<int> level(std::string_view e) {
    if (e == "off") return 0;
    if (e == "minimal" || e == "low") return 1;
    if (e == "medium") return 2;
    if (e == "high") return 3;
    if (e == "xhigh") return 4;
    if (e == "max" || e == "ultra" || e == "ultracode") return 5;
    return std::nullopt;
}

bool isTopRung(std::string_view e) { return e == "ultra" || e == "ultracode"; }

int bolts(std::string_view s) {
    if (s == "fast" || s == "highspeed") return 1;
    if (s == "ultrafast") return 2;
    return 0;
}

bool less(ModeKind kind, std::string_view a, std::string_view b) {
    const auto ra = kind == ModeKind::Effort ? rank(kEffortOrder, a) : rank(kSpeedOrder, a);
    const auto rb = kind == ModeKind::Effort ? rank(kEffortOrder, b) : rank(kSpeedOrder, b);
    return ra != rb ? ra < rb : a < b;
}

}  // namespace effort_speed

}  // namespace agentspeed
