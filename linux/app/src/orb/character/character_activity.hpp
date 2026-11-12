#pragma once

#include <QVariantList>
#include <QtGlobal>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"

namespace orb {

// What the engine says of one agent of a `character` orb's set for her moods (CHARACTERS 5, Behavior, Inputs).
struct AgentFacts {
    std::string id;
    agentspeed::Phase phase = agentspeed::Phase::Idle;
    std::optional<qint64> inFlightSinceMs;
    bool outputSinceInFlight = false;
    std::optional<qint64> lastResponseEndMs;
    std::optional<qint64> repeatFailureMs;
    bool looping = false;

    // The rows of an orb's `agents` (MonitorWorker's agent maps).
    static std::vector<AgentFacts> fromAgents(const QVariantList& agents);
};

// What the agents of a `character` orb are doing (CHARACTERS 5, Behavior, Inputs): the phase of the orb's set, when the
// request still waiting for its first output started, when the newest response ended, and `trouble` events. Updated
// every engine tick from the orb's agents (METRICS 6.8, set A). Times are wall-clock milliseconds.
class CharacterActivity {
public:
    static constexpr std::size_t kRemembered = 512;

    // `startMs`: the orb's first reading, the idle time's origin while no response of the set ended.
    explicit CharacterActivity(qint64 startMs) : idleSinceMs_(startMs) {}

    // Takes one engine tick's agents of the set; true when a `trouble` event fired: an agent starts Looping
    // (METRICS 6.10), or a tool result read live fails right after the previous result of that agent failed too. The
    // first tick only takes what it sees.
    bool update(const std::vector<AgentFacts>& agents);
    // A response of the set ended (a newer end on a tick).
    void noteResponseEnd(qint64 endMs) {
        if (endMs > idleSinceMs_) idleSinceMs_ = endMs;
    }

    // `generating` when an agent of the set is generating, else `tools` when one waits for a tool, else `idle` (the
    // agents' phases already follow the 300 s staleness of METRICS 6.7).
    agentspeed::Phase phase() const { return phase_; }
    // While generating and none of the generating agents has output since its in-flight request started: the earliest
    // such start.
    std::optional<qint64> waitingSinceMs() const { return waitingSinceMs_; }
    // The newest end of a response of the set; the orb's first reading while there is none.
    qint64 idleSinceMs() const { return idleSinceMs_; }
    int troubles() const { return troubles_; }

private:
    agentspeed::Phase phase_ = agentspeed::Phase::Idle;
    std::optional<qint64> waitingSinceMs_;
    qint64 idleSinceMs_;
    int troubles_ = 0;
    bool primed_ = false;
    std::map<std::string, bool> looping_;
    std::map<std::string, qint64> failures_;
};

// A single click (DESIGN 9, Orb, Switching 1, and CHARACTERS 5, Behavior, `click`): a press and release within 4 pt and
// 0.5 s, fired once the system double-click time passed without a second click; a double-click and the end of a drag
// never fire. Times in seconds, points in screen points.
class CharacterClickRule {
public:
    static constexpr double kMaxMovement = 4;
    static constexpr double kMaxPress = 0.5;

    struct Pending {
        double fireAt, x, y;
    };

    explicit CharacterClickRule(double interval) : doubleClickInterval(interval) {}

    double doubleClickInterval;

    // A press; `armed` when it may start a click (on a living character's orb, not click-through).
    void down(double time, double x, double y, int clickCount, bool armed = true);
    // The release; `moved` when the press turned into a drag. Returns the delay until the click fires, or nullopt.
    std::optional<double> up(double time, double x, double y, bool moved);
    // The pending click once its time came, taken once.
    std::optional<std::pair<double, double>> take(double now);
    void cancel() {
        press_.reset();
        pending_.reset();
    }
    const std::optional<Pending>& pending() const { return pending_; }

private:
    struct Press {
        double time, x, y;
    };
    std::optional<Press> press_;
    std::optional<Pending> pending_;
};

}  // namespace orb
