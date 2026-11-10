#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/window.hpp"

// The Claude CLI's quota file `<data>/quota/claude-cli.json` (QUOTAS 5.5): one value per window as Claude Code last
// passed it to the status line, and when a value last changed. Shared by the helper (merge, line) and the app (reader).
//
//   {"version": 1, "receivedMs": 1794338685000,
//    "windows": {"five_hour": {"used_percentage": 6, "resets_at": 1794354000},
//                "seven_day": {"used_percentage": 86, "resets_at": 1794448800}}}
namespace agentspeed::quota::cli {

constexpr std::size_t kInputLimit = 1 << 20;
// Two values whose reset times are this close belong to the same window.
constexpr double kSameWindowMs = 60'000;

// One stored window: the numbers as Claude Code wrote them (their JSON text is kept and written back as is).
struct Value {
    double usedPercentage = 0;
    double resetsAt = 0;  // epoch seconds
    std::string usedText;
    std::string resetsText;
    // `resets_at × 1000`, truncated and saturated.
    std::int64_t resetsAtMs() const;
    bool operator==(const Value& other) const { return usedPercentage == other.usedPercentage && resetsAt == other.resetsAt; }
};

struct Stored {
    std::optional<std::int64_t> receivedMs;
    std::optional<Value> fiveHour;
    std::optional<Value> sevenDay;
    bool operator==(const Stored&) const = default;
};

// An object with a numeric (finite, not boolean) `used_percentage` and `resets_at`.
std::optional<Value> windowValue(const json::Value& object);

// The file's contents; anything that is not a version-1 object reads as an empty file.
Stored parseFile(std::string_view bytes);
std::string fileText(const Stored& stored);

// QUOTAS 5.5 step 2: merges a status line's `rate_limits`. A reset more than 60 s later replaces the stored value;
// within 60 s it is the same window and the higher percentage wins; an earlier one is ignored; a window missing from the
// input is kept. Returns whether a value changed (`receivedMs` is then `nowMs`).
bool merge(Stored& stored, const json::Value& rateLimits, std::int64_t nowMs);
// The same for a whole status line input: nothing happens unless it is a JSON object whose `rate_limits` is an object.
bool mergeInput(Stored& stored, std::string_view input, std::int64_t nowMs);

// `5h 6% · 7d 86%`: the stored windows that have not reset at `nowMs`, in QUOTAS 2's order and labels; empty when none.
std::string line(const Stored& stored, std::int64_t nowMs);

// The reader's snapshot (QUOTAS 5.5 Reader): `fiveHour` / `sevenDay`, `usedPercent = used_percentage`,
// `resetsAtMs = resets_at × 1000`, `observedMs = receivedMs`; nullopt without `receivedMs`.
std::optional<Snapshot> snapshotOf(const Stored& stored);

// The sidecar's previous status line command: `statusLine` is an object with `type == "command"` and a non-empty
// string `command`; else nullopt.
std::optional<std::string> previousCommand(std::string_view sidecarBytes);

// The app's side of the quota file: refreshed every 5 s while the bridge is on; the file is read only when its size or
// modification time changed. Not thread-safe.
class FileReader {
public:
    static constexpr std::int64_t kCheckIntervalMs = 5'000;

    explicit FileReader(std::string quotaFile) : file_(std::move(quotaFile)) {}

    // `enabled` false (Claude Code disabled in Data sources, or the switch off): no stat, no read, no snapshot.
    // Returns true when the snapshot changed.
    bool refresh(bool enabled);
    const std::optional<Snapshot>& snapshot() const { return snapshot_; }
    bool hasFile() const { return stamp_.has_value(); }
    const std::string& file() const { return file_; }

private:
    std::string file_;
    std::optional<files::Stamp> stamp_;
    std::optional<Snapshot> snapshot_;
};

}  // namespace agentspeed::quota::cli
