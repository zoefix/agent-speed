#include <limits>

#include "agentspeed/devin.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/text.hpp"

// METRICS 4.17 Opening and rows, Calls: one `chat_message` and the call it records.
namespace agentspeed::devin {

namespace {

// A string trimmed; nullopt when it is no string or blank.
std::optional<std::string> trimmedText(const json::Value& value) {
    const auto raw = value.string();
    if (!raw) return std::nullopt;
    auto text = text::strip(*raw);
    if (text.empty()) return std::nullopt;
    return text;
}

Role roleOf(const json::Value& value) {
    if (value.equals("assistant")) return Role::Assistant;
    if (value.equals("user")) return Role::User;
    if (value.equals("tool")) return Role::Tool;
    return Role::Other;
}

std::int64_t saturatingSum(std::int64_t a, std::int64_t b) { return TokenCounts::saturatingAdd(a, b); }

}  // namespace

std::optional<Phase> Message::phase() const {
    switch (role) {
        case Role::User:
        case Role::Tool: return Phase::Generating;
        case Role::Assistant: return hasToolCalls || finishedWithToolCalls ? Phase::Tools : Phase::Idle;
        case Role::Other: break;
    }
    return std::nullopt;
}

std::optional<Message> Message::parse(std::string_view json) {
    json::Document document;
    if (!document.parse(json) || !document.root().isObject()) return std::nullopt;
    const auto root = document.root();
    Message message;
    message.role = roleOf(root["role"]);
    const auto tools = root["tool_calls"];
    message.hasToolCalls = tools.isArray() && tools.size() > 0;
    const auto metadata = root["metadata"];
    if (!metadata.isObject()) return message;
    message.requestId = trimmedText(metadata["request_id"]);
    message.model = trimmedText(metadata["generation_model"]);
    message.finishedWithToolCalls = metadata["finish_reason"].equals("tool_calls");
    if (const auto m = metadata["metrics"]; m.isObject()) {
        Metrics metrics;
        metrics.input = m["input_tokens"].count();
        metrics.output = m["output_tokens"].count();
        metrics.cacheRead = m["cache_read_tokens"].count();
        metrics.cacheCreation = m["cache_creation_tokens"].count();
        metrics.totalMs = m["total_time_ms"].truncated();
        metrics.ttftMs = m["ttft_ms"].truncated();
        message.metrics = metrics;
    }
    return message;
}

std::string callKey(const std::string& session, const std::optional<std::string>& requestId, std::int64_t rowId) {
    return requestId ? "devin:" + session + ":request:" + *requestId : "devin:" + session + ":row:" + std::to_string(rowId);
}

std::optional<std::string> sessionModel(const std::optional<std::string>& raw) {
    if (!raw) return std::nullopt;
    auto model = text::strip(*raw);
    if (model.empty() || fsx::equalsNoCase(model, "adaptive")) return std::nullopt;
    return model;
}

std::optional<ParsedResponse> response(const Message& message, const std::string& session, std::int64_t rowId, std::int64_t endMs,
                                       const std::optional<std::string>& sessionModel, const std::string& agentId) {
    if (message.role != Role::Assistant || !message.metrics) return std::nullopt;
    const auto& m = *message.metrics;
    const auto input = saturatingSum(saturatingSum(m.input, m.cacheRead), m.cacheCreation);
    if (input == 0 && m.output == 0) return std::nullopt;
    ParsedResponse r;
    r.key = callKey(session, message.requestId, rowId);
    r.source = SourceKind::Devin;
    r.agentId = agentId;
    r.model = message.model ? message.model : sessionModel;
    r.endMs = endMs;
    if (m.totalMs && *m.totalMs > 0) {
        const auto total = *m.totalMs;
        r.startMs = endMs < std::numeric_limits<std::int64_t>::min() + total ? std::numeric_limits<std::int64_t>::min() : endMs - total;
        if (m.ttftMs && *m.ttftMs > 0 && *m.ttftMs <= total) r.firstMs = *r.startMs + *m.ttftMs;
    }
    r.tokens.input = input;
    r.tokens.cachedInput = m.cacheRead;
    r.tokens.cacheWriteInput = m.cacheCreation;
    r.tokens.output = m.output;
    return r;
}

}  // namespace agentspeed::devin
