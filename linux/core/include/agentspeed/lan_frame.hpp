#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/model.hpp"

namespace agentspeed::report {
struct Sample;
}

// The frame every transport carries (LAN 3): the tick's live values, its history and who sent it.
namespace agentspeed::lan {

inline constexpr std::size_t kNameLimit = 64;
inline constexpr std::size_t kFrameLimit = 16'384;
inline constexpr std::size_t kDatagramLimit = 1400;

// One set of live values as the tick computes them, before rounding (METRICS 11.2).
struct LiveValues {
    double outputTokS = 0, estimatedShare = 0;
    int generating = 0, tools = 0, active = 0;
};

// The tick's `live` values and each enabled source's: every agent of every host, the report's computations.
LiveValues overallValues(const report::Sample& sample);
std::map<SourceKind, LiveValues> sourceValues(const report::Sample& sample);

struct FrameInput {
    std::string instance, name, run;
    std::int64_t seq = 1;
    std::int64_t atMs = 0;  // the wall clock of the tick, Unix ms
    LiveValues live;
    std::map<SourceKind, LiveValues> sources;  // every enabled source
    std::vector<double> history;               // rounded samples, oldest first
};

// Rounded and clamped as METRICS 11.7.
struct Values {
    std::string phase;
    double outputTokS = 0, estimatedShare = 0;
    int active = 0, generating = 0, tools = 0;
    bool operator==(const Values&) const = default;
};

struct SourceEntry {
    SourceKind source = SourceKind::Codex;
    Values values;
    bool operator==(const SourceEntry&) const = default;
};

struct Frame {
    std::string instance, name, run;
    std::int64_t seq = 1, atMs = 0;
    Values values;
    std::vector<SourceEntry> sources;
    std::vector<double> history;

    bool estimated() const { return values.estimatedShare > 0; }
    // Compact JSON in the member order of LAN 3.1; `history` and `sources` can be left out (3.5).
    std::string json(bool withSources = true, bool withHistory = true) const;
    // At most 16 KiB: history, then sources are left out when needed.
    std::string wire() const;
    // Every member other than `seq` and `at` is equal (3.3).
    bool sameContent(const Frame& other) const;
};

Frame buildFrame(const FrameInput& input);
// Trimmed, cut to 64 code points and trimmed again; empty becomes `Agent Speed`.
std::string computerName(std::string_view raw);
std::string_view phaseName(int generating, int tools);
double roundTokS(double value);
double roundShare(double value);
int clampCount(int value);

// 3.4: one sample of the rounded output speed per second on the monotonic clock, the newest 60.
class History {
public:
    static constexpr std::int64_t kStepMs = 1000;
    static constexpr std::size_t kLength = 60;
    static constexpr std::int64_t kRestartMs = 60'000;

    // A tick at monotonic `nowMs` with the unrounded value; returns the samples after it.
    const std::vector<double>& tick(std::int64_t nowMs, double value);
    const std::vector<double>& samples() const { return samples_; }
    void reset();

private:
    std::optional<std::int64_t> s0_;
    std::int64_t next_ = 0, lastSampleMs_ = 0;
    std::vector<double> samples_;
};

// 5.4: `sha256=` and the lowercase hex HMAC-SHA256 of the body, keyed with the key's UTF-8 bytes.
std::string signature(std::string_view key, std::string_view body);

// 3.5, 5.2: the frame's bytes and, with a key, `\n` and the signature, within 1400 bytes.
struct Datagram {
    std::string bytes;
    bool historyOmitted = false, sourcesOmitted = false;
};
Datagram datagram(const Frame& frame, const std::optional<std::string>& key);

}  // namespace agentspeed::lan
