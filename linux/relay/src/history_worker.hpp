#pragma once

#include <atomic>
#include <condition_variable>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <utility>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/price_book.hpp"
#include "channel.hpp"

// REMOTE 8.1, 8.2: the host's own history store in `<folder>/history.sqlite`, analysed on one low-priority thread, one
// run at a time; each `history` request is answered after the run it waits for.
namespace agentspeed::relay {

class HistoryWorker {
public:
    HistoryWorker(Output& out, std::string folder, std::set<SourceKind> enabled, std::shared_ptr<const pricing::PriceTable> table);
    ~HistoryWorker();
    HistoryWorker(const HistoryWorker&) = delete;
    HistoryWorker& operator=(const HistoryWorker&) = delete;

    // The app's zone (base64 TZif): the next run labels days with it.
    void setZone(std::string id, std::string tzif);
    void setPrices(std::shared_ptr<const pricing::PriceTable> table);
    void request(std::map<std::string, std::string> digests);
    // Cancels a run in progress and ends the thread.
    void stop();

private:
    void run();
    void answer(const std::map<std::string, std::string>& digests);
    bool openStore();
    void applyZone();

    Output& out_;
    const std::string folder_;
    const std::set<SourceKind> enabled_;
    const SourceLocations locations_;
    std::shared_ptr<pricing::PriceBook> book_;
    std::shared_ptr<const TimeZone> hermesZone_;
    std::unique_ptr<history::Store> store_;
    std::unique_ptr<history::Analyzer> analyzer_;
    std::shared_ptr<const TimeZone> zone_;
    std::string zoneId_;

    std::mutex mutex_;
    std::condition_variable wake_;
    std::deque<std::map<std::string, std::string>> requests_;
    std::optional<std::pair<std::string, std::string>> pendingZone_;
    bool stopping_ = false;
    std::atomic<bool> cancelled_{false};
    std::thread thread_;
};

}  // namespace agentspeed::relay
