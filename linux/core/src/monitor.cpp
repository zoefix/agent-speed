#include "agentspeed/monitor.hpp"

#include <algorithm>
#include <atomic>
#include <utility>

#include "agentspeed/antigravity.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/gemini_parser.hpp"
#include "agentspeed/grok_parser.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/monitor_store.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/opencode.hpp"
#include "agentspeed/parents.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/tz.hpp"
#include "agentspeed/zcode.hpp"

namespace agentspeed {

namespace {

constexpr std::int64_t kBacklogWindowMs = 15 * 60'000;
constexpr std::int64_t kLargeFileBytes = 256LL << 20;
constexpr std::int64_t kLargeFileTailBytes = 64LL << 20;
constexpr std::int64_t kRecentWriteWindowNs = 2LL * 60'000 * 1'000'000;
constexpr std::int64_t kRediscoveryMs = 10'000;
constexpr std::int64_t kDatabaseReadIntervalMs = 500;

std::int64_t msOf(std::int64_t ns) { return ns / 1'000'000; }

std::int64_t nextEpoch() {
    static std::atomic<std::int64_t> next{0};
    return ++next;
}

}  // namespace

std::set<SourceKind> MonitorConfig::allSources() {
    std::set<SourceKind> all;
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) all.insert(static_cast<SourceKind>(k));
    return all;
}

MonitorConfig MonitorConfig::fromEnvironment(const RootSettings& settings, std::set<SourceKind> enabled) {
    MonitorConfig config;
    const auto environment = processEnvironment();
    config.locations = SourceLocations::resolve(settings, environment, userHome(environment));
    config.enabled = std::move(enabled);
    return config;
}

Monitor::Monitor(MonitorConfig config)
    : config_(std::move(config)), environment_(processEnvironment()), home_(userHome(environment_)),
      store_(std::make_unique<Store>()), epoch_(nextEpoch()) {
    store_->setEnabled(&config_.enabled);
    store_->setPerfLedger(&perfLedger_);
    if (!config_.prices) config_.prices = std::make_shared<pricing::PriceBook>();
    zone_ = localZone(environment_);
    store_->setPricing(config_.prices, zone_);
    for (const auto& [family, path] : config_.locations.fileRoots()) addRoot(family, path);
    initSources();
    refreshAvailability();
}

Monitor::~Monitor() = default;

bool Monitor::familyEnabled(RootFamily family) const {
    const auto& e = config_.enabled;
    switch (family) {
        case RootFamily::Claude: return e.contains(SourceKind::ClaudeCode) || e.contains(SourceKind::ClaudeDesktop);
        case RootFamily::Codex: return e.contains(SourceKind::Codex);
        case RootFamily::DeepSeek: return e.contains(SourceKind::Deepseek);
        case RootFamily::Gemini: return e.contains(SourceKind::Gemini);
        case RootFamily::Omp: return e.contains(SourceKind::Omp);
        case RootFamily::Antigravity: return e.contains(SourceKind::Antigravity);
        case RootFamily::WorkBuddy: return e.contains(SourceKind::WorkBuddy);
        case RootFamily::WorkBuddyAi: return e.contains(SourceKind::WorkBuddyAi);
        case RootFamily::OpenCode: return e.contains(SourceKind::OpenCode);
        case RootFamily::ZCode: return e.contains(SourceKind::ZCode);
        case RootFamily::Kimi: return e.contains(SourceKind::Kimi);
        case RootFamily::GrokBuild: return e.contains(SourceKind::GrokBuild);
        case RootFamily::Pi: return e.contains(SourceKind::Pi);
        case RootFamily::OpenClaw: return e.contains(SourceKind::OpenClaw);
        case RootFamily::Hermes: return e.contains(SourceKind::Hermes);
        case RootFamily::Droid: return e.contains(SourceKind::Droid);
    }
    return false;
}

void Monitor::addRoot(RootFamily family, const std::string& path) {
    Root root;
    root.family = family;
    root.path = path;
    // WorkBuddy is detected by its data folder, even before `projects` exists (METRICS 2).
    root.detectionPath = (family == RootFamily::WorkBuddy || family == RootFamily::WorkBuddyAi) && !path.empty()
                             ? paths::workBuddyDataFolderOf(path)
                             : path;
    root.enabled = familyEnabled(family);
    roots_.push_back(std::move(root));
}

void Monitor::refreshAvailability() {
    for (auto& root : roots_) root.available = !root.detectionPath.empty() && fsx::isDirectory(root.detectionPath);
}

void Monitor::start(std::int64_t nowMs, std::int64_t todayStartMs) {
    clockMs_ = nowMs;
    store_->setClock(nowMs);
    seedRoots(todayStartMs);
    seedSources(todayStartMs);
    lastRediscoveryMs_ = nowMs;
    store_->goLive();
}

Monitor::Tracked& Monitor::track(std::size_t root, const std::string& path) {
    auto file = std::make_unique<Tracked>();
    file->root = root;
    file->tailer = makeTailer(roots_[root].family, path);
    file->parser = makeParser(roots_[root].family, path, roots_[root].path, deduper_);
    if (work::hasActions(file->parser->source())) {
        file->actions = makeActions(path, file->parser->source());
        file->parser->setLineObserver(file->actions.get());
    }
    auto& ref = *file;
    files_[path] = std::move(file);
    return ref;
}

std::optional<Monitor::Plan> Monitor::planInitialRead(Tracked& file, const discovery::DiscoveredFile& found,
                                                      std::int64_t todayStartMs) {
    file.knownLength = found.length;
    file.knownWriteNs = found.writeNs;
    if (msOf(found.writeNs) < todayStartMs - kBacklogWindowMs) {
        if (auto* droidParser = dynamic_cast<droid::Parser*>(file.parser.get())) {
            // An empty read from the end whose sidecar read only takes the counters' high-water mark (METRICS 4.16).
            droidParser->startAtEnd();
            return Plan{&file, found.writeNs, found.length, Alignment::SkipPartialLine};
        }
        file.tailer->startAtEnd(found.length, found.writeNs);
        return std::nullopt;
    }
    if (found.length > kLargeFileBytes && dynamic_cast<FrameTailer*>(file.tailer.get())) {
        partialHistory_ = true;
        file.tailer->startAt(found.length);
        return std::nullopt;
    }
    if (found.length > static_cast<std::int64_t>(GeminiParser::kMaxDocumentBytes) && dynamic_cast<WholeFileTailer*>(file.tailer.get()) &&
        fsx::endsWithNoCase(found.path, ".json")) {
        partialHistory_ = true;
        file.tailer->startAtEnd(found.length, found.writeNs);
        return std::nullopt;
    }
    if (found.length > kLargeFileBytes && dynamic_cast<LineTailer*>(file.tailer.get())) {
        partialHistory_ = true;
        return Plan{&file, found.writeNs, found.length - kLargeFileTailBytes, Alignment::SkipToNextLine};
    }
    return Plan{&file, found.writeNs, 0, Alignment::None};
}

void Monitor::seedRoots(std::int64_t todayStartMs) {
    std::vector<Plan> plans;
    for (std::size_t r = 0; r < roots_.size(); ++r) {
        auto& root = roots_[r];
        if (!root.enabled || root.seeded) continue;
        root.available = !root.detectionPath.empty() && fsx::isDirectory(root.detectionPath);
        if (!root.available) continue;
        for (const auto& found : discovery::enumerate(root.path, root.family)) {
            if (files_.contains(found.path)) continue;
            auto& file = track(r, found.path);
            if (auto plan = planInitialRead(file, found, todayStartMs)) plans.push_back(*plan);
        }
        root.seeded = true;
    }
    // The startup order decides which file owns a duplicated key: modification time, then path (METRICS 5).
    std::sort(plans.begin(), plans.end(), [](const Plan& a, const Plan& b) {
        return a.writeNs != b.writeNs ? a.writeNs < b.writeNs : a.file->tailer->path() < b.file->tailer->path();
    });
    for (const auto& plan : plans) {
        plan.file->tailer->startAt(plan.startOffset, plan.alignment);
        readLive(*plan.file, true);
    }
}

bool Monitor::readLive(Tracked& file, bool seeded) {
    if (auto* database = dynamic_cast<DatabaseTailer*>(file.tailer.get()); database && !seeded) {
        if (database->lastReadMs() != 0 && steadyMs() - database->lastReadMs() < kDatabaseReadIntervalMs) {
            file.dirty = true;
            return false;
        }
    }
    file.dirty = false;
    const auto version = file.parser->version();
    const auto path = file.tailer->path();
    store_->setSeeding(seeded);
    // Seeded actions count for the performance score, not for the work signals (METRICS 6.10 Storage).
    if (file.actions) file.actions->setSeeding(seeded);
    try {
        const auto result = file.tailer->read(*file.parser, *store_);
        store_->setSeeding(false);
        if (file.actions) file.actions->setSeeding(false);
        if (auto* whole = dynamic_cast<WholeFileTailer*>(file.tailer.get()); whole && whole->oversized()) partialHistory_ = true;
        if (auto* grok = dynamic_cast<GrokParser*>(file.parser.get()); grok && grok->pendingOverflow()) partialHistory_ = true;
        file.knownLength = result.length;
        file.knownWriteNs = result.writeNs;
        file.unreadable = false;
        pushAgent(file);
        return result.bytesRead > 0 || result.wasReset || file.parser->version() != version;
    } catch (const FileGone&) {
        store_->setSeeding(false);
        if (file.actions) file.actions->setSeeding(false);
        remove(path);
        return true;
    } catch (const std::exception&) {
        store_->setSeeding(false);
        if (file.actions) file.actions->setSeeding(false);
        file.unreadable = true;
        return false;
    }
}

bool Monitor::addRuntimeFile(std::size_t root, const discovery::DiscoveredFile& found, std::int64_t todayStartMs) {
    if (files_.contains(found.path)) return false;
    auto& file = track(root, found.path);
    const auto plan = planInitialRead(file, found, todayStartMs);
    if (!plan) return false;
    file.tailer->startAt(plan->startOffset, plan->alignment);
    return readLive(file);
}

void Monitor::remove(const std::string& path) {
    const auto it = files_.find(path);
    if (it == files_.end()) return;
    removedMalformed_ += it->second->parser->malformedLines();
    removeAgent(it->second->parser->agentId());
    files_.erase(it);
}

void Monitor::removeAgent(const std::string& id) {
    agents_.erase(id);
    if (links_.erase(id) > 0) linksChanged_ = true;
}

void Monitor::pushInfo(AgentInfo info, const AgentLink& link) {
    const auto known = links_.find(info.id);
    if (known == links_.end() || known->second.kind != link.kind || known->second.threadId != link.threadId ||
        known->second.parentThreadId != link.parentThreadId || known->second.createdMs != link.createdMs) {
        links_[info.id] = link;
        linksChanged_ = true;
    }
    agents_[info.id] = std::move(info);
}

void Monitor::pushAgent(Tracked& file) {
    const auto& parser = *file.parser;
    if (parser.version() == file.pushedVersion) return;
    file.pushedVersion = parser.version();
    if (!parser.lastLineMs()) return;
    if (!config_.enabled.contains(parser.source())) {
        removeAgent(parser.agentId());
        return;
    }
    AgentInfo info;
    info.id = parser.agentId();
    info.source = parser.source();
    info.project = parser.project();
    if (!info.project && roots_[file.root].family == RootFamily::Antigravity) info.project = antigravity::storeNameOf(info.id);
    info.model = parser.model();
    info.isSubagent = parser.isSubagent();
    info.phase = parser.phase();
    info.inFlightStartMs = parser.inFlightStartMs();
    info.lastLineMs = parser.lastLineMs();
    info.modes = parser.modes();
    pushInfo(std::move(info), parser.link());
}

void Monitor::resolveParents() {
    if (!linksChanged_) return;
    linksChanged_ = false;
    std::vector<AgentLink> links;
    links.reserve(links_.size());
    for (const auto& [id, link] : links_) links.push_back(link);
    parents_ = parent_links::resolve(links);
}

std::optional<std::string> Monitor::parentOf(const std::string& agentId) {
    resolveParents();
    const auto it = parents_.find(agentId);
    return it == parents_.end() ? std::nullopt : std::optional<std::string>(it->second);
}

std::optional<std::pair<std::int64_t, std::int64_t>> Monitor::stampOf(const Tracked& file) const {
    const auto& path = file.tailer->path();
    if (dynamic_cast<const DatabaseTailer*>(file.tailer.get())) {
        const auto stamp = sqlite::stampOf(path);
        if (!stamp) return std::nullopt;
        return std::make_pair(stamp->length(), stamp->lastWriteNs());
    }
    const auto s = fsx::statOf(path, true);
    if (!s || !s->regular) return std::nullopt;
    auto writeNs = s->writeNs;
    if (dynamic_cast<const GrokParser*>(file.parser.get())) writeNs = grokModifiedNs(path, writeNs);
    if (dynamic_cast<const droid::Parser*>(file.parser.get())) {
        if (const auto sidecar = fsx::statOf(droid::sidecarOf(path)); sidecar && sidecar->regular) writeNs = std::max(writeNs, sidecar->writeNs);
    }
    return std::make_pair(s->length, writeNs);
}

bool Monitor::poll(std::int64_t nowMs, std::int64_t todayStartMs) {
    store_->setClock(nowMs);
    clockMs_ = nowMs;
    bool changed = pollRecent(nowMs);
    changed = refreshSources(false, nowMs, todayStartMs) || changed;
    if (nowMs - lastRediscoveryMs_ >= kRediscoveryMs) {
        changed = rediscover(nowMs, todayStartMs) || changed;
        lastRediscoveryMs_ = nowMs;
    }
    store_->prune(nowMs, todayStartMs);
    if (traffic_->enabled()) {
        resolveParents();
        traffic_->tick(nowMs, trafficAgents(nowMs));
    }
    return changed;
}

// METRICS 10.6: the agents with what per-agent attribution needs — their tree, transcript, working directory and when
// they were active (a busy phase now, or their last line).
std::vector<traffic::AgentFacts> Monitor::trafficAgents(std::int64_t nowMs) {
    std::vector<traffic::AgentFacts> out;
    for (const auto& [id, info] : agents_) {
        traffic::AgentFacts f;
        f.id = id;
        f.source = info.source;
        if (const auto parent = parents_.find(id); parent != parents_.end()) f.parent = parent->second;
        if (const auto file = files_.find(id); file != files_.end()) {
            f.transcript = id;
            f.cwdPath = file->second->parser->cwdPath();
        }
        const bool stale = info.lastLineMs && nowMs - *info.lastLineMs > 300'000;
        if (info.phase != Phase::Idle && !stale) f.active.emplace_back(nowMs, nowMs);
        if (info.lastLineMs) f.active.emplace_back(*info.lastLineMs, *info.lastLineMs);
        out.push_back(std::move(f));
    }
    return out;
}

bool Monitor::pollRecent(std::int64_t nowMs) {
    const auto cutoffNs = nowMs * 1'000'000 - kRecentWriteWindowNs;
    std::vector<std::string> due;
    for (const auto& [path, file] : files_) {
        const auto* grok = dynamic_cast<const GrokParser*>(file->parser.get());
        if (file->knownWriteNs >= cutoffNs || file->dirty || (grok && grok->pendingUsageCount() > 0)) due.push_back(path);
    }
    bool changed = false;
    for (const auto& path : due) {
        const auto it = files_.find(path);
        if (it == files_.end()) continue;
        auto& file = *it->second;
        const auto stamp = stampOf(file);
        if (!stamp) {
            remove(path);
            changed = true;
        } else if (file.dirty || stamp->first != file.knownLength || stamp->second != file.knownWriteNs) {
            changed |= readLive(file);
        }
    }
    return changed;
}

bool Monitor::rediscover(std::int64_t nowMs, std::int64_t todayStartMs) {
    bool changed = refreshSources(true, nowMs, todayStartMs);
    addNewPiRoots();
    if (restartRequested_) return changed;
    addNewOmpRoots();
    addNewCodexHomeRoots();
    for (std::size_t r = 0; r < roots_.size(); ++r) {
        if (!roots_[r].enabled) continue;
        const bool available = !roots_[r].detectionPath.empty() && fsx::isDirectory(roots_[r].detectionPath);
        changed |= available != roots_[r].available;
        roots_[r].available = available;
        if (!available) continue;
        if (!roots_[r].seeded) {
            seedRoots(todayStartMs);
            changed = true;
            continue;
        }
        std::set<std::string> seen;
        const auto root = roots_[r];
        for (const auto& found : discovery::enumerate(root.path, root.family)) {
            seen.insert(found.path);
            const auto it = files_.find(found.path);
            if (it == files_.end()) {
                changed |= addRuntimeFile(r, found, todayStartMs);
            } else if (found.length != it->second->knownLength || found.writeNs != it->second->knownWriteNs) {
                changed |= readLive(*it->second);
            }
        }
        std::vector<std::string> gone;
        for (const auto& [path, file] : files_) {
            if (file->root == r && !seen.contains(path)) gone.push_back(path);
        }
        for (const auto& path : gone) {
            remove(path);
            changed = true;
        }
    }
    return changed;
}

void Monitor::addNewOmpRoots() {
    if (!config_.locations.ompAutomatic || !familyEnabled(RootFamily::Omp)) return;
    std::set<std::string> known;
    std::vector<std::string> pi;
    for (const auto& r : roots_) {
        if (r.family == RootFamily::Omp) known.insert(r.path);
        if (r.family == RootFamily::Pi) pi.push_back(r.path);
    }
    std::vector<std::string> automatic;
    for (const auto& p : paths::ompRoots(environment_, home_)) automatic.push_back(paths::normalizeRoot(p));
    for (const auto& path : paths::withoutPi(automatic, pi)) {
        if (known.insert(path).second) addRoot(RootFamily::Omp, path);
    }
}

void Monitor::addNewPiRoots() {
    if (!config_.locations.piAutomatic) return;
    std::set<std::string> known;
    std::vector<std::string> omp;
    for (const auto& r : roots_) {
        if (r.family == RootFamily::Pi) known.insert(r.path);
        if (r.family == RootFamily::Omp) omp.push_back(r.path);
    }
    std::vector<std::string> added;
    for (const auto& p : paths::piRoots(environment_, home_)) {
        if (known.insert(p).second) added.push_back(p);
    }
    if (added.empty()) return;
    if (paths::withoutPi(omp, added).size() < omp.size()) {
        restartRequested_ = true;
        return;
    }
    for (const auto& path : added) addRoot(RootFamily::Pi, path);
}

void Monitor::addNewCodexHomeRoots() {
    if (!config_.locations.codexAutomatic) return;
    std::set<std::string> known;
    for (const auto& r : roots_) {
        if (r.family == RootFamily::Codex) known.insert(fsx::trimTrailing(r.path));
    }
    const auto roots = config_.locations.openClawAutomatic ? paths::openClawStateFolders(environment_, home_) : config_.locations.openClawRoots;
    for (const auto& root : paths::openClawCodexRoots(paths::openClawDatabases(roots))) {
        const auto normalized = paths::normalizeRoot(root);
        if (known.insert(fsx::trimTrailing(normalized)).second) addRoot(RootFamily::Codex, normalized);
    }
}

int Monitor::sessionCount(SourceKind kind, std::int64_t todayStartMs) const {
    if (kind == SourceKind::Hermes) {
        int count = 0;
        if (hermes_) {
            for (const auto* set : hermes_->sets()) {
                for (const auto* agent : set->agents()) count += agent->lastMainTurnMs && *agent->lastMainTurnMs >= todayStartMs ? 1 : 0;
            }
        }
        return count;
    }
    if (kind == SourceKind::OpenCode || kind == SourceKind::ZCode || kind == SourceKind::OpenClaw) {
        int count = 0;
        for (const auto& [id, agent] : agents_) {
            if (agent.source != kind || !agent.lastLineMs || *agent.lastLineMs < todayStartMs) continue;
            // OpenCode counts every session; ZCode and OpenClaw their main sessions (METRICS 2).
            if (kind == SourceKind::OpenCode || !agent.isSubagent) ++count;
        }
        return count;
    }
    const bool mainOnly = kind == SourceKind::WorkBuddy || kind == SourceKind::WorkBuddyAi || kind == SourceKind::Kimi ||
                          kind == SourceKind::GrokBuild || kind == SourceKind::Pi || kind == SourceKind::Droid;
    int count = 0;
    for (const auto& [path, file] : files_) {
        const auto& p = *file->parser;
        if (p.source() == kind && p.lastLineMs() && *p.lastLineMs() >= todayStartMs && !(mainOnly && p.isSubagent())) ++count;
    }
    return count;
}

std::vector<Completion> Monitor::takeCompletions() { return store_->takeCompletions(); }

std::vector<ParsedResponse> Monitor::responses() const { return store_->responses(); }

void Monitor::repriceToday() { store_->reprice(); }

}  // namespace agentspeed
