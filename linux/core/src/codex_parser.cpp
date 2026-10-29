#include "agentspeed/codex_parser.hpp"

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

constexpr std::int64_t kCopiedHistoryToleranceMs = 2000;

bool endsWith(std::string_view text, std::string_view suffix) {
    return text.size() >= suffix.size() && text.substr(text.size() - suffix.size()) == suffix;
}

CodexParser::Usage readUsage(const json::Value& usage) {
    CodexParser::Usage u;
    u.input = usage["input_tokens"].count();
    u.cached = usage["cached_input_tokens"].count();
    u.cacheWrite = usage["cache_write_input_tokens"].count();
    u.output = usage["output_tokens"].count();
    u.reasoning = usage["reasoning_output_tokens"].count();
    u.totalTokens = usage["total_tokens"].count();
    return u;
}

TokenCounts tokensOf(const CodexParser::Usage& u) {
    TokenCounts t;
    t.input = u.input;
    t.cachedInput = u.cached;
    t.cacheWriteInput = u.cacheWrite;
    t.output = u.output;
    t.reasoning = u.reasoning;
    return t;
}

}  // namespace

CodexParser::CodexParser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Codex),
      pathIsSubagent_(path_text::hasDirectoryComponent(this->agentId(), "subagents")) {
    setSubagent(pathIsSubagent_);
}

void CodexParser::reset() {
    TranscriptParser::reset();
    fileCreatedMs_.reset();
    subagentStartOrdinal_.reset();
    myAgentPath_ = "/root";
    metaThreadId_.reset();
    parentThreadId_.reset();
    provider_.reset();
    pendingStartMs_.reset();
    streaming_ = false;
    sawModernUsage_ = false;
    previousLegacyTotals_.reset();
    setSubagent(pathIsSubagent_);
}

void CodexParser::onInput(std::int64_t ts) {
    if (streaming_) return;
    pendingStartMs_ = ts;
    setPhase(Phase::Generating, ts, pendingStartMs_);
}

void CodexParser::afterUsage(std::int64_t ts) {
    pendingStartMs_ = ts;
    streaming_ = false;
    setPhase(Phase::Tools, ts, std::nullopt);
}

void CodexParser::emit(const std::string& key, const TokenCounts& tokens, std::int64_t ts, ResponseSink& sink) {
    ParsedResponse response;
    response.key = key;
    response.source = SourceKind::Codex;
    response.agentId = agentId();
    response.model = model();
    response.startMs = pendingStartMs_;
    response.endMs = ts;
    response.tokens = tokens;
    response.provider = provider_;
    sink.onResponse(response, true);
}

void CodexParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) {
        countMalformed();
        return;
    }
    const auto root = doc.root();
    const auto tsValue = root["timestamp"];
    if (!tsValue.isString()) return;
    const auto parsedTs = parseTimestamp(*tsValue.string());
    if (!parsedTs) return;
    const std::int64_t ts = *parsedTs;
    const auto type = root["type"];
    const auto payload = root["payload"];

    if (type.equals("session_meta")) {
        if (fileCreatedMs_) return;
        fileCreatedMs_ = ts;
        subagentStartOrdinal_ = payload["subagent_history_start_ordinal"].ordinal();
        touch(ts);
        setProjectFromCwd(payload["cwd"].nonEmptyString());
        if (auto path = payload["agent_path"].nonEmptyString()) myAgentPath_ = *path;
        if (auto id = payload["id"].nonEmptyString()) metaThreadId_ = id;
        parentThreadId_ = payload["parent_thread_id"].nonEmptyString();
        if (!parentThreadId_) {
            parentThreadId_ = payload["source"]["subagent"]["thread_spawn"]["parent_thread_id"].nonEmptyString();
        }
        provider_ = payload["model_provider"].nonEmptyString();
        markChanged();
        const bool threadSourceSubagent = payload["thread_source"].equals("subagent");
        const bool sourceHasSubagent = payload["source"].isObject() && payload["source"]["subagent"].valid();
        setSubagent(isSubagent() || threadSourceSubagent || sourceHasSubagent);
        return;
    }
    if (fileCreatedMs_ && ts < *fileCreatedMs_ - kCopiedHistoryToleranceMs) return;
    if (subagentStartOrdinal_) {
        if (auto ordinal = root["ordinal"].ordinal(); ordinal && *ordinal < *subagentStartOrdinal_) return;
    }
    touch(ts);

    const auto payloadType = payload["type"];
    const std::string kind = payloadType.isString() ? *payloadType.string() : std::string();

    if (type.equals("turn_context")) {
        setProjectFromCwd(payload["cwd"].nonEmptyString());
        setModel(payload["model"].nonEmptyString());
    } else if (type.equals("response_item")) {
        const auto role = payload["role"];
        if (kind == "reasoning" || endsWith(kind, "_call") || (kind == "message" && role.equals("assistant"))) {
            streaming_ = true;
            setPhase(Phase::Generating, ts, pendingStartMs_ ? pendingStartMs_ : std::optional<std::int64_t>(ts));
        } else if (endsWith(kind, "_output") ||
                   (kind == "message" && (role.equals("user") || role.equals("developer") || role.equals("system")))) {
            onInput(ts);
        } else if (kind == "agent_message" && payload["recipient"].equals(myAgentPath_)) {
            if (!streaming_) pendingStartMs_ = ts;
        }
    } else if (type.equals("event_msg")) {
        if (kind == "task_started" || kind == "user_message") {
            onInput(ts);
        } else if (kind == "token_count") {
            if (sawModernUsage_ && streaming_) {
                afterUsage(ts);
                return;
            }
            if (sawModernUsage_) return;
            const auto info = payload["info"];
            if (!info.isObject()) return;
            const auto total = readUsage(info["total_token_usage"]);
            const auto lastValue = info["last_token_usage"];
            const auto last = readUsage(lastValue);
            TokenCounts tokens;
            if (!previousLegacyTotals_) {
                tokens = tokensOf(lastValue.isObject() ? last : total);
            } else {
                const auto& p = *previousLegacyTotals_;
                const Usage delta{total.input - p.input,   total.cached - p.cached,       total.cacheWrite - p.cacheWrite,
                                  total.output - p.output, total.reasoning - p.reasoning, total.totalTokens - p.totalTokens};
                if (delta.totalTokens == 0) return;
                const bool anyNegative = delta.input < 0 || delta.cached < 0 || delta.cacheWrite < 0 ||
                                         delta.output < 0 || delta.reasoning < 0 || delta.totalTokens < 0;
                tokens = tokensOf(anyNegative ? last : delta);
            }
            previousLegacyTotals_ = total;
            const std::string key = "codex-legacy:" + threadId() + ":" + std::to_string(total.totalTokens);
            if (!deduper().tryClaim(key, agentId())) return;
            emit(key, tokens, ts, sink);
            afterUsage(ts);
        } else if (kind == "task_complete" || kind == "turn_aborted") {
            pendingStartMs_.reset();
            streaming_ = false;
            setPhase(Phase::Idle, ts, std::nullopt);
        }
    } else if (type.equals("token_usage_record")) {
        sawModernUsage_ = true;
        std::string key;
        if (auto id = payload["response_id"].nonEmptyString()) {
            key = *id;
        } else {
            const auto thread = payload["thread_id"].nonEmptyString();
            key = "codex:" + (thread ? *thread : threadId()) + ":" + std::to_string(ts);
        }
        if (!deduper().tryClaim(key, agentId())) return;
        emit(key, tokensOf(readUsage(payload["usage"])), ts, sink);
        afterUsage(ts);
    }
}

}  // namespace agentspeed
