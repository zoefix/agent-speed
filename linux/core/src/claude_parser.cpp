#include "agentspeed/claude_parser.hpp"

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

constexpr std::string_view kInterruptPrefix = "[Request interrupted by user";

enum class Kind { Other, User, Assistant, ApiRequest, Attachment };
enum class Marker { None, SessionEnter, SessionExit, WorkflowKeyword };
enum class StopReason { Missing, ToolUse, PauseTurn, Other };

struct Line {
    Kind kind = Kind::Other;
    std::optional<std::int64_t> timestampMs;
    std::optional<bool> desktop;  // set when the line has a string entrypoint
    std::optional<std::string> cwd;
    bool sidechain = false;
    bool interrupt = false;
    bool meta = false;
    bool promptContent = false;
    Marker marker = Marker::None;
    bool toolEndsTurn = false;
    bool hasToolUse = false;
    StopReason stopReason = StopReason::Missing;
    std::optional<std::string> key;
    std::optional<std::string> model;
    TokenCounts usage;
    bool fast = false;
    std::optional<std::string> speed;
    std::optional<std::string> effort;
    std::optional<std::string> perTurnEffort;

    bool isPrompt() const { return kind == Kind::User && !meta && promptContent; }
    const std::optional<std::string>& recordedEffort() const { return perTurnEffort ? perTurnEffort : effort; }
};

std::optional<std::string> token(const json::Value& value) {
    return value.isString() ? value.string() : std::nullopt;
}

bool rawStartsWith(const json::Value& value, std::string_view prefix) {
    return value.isString() && value.raw().substr(0, prefix.size()) == prefix;
}

TokenCounts readUsage(const json::Value& usage, bool& fast, std::optional<std::string>& speed) {
    const auto input = usage["input_tokens"].count();
    const auto cacheCreation = usage["cache_creation_input_tokens"].count();
    const auto cacheRead = usage["cache_read_input_tokens"].count();
    const auto output = usage["output_tokens"].count();
    const auto thinking = usage["output_tokens_details"]["thinking_tokens"].count();
    const auto cacheCreation1h = usage["cache_creation"]["ephemeral_1h_input_tokens"].count();
    speed = usage["speed"].nonEmptyString();
    fast = speed == "fast";
    TokenCounts tokens;
    tokens.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, cacheCreation), cacheRead);
    tokens.cachedInput = cacheRead;
    tokens.cacheWriteInput = cacheCreation;
    tokens.output = output;
    tokens.reasoning = thinking;
    tokens.cacheWrite1hInput = std::min(cacheCreation1h, cacheCreation);
    return tokens;
}

// The raw-text hints of the macOS and Windows readers: a property is only read when its line could hold it.
struct Hints {
    bool assistant, interrupt, toolUse, user, ultra;
    explicit Hints(std::string_view raw)
        : assistant(raw.find("\"assistant\"") != std::string_view::npos),
          interrupt(raw.find(kInterruptPrefix) != std::string_view::npos),
          toolUse(raw.find("\"tool_use\"") != std::string_view::npos),
          user(raw.find("\"user\"") != std::string_view::npos),
          ultra(raw.find("\"ultra_effort_") != std::string_view::npos ||
                raw.find("\"workflow_keyword_request\"") != std::string_view::npos) {}
};

Line read(const json::Value& root, std::string_view raw) {
    Line line;
    const Hints hints(raw);
    const auto type = root["type"];
    if (type.equals("assistant")) line.kind = Kind::Assistant;
    else if (type.equals("user")) line.kind = Kind::User;
    else if (type.equals("api-request")) line.kind = Kind::ApiRequest;
    else if (type.equals("attachment")) line.kind = Kind::Attachment;
    if (const auto ts = root["timestamp"]; ts.isString()) {
        if (auto text = ts.string()) line.timestampMs = parseTimestamp(*text);
    }
    if (const auto entrypoint = root["entrypoint"]; entrypoint.isString()) {
        line.desktop = entrypoint.startsWith("claude-desktop");
    }
    line.cwd = root["cwd"].nonEmptyString();
    line.sidechain = root["isSidechain"].isTrue();
    line.toolEndsTurn = root["toolEndsTurn"].isTrue();
    line.meta = root["isMeta"].isTrue();
    if (hints.ultra) {
        const auto attachment = root["attachment"];
        if (attachment.isObject()) {
            const auto marker = attachment["type"];
            line.marker = marker.equals("ultra_effort_enter")         ? Marker::SessionEnter
                          : marker.equals("ultra_effort_exit")        ? Marker::SessionExit
                          : marker.equals("workflow_keyword_request") ? Marker::WorkflowKeyword
                                                                      : Marker::None;
        }
    }
    if (hints.assistant) {
        line.effort = token(root["effort"]);
        line.perTurnEffort = token(root["perTurnEffort"]);
    }

    const auto message = root["message"];
    if (message.isObject() && (hints.assistant || hints.interrupt || hints.toolUse || hints.user)) {
        if (hints.assistant) {
            line.key = message["id"].nonEmptyString();
            line.model = message["model"].nonEmptyString();
            if (const auto stop = message["stop_reason"]; stop.valid() && !stop.isNull()) {
                line.stopReason = stop.equals("tool_use") ? StopReason::ToolUse
                                  : stop.equals("pause_turn") ? StopReason::PauseTurn
                                                              : StopReason::Other;
            }
            if (const auto usage = message["usage"]; usage.isObject()) line.usage = readUsage(usage, line.fast, line.speed);
        }
        const auto content = message["content"];
        if (hints.interrupt || hints.toolUse || hints.user) {
            if (content.isString()) {
                line.interrupt = rawStartsWith(content, kInterruptPrefix);
                line.promptContent = true;
            } else if (content.isArray()) {
                for (const auto& block : content.elements()) {
                    if (!block.isObject()) continue;
                    const auto blockType = block["type"];
                    const bool text = blockType.equals("text");
                    if (text && rawStartsWith(block["text"], kInterruptPrefix)) line.interrupt = true;
                    if (text) line.promptContent = true;
                    if (blockType.equals("tool_use")) line.hasToolUse = true;
                }
            }
        }
    }
    if (hints.assistant && !line.key) {
        line.key = root["requestId"].nonEmptyString();
        if (!line.key) line.key = root["uuid"].nonEmptyString();
    }
    return line;
}

const ModeFact& ultracodeFact() {
    static const ModeFact fact = *native::effort("ultracode", ModeEvidence::Recorded, ModeScope::Request);
    return fact;
}

}  // namespace

ClaudeParser::ClaudeParser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::ClaudeCode),
      pathIsSubagent_(path_text::hasDirectoryComponent(this->agentId(), "subagents")) {
    setSubagent(pathIsSubagent_);
}

void ClaudeParser::reset() {
    TranscriptParser::reset();
    recent_.clear();
    recentOrder_.clear();
    pendingStartMs_.reset();
    preciseStart_ = false;
    ultraSession_ = false;
    ultraTurn_ = false;
    latestModes_ = ExecutionModes();
    lineEffort_.reset();
    setSubagent(pathIsSubagent_);
}

void ClaudeParser::refreshModes() {
    setModes(latestModes_.without([](const ModeFact& f) { return f.kind == ModeKind::Effort; })
                 .with(ultracode() ? std::optional<ModeFact>(ultracodeFact()) : lineEffort_));
}

void ClaudeParser::remember(const ParsedResponse& response) {
    if (!recent_.contains(response.key)) {
        if (recentOrder_.size() >= kMergeCacheCapacity) {
            recent_.erase(recentOrder_.front());
            recentOrder_.pop_front();
        }
        recentOrder_.push_back(response.key);
    }
    recent_[response.key] = response;
}

void ClaudeParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) {
        countMalformed();
        return;
    }
    const Line line = read(doc.root(), text);
    if (!line.timestampMs) return;
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    if (!sourceDecided() && line.desktop) setSource(*line.desktop ? SourceKind::ClaudeDesktop : SourceKind::ClaudeCode);
    setProjectFromCwd(line.cwd);
    if (line.sidechain) setSubagent(true);

    // Ultracode: ultra_effort_enter / _exit switch the session; workflow_keyword_request the turn, which the next
    // prompt ends (EXECUTION-MODES, Claude Code).
    const bool wasUltra = ultracode();
    if (line.kind == Kind::Attachment && (line.marker == Marker::SessionEnter || line.marker == Marker::SessionExit)) {
        ultraSession_ = line.marker == Marker::SessionEnter;
    } else if (line.kind == Kind::Attachment && line.marker == Marker::WorkflowKeyword) {
        ultraTurn_ = true;
    } else if (line.isPrompt()) {
        ultraTurn_ = false;
    }
    if (ultracode() != wasUltra) refreshModes();

    switch (line.kind) {
        case Kind::ApiRequest:
            pendingStartMs_ = ts;
            preciseStart_ = true;
            setPhase(Phase::Generating, ts, ts);
            return;
        case Kind::User:
            if (line.interrupt || line.toolEndsTurn) {
                pendingStartMs_.reset();
                preciseStart_ = false;
                setPhase(Phase::Idle, ts, std::nullopt);
            } else {
                if (!preciseStart_) pendingStartMs_ = ts;
                setPhase(Phase::Generating, ts, pendingStartMs_);
            }
            return;
        case Kind::Other:
        case Kind::Attachment:
            return;
        case Kind::Assistant:
            break;
    }

    if (line.model == "<synthetic>" || !line.key) return;
    const std::string& key = *line.key;
    ParsedResponse response;
    bool isCompletion = false;
    std::string owner;
    const auto recorded = native::effort(line.recordedEffort(), ModeEvidence::Recorded, ModeScope::Request);
    const ExecutionModes lineModes =
        ExecutionModes::of(ultracode() ? std::optional<ModeFact>(ultracodeFact()) : recorded).merge(native::claudeSpeed(line.speed));
    auto create = [&] {
        ParsedResponse created;
        created.key = key;
        created.source = source();
        created.agentId = agentId();
        created.model = line.model;
        created.startMs = pendingStartMs_;
        created.endMs = ts;
        created.tokens = line.usage;
        created.final = line.stopReason != StopReason::Missing;
        created.fast = line.fast;
        created.modes = lineModes;
        pendingStartMs_.reset();
        preciseStart_ = false;
        return created;
    };
    if (deduper().tryClaim(key, agentId(), &owner)) {
        response = create();
        isCompletion = true;
    } else if (owner != agentId()) {
        return;
    } else if (auto it = recent_.find(key); it != recent_.end()) {
        response = it->second;
        response.endMs = std::max(response.endMs, ts);
        response.tokens = TokenCounts::max(response.tokens, line.usage);
        if (line.model) response.model = line.model;
        response.final = response.final || line.stopReason != StopReason::Missing;
        response.fast = response.fast || line.fast;
        response.modes = response.modes.merge(lineModes);
    } else {
        response = create();
    }
    remember(response);
    setModel(response.model);
    latestModes_ = response.modes;
    if (recorded) lineEffort_ = recorded;
    refreshModes();
    sink.onResponse(response, isCompletion);

    switch (line.stopReason) {
        case StopReason::ToolUse:
            setPhase(Phase::Tools, ts, std::nullopt);
            break;
        case StopReason::Missing:
            if (line.hasToolUse) {
                setPhase(Phase::Tools, ts, std::nullopt);
            } else {
                setPhase(Phase::Generating, ts, response.startMs ? response.startMs : std::optional<std::int64_t>(ts));
            }
            break;
        case StopReason::PauseTurn:
            setPhase(Phase::Generating, ts, response.startMs ? response.startMs : std::optional<std::int64_t>(ts));
            break;
        case StopReason::Other:
            setPhase(Phase::Idle, ts, std::nullopt);
            break;
    }
}

}  // namespace agentspeed
