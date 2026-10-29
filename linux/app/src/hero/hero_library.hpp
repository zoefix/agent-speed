#pragma once

#include <QPointF>
#include <QString>
#include <array>
#include <memory>
#include <optional>
#include <vector>

// spec/assets/vehicles.json prepared for drawing (DESIGN 4c, Art format): every vehicle of the title ladder with its
// parts as flattened polylines, its rig, emitters, motion constants and scenery.
namespace hero {

enum class Role { Body, Detail, Window, Wheel, Rim, Spoke, Crank, Rider, Light, Glow, Shock, Flame, FlameCore, Count };
enum class Kind { Walk, Bicycle, Ebike, Motorcycle, Car, SportsCar, BulletTrain, RaceCar, Maglev, Jetliner, FighterJet, Hypersonic, Rocket };
enum class Scenery { Road, Rail, Guideway, Sky, Stratosphere, Space };
enum class EmitterKind { Dust, Arc, Exhaust, Trail, Sparks, Contrail, Afterburner, Plume };

struct RoleStyle {
    double weight = 1, opacity = 1, glow = 0;
    bool sprung = false;
    bool hot = false;      // painted with the accent H
    int fill = 0;          // 0 none, 1 heat, 2 hot
    double fillOpacity = 0;
};

// A point of a flattened path; `move` starts a subpath.
struct Vertex {
    double x = 0, y = 0;
    bool move = false;
};

struct Part {
    QString id;
    Role role = Role::Body;
    std::vector<Vertex> path;  // empty for a circle
    bool isCircle = false;
    double cx = 0, cy = 0, r = 0;
    bool hasSpin = false;
    QPointF spinPivot;
    double spinRadius = 1, spinSymmetry = 1;
    bool hasNozzle = false;
    QPointF nozzleAt, nozzleDir;
    bool sprung = false;
    bool bleed = false;
    QPointF apex;               // first point: the shock cone scales about it
    double spanX0 = 0, spanX1 = 0, spanY = 0;
};

struct Emitter {
    EmitterKind kind = EmitterKind::Dust;
    QPointF at, dir;
    int count = 0;
    std::vector<double> lanes;
    double length = 0;
    bool weak = false;
};

struct WalkRig {
    QPointF hip, torso;
    double headX = 0, headY = 0, headR = 0;
    double thigh = 0, shin = 0, foot = 0, upperArm = 0, forearm = 0;
    double cycle = 1, stance = 0.5, lift = 0, bob = 0, armSwing = 0, elbow = 0;
};

struct PedalRig {
    QPointF hip, crankPivot;
    double crankLength = 0, crankRadius = 1;
    double thigh = 0, shin = 0, pedal = 0, bob = 0;
};

struct Vehicle {
    Kind kind = Kind::Walk;
    QString key;
    Scenery scenery = Scenery::Road;
    std::optional<double> groundY;
    std::optional<double> heading;  // radians
    double lo = 0, hi = 0;          // world speed range, units/s
    double rest = 0, bob = 0, floatAmount = 0, hover = 0;
    std::optional<WalkRig> walk;
    std::optional<PedalRig> pedal;
    std::vector<Part> parts;
    std::vector<Emitter> emitters;
    double x0 = 0, y0 = 0, x1 = 0, y1 = 0;  // body bounds
    QPointF pitchPivot;
    bool rollsOnTires = false;

    QPointF center() const { return {(x0 + x1) / 2, (y0 + y1) / 2}; }
    QPointF headingVector() const;
    const Emitter* emitter(EmitterKind kind) const;
};

struct SceneryParams {
    double dash = 7, dashGap = 5, roadParallax = 1;
    double sleeperSpacing = 9, sleeperDepth = 2.6, railParallax = 1;
    double guidewayDepth = 3.4, pylonSpacing = 38, pylonDepth = 9, guidewayParallax = 1;
    struct Cloud {
        double y = 0, scale = 1, parallax = 0, opacity = 0;
    };
    std::vector<Cloud> skyLayers;
    double horizonCy = 178, horizonR = 108;
    int stars = 14;
    double spaceParallax = 0.5;
};

class Library {
public:
    // The embedded vehicles.json; nullptr when it does not parse.
    static const Library* shared();

    double strokeWidth = 2;
    double maskSolid = 42, maskClear = 49;
    double orbFraction = 0.72;
    std::array<RoleStyle, static_cast<int>(Role::Count)> roles{};
    std::vector<Vehicle> vehicles;
    SceneryParams scenery;
    std::vector<std::vector<Vertex>> clouds;
    int fallback = 0;

    const RoleStyle& style(Role role) const { return roles[static_cast<int>(role)]; }
    int indexOf(const QString& key) const;
};

}  // namespace hero
