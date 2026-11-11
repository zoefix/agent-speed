#include "quota_bridge.hpp"

#include <unistd.h>

#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/ordered_json.hpp"

namespace agentspeed::relay {

namespace {

constexpr mode_t kHelperMode = 0700;
constexpr std::size_t kSettingsLimit = 16u << 20;

std::string abbreviated(const std::string& path, const std::string& home) {
    if (home.empty() || home == "/" || path.rfind(home + "/", 0) != 0) return path;
    return "~" + path.substr(home.size());
}

std::string joined(const std::string& folder, std::string_view name) {
    return (folder.empty() || folder.back() != '/' ? folder + "/" : folder) + std::string(name);
}

}  // namespace

std::string absoluteFolder(const std::string& folder) {
    std::string out = folder;
    if (out.empty() || out.front() != '/') {
        char buffer[4096];
        const char* cwd = ::getcwd(buffer, sizeof buffer);
        out = joined(cwd ? cwd : "/", out);
    }
    while (out.size() > 1 && out.back() == '/') out.pop_back();
    return out;
}

std::string helperCommand(const std::string& folder) {
    return quota::bridge::posixCommand(joined(folder, quota::kHelperName)) + " --folder " + quota::bridge::posixCommand(folder);
}

HostBridge::HostBridge(const std::string& folder, const quota::Environment& environment, std::string self)
    : bridge_(quota::bridge::Paths{quota::claudeSettingsFile(environment), joined(folder, "quota"), joined(folder, quota::kHelperName)},
              helperCommand(folder)),
      self_(std::move(self)),
      settingsPath_(abbreviated(bridge_.paths().settingsFile, quota::homeFolder(environment))) {}

// This folder's bridge was on when the last session ended: its sidecar is there, or the status line runs its helper.
bool HostBridge::leftOn() const {
    if (quota::files::stampOf(bridge_.paths().sidecar(), false)) return true;
    const auto read = quota::files::readFile(bridge_.paths().settingsFile, kSettingsLimit, true);
    if (read.status != quota::files::ReadStatus::Ok) return false;
    const auto settings = quota::ordered_json::parse(read.bytes);
    const auto* line = settings && settings->isObject() ? settings->find("statusLine") : nullptr;
    const auto* command = line && line->isObject() ? line->find("command") : nullptr;
    return command && command->isString() && command->text == bridge_.command();
}

HostBridge::State HostBridge::apply(bool on, bool first) {
    const bool wasOn = on_;
    on_ = on;
    if (on) {
        if (first && leftOn()) {
            failure_.reset();
            if (!bridge_.installHelper(self_, kHelperMode)) failure_ = State::Failed;
            return check();
        }
        return first || !wasOn ? turnOn() : check();
    }
    failure_.reset();
    if (wasOn || (first && leftOn())) return turnOff();
    return State::Off;
}

HostBridge::State HostBridge::turnOn() {
    failure_.reset();
    switch (bridge_.turnOn(self_, kHelperMode)) {
        case quota::bridge::TurnOnResult::On: break;
        case quota::bridge::TurnOnResult::Unreadable: failure_ = State::Unreadable; break;
        case quota::bridge::TurnOnResult::Failed: failure_ = State::Failed; break;
    }
    return check();
}

HostBridge::State HostBridge::turnOff() { return bridge_.turnOff() ? State::Off : State::Failed; }

HostBridge::State HostBridge::check() {
    if (!on_) return State::Off;
    if (failure_) return *failure_;
    switch (bridge_.status()) {
        case quota::bridge::Status::Waiting: return State::Waiting;
        case quota::bridge::Status::Ok: return State::Ok;
        case quota::bridge::Status::Replaced: return State::Replaced;
        case quota::bridge::Status::Unreadable: return State::Unreadable;
        case quota::bridge::Status::Off:
        case quota::bridge::Status::Failed: break;
    }
    return State::Failed;
}

}  // namespace agentspeed::relay
