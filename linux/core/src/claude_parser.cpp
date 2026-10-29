#include "agentspeed/claude_parser.hpp"

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

constexpr std::string_view kInterruptPrefix = "[Request interrupted by user";

enum class Kind { Other, User, Assistant, ApiRequest };
enum class StopReason { Missing, ToolUse, PauseTurn, Other };

struct Line {
    Kind kind = Kind::Other;
    std::optional<std::int64_t> timestampMs;
    std::optional<bool> desktop;  // set when the line has a string entrypoint
    std::optional<std::string> cwd;
    bool sidechain = false;
    bool interrupt = false;
    bool toolEndsTurn = false;
    bool hasToolUse = false;
    StopReason stopReason = StopReason::Missing;
    std::optional<std::string> key;
    std::optional<std::string> model;
    TokenCounts usage;
    bool fast = false;
};

bool rawStartsWith(const json::Value& value, std::string_view prefix) {
    return value.isString() && value.raw().substr(0, prefix.size()) == prefix;
}

TokenCounts readUsage(const json::Value& usage, bool& fast) {
    const auto input = usage["input_tokens"].count();
    const auto cacheCreation = usage["cache_creation_input_tokens"].count();
    const auto cacheRead = usage["cache_read_input_tokens"].count();
    const auto output = usage["output_tokens"].count();
    const auto thinking = usage["output_tokens_details"]["thinking_tokens"].count();
    const auto cacheCreation1h = usage["cache_creation"]["ephemeral_1h_input_tokens"].count();
    fast = usage["speed"].equals("fast");
    TokenCounts tokens;
    tokens.input = TokenCounts::saturatingAdd(TokenCounts::saturatingAdd(input, cacheCreation), cacheRead);
    tokens.cachedInput = cacheRead;
    tokens.cacheWriteInput = cacheCreation;
    tokens.output = output;
    tokens.reasoning = thinking;
    tokens.cacheWrite1hInput = std::min(cacheCreation1h, cacheCreation);
    return tokens;
}

Line read(const json::Value& root) {
    Line line;
    const auto type = root["type"];
    if (type.equals("assistant")) line.kind = Kind::Assistant;
    else if (type.equals("user")) line.kind = Kind::User;
    else if (type.equals("api-request")) line.kind = Kind::ApiRequest;
    if (const auto ts = root["timestamp"]; ts.isString()) {
        if (auto text = ts.string()) line.timestampMs = parseTimestamp(*text);
    }
    if (const auto entrypoint = root["entrypoint"]; entrypoint.isString()) {
        line.desktop = entrypoint.startsWith("claude-desktop");
    }
    line.cwd = root["cwd"].nonEmptyString();
    line.sidechain = root["isSidechain"].isTrue();
    line.toolEndsTurn = root["toolEndsTurn"].isTrue();

    const auto message = root["message"];
    if (message.isObject()) {
        if (line.kind == Kind::Assistant) {
            line.key = message["id"].nonEmptyString();
            line.model = message["model"].nonEmptyString();
            if (const auto stop = message["stop_reason"]; stop.valid() && !stop.isNull()) {
                line.stopReason = stop.equals("tool_use") ? StopReason::ToolUse
                                  : stop.equals("pause_turn") ? StopReason::PauseTurn
                                                              : StopReason::Other;
            }
            if (const auto usage = message["usage"]; usage.isObject()) line.usage = readUsage(usage, line.fast);
        }
        const auto content = message["content"];
        if (content.isString()) {
            line.interrupt = rawStartsWith(content, kInterruptPrefix);
        } else if (content.isArray()) {
            for (const auto& block : content.elements()) {
                if (!block.isObject()) continue;
                const auto blockType = block["type"];
                if (blockType.equals("text") && rawStartsWith(block["text"], kInterruptPrefix)) line.interrupt = true;
                if (blockType.equals("tool_use")) line.hasToolUse = true;
            }
        }
    }
    if (line.kind == Kind::Assistant && !line.key) {
        line.key = root["requestId"].nonEmptyString();
        if (!line.key) line.key = root["uuid"].nonEmptyString();
    }
    return line;
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
    setSubagent(pathIsSubagent_);
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
    const Line line = read(doc.root());
    if (!line.timestampMs) return;
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    if (!sourceDecided() && line.desktop) setSource(*line.desktop ? SourceKind::ClaudeDesktop : SourceKind::ClaudeCode);
    setProjectFromCwd(line.cwd);
    if (line.sidechain) setSubagent(true);

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
            return;
        case Kind::Assistant:
            break;
    }

    if (line.model == "<synthetic>" || !line.key) return;
    const std::string& key = *line.key;
    ParsedResponse response;
    bool isCompletion = false;
    std::string owner;
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
    } else {
        response = create();
    }
    remember(response);
    setModel(response.model);
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
