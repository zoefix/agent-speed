#include "agentspeed/task_score.hpp"

#include <unicode/normalizer2.h>
#include <unicode/unistr.h>

#include <algorithm>
#include <set>

#include "agentspeed/rfc3339.hpp"
#include "task_eval.hpp"

namespace agentspeed::tasks {

namespace {

using detail::Evaluation;

const std::set<std::string> kContinuations = {
    "continue", "continue please", "please continue", "go on", "keep going", "proceed", "please proceed", "carry on",
    "go ahead", "resume", "ok", "okay", "ok continue", "ok go ahead", "yes", "y", "yep", "yes please", "sure", "do it", "lgtm",
    "繼續", "繼續吧", "請繼續", "繼續做", "接著做", "好", "好的", "好啊", "可以", "嗯", "是", "對", "行", "沒問題",
    "继续", "继续吧", "请继续", "继续做", "接着做", "对", "没问题",
    "続けて", "続けてください", "続行", "進めて", "進めてください", "はい", "お願いします", "うん", "いいよ", "どうぞ",
};
constexpr std::string_view kCodexAgents = "# AGENTS.md instructions";

// Python's str.isspace.
bool isSpace(char32_t c) {
    return (c >= 0x09 && c <= 0x0D) || (c >= 0x1C && c <= 0x20) || c == 0x85 || c == 0xA0 || c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

std::u32string decode(std::string_view text) {
    std::u32string out;
    for (std::size_t i = 0; i < text.size();) {
        const auto b = static_cast<unsigned char>(text[i]);
        const std::size_t n = b < 0x80 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
        char32_t cp = n == 1 ? b : n == 2 ? (b & 0x1F) : n == 3 ? (b & 0x0F) : (b & 0x07);
        for (std::size_t k = 1; k < n && i + k < text.size(); ++k) cp = (cp << 6) | (static_cast<unsigned char>(text[i + k]) & 0x3F);
        out.push_back(cp);
        i += n;
    }
    return out;
}

std::string encode(std::u32string_view text) {
    std::string out;
    for (const char32_t c : text) {
        if (c < 0x80) {
            out += static_cast<char>(c);
        } else if (c < 0x800) {
            out += static_cast<char>(0xC0 | (c >> 6));
            out += static_cast<char>(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            out += static_cast<char>(0xE0 | (c >> 12));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (c >> 18));
            out += static_cast<char>(0x80 | ((c >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (c & 0x3F));
        }
    }
    return out;
}

std::string nfkc(std::string_view text) {
    UErrorCode status = U_ZERO_ERROR;
    const auto* normalizer = icu::Normalizer2::getNFKCInstance(status);
    if (U_FAILURE(status)) return std::string(text);
    const auto source = icu::UnicodeString::fromUTF8(icu::StringPiece(text.data(), static_cast<std::int32_t>(text.size())));
    const auto normalized = normalizer->normalize(source, status);
    if (U_FAILURE(status)) return std::string(text);
    std::string out;
    normalized.toUTF8String(out);
    return out;
}

std::u32string_view pyStrip(std::u32string_view s) {
    while (!s.empty() && isSpace(s.front())) s.remove_prefix(1);
    while (!s.empty() && isSpace(s.back())) s.remove_suffix(1);
    return s;
}

std::string lstripText(std::string_view text) {
    const auto chars = decode(text);
    std::size_t i = 0;
    while (i < chars.size() && isSpace(chars[i])) ++i;
    return encode(std::u32string_view(chars).substr(i));
}

bool blankText(std::string_view text) { return pyStrip(decode(text)).empty(); }

bool startsWithTag(const std::string& head, std::initializer_list<std::string_view> tags) {
    for (const auto tag : tags) {
        const std::string opening = "<" + std::string(tag);
        if (head.compare(0, opening.size(), opening) != 0) continue;
        if (head.size() == opening.size()) return true;
        const char next = head[opening.size()];
        if (next == '>' || next == '/') return true;
        const auto rest = decode(std::string_view(head).substr(opening.size(), 4));
        if (!rest.empty() && isSpace(rest.front())) return true;
    }
    return false;
}

struct ContentParts {
    std::vector<std::string> texts;
    bool media = false;
    std::string lead;
};

bool typeIn(const json::Value& block, std::initializer_list<std::string_view> types) {
    for (const auto t : types) {
        if (block["type"].equals(t)) return true;
    }
    return false;
}

std::optional<ContentParts> contentParts(const json::Value& content, std::initializer_list<std::string_view> mediaTypes,
                                         std::initializer_list<std::string_view> textTypes = {"text"}) {
    ContentParts parts;
    if (content.isString()) {
        parts.texts.push_back(*content.string());
    } else if (content.isArray()) {
        for (const auto& block : content.elements()) {
            if (!block.isObject()) continue;
            if (typeIn(block, textTypes) && block["text"].isString()) parts.texts.push_back(*block["text"].string());
            if (typeIn(block, mediaTypes)) parts.media = true;
        }
    } else {
        return std::nullopt;
    }
    if (!parts.media && std::all_of(parts.texts.begin(), parts.texts.end(), [](const auto& t) { return blankText(t); })) return std::nullopt;
    if (!parts.texts.empty()) parts.lead = parts.texts.front();
    return parts;
}

std::string promptText(const json::Value& content, const std::vector<std::string>& texts) {
    if (content.isString()) return *content.string();
    std::string out;
    bool first = true;
    for (const auto& t : texts) {
        if (lstripText(t).starts_with("<")) continue;
        if (!first) out += "\n";
        out += t;
        first = false;
    }
    return out;
}

bool hasToolResult(const json::Value& content) {
    if (!content.isArray()) return false;
    for (const auto& b : content.elements()) {
        if (b.isObject() && b["type"].equals("tool_result")) return true;
    }
    return false;
}

PromptLine prompt(bool continuation) { return {PromptLine::Kind::Prompt, continuation, std::nullopt}; }

std::optional<PromptLine> claudeQueuedPrompt(const json::Value& obj) {
    const auto attachment = obj["attachment"];
    if (!attachment.isObject() || !attachment["type"].equals("queued_command")) return std::nullopt;
    if (!attachment["commandMode"].equals("prompt") || !attachment["humanTurn"].isTrue()) return std::nullopt;
    if (obj["isMeta"].isTrue() || attachment["isMeta"].isTrue()) return std::nullopt;
    const auto content = attachment["prompt"];
    const auto parts = contentParts(content, {"image", "document"});
    if (!parts) return std::nullopt;
    auto found = prompt(isContinuation(promptText(content, parts->texts)));
    if (const auto ts = attachment["timestamp"].string()) found.t = parseTimestamp(*ts);
    return found;
}

std::optional<PromptLine> claudePrompt(const json::Value& obj) {
    if (obj["type"].equals("attachment")) return claudeQueuedPrompt(obj);
    if (!obj["type"].equals("user") || obj["isMeta"].isTrue() || obj["isCompactSummary"].isTrue() ||
        obj["isVisibleInTranscriptOnly"].isTrue()) {
        return std::nullopt;
    }
    const auto message = obj["message"];
    const auto content = message.isObject() ? message["content"] : json::Value();
    if (hasToolResult(content)) return std::nullopt;
    const auto parts = contentParts(content, {"image", "document"});
    if (!parts) return std::nullopt;
    const auto head = lstripText(parts->lead);
    if (head.starts_with("[Request interrupted by user")) return std::nullopt;
    if (startsWithTag(head, {"local-command-stdout", "local-command-stderr"})) return PromptLine{PromptLine::Kind::CommandOutput, false, {}};
    if (startsWithTag(head, {"local-command-caveat", "bash-input", "bash-stdout", "bash-stderr", "user-memory-input", "system-reminder",
                             "task-notification"})) {
        return std::nullopt;
    }
    if (head.starts_with("<command-name>") || head.starts_with("<command-message>")) return PromptLine{PromptLine::Kind::Command, false, {}};
    return prompt(isContinuation(promptText(content, parts->texts)));
}

std::optional<PromptLine> codexPrompt(const json::Value& obj) {
    const auto payload = obj["payload"];
    if (!obj["type"].equals("response_item") || !payload.isObject() || !payload["type"].equals("message") || !payload["role"].equals("user")) {
        return std::nullopt;
    }
    const auto content = payload["content"];
    const auto parts = contentParts(content, {"input_image"}, {"input_text", "text"});
    if (!parts) return std::nullopt;
    const auto lead = lstripText(parts->lead);
    const bool tagged = lead.size() > 1 && lead[0] == '<' && std::isalpha(static_cast<unsigned char>(lead[1])) &&
                        static_cast<unsigned char>(lead[1]) < 0x80;
    if (tagged || lead.starts_with(kCodexAgents)) return std::nullopt;
    return prompt(isContinuation(promptText(content, parts->texts)));
}

std::optional<PromptLine> piFamilyPrompt(const json::Value& obj) {
    const auto message = obj["message"];
    if (!obj["type"].equals("message") || !message.isObject() || !message["role"].equals("user")) return std::nullopt;
    const auto parts = contentParts(message["content"], {"image"});
    if (!parts) return std::nullopt;
    std::string joined;
    for (std::size_t i = 0; i < parts->texts.size(); ++i) joined += (i ? "\n" : "") + parts->texts[i];
    return prompt(isContinuation(joined));
}

std::optional<PromptLine> droidPrompt(const json::Value& obj) {
    const auto message = obj["message"];
    if (!obj["type"].equals("message") || !message.isObject() || !message["role"].equals("user")) return std::nullopt;
    if (const auto visibility = message["visibility"]; visibility.valid() && !visibility.isNull()) return std::nullopt;
    const auto content = message["content"];
    if (hasToolResult(content)) return std::nullopt;
    const auto parts = contentParts(content, {"image"});
    if (!parts) return std::nullopt;
    return prompt(isContinuation(promptText(content, parts->texts)));
}

std::u16string utf16(std::string_view text) {
    std::u16string out;
    for (const char32_t c : decode(text)) {
        if (c < 0x10000) {
            out.push_back(static_cast<char16_t>(c));
        } else {
            out.push_back(static_cast<char16_t>(0xD800 + ((c - 0x10000) >> 10)));
            out.push_back(static_cast<char16_t>(0xDC00 + ((c - 0x10000) & 0x3FF)));
        }
    }
    return out;
}

}  // namespace

bool isScoredSource(SourceKind source) {
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

std::string normalizePrompt(std::string_view text) {
    const auto chars = decode(nfkc(text));
    auto s = pyStrip(chars);
    static const std::u32string kTrailing = U".!~,。、";
    while (!s.empty() && (kTrailing.find(s.back()) != std::u32string::npos || isSpace(s.back()))) s.remove_suffix(1);
    std::u32string out;
    bool pending = false;
    for (char32_t c : s) {
        if (isSpace(c)) {
            pending = !out.empty();
            continue;
        }
        if (pending) out.push_back(U' ');
        pending = false;
        out.push_back(c >= U'A' && c <= U'Z' ? c + 32 : c);
    }
    return encode(out);
}

bool isContinuation(std::string_view text) { return kContinuations.contains(normalizePrompt(text)); }

std::optional<PromptLine> promptLine(SourceKind source, const json::Value& line) {
    if (!line.isObject()) return std::nullopt;
    switch (source) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop:
            return claudePrompt(line);
        case SourceKind::Codex:
            return codexPrompt(line);
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw:
            return piFamilyPrompt(line);
        case SourceKind::Droid:
            return droidPrompt(line);
        default:
            return std::nullopt;
    }
}

std::vector<Event> segment(const std::vector<ClassifiedLine>& lines) {
    std::vector<Event> out;
    std::optional<std::string> pending;
    for (const auto& line : lines) {
        if (line.line == "prompt" || line.line == "command") {
            Event e;
            e.type = Event::Type::Prompt;
            e.t = line.t;
            e.continuation = line.line == "prompt" && line.continuation;
            out.push_back(std::move(e));
            pending = line.line;
        } else if (line.line == "commandOutput") {
            if (pending == "command") out.pop_back();
            pending.reset();
        } else if (line.line == "assistant" && pending) {
            if (line.duplicate) out.pop_back();
            pending.reset();
        }
    }
    return out;
}

std::vector<Task> splitTasks(const std::vector<Event>& events) {
    std::vector<Task> tasks;
    for (const auto& e : events) {
        if (e.type == Event::Type::Prompt && !(e.continuation && !tasks.empty())) {
            tasks.push_back({e.t, {e}});
        } else {
            if (tasks.empty()) tasks.push_back({e.t, {}});
            tasks.back().events.push_back(e);
        }
    }
    return tasks;
}

std::int64_t taskEnd(const Task& task) {
    auto end = task.startMs;
    for (const auto& e : task.events) {
        end = std::max(end, e.t);
        if (e.outcome && e.outcome->t) end = std::max(end, *e.outcome->t);
    }
    return end;
}

std::optional<Task> currentTask(const std::vector<Event>& events, std::optional<std::int64_t> now) {
    std::vector<Event> visible;
    for (const auto& e : events) {
        if (!now || e.t <= *now) visible.push_back(e);
    }
    auto tasks = splitTasks(visible);
    if (tasks.empty()) return std::nullopt;
    return std::move(tasks.back());
}

TaskResult evaluateTask(const std::vector<Event>& events, std::optional<std::int64_t> now) { return Evaluation(events, now).result(); }

std::int64_t roundHalfAway(double value) {
    return value >= 0 ? static_cast<std::int64_t>(value + 0.5) : -static_cast<std::int64_t>(-value + 0.5);
}

AgentValue agentValue(const std::vector<Event>& events, Phase phase, std::int64_t now) {
    AgentValue v;
    const auto task = currentTask(events, now);
    if (!task) return v;
    const Evaluation ev(task->events, now);
    v.state = ev.state;
    if (!ev.score) return v;
    v.form = phase == Phase::Idle ? "resting" : "live";
    v.score = ev.score;
    v.shown = roundHalfAway(*ev.score);
    return v;
}

bool ordinalLess(std::string_view a, std::string_view b) { return utf16(a) < utf16(b); }

Highest highest(const std::vector<ValuedAgent>& agents) {
    for (const auto* form : {"live", "resting"}) {
        std::vector<const ValuedAgent*> candidates;
        for (const auto& a : agents) {
            if (a.value.form == form) candidates.push_back(&a);
        }
        if (candidates.empty()) continue;
        double best = *candidates.front()->value.score;
        for (const auto* a : candidates) best = std::max(best, *a->value.score);
        const ValuedAgent* pick = nullptr;
        for (const auto* a : candidates) {
            if (*a->value.score != best) continue;
            if (!pick || a->lastLineMs > pick->lastLineMs || (a->lastLineMs == pick->lastLineMs && ordinalLess(a->id, pick->id))) pick = a;
        }
        return {form, best, roundHalfAway(best), pick->id};
    }
    return {};
}

}  // namespace agentspeed::tasks
