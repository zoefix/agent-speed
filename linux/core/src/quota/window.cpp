#include "agentspeed/quota/window.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

namespace agentspeed::quota {

namespace {

constexpr std::array<std::string_view, 6> kKindIds = {"fiveHour", "other", "sevenDay", "modelWeek", "month", "credits"};

// The sort key of QUOTAS 2: length first (credits and unknown lengths last), then the kind order.
std::pair<double, int> lengthKey(const Window& w) {
    const double length = (!w.minutes || w.kind == Kind::Credits) ? std::numeric_limits<double>::infinity()
                                                                   : static_cast<double>(*w.minutes);
    return {length, static_cast<int>(w.kind)};
}

void sortByLength(std::vector<Window>& windows) {
    std::stable_sort(windows.begin(), windows.end(), [](const Window& a, const Window& b) { return lengthKey(a) < lengthKey(b); });
}

bool hasName(const Window& w) { return w.name && !w.name->empty(); }

std::string baseLabel(const Window& w, std::string_view monthWord, std::string_view creditsWord) {
    switch (w.kind) {
        case Kind::FiveHour: return "5h";
        case Kind::SevenDay:
        case Kind::ModelWeek: return "7d";
        case Kind::Month: return std::string(monthWord);
        case Kind::Credits: return std::string(creditsWord);
        case Kind::Other: break;
    }
    const std::int64_t minutes = w.minutes.value_or(0);
    if (minutes > 0 && minutes % kDayMinutes == 0) return std::to_string(minutes / kDayMinutes) + "d";
    if (minutes > 0 && minutes % 60 == 0) return std::to_string(minutes / 60) + "h";
    return std::to_string(minutes) + "m";
}

}  // namespace

std::string_view kindId(Kind kind) { return kKindIds[static_cast<std::size_t>(kind)]; }

std::optional<Kind> kindFromId(std::string_view id) {
    for (std::size_t i = 0; i < kKindIds.size(); ++i) {
        if (kKindIds[i] == id) return static_cast<Kind>(i);
    }
    return std::nullopt;
}

std::optional<std::int64_t> defaultMinutes(Kind kind) {
    switch (kind) {
        case Kind::FiveHour: return kFiveHourMinutes;
        case Kind::SevenDay:
        case Kind::ModelWeek: return kWeekMinutes;
        case Kind::Month: return kMonthMinutes;
        case Kind::Other:
        case Kind::Credits: break;
    }
    return std::nullopt;
}

Window makeWindow(Kind kind, double usedPercent, std::optional<std::int64_t> resetsAtMs, std::optional<std::string> name,
                  std::optional<std::int64_t> minutes) {
    Window w;
    w.kind = kind;
    w.name = std::move(name);
    w.minutes = minutes ? minutes : defaultMinutes(kind);
    w.usedPercent = usedPercent;
    w.resetsAtMs = resetsAtMs;
    return w;
}

std::vector<Window> ordered(const std::vector<Window>& windows) {
    std::vector<Window> out;
    std::vector<std::string> names;
    for (const auto& w : windows) {
        if (!hasName(w)) {
            out.push_back(w);
        } else if (std::find(names.begin(), names.end(), *w.name) == names.end()) {
            names.push_back(*w.name);
        }
    }
    sortByLength(out);
    for (const auto& name : names) {
        std::vector<Window> group;
        for (const auto& w : windows) {
            if (hasName(w) && *w.name == name) group.push_back(w);
        }
        sortByLength(group);
        out.insert(out.end(), group.begin(), group.end());
    }
    return out;
}

std::string label(const Window& window, std::string_view monthWord, std::string_view creditsWord) {
    auto base = baseLabel(window, monthWord, creditsWord);
    return hasName(window) ? *window.name + " " + base : base;
}

std::string englishLabel(const Window& window) { return label(window, "mo", "credits"); }

int displayPercent(double usedPercent) {
    if (!std::isfinite(usedPercent)) return 0;
    const double rounded = std::floor(usedPercent + 0.5);
    return static_cast<int>(std::clamp(rounded, 0.0, 100.0));
}

std::vector<Shown> shown(const Snapshot& snapshot, std::int64_t nowMs) {
    std::vector<Shown> out;
    for (const auto& w : ordered(snapshot.windows)) {
        Shown s;
        s.window = w;
        s.passed = w.resetsAtMs && nowMs >= *w.resetsAtMs;
        s.percent = s.passed ? 0 : displayPercent(w.usedPercent);
        s.bar = s.passed || !std::isfinite(w.usedPercent) ? 0.0 : std::clamp(w.usedPercent, 0.0, 100.0);
        s.resetsAtMs = s.passed ? std::nullopt : w.resetsAtMs;
        out.push_back(std::move(s));
    }
    return out;
}

std::optional<double> pace(const Window& window, std::int64_t nowMs) {
    if (!window.resetsAtMs || nowMs >= *window.resetsAtMs || !window.minutes || *window.minutes <= 0) return std::nullopt;
    const double share = 1.0 - static_cast<double>(*window.resetsAtMs - nowMs) / (static_cast<double>(*window.minutes) * 60000.0);
    return std::clamp(share, 0.0, 1.0);
}

}  // namespace agentspeed::quota
