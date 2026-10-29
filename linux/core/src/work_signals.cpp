#include "agentspeed/work_signals.hpp"

#include <algorithm>
#include <set>
#include <vector>

#include "agentspeed/task_events.hpp"
#include "tasks/internal.hpp"

namespace agentspeed::work {

namespace {

constexpr std::int64_t kRecentMs = 600'000;
constexpr std::int64_t kFreshMs = 120'000;
constexpr std::int64_t kLoopRepeats = 3;
constexpr std::size_t kNoveltyMin = 8;
constexpr std::size_t kRecoveryWindow = 5;
constexpr std::string_view kSpace = " \t\n\r\v\f";

std::optional<std::int64_t> exitCodeOf(std::string_view text) {
    const auto stripped = tasks::detail::lstrip(text, kSpace);
    if (stripped.empty() || stripped[0] != '{') return std::nullopt;
    const auto head = tasks::detail::leadingObject(stripped);
    if (!head) return std::nullopt;
    const auto code = head->root()["exit_code"];
    if (!code.isIntegerLiteral()) return std::nullopt;
    return code.truncated();
}

using Key = std::pair<std::string, std::string>;
Key keyOf(const Action& a) { return {a.tool, a.digest}; }

// How many times the newest block of `size` actions repeats back to back, and where the run starts.
std::pair<std::int64_t, std::size_t> repeatRun(const std::vector<const Action*>& actions, std::size_t size) {
    if (actions.size() < size) return {0, actions.size()};
    const auto same = [&](std::size_t a, std::size_t b) {
        for (std::size_t k = 0; k < size; ++k) {
            if (keyOf(*actions[a + k]) != keyOf(*actions[b + k])) return false;
        }
        return true;
    };
    std::int64_t count = 1;
    std::size_t end = actions.size() - size;
    while (end >= size && same(end - size, actions.size() - size)) {
        ++count;
        end -= size;
    }
    return {count, end};
}

}  // namespace

std::optional<std::int64_t> codexExitCode(const json::Value& output) {
    if (output.isArray()) {
        for (const auto& item : output.elements()) {
            if (item.isObject() && item["type"].equals("input_text")) {
                const auto text = item["text"].string();
                return text ? exitCodeOf(*text) : std::nullopt;
            }
        }
        return std::nullopt;
    }
    const auto text = output.string();
    return text ? exitCodeOf(*text) : std::nullopt;
}

void ActionLog::clear() {
    actions_.clear();
    byCall_.clear();
}

void ActionLog::call(const json::Value& callId, std::int64_t at, const json::Value& tool, const json::Value& input) {
    if (!callId.isString() || !tool.isString()) return;
    const auto id = *callId.string();
    if (byCall_.contains(id)) return;
    Action a{at, *tool.string(), tasks::inputDigest(input), std::nullopt, nextSerial_++};
    byCall_[id] = a.serial;
    actions_.push_back(std::move(a));
    if (actions_.size() > kMaxActions) {
        const auto dropped = actions_.front().serial;
        actions_.pop_front();
        std::erase_if(byCall_, [&](const auto& entry) { return entry.second == dropped; });
    }
}

void ActionLog::result(const json::Value& callId, std::optional<bool> failed) {
    if (!callId.isString() || !failed || actions_.empty()) return;
    const auto it = byCall_.find(*callId.string());
    if (it == byCall_.end()) return;
    const auto index = it->second - actions_.front().serial;
    if (index < actions_.size()) actions_[index].failed = failed;
}

void ActionLog::feed(SourceKind source, const json::Value& obj, std::int64_t at) {
    switch (source) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop: {
            const auto content = obj["message"]["content"];
            if (!content.isArray()) return;
            for (const auto& block : content.elements()) {
                if (!block.isObject()) continue;
                if (obj["type"].equals("assistant") && block["type"].equals("tool_use")) {
                    call(block["id"], at, block["name"], block["input"]);
                } else if (obj["type"].equals("user") && block["type"].equals("tool_result")) {
                    result(block["tool_use_id"], block["is_error"].isTrue());
                }
            }
            return;
        }
        case SourceKind::Codex: {
            const auto payload = obj["payload"];
            if (!obj["type"].equals("response_item") || !payload.isObject()) return;
            const auto kind = payload["type"];
            if (kind.equals("function_call") || kind.equals("custom_tool_call")) {
                call(payload["call_id"], at, payload["name"], payload[kind.equals("function_call") ? "arguments" : "input"]);
            } else if (kind.equals("function_call_output") || kind.equals("custom_tool_call_output")) {
                const auto output = payload["output"];
                std::optional<std::int64_t> code;
                if (const auto text = output.string(); text && tasks::detail::lstrip(*text, kSpace).starts_with("[")) {
                    json::Document doc;
                    code = doc.parse(*text) ? codexExitCode(doc.root()) : codexExitCode(output);
                } else {
                    code = codexExitCode(output);
                }
                result(payload["call_id"], code ? std::optional<bool>(*code != 0) : std::nullopt);
            }
            return;
        }
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw: {
            const auto message = obj["type"].equals("message") ? obj["message"] : json::Value();
            if (!message.isObject()) return;
            if (message["role"].equals("assistant")) {
                const auto content = message["content"];
                if (!content.isArray()) return;
                for (const auto& block : content.elements()) {
                    if (block.isObject() && block["type"].equals("toolCall")) call(block["id"], at, block["name"], block["arguments"]);
                }
            } else if (message["role"].equals("toolResult")) {
                result(message["toolCallId"], message["isError"].isTrue());
            }
            return;
        }
        case SourceKind::Droid: {
            const auto message = obj["type"].equals("message") ? obj["message"] : json::Value();
            if (!message.isObject() || !message["content"].isArray()) return;
            for (const auto& block : message["content"].elements()) {
                if (!block.isObject()) continue;
                if (message["role"].equals("assistant") && block["type"].equals("tool_use")) {
                    call(block["id"], at, block["name"], block["input"]);
                } else if (message["role"].equals("user") && block["type"].equals("tool_result")) {
                    result(block["tool_use_id"], block["is_error"].isTrue());
                }
            }
            return;
        }
        default:
            return;
    }
}

Signals signals(const std::deque<Action>& actions, std::int64_t now) {
    std::vector<const Action*> recent;
    for (const auto& a : actions) {
        if (a.at >= now - kRecentMs) recent.push_back(&a);
    }
    std::int64_t looping = 0, repeating = 0;
    if (!recent.empty() && now - recent.back()->at < kFreshMs) {
        for (std::size_t size = 1; size <= 3; ++size) {
            const auto [count, start] = repeatRun(recent, size);
            if (count < kLoopRepeats) continue;
            const bool failure = std::any_of(recent.begin() + static_cast<std::ptrdiff_t>(start), recent.end(),
                                             [](const Action* a) { return a->failed == true; });
            (failure ? looping : repeating) = std::max(failure ? looping : repeating, count);
        }
    }
    Signals out;
    for (std::size_t i = 0; i < recent.size(); ++i) {
        if (recent[i]->failed != true) continue;
        ++out.failed;
        const auto last = std::min(recent.size(), i + 1 + kRecoveryWindow);
        for (std::size_t k = i + 1; k < last; ++k) {
            const auto* b = recent[k];
            if (b->tool == recent[i]->tool && b->failed == false && b->at - recent[i]->at <= kRecentMs) {
                ++out.recovered;
                break;
            }
        }
    }
    if (recent.size() >= kNoveltyMin) {
        std::set<Key> distinct;
        for (const auto* a : recent) distinct.insert(keyOf(*a));
        out.novelty = static_cast<double>(distinct.size()) / static_cast<double>(recent.size());
    }
    if (looping) out.looping = looping;
    if (!looping && repeating) out.repeating = repeating;
    return out;
}

}  // namespace agentspeed::work
