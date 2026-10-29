#include "agentspeed/dsh_parser.hpp"

#include <charconv>
#include <cmath>
#include <vector>

#include "agentspeed/json.hpp"

namespace agentspeed {

namespace {

constexpr std::int64_t kInheritedHistoryToleranceMs = 2000;
constexpr int kMaxWrapperDepth = 64;

enum class EventKind { Other, Header, RequestHeader, StepStart, AssistantMessage, TurnEnd };

// A JSON number (not a boolean) as whole milliseconds, truncated toward zero.
std::optional<std::int64_t> milliseconds(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    const auto raw = v.raw();
    std::int64_t exact = 0;
    const auto [end, ec] = std::from_chars(raw.data(), raw.data() + raw.size(), exact);
    if (ec == std::errc() && end == raw.data() + raw.size()) return exact;
    const auto real = v.number();
    if (real && std::isfinite(*real) && std::abs(*real) < 9.2e18) return static_cast<std::int64_t>(std::trunc(*real));
    return std::nullopt;
}

// Missing parts are spelled like the reference implementation (Python's str), so keys agree across platforms.
std::optional<std::string> scalarText(const json::Value& v) {
    if (v.isNumber()) return std::string(v.raw());
    if (v.isString()) return v.string();
    if (v.isBool()) return v.isTrue() ? std::string("True") : std::string("False");
    return std::nullopt;
}

std::optional<std::string> token(const json::Value& v) { return v.isString() ? v.string() : std::nullopt; }

}  // namespace

struct DshParser::Event {
    EventKind kind = EventKind::Other;
    std::optional<std::int64_t> timeMs;
    std::optional<std::string> seq, id, cwd, parentSession;
    std::int64_t createdMs = 0;
    bool originSubagent = false, delegated = false;
    std::optional<std::string> configModel, configProvider, configEffort, sourceModel, sourceProvider, messageId;
    bool hasUsage = false, hasToolCall = false;
    TokenCounts usage;
};

namespace {

using Event = DshParser::Event;

Event readFields(const json::Value& object, EventKind kind) {
    Event e;
    e.kind = kind;
    e.timeMs = milliseconds(object["time"]);
    e.seq = scalarText(object["seq"]);
    e.id = object["id"].nonEmptyString();
    if (const auto created = object["createdAt"]; created.valid()) e.createdMs = milliseconds(created).value_or(0);
    e.cwd = object["cwd"].nonEmptyString();
    e.originSubagent = object["origin"].equals("subagent");
    if (const auto depth = object["delegationDepth"]; depth.isNumber()) {
        const auto level = depth.number();
        e.delegated = level && *level > 0;
    }
    e.parentSession = object["parentSession"].nonEmptyString();
    const auto data = object["data"];
    if (data.isObject()) {
        const auto config = data["header"]["config"];
        if (config.isObject()) {
            e.configModel = config["model"].nonEmptyString();
            e.configProvider = config["provider"].nonEmptyString();
            e.configEffort = token(config["reasoningEffort"]);
        }
        const auto message = data["message"];
        if (message.isObject()) {
            e.messageId = message["id"].nonEmptyString();
            const auto source = message["source"];
            if (source.isObject()) {
                e.sourceModel = source["model"].nonEmptyString();
                e.sourceProvider = source["provider"].nonEmptyString();
            }
            if (const auto content = message["content"]; content.isArray()) {
                for (const auto& block : content.elements()) {
                    if (block.isObject() && block["type"].equals("tool-call")) e.hasToolCall = true;
                }
            }
        }
        if (const auto usage = data["usage"]; usage.isObject()) {
            e.hasUsage = true;
            const auto input = usage["inputTokens"].count();
            const auto output = usage["outputTokens"].count();
            const auto cacheRead = usage["cacheReadTokens"].count();
            const auto cacheWrite = usage["cacheWriteTokens"].count();
            // dsh reports reasoning inside outputTokens, never separately.
            e.usage.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, cacheRead), cacheWrite);
            e.usage.cachedInput = cacheRead;
            e.usage.cacheWriteInput = cacheWrite;
            e.usage.output = output;
        }
    }
    return e;
}

EventKind classify(const json::Value& type) {
    if (type.equals("assistant/message")) return EventKind::AssistantMessage;
    if (type.equals("step/start")) return EventKind::StepStart;
    if (type.equals("turn/end")) return EventKind::TurnEnd;
    if (type.equals("request/header")) return EventKind::RequestHeader;
    if (type.equals("session")) return EventKind::Header;
    return EventKind::Other;
}

// Unwraps `event` and `snapshot` wrappers into the events they stand for.
void collect(const json::Value& object, std::vector<Event>& out, int depth) {
    const auto type = object["type"];
    if (type.equals("event")) {
        const auto inner = object["event"];
        if (inner.isObject() && depth < kMaxWrapperDepth) {
            collect(inner, out, depth + 1);
            return;
        }
    }
    if (type.equals("snapshot")) {
        // A snapshot header counts by its own fields, whatever its type says.
        if (const auto header = object["header"]; header.isObject()) out.push_back(readFields(header, EventKind::Header));
        if (const auto records = object["records"]; records.isArray() && depth < kMaxWrapperDepth) {
            for (const auto& record : records.elements()) {
                if (record.isObject()) collect(record, out, depth + 1);
            }
        }
        return;
    }
    out.push_back(readFields(object, classify(type)));
}

}  // namespace

DshParser::DshParser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Deepseek) {}

void DshParser::reset() {
    TranscriptParser::reset();
    sessionId_.reset();
    createdMs_.reset();
    parentSession_.reset();
    pendingStartMs_.reset();
    provider_.reset();
}

void DshParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) {
        countMalformed();
        return;
    }
    std::vector<Event> events;
    collect(doc.root(), events, 0);
    for (const auto& e : events) apply(e, sink);
}

void DshParser::apply(const Event& e, ResponseSink& sink) {
    if (e.kind == EventKind::Header) {
        // Only the first header applies.
        if (createdMs_) return;
        createdMs_ = e.createdMs;
        if (e.id) sessionId_ = e.id;
        parentSession_ = e.parentSession;
        setProjectFromCwd(e.cwd);
        markChanged();
        setSubagent(isSubagent() || e.originSubagent || e.delegated || e.parentSession.has_value());
        return;
    }
    if (!e.timeMs) return;
    const std::int64_t ts = *e.timeMs;
    if (createdMs_ && ts < *createdMs_ - kInheritedHistoryToleranceMs) return;
    touch(ts);
    switch (e.kind) {
        case EventKind::RequestHeader:
            setModel(e.configModel);
            if (e.configProvider) provider_ = e.configProvider;
            setModes(ExecutionModes::of(native::effort(e.configEffort, ModeEvidence::Recorded, ModeScope::Request)));
            break;
        case EventKind::StepStart:
            pendingStartMs_ = ts;
            setPhase(Phase::Generating, ts, ts);
            break;
        case EventKind::TurnEnd:
            pendingStartMs_.reset();
            setPhase(Phase::Idle, ts, std::nullopt);
            break;
        case EventKind::AssistantMessage: {
            const std::string key =
                e.messageId ? *e.messageId
                            : "dsh:" + sessionId_.value_or("None") + ":" + e.seq.value_or("None");
            if (!deduper().tryClaim(key, agentId())) return;
            if (e.hasUsage) {
                ParsedResponse r;
                r.key = key;
                r.source = SourceKind::Deepseek;
                r.agentId = agentId();
                r.model = e.sourceModel ? e.sourceModel : model();
                r.startMs = pendingStartMs_;
                r.endMs = ts;
                r.tokens = e.usage;
                r.provider = e.sourceProvider ? e.sourceProvider : provider_;
                r.modes = modes();
                sink.onResponse(r, true);
                setModel(r.model);
            }
            pendingStartMs_.reset();
            setPhase(e.hasToolCall ? Phase::Tools : Phase::Idle, ts, std::nullopt);
            break;
        }
        case EventKind::Header:
        case EventKind::Other:
            break;
    }
}

}  // namespace agentspeed
