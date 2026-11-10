#include <algorithm>

#include "agentspeed/json.hpp"
#include "agentspeed/qoder.hpp"
#include "agentspeed/text.hpp"

// METRICS 4.18 Calls: the JSON columns of a row (content is never read) and the call an assistant row records.
namespace agentspeed::qoder {

namespace {

std::optional<json::Document> objectOf(std::string_view text) {
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) return std::nullopt;
    return document;
}

}  // namespace

std::optional<Tokens> tokensOf(std::string_view json) {
    const auto document = objectOf(json);
    if (!document) return std::nullopt;
    const auto root = document->root();
    return Tokens{root["prompt_tokens"].count(), root["completion_tokens"].count(), root["cached_tokens"].count()};
}

std::optional<std::string> modelKeyOf(std::string_view json) {
    const auto document = objectOf(json);
    if (!document) return std::nullopt;
    const auto raw = document->root()["model_key"].string();
    if (!raw) return std::nullopt;
    auto model = text::strip(*raw);
    if (model.empty()) return std::nullopt;
    return model;
}

std::optional<std::int64_t> finishedAtOf(std::string_view json) {
    const auto document = objectOf(json);
    if (!document) return std::nullopt;
    const auto turn = document->root()["ai-coding/turn-info"];
    if (!turn.isObject()) return std::nullopt;
    return turn["finishedAt"].truncated();
}

std::string callKey(const std::string& session, const std::optional<std::string>& messageId, std::int64_t rowId) {
    return messageId ? "qoder:" + *messageId : "qoder:" + session + ":row:" + std::to_string(rowId);
}

std::optional<ParsedResponse> response(const Tokens& tokens, const std::string& session, const std::optional<std::string>& messageId,
                                       std::int64_t rowId, std::optional<std::int64_t> startMs, std::int64_t endMs,
                                       const std::optional<std::string>& model, const std::string& agentId) {
    if (tokens.prompt == 0 && tokens.completion == 0) return std::nullopt;
    ParsedResponse r;
    r.key = callKey(session, messageId, rowId);
    r.source = SourceKind::Qoder;
    r.agentId = agentId;
    r.model = model;
    r.startMs = startMs;
    r.endMs = endMs;
    // `prompt_tokens` already includes the cache read, OpenAI's way.
    r.tokens.input = tokens.prompt;
    r.tokens.cachedInput = std::min(tokens.cached, tokens.prompt);
    r.tokens.output = tokens.completion;
    return r;
}

std::vector<ParsedResponse> responsesOf(const std::vector<Row>& rows, const std::string& session, const std::string& agentId) {
    std::vector<ParsedResponse> out;
    std::optional<std::int64_t> previous;
    for (const auto& row : rows) {
        const auto start = previous;
        previous = row.timeMs;
        if (!row.timeMs || row.role != Role::Assistant || !row.tokens) continue;
        if (auto r = response(*row.tokens, session, row.messageId, row.rowId, start, *row.timeMs, row.model, agentId)) {
            out.push_back(std::move(*r));
        }
    }
    return out;
}

}  // namespace agentspeed::qoder
