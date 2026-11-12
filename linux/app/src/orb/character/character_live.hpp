#pragma once

#include <QPointF>
#include <QtGlobal>
#include <functional>
#include <optional>
#include <utility>

#include "agentspeed/character/behavior.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "orb/character/character_activity.hpp"

namespace orb {

// What a living character reacts to, shared by her orb window (the agents, the pointer and single clicks) and her
// style, which reads it every frame (CHARACTERS 5, Behavior, Inputs). Plain storage: writing it never redraws. Times are
// seconds on the window's clock; points are window points (y down).
struct CharacterLive {
    agentspeed::character::AgentActivity phase = agentspeed::character::AgentActivity::Idle;
    // The request still waiting for its first output started; nullopt when none waits.
    std::optional<double> waitingSince;
    // The newest response of the orb's set ended (the orb's first reading while there is none).
    double idleSince = 0;
    int hour = 12;
    int peaks = 0;
    int troubles = 0;
    // The orb's new peaks cheer her only while peaks are celebrated (Settings, Effects).
    bool celebratesPeaks = true;

    // The pointer in window points, or nullopt while it may not be read (the window answers, only when asked).
    std::function<std::optional<QPointF>()> pointer;
    bool dragging = false;
    int clicks = 0;
    // Where the last single click was pressed.
    QPointF clickPoint;

    // The frame harness scripts the cursor (in her heights from her face, x right, y down) instead of the pointer.
    bool scripted = false;
    std::optional<std::pair<double, double>> scriptedCursor;

    void notePeak() { ++peaks; }
    void noteTrouble() { ++troubles; }
    void click(QPointF point) {
        clickPoint = point;
        ++clicks;
    }
    // Takes one engine tick of the orb's agents, `now` on the window's clock being `wallNowMs`.
    void update(const CharacterActivity& activity, double now, qint64 wallNowMs);

    // A wall-clock instant on the window's clock, `now` being `wallNowMs`.
    static double clockOf(qint64 instantMs, double now, qint64 wallNowMs) { return now - static_cast<double>(wallNowMs - instantMs) / 1000.0; }
    // A window point on her canvas, with the hover scale around the canvas center undone.
    static QPointF canvasPoint(QPointF point, double canvasSide, double contentScale);
    // The pointer relative to her face in her heights (screen axes, y down); nullopt beyond her look range.
    static std::optional<std::pair<double, double>> cursor(QPointF point, std::pair<double, double> face, double height);
    // Whether a canvas point is inside her hit capsule (DESIGN 2a.6), moved with her side steps.
    static bool overCapsule(QPointF point, QPointF center, double diameter, double bodyDx);
    // Whether a count moved since the last frame; the first frame only takes it, so events from before her first frame
    // never fire.
    static bool fresh(int count, std::optional<int>& seen) {
        const bool moved = seen && *seen != count;
        seen = count;
        return moved;
    }
};

// Where she stands this frame on her canvas (points, y down), for her inputs.
struct LiveGeometry {
    QPointF center;            // the canvas center
    double diameter = 0;       // the footprint D
    double canvasSide = 0;
    double contentScale = 1;   // the hover scale the scene is drawn at around the center
    std::pair<double, double> face;  // her face center (Performer::faceCenter)
    double height = 0;         // her height
    double bodyDx = 0;         // her side step
};

// Turns the live state into one frame's inputs (CHARACTERS 5, Behavior, Inputs), firing each peak, trouble and click
// once. The pointer is read here only, so only while her style draws.
class CharacterLiveReader {
public:
    // Slack around her drawn outline for a click (points).
    static constexpr double kClickSlack = 2;

    // `outline` (her traced outline, nullptr before the first) hit-tests a click.
    agentspeed::character::BehaviorInputs read(const CharacterLive& live, double time, const LiveGeometry& g,
                                               const agentspeed::character::HudSilhouette* outline);

    // Her face and the pointer on the canvas at the last read, for the frame harness's trace.
    std::optional<std::pair<double, double>> lastFace;
    std::optional<QPointF> lastPointer;

private:
    std::optional<int> peaks_, troubles_, clicks_;
};

}  // namespace orb
