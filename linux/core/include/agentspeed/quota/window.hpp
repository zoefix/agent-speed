#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/model.hpp"

// QUOTAS 2 and 9.1: the limit windows of one account, as the dashboard and the orb show them.
namespace agentspeed::quota {

// The order is the tie-break order of QUOTAS 2 (equal lengths).
enum class Kind : std::uint8_t { FiveHour, Other, SevenDay, ModelWeek, Month, Credits };

std::string_view kindId(Kind kind);
std::optional<Kind> kindFromId(std::string_view id);

constexpr std::int64_t kFiveHourMinutes = 300;
constexpr std::int64_t kWeekMinutes = 10080;
constexpr std::int64_t kDayMinutes = 1440;
constexpr std::int64_t kMonthMinutes = 43200;

// The length a kind has when the vendor does not state one: 300 for fiveHour, 10080 for sevenDay and modelWeek,
// 43200 for month; nullopt for other and credits.
std::optional<std::int64_t> defaultMinutes(Kind kind);

struct Window {
    Kind kind = Kind::Other;
    std::optional<std::string> name;  // a model or model group (`Fable`, `Gemini`, `Claude/GPT`)
    std::optional<std::int64_t> minutes;
    double usedPercent = 0;
    std::optional<std::int64_t> resetsAtMs;
    bool operator==(const Window&) const = default;
};

// A window of `kind` with the kind's default length.
Window makeWindow(Kind kind, double usedPercent, std::optional<std::int64_t> resetsAtMs,
                  std::optional<std::string> name = std::nullopt, std::optional<std::int64_t> minutes = std::nullopt);

struct Snapshot {
    SourceKind source = SourceKind::Codex;
    std::int64_t observedMs = 0;
    std::vector<Window> windows;
    bool operator==(const Snapshot&) const = default;
};

// QUOTAS 2 Order: unnamed windows by length (null minutes and credits last; equal lengths in kind order), then the
// named ones grouped by name in first-seen order, each group the same way. Stable.
std::vector<Window> ordered(const std::vector<Window>& windows);

// QUOTAS 2 and 9.1 labels: `5h`, `7d`, `<n>d` / `<n>h` / `<n>m`, the month and credits words, `<name> <base>`.
std::string label(const Window& window, std::string_view monthWord, std::string_view creditsWord);
// The English words (`mo`, `credits`): the status line helper and tests.
std::string englishLabel(const Window& window);

// The integer shown: rounded half up, clamped to 0…100.
int displayPercent(double usedPercent);

// One window as displayed at `nowMs` (QUOTAS 2 Reset passed).
struct Shown {
    Window window;
    int percent = 0;
    double bar = 0;  // clamp(usedPercent, 0, 100); 0 once passed
    std::optional<std::int64_t> resetsAtMs;  // null once passed
    bool passed = false;
};
std::vector<Shown> shown(const Snapshot& snapshot, std::int64_t nowMs);

// QUOTAS 7 Pace: the share of the window that has passed, 0…1; nullopt without a reset time, after it, or without a
// length.
std::optional<double> pace(const Window& window, std::int64_t nowMs);

}  // namespace agentspeed::quota
