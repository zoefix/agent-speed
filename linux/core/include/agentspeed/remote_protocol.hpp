#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

#include "agentspeed/model.hpp"
#include "agentspeed/remote_messages.hpp"

// The app's side of the relay protocol (REMOTE 5): relay lines decoded with every rule of 5.6, app lines encoded.
namespace agentspeed::remote {

// Why a relay line is invalid; only the field name is kept, never the value.
struct LineError {
    enum class Kind : std::uint8_t {
        NotJson, NotObject, Oversized, Missing, WrongType, TooLong, BadCharacter, OutOfRange, BadTime, TooMany
    };
    Kind kind = Kind::NotJson;
    std::string field;
    bool operator==(const LineError&) const = default;
};

// 2000-01-01T00:00:00Z … 2100-12-31T23:59:59.999Z.
inline constexpr std::int64_t kEarliestMs = 946'684'800'000;
inline constexpr std::int64_t kLatestMs = 4'133'980'799'999;
inline constexpr std::int64_t kIntegerLimit = std::int64_t{1} << 53;
bool isTime(std::int64_t ms);
// `YYYY-MM-DD` of a real calendar day from 2000-01-01 to 2100-12-31 (REMOTE 8.3 Bounds).
bool isDay(std::string_view text);

// Decodes one relay line (REMOTE 5.2). Unknown types are ignored; anything else that does not fit is invalid.
class LineDecoder {
public:
    struct Ignored {};
    using Outcome = std::variant<RelayMessage, Ignored, LineError>;

    explicit LineDecoder(std::string hostId);
    Outcome decode(std::string_view line) const;
    const std::string& hostId() const { return hostId_; }

private:
    std::string hostId_, prefix_;
};

// The stored texts of a remote day (`remote_days.data`) and record (`remote_perf.data`), with the line's validation.
std::optional<history::DayAggregate> decodeAggregateText(std::string_view json);
std::optional<perf::Record> decodePerfRecordText(std::string_view json);

// Splits the relay's stdout into lines of at most 1 MiB; a longer line is dropped and counted.
class LineFramer {
public:
    LineFramer() = default;
    explicit LineFramer(std::size_t maxLineBytes) : max_(maxLineBytes) {}
    // The complete lines of the chunk, in order; `oversized` counts the lines it dropped.
    std::vector<std::string> consume(std::string_view chunk, int& oversized);

private:
    std::size_t max_ = limits::kRelayLineBytes;
    std::string partial_;
    bool skipping_ = false;
};

// The app's time zone as `start.zone` and `zone` send it (REMOTE 5.3, 8.1): the IANA id and its TZif bytes.
struct Zone {
    std::string id;
    std::string tzif;
    bool operator==(const Zone&) const = default;
    static bool validId(std::string_view id);
    static bool isTzif(std::string_view data);
};
// The system zone (TZ, then /etc/localtime and /etc/timezone); a zone without readable TZif data is sent as UTC.
// `preferredId` (QTimeZone's id) is used when its zoneinfo file can be read.
Zone currentZone(const std::optional<std::string>& preferredId = std::nullopt);
// The minimal version 1 TZif file for UTC.
std::string utcTzif();

// The price table as a `start.prices` / `prices.table` value: a JSON object validated by the parser and written
// compactly (insignificant whitespace removed, literals kept), so it fits one line.
std::optional<std::string> compactPriceTable(std::string_view json);

// The app → relay lines of REMOTE 5.3, each a compact JSON object ending in a newline (nullopt beyond 4 MiB).
namespace encode {
// `quota` (REMOTE 11.1) is left out when nullopt.
std::optional<std::string> start(std::int64_t todayStartMs, const std::set<SourceKind>& sources, const Zone& zone,
                                 std::string_view compactPrices, const std::optional<HostQuotaConfig>& quota = std::nullopt);
// `{"t":"quota","config":{...}}`: the host's quota config changed (REMOTE 11.1).
std::optional<std::string> quota(const HostQuotaConfig& config);
std::optional<std::string> ping(std::int64_t id);
std::optional<std::string> today(std::int64_t todayStartMs);
std::optional<std::string> prices(std::string_view compactPrices);
std::optional<std::string> zone(const Zone& zone);
// `digests`: "<day>/<source>" → digest, in key order.
std::optional<std::string> history(const std::map<std::string, std::string>& digests);
std::optional<std::string> watch(bool on);
std::string base64(std::string_view bytes);
}  // namespace encode

// The digest of REMOTE 5.4: the first 16 hex digits of SHA-256 over the aggregate's text followed by the records'
// texts in model order (UTF-8 bytes).
std::string historyDigest(std::string_view aggregateJson, std::vector<std::pair<std::string, std::string>> perf);

}  // namespace agentspeed::remote
