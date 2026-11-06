#include "history_worker.hpp"

#include <sys/resource.h>
#include <sys/syscall.h>
#include <unistd.h>

#include <chrono>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/tz.hpp"
#include "history_sync.hpp"
#include "zone_file.hpp"

namespace agentspeed::relay {

namespace {

constexpr int kNice = 10;
constexpr std::int64_t kProgressIntervalMs = 1000;
// More waiting requests than this are the app's retries; the oldest go unanswered.
constexpr std::size_t kMaxQueued = 8;

std::int64_t steadyNowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

SourceLocations hostLocations() {
    const auto environment = processEnvironment();
    return SourceLocations::resolve({}, environment, userHome(environment));
}

}  // namespace

HistoryWorker::HistoryWorker(Output& out, std::string folder, std::set<SourceKind> enabled, std::shared_ptr<const pricing::PriceTable> table)
    : out_(out), folder_(std::move(folder)), enabled_(std::move(enabled)), locations_(hostLocations()),
      book_(std::make_shared<pricing::PriceBook>(std::vector<std::shared_ptr<const pricing::PriceTable>>{table ? table : pricing::PriceBook::bundled()})),
      hermesZone_(localZone(processEnvironment())), zone_(TimeZone::utc()), zoneId_("UTC"), thread_([this] { run(); }) {}

HistoryWorker::~HistoryWorker() { stop(); }

void HistoryWorker::setZone(std::string id, std::string tzif) {
    std::lock_guard lock(mutex_);
    pendingZone_ = std::make_pair(std::move(id), std::move(tzif));
}

void HistoryWorker::setPrices(std::shared_ptr<const pricing::PriceTable> table) {
    if (table) book_->add(std::move(table));
}

void HistoryWorker::request(std::map<std::string, std::string> digests) {
    {
        std::lock_guard lock(mutex_);
        requests_.push_back(std::move(digests));
        while (requests_.size() > kMaxQueued) requests_.pop_front();
    }
    wake_.notify_all();
}

void HistoryWorker::stop() {
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
    }
    cancelled_ = true;
    wake_.notify_all();
    if (thread_.joinable()) thread_.join();
}

void HistoryWorker::run() {
    // Linux nice applies per thread: the analysis yields to the live loop and to the host's own work (REMOTE 3.4).
    setpriority(PRIO_PROCESS, static_cast<id_t>(::syscall(SYS_gettid)), kNice);
    while (true) {
        std::map<std::string, std::string> digests;
        {
            std::unique_lock lock(mutex_);
            wake_.wait(lock, [&] { return stopping_ || !requests_.empty(); });
            if (stopping_) return;
            digests = std::move(requests_.front());
            requests_.pop_front();
        }
        try {
            answer(digests);
        } catch (const std::exception&) {
            out_.line(errorLine("historyFailed", "the history analysis failed"));
        }
    }
}

void HistoryWorker::applyZone() {
    std::optional<std::pair<std::string, std::string>> zone;
    {
        std::lock_guard lock(mutex_);
        zone.swap(pendingZone_);
    }
    if (!zone) return;
    auto loaded = installZone(folder_, zone->first, zone->second);
    zone_ = loaded.zone;
    zoneId_ = loaded.id;
}

bool HistoryWorker::openStore() {
    if (analyzer_) return true;
    if (!fsx::isDirectory(folder_)) return false;
    try {
        store_ = history::Store::open(folder_ + "/history.sqlite");
        analyzer_ = std::make_unique<history::Analyzer>(*store_, book_, hermesZone_);
        return true;
    } catch (const std::exception&) {
        analyzer_.reset();
        store_.reset();
        return false;
    }
}

void HistoryWorker::answer(const std::map<std::string, std::string>& digests) {
    applyZone();
    if (!openStore()) {
        out_.line(errorLine("historyFailed", "the history store cannot be opened"));
        return;
    }
    std::int64_t lastProgress = steadyNowMs();
    const auto progress = [&](int done, int total) {
        const auto now = steadyNowMs();
        if (total <= 0 || now - lastProgress < kProgressIntervalMs) return;
        lastProgress = now;
        out_.line(historyProgressLine(static_cast<double>(done) / static_cast<double>(total)));
    };
    history::Report report;
    std::vector<SyncDay> days;
    try {
        report = analyzer_->run(locations_, enabled_, zone_, zoneId_, [this] { return cancelled_.load(); }, progress);
        if (report.cancelled) return;
        days = syncDays(analyzer_->days(), analyzer_->perfRecords());
    } catch (const std::exception&) {
        out_.line(errorLine("historyFailed", "the history analysis failed"));
        return;
    }
    LineBatch lines(out_);
    std::int64_t sent = 0;
    for (const auto* day : changedDays(days, digests)) {
        lines.add(historyDayLine(day->day, day->source, day->aggregate, day->digest));
        for (const auto& [model, record] : day->perf) lines.add(historyPerfLine(day->day, day->source, model, record));
        ++sent;
    }
    lines.add(historyDoneLine(sent, report.filesAnalyzed));
}

}  // namespace agentspeed::relay
