#include "agentspeed/quota/presence.hpp"

#include <string.h>

#include <algorithm>
#include <iterator>

#include "agentspeed/json.hpp"

namespace agentspeed::quota {

bool RunningSources::update(const std::vector<traffic::Process>& processes) {
    std::set<traffic::Key> listed;
    for (const auto& p : processes) listed.insert({p.pid, p.start});
    std::vector<traffic::Key> gone;
    std::set_difference(listed_.begin(), listed_.end(), listed.begin(), listed.end(), std::back_inserter(gone));
    table_.forget(gone);
    table_.refresh(processes);
    listed_ = std::move(listed);

    std::set<SourceKind> running;
    bool desktop = false;
    for (const auto& p : processes) {
        const auto* c = table_.classify({p.pid, p.start});
        if (c && c->role == traffic::Classification::Role::Agent && c->source) running.insert(*c->source);
        if (p.exe.empty()) continue;
        if (traffic::isClaudeDesktopProcess(table_.env(), p)) desktop = true;
        if (traffic::isCodexAppProcess(table_.env(), p)) running.insert(SourceKind::Codex);
    }
    if (desktop) running.insert(SourceKind::ClaudeDesktop);
    desktopStarted_ = desktop && !running_.contains(SourceKind::ClaudeDesktop);
    const bool changed = running != running_;
    running_ = std::move(running);
    return changed;
}

bool signedInMarker(SignInKind kind, std::string_view bytes) {
    json::Document doc;
    if (!doc.parse(bytes) || !doc.root().isObject()) return false;
    const auto root = doc.root();
    switch (kind) {
        case SignInKind::Codex: return root["auth_mode"].equals("chatgpt") || root["tokens"].isObject();
        case SignInKind::ClaudeCode: return root["oauthAccount"].isObject();
    }
    return false;
}

bool SignInMarker::refresh(std::int64_t nowMs, bool enabled) {
    if (!enabled) {
        stamp_.reset();
        readMs_.reset();
        signedIn_ = false;
        return false;
    }
    const auto stamp = files::stampOf(file_, true);
    if (!stamp) {
        stamp_.reset();
        signedIn_ = false;
        return false;
    }
    if (stamp_ == stamp || (readMs_ && nowMs - *readMs_ < kRereadAfterMs)) return signedIn_;
    readMs_ = nowMs;
    stamp_ = stamp;
    ++reads_;
    auto read = files::readFile(file_, kFileLimit, true);
    signedIn_ = read.status == files::ReadStatus::Ok && signedInMarker(kind_, read.bytes);
    // The file may hold tokens: nothing of it outlives the check.
    if (!read.bytes.empty()) ::explicit_bzero(read.bytes.data(), read.bytes.size());
    return signedIn_;
}

bool desktopSignedIn(const std::optional<Snapshot>& snapshot, std::int64_t nowMs) {
    return snapshot && nowMs - snapshot->observedMs <= kDesktopSignedInMs;
}

}  // namespace agentspeed::quota
