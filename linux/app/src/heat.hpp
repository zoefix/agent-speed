#pragma once

#include <QColor>

// DESIGN 1: speed to heat, and the heat palette.
namespace heat {

constexpr double kDefaultRedline = 2000.0;

double of(double tokS, double redline = kDefaultRedline);
QColor color(double h);

}  // namespace heat
