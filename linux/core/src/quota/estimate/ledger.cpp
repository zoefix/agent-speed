#include "agentspeed/quota/estimate/ledger.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <set>

#include "agentspeed/quota/estimate/model.hpp"

namespace agentspeed::quota::estimate {

namespace {

std::string lowered(std::string_view text) {
    std::string out(text);
    for (auto& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

std::int64_t ceilMinute(std::int64_t ms) { return -floorDiv(-ms, kMinute) * kMinute; }

}  // namespace

bool modelMatches(std::string_view model, std::string_view name) {
    if (name.empty()) return true;
    std::set<std::string> tokens;
    const auto lower = lowered(model);
    std::size_t start = 0;
    while (true) {
        const auto dash = lower.find('-', start);
        tokens.insert(lower.substr(start, dash == std::string::npos ? std::string::npos : dash - start));
        if (dash == std::string::npos) break;
        start = dash + 1;
    }
    const auto words = lowered(name);
    std::size_t i = 0;
    while (i < words.size()) {
        while (i < words.size() && std::isspace(static_cast<unsigned char>(words[i]))) ++i;
        if (i >= words.size()) break;
        std::size_t j = i;
        while (j < words.size() && !std::isspace(static_cast<unsigned char>(words[j]))) ++j;
        if (!tokens.contains(words.substr(i, j - i))) return false;
        i = j;
    }
    return true;
}

Ledger::Ledger(const Ledger& other) : rows_(other.rows_) {}

Ledger& Ledger::operator=(const Ledger& other) {
    if (this != &other) {
        rows_ = other.rows_;
        const std::lock_guard lock(mutex_);
        cache_.clear();
    }
    return *this;
}

void Ledger::add(const std::string& account, const std::string& source, const std::string& model, std::int64_t endMs,
                 std::optional<std::int64_t> cost, std::int64_t requests, std::int64_t tokens) {
    auto& row = rows_[{account, source, model, minuteOf(endMs)}];
    if (!cost) {
        row.unpricedRequests += requests;
        row.unpricedTokens += tokens;
    } else {
        row.cost += *cost;
        row.pricedTokens += tokens;
    }
    const std::lock_guard lock(mutex_);
    cache_.clear();
}

void Ledger::prune(std::int64_t nowMs) {
    std::erase_if(rows_, [&](const auto& item) { return std::get<3>(item.first) < nowMs - kKeepMs; });
    const std::lock_guard lock(mutex_);
    cache_.clear();
}

const Ledger::Series& Ledger::series(const std::string& account, const std::string& name) const {
    const auto key = std::make_pair(account, name);
    if (const auto it = cache_.find(key); it != cache_.end()) return it->second;
    std::map<std::int64_t, std::array<std::int64_t, 3>> per;
    std::map<std::string, std::pair<std::int64_t, std::int64_t>> rates;
    for (auto it = rows_.lower_bound({account, std::string(), std::string(), INT64_MIN}); it != rows_.end() && std::get<0>(it->first) == account; ++it) {
        const auto& model = std::get<2>(it->first);
        if (!modelMatches(model, name)) continue;
        auto& v = per[std::get<3>(it->first)];
        v[0] += it->second.cost;
        v[1] += it->second.unpricedRequests;
        v[2] += it->second.unpricedTokens;
        auto& r = rates[model];
        r.first += it->second.cost;
        r.second += it->second.pricedTokens;
    }
    Series s;
    s.cost.push_back(0);
    s.unpriced.push_back(0);
    s.tokens.push_back(0);
    for (const auto& [minute, v] : per) {
        s.minutes.push_back(minute);
        s.cost.push_back(s.cost.back() + v[0]);
        s.unpriced.push_back(s.unpriced.back() + v[1]);
        s.tokens.push_back(s.tokens.back() + v[2]);
    }
    for (const auto& [model, r] : rates) {
        if (r.second <= 0) continue;
        const double rate = static_cast<double>(r.first) / static_cast<double>(r.second);
        if (!s.rate || rate > *s.rate) s.rate = rate;
    }
    return cache_.emplace(key, std::move(s)).first->second;
}

Ledger::Range Ledger::range(const std::string& account, const std::optional<std::string>& name, std::int64_t first, std::int64_t last) const {
    const std::lock_guard lock(mutex_);
    const auto& s = series(account, name.value_or(std::string()));
    Range out;
    out.rate = s.rate;
    if (last < first) return out;
    const auto i = static_cast<std::size_t>(std::lower_bound(s.minutes.begin(), s.minutes.end(), first) - s.minutes.begin());
    const auto j = static_cast<std::size_t>(std::upper_bound(s.minutes.begin(), s.minutes.end(), last) - s.minutes.begin());
    out.cost = s.cost[j] - s.cost[i];
    out.unpriced = s.unpriced[j] - s.unpriced[i];
    out.tokens = s.tokens[j] - s.tokens[i];
    return out;
}

Spend Ledger::inner(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const {
    const auto r = range(account, name, ceilMinute(start), minuteOf(end) - kMinute);
    return {r.cost, r.unpriced};
}

Spend Ledger::outer(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const {
    const auto r = range(account, name, minuteOf(start), minuteOf(end));
    return {r.cost, r.unpriced};
}

double Ledger::outerHigh(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end) const {
    const auto r = range(account, name, minuteOf(start), minuteOf(end));
    if (!r.unpriced) return static_cast<double>(r.cost);
    if (!r.rate) return kInfinity;
    return static_cast<double>(r.cost) + static_cast<double>(r.tokens) * *r.rate;
}

std::vector<MixShare> Ledger::mix(const std::string& account, const std::optional<std::string>& name, std::int64_t start, std::int64_t end,
                                  std::size_t top) const {
    std::map<std::string, std::int64_t> totals;
    const auto first = minuteOf(start), last = minuteOf(end);
    for (auto it = rows_.lower_bound({account, std::string(), std::string(), INT64_MIN}); it != rows_.end() && std::get<0>(it->first) == account; ++it) {
        const auto minute = std::get<3>(it->first);
        if (it->second.cost <= 0 || minute < first || minute > last || !modelMatches(std::get<2>(it->first), name.value_or(std::string()))) continue;
        totals[std::get<2>(it->first)] += it->second.cost;
    }
    std::int64_t whole = 0;
    for (const auto& [_, c] : totals) whole += c;
    if (whole <= 0) return {};
    std::vector<std::pair<std::string, std::int64_t>> ranked(totals.begin(), totals.end());
    std::stable_sort(ranked.begin(), ranked.end(), [](const auto& a, const auto& b) { return a.second != b.second ? a.second > b.second : a.first < b.first; });
    if (ranked.size() > top) ranked.resize(top);
    std::vector<MixShare> out;
    for (const auto& [model, c] : ranked) out.push_back({model, roundHalfAway(static_cast<double>(c) / static_cast<double>(whole), 3)});
    return out;
}

}  // namespace agentspeed::quota::estimate
