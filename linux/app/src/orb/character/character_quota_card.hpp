#pragma once

#include <QString>
#include <array>
#include <map>
#include <string>
#include <vector>

#include "agentspeed/character/hud_quota.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "orb/character/character_cards.hpp"
#include "orb/scene.hpp"

namespace orb {

// The character orb's 額度 card (DESIGN 2a.6, 額度): per source a header line (its color dot and name, the host after
// it or on a line of its own), then a line per window — the label in a column as wide as the widest, the bar, the
// percent right-aligned in a `100%` cell and the short reset in a cell as wide as the widest. It takes the widest of its
// forms that fits the room the column gives it (QuotaForm); lines move by QuotaMotion. When it empties or is switched
// off it fades out with its last lines and starts afresh once gone.
class QuotaCard {
public:
    struct Ink {
        Rgba number, secondary;
        bool dark = true;
    };

    // One frame: `live` while it is switched on and has sources, `gone` once its plate has faded out; `small` below
    // S = 96 (its first source, no reset cells).
    void sync(const std::vector<agentspeed::character::QuotaSource>& sources, bool live, bool gone, bool small, double time, bool still);
    // The plate width of every form at `time`, widest first, as HudWings and the fit weigh them (zeros while empty).
    agentspeed::character::QuotaWidths widths(const cards::CardMetrics& m, double time) const;
    // Takes the widest form that fits `room` (narrower at once, wider after 1.2 s with `spare`); `snap` while off screen.
    // The bars and reset cells follow the plate to the new form, at once when `still`.
    void fit(double dt, const agentspeed::character::QuotaWidths& widths, double room, double spare, bool snap, bool still);
    // The plate in its form at `time`, nothing while it has no line.
    agentspeed::character::CardSize size(const cards::CardMetrics& m, double time) const;
    // Its lines inside a plate of `box` centered on the pen.
    void draw(Scene& scene, const cards::Pen& pen, agentspeed::character::CardSize box, const cards::CardMetrics& m, const Ink& ink,
              double time) const;

    agentspeed::character::QuotaForm form() const;
    bool moving(double time) const { return motion_.moving(time); }
    bool empty() const { return motion_.empty(); }
    const agentspeed::character::QuotaMotion& motion() const { return motion_; }

private:
    agentspeed::character::QuotaColumns columns(const cards::CardMetrics& m, double time) const;
    const QString& text(const std::string& s) const;
    bool hostOwnLine(const agentspeed::character::QuotaMotion::Source& source, agentspeed::character::QuotaForm form,
                     const agentspeed::character::QuotaColumns& c, const cards::CardMetrics& m) const;

    agentspeed::character::QuotaMotion motion_;
    agentspeed::character::FormFit fit_;
    agentspeed::character::QuotaFormEase ease_;
    bool small_ = false;
    mutable std::map<std::string, QString> texts_;
    // The columns of the frame at `columnsAt_` (time, t): a frame measures them once.
    mutable std::array<double, 2> columnsAt_{-1, -1};
    mutable agentspeed::character::QuotaColumns columns_;
};

}  // namespace orb
