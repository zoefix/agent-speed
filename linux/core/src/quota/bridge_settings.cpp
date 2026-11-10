#include "agentspeed/quota/bridge.hpp"

// QUOTAS 5.2–5.4 as pure text transforms: what turning the bridge on or off makes of Claude Code's `settings.json`, and
// what its status is. Members keep their order and numbers their text; `statusLine` is replaced where it was.
namespace agentspeed::quota::bridge {

namespace {

// A missing file reads as `{}`; anything but a JSON object is nullopt.
std::optional<ordered_json::Value> settingsObject(const std::optional<std::string>& text) {
    if (!text) return ordered_json::Value::object();
    auto value = ordered_json::parse(*text);
    if (!value || !value->isObject()) return std::nullopt;
    return value;
}

std::string fileText(const ordered_json::Value& value) { return ordered_json::write(value) + "\n"; }

}  // namespace

std::string_view statusId(Status status) {
    switch (status) {
        case Status::Off: return "off";
        case Status::Waiting: return "waiting";
        case Status::Ok: return "ok";
        case Status::Replaced: return "replaced";
        case Status::Unreadable: return "unreadable";
        case Status::Failed: return "failed";
    }
    return "failed";
}

std::string posixCommand(std::string_view path) {
    std::string out = "'";
    for (const char c : path) {
        if (c == '\'') {
            out += "'\\''";
        } else {
            out += c;
        }
    }
    out += '\'';
    return out;
}

bool isOurs(const ordered_json::Value* statusLine) {
    if (!statusLine || !statusLine->isObject()) return false;
    const auto* type = statusLine->find("type");
    const auto* command = statusLine->find("command");
    return type && type->isString() && type->text == "command" && command && command->isString() &&
           command->text.find(kHelperName) != std::string::npos;
}

TurnOnEdit turnOnEdit(const std::optional<std::string>& settings, std::string_view command,
                      const std::optional<ordered_json::Value>& sidecar) {
    TurnOnEdit edit;
    auto current = settingsObject(settings);
    if (!current) {
        edit.previous = sidecar;
        return edit;
    }
    edit.readable = true;
    const auto* existing = current->find("statusLine");
    edit.wasOurs = isOurs(existing);
    if (edit.wasOurs) {
        edit.previous = sidecar;
    } else {
        auto previous = ordered_json::Value::object();
        previous.set("version", ordered_json::Value::number("1"));
        if (existing) previous.set("statusLine", *existing);
        edit.previous = std::move(previous);
    }
    auto line = ordered_json::Value::object();
    line.set("type", ordered_json::Value::string("command"));
    line.set("command", ordered_json::Value::string(std::string(command)));
    if (edit.previous) {
        if (const auto* old = edit.previous->find("statusLine"); old && old->isObject()) {
            if (const auto* padding = old->find("padding"); padding && padding->isFiniteNumber()) line.set("padding", *padding);
        }
    }
    current->set("statusLine", std::move(line));
    edit.settings = fileText(*current);
    return edit;
}

std::optional<std::string> turnOffEdit(const std::optional<std::string>& settings, const std::optional<ordered_json::Value>& previous) {
    if (!settings) return std::nullopt;
    auto current = settingsObject(settings);
    if (!current || !isOurs(current->find("statusLine"))) return std::nullopt;
    const auto* line = previous ? previous->find("statusLine") : nullptr;
    if (line) {
        current->set("statusLine", *line);
    } else {
        current->remove("statusLine");
    }
    return fileText(*current);
}

Status statusOf(const std::optional<std::string>& settings, bool hasQuotaFile) {
    const auto current = settingsObject(settings);
    if (!current) return Status::Unreadable;
    if (!isOurs(current->find("statusLine"))) return Status::Replaced;
    return hasQuotaFile ? Status::Ok : Status::Waiting;
}

}  // namespace agentspeed::quota::bridge
