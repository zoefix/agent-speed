#pragma once

#include <QColor>
#include <QString>
#include <functional>
#include <optional>
#include <vector>

#include "orb/frame.hpp"

namespace orb {

// The intelligence readout (DESIGN 10): 智力 over 思考, above the value.
struct IntelReadout {
    double opacity = 0;  // the quiet rule's fade
    QString glyph, number;
    int form = 0;  // 0 none, 1 live, 2 resting
    bool proof = false;
    double numberAlpha = 1;  // a fall crossfades the digits
    QString thinkingGlyph, thinking, unit;
    double thinkingOpacity = 1;  // hidden at zero
    double glide = 0;            // 0: two lines; 1: the single-line position
};

// The network readout (DESIGN 10): download over upload, each arrow · number · unit in fixed cells.
struct NetReadout {
    double opacity = 0;  // the quiet rule's fade
    QString downNumber, downUnit, upNumber, upUnit;
};

// What the readout shows this frame (DESIGN 2 layer 6).
struct ReadoutContent {
    QString value;
    bool atLeast = false;  // a partly unpriced money value: a small ≥ before it (DESIGN 9)
    // The odometer (DESIGN 2a, Shared foundation): the value it replaces, when, and whether it rose.
    QString previousValue;
    double valueChangedAt = -1e9;
    bool rising = true;
    bool rolls = false;  // the effects allow it (not Off, S ≥ 80)
    QString unit;
    QString title;          // the speed title, or the idle label
    QString previousTitle;  // the one sliding out
    double titleChangedAt = -1e9;
    double titleHeat = 0;
    QString info;  // the hover line
    double hover = 0;
    double valueGlow = 0;  // envelope from boost up
    double skew = 0;       // envelope from overdrive up
    QPointF jitter;
    std::vector<QColor> agents;  // one dot per generating agent
    QString peakLabel;
    IntelReadout intel;
    NetReadout net;
};

// Where a style places the readout (the frame table of DESIGN 2a, Shared foundation), fractions of S from the center.
struct ReadoutLayout {
    enum class Title { BelowUnit, BelowValue, Fixed };
    double valueSize = 0.24, valueBaseline = 0.012, valueMaxWidth = 0.62;
    double unitSize = 0.072;  // 0: no unit
    double unitGap = 0.104;
    Title title = Title::BelowUnit;
    double titlePlace = 0.09;  // the gap below the unit or value, or the fixed baseline
    double titleSize = 0.062, titleMaxWidth = 0.58;
    double infoScale = 1, infoMaxWidth = 0.62;
    std::optional<double> dots = 0.3785;
    bool dotHalo = false;
    double pill = -0.232;
    bool titleCrossfades = false;
    bool rolls = true;  // the odometer (`minimal` never rolls)
    bool valueGlow = true;
    bool valueMotion = true;  // the lean from overdrive and the redline jitter

    double titleOffset() const;
    static const ReadoutLayout led;
};

// Where a style puts the two-line readout (DESIGN 10), fractions of S from the center.
struct LinePlacement {
    double nearestBaseline = -0.245;  // the line nearest the value
    bool below = false;               // below the value (`needle`, ↓ nearest), else above it (↑ nearest)
    double size = 0.050;              // t = max(7 pt, size S)
    double fit = 0.44;                // a line must fit this × S
    bool recenters = true;            // the stack moves down while the block shows
    std::optional<double> thinkingBaseline;
    bool thinkingUnit = true;
    std::optional<double> hubReach;  // `needle`: each line's bottom corners stay within this × S of the center

    double textSize(double s) const { return std::max(7.0, size * s); }
    static const LinePlacement led, reactor, needle, minimal;
};

// Colors that replace the tokens (the readout under the liquid surface); unset keeps the token.
struct ReadoutPalette {
    std::optional<Rgba> value, unit, title, lineNumber, lineSecondary;
    Rgba shadow;
    double shadowRadius = 0;  // points
    bool valueGlow = true;
};

// One pass of the readout: `liquid` draws it twice, split at its surface.
struct ReadoutPass {
    ReadoutPalette palette;
    int clip = 0;  // TextDraw::clip
    bool pill = true;
    bool dots = true;
    bool linesYieldToPill = true;
};

using MeasureText = std::function<double(const TextDraw&)>;

// The readout stack's shift down while the two-line block shows above the value (DESIGN 10), eased with its fade.
double readoutShift(const Frame& frame, const ReadoutLayout& layout, const LinePlacement& placement,
                    const ReadoutContent& content, const MeasureText& measure);
// Whether the two-line readout fits at this size (and would be drawn).
bool linesFit(const Frame& frame, const LinePlacement& placement, const ReadoutContent& content, const MeasureText& measure);

// Adds the value, unit, title (or hover line), agent dots, the new-peak pill and the two-line readout.
void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure,
                  const ReadoutLayout& layout, const LinePlacement& placement, const ReadoutPass& pass = {});

}  // namespace orb
