#include "agentspeed/pi_parser.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <cmath>
#include <fstream>

#include "agentspeed/json.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace pi {

namespace {

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

std::optional<std::string> token(const json::Value& v) { return v.isString() ? v.string() : std::nullopt; }

// model, provider, api and usage: on assistant messages, tool results and usage entries.
Call readCall(const json::Value& o) {
    Call c;
    if (const auto model = o["model"]; model.isString()) c.model = model.string();
    const auto provider = o["provider"];
    c.hasProvider = provider.isString();
    if (c.hasProvider && !provider.raw().empty()) c.provider = provider.string();
    const auto api = o["api"];
    c.hasApi = api.isString();
    c.apiIsCli = api.equals("cli");
    const auto usage = o["usage"];
    c.hasUsageObject = usage.isObject();
    if (c.hasUsageObject) {
        const auto input = usage["input"].count(), output = usage["output"].count();
        const auto cacheRead = usage["cacheRead"].count(), cacheWrite = usage["cacheWrite"].count();
        const auto cacheWrite1h = usage["cacheWrite1h"].count(), reasoning = usage["reasoning"].count();
        c.reasoningTokens = usage["reasoningTokens"].count();
        if (const auto report = usage["usageReport"]; report.isObject()) c.usageUnavailable = report["state"].equals("unavailable");
        c.hasUsage = input > 0 || output > 0 || cacheRead > 0 || cacheWrite > 0;
        c.tokens.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, cacheRead), cacheWrite);
        c.tokens.cachedInput = cacheRead;
        c.tokens.cacheWriteInput = cacheWrite;
        c.tokens.output = output;
        c.tokens.reasoning = reasoning;
        c.tokens.cacheWrite1hInput = std::min(cacheWrite1h, cacheWrite);
    }
    return c;
}

}  // namespace

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
    const auto message = root["message"];
    if (type.equals("session")) line.kind = Kind::Session;
    else if (type.equals("message")) line.kind = message.isObject() ? Kind::Message : Kind::Other;
    else if (type.equals("usage")) line.kind = Kind::Usage;
    else if (type.equals("compaction") || type.equals("branch_summary")) line.kind = Kind::Summary;
    else if (type.equals("thinking_level_change")) line.kind = Kind::ThinkingLevelChange;
    if (const auto id = root["id"]; id.isString()) line.id = id.string();
    if (const auto ts = root["timestamp"]; ts.isString()) line.timestampMs = parseTimestamp(*ts.string());
    if (const auto cwd = root["cwd"]; cwd.isString()) line.cwd = cwd.string();
    switch (line.kind) {
        case Kind::Message: {
            const auto role = message["role"];
            line.role = role.equals("assistant")    ? Role::Assistant
                        : role.equals("user")       ? Role::User
                        : role.equals("toolResult") ? Role::ToolResult
                        : role.equals("developer")  ? Role::Developer
                                                    : Role::Other;
            if (auto started = finite(message["timestamp"]); started && *started > 0) line.startedMs = truncate(*started);
            if (auto duration = finite(message["durationMs"]); duration && *duration >= 0) line.durationMs = truncate(*duration);
            const auto stop = message["stopReason"];
            line.stop = !stop.isString()           ? Stop::Aborted
                        : stop.equals("stop")     ? Stop::Stop
                        : stop.equals("toolUse")  ? Stop::ToolUse
                        : stop.equals("length")   ? Stop::Length
                        : stop.equals("error")    ? Stop::Error
                        : stop.equals("aborted")  ? Stop::Aborted
                        : stop.equals("pending")  ? Stop::Pending
                        : stop.equals("deferred") ? Stop::Deferred
                                                  : Stop::Other;
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
            if (const auto callId = message["toolCallId"]; callId.isString()) line.toolCallId = callId.string();
            line.thinkingLevel = token(message["thinkingLevel"]);
            if (const auto mirror = message["__openclaw"]; mirror.isObject()) {
                const auto origin = mirror["mirrorOrigin"];
                line.mirrored = origin.isString() && !origin.raw().empty();
            }
            line.call = readCall(message);
            break;
        }
        case Kind::Usage:
        case Kind::Summary:
            line.call = readCall(root);
            break;
        case Kind::ThinkingLevelChange:
            line.thinkingLevel = token(root["thinkingLevel"]);
            break;
        default:
            break;
    }
    return line;
}

std::optional<std::string> parentFileOf(const std::string& path) {
    const auto cut = path.find_last_of('/');
    if (cut == std::string::npos || cut == 0) return std::nullopt;
    const std::string candidate = path.substr(0, cut) + ".jsonl";
    struct stat st {};
    if (lstat(candidate.c_str(), &st) != 0 || !S_ISREG(st.st_mode)) return std::nullopt;
    return candidate;
}

}  // namespace pi

PiParser::PiParser(std::string agentId, Deduper& deduper, std::optional<std::string> parentPath)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Pi), parentPath_(std::move(parentPath)) {
    setSource(SourceKind::Pi);
    setSubagent(parentPath_.has_value());
}

std::unique_ptr<PiParser> PiParser::forPath(const std::string& path, Deduper& deduper) {
    return std::make_unique<PiParser>(path, deduper, pi::parentFileOf(path));
}

AgentLink PiParser::link() const { return {agentId(), LinkKind::PiPath, std::nullopt, parentPath_, headerMs_, 0}; }

void PiParser::reset() {
    TranscriptParser::reset();
    pendingToolCalls_.clear();
    responses_.clear();
    context_ = ExecutionModes();
    headerSeen_ = false;
    headerProbed_ = false;
    headerMs_.reset();
    setSource(SourceKind::Pi);
    setSubagent(parentPath_.has_value());
}

void PiParser::probeHeader(ResponseSink& sink) {
    if (headerSeen_ || headerProbed_) return;
    headerProbed_ = true;
    std::ifstream in(agentId(), std::ios::binary);
    std::string first;
    for (int i = 0; i < 2 && std::getline(in, first); ++i) {
        if (!first.empty() && first.back() == '\r') first.pop_back();
        bool malformed = false;
        if (auto line = pi::readLine(first, malformed); line && line->kind == pi::Kind::Session) {
            apply(*line, sink);
            observeText(first);
            return;
        }
    }
}

void PiParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    bool malformed = false;
    auto line = pi::readLine(text, malformed);
    if (malformed) {
        countMalformed();
        return;
    }
    apply(*line, sink);
    observeText(text);
}

void PiParser::apply(const pi::Line& line, ResponseSink& sink) {
    if (line.kind == pi::Kind::Session) {
        if (headerSeen_) return;
        headerSeen_ = true;
        headerMs_ = line.timestampMs;
        setProjectFromCwd(line.cwd);
        markChanged();
        if (line.timestampMs) touch(*line.timestampMs);
        return;
    }
    if (!headerSeen_ || !line.id || !line.timestampMs || (headerMs_ && *line.timestampMs < *headerMs_ - 2000)) return;
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    switch (line.kind) {
        case pi::Kind::Message:
            switch (line.role) {
                case pi::Role::User:
                case pi::Role::Developer:
                    pendingToolCalls_.clear();
                    setPhase(Phase::Generating, ts, ts);
                    break;
                case pi::Role::Assistant:
                    applyAssistant(line, ts, sink);
                    break;
                case pi::Role::ToolResult:
                    // A tool's own model calls (METRICS 4.13); tool results never end a run in Pi (no yield).
                    applySide(*line.id, ts, line.call, std::nullopt, std::nullopt, sink);
                    if (line.toolCallId) pendingToolCalls_.erase(*line.toolCallId);
                    if (phase() == Phase::Tools && pendingToolCalls_.empty()) setPhase(Phase::Generating, ts, ts);
                    break;
                case pi::Role::Other:
                    break;
            }
            break;
        case pi::Kind::Usage:
            if (line.call.guardedEntry()) applySide(*line.id, ts, line.call, line.call.model, line.call.provider, sink);
            break;
        case pi::Kind::Summary:
            applySide(*line.id, ts, line.call, std::nullopt, std::nullopt, sink);
            break;
        case pi::Kind::ThinkingLevelChange:
            selectEffort(line.thinkingLevel, ModeEvidence::Selected, ModeScope::Session);
            break;
        default:
            break;
    }
}

void PiParser::applyAssistant(const pi::Line& line, std::int64_t ts, ResponseSink& sink) {
    const std::string key = "pi:" + *line.id + ":" + std::to_string(line.startedMs.value_or(ts));
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    const auto& call = line.call;
    if (!call.guardedMessage()) return;
    std::int64_t end = ts;
    if (line.startedMs && line.durationMs) {
        end = *line.durationMs > INT64_MAX - *line.startedMs ? INT64_MAX : *line.startedMs + *line.durationMs;
    }
    selectEffort(line.thinkingLevel, ModeEvidence::Recorded, ModeScope::Request);
    if (call.hasUsage) {
        emitResponse(key, line.failed() ? std::nullopt : line.startedMs, end, call, call.model, call.provider, context_, sink);
        setModel(call.model);
    }
    if (line.failed()) {
        pendingToolCalls_.clear();
        setPhase(Phase::Idle, ts, std::nullopt);
    } else if (!line.toolCallIds.empty() &&
               (line.stop == pi::Stop::ToolUse || line.stop == pi::Stop::Stop || line.stop == pi::Stop::Length)) {
        pendingToolCalls_.clear();
        pendingToolCalls_.insert(line.toolCallIds.begin(), line.toolCallIds.end());
        setPhase(Phase::Tools, ts, std::nullopt);
    } else {
        setPhase(Phase::Idle, ts, std::nullopt);
    }
}

void PiParser::applySide(const std::string& id, std::int64_t ts, const pi::Call& call, const std::optional<std::string>& model,
                         const std::optional<std::string>& provider, ResponseSink& sink) {
    if (!call.hasUsageObject || !call.hasUsage) return;
    const std::string key = "pi:" + id + ":" + std::to_string(ts);
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    emitResponse(key, std::nullopt, ts, call, model, provider, ExecutionModes(), sink);
}

// The context holds one effort fact; the later of a selection and a recorded level replaces it.
void PiParser::selectEffort(const std::optional<std::string>& level, ModeEvidence evidence, ModeScope scope) {
    if (auto fact = native::effort(level, evidence, scope)) {
        std::vector<ModeFact> facts;
        for (const auto& f : context_.facts()) {
            if (f.kind != ModeKind::Effort) facts.push_back(f);
        }
        facts.push_back(*fact);
        context_ = ExecutionModes(std::move(facts));
        setModes(context_);
    }
}

void PiParser::emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs, const pi::Call& call,
                            const std::optional<std::string>& model, const std::optional<std::string>& provider,
                            const ExecutionModes& modes, ResponseSink& sink) {
    const bool completion = deduper().tryClaim(key, agentId());
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Pi;
    r.agentId = agentId();
    r.model = model;
    r.startMs = startMs;
    r.endMs = endMs;
    r.tokens = call.tokens;
    r.provider = provider;
    r.modes = modes;
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
