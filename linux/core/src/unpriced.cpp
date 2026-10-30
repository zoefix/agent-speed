#include "agentspeed/unpriced.hpp"

#include <algorithm>
#include <cstdio>
#include <tuple>

#include "agentspeed/text.hpp"

namespace agentspeed::unpriced {

namespace {

constexpr std::size_t kMaxScalars = 120;
// METRICS 8: responses outside 0001-01-03 … 9999-12-29 have no date.
constexpr std::int64_t kDayFirstMs = -62'135'596'800'000 + 2 * 86'400'000;
constexpr std::int64_t kDayLastMs = 253'402'300'799'999 - 2 * 86'400'000;

using Key = std::tuple<std::string, std::optional<std::string>, bool, bool>;

Key factKey(const std::optional<std::string>& model, const std::optional<std::string>& provider, bool fast, bool noDate) {
    std::optional<std::string> p;
    if (provider && !provider->empty()) p = provider;
    return {model ? text::strip(*model) : std::string(), p, fast, noDate};
}

// The fact order: model bytes, provider (none first, then bytes), fast, noDate.
bool factLess(const Key& a, const Key& b) {
    const auto rank = [](const Key& k) {
        return std::make_tuple(std::get<0>(k), std::get<1>(k).has_value(), std::get<1>(k).value_or(""), std::get<2>(k), std::get<3>(k));
    };
    return rank(a) < rank(b);
}

std::vector<Fact> factList(const std::map<Key, std::int64_t>& tally) {
    std::vector<std::pair<Key, std::int64_t>> items(tally.begin(), tally.end());
    std::sort(items.begin(), items.end(), [](const auto& a, const auto& b) { return factLess(a.first, b.first); });
    std::vector<Fact> out;
    for (const auto& [k, n] : items) {
        if (n > 0) out.push_back({std::get<0>(k), std::get<1>(k), std::get<2>(k), std::get<3>(k), n});
    }
    return out;
}

std::size_t sourceRank(SourceKind source) { return static_cast<std::size_t>(source); }

bool hasDay(std::int64_t ms) { return kDayFirstMs <= ms && ms <= kDayLastMs; }

std::string asciiLower(std::string_view text) {
    std::string out(text);
    for (auto& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
    }
    return out;
}

bool pathLike(std::string_view value) {
    const auto low = asciiLower(value);
    if (low.starts_with("/") || low.starts_with("\\") || low.starts_with("~") || low.starts_with("file:")) return true;
    if (low.size() >= 3 && low[0] >= 'a' && low[0] <= 'z' && low[1] == ':' && (low[2] == '/' || low[2] == '\\')) return true;
    return low.find("/users/") != std::string::npos || low.find("\\users\\") != std::string::npos || low.find("/home/") != std::string::npos;
}

std::int64_t daysBetween(const std::string& first, const std::string& last) {
    const auto a = pricing::UtcDay::instantMs(first), b = pricing::UtcDay::instantMs(last);
    if (!a || !b) return 0;
    return (*b - *a) / 86'400'000 + 1;
}

}  // namespace

std::string_view reasonId(Reason reason) {
    switch (reason) {
        case Reason::NoModel: return "noModel";
        case Reason::NotInTable: return "notInTable";
        case Reason::NoFastPrice: return "noFastPrice";
        case Reason::Unpriceable: return "unpriceable";
        case Reason::NoDate: return "noDate";
    }
    return {};
}

std::optional<Diagnosis> diagnose(const pricing::PriceTable& table, const std::optional<std::string>& model,
                                  const std::optional<std::string>& provider, bool fast) {
    const auto& resolver = table.resolver();
    const auto outcome = resolver.walk(model, provider);
    switch (outcome.resolution.kind) {
        case pricing::ResolutionKind::Free:
            return std::nullopt;
        case pricing::ResolutionKind::Priced:
            if ((outcome.resolution.fast || fast) && !resolver.hasFast(outcome.resolution.index)) return Diagnosis{Reason::NoFastPrice, outcome.key, std::nullopt};
            return std::nullopt;
        case pricing::ResolutionKind::Unknown:
            break;
    }
    switch (outcome.miss) {
        case pricing::Miss::NoModel: return Diagnosis{Reason::NoModel, std::nullopt, std::nullopt};
        case pricing::Miss::NotInTable: {
            const auto exact = pricing::ModelResolver::exactKey(model.value_or(""));
            return Diagnosis{Reason::NotInTable, exact.empty() ? std::nullopt : std::optional<std::string>(exact), std::nullopt};
        }
        case pricing::Miss::NoFastPrice: return Diagnosis{Reason::NoFastPrice, outcome.key, std::nullopt};
        default: return Diagnosis{Reason::Unpriceable, outcome.key, outcome.rule};
    }
}

Part readPart(const Stored& stored) {
    Part out;
    out.requests = stored.requests;
    if (!stored.unpriced) {
        out.noCostData = stored.requests;
        return out;
    }
    const auto unpriced = *stored.unpriced;
    if (unpriced <= 0) return out;
    std::int64_t sum = 0;
    bool valid = !stored.side.empty();
    for (const auto& r : stored.side) {
        valid = valid && r.version == stored.fileVersion && r.requests >= 1;
        sum += r.requests;
    }
    std::map<Key, std::int64_t> tally;
    if (valid && sum == unpriced) {
        for (const auto& r : stored.side) tally[factKey(r.model, r.provider, r.fast, false)] += r.requests;
        out.facts = factList(tally);
        return out;
    }
    std::int64_t left = unpriced;
    for (const auto& [model, n] : stored.models) {  // std::map: UTF-8 byte order
        const auto take = std::min(std::max<std::int64_t>(n, 0), left);
        if (take > 0) {
            tally[factKey(model, std::nullopt, false, false)] += take;
            left -= take;
        }
    }
    if (left > 0) tally[factKey(std::string(), std::nullopt, false, false)] += left;
    out.facts = factList(tally);
    out.derived = unpriced;
    return out;
}

std::vector<Part> mergeParts(const std::vector<Part>& parts) {
    struct Merged {
        Part part;
        std::map<Key, std::int64_t> tally;
    };
    std::map<std::pair<std::string, std::size_t>, Merged> merged;
    for (const auto& p : parts) {
        auto& m = merged[{p.day, sourceRank(p.source)}];
        m.part.day = p.day;
        m.part.source = p.source;
        m.part.requests += p.requests;
        m.part.noCostData += p.noCostData;
        m.part.derived += p.derived;
        for (const auto& f : p.facts) m.tally[factKey(f.model, f.provider, f.fast, f.noDate)] += f.requests;
    }
    std::vector<Part> out;
    for (auto& [_, m] : merged) {
        m.part.facts = factList(m.tally);
        out.push_back(std::move(m.part));
    }
    return out;
}

std::vector<Part> liveParts(const std::vector<LiveResponse>& responses, const std::string& today) {
    std::vector<Part> out;
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
        const auto source = static_cast<SourceKind>(k);
        Part part;
        part.day = today;
        part.source = source;
        std::map<Key, std::int64_t> tally;
        bool any = false;
        for (const auto& r : responses) {
            if (r.source != source) continue;
            any = true;
            part.requests += r.requestCount;
            if (!r.costMicroUsd) tally[factKey(r.model, r.provider, r.fast, !hasDay(r.endMs))] += r.requestCount;
        }
        if (!any) continue;
        part.facts = factList(tally);
        out.push_back(std::move(part));
    }
    return out;
}

std::vector<Group> aggregate(const std::vector<Part>& parts, const pricing::PriceTable& table, const std::string& first, const std::string& last,
                             std::optional<SourceKind> source, const std::set<SourceKind>& excluded) {
    std::map<std::size_t, Group> groups;
    std::map<std::pair<std::size_t, Key>, Row> rows;
    for (const auto& part : parts) {
        if (part.day < first || part.day > last || excluded.contains(part.source) || (source && part.source != *source)) continue;
        auto& g = groups[sourceRank(part.source)];
        g.source = part.source;
        g.requests += part.requests;
        g.noCostData += part.noCostData;
        g.derived += part.derived;
        for (const auto& fact : part.facts) {
            const auto n = fact.requests;
            g.unpriced += n;
            const auto key = factKey(fact.model, fact.provider, fact.fast, fact.noDate);
            Diagnosis diag{Reason::NoDate, std::nullopt, std::nullopt};
            if (!std::get<3>(key)) {
                const auto found = diagnose(table, std::get<0>(key), std::get<1>(key), std::get<2>(key));
                if (!found) {
                    g.pricedNow += n;
                    continue;
                }
                diag = *found;
            }
            auto [it, created] = rows.try_emplace({sourceRank(part.source), key});
            auto& row = it->second;
            if (created) {
                row.source = part.source;
                row.reason = diag.reason;
                row.model = std::get<0>(key);
                row.provider = std::get<1>(key);
                row.fast = std::get<2>(key);
                row.noDate = std::get<3>(key);
                row.canonical = diag.canonical;
                row.rule = diag.rule;
                row.first = row.last = part.day;
            }
            row.requests += n;
            row.first = std::min(row.first, part.day);
            row.last = std::max(row.last, part.day);
        }
    }
    std::vector<Group> out;
    for (auto& [rank, g] : groups) {
        std::vector<std::pair<Key, Row>> mine;
        for (const auto& [k, row] : rows) {
            if (k.first == rank) mine.emplace_back(k.second, row);
        }
        std::sort(mine.begin(), mine.end(), [](const auto& a, const auto& b) {
            if (a.second.requests != b.second.requests) return a.second.requests > b.second.requests;
            return factLess(a.first, b.first);
        });
        for (auto& [_, row] : mine) g.rows.push_back(std::move(row));
        out.push_back(std::move(g));
    }
    std::stable_sort(out.begin(), out.end(), [](const Group& a, const Group& b) {
        if (a.unpriced != b.unpriced) return a.unpriced > b.unpriced;
        return sourceRank(a.source) < sourceRank(b.source);
    });
    return out;
}

std::string quoted(const std::optional<std::string>& value, bool redact) {
    if (!value) return "null";
    // Lone surrogates and invalid bytes become U+FFFD.
    std::vector<std::uint32_t> scalars;
    for (std::size_t i = 0; i < value->size();) scalars.push_back(text::nextCodePoint(*value, i));
    std::string clean;
    for (const auto cp : scalars) text::appendUtf8(clean, cp >= 0xD800 && cp <= 0xDFFF ? 0xFFFD : cp);
    if (redact && pathLike(clean)) {
        scalars.clear();
        for (const char c : std::string_view("<path>")) scalars.push_back(static_cast<unsigned char>(c));
    }
    bool cut = false;
    if (scalars.size() > kMaxScalars) {
        scalars.resize(kMaxScalars);
        cut = true;
    }
    std::string out = "\"";
    for (auto cp : scalars) {
        if (cp >= 0xD800 && cp <= 0xDFFF) cp = 0xFFFD;
        switch (cp) {
            case '\b': out += "\\b"; continue;
            case '\f': out += "\\f"; continue;
            case '\n': out += "\\n"; continue;
            case '\r': out += "\\r"; continue;
            case '\t': out += "\\t"; continue;
            case '"': out += "\\\""; continue;
            case '\\': out += "\\\\"; continue;
            default: break;
        }
        if (cp < 0x20 || (cp >= 0x7F && cp <= 0x9F) || cp == 0x2028 || cp == 0x2029) {
            char buffer[8];
            std::snprintf(buffer, sizeof buffer, "\\u%04x", cp);
            out += buffer;
        } else {
            text::appendUtf8(out, cp);
        }
    }
    if (cut) out += "…";
    return out + "\"";
}

std::string render(const std::vector<Group>& groups, const App& app, const pricing::PriceTable& table, const std::string& view,
                   const std::string& first, const std::string& last, std::optional<SourceKind> source, const std::set<SourceKind>& excluded) {
    std::string out = "Agent Speed unpriced diagnostics 1\n";
    out += "app version=" + app.version + " platform=" + app.platform + " os=" + app.os + "\n";
    out += "prices version=" + std::to_string(table.version()) + " updated=" + table.updated() + "\n";
    std::string excludedText;
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
        const auto kind = static_cast<SourceKind>(k);
        if (!excluded.contains(kind)) continue;
        if (!excludedText.empty()) excludedText += ",";
        excludedText += sourceId(kind);
    }
    out += "range view=" + view + " first=" + first + " last=" + last + " days=" + std::to_string(daysBetween(first, last)) +
           " source=" + (source ? std::string(sourceId(*source)) : std::string("all")) + " excluded=" + (excludedText.empty() ? "none" : excludedText) + "\n";
    std::int64_t requests = 0, unpricedTotal = 0, pricedNow = 0, noCostData = 0, derived = 0, rowCount = 0;
    for (const auto& g : groups) {
        out += "source id=" + std::string(sourceId(g.source)) + " requests=" + std::to_string(g.requests) + " unpriced=" + std::to_string(g.unpriced) +
               " pricedNow=" + std::to_string(g.pricedNow) + " noCostData=" + std::to_string(g.noCostData) + " derived=" + std::to_string(g.derived) +
               " rows=" + std::to_string(g.rows.size()) + "\n";
        for (const auto& r : g.rows) {
            out += "row source=" + std::string(sourceId(r.source)) + " reason=" + std::string(reasonId(r.reason)) + " requests=" + std::to_string(r.requests) +
                   " first=" + r.first + " last=" + r.last + " fast=" + (r.fast ? "true" : "false") + " model=" + quoted(r.model) +
                   " provider=" + quoted(r.provider) + " canonical=" + quoted(r.canonical) + " rule=" + quoted(r.rule, false) + "\n";
        }
        requests += g.requests;
        unpricedTotal += g.unpriced;
        pricedNow += g.pricedNow;
        noCostData += g.noCostData;
        derived += g.derived;
        rowCount += static_cast<std::int64_t>(g.rows.size());
    }
    out += "total requests=" + std::to_string(requests) + " unpriced=" + std::to_string(unpricedTotal) + " pricedNow=" + std::to_string(pricedNow) +
           " noCostData=" + std::to_string(noCostData) + " derived=" + std::to_string(derived) + " rows=" + std::to_string(rowCount) + "\n";
    return out;
}

}  // namespace agentspeed::unpriced
