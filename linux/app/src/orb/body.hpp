#pragma once

#include <QImage>

#include "orb/color.hpp"
#include "orb/style.hpp"

namespace orb {

// The static smoked-glass (or porcelain) disc of DESIGN 2 layer 2: drop shadow outside the disc, body gradient, inner
// shadow, lens and rim. `side` is the window side in points; the disc of diameter `s` sits in its center.
QImage renderBody(double s, double side, double dpr, const Tokens& tokens, bool blurBehind, StyleKind style = StyleKind::Led);

}  // namespace orb
