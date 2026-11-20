#include "agentspeed/openbitfun_ledger.hpp"

#include <limits>
#include <set>
#include <tuple>

#include "agentspeed/harness.hpp"
#include "agentspeed/history.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/tailer.hpp"

namespace agentspeed::openbitfun {

UsageLedger& UsageLedger::shared() {
    static UsageLedger ledger([] {
        const auto env = processEnvironment();
        const auto xdg = env("XDG_CONFIG_HOME");
        const auto config = xdg && xdg->starts_with('/') ? *xdg : fsx::join(userHome(env), ".config");
        return fsx::join(config, "openbitfun/data/token_usage/records");
    }());
    return ledger;
}

std::optional<UsageLedger::Day> UsageLedger::Day::read(const fsx::Entry& file) {
    const auto document = harness::readMetadata(file.path, 64LL << 20);
    if (!document || !document->root()["records"].isArray()) return std::nullopt;
    Day day{file.stat, {}};
    for (const auto& row : document->root()["records"].elements()) {
        const auto details = row["token_details"];
        const auto request = details["openbitfunModelRequest"];
        const auto sessionId = row["session_id"].nonEmptyString();
        const auto turn = row["turn_id"].nonEmptyString();
        const auto round = request["roundId"].nonEmptyString();
        const auto id = request["attemptId"].nonEmptyString();
        if (harness::milliseconds(request["version"]) != 1 || !sessionId || !turn || !round || !id) continue;
        Attempt attempt;
        attempt.model = row["effective_model_name"].nonEmptyString();
        if (!attempt.model) attempt.model = request["responseModel"].nonEmptyString();
        if (!attempt.model) attempt.model = request["requestedModel"].nonEmptyString();
        attempt.provider = request["channel"]["protocol"].nonEmptyString();
        if (request["usageKnown"].isTrue()) {
            const auto output = row["output_tokens"].count();
            attempt.tokens = TokenCounts{row["input_tokens"].count(), row["cached_tokens"].count(),
                row["cache_write_tokens"].count(), output, std::min(output, details["reasoningTokenCount"].count())};
        }
        day.sessions[*sessionId].push_back({{*turn, *round}, *id, std::move(attempt), harness::timestamp(row["timestamp"]).value_or(0)});
    }
    return day;
}

std::shared_ptr<const UsageSnapshot> UsageLedger::forSession(const std::string& session) {
    std::lock_guard lock(mutex_);
    const auto now = steadyMs();
    if (now >= nextScan_) {
        nextScan_ = now + 1000;
        std::set<std::string> seen;
        std::set<std::string> affected;
        const auto files = fsx::isPlainDirectory(root_) ? fsx::list(root_) : std::vector<fsx::Entry>{};
        for (const auto& file : files) {
            if (!file.stat.regular || file.name.size() != 15 || !file.name.ends_with(".json") ||
                !parseTimestamp(file.name.substr(0, 10) + "T00:00:00Z")) continue;
            seen.insert(file.name);
            const auto previous = days_.find(file.name);
            if (previous != days_.end()) {
                const auto& stat = previous->second.stat;
                if (stat.length == file.stat.length && stat.writeNs == file.stat.writeNs &&
                    stat.device == file.stat.device && stat.inode == file.stat.inode) continue;
            }
            auto day = Day::read(file);
            if (!day) continue;
            for (const auto& [owner, rows] : day->sessions) {
                if (previous == days_.end() || !previous->second.sessions.contains(owner) ||
                    previous->second.sessions.at(owner) != rows) affected.insert(owner);
            }
            if (previous != days_.end()) {
                for (const auto& entry : previous->second.sessions)
                    if (!day->sessions.contains(entry.first)) affected.insert(entry.first);
            }
            days_[file.name] = std::move(*day);
        }
        std::erase_if(days_, [&](const auto& day) {
            if (seen.contains(day.first)) return false;
            for (const auto& entry : day.second.sessions) affected.insert(entry.first);
            return true;
        });
        for (const auto& owner : affected) {
            auto snapshot = std::make_shared<UsageSnapshot>();
            std::map<std::pair<RoundId, std::string>, std::size_t> positions;
            std::uint64_t hash = 0;
            std::int64_t newest = 0;
            for (const auto& entry : days_) {
                const auto rows = entry.second.sessions.find(owner);
                if (rows == entry.second.sessions.end()) continue;
                for (const auto& row : rows->second) {
                    const auto& attempt = row.attempt;
                    for (const auto& value : {row.round.first, row.round.second, row.id,
                                              attempt.model.value_or(""), attempt.provider.value_or("")})
                        hash = (hash ^ history::fnv1a(value)) * 1099511628211ULL;
                    if (const auto& t = attempt.tokens) {
                        for (const auto count : {t->input, t->cachedInput, t->cacheWriteInput, t->output, t->reasoning})
                            hash = (hash ^ static_cast<std::uint64_t>(count)) * 1099511628211ULL;
                    }
                    newest = std::max(newest, row.recordedMs);
                    auto& attempts = snapshot->rounds[row.round];
                    const auto [pos, inserted] = positions.try_emplace({row.round, row.id}, attempts.size());
                    if (inserted) attempts.push_back(attempt);
                    else attempts[pos->second] = attempt;
                }
            }
            snapshot->signature = static_cast<std::int64_t>(hash);
            snapshot->writeNs = std::min(newest, std::numeric_limits<std::int64_t>::max() / 1000000) * 1000000;
            if (snapshot->rounds.empty()) sessions_.erase(owner);
            else sessions_[owner] = std::move(snapshot);
        }
    }
    const auto found = sessions_.find(session);
    return found == sessions_.end() ? nullptr : found->second;
}

}  // namespace agentspeed::openbitfun
