#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/pricing.hpp"

// Unpriced diagnostics (METRICS 9.10, spec/tools/unpriced.py): why responses have no price, grouped into rows, and the
// text the user copies. Nothing of it is reported.
namespace agentspeed::unpriced {

enum class Reason : std::uint8_t { NoModel, NotInTable, NoFastPrice, Unpriceable, NoDate };
std::string_view reasonId(Reason reason);

struct Diagnosis {
    Reason reason = Reason::NotInTable;
    std::optional<std::string> canonical;
    std::optional<std::string> rule;  // the table's `unknown` entry's reason (unpriceable only)
};
// Why a response with these facts has no price under `table`; nullopt when it is priced or free now.
std::optional<Diagnosis> diagnose(const pricing::PriceTable& table, const std::optional<std::string>& model,
                                  const std::optional<std::string>& provider, bool fast);

// An unpriced fact: (trimmed model, provider, fast, noDate) and its requests.
struct Fact {
    std::string model;
    std::optional<std::string> provider;
    bool fast = false, noDate = false;
    std::int64_t requests = 0;
    bool operator==(const Fact&) const = default;
};

// The facts, counters and day of one (day, source).
struct Part {
    std::string day;
    SourceKind source = SourceKind::Codex;
    std::int64_t requests = 0, noCostData = 0, derived = 0;
    std::vector<Fact> facts;
};

// One stored contribution as *Reading* takes it: the file's version, its requests and unpriced requests (nullopt: no
// cost data), the unpriced requests per raw model and the stored facts with the version that wrote them.
struct Stored {
    int fileVersion = 0;
    std::int64_t requests = 0;
    std::optional<std::int64_t> unpriced;
    std::map<std::string, std::int64_t> models;
    struct Side {
        std::string model;
        std::optional<std::string> provider;
        bool fast = false;
        std::int64_t requests = 0;
        int version = 0;
    };
    std::vector<Side> side;
};
// METRICS 9.10 Reading: the facts of a stored contribution (day and source left to the caller).
Part readPart(const Stored& stored);
// Parts of the same (day, source) summed, by day then source order.
std::vector<Part> mergeParts(const std::vector<Part>& parts);

// METRICS 9.10 Live: today's responses of the engine with their cost (nullopt: unknown) → one part per source.
struct LiveResponse {
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> model, provider;
    bool fast = false;
    std::int64_t endMs = 0, requestCount = 1;
    std::optional<std::int64_t> costMicroUsd;
};
std::vector<Part> liveParts(const std::vector<LiveResponse>& responses, const std::string& today);

struct Row {
    SourceKind source = SourceKind::Codex;
    Reason reason = Reason::NotInTable;
    std::string model;
    std::optional<std::string> provider;
    bool fast = false, noDate = false;
    std::optional<std::string> canonical, rule;
    std::int64_t requests = 0;
    std::string first, last;
};
struct Group {
    SourceKind source = SourceKind::Codex;
    std::int64_t requests = 0, unpriced = 0, pricedNow = 0, noCostData = 0, derived = 0;
    std::vector<Row> rows;
};
// METRICS 9.10 Rows: the source groups of the parts in [first, last], for one source or all, without `excluded`.
std::vector<Group> aggregate(const std::vector<Part>& parts, const pricing::PriceTable& table, const std::string& first, const std::string& last,
                             std::optional<SourceKind> source = std::nullopt, const std::set<SourceKind>& excluded = {});

// METRICS 9.10 Text: a text value (null, redacted when path-like unless `redact` is false, cut at 120 scalars, escaped).
std::string quoted(const std::optional<std::string>& text, bool redact = true);
struct App {
    std::string version, platform, os;
};
std::string render(const std::vector<Group>& groups, const App& app, const pricing::PriceTable& table, const std::string& view,
                   const std::string& first, const std::string& last, std::optional<SourceKind> source = std::nullopt,
                   const std::set<SourceKind>& excluded = {});

}  // namespace agentspeed::unpriced
