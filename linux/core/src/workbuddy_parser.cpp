#include "agentspeed/workbuddy_parser.hpp"

#include <charconv>
#include <cmath>

#include "agentspeed/json.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed {

namespace {

enum class Item { Other, UserMessage, AssistantMessage, FunctionCall, FunctionCallResult, Reasoning, ModelUsage };

std::optional<std::int64_t> exactInteger(const json::Value& v) {
    const auto raw = v.raw();
    std::int64_t value = 0;
    const auto [end, ec] = std::from_chars(raw.data(), raw.data() + raw.size(), value);
    if (ec == std::errc() && end == raw.data() + raw.size()) return value;
    return std::nullopt;
}

// Valid when a finite number > 0 (truncated) or a timestamp string (3.1).
std::optional<std::int64_t> readTimestamp(const json::Value& v) {
    if (v.isString()) return parseTimestamp(*v.string());
    if (!v.isNumber()) return std::nullopt;
    if (auto whole = exactInteger(v)) return *whole > 0 ? whole : std::nullopt;
    const auto real = v.number();
    if (!real || !std::isfinite(*real) || *real <= 0) return std::nullopt;
    return *real >= 9'223'372'036'854'775'808.0 ? INT64_MAX : static_cast<std::int64_t>(std::trunc(*real));
}

// A "first value above 0" candidate: null unless a finite number above 0.
std::optional<std::int64_t> readPositive(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    if (auto whole = exactInteger(v)) return *whole > 0 ? std::optional<std::int64_t>(std::min(*whole, TokenCounts::kMax)) : std::nullopt;
    const auto real = v.number();
    if (!real || !std::isfinite(*real) || *real <= 0) return std::nullopt;
    return *real >= static_cast<double>(TokenCounts::kMax) ? TokenCounts::kMax : static_cast<std::int64_t>(*real);
}

std::int64_t sumDetails(const json::Value& v, const char* field) {
    if (v.isObject()) return v[field].count();
    if (!v.isArray()) return 0;
    std::int64_t sum = 0;
    for (const auto& item : v.elements()) {
        if (item.isObject()) sum = TokenCounts::saturatingAdd(sum, item[field].count());
    }
    return sum;
}

}  // namespace

struct WorkBuddyParser::Line {
    Item item = Item::Other;
    std::optional<std::int64_t> timestampMs;
    std::optional<std::string> cwd, id, callId, messageId, model, requestModelId, provider;
    bool incomplete = false, skipRun = false, partialAborted = false, hasError = false, compaction = false;
    bool hasUsage = false;
    TokenCounts tokens;
};

namespace workbuddy {

bool isSubagentPath(const std::string& root, const std::string& path) {
    std::string base = root;
    while (base.size() > 1 && base.back() == '/') base.pop_back();
    if (path.size() <= base.size() || path.compare(0, base.size(), base) != 0 || path[base.size()] != '/') return false;
    std::vector<std::string> parts;
    std::string current;
    for (std::size_t i = base.size() + 1; i <= path.size(); ++i) {
        if (i == path.size() || path[i] == '/' || path[i] == '\\') {
            if (!current.empty()) parts.push_back(current);
            current.clear();
        } else {
            current += path[i];
        }
    }
    return parts.size() == 4 && parts[2] == "subagents";
}

std::pair<std::optional<std::string>, std::optional<std::string>> resolveModel(const std::optional<std::string>& streamModel,
                                                                                const std::optional<std::string>& requestModelId) {
    auto firstNonBlank = [](const std::optional<std::string>& v) -> std::optional<std::string> {
        if (!v) return std::nullopt;
        auto trimmed = text::strip(*v);
        return trimmed.empty() ? std::nullopt : std::optional<std::string>(trimmed);
    };
    auto model = firstNonBlank(streamModel);
    if (!model) model = firstNonBlank(requestModelId);
    if (!model) return {std::nullopt, std::nullopt};
    for (std::string_view prefix : {std::string_view("custom:"), std::string_view("custom-local:")}) {
        if (model->size() < prefix.size()) continue;
        bool same = true;
        for (std::size_t i = 0; i < prefix.size() && same; ++i) {
            char c = (*model)[i];
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            same = c == prefix[i];
        }
        if (same) {
            const auto rest = text::strip(model->substr(model->find(':') + 1));
            return {rest.empty() ? std::nullopt : std::optional<std::string>(rest), std::string("custom")};
        }
    }
    return {model, std::nullopt};
}

}  // namespace workbuddy

namespace {

using Line = WorkBuddyParser::Line;

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
    const auto role = root["role"];
    const bool user = role.equals("user"), assistant = role.equals("assistant");
    if (type.equals("message")) line.item = user ? Item::UserMessage : assistant ? Item::AssistantMessage : Item::Other;
    else if (type.equals("function_call")) line.item = Item::FunctionCall;
    else if (type.equals("function_call_result")) line.item = Item::FunctionCallResult;
    else if (type.equals("reasoning")) line.item = Item::Reasoning;
    else if (type.equals("model-usage")) line.item = Item::ModelUsage;
    line.timestampMs = readTimestamp(root["timestamp"]);
    if (const auto cwd = root["cwd"]; cwd.isString()) {
        auto value = *cwd.string();
        if (!text::strip(value).empty()) line.cwd = std::move(value);
    }
    line.id = root["id"].nonEmptyString();
    if (const auto call = root["callId"]; call.isString()) line.callId = call.string();
    line.incomplete = root["status"].equals("incomplete");
    std::optional<std::string> streamModel;
    if (const auto data = root["providerData"]; data.isObject()) {
        line.skipRun = data["skipRun"].isTrue();
        line.partialAborted = data["isPartialAborted"].isTrue();
        line.hasError = data["error"].isObject();
        line.compaction = data["agent"].equals("compact");
        line.messageId = data["messageId"].nonEmptyString();
        if (const auto m = data["model"]; m.isString()) streamModel = m.string();
        if (const auto m = data["requestModelId"]; m.isString()) line.requestModelId = m.string();
        std::int64_t inputTokens = 0, outputTokens = 0, detailCached = 0, detailReasoning = 0;
        std::int64_t promptTokens = 0, completionTokens = 0, completionReasoning = 0;
        std::optional<std::int64_t> cacheReadInput, promptDetailsCached, promptCacheHit, cacheCreationInput, promptCacheWrite;
        const auto usage = data["usage"];
        const auto raw = data["rawUsage"];
        if (usage.isObject()) {
            inputTokens = usage["inputTokens"].count();
            outputTokens = usage["outputTokens"].count();
            detailCached = sumDetails(usage["inputTokensDetails"], "cached_tokens");
            detailReasoning = sumDetails(usage["outputTokensDetails"], "reasoning_tokens");
        }
        if (raw.isObject()) {
            promptTokens = raw["prompt_tokens"].count();
            completionTokens = raw["completion_tokens"].count();
            if (const auto d = raw["prompt_tokens_details"]; d.isObject()) promptDetailsCached = readPositive(d["cached_tokens"]);
            if (const auto d = raw["completion_tokens_details"]; d.isObject()) completionReasoning = d["reasoning_tokens"].count();
            cacheReadInput = readPositive(raw["cache_read_input_tokens"]);
            promptCacheHit = readPositive(raw["prompt_cache_hit_tokens"]);
            cacheCreationInput = readPositive(raw["cache_creation_input_tokens"]);
            promptCacheWrite = readPositive(raw["prompt_cache_write_tokens"]);
        }
        line.hasUsage = usage.isObject() || raw.isObject();
        const std::int64_t cached = std::max(
            detailCached, cacheReadInput ? *cacheReadInput : promptDetailsCached ? *promptDetailsCached : promptCacheHit.value_or(0));
        const std::int64_t write = cacheCreationInput ? *cacheCreationInput : promptCacheWrite.value_or(0);
        line.tokens.input = std::max({inputTokens, promptTokens, TokenCounts::saturatingAdd(cached, write)});
        line.tokens.cachedInput = cached;
        line.tokens.cacheWriteInput = write;
        line.tokens.output = std::max(outputTokens, completionTokens);
        line.tokens.reasoning = std::max(detailReasoning, completionReasoning);
    }
    auto [model, provider] = workbuddy::resolveModel(streamModel, line.requestModelId);
    line.model = model;
    line.provider = provider;
    return line;
}

}  // namespace

WorkBuddyParser::WorkBuddyParser(std::string agentId, Deduper& deduper, SourceKind source, const std::string& root)
    : TranscriptParser(std::move(agentId), deduper, source), edition_(source),
      pathIsSubagent_(workbuddy::isSubagentPath(root, this->agentId())) {
    setSource(edition_);
    setSubagent(pathIsSubagent_);
}

AgentLink WorkBuddyParser::link() const {
    return {agentId(), edition_ == SourceKind::WorkBuddyAi ? LinkKind::WorkBuddyAiPath : LinkKind::WorkBuddyPath,
            std::nullopt, parent_links::claudeParentPath(agentId()), std::nullopt, 0};
}

void WorkBuddyParser::reset() {
    TranscriptParser::reset();
    openCalls_.clear();
    responses_.clear();
    pendingStartMs_.reset();
    setSource(edition_);
    setSubagent(pathIsSubagent_);
}

void WorkBuddyParser::feed(std::string_view text, ResponseSink& sink) {
    if (json::isBlank(text)) return;
    bool malformed = false;
    auto line = readLine(text, malformed);
    if (malformed) {
        countMalformed();
        return;
    }
    if (!line->timestampMs) return;
    const std::int64_t ts = *line->timestampMs;
    touch(ts);
    setProjectFromCwd(line->cwd);
    switch (line->item) {
        case Item::UserMessage:
            if (line->skipRun) break;
            setModes(ExecutionModes());
            openCalls_.clear();
            pendingStartMs_ = ts;
            setPhase(Phase::Generating, ts, ts);
            break;
        case Item::FunctionCallResult:
            applyToolResult(*line, ts);
            break;
        case Item::AssistantMessage:
            if (line->skipRun) {
                // A notice the CLI wrote: an interrupted or failed request ends the turn, hook output changes nothing.
                if (line->incomplete || line->hasError) idle(ts);
                break;
            }
            applyOutput(*line, ts, sink);
            break;
        case Item::FunctionCall:
        case Item::Reasoning:
        case Item::ModelUsage:
            applyOutput(*line, ts, sink);
            break;
        case Item::Other:
            break;
    }
}

void WorkBuddyParser::applyToolResult(const Line& line, std::int64_t ts) {
    if (line.callId) openCalls_.erase(*line.callId);
    if (line.skipRun) {
        // A rejected or cancelled tool: no request follows once every call of the step is answered.
        if (openCalls_.empty()) idle(ts);
        return;
    }
    pendingStartMs_ = ts;
    if (openCalls_.empty()) setPhase(Phase::Generating, ts, ts);
}

void WorkBuddyParser::applyOutput(const Line& line, std::int64_t ts, ResponseSink& sink) {
    if (line.partialAborted) return;
    std::optional<std::string> key = line.messageId;
    if (!key && line.id) key = "workbuddy:" + *line.id;
    if (!key) return;
    if (auto owner = deduper().owner(*key); owner && *owner != agentId()) return;
    if (line.hasUsage && (line.tokens.input > 0 || line.tokens.output > 0)) upsert(*key, line, ts, sink);
    if (line.item == Item::ModelUsage) {
        idle(ts);
        return;
    }
    for (auto it = openCalls_.begin(); it != openCalls_.end();) {
        it = it->second != *key ? openCalls_.erase(it) : std::next(it);
    }
    if (line.item == Item::FunctionCall) {
        if (line.callId) openCalls_[*line.callId] = *key;
        setPhase(Phase::Tools, ts, std::nullopt);
    } else if (line.item == Item::AssistantMessage && openCalls_.empty()) {
        if (line.compaction) {
            // The request the compaction made room for follows.
            pendingStartMs_ = ts;
            setPhase(Phase::Generating, ts, ts);
        } else {
            setPhase(Phase::Idle, ts, std::nullopt);
        }
    }
}

void WorkBuddyParser::upsert(const std::string& key, const Line& line, std::int64_t ts, ResponseSink& sink) {
    ParsedResponse response;
    bool completion = false;
    const auto previous = responses_.find(key);
    if (previous != responses_.end()) {
        response = previous->second;
        response.endMs = std::max(response.endMs, ts);
        response.tokens = TokenCounts::max(response.tokens, line.tokens);
        if (line.model) response.model = line.model;
        if (line.provider) response.provider = line.provider;
        response.modes = response.modes.merge(native::routing(line.requestModelId));
    } else {
        // A key this file claimed before a reset is replayed as an update.
        completion = deduper().tryClaim(key, agentId());
        response.key = key;
        response.source = source();
        response.agentId = agentId();
        response.model = line.model;
        response.startMs = pendingStartMs_;
        response.endMs = ts;
        response.tokens = line.tokens;
        response.provider = line.provider;
        response.modes = native::routing(line.requestModelId);
        pendingStartMs_.reset();
    }
    setModel(response.model);
    setModes(response.modes);
    if (previous != responses_.end()) {
        const auto& p = previous->second;
        if (p.endMs == response.endMs && p.tokens == response.tokens && p.model == response.model &&
            p.provider == response.provider && p.modes == response.modes) {
            return;
        }
    }
    responses_[key] = response;
    sink.onResponse(response, completion);
}

void WorkBuddyParser::idle(std::int64_t ts) {
    openCalls_.clear();
    pendingStartMs_.reset();
    setPhase(Phase::Idle, ts, std::nullopt);
}

}  // namespace agentspeed
