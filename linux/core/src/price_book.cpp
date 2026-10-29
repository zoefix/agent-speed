#include "agentspeed/price_book.hpp"

#include <algorithm>
#include <stdexcept>

namespace agentspeed::pricing {

PriceBook::PriceBook(std::vector<std::shared_ptr<const PriceTable>> tables, std::map<std::string, int> bindings)
    : bindings_(std::move(bindings)) {
    for (auto& table : tables) {
        if (table) tables_[table->version()] = std::move(table);
    }
    if (tables_.empty()) throw std::invalid_argument("A price book needs at least one table.");
    current_ = tables_.rbegin()->first;
}

PriceBook::PriceBook() : PriceBook({bundled()}) {}

std::shared_ptr<const PriceTable> PriceBook::bundled() {
    static const auto table = [] {
        TableError error;
        auto loaded = PriceTable::load(bundledPricesJson(), &error);
        if (!loaded) throw std::runtime_error("The bundled price table does not validate: " + error.describe());
        return loaded;
    }();
    return table;
}

std::shared_ptr<const PriceTable> PriceBook::current() const {
    std::lock_guard lock(mutex_);
    return tables_.at(current_);
}

int PriceBook::currentVersion() const {
    std::lock_guard lock(mutex_);
    return current_;
}

std::shared_ptr<const PriceTable> PriceBook::table(int version) const {
    std::lock_guard lock(mutex_);
    const auto it = tables_.find(version);
    return it == tables_.end() ? nullptr : it->second;
}

std::map<std::string, int> PriceBook::bindings() const {
    std::lock_guard lock(mutex_);
    return bindings_;
}

bool PriceBook::add(std::shared_ptr<const PriceTable> table) {
    std::lock_guard lock(mutex_);
    const auto version = table->version();
    tables_.try_emplace(version, std::move(table));
    if (version <= current_) return false;
    current_ = version;
    return true;
}

std::vector<std::string> PriceBook::restore(const std::map<std::string, int>& stored) {
    std::lock_guard lock(mutex_);
    std::vector<std::string> repaired;
    for (const auto& [day, version] : stored) {
        if (tables_.contains(version)) {
            bindings_[day] = version;
            unsaved_.erase(day);
        } else {
            const auto bound = bindings_.find(day);
            if (bound == bindings_.end() || !tables_.contains(bound->second)) bindings_[day] = current_;
            unsaved_.insert(day);
            repaired.push_back(day);
        }
    }
    std::sort(repaired.begin(), repaired.end());
    return repaired;
}

void PriceBook::clearBindings() {
    std::lock_guard lock(mutex_);
    bindings_.clear();
    unsaved_.clear();
}

int PriceBook::versionForDay(const std::string& day) {
    std::function<void()> observer;
    int version = 0;
    {
        std::lock_guard lock(mutex_);
        if (const auto it = bindings_.find(day); it != bindings_.end() && tables_.contains(it->second)) return it->second;
        version = bindings_[day] = current_;
        unsaved_.insert(day);
        observer = observer_;
    }
    if (observer) observer();
    return version;
}

std::optional<int> PriceBook::boundVersion(const std::string& day) const {
    std::lock_guard lock(mutex_);
    const auto it = bindings_.find(day);
    return it == bindings_.end() ? std::nullopt : std::optional<int>(it->second);
}

std::optional<std::int64_t> PriceBook::cost(const ParsedResponse& r, const std::string& day) {
    return cost(r.model, r.provider, r.tokens, r.endMs, r.fast, day);
}

std::optional<std::int64_t> PriceBook::cost(const std::optional<std::string>& model, const std::optional<std::string>& provider,
                                            const TokenCounts& tokens, std::optional<std::int64_t> endMs, bool fast, const std::string& day) {
    const auto version = versionForDay(day);
    std::shared_ptr<const PriceTable> bound, latest;
    {
        std::lock_guard lock(mutex_);
        bound = tables_.at(version);
        latest = tables_.at(current_);
    }
    if (auto value = bound->cost(model, provider, tokens, endMs, fast)) return value;
    return version == latest->version() ? std::nullopt : latest->cost(model, provider, tokens, endMs, fast);
}

std::map<std::string, int> PriceBook::takeNewBindings() {
    std::lock_guard lock(mutex_);
    std::map<std::string, int> result;
    for (const auto& day : unsaved_) {
        if (const auto it = bindings_.find(day); it != bindings_.end()) result[day] = it->second;
    }
    unsaved_.clear();
    return result;
}

void PriceBook::onBound(std::function<void()> observer) {
    std::lock_guard lock(mutex_);
    observer_ = std::move(observer);
}

}  // namespace agentspeed::pricing
