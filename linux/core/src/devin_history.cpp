#include "agentspeed/devin_history.hpp"

#include <set>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/history.hpp"

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

std::int64_t DevinPlanner::modified(std::int64_t maxRowId, const std::optional<std::string>& model) {
    return static_cast<std::int64_t>(fnv1a(std::to_string(maxRowId) + "|" + model.value_or("")));
}

const std::map<std::string, devin::RowStats>& DevinPlanner::statsOf(const devin::Database& database) {
    // Read again only when the database or its `-wal` changed.
    auto identity = fileIdentity(database.path());
    auto& cached = cache_[database.path()];
    if (cached.identity != identity || cached.identity.empty()) {
        cached.stats = database.rowStats();
        cached.identity = std::move(identity);
    }
    return cached.stats;
}

std::vector<DevinSession> DevinPlanner::plan(const std::vector<devin::Root>& roots,
                                             const std::function<bool(const std::string&, std::int64_t, std::int64_t)>& unchanged) {
    discovered_ = 0;
    unreadable_ = 0;
    std::vector<DevinSession> planned;
    std::set<std::string> claimed;
    for (const auto& path : devin::databases(roots)) {
        ++discovered_;
        try {
            const auto database = devin::Database::open(path);
            database.snapshot([&] {
                struct Changed {
                    devin::Session session;
                    DevinSession entry;
                    std::vector<std::string> order;
                    std::map<std::string, ParsedResponse> latest;
                };
                std::map<std::string, Changed> changed;
                const auto sessions = database.sessions();
                const auto& stats = statsOf(database);
                for (const auto& session : sessions) {
                    if (!claimed.insert(session.id).second) continue;
                    const auto stat = stats.find(session.id);
                    if (stat == stats.end()) continue;
                    DevinSession entry{database.agentId(session.id), path, {}, stat->second.count, modified(stat->second.maxRowId, session.model)};
                    if (unchanged(entry.key, entry.size, entry.modified)) continue;
                    changed[session.id] = {session, std::move(entry), {}, {}};
                }
                if (changed.empty()) return;
                std::set<std::string> ids;
                for (const auto& [id, item] : changed) ids.insert(id);
                for (const auto& row : database.rowsOfSessions(ids)) {
                    if (!row.sessionId || !row.timeMs || !row.message) continue;
                    const auto item = changed.find(*row.sessionId);
                    if (item == changed.end()) continue;
                    auto& c = item->second;
                    auto r = devin::response(*row.message, c.session.id, row.rowId, *row.timeMs, c.session.model, c.entry.key);
                    if (!r) continue;
                    if (!c.latest.contains(r->key)) c.order.push_back(r->key);
                    c.latest[r->key] = std::move(*r);
                }
                for (auto& [id, c] : changed) {
                    for (const auto& key : c.order) c.entry.responses.push_back(std::move(c.latest[key]));
                    planned.push_back(std::move(c.entry));
                }
            });
        } catch (const devin::UnsupportedSchema&) {
        } catch (const sqlite::Error&) {
            ++unreadable_;
        }
    }
    return planned;
}

}  // namespace agentspeed::history
