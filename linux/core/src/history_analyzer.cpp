#include "agentspeed/history_analyzer.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <fstream>

#include "agentspeed/discovery.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/harness.hpp"
#include "agentspeed/hermes.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/opencode.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/tailer.hpp"
#include "agentspeed/actions.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/tz.hpp"
#include "agentspeed/zcode.hpp"

namespace agentspeed::history {

namespace {

constexpr const char* kOpenCodeFamily = "opencode";
constexpr const char* kZCodeFamily = "zcode";
constexpr const char* kOpenClawFamily = "openclaw";
constexpr const char* kOpenClawDatabaseFamily = "openclawDatabase";
constexpr const char* kHermesFamily = "hermes";
constexpr const char* kHermesLogFamily = "hermesLog";

const char* familyName(RootFamily family) {
    switch (family) {
        case RootFamily::Codex: return "Codex";
        case RootFamily::Claude: return "Claude";
        case RootFamily::DeepSeek: return "DeepSeek";
        case RootFamily::Gemini: return "Gemini";
        case RootFamily::Omp: return "Omp";
        case RootFamily::Antigravity: return "Antigravity";
        case RootFamily::WorkBuddy: return "WorkBuddy";
        case RootFamily::WorkBuddyAi: return "WorkBuddyAi";
        case RootFamily::OpenCode: return "OpenCode";
        case RootFamily::ZCode: return "ZCode";
        case RootFamily::Kimi: return "Kimi";
        case RootFamily::GrokBuild: return "GrokBuild";
        case RootFamily::Pi: return "Pi";
        case RootFamily::OpenClaw: return "OpenClaw";
        case RootFamily::Hermes: return "Hermes";
        case RootFamily::Droid: return "Droid";
    }
    return "?";
}

bool familyEnabled(RootFamily family, const std::set<SourceKind>& enabled) {
    if (family == RootFamily::Claude) return enabled.contains(SourceKind::ClaudeCode) || enabled.contains(SourceKind::ClaudeDesktop);
    for (const auto kind : enabled) {
        if (familyOf(kind) == family && kind != SourceKind::ClaudeCode && kind != SourceKind::ClaudeDesktop) return true;
    }
    return false;
}

// The latest version of each response key of one file; Claude's updates merge into the first emission.
engine::Response engineOf(const ParsedResponse& r) {
    engine::Response e;
    e.key = r.key;
    e.agentId = r.agentId;
    e.source = r.source;
    e.model = r.model;
    e.startMs = r.startMs;
    e.endMs = r.endMs;
    e.firstMs = r.firstMs;
    e.input = r.tokens.input;
    e.cachedInput = r.tokens.cachedInput;
    e.output = r.tokens.output;
    e.reasoning = r.tokens.reasoning;
    e.final = r.final;
    return e;
}

// The performance records of responses in the order they were emitted (sources without a response update order).
std::vector<perf::DayRecord> emittedRecords(const std::vector<ParsedResponse>& responses, SourceKind source, const DayLabeler& labeler) {
    perf::Recorder recorder([&](std::int64_t ms) { return labeler.label(ms); });
    for (const auto& r : responses) recorder.response(r.agentId, source, engineOf(r));
    return recorder.rows();
}

class Latest : public ResponseSink {
public:
    explicit Latest(bool merge) : merge_(merge) {}
    void onResponse(const ParsedResponse& response, bool completion) override {
        auto it = byKey_.find(response.key);
        if (it == byKey_.end()) {
            order_.push_back(response.key);
            noteMeasured(byKey_.emplace(response.key, response).first->second);
            return;
        }
        if (merge_ && !completion) {
            auto& previous = it->second;
            ParsedResponse merged = previous;
            merged.endMs = std::max(previous.endMs, response.endMs);
            if (response.firstMs) merged.firstMs = std::min(previous.firstMs.value_or(*response.firstMs), *response.firstMs);
            merged.tokens = TokenCounts::max(previous.tokens, response.tokens);
            merged.model = response.model ? response.model : previous.model;
            merged.final = previous.final || response.final;
            merged.fast = previous.fast || response.fast;
            merged.modes = previous.modes.merge(response.modes);
            previous = merged;
            noteMeasured(previous);
            return;
        }
        it->second = response;
        noteMeasured(it->second);
    }
    // METRICS 6.11 Completion order: a tool call completes when its first result is read.
    void action(const work::Action& action) { done_.push_back({{}, action}); }
    // The agent's completions in order, each response with its final values.
    std::vector<perf::DayRecord> perfRecords(const std::string& agentId, SourceKind source, const DayLabeler& labeler,
                                             const std::string* onlyAgent = nullptr) const {
        perf::Recorder recorder([&](std::int64_t ms) { return labeler.label(ms); });
        for (const auto& d : done_) {
            if (d.action) {
                recorder.action(agentId, source, *d.action);
                continue;
            }
            const auto& r = byKey_.at(d.key);
            if (!onlyAgent || r.agentId == *onlyAgent) recorder.response(r.agentId, source, engineOf(r));
        }
        return recorder.rows();
    }
    std::vector<ParsedResponse> items() const {
        std::vector<ParsedResponse> out;
        out.reserve(order_.size());
        for (const auto& key : order_) out.push_back(byKey_.at(key));
        return out;
    }

private:
    struct Done {
        std::string key;
        std::optional<work::Action> action;
    };
    // A response completes at the upsert that first makes it measured.
    void noteMeasured(const ParsedResponse& r) {
        if (measured_.contains(r.key) || !r.final || !engine::interval(engineOf(r))) return;
        measured_.insert(r.key);
        done_.push_back({r.key, std::nullopt});
    }

    bool merge_;
    std::vector<std::string> order_;
    std::map<std::string, ParsedResponse> byKey_;
    std::set<std::string> measured_;
    std::vector<Done> done_;
};

std::int64_t droidIdentity(const discovery::DiscoveredFile& file) {
    const auto parent = droid::parentOfTranscript(file.path);
    const auto text = std::to_string(file.length) + ":" + std::to_string(file.writeNs) + "|" + droid::stampText(droid::sidecarOf(file.path)) +
                      "|" + (parent ? droid::stampText(droid::siblingSidecar(file.path, *parent)) : std::string("-"));
    return static_cast<std::int64_t>(fnv1a(text));
}

}  // namespace

struct Analyzer::Work {
    std::vector<Planned> files;
    struct OpenCodeWork {
        std::string path, identity;
    };
    std::optional<OpenCodeWork> openCode;
    struct ZCodeWork {
        std::string path;
        std::int64_t size = 0, modified = 0;
    };
    std::optional<ZCodeWork> zcode;
    struct OpenClawWork {
        std::string path;
        sqlite::DatabaseStamp stamp;
    };
    std::vector<OpenClawWork> openClaw;
    struct HermesWork {
        std::string folder, path, marker;
        std::int64_t size = 0, writeNs = 0;
    };
    std::vector<HermesWork> hermes;
    std::set<std::int64_t> replaced;
};

Analyzer::Analyzer(Store& store, std::shared_ptr<pricing::PriceBook> prices, std::shared_ptr<const TimeZone> hermesZone)
    : store_(store), prices_(std::move(prices)), hermesZone_(std::move(hermesZone)) {
    entries_ = store_.loadFiles();
    peaks_ = store_.peaks();
    for (const auto& [version, data] : store_.priceTables()) {
        if (prices_->table(version)) continue;
        if (auto table = pricing::PriceTable::load(data); table && table->version() == version) prices_->add(table);
    }
    prices_->restore(store_.priceBindings());
    saveBindings();
}

void Analyzer::saveBindings() {
    const auto bindings = prices_->takeNewBindings();
    if (bindings.empty()) return;
    store_.savePriceBindings(bindings, [&](int version) {
        const auto table = prices_->table(version);
        return table ? table->data() : std::string();
    });
}

const StoredFile* Analyzer::previousOf(const std::string& path) const {
    const auto it = entries_.find(path);
    return it == entries_.end() ? nullptr : &it->second;
}

bool Analyzer::unchanged(const StoredFile* previous, const std::string& family, std::int64_t size, std::int64_t modified, bool force) const {
    return previous && !force && previous->version == kVersion && previous->family == family && previous->size == size &&
           previous->modified == modified && !(repricing_ && previous->hasUnpriced());
}

std::vector<perf::DayRecord> Analyzer::perfRecords(const std::set<SourceKind>* sources) const {
    std::vector<perf::DayRecord> out;
    for (const auto& [path, file] : entries_) {
        for (const auto& r : file.perf) {
            if (!sources || sources->contains(r.source)) out.push_back(r);
        }
    }
    return out;
}

StoredFile& Analyzer::keep(StoredFile file, std::vector<std::uint64_t> owned) {
    if (const auto* previous = previousOf(file.path)) file.id = previous->id;
    auto& stored = entries_[file.path] = std::move(file);
    pending_.emplace_back(&stored, std::move(owned));
    return stored;
}

void Analyzer::flush(std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>>& pending) {
    if (pending.empty()) return;
    store_.save(pending);
    saveBindings();
    pending.clear();
}

Report Analyzer::run(const SourceLocations& locations, const std::set<SourceKind>& enabled, std::shared_ptr<const TimeZone> zone,
                     const std::string& zoneId, const std::function<bool()>& cancelled) {
    Report report;
    const bool zoneChanged = !entries_.empty() && store_.meta("timeZone") != zoneId;
    const auto priceVersion = std::to_string(prices_->currentVersion());
    repricing_ = !entries_.empty() && store_.meta("priceTableVersion") != priceVersion;
    Work work;
    planFiles(locations, enabled, zoneChanged, work, report);
    planOpenClaw(locations, enabled, zoneChanged, work, report);
    planHermes(locations, enabled, zoneChanged, work, report);
    planZCode(locations, enabled, zoneChanged, work, report);
    planOpenCode(locations, enabled, zoneChanged, zoneId, work, report);
    report.filesPlanned = static_cast<int>(work.files.size() + work.openClaw.size() + work.hermes.size()) + (work.zcode ? 1 : 0) +
                          (work.openCode ? 1 : 0);

    KeyLedger ledger;
    store_.loadKeys(ledger, work.replaced);
    const DayLabeler labeler(zone);
    try {
        analyzeFiles(work, ledger, labeler, report, cancelled);
        if (!report.cancelled) analyzeOpenClaw(work, ledger, labeler, report);
        if (!report.cancelled) analyzeHermes(work, ledger, labeler, report);
        if (!report.cancelled) analyzeZCode(work, ledger, labeler, report);
        if (!report.cancelled) analyzeOpenCode(work, ledger, labeler, report);
    } catch (...) {
        flush(pending_);
        throw;
    }
    flush(pending_);
    saveBindings();
    if (!report.cancelled) {
        store_.setMeta("timeZone", zoneId);
        store_.setMeta("priceTableVersion", priceVersion);
        if (work.openCode) store_.setMeta("opencode", work.openCode->identity);
    }
    return report;
}

void Analyzer::planFiles(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report) {
    std::set<std::string> seen;
    for (const auto& [family, root] : locations.fileRoots()) {
        if (!familyEnabled(family, enabled) || root.empty()) continue;
        std::vector<std::string> roots{root};
        if (family == RootFamily::Codex) {
            if (const auto parent = fsx::parentOf(root); !parent.empty()) roots.push_back(fsx::join(parent, "archived_sessions"));
        }
        for (const auto& folder : roots) {
            if (!fsx::isDirectory(folder)) continue;
            for (const auto& file : discovery::enumerate(folder, family, discovery::kUnlimited)) {
                if (!seen.insert(file.path).second) continue;
                ++report.filesDiscovered;
                const auto modified = family == RootFamily::Droid ? droidIdentity(file) : file.writeNs;
                const auto* previous = previousOf(file.path);
                if (unchanged(previous, familyName(family), file.length, modified, force)) continue;
                if (previous) work.replaced.insert(previous->id);
                work.files.push_back({file.path, folder, family, file.length, file.writeNs, modified, previous});
            }
        }
    }
    std::sort(work.files.begin(), work.files.end(), [](const Planned& a, const Planned& b) {
        return a.writeNs != b.writeNs ? a.writeNs < b.writeNs : a.path < b.path;
    });
}

void Analyzer::analyzeFiles(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report, const std::function<bool()>& cancelled) {
    for (const auto& file : work.files) {
        if (cancelled && cancelled()) {
            report.cancelled = true;
            return;
        }
        auto parser = makeParser(file.family, file.path, file.root, ledger);
        auto tailer = makeTailer(file.family, file.path);
        Latest sink(file.family == RootFamily::Claude);
        // Every action of the whole file, without the cap (METRICS 6.10 Storage).
        work::ActionReader actions(file.path, 0);
        actions.setResultHandler([&](const work::Action& action) { sink.action(action); });
        if (work::hasActions(parser->source())) parser->setLineObserver(&actions);
        auto* droidParser = dynamic_cast<droid::Parser*>(parser.get());
        TailResult result;
        try {
            if (droidParser) {
                const auto remembered = droidUnreadableParents_.find(file.path);
                if (remembered != droidUnreadableParents_.end()) droidParser->unreadableParent = remembered->second;
            }
            tailer->startAt(0);
            result = tailer->read(*parser, sink);
            if (droidParser) {
                if (droidParser->unreadableParent) {
                    droidUnreadableParents_[file.path] = *droidParser->unreadableParent;
                } else {
                    droidUnreadableParents_.erase(file.path);
                }
                if (droidParser->countersUnknown()) {
                    // The parent's sidecar is unreadable: nothing is stored, the next analysis tries again.
                    ledger.finish(file.path);
                    if (file.previous) store_.loadKeys(ledger, file.previous->id);
                    continue;
                }
                droidParser->finishHistory(sink);
            }
        } catch (...) {
            // Unreadable or vanished: keep what was stored, with its key ownership.
            ledger.abandon();
            ++report.filesUnreadable;
            if (file.previous) store_.loadKeys(ledger, file.previous->id);
            continue;
        }
        StoredFile entry;
        entry.path = file.path;
        entry.family = familyName(file.family);
        entry.size = file.family == RootFamily::Droid ? file.size : result.length;
        entry.modified = file.family == RootFamily::Droid ? file.identity : result.writeNs;
        entry.version = kVersion;
        entry.days = contributionsOf(sink.items(), parser->source(), labeler, *prices_);
        entry.perf = sink.perfRecords(parser->agentId(), parser->source(), labeler);
        keep(std::move(entry), ledger.finish(parser->agentId()));
        ++report.filesAnalyzed;
        report.bytesAnalyzed += result.length;
        if (pending_.size() >= 256) flush(pending_);
    }
}

void Analyzer::planOpenCode(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, const std::string& zoneId,
                            Work& work, Report& report) {
    if (!enabled.contains(SourceKind::OpenCode) || locations.openCodeRoot.empty()) return;
    const auto path = opencode::Database::databasePath(locations.openCodeRoot);
    const auto database = fsx::statOf(path, true);
    if (!database || !database->regular) return;
    ++report.filesDiscovered;
    const auto wal = fsx::statOf(path + "-wal", true);
    const auto identity = path + "|" + std::to_string(database->length) + ":" + std::to_string(database->writeNs) + "|" +
                          (wal ? std::to_string(wal->length) + ":" + std::to_string(wal->writeNs) : std::string("-")) + "|v" +
                          std::to_string(kVersion) + "|" + zoneId;
    bool reprices = false;
    for (const auto& [p, e] : entries_) reprices |= repricing_ && e.family == kOpenCodeFamily && e.hasUnpriced();
    if (!force && !reprices && store_.meta("opencode") == identity) return;
    for (const auto& [p, e] : entries_) {
        if (e.family == kOpenCodeFamily) work.replaced.insert(e.id);
    }
    work.openCode = Work::OpenCodeWork{path, identity};
}

void Analyzer::analyzeOpenCode(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report) {
    if (!work.openCode) return;
    std::unique_ptr<opencode::Database> database;
    try {
        database = opencode::Database::open(work.openCode->path);
    } catch (...) {
        ++report.filesUnreadable;
        work.openCode.reset();
        return;
    }
    try {
        auto sessions = database->sessions();
        std::sort(sessions.begin(), sessions.end(), [](const auto& a, const auto& b) { return a.id < b.id; });
        for (const auto& info : sessions) {
            const auto agent = opencode::agentId(info.id);
            std::map<std::string, ParsedResponse> latest;
            std::vector<std::string> order;
            std::int64_t lastUpdated = 0, rows = 0;
            for (const auto& row : database->sessionRows(info.id)) {
                ++rows;
                lastUpdated = std::max(lastUpdated, row.timeUpdated);
                const auto response = opencode::response(row, info);
                if (!response) continue;
                std::string owner;
                if (ledger.tryClaim(row.id, agent, &owner) || owner == agent) {
                    if (!latest.contains(row.id)) order.push_back(row.id);
                    latest[row.id] = *response;
                }
            }
            std::vector<ParsedResponse> responses;
            for (const auto& id : order) responses.push_back(latest[id]);
            StoredFile entry;
            entry.path = agent;
            entry.family = kOpenCodeFamily;
            entry.size = rows;
            entry.modified = std::max(info.updatedMs, lastUpdated);
            entry.version = kVersion;
            entry.days = contributionsOf(responses, SourceKind::OpenCode, labeler, *prices_);
            entry.perf = emittedRecords(responses, SourceKind::OpenCode, labeler);
            keep(std::move(entry), ledger.finish(agent));
            ++report.sessionsAnalyzed;
        }
    } catch (const sqlite::Error&) {
        ++report.filesUnreadable;
        work.openCode.reset();
    }
}

void Analyzer::planZCode(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report) {
    if (!enabled.contains(SourceKind::ZCode) || locations.zcodeRoot.empty()) return;
    const auto path = harness::zcodeDatabase(locations.zcodeRoot);
    const auto stamp = sqlite::stampOf(path);
    if (!stamp) return;
    ++report.filesDiscovered;
    bool changed = force;
    bool any = false;
    const auto prefix = path + "#";
    for (const auto& [p, e] : entries_) {
        if (e.family != kZCodeFamily || p.rfind(prefix, 0) != 0) continue;
        any = true;
        changed |= !unchanged(&e, kZCodeFamily, stamp->length(), stamp->lastWriteNs(), false);
    }
    if (any && !changed) return;
    for (const auto& [p, e] : entries_) {
        if (e.family == kZCodeFamily && p.rfind(prefix, 0) == 0) work.replaced.insert(e.id);
    }
    work.zcode = Work::ZCodeWork{path, stamp->length(), stamp->lastWriteNs()};
}

void Analyzer::analyzeZCode(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report) {
    if (!work.zcode) return;
    std::vector<zcode::Row> rows;
    try {
        rows = zcode::Database::open(work.zcode->path).read();
    } catch (const zcode::UnsupportedSchema&) {
        return;
    } catch (...) {
        ++report.filesUnreadable;
        return;
    }
    std::vector<std::string> order;
    std::map<std::string, std::vector<const zcode::Row*>> sessions;
    for (const auto& row : rows) {
        auto& group = sessions[row.sessionId];
        if (group.empty()) order.push_back(row.sessionId);
        group.push_back(&row);
    }
    for (const auto& sessionId : order) {
        const auto id = work.zcode->path + "#" + sessionId;
        std::map<std::string, ParsedResponse> latest;
        std::vector<std::string> keys;
        for (const auto* row : sessions[sessionId]) {
            const auto response = row->response(work.zcode->path);
            if (!response) continue;
            std::string owner;
            if (ledger.tryClaim(response->key, id, &owner) || owner == id) {
                if (!latest.contains(response->key)) keys.push_back(response->key);
                latest[response->key] = *response;
            }
        }
        std::vector<ParsedResponse> responses;
        for (const auto& key : keys) responses.push_back(latest[key]);
        StoredFile entry;
        entry.path = id;
        entry.family = kZCodeFamily;
        entry.size = work.zcode->size;
        entry.modified = work.zcode->modified;
        entry.version = kVersion;
        entry.days = contributionsOf(responses, SourceKind::ZCode, labeler, *prices_);
        entry.perf = emittedRecords(responses, SourceKind::ZCode, labeler);
        keep(std::move(entry), ledger.finish(id));
        ++report.filesAnalyzed;
    }
}

void Analyzer::planOpenClaw(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report) {
    if (!enabled.contains(SourceKind::OpenClaw) || locations.openClawRoots.empty()) return;
    for (const auto& path : paths::openClawDatabases(locations.openClawRoots)) {
        const auto stamp = sqlite::stampOf(path);
        if (!stamp) continue;
        ++report.filesDiscovered;
        const auto prefix = path + "#";
        bool windowsUnpriced = false;
        for (const auto& [p, e] : entries_) windowsUnpriced |= e.family == kOpenClawFamily && p.rfind(prefix, 0) == 0 && e.hasUnpriced();
        const auto* previous = previousOf(path);
        if (unchanged(previous, kOpenClawDatabaseFamily, stamp->length(), stamp->lastWriteNs(), force) && !(repricing_ && windowsUnpriced)) {
            continue;
        }
        if (previous) work.replaced.insert(previous->id);
        for (const auto& [p, e] : entries_) {
            if (e.family == kOpenClawFamily && p.rfind(prefix, 0) == 0) work.replaced.insert(e.id);
        }
        work.openClaw.push_back({path, *stamp});
    }
    std::sort(work.openClaw.begin(), work.openClaw.end(), [](const auto& a, const auto& b) {
        return a.stamp.lastWriteNs() != b.stamp.lastWriteNs() ? a.stamp.lastWriteNs() < b.stamp.lastWriteNs() : a.path < b.path;
    });
}

void Analyzer::analyzeOpenClaw(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report) {
    for (const auto& database : work.openClaw) {
        std::map<const openclaw::Parser*, std::unique_ptr<Latest>> sinks;
        std::map<const openclaw::Parser*, std::unique_ptr<work::ActionReader>> readers;
        std::optional<std::vector<std::unique_ptr<openclaw::Parser>>> parsers;
        try {
            parsers = openclaw::readAll(database.path, ledger, [&](openclaw::Parser& parser) -> ResponseSink& {
                auto& sink = sinks[&parser];
                if (!sink) sink = std::make_unique<Latest>(false);
                auto& reader = readers[&parser];
                reader = std::make_unique<work::ActionReader>(parser.agentId(), 0);
                reader->setResultHandler([target = sink.get()](const work::Action& action) { target->action(action); });
                parser.setLineObserver(reader.get());
                return *sink;
            });
        } catch (...) {
            ledger.abandon();
            ++report.filesUnreadable;
            continue;
        }
        auto owned = ledger.finishAll();
        if (parsers) {
            for (const auto& parser : *parsers) {
                StoredFile entry;
                entry.path = parser->agentId();
                entry.family = kOpenClawFamily;
                entry.size = database.stamp.length();
                entry.modified = database.stamp.lastWriteNs();
                entry.version = kVersion;
                const auto sink = sinks.find(parser.get());
                entry.days = contributionsOf(sink == sinks.end() ? std::vector<ParsedResponse>{} : sink->second->items(), SourceKind::OpenClaw,
                                             labeler, *prices_);
                if (sink != sinks.end()) entry.perf = sink->second->perfRecords(parser->agentId(), SourceKind::OpenClaw, labeler);
                keep(std::move(entry), owned[parser->agentId()]);
            }
            ++report.filesAnalyzed;
            report.bytesAnalyzed += database.stamp.length();
        }
        StoredFile marker;
        marker.path = database.path;
        marker.family = kOpenClawDatabaseFamily;
        marker.size = database.stamp.length();
        marker.modified = database.stamp.lastWriteNs();
        marker.version = kVersion;
        keep(std::move(marker), {});
    }
}

void Analyzer::planHermes(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report) {
    if (!enabled.contains(SourceKind::Hermes) || locations.hermesRoot.empty()) return;
    for (const auto& folder : locations.hermesFolders()) {
        std::vector<Work::HermesWork> files;
        for (const auto& path : hermes::logFiles(folder)) {
            const auto s = fsx::statOf(path, true);
            if (!s || !s->regular) continue;
            ++report.filesDiscovered;
            const auto marker = std::to_string(s->device) + ":" + std::to_string(s->inode) + "|" + folder;
            const auto* previous = previousOf(marker);
            bool sessionsUnpriced = false;
            for (const auto& [p, e] : entries_) sessionsUnpriced |= e.family == kHermesFamily && p.rfind(marker + "#", 0) == 0 && e.hasUnpriced();
            if (unchanged(previous, kHermesLogFamily, s->length, s->writeNs, force) && !(repricing_ && sessionsUnpriced)) continue;
            if (previous) work.replaced.insert(previous->id);
            for (const auto& [p, e] : entries_) {
                if (e.family == kHermesFamily && p.rfind(marker + "#", 0) == 0) work.replaced.insert(e.id);
            }
            files.push_back({folder, path, marker, s->length, s->writeNs});
        }
        std::sort(files.begin(), files.end(), [](const auto& a, const auto& b) {
            return a.writeNs != b.writeNs ? a.writeNs < b.writeNs : a.path < b.path;
        });
        work.hermes.insert(work.hermes.end(), files.begin(), files.end());
    }
}

void Analyzer::analyzeHermes(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report) {
    const auto zone = hermesZone_ ? hermesZone_ : TimeZone::load("localtime");
    for (const auto& log : work.hermes) {
        Latest sink(false);
        hermes::LogSet set(log.folder, ledger, zone ? zone : TimeZone::utc());
        std::int64_t size = 0;
        try {
            std::ifstream in(log.path, std::ios::binary);
            if (!in) throw std::runtime_error("unreadable");
            LineSplitter lines;
            std::vector<char> buffer(1 << 20);
            while (in) {
                in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
                const auto got = in.gcount();
                if (got <= 0) break;
                size += got;
                lines.feed(std::string_view(buffer.data(), static_cast<std::size_t>(got)), [&](std::string_view line) { set.line(line, sink); });
            }
        } catch (...) {
            ledger.abandon();
            ++report.filesUnreadable;
            continue;
        }
        auto owned = ledger.finishAll();
        std::vector<std::uint64_t> all;
        for (auto& [owner, hashes] : owned) all.insert(all.end(), hashes.begin(), hashes.end());
        StoredFile marker;
        marker.path = log.marker;
        marker.family = kHermesLogFamily;
        marker.size = size;
        marker.modified = log.writeNs;
        marker.version = kVersion;
        keep(std::move(marker), std::move(all));
        std::map<std::string, std::vector<ParsedResponse>> byAgent;
        for (const auto& r : sink.items()) byAgent[r.agentId].push_back(r);
        std::set<std::string> written;
        for (const auto& [agent, responses] : byAgent) {
            const auto path = log.marker + agent.substr(log.folder.size());
            written.insert(path);
            StoredFile entry;
            entry.path = path;
            entry.family = kHermesFamily;
            entry.size = size;
            entry.modified = log.writeNs;
            entry.version = kVersion;
            entry.days = contributionsOf(responses, SourceKind::Hermes, labeler, *prices_);
            entry.perf = sink.perfRecords(agent, SourceKind::Hermes, labeler, &agent);
            keep(std::move(entry), {});
        }
        // Sessions this file no longer has (it was rewritten) keep nothing of it.
        std::vector<std::string> stale;
        for (const auto& [p, e] : entries_) {
            if (e.family == kHermesFamily && p.rfind(log.marker + "#", 0) == 0 && !written.contains(p)) stale.push_back(p);
        }
        for (const auto& p : stale) {
            StoredFile entry;
            entry.path = p;
            entry.family = kHermesFamily;
            entry.size = size;
            entry.modified = log.writeNs;
            entry.version = kVersion;
            keep(std::move(entry), {});
        }
        ++report.filesAnalyzed;
        report.bytesAnalyzed += size;
    }
}

std::vector<DailyUsage> Analyzer::days(const std::set<SourceKind>* sources, const std::map<std::string, double>* livePeaks) const {
    std::map<DayKey, DayAggregate> totals;
    std::map<DayKey, std::set<std::string>> hermesAgents;
    for (const auto& [path, entry] : entries_) {
        // A Hermes session spans rotated files: its days count it once.
        const bool isHermes = entry.family == kHermesFamily;
        const auto agent = isHermes ? path.substr(path.find('|') + 1) : std::string();
        for (const auto& [key, day] : entry.days) {
            if (sources && !sources->contains(key.source)) continue;
            auto& total = totals[key];
            total.merge(day);
            if (isHermes) {
                total.agents -= day.agents;
                if (day.requests > 0) hermesAgents[key].insert(agent);
            }
        }
    }
    for (const auto& [key, agents] : hermesAgents) totals[key].agents += static_cast<int>(agents.size());
    std::vector<DailyUsage> out;
    for (const auto& [key, total] : totals) {
        DailyUsage usage;
        usage.key = key;
        usage.totals = total;
        std::optional<double> peak;
        if (const auto it = peaks_.find(key.day); it != peaks_.end()) peak = it->second;
        if (livePeaks) {
            if (const auto it = livePeaks->find(key.day); it != livePeaks->end() && it->second > peak.value_or(0)) peak = it->second;
        }
        usage.peak = peak;
        if (total.costMicroUsd) usage.priceVersion = prices_->boundVersion(key.day);
        out.push_back(std::move(usage));
    }
    std::stable_sort(out.begin(), out.end(), [](const DailyUsage& a, const DailyUsage& b) {
        if (a.key.day != b.key.day) return a.key.day < b.key.day;
        return sourceId(a.key.source) < sourceId(b.key.source);
    });
    return out;
}

bool Analyzer::recordPeaks(const std::map<std::string, double>& peaks) {
    std::map<std::string, double> raised;
    for (const auto& [day, peak] : peaks) {
        if (std::isfinite(peak) && peak > 0 && peak > (peaks_.contains(day) ? peaks_.at(day) : 0)) raised[day] = peak;
    }
    if (raised.empty()) return false;
    store_.savePeaks(raised);
    for (const auto& [day, peak] : raised) peaks_[day] = peak;
    return true;
}

void Analyzer::clear() {
    store_.clear();
    entries_.clear();
    peaks_.clear();
    prices_->clearBindings();
}

}  // namespace agentspeed::history
