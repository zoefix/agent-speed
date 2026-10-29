#pragma once

#include <QColor>
#include <QString>
#include <functional>
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

using MeasureText = std::function<double(const TextDraw&)>;

// Adds the value, unit, title (or hover line), agent dots and the new-peak pill.
void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure);

}  // namespace orb
