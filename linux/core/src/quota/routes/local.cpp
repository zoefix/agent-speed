#include "agentspeed/quota/routes/local.hpp"

#include <algorithm>

#include "agentspeed/sqlite.hpp"

namespace agentspeed::quota {

namespace {

constexpr std::size_t kDevinRows = 64;

}  // namespace

std::string GrokLogReader::defaultFile(const VendorEnvironment& env) {
    return fsx::join(env.folder("GROK_HOME").value_or(env.inHome(".grok")), "logs/unified.jsonl");
}

std::vector<std::string_view> tailLines(std::string_view tail, bool fromStart) {
    std::vector<std::string_view> out;
    std::size_t start = 0;
    bool first = true;
    while (start <= tail.size()) {
        auto end = tail.find('\n', start);
        if (end == std::string_view::npos) end = tail.size();
        const auto line = tail.substr(start, end - start);
        if (!(first && !fromStart) && !line.empty()) out.push_back(line);
        first = false;
        start = end + 1;
    }
    return out;
}

bool GrokLogReader::refresh(const VendorEnvironment& env) {
    auto& files = env.fs();
    const auto file = defaultFile(env);
    const auto info = files.stat(file);
    if (!info || !info->regular) {
        stamp_.reset();
        return false;
    }
    const std::pair<std::int64_t, std::int64_t> stamp{info->length, info->writeNs};
    if (stamp_ == stamp && file_ == file) return false;
    stamp_ = stamp;
    file_ = file;
    const std::int64_t start = std::max<std::int64_t>(0, info->length - static_cast<std::int64_t>(kTailBytes));
    const auto tail = files.readRange(file, start, kTailBytes);
    if (!tail) return false;
    const auto found = vendors::grokFromLog(tailLines(*tail, start == 0));
    if (!found) return false;
    Snapshot next{SourceKind::GrokBuild, found->observedMs, found->windows};
    if (snapshot_ == next) return false;
    snapshot_ = std::move(next);
    return true;
}

std::vector<std::string> DevinPlanReader::defaultFolders(const VendorEnvironment& env) {
    const auto config = env.configHome();
    return {fsx::join(config, "Devin"), fsx::join(config, "Windsurf")};
}

std::string DevinPlanReader::database(const std::string& folder) { return fsx::join(folder, "User/globalStorage/state.vscdb"); }

std::vector<vendors::DevinRow> DevinPlanReader::rows(const std::string& path) {
    try {
        auto db = sqlite::Connection::open(path, sqlite::OpenMode::Immutable);
        for (const auto* pattern : {"windsurf.reactSettings.cachedPlanInfoData%", "windsurf.settings.cachedPlanInfo%"}) {
            auto statement = db.prepare("SELECT key, value FROM ItemTable WHERE key LIKE ?1");
            statement.bind(1, std::string_view(pattern));
            std::vector<vendors::DevinRow> out;
            while (out.size() < kDevinRows && statement.step()) {
                out.push_back({std::string(statement.getText(0)), std::string(statement.getBlob(1))});
            }
            if (!out.empty()) return out;
        }
    } catch (const sqlite::Error&) {
    }
    return {};
}

bool DevinPlanReader::refresh(const VendorEnvironment& env) {
    auto& files = env.fs();
    const auto folders = defaultFolders(env);
    std::optional<std::size_t> best;
    fsx::Stat bestStat;
    for (std::size_t i = 0; i < folders.size(); ++i) {
        const auto info = files.stat(database(folders[i]));
        if (!info || !info->regular) continue;
        if (!best || info->writeNs > bestStat.writeNs) {
            best = i;
            bestStat = *info;
        }
    }
    if (!best) {
        stamp_.clear();
        return false;
    }
    const auto& folder = folders[*best];
    const auto logs = fsx::join(folder, "logs");
    const auto logsInfo = files.stat(logs);
    std::vector<std::int64_t> next = {bestStat.length, bestStat.writeNs, logsInfo ? logsInfo->writeNs : 0,
                                      static_cast<std::int64_t>(*best)};
    if (next == stamp_) return false;
    stamp_ = std::move(next);
    std::optional<std::int64_t> observed;
    const auto zone = env.zone();
    for (const auto& name : files.list(logs)) {
        const auto launch = vendors::devinLaunchMs(name, *zone);
        if (launch && (!observed || *launch > *observed)) observed = launch;
    }
    if (!observed) return false;
    Snapshot snapshot{SourceKind::Devin, *observed, vendors::devinWindows(rows(database(folder)))};
    if (snapshot_ == snapshot) return false;
    snapshot_ = std::move(snapshot);
    return true;
}

}  // namespace agentspeed::quota
