#include "agentspeed/actions.hpp"

#include <openssl/evp.h>

#include <algorithm>

#include "agentspeed/rfc3339.hpp"

namespace agentspeed::work {

namespace {

constexpr std::int64_t kCopyMs = 2000;
constexpr std::string_view kJsonSpace = " \t\r\n";
constexpr std::string_view kSpace = " \t\n\r\v\f";

std::optional<std::int64_t> timestampOf(const json::Value& v) {
    const auto text = v.string();
    return text ? parseTimestamp(*text) : std::nullopt;
}

std::string_view lstrip(std::string_view text, std::string_view chars) {
    const auto at = text.find_first_not_of(chars);
    return at == std::string_view::npos ? std::string_view() : text.substr(at);
}

std::string sha16(std::string_view text) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    EVP_Digest(text.data(), text.size(), digest, &length, EVP_sha256(), nullptr);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (int i = 0; i < 8; ++i) {
        out += kHex[digest[i] >> 4];
        out += kHex[digest[i] & 15];
    }
    return out;
}

// The `exit_code` of the JSON object that starts the text (after JSON whitespace), ignoring what follows it.
std::optional<std::int64_t> leadingExitCode(std::string_view text) {
    const auto stripped = lstrip(lstrip(text, kSpace), kJsonSpace);
    if (stripped.empty() || stripped[0] != '{') return std::nullopt;
    int depth = 0;
    bool inString = false;
    for (std::size_t i = 0; i < stripped.size(); ++i) {
        const char c = stripped[i];
        if (inString) {
            if (c == '\\') {
                ++i;
            } else if (c == '"') {
                inString = false;
            }
            continue;
        }
        if (c == '"') {
            inString = true;
        } else if (c == '{' || c == '[') {
            ++depth;
        } else if ((c == '}' || c == ']') && --depth == 0) {
            const std::string head(stripped.substr(0, i + 1));
            json::Document doc;
            if (!doc.parse(head) || !doc.root().isObject()) return std::nullopt;
            const auto code = doc.root()["exit_code"];
            if (!code.isIntegerLiteral()) return std::nullopt;
            return code.truncated();
        }
    }
    return std::nullopt;
}

// METRICS 4.7 step 1: an omp, Pi or OpenClaw assistant message with usage.
bool ompGuard(const json::Value& message) {
    return message["model"].isString() && message["provider"].isString() && message["api"].isString() && message["usage"].isObject();
}

// METRICS 4.14 Mirrors: a delegated turn mirrored into the transcript.
bool openclawMirror(const TranscriptParser& p, const json::Value& message) {
    const auto meta = message["__openclaw"];
    const auto origin = meta.isObject() ? meta["mirrorOrigin"] : json::Value();
    const auto provider = message["provider"].string().value_or("");
    return (origin.isString() && !origin.raw().empty()) || (message["api"].equals("cli") && provider == "claude-cli") ||
           provider.rfind("acp-", 0) == 0 || p.copyFacts().mirrorWindow;
}

// Python truthiness of a candidate key.
bool truthy(const json::Value& v) {
    if (!v.valid()) return false;
    switch (v.type()) {
        case json::Type::Null:
        case json::Type::False:
            return false;
        case json::Type::True:
            return true;
        case json::Type::Number:
            return v.number().value_or(0) != 0;
        case json::Type::String:
            return !v.raw().empty();
        case json::Type::Array:
        case json::Type::Object:
            return v.size() > 0;
    }
    return false;
}

std::optional<bool> codexFailed(const json::Value& output) {
    std::optional<std::int64_t> code;
    if (const auto text = output.string(); text && lstrip(*text, kSpace).starts_with("[")) {
        json::Document doc;
        code = doc.parse(*text) ? codexExitCode(doc.root()) : codexExitCode(output);
    } else {
        code = codexExitCode(output);
    }
    return code ? std::optional<bool>(*code != 0) : std::nullopt;
}

}  // namespace

bool hasActions(SourceKind source) {
    switch (source) {
        case SourceKind::Codex:
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop:
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw:
        case SourceKind::Droid:
            return true;
        default:
            return false;
    }
}

std::string inputDigest(const json::Value& value) {
    if (!value.valid() || value.isNull()) return sha16("");
    if (value.isString()) return sha16(*value.string());
    return sha16(value.raw());
}

std::optional<std::int64_t> codexExitCode(const json::Value& output) {
    if (output.isArray()) {
        for (const auto& item : output.elements()) {
            if (item.isObject() && item["type"].equals("input_text")) {
                const auto text = item["text"].string();
                return text ? leadingExitCode(*text) : std::nullopt;
            }
        }
        return std::nullopt;
    }
    const auto text = output.string();
    return text ? leadingExitCode(*text) : std::nullopt;
}

std::vector<ToolEvent> toolEvents(SourceKind source, const json::Value& obj) {
    std::vector<ToolEvent> out;
    const auto addCall = [&](const json::Value& id, const json::Value& tool, const json::Value& input) {
        if (!id.isString() || !tool.isString()) return;
        out.push_back({ToolEvent::Kind::Call, *id.string(), *tool.string(), inputDigest(input), std::nullopt});
    };
    const auto addResult = [&](const json::Value& id, std::optional<bool> failed) {
        if (id.isString()) out.push_back({ToolEvent::Kind::Result, *id.string(), {}, {}, failed});
    };
    switch (source) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop:
        case SourceKind::Droid: {
            const bool droid = source == SourceKind::Droid;
            const auto message = obj["message"];
            if ((droid && !obj["type"].equals("message")) || !message.isObject() || !message["content"].isArray()) break;
            const auto role = droid ? message["role"] : obj["type"];
            for (const auto& block : message["content"].elements()) {
                if (!block.isObject()) continue;
                if (role.equals("assistant") && block["type"].equals("tool_use")) {
                    addCall(block["id"], block["name"], block["input"]);
                } else if (role.equals("user") && block["type"].equals("tool_result")) {
                    addResult(block["tool_use_id"], block["is_error"].isTrue());
                }
            }
            break;
        }
        case SourceKind::Codex: {
            const auto payload = obj["payload"];
            if (!obj["type"].equals("response_item") || !payload.isObject()) break;
            const auto type = payload["type"];
            if (type.equals("function_call") || type.equals("custom_tool_call")) {
                addCall(payload["call_id"], payload["name"], payload[type.equals("function_call") ? "arguments" : "input"]);
            } else if (type.equals("function_call_output") || type.equals("custom_tool_call_output")) {
                addResult(payload["call_id"], codexFailed(payload["output"]));
            }
            break;
        }
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw: {
            const auto message = obj["type"].equals("message") ? obj["message"] : json::Value();
            if (!message.isObject()) break;
            if (message["role"].equals("assistant") && message["content"].isArray()) {
                for (const auto& block : message["content"].elements()) {
                    if (block.isObject() && block["type"].equals("toolCall")) addCall(block["id"], block["name"], block["arguments"]);
                }
            } else if (message["role"].equals("toolResult")) {
                addResult(message["toolCallId"], message["isError"].isTrue());
            }
            break;
        }
        default:
            break;
    }
    return out;
}

void ActionLog::clear() {
    actions_.clear();
    byCall_.clear();
    ++version_;
}

void ActionLog::record(const std::vector<ToolEvent>& events, std::int64_t at, const std::optional<std::string>& model, bool seeded) {
    for (const auto& e : events) {
        if (e.kind == ToolEvent::Kind::Call) {
            if (byCall_.contains(e.id)) continue;
            Action a;
            a.callMs = at;
            a.tool = e.tool;
            a.digest = e.digest;
            a.model = model;
            a.seeded = seeded;
            a.serial = nextSerial_++;
            byCall_.emplace(e.id, a.serial);
            actions_.push_back(std::move(a));
            ++version_;
            if (cap_ > 0 && actions_.size() > cap_) actions_.pop_front();
            continue;
        }
        const auto it = byCall_.find(e.id);
        if (it == byCall_.end() || actions_.empty() || it->second < actions_.front().serial) continue;
        auto& action = actions_[it->second - actions_.front().serial];
        if (action.resultMs) continue;
        action.resultMs = at;
        action.failed = e.failed;
        ++version_;
        if (onResult_) onResult_(action);
    }
}

void ActionReader::clear() {
    log_.clear();
    headerSeen_ = false;
}

void ActionReader::onLine(const TranscriptParser& parser, const json::Value& line, std::string_view) {
    Gate gate;
    switch (parser.source()) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop:
            gate = claude(parser, line);
            break;
        case SourceKind::Codex:
            gate = codex(parser, line);
            break;
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw:
            gate = piFamily(parser, line);
            break;
        case SourceKind::Droid:
            gate = droid(parser, line);
            break;
        default:
            return;
    }
    if (!gate.at) return;
    auto events = toolEvents(parser.source(), line);
    if (!gate.calls) std::erase_if(events, [](const ToolEvent& e) { return e.kind == ToolEvent::Kind::Call; });
    if (!events.empty()) log_.record(events, *gate.at, parser.model(), seeding_);
}

bool ActionReader::ownedByOther(const TranscriptParser& p, const std::string& key) const {
    const auto owner = p.dedupeSet().owner(key);
    return owner && *owner != path_;
}

ActionReader::Gate ActionReader::claude(const TranscriptParser& p, const json::Value& obj) const {
    Gate gate{timestampOf(obj["timestamp"])};
    if (!gate.at || !obj["type"].equals("assistant")) return gate;
    const auto message = obj["message"].isObject() ? obj["message"] : json::Value();
    if (message["model"].equals("<synthetic>")) return {};
    json::Value key;
    for (const auto& candidate : {message["id"], obj["requestId"], obj["uuid"]}) {
        if (truthy(candidate)) {
            key = candidate;
            break;
        }
    }
    gate.calls = key.isString() && !key.raw().empty() && !ownedByOther(p, *key.string());
    return gate;
}

ActionReader::Gate ActionReader::codex(const TranscriptParser& p, const json::Value& obj) const {
    const auto t = timestampOf(obj["timestamp"]);
    if (!t || !obj["type"].equals("response_item")) return {};
    const auto facts = p.copyFacts();
    if (facts.createdMs && *t < *facts.createdMs - kCopyMs) return {};
    const auto lineOrdinal = obj["ordinal"].ordinal();
    if (facts.startOrdinal && lineOrdinal && *lineOrdinal < *facts.startOrdinal) return {};
    return {t};
}

ActionReader::Gate ActionReader::piFamily(const TranscriptParser& p, const json::Value& obj) {
    const auto kind = obj["type"];
    if (kind.equals("session")) {
        headerSeen_ = true;
        return {};
    }
    if (!headerSeen_ || !obj["id"].isString()) return {};
    const auto t = timestampOf(obj["timestamp"]);
    const auto created = p.copyFacts().createdMs;
    if (!t || (created && *t < *created - kCopyMs)) return {};
    const auto message = obj["message"];
    if (!kind.equals("message") || !message.isObject() || !message["role"].equals("assistant")) return {t};
    if (!ompGuard(message)) return {};
    std::int64_t stamp = *t;
    if (const auto ts = message["timestamp"].finiteNumber(); ts && *ts > 0) stamp = *message["timestamp"].truncated();
    const auto key = std::string(sourceId(p.source())) + ":" + *obj["id"].string() + ":" + std::to_string(stamp);
    if (ownedByOther(p, key) || (p.source() == SourceKind::OpenClaw && openclawMirror(p, message))) return {};
    return {t};
}

ActionReader::Gate ActionReader::droid(const TranscriptParser& p, const json::Value& obj) const {
    Gate gate{timestampOf(obj["timestamp"])};
    const auto message = obj["message"];
    if (!gate.at || !obj["type"].equals("message") || !message.isObject() || !message["role"].equals("assistant")) return gate;
    gate.calls = obj["id"].isString() && !obj["id"].raw().empty() && !ownedByOther(p, "droid:" + *obj["id"].string());
    return gate;
}

}  // namespace agentspeed::work
