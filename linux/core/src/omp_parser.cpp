#include "agentspeed/omp_parser.hpp"

#include <algorithm>
#include <cmath>
#include <fstream>

#include "agentspeed/json.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

constexpr std::int64_t kCopiedHistoryMs = 2000;

enum class Kind { Other, Title, Session, Message, ModelUsage, SessionExit, ServiceTierChange };
enum class Role { Other, User, Developer, Assistant, ToolResult };
enum class Stop { Aborted, Error, Stop, ToolUse, Length, Other };

std::optional<double> finite(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    const auto d = v.number();
    return d && std::isfinite(*d) ? d : std::nullopt;
}

std::int64_t truncate(double value) {
    constexpr double kTwoTo63 = 9'223'372'036'854'775'808.0;
    if (value >= kTwoTo63) return INT64_MAX;
    if (value <= -kTwoTo63) return INT64_MIN;
    return static_cast<std::int64_t>(std::trunc(value));
}

}  // namespace

struct OmpParser::Line {
    Kind kind = Kind::Other;
    std::optional<std::string> id, parentId, cwd;
    std::optional<ExecutionModes> selectedModes;
    std::optional<std::int64_t> timestampMs;
    Role role = Role::Other;
    // The call fields (assistant message or model_usage entry).
    bool guarded = false, hasUsage = false, disabledPriority = false;
    std::optional<std::string> model, provider, serviceTier;
    TokenCounts tokens;
    std::optional<std::int64_t> startedMs, durationMs, completedAtMs;
    Stop stop = Stop::Aborted;
    bool pauseTurn = false;
    std::vector<std::string> toolCallIds;
    std::optional<std::string> toolCallId;
    bool finalYield = false;
};

namespace {

using Line = OmpParser::Line;

// model, provider, api and usage: on assistant messages and on model_usage entries.
void readCall(const json::Value& o, Line& line, std::optional<std::string>* topTier) {
    const auto model = o["model"];
    line.model = model.isString() ? model.string() : std::nullopt;
    const auto provider = o["provider"];
    const bool hasProvider = provider.isString();
    line.provider = hasProvider && !provider.raw().empty() ? provider.string() : std::nullopt;
    if (const auto tier = o["serviceTier"]; tier.valid() && !topTier) line.serviceTier = tier.nonEmptyString();
    if (const auto disabled = o["disabledFeatures"]; disabled.isArray()) {
        for (const auto& f : disabled.elements()) {
            if (f.equals("priority")) line.disabledPriority = true;
        }
    }
    const bool hasApi = o["api"].isString();
    const auto usage = o["usage"];
    const bool hasUsageObject = usage.isObject();
    std::int64_t input = 0, output = 0, cacheRead = 0, cacheWrite = 0, reasoning = 0;
    if (hasUsageObject) {
        input = usage["input"].count();
        output = usage["output"].count();
        cacheRead = usage["cacheRead"].count();
        cacheWrite = usage["cacheWrite"].count();
        reasoning = usage["reasoningTokens"].count();
    }
    line.guarded = line.model.has_value() && hasProvider && hasApi && hasUsageObject;
    line.hasUsage = input > 0 || output > 0 || cacheRead > 0 || cacheWrite > 0;
    line.tokens.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, cacheRead), cacheWrite);
    line.tokens.cachedInput = cacheRead;
    line.tokens.cacheWriteInput = cacheWrite;
    line.tokens.output = output;
    line.tokens.reasoning = reasoning;
}

std::optional<Line> readLine(std::string_view text, bool& malformed) {
    malformed = false;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) {
        malformed = true;
        return std::nullopt;
    }
    const auto root = doc.root();
    Line line;
    const auto type = root["type"];
    enum class Type { Other, Title, Session, Message, ModelUsage, Custom, ServiceTierChange } t = Type::Other;
    if (type.equals("message")) t = Type::Message;
    else if (type.equals("session")) t = Type::Session;
    else if (type.equals("title")) t = Type::Title;
    else if (type.equals("service_tier_change")) t = Type::ServiceTierChange;
    else if (type.equals("model_usage")) t = Type::ModelUsage;
    else if (type.equals("custom")) t = Type::Custom;
    const auto id = root["id"];
    line.id = id.isString() ? id.string() : std::nullopt;
    if (const auto parent = root["parentId"]; parent.valid()) line.parentId = parent.nonEmptyString();
    std::optional<std::string> topTier;
    if (const auto tier = root["serviceTier"]; tier.valid()) {
        ExecutionModes modes;
        if (tier.isObject()) {
            for (const auto& [family, value] : tier.members()) {
                if (family == "openai" || family == "anthropic" || family == "google") {
                    modes = modes.merge(native::ompTier(value.nonEmptyString(), family, ModeEvidence::Selected, ModeScope::Session));
                }
            }
        } else if (tier.isString()) {
            topTier = tier.string();
        }
        line.selectedModes = modes;
    }
    if (const auto ts = root["timestamp"]; ts.isString()) line.timestampMs = parseTimestamp(*ts.string());
    const auto cwd = root["cwd"];
    line.cwd = cwd.isString() ? cwd.string() : std::nullopt;
    const auto message = root["message"];
    const bool hasMessage = message.isObject();
    const bool sessionExit = root["customType"].equals("session_exit");

    switch (t) {
        case Type::Title: line.kind = Kind::Title; break;
        case Type::Session: line.kind = Kind::Session; break;
        case Type::Message: line.kind = hasMessage ? Kind::Message : Kind::Other; break;
        case Type::ModelUsage: line.kind = Kind::ModelUsage; break;
        case Type::ServiceTierChange: line.kind = Kind::ServiceTierChange; break;
        case Type::Custom: line.kind = sessionExit ? Kind::SessionExit : Kind::Other; break;
        case Type::Other: line.kind = Kind::Other; break;
    }
    if (line.kind == Kind::Message) {
        const auto role = message["role"];
        line.role = role.equals("assistant")    ? Role::Assistant
                    : role.equals("user")       ? Role::User
                    : role.equals("toolResult") ? Role::ToolResult
                    : role.equals("developer")  ? Role::Developer
                                                : Role::Other;
        if (auto started = finite(message["timestamp"]); started && *started > 0) line.startedMs = truncate(*started);
        if (auto duration = finite(message["duration"]); duration && *duration >= 0) line.durationMs = truncate(*duration);
        if (auto completed = finite(message["completedAt"]); completed && *completed > 0) line.completedAtMs = truncate(*completed);
        const auto stop = message["stopReason"];
        line.stop = !stop.isString()             ? Stop::Aborted
                    : stop.equals("stop")       ? Stop::Stop
                    : stop.equals("toolUse")    ? Stop::ToolUse
                    : stop.equals("length")     ? Stop::Length
                    : stop.equals("error")      ? Stop::Error
                    : stop.equals("aborted")    ? Stop::Aborted
                                                : Stop::Other;
        if (const auto details = message["stopDetails"]; details.isObject()) line.pauseTurn = details["type"].equals("pause_turn");
        if (const auto content = message["content"]; content.isArray()) {
            for (const auto& block : content.elements()) {
                if (!block.isObject() || !block["type"].equals("toolCall")) continue;
                const auto blockId = block["id"];
                if (!blockId.isString()) continue;
                auto value = *blockId.string();
                if (std::find(line.toolCallIds.begin(), line.toolCallIds.end(), value) == line.toolCallIds.end()) {
                    line.toolCallIds.push_back(std::move(value));
                }
            }
        }
        const auto callId = message["toolCallId"];
        line.toolCallId = callId.isString() ? callId.string() : std::nullopt;
        const bool yield = message["toolName"].equals("yield");
        line.finalYield = yield && !message["isError"].isTrue();
        readCall(message, line, nullptr);
    } else if (line.kind == Kind::ModelUsage) {
        readCall(root, line, nullptr);
        if (topTier) line.serviceTier = topTier;
    }
    if (line.kind != Kind::ServiceTierChange) line.selectedModes.reset();
    return line;
}

}  // namespace

int depthBelow(const std::string& root, const std::string& path) {
    std::string base = root;
    while (base.size() > 1 && base.back() == '/') base.pop_back();
    if (path.size() <= base.size() || path.compare(0, base.size(), base) != 0 || path[base.size()] != '/') return 0;
    int depth = 0;
    bool inPart = false;
    for (std::size_t i = base.size() + 1; i < path.size(); ++i) {
        if (path[i] == '/') {
            inPart = false;
        } else if (!inPart) {
            inPart = true;
            ++depth;
        }
    }
    return depth;
}

OmpParser::OmpParser(std::string agentId, Deduper& deduper, const std::string& root)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Omp),
      pathIsSubagent_(depthBelow(root, this->agentId()) >= 3) {
    setSource(SourceKind::Omp);
    setSubagent(pathIsSubagent_);
}

std::optional<std::string> OmpParser::parentPath() const {
    if (!pathIsSubagent_) return std::nullopt;
    const auto cut = agentId().find_last_of('/');
    if (cut == std::string::npos || cut == 0) return std::nullopt;
    return agentId().substr(0, cut) + ".jsonl";
}

AgentLink OmpParser::link() const { return {agentId(), LinkKind::OmpPath, std::nullopt, parentPath(), headerMs_, 0}; }

void OmpParser::reset() {
    TranscriptParser::reset();
    pendingToolCalls_.clear();
    responses_.clear();
    modeBranches_.clear();
    branchModes_ = ExecutionModes();
    headerSeen_ = false;
    headerProbed_ = false;
    headerMs_.reset();
    setSource(SourceKind::Omp);
    setSubagent(pathIsSubagent_);
}

void OmpParser::probeHeader(ResponseSink& sink) {
    if (headerSeen_ || headerProbed_) return;
    headerProbed_ = true;
    std::ifstream in(agentId(), std::ios::binary);
    std::string first;
    for (int i = 0; i < 2 && std::getline(in, first); ++i) {
        if (!first.empty() && first.back() == '\r') first.pop_back();
        bool malformed = false;
        if (auto line = readLine(first, malformed); line && line->kind == Kind::Session) {
            apply(*line, sink);
            observeText(first);
            return;
        }
    }
}

void OmpParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    bool malformed = false;
    auto line = readLine(text, malformed);
    if (malformed) {
        countMalformed();
        return;
    }
    apply(*line, sink);
    observeText(text);
}

void OmpParser::apply(const Line& line, ResponseSink& sink) {
    if (line.kind == Kind::Title) return;
    if (line.kind == Kind::Session) {
        if (headerSeen_) return;
        headerSeen_ = true;
        headerMs_ = line.timestampMs;
        setProjectFromCwd(line.cwd);
        markChanged();
        if (line.timestampMs) touch(*line.timestampMs);
        return;
    }
    if (!headerSeen_ || !line.id || !line.timestampMs || (headerMs_ && *line.timestampMs < *headerMs_ - kCopiedHistoryMs)) {
        return;
    }
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    if (line.selectedModes) {
        branchModes_ = *line.selectedModes;
    } else if (line.parentId && modeBranches_.contains(*line.parentId)) {
        branchModes_ = modeBranches_[*line.parentId];
    } else {
        branchModes_ = ExecutionModes();
    }
    if (modeBranches_.size() >= 65536) modeBranches_.clear();
    modeBranches_[*line.id] = branchModes_;
    setModes(branchModes_);
    switch (line.kind) {
        case Kind::Message:
            switch (line.role) {
                case Role::User:
                case Role::Developer:
                    pendingToolCalls_.clear();
                    setPhase(Phase::Generating, ts, ts);
                    break;
                case Role::Assistant:
                    applyAssistant(line, ts, sink);
                    break;
                case Role::ToolResult:
                    if (line.toolCallId) pendingToolCalls_.erase(*line.toolCallId);
                    if (line.finalYield) {
                        setPhase(Phase::Idle, ts, std::nullopt);
                    } else if (phase() == Phase::Tools && pendingToolCalls_.empty()) {
                        setPhase(Phase::Generating, ts, ts);
                    }
                    break;
                case Role::Other:
                    break;
            }
            break;
        case Kind::ModelUsage: {
            // Side calls count in totals only and never set the agent's model.
            if (!line.guarded) break;
            const std::string key = "omp:" + *line.id + ":" + std::to_string(ts);
            if (auto owner = deduper().owner(key); owner && *owner != agentId()) break;
            if (line.hasUsage) emitResponse(key, std::nullopt, ts, line, sink);
            break;
        }
        case Kind::SessionExit:
            pendingToolCalls_.clear();
            setPhase(Phase::Idle, ts, std::nullopt);
            break;
        default:
            break;
    }
}

void OmpParser::applyAssistant(const Line& line, std::int64_t ts, ResponseSink& sink) {
    const std::string key = "omp:" + *line.id + ":" + std::to_string(line.startedMs.value_or(ts));
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    if (!line.guarded) return;
    const bool failed = line.stop == Stop::Error || line.stop == Stop::Aborted;
    std::int64_t end = ts;
    if (line.startedMs && line.durationMs) {
        end = *line.durationMs > INT64_MAX - *line.startedMs ? INT64_MAX : *line.startedMs + *line.durationMs;
    } else if (line.completedAtMs) {
        end = *line.completedAtMs;
    }
    if (line.hasUsage) {
        emitResponse(key, failed ? std::nullopt : line.startedMs, end, line, sink);
        setModel(line.model);
    }
    if (failed) {
        pendingToolCalls_.clear();
        setPhase(Phase::Idle, ts, std::nullopt);
    } else if (!line.toolCallIds.empty() &&
               (line.stop == Stop::ToolUse || line.stop == Stop::Stop || line.stop == Stop::Length)) {
        pendingToolCalls_.clear();
        pendingToolCalls_.insert(line.toolCallIds.begin(), line.toolCallIds.end());
        setPhase(Phase::Tools, ts, std::nullopt);
    } else if (line.stop == Stop::Stop && line.pauseTurn) {
        setPhase(Phase::Generating, ts, end);
    } else {
        setPhase(Phase::Idle, ts, std::nullopt);
    }
}

void OmpParser::emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs,
                             const Line& line, ResponseSink& sink) {
    const bool completion = deduper().tryClaim(key, agentId());
    const std::optional<std::string> family = line.provider == "openai-codex" ? std::optional<std::string>("openai") : line.provider;
    std::vector<ModeFact> selected;
    for (const auto& f : branchModes_.facts()) {
        if (f.provider == family) selected.push_back(f);
    }
    const ExecutionModes recorded =
        line.disabledPriority ? ExecutionModes() : native::ompTier(line.serviceTier, family, ModeEvidence::Recorded, ModeScope::Response);
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Omp;
    r.agentId = agentId();
    r.model = line.model;
    r.startMs = startMs;
    r.endMs = endMs;
    r.tokens = line.tokens;
    r.provider = line.provider;
    r.modes = ExecutionModes(selected).merge(recorded);
    setModes(r.modes);
    if (auto previous = responses_.find(key); previous != responses_.end()) {
        const auto& p = previous->second;
        if (p.startMs == r.startMs && p.endMs == r.endMs && p.tokens == r.tokens && p.model == r.model &&
            p.provider == r.provider && p.modes == r.modes) {
            return;
        }
    }
    responses_[key] = r;
    sink.onResponse(r, completion);
}

}  // namespace agentspeed
