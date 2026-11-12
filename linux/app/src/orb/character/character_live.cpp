#include "orb/character/character_live.hpp"

#include <QDateTime>
#include <algorithm>
#include <cmath>

#include "agentspeed/character/selection.hpp"

namespace orb {

namespace {

// Slack around her hit capsule for `over` (points), as the orb window's hit test.
constexpr double kHoverSlack = 1;

}  // namespace

void CharacterLive::update(const CharacterActivity& activity, double now, qint64 wallNowMs) {
    using agentspeed::Phase;
    using agentspeed::character::AgentActivity;
    phase = activity.phase() == Phase::Generating ? AgentActivity::Generating
            : activity.phase() == Phase::Tools    ? AgentActivity::Tools
                                                  : AgentActivity::Idle;
    waitingSince.reset();
    if (const auto since = activity.waitingSinceMs()) waitingSince = clockOf(*since, now, wallNowMs);
    idleSince = clockOf(activity.idleSinceMs(), now, wallNowMs);
    hour = QDateTime::fromMSecsSinceEpoch(wallNowMs).time().hour();
}

QPointF CharacterLive::canvasPoint(QPointF point, double canvasSide, double contentScale) {
    const double c = canvasSide / 2;
    const double s = std::max(contentScale, 0.01);
    return {c + (point.x() - c) / s, c + (point.y() - c) / s};
}

std::optional<std::pair<double, double>> CharacterLive::cursor(QPointF point, std::pair<double, double> face, double height) {
    if (!(height > 0)) return std::nullopt;
    const double x = (point.x() - face.first) / height;
    const double y = (point.y() - face.second) / height;
    if (std::sqrt(x * x + y * y) > agentspeed::character::Behavior::kLookRange) return std::nullopt;
    return std::pair{x, y};
}

bool CharacterLive::overCapsule(QPointF point, QPointF center, double diameter, double bodyDx) {
    return agentspeed::character::footprint::characterContains(point.x() - center.x() - bodyDx, point.y() - center.y(), diameter, kHoverSlack);
}

agentspeed::character::BehaviorInputs CharacterLiveReader::read(const CharacterLive& live, double time, const LiveGeometry& g,
                                                                const agentspeed::character::HudSilhouette* outline) {
    using agentspeed::character::AgentActivity;
    agentspeed::character::BehaviorInputs in;
    in.phase = live.phase;
    in.hour = live.hour;
    if (live.phase == AgentActivity::Generating && live.waitingSince) in.wait = std::max(0.0, time - *live.waitingSince);
    if (live.phase == AgentActivity::Idle) in.idle = std::max(0.0, time - live.idleSince);
    in.newPeak = CharacterLive::fresh(live.peaks, peaks_) && live.celebratesPeaks;
    in.trouble = CharacterLive::fresh(live.troubles, troubles_);

    std::optional<QPointF> point;
    if (live.scripted) {
        if (live.scriptedCursor) point = QPointF(g.face.first + live.scriptedCursor->first * g.height, g.face.second + live.scriptedCursor->second * g.height);
    } else if (live.pointer) {
        if (const auto p = live.pointer()) point = CharacterLive::canvasPoint(*p, g.canvasSide, g.contentScale);
    }
    lastFace = g.face;
    lastPointer = point;
    if (point) {
        in.cursor = CharacterLive::cursor(*point, g.face, g.height);
        in.over = !live.dragging && CharacterLive::overCapsule(*point, g.center, g.diameter, g.bodyDx);
    }
    if (CharacterLive::fresh(live.clicks, clicks_)) {
        const auto press = live.scripted ? point : std::optional<QPointF>(CharacterLive::canvasPoint(live.clickPoint, g.canvasSide, g.contentScale));
        in.click = press && outline && outline->contains(press->x(), press->y(), kClickSlack);
    }
    return in;
}

}  // namespace orb
