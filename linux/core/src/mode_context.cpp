#include "agentspeed/mode_context.hpp"

#include <vector>

#include "agentspeed/fs_util.hpp"

namespace agentspeed {

std::optional<ModeContext::Stamp> ModeContext::stampOf(const std::string& path) {
    const auto stat = fsx::statOf(path);
    if (!stat) return std::nullopt;
    return Stamp{stat->length, stat->writeNs};
}

bool ModeContext::Config::refresh() {
    const auto current = stampOf(path);
    if (current == stamp) return false;
    stamp = current;
    auto next = read(path);
    const bool changed = next != value;
    value = std::move(next);
    return changed;
}

bool ModeContext::Gemini::refresh() {
    if (path.empty()) return false;
    const auto current = stampOf(path);
    if (current == stamp) return false;
    stamp = current;
    auto next = configured::gemini(path);
    const bool changed = next != value;
    value = std::move(next);
    return changed;
}

// Queries when the file changed, else only the sessions not queried yet.
bool ModeContext::Store::refresh(const std::set<std::string>& sessions) {
    const auto current = stampOf(path), wal = stampOf(path + "-wal");
    const bool fileChanged = current != stamp || wal != walStamp;
    stamp = current;
    walStamp = wal;
    std::vector<std::string> pending;
    for (const auto& session : sessions) {
        if (fileChanged || !queried.contains(session)) pending.push_back(session);
    }
    if (pending.empty()) return false;
    const auto read = session_modes::workBuddy(path, pending);
    bool changed = false;
    for (const auto& session : pending) {
        queried.insert(session);
        const auto it = read.find(session);
        auto value = it == read.end() ? ExecutionModes() : it->second;
        auto& held = values[session];
        changed |= value != held;
        held = std::move(value);
    }
    return changed;
}

void ModeContext::addRoot(RootFamily family, const std::string& root) {
    if (root.empty() || configs_.contains(root)) return;
    switch (family) {
        case RootFamily::Codex:
            configs_[root] = Config{configured::codexConfigPath(root), configured::codex, {}, {}};
            break;
        case RootFamily::Antigravity:
            configs_[root] = Config{paths::antigravityConfigOf(root), configured::antigravity, {}, {}};
            break;
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: {
            const auto folder = paths::workBuddyDataFolderOf(root);
            configs_[root] = Config{fsx::join(folder, "settings.json"), configured::workBuddy, {}, {}};
            stores_[root].path = fsx::join(folder, "workbuddy.db");
            break;
        }
        default:
            break;
    }
}

bool ModeContext::refresh(const std::map<std::string, std::set<std::string>>& workBuddySessions) {
    bool changed = false;
    for (auto& [root, config] : configs_) changed |= config.refresh();
    changed |= gemini_.refresh();
    static const std::set<std::string> none;
    for (auto& [root, store] : stores_) {
        const auto sessions = workBuddySessions.find(root);
        changed |= store.refresh(sessions == workBuddySessions.end() ? none : sessions->second);
    }
    return changed;
}

ExecutionModes ModeContext::configured(RootFamily family, const std::string& root, const std::optional<std::string>& model) const {
    if (family == RootFamily::Gemini) return gemini_.value.modes(model);
    const auto it = configs_.find(root);
    return it == configs_.end() ? ExecutionModes() : it->second.value;
}

ExecutionModes ModeContext::session(const std::string& root, const std::string& sessionId) const {
    const auto store = stores_.find(root);
    if (store == stores_.end()) return {};
    const auto value = store->second.values.find(sessionId);
    return value == store->second.values.end() ? ExecutionModes() : value->second;
}

}  // namespace agentspeed
