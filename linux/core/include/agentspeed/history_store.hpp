#pragma once

#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/history.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/task_score.hpp"

// The persistent daily history (METRICS 8): per-file identities, owned key hashes and per-day contributions, plus the
// peaks observed live, the price bindings (9.7) and the table copies they reference.
namespace agentspeed::history {

struct StoredFile {
    std::int64_t id = 0;
    std::string path;
    std::string family;
    std::int64_t size = 0;
    std::int64_t modified = 0;
    int version = 0;
    Contributions days;
    // METRICS 6.11: the task records of a scored agent's file (an OpenClaw window), current version only.
    std::vector<tasks::TaskRecord> records;
    bool hasUnpriced() const;
    bool hasIncompleteTask() const;
};

class Store {
public:
    static constexpr int kSchema = 1;

    // Creates the store when missing; a store of another schema starts over.
    static std::unique_ptr<Store> open(const std::string& path);

    std::map<std::string, StoredFile> loadFiles() const;
    // Inserts or replaces each file with its owned key hashes and contributions; assigns new ids.
    void save(std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>>& files);
    // Settles the key hashes of every stored file except `except` (their files are analysed again).
    void loadKeys(KeyLedger& ledger, const std::set<std::int64_t>& except) const;
    void loadKeys(KeyLedger& ledger, std::int64_t fileId) const;

    std::map<std::string, double> peaks() const;
    void savePeaks(const std::map<std::string, double>& peaks);
    std::map<std::string, int> priceBindings() const;
    std::map<int, std::string> priceTables() const;
    void savePriceBindings(const std::map<std::string, int>& bindings, const std::function<std::string(int)>& tableData);

    std::optional<std::string> meta(const std::string& key) const;
    void setMeta(const std::string& key, const std::string& value);
    // "Clear history": every table emptied.
    void clear();

private:
    explicit Store(sqlite::Connection connection) : db_(std::move(connection)) {}
    void createSchema();

    sqlite::Connection db_;
};

// Compact JSON of one contribution and back (the `data` column).
std::string encode(const DayAggregate& day);
std::optional<DayAggregate> decode(std::string_view text);

}  // namespace agentspeed::history
