#pragma once

#include <QByteArray>
#include <QString>
#include <filesystem>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/character/behavior.hpp"
#include "orb/character/character_live.hpp"

namespace orb {

// A scripted session for the frame harness (`AGENT_SPEED_ORB_SESSION=<file.json>`): the agents' phases, waits, idle
// time, events, clicks and the cursor of a living character (CHARACTERS 5, Behavior), in the format of the `session` of
// spec/fixtures/characters/behavior.json, plus `speed`: `[[t, tok/s], …]` keys, linear between them (held after the
// last), and `shots`: the times to capture. Times are seconds from the session's start; the cursor is in her heights
// relative to her face, linear between its keys and absent outside them.
class CharacterSessionScript {
public:
    // The script in `json`, or nullopt when it is not a JSON object.
    static std::optional<CharacterSessionScript> parse(const QByteArray& json);
    // The script of the file at `path`, or nullopt when it is missing or unreadable.
    static std::optional<CharacterSessionScript> load(const QString& path);

    int hour = 12;
    std::optional<double> seconds;
    std::vector<double> shots;  // in order

    struct Phase {
        agentspeed::character::AgentActivity phase;
        double idleAt, since;
    };
    Phase phaseAt(double t) const;
    // How long the request has waited for its first output at `t`; nullopt when none waits.
    std::optional<double> waitAt(double t) const;
    // The idle time at `t` (0 outside an idle phase).
    double idleAt(double t) const;
    std::optional<std::pair<double, double>> cursorAt(double t) const;
    // The scripted speed at `t` (tok/s); nullopt when the script has none.
    std::optional<double> speedAt(double t) const;
    // Feeds the step that ends at `t` (of `dt`) to her live inputs on the clock `now`; true when a `newPeak` fires in it.
    bool apply(double t, double dt, double now, CharacterLive& live) const;

private:
    struct Start {
        double start;
        agentspeed::character::AgentActivity phase;
        double idleAt;
    };
    std::vector<Start> phases_;
    std::vector<std::pair<double, double>> requests_, idleJumps_, speed_;
    std::vector<std::pair<double, std::string>> events_;
    std::vector<double> clicks_;
    struct Key {
        double t, x, y;
    };
    std::vector<Key> cursor_;
};

// Development aid (`AGENT_SPEED_ORB_SESSION=<script.json> agent-speed --orb-session=<folder>`): the living pack at
// `AGENT_SPEED_ORB_PACK` on a 148 pt dark character orb, drawn offscreen with OpenGL and stepped every 1/60 s through the
// script with its phases, waits, idle time, events, clicks and cursor as her live inputs and its `speed` as the orb's
// value; frames at the script's `shots` (else every 0.5 s) into `<folder>/frame-T.png`. `AGENT_SPEED_ORB_TRACE=1`
// writes her mood, pose, face, gaze, mouth and action at every shot to `<folder>/trace.txt`. Returns the exit code.
int runCharacterSession(const QString& scriptPath, const std::filesystem::path& pack, const QString& folder);

}  // namespace orb
