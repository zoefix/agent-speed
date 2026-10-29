#pragma once

#include <QByteArray>

// GLSL 3.30 ports of the macOS orb shaders (OrbShaderSource, OrbLedShaderSource, OrbLiquidShaderSource).
namespace orb::shaders {

QByteArray shapeVertex();
QByteArray shapeFragment();
QByteArray ledVertex();
QByteArray ledFragment();
QByteArray textVertex();
QByteArray textFragment();
QByteArray liquidVertex();
QByteArray liquidFragment();
QByteArray imageVertex();
QByteArray imageFragment();

}  // namespace orb::shaders
