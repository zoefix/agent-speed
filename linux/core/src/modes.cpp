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

ModeAttribution ExecutionModes::attribute(ModeKind kind) const {
    if (kind == ModeKind::Speed) return attributeSpeed();
    const ModeFact* best = nullptr;
    for (const auto& f : facts_) {
        if (f.kind != kind || f.evidence == ModeEvidence::Configured) continue;
        if (!best || f.evidence > best->evidence || (f.evidence == best->evidence && f.scope > best->scope)) best = &f;
    }
    if (!best || best->value == "unknown" || best->value == "none" || best->value == "auto" || best->value == "inherit" ||
        best->value == "inherited") {
        return ModeAttribution::unknown(kind);
    }
    return {kind, best->value, std::string(modeEvidenceName(best->evidence)), best->provider};
}

ModeAttribution ExecutionModes::attributeSpeed() const {
    for (ModeKind kind : {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant}) {
        ModeAttribution value = attribute(kind);
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
    if (v == "auto" || v == "default" || v == "inherit" || v == "inherited" || v == "unknown") return std::nullopt;
    if (v == "none") fact->value = "off";
    return fact;
}

ExecutionModes workflow(const std::optional<std::string>& workflow) {
    if (workflow != "fast" && workflow != "planning") return {};
    return ExecutionModes::of(ModeFact::create(ModeKind::Workflow, workflow, ModeEvidence::Selected, std::nullopt, ModeScope::Turn));
}

ExecutionModes ompTier(const std::optional<std::string>& tier, std::optional<std::string> provider, ModeEvidence evidence,
                       ModeScope scope) {
    if (provider == "openai-codex") provider = "openai";
    auto fact = ModeFact::create(ModeKind::ServiceTier, tier, evidence, provider, scope);
    if (fact && provider == "anthropic" && tier == "priority") fact->value = "fast";
    if (fact && tier == "none") fact->value = "standard";
    return ExecutionModes::of(fact);
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

namespace {

ExecutionModes parseCodexConfig(const std::string& path) {
    static const std::regex tierLine(R"re(^service_tier\s*=\s*(["'])([A-Za-z0-9][A-Za-z0-9._:-]{0,63})\1\s*(?:#.*)?$)re");
    std::ifstream in(path);
    ExecutionModes modes;
    bool found = false;
    std::string line;
    while (std::getline(in, line)) {
        auto begin = line.find_first_not_of(" \t\r\n\v\f");
        auto end = line.find_last_not_of(" \t\r\n\v\f");
        const std::string text = begin == std::string::npos ? std::string() : line.substr(begin, end - begin + 1);
        if (!text.empty() && text[0] == '#') continue;
        if (text.find("\"\"\"") != std::string::npos || text.find("'''") != std::string::npos) return {};
        if (!text.empty() && text[0] == '[') break;
        std::smatch match;
        if (std::regex_match(text, match, tierLine)) {
            if (found) return {};
            modes = native::openAiTier(match[2].str(), ModeEvidence::Configured, ModeScope::Configuration);
            found = true;
        } else if (text.rfind("service_tier", 0) == 0 &&
                   (text.size() == 12 || text[12] == '=' || text[12] == ' ' || text[12] == '\t')) {
            return {};
        }
    }
    return modes;
}

}  // namespace

ExecutionModes codexConfiguredModes(const std::string& root) {
    if (root.empty()) return {};
    std::string base = root;
    while (base.size() > 1 && base.back() == '/') base.pop_back();
    const auto slash = base.find_last_of('/');
    if (slash != std::string::npos && base.substr(slash + 1) == "sessions") base = base.substr(0, slash);
    const std::string config = base + "/config.toml";
    struct stat st {};
    if (lstat(config.c_str(), &st) != 0 || !S_ISREG(st.st_mode) || st.st_size > 1024 * 1024) return {};
    static std::mutex mutex;
    static std::map<std::string, std::tuple<off_t, std::int64_t, ExecutionModes>> cache;
    const std::int64_t modified = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
    std::lock_guard lock(mutex);
    if (auto it = cache.find(config); it != cache.end() && std::get<0>(it->second) == st.st_size &&
                                      std::get<1>(it->second) == modified) {
        return std::get<2>(it->second);
    }
    ExecutionModes modes = parseCodexConfig(config);
    if (cache.size() >= 16) cache.clear();
    cache[config] = {st.st_size, modified, modes};
    return modes;
}

}  // namespace agentspeed
