#pragma once

#include <cstdint>
#include <deque>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <variant>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/monitor.hpp"

// The relay's side of the protocol (REMOTE 5): the app's commands decoded defensively, and every line the relay
// writes, bounded and validated as the app checks them (5.6), so a line the app would reject is never sent.
namespace agentspeed::relay {

inline constexpr int kProtocol = 1;
inline constexpr std::size_t kMaxInputLine = 4u << 20;
inline constexpr std::size_t kMaxOutputLine = 1u << 20;
// 2000-01-01 and 2100-01-01 (UTC): times outside make a line invalid (5.6).
inline constexpr std::int64_t kMinTimeMs = 946'684'800'000;
inline constexpr std::int64_t kMaxTimeMs = 4'102'444'800'000;

// REMOTE 11.1: the sources whose quota switch is on, the master switch, Grok's live request and the host's bridge.
struct QuotaConfig {
    std::set<SourceKind> sources;
    bool network = false;
    bool grokLive = false;
    bool claudeBridge = false;
    bool operator==(const QuotaConfig&) const = default;
};

struct StartCommand {
    std::int64_t protocol = 0;
    std::int64_t todayStartMs = 0;
    std::set<SourceKind> sources;
    std::string zoneId, zoneTzif;
    std::string prices;  // the table's JSON text
    // Absent (an older app) or not a valid config: the relay reads no quota and leaves the bridge alone.
    std::optional<QuotaConfig> quota;
};
struct PingCommand {
    std::int64_t id = 0;
};
struct TodayCommand {
    std::int64_t todayStartMs = 0;
};
struct PricesCommand {
    std::string table;
};
struct ZoneCommand {
    std::string id, tzif;
};
struct HistoryCommand {
    std::map<std::string, std::string> digests;
};
struct WatchCommand {
    bool on = false;
};
struct QuotaCommand {
    QuotaConfig config;
};
using Command =
    std::variant<StartCommand, PingCommand, TodayCommand, PricesCommand, ZoneCommand, HistoryCommand, WatchCommand, QuotaCommand>;

// Nullopt for a line that is not a valid command: not a JSON object, an unknown `t`, a missing field or a wrong type.
std::optional<Command> decodeCommand(std::string_view line);

// The app's bounds (5.4, 5.6): valid UTF-8, no control character, at most `max` bytes.
std::optional<std::string> boundedText(const std::optional<std::string>& text, std::size_t max);
bool validTime(std::int64_t ms);

std::string helloLine(std::string_view version, std::string_view arch, std::int64_t nowMs);
std::string progressLine(double value);
std::string readyLine();
std::string clockLine(std::int64_t id, std::int64_t nowMs);
std::string errorLine(std::string_view code, std::string_view detail);

// An agent's complete state (5.4); empty when its id cannot be sent.
struct AgentView {
    const AgentInfo* info = nullptr;
    std::optional<std::string> cwd;
    const std::deque<work::Action>* actions = nullptr;
};
std::string agentLine(const AgentView& agent);
std::string agentRemovedLine(std::string_view id);
// Links whose ids cannot be sent are left out.
std::string parentsLine(const std::vector<std::pair<std::string, std::string>>& links);
// Empty when the response cannot be sent (its key, agent id or end time).
std::string responseLine(const ParsedResponse& response, bool completion, const std::optional<std::string>& project);
std::string toolLine(std::string_view agentId, SourceKind source, const work::Action& action);

struct StatusView {
    std::vector<SourceState> sources;
    Warnings warnings;
};
std::string statusLine(const StatusView& status);

std::string historyDayLine(std::string_view day, SourceKind source, std::string_view aggregate, std::string_view digest);
std::string historyPerfLine(std::string_view day, SourceKind source, std::string_view model, std::string_view record);
std::string historyDoneLine(std::int64_t days, std::int64_t analyzed);
std::string historyProgressLine(double value);

}  // namespace agentspeed::relay
