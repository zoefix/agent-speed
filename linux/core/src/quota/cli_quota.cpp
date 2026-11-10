#include "agentspeed/quota/cli_quota.hpp"

#include <cmath>

namespace agentspeed::quota::cli {

namespace {

constexpr double kInt64Edge = 9223372036854775808.0;  // 2^63

// Python's int() of a finite double, saturated.
std::int64_t truncatedSaturated(double value) {
    if (value >= kInt64Edge) return INT64_MAX;
    if (value < -kInt64Edge) return INT64_MIN;
    return static_cast<std::int64_t>(std::trunc(value));
}

void appendValue(std::string& out, std::string_view field, const Value& value) {
    out += '"';
    out += field;
    out += "\": {\"used_percentage\": ";
    out += value.usedText;
    out += ", \"resets_at\": ";
    out += value.resetsText;
    out += '}';
}

// The stored value after one new value (QUOTAS 5.5 step 2); true when it changed.
bool pick(std::optional<Value>& stored, const std::optional<Value>& next) {
    if (!next) return false;
    if (!stored || next->resetsAt * 1000 - stored->resetsAt * 1000 > kSameWindowMs) {
        stored = next;
        return true;
    }
    if (std::fabs(next->resetsAt - stored->resetsAt) * 1000 <= kSameWindowMs && next->usedPercentage > stored->usedPercentage) {
        stored = next;
        return true;
    }
    return false;
}

}  // namespace

std::int64_t Value::resetsAtMs() const { return truncatedSaturated(resetsAt * 1000); }

std::optional<Value> windowValue(const json::Value& object) {
    if (!object.isObject()) return std::nullopt;
    const auto used = object["used_percentage"];
    const auto resets = object["resets_at"];
    const auto usedNumber = used.finiteNumber();
    const auto resetsNumber = resets.finiteNumber();
    if (!usedNumber || !resetsNumber) return std::nullopt;
    return Value{*usedNumber, *resetsNumber, std::string(used.raw()), std::string(resets.raw())};
}

Stored parseFile(std::string_view bytes) {
    json::Document doc;
    if (!doc.parse(bytes)) return {};
    const auto root = doc.root();
    if (!root.isObject() || root["version"].finiteNumber() != 1.0) return {};
    Stored stored;
    if (const auto received = root["receivedMs"]; received.isNumber()) stored.receivedMs = received.truncated();
    const auto windows = root["windows"];
    if (windows.isObject()) {
        stored.fiveHour = windowValue(windows["five_hour"]);
        stored.sevenDay = windowValue(windows["seven_day"]);
    }
    return stored;
}

std::string fileText(const Stored& stored) {
    std::string out = "{\"version\": 1, \"receivedMs\": ";
    out += stored.receivedMs ? std::to_string(*stored.receivedMs) : "null";
    out += ", \"windows\": {";
    if (stored.fiveHour) appendValue(out, "five_hour", *stored.fiveHour);
    if (stored.sevenDay) {
        if (stored.fiveHour) out += ", ";
        appendValue(out, "seven_day", *stored.sevenDay);
    }
    out += "}}\n";
    return out;
}

bool merge(Stored& stored, const json::Value& rateLimits, std::int64_t nowMs) {
    if (!rateLimits.isObject()) return false;
    const bool five = pick(stored.fiveHour, windowValue(rateLimits["five_hour"]));
    const bool seven = pick(stored.sevenDay, windowValue(rateLimits["seven_day"]));
    if (five || seven) stored.receivedMs = nowMs;
    return five || seven;
}

bool mergeInput(Stored& stored, std::string_view input, std::int64_t nowMs) {
    if (input.size() > kInputLimit) return false;
    json::Document doc;
    if (!doc.parse(input) || !doc.root().isObject()) return false;
    return merge(stored, doc.root()["rate_limits"], nowMs);
}

std::optional<Snapshot> snapshotOf(const Stored& stored) {
    if (!stored.receivedMs) return std::nullopt;
    Snapshot snapshot;
    snapshot.source = SourceKind::ClaudeCode;
    snapshot.observedMs = *stored.receivedMs;
    if (stored.fiveHour) snapshot.windows.push_back(makeWindow(Kind::FiveHour, stored.fiveHour->usedPercentage, stored.fiveHour->resetsAtMs()));
    if (stored.sevenDay) snapshot.windows.push_back(makeWindow(Kind::SevenDay, stored.sevenDay->usedPercentage, stored.sevenDay->resetsAtMs()));
    return snapshot;
}

std::string line(const Stored& stored, std::int64_t nowMs) {
    Snapshot snapshot;
    snapshot.source = SourceKind::ClaudeCode;
    if (stored.fiveHour) snapshot.windows.push_back(makeWindow(Kind::FiveHour, stored.fiveHour->usedPercentage, stored.fiveHour->resetsAtMs()));
    if (stored.sevenDay) snapshot.windows.push_back(makeWindow(Kind::SevenDay, stored.sevenDay->usedPercentage, stored.sevenDay->resetsAtMs()));
    std::string out;
    for (const auto& w : shown(snapshot, nowMs)) {
        if (w.passed) continue;
        if (!out.empty()) out += " \xC2\xB7 ";
        out += englishLabel(w.window) + " " + std::to_string(w.percent) + "%";
    }
    return out;
}

std::optional<std::string> previousCommand(std::string_view sidecarBytes) {
    json::Document doc;
    if (!doc.parse(sidecarBytes) || !doc.root().isObject()) return std::nullopt;
    const auto statusLine = doc.root()["statusLine"];
    if (!statusLine.isObject() || !statusLine["type"].equals("command")) return std::nullopt;
    auto command = statusLine["command"].string();
    if (!command || command->empty()) return std::nullopt;
    return command;
}

bool FileReader::refresh(bool enabled) {
    if (!enabled) {
        const bool had = snapshot_.has_value();
        stamp_.reset();
        snapshot_.reset();
        return had;
    }
    const auto stamp = files::stampOf(file_, false);
    if (!stamp) {
        const bool had = snapshot_.has_value();
        stamp_.reset();
        snapshot_.reset();
        return had;
    }
    if (stamp_ && *stamp_ == *stamp) return false;
    auto read = files::readFile(file_, kInputLimit, false);
    std::optional<Snapshot> next;
    if (read.status == files::ReadStatus::Ok) {
        next = snapshotOf(parseFile(read.bytes));
        stamp_ = read.stamp;
    } else if (read.status == files::ReadStatus::TooLarge) {
        stamp_ = read.stamp;
    } else {
        stamp_.reset();
    }
    const bool changed = next != snapshot_;
    snapshot_ = std::move(next);
    return changed;
}

}  // namespace agentspeed::quota::cli
