#include "agentspeed/qoder_history.hpp"

#include <set>

#include "agentspeed/fs_util.hpp"

namespace agentspeed::history {

namespace {

std::string fileIdentity(const std::string& path) {
    const auto main = fsx::statOf(path), wal = fsx::statOf(path + "-wal");
    const auto part = [](const std::optional<fsx::Stat>& s) {
        return s ? std::to_string(s->length) + ":" + std::to_string(s->writeNs) : std::string("-");
    };
    return (main ? std::to_string(main->device) + ":" + std::to_string(main->inode) : std::string("-")) + "|" + part(main) + "|" + part(wal);
}

}  // namespace

const std::map<std::string, qoder::RowStats>& QoderPlanner::statsOf(const qoder::Database& database) {
    // Read again only when the database or its `-wal` changed.
    auto identity = fileIdentity(database.path());
    auto& cached = cache_[database.path()];
    if (cached.identity != identity || cached.identity.empty()) {
        cached.stats = database.rowStats();
        cached.identity = std::move(identity);
    }
    return cached.stats;
}

std::vector<QoderSession> QoderPlanner::plan(const std::vector<qoder::Root>& roots,
                                             const std::function<bool(const std::string&, std::int64_t, std::int64_t)>& unchanged) {
    discovered_ = 0;
    unreadable_ = 0;
    std::vector<QoderSession> planned;
    std::set<std::string> claimed;
    for (const auto& path : qoder::databases(roots)) {
        ++discovered_;
        try {
            const auto database = qoder::Database::open(path);
            database.snapshot([&] {
                std::map<std::string, QoderSession> changed;
                const auto& stats = statsOf(database);
                for (const auto& session : database.sessions()) {
                    if (!claimed.insert(session.id).second) continue;
                    const auto stat = stats.find(session.id);
                    if (stat == stats.end()) continue;
                    QoderSession entry{database.agentId(session.id), path, {}, stat->second.count, stat->second.maxRowId};
                    if (unchanged(entry.key, entry.size, entry.modified)) continue;
                    changed.emplace(session.id, std::move(entry));
                }
                if (changed.empty()) return;
                std::set<std::string> ids;
                for (const auto& [id, entry] : changed) ids.insert(id);
                std::map<std::string, std::vector<qoder::Row>> rows;
                for (auto& row : database.rowsOfSessions(ids)) rows[*row.sessionId].push_back(std::move(row));
                for (auto& [id, entry] : changed) {
                    // The last version of each call, in first-seen order (a stored copy updates it, METRICS 4.4).
                    std::vector<std::string> order;
                    std::map<std::string, ParsedResponse> latest;
                    for (auto& r : qoder::responsesOf(rows[id], id, entry.key)) {
                        if (!latest.contains(r.key)) order.push_back(r.key);
                        latest[r.key] = std::move(r);
                    }
                    for (const auto& key : order) entry.responses.push_back(std::move(latest[key]));
                    planned.push_back(std::move(entry));
                }
            });
        } catch (const qoder::UnsupportedSchema&) {
        } catch (const sqlite::Error&) {
            ++unreadable_;
        }
    }
    return planned;
}

}  // namespace agentspeed::history
