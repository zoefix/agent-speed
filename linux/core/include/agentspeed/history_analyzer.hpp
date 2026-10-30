#pragma once

#include <functional>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/droid.hpp"
#include "agentspeed/history.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/unpriced.hpp"

namespace agentspeed::pricing {
class PriceBook;
}

namespace agentspeed::history {

struct Report {
    int filesDiscovered = 0;
    int filesPlanned = 0;
    int filesAnalyzed = 0;
    std::int64_t bytesAnalyzed = 0;
    int sessionsAnalyzed = 0;
    int filesUnreadable = 0;
    bool cancelled = false;
};

// METRICS 8: analyses every discovered file of every enabled source with the parsers of section 4 over whole files,
// in startup order, and keeps per-file contributions; unchanged files are skipped.
class Analyzer {
public:
    // The parsers' analysis version: a change re-analyses every file.
    // 2: task records; 3: performance records instead (METRICS 6.11); 4: the effort and speed breakdowns
    // (EXECUTION-MODES).
    static constexpr int kVersion = 4;

    Analyzer(Store& store, std::shared_ptr<pricing::PriceBook> prices, std::shared_ptr<const TimeZone> hermesZone = nullptr);

    // `zoneId` names the zone the days are labelled in; a change re-analyses everything. `progress` hears the files
    // analysed so far and the files planned.
    Report run(const SourceLocations& locations, const std::set<SourceKind>& enabled, std::shared_ptr<const TimeZone> zone,
               const std::string& zoneId, const std::function<bool()>& cancelled = {},
               const std::function<void(int, int)>& progress = {});
    // Day rows, by day then source id; live peaks raise the stored ones.
    std::vector<DailyUsage> days(const std::set<SourceKind>* sources = nullptr, const std::map<std::string, double>* livePeaks = nullptr) const;
    bool recordPeaks(const std::map<std::string, double>& peaks);
    // Every stored performance record (METRICS 6.11): per file, day, source and raw model; a day's record of a
    // (source, model) is the sum over files.
    std::vector<perf::DayRecord> perfRecords(const std::set<SourceKind>* sources = nullptr) const;
    // METRICS 9.10 Reading: the unpriced facts of every stored contribution, merged per (day, source).
    std::vector<unpriced::Part> unpricedParts(const std::set<SourceKind>* sources = nullptr) const;
    void clear();
    pricing::PriceBook& prices() { return *prices_; }

private:
    struct Planned {
        std::string path, root;
        RootFamily family;
        std::int64_t size, writeNs, identity;
        const StoredFile* previous;
    };
    void saveBindings();
    void flush(std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>>& pending);
    struct Work;
    void planFiles(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report);
    void planOpenCode(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, const std::string& zoneId, Work& work,
                      Report& report);
    void planZCode(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report);
    void planOpenClaw(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report);
    void planHermes(const SourceLocations& locations, const std::set<SourceKind>& enabled, bool force, Work& work, Report& report);
    void analyzeFiles(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report, const std::function<bool()>& cancelled);
    void analyzeOpenClaw(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report);
    void analyzeHermes(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report);
    void analyzeZCode(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report);
    void analyzeOpenCode(Work& work, KeyLedger& ledger, const DayLabeler& labeler, Report& report);
    void advance(Report& report);
    bool unchanged(const StoredFile* previous, const std::string& family, std::int64_t size, std::int64_t modified, bool force) const;
    const StoredFile* previousOf(const std::string& path) const;
    StoredFile& keep(StoredFile file, std::vector<std::uint64_t> owned);
    std::map<std::string, droid::SidecarSignature> droidUnreadableParents_;

    Store& store_;
    std::shared_ptr<pricing::PriceBook> prices_;
    std::shared_ptr<const TimeZone> hermesZone_;
    std::map<std::string, StoredFile> entries_;
    std::map<std::string, double> peaks_;
    std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>> pending_;
    bool repricing_ = false;
    std::function<void(int, int)> progress_;
};

}  // namespace agentspeed::history
