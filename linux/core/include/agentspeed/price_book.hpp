#pragma once

#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/pricing.hpp"

// METRICS 9.7: the known price tables and the day bindings (each local date keeps the version it was first priced
// with; a response that table cannot price takes the current one). Thread-safe.
namespace agentspeed::pricing {

class PriceBook {
public:
    // Not empty; the highest version is current.
    explicit PriceBook(std::vector<std::shared_ptr<const PriceTable>> tables, std::map<std::string, int> bindings = {});
    // A book with only the bundled table.
    PriceBook();

    // The table bundled with this build (spec/assets/prices.json).
    static std::shared_ptr<const PriceTable> bundled();

    std::shared_ptr<const PriceTable> current() const;
    int currentVersion() const;
    std::shared_ptr<const PriceTable> table(int version) const;
    std::map<std::string, int> bindings() const;
    // Returns true when the table became the current one.
    bool add(std::shared_ptr<const PriceTable> table);
    // Takes the stored bindings; a binding whose table is unknown is repaired to the current version (returned).
    std::vector<std::string> restore(const std::map<std::string, int>& stored);
    void clearBindings();
    // The version bound to the date, binding it to the current table first when it has none.
    int versionForDay(const std::string& day);
    std::optional<int> boundVersion(const std::string& day) const;
    std::optional<std::int64_t> cost(const ParsedResponse& response, const std::string& day);
    std::optional<std::int64_t> cost(const std::optional<std::string>& model, const std::optional<std::string>& provider,
                                     const TokenCounts& tokens, std::optional<std::int64_t> endMs, bool fast, const std::string& day);
    // Bindings made since the last call, for the history store.
    std::map<std::string, int> takeNewBindings();
    void onBound(std::function<void()> observer);

private:
    mutable std::mutex mutex_;
    std::map<int, std::shared_ptr<const PriceTable>> tables_;
    std::map<std::string, int> bindings_;
    std::set<std::string> unsaved_;
    int current_ = 0;
    std::function<void()> observer_;
};

// The bytes of spec/assets/prices.json, compiled into the library.
std::string_view bundledPricesJson();

}  // namespace agentspeed::pricing
