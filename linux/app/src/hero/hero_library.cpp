#include "hero/hero_library.hpp"

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <algorithm>
#include <cmath>
#include <limits>

namespace hero {

namespace {

constexpr double kPi = 3.14159265358979323846;

const char* const kRoles[] = {"body", "detail", "window", "wheel", "rim", "spoke", "crank", "rider", "light", "glow", "shock", "flame", "flameCore"};

std::optional<Role> roleOf(const QString& name) {
    for (int i = 0; i < static_cast<int>(Role::Count); ++i) {
        if (name == QLatin1String(kRoles[i])) return static_cast<Role>(i);
    }
    return std::nullopt;
}

std::optional<Kind> kindOf(const QString& key) {
    static const QStringList keys{QStringLiteral("walk"),        QStringLiteral("bicycle"),   QStringLiteral("ebike"),     QStringLiteral("motorcycle"),
                                  QStringLiteral("car"),         QStringLiteral("sportsCar"), QStringLiteral("bulletTrain"), QStringLiteral("raceCar"),
                                  QStringLiteral("maglev"),      QStringLiteral("jetliner"),  QStringLiteral("fighterJet"), QStringLiteral("hypersonic"),
                                  QStringLiteral("rocket")};
    const int i = static_cast<int>(keys.indexOf(key));
    return i < 0 ? std::nullopt : std::optional<Kind>(static_cast<Kind>(i));
}

std::optional<Scenery> sceneryOf(const QString& key) {
    static const QStringList keys{QStringLiteral("road"), QStringLiteral("rail"), QStringLiteral("guideway"),
                                  QStringLiteral("sky"),  QStringLiteral("stratosphere"), QStringLiteral("space")};
    const int i = static_cast<int>(keys.indexOf(key));
    return i < 0 ? std::nullopt : std::optional<Scenery>(static_cast<Scenery>(i));
}

std::optional<EmitterKind> emitterOf(const QString& key) {
    static const QStringList keys{QStringLiteral("dust"),     QStringLiteral("arc"),         QStringLiteral("exhaust"), QStringLiteral("trail"),
                                  QStringLiteral("sparks"),   QStringLiteral("contrail"),    QStringLiteral("afterburner"), QStringLiteral("plume")};
    const int i = static_cast<int>(keys.indexOf(key));
    return i < 0 ? std::nullopt : std::optional<EmitterKind>(static_cast<EmitterKind>(i));
}

QPointF point(const QJsonValue& v) {
    const auto a = v.toArray();
    return {a.size() > 0 ? a[0].toDouble() : 0, a.size() > 1 ? a[1].toDouble() : 0};
}

struct Command {
    char op = 'M';
    double v[6] = {0, 0, 0, 0, 0, 0};
};

// Absolute `M L C Q Z` commands, tokens separated by single spaces (DESIGN 4c, Geometry).
std::vector<Command> parse(const QString& d) {
    std::vector<Command> out;
    const auto tokens = d.split(QLatin1Char(' '), Qt::SkipEmptyParts);
    for (int i = 0; i < tokens.size();) {
        Command c;
        c.op = tokens[i].at(0).toLatin1();
        ++i;
        const int count = c.op == 'C' ? 6 : c.op == 'Q' ? 4 : c.op == 'Z' ? 0 : 2;
        for (int k = 0; k < count && i < tokens.size(); ++k, ++i) c.v[k] = tokens[i].toDouble();
        out.push_back(c);
    }
    return out;
}

std::vector<Vertex> flatten(const std::vector<Command>& commands, double step = 1.1) {
    std::vector<Vertex> out;
    double cx = 0, cy = 0, sx = 0, sy = 0;
    const auto steps = [step](double length) { return std::min(32, std::max(2, static_cast<int>(std::ceil(length / step)))); };
    for (const auto& c : commands) {
        switch (c.op) {
        case 'M':
            cx = sx = c.v[0];
            cy = sy = c.v[1];
            out.push_back({cx, cy, true});
            break;
        case 'L':
            cx = c.v[0];
            cy = c.v[1];
            out.push_back({cx, cy, false});
            break;
        case 'Q': {
            const int n = steps(std::hypot(c.v[0] - cx, c.v[1] - cy) + std::hypot(c.v[2] - c.v[0], c.v[3] - c.v[1]));
            for (int k = 1; k <= n; ++k) {
                const double t = static_cast<double>(k) / n, u = 1 - t;
                out.push_back({u * u * cx + 2 * u * t * c.v[0] + t * t * c.v[2], u * u * cy + 2 * u * t * c.v[1] + t * t * c.v[3], false});
            }
            cx = c.v[2];
            cy = c.v[3];
            break;
        }
        case 'C': {
            const int n = steps(std::hypot(c.v[0] - cx, c.v[1] - cy) + std::hypot(c.v[2] - c.v[0], c.v[3] - c.v[1]) +
                                std::hypot(c.v[4] - c.v[2], c.v[5] - c.v[3]));
            for (int k = 1; k <= n; ++k) {
                const double t = static_cast<double>(k) / n, u = 1 - t;
                const double a = u * u * u, b = 3 * u * u * t, e = 3 * u * t * t, f = t * t * t;
                out.push_back({a * cx + b * c.v[0] + e * c.v[2] + f * c.v[4], a * cy + b * c.v[1] + e * c.v[3] + f * c.v[5], false});
            }
            cx = c.v[4];
            cy = c.v[5];
            break;
        }
        case 'Z':
            cx = sx;
            cy = sy;
            out.push_back({sx, sy, false});
            break;
        default:
            break;
        }
    }
    return out;
}

// Every control point of the commands (the bounds use them, like the reference).
std::vector<QPointF> endpoints(const std::vector<Command>& commands) {
    std::vector<QPointF> out;
    for (const auto& c : commands) {
        const int count = c.op == 'C' ? 3 : c.op == 'Q' ? 2 : c.op == 'Z' ? 0 : 1;
        for (int k = 0; k < count; ++k) out.emplace_back(c.v[2 * k], c.v[2 * k + 1]);
    }
    return out;
}

Library* load() {
    QFile file(QStringLiteral(":/assets/vehicles.json"));
    if (!file.open(QIODevice::ReadOnly)) return nullptr;
    const auto doc = QJsonDocument::fromJson(file.readAll());
    if (!doc.isObject()) return nullptr;
    const auto root = doc.object();
    auto* lib = new Library;
    lib->strokeWidth = root.value(QStringLiteral("stroke")).toObject().value(QStringLiteral("width")).toDouble(2);
    const auto mask = root.value(QStringLiteral("mask")).toObject();
    lib->maskSolid = mask.value(QStringLiteral("solid")).toDouble(42);
    lib->maskClear = mask.value(QStringLiteral("clear")).toDouble(49);
    lib->orbFraction = root.value(QStringLiteral("canvas")).toObject().value(QStringLiteral("orbFraction")).toDouble(0.72);
    const auto roles = root.value(QStringLiteral("roles")).toObject();
    for (int i = 0; i < static_cast<int>(Role::Count); ++i) {
        const auto r = roles.value(QLatin1String(kRoles[i])).toObject();
        RoleStyle s;
        s.weight = r.value(QStringLiteral("weight")).toDouble(1);
        s.opacity = r.value(QStringLiteral("opacity")).toDouble(1);
        s.glow = r.value(QStringLiteral("glow")).toDouble(0);
        s.sprung = r.value(QStringLiteral("sprung")).toBool();
        s.hot = r.value(QStringLiteral("color")).toString() == QLatin1String("hot");
        const QString fill = r.value(QStringLiteral("fill")).toString();
        s.fill = fill.isEmpty() ? 0 : fill == QLatin1String("hot") ? 2 : 1;
        s.fillOpacity = r.value(QStringLiteral("fillOpacity")).toDouble(0);
        lib->roles[static_cast<std::size_t>(i)] = s;
    }
    const auto scenery = root.value(QStringLiteral("scenery")).toObject();
    auto& sp = lib->scenery;
    const auto road = scenery.value(QStringLiteral("road")).toObject();
    const auto dash = road.value(QStringLiteral("dash")).toArray();
    sp.dash = dash.size() > 0 ? dash[0].toDouble() : 7;
    sp.dashGap = dash.size() > 1 ? dash[1].toDouble() : 5;
    sp.roadParallax = road.value(QStringLiteral("parallax")).toDouble(1);
    const auto rail = scenery.value(QStringLiteral("rail")).toObject();
    sp.sleeperSpacing = rail.value(QStringLiteral("sleeperSpacing")).toDouble(9);
    sp.sleeperDepth = rail.value(QStringLiteral("sleeperDepth")).toDouble(2.6);
    sp.railParallax = rail.value(QStringLiteral("parallax")).toDouble(1);
    const auto guideway = scenery.value(QStringLiteral("guideway")).toObject();
    sp.guidewayDepth = guideway.value(QStringLiteral("depth")).toDouble(3.4);
    sp.pylonSpacing = guideway.value(QStringLiteral("pylonSpacing")).toDouble(38);
    sp.pylonDepth = guideway.value(QStringLiteral("pylonDepth")).toDouble(9);
    sp.guidewayParallax = guideway.value(QStringLiteral("parallax")).toDouble(1);
    const auto sky = scenery.value(QStringLiteral("sky")).toObject();
    for (const auto& layer : sky.value(QStringLiteral("layers")).toArray()) {
        const auto o = layer.toObject();
        sp.skyLayers.push_back({o.value(QStringLiteral("y")).toDouble(), o.value(QStringLiteral("scale")).toDouble(1),
                                o.value(QStringLiteral("parallax")).toDouble(), o.value(QStringLiteral("opacity")).toDouble()});
    }
    for (const auto& cloud : sky.value(QStringLiteral("clouds")).toArray()) lib->clouds.push_back(flatten(parse(cloud.toString()), 0.8));
    const auto horizon = scenery.value(QStringLiteral("stratosphere")).toObject().value(QStringLiteral("horizon")).toObject();
    sp.horizonCy = horizon.value(QStringLiteral("cy")).toDouble(178);
    sp.horizonR = horizon.value(QStringLiteral("r")).toDouble(108);
    const auto space = scenery.value(QStringLiteral("space")).toObject();
    sp.stars = space.value(QStringLiteral("stars")).toInt(14);
    sp.spaceParallax = space.value(QStringLiteral("parallax")).toDouble(0.5);

    for (const auto& item : root.value(QStringLiteral("vehicles")).toArray()) {
        const auto v = item.toObject();
        const auto kind = kindOf(v.value(QStringLiteral("key")).toString());
        const auto scene = sceneryOf(v.value(QStringLiteral("scenery")).toString());
        if (!kind || !scene) continue;
        Vehicle out;
        out.kind = *kind;
        out.key = v.value(QStringLiteral("key")).toString();
        out.scenery = *scene;
        if (v.contains(QStringLiteral("groundY"))) out.groundY = v.value(QStringLiteral("groundY")).toDouble();
        if (v.contains(QStringLiteral("heading"))) out.heading = v.value(QStringLiteral("heading")).toDouble() * kPi / 180;
        const auto motion = v.value(QStringLiteral("motion")).toObject();
        const auto speed = motion.value(QStringLiteral("speed")).toArray();
        out.lo = speed.size() > 0 ? speed[0].toDouble() : 0;
        out.hi = speed.size() > 1 ? speed[1].toDouble() : 0;
        out.rest = motion.value(QStringLiteral("rest")).toDouble(0);
        out.bob = motion.value(QStringLiteral("bob")).toDouble(0);
        out.floatAmount = motion.value(QStringLiteral("float")).toDouble(0);
        out.hover = motion.value(QStringLiteral("hover")).toDouble(0);
        const auto rig = v.value(QStringLiteral("rig")).toObject();
        const QString rigType = rig.value(QStringLiteral("type")).toString();
        if (rigType == QLatin1String("walk")) {
            WalkRig w;
            w.hip = point(rig.value(QStringLiteral("hip")));
            w.torso = point(rig.value(QStringLiteral("torso")));
            const auto head = rig.value(QStringLiteral("head")).toArray();
            w.headX = head.size() > 0 ? head[0].toDouble() : 0;
            w.headY = head.size() > 1 ? head[1].toDouble() : 0;
            w.headR = head.size() > 2 ? head[2].toDouble() : 0;
            w.thigh = rig.value(QStringLiteral("thigh")).toDouble();
            w.shin = rig.value(QStringLiteral("shin")).toDouble();
            w.foot = rig.value(QStringLiteral("foot")).toDouble();
            w.upperArm = rig.value(QStringLiteral("upperArm")).toDouble();
            w.forearm = rig.value(QStringLiteral("forearm")).toDouble();
            w.cycle = rig.value(QStringLiteral("cycle")).toDouble(1);
            w.stance = rig.value(QStringLiteral("stance")).toDouble(0.5);
            w.lift = rig.value(QStringLiteral("lift")).toDouble();
            w.bob = rig.value(QStringLiteral("bob")).toDouble();
            w.armSwing = rig.value(QStringLiteral("armSwing")).toDouble();
            w.elbow = rig.value(QStringLiteral("elbow")).toDouble();
            out.walk = w;
        } else if (rigType == QLatin1String("pedal")) {
            PedalRig p;
            p.hip = point(rig.value(QStringLiteral("hip")));
            const auto crank = rig.value(QStringLiteral("crank")).toObject();
            p.crankPivot = point(crank.value(QStringLiteral("pivot")));
            p.crankLength = crank.value(QStringLiteral("length")).toDouble();
            p.crankRadius = crank.value(QStringLiteral("radius")).toDouble(1);
            p.thigh = rig.value(QStringLiteral("thigh")).toDouble();
            p.shin = rig.value(QStringLiteral("shin")).toDouble();
            p.pedal = rig.value(QStringLiteral("pedal")).toDouble();
            p.bob = rig.value(QStringLiteral("bob")).toDouble();
            out.pedal = p;
        }
        // Body bounds: every part's points but bleeding and soft parts (a bleeding body counts its leading half).
        double x0 = std::numeric_limits<double>::max(), y0 = x0, x1 = -x0, y1 = -x0;
        const auto add = [&](double x, double y) {
            x0 = std::min(x0, x);
            y0 = std::min(y0, y);
            x1 = std::max(x1, x);
            y1 = std::max(y1, y);
        };
        for (const auto& pv : v.value(QStringLiteral("parts")).toArray()) {
            const auto p = pv.toObject();
            const auto role = roleOf(p.value(QStringLiteral("role")).toString());
            if (!role) continue;
            Part part;
            part.id = p.value(QStringLiteral("id")).toString();
            part.role = *role;
            const QString d = p.value(QStringLiteral("d")).toString();
            std::vector<Command> commands;
            if (!d.isEmpty()) {
                commands = parse(d);
                part.path = flatten(commands);
                if (!part.path.empty()) {
                    part.apex = QPointF(part.path.front().x, part.path.front().y);
                    part.spanX0 = part.path.front().x;
                    part.spanX1 = part.path.back().x;
                    part.spanY = part.path.front().y;
                }
            } else {
                const auto c = p.value(QStringLiteral("circle")).toArray();
                part.isCircle = true;
                part.cx = c.size() > 0 ? c[0].toDouble() : 0;
                part.cy = c.size() > 1 ? c[1].toDouble() : 0;
                part.r = c.size() > 2 ? c[2].toDouble() : 0;
            }
            if (p.contains(QStringLiteral("spin"))) {
                const auto s = p.value(QStringLiteral("spin")).toObject();
                part.hasSpin = true;
                part.spinPivot = point(s.value(QStringLiteral("pivot")));
                part.spinRadius = s.value(QStringLiteral("radius")).toDouble(1);
                part.spinSymmetry = s.value(QStringLiteral("symmetry")).toDouble(1);
            }
            if (p.contains(QStringLiteral("nozzle"))) {
                const auto n = p.value(QStringLiteral("nozzle")).toObject();
                part.hasNozzle = true;
                part.nozzleAt = point(n.value(QStringLiteral("at")));
                part.nozzleDir = point(n.value(QStringLiteral("dir")));
            }
            part.sprung = p.contains(QStringLiteral("sprung")) ? p.value(QStringLiteral("sprung")).toBool() : lib->style(part.role).sprung;
            part.bleed = p.value(QStringLiteral("bleed")).toBool();
            const bool soft = part.role == Role::Flame || part.role == Role::FlameCore || part.role == Role::Shock || part.role == Role::Glow;
            if (part.bleed || soft) {
                if (part.bleed && part.role == Role::Body) {
                    for (const auto& pt : endpoints(commands)) {
                        if (pt.x() > 30) add(pt.x(), pt.y());
                    }
                }
            } else if (part.isCircle) {
                add(part.cx - part.r, part.cy - part.r);
                add(part.cx + part.r, part.cy + part.r);
            } else {
                for (const auto& pt : endpoints(commands)) add(pt.x(), pt.y());
            }
            out.parts.push_back(part);
        }
        if (out.walk) {
            const auto& w = *out.walk;
            add(w.hip.x() - 10, w.hip.y() + w.torso.y() + w.headY - w.headR);
            add(w.hip.x() + 12, out.groundY.value_or(w.hip.y()));
        } else if (out.pedal) {
            const auto& p = *out.pedal;
            add(p.hip.x() - 10, p.hip.y() - 20);
            add(p.hip.x() + 12, out.groundY.value_or(p.hip.y()));
        }
        out.x0 = x0;
        out.y0 = y0;
        out.x1 = x1;
        out.y1 = y1;
        for (const auto& ev : v.value(QStringLiteral("emitters")).toArray()) {
            const auto e = ev.toObject();
            const auto kind = emitterOf(e.value(QStringLiteral("type")).toString());
            if (!kind) continue;
            Emitter em;
            em.kind = *kind;
            em.at = point(e.value(QStringLiteral("at")));
            em.dir = point(e.value(QStringLiteral("dir")));
            em.count = e.value(QStringLiteral("count")).toInt(0);
            for (const auto& lane : e.value(QStringLiteral("lanes")).toArray()) em.lanes.push_back(lane.toDouble());
            em.length = e.value(QStringLiteral("length")).toDouble(0);
            em.weak = e.value(QStringLiteral("weak")).toBool();
            out.emitters.push_back(em);
        }
        // The pitch pivot: the rear tire's ground contact, else the body center.
        out.pitchPivot = out.center();
        for (const auto& part : out.parts) {
            if (part.hasSpin && part.role == Role::Spoke && out.groundY) {
                out.pitchPivot = QPointF(part.spinPivot.x(), *out.groundY);
                break;
            }
        }
        out.rollsOnTires = out.scenery == Scenery::Road &&
                           std::any_of(out.parts.begin(), out.parts.end(), [](const Part& p) { return p.role == Role::Wheel; });
        lib->vehicles.push_back(std::move(out));
    }
    const QString fallback = root.value(QStringLiteral("idle")).toObject().value(QStringLiteral("fallback")).toString(QStringLiteral("walk"));
    lib->fallback = std::max(0, lib->indexOf(fallback));
    if (lib->vehicles.empty()) {
        delete lib;
        return nullptr;
    }
    return lib;
}

}  // namespace

QPointF Vehicle::headingVector() const { return heading ? QPointF(std::cos(*heading), std::sin(*heading)) : QPointF(1, 0); }

const Emitter* Vehicle::emitter(EmitterKind kind) const {
    for (const auto& e : emitters) {
        if (e.kind == kind) return &e;
    }
    return nullptr;
}

int Library::indexOf(const QString& key) const {
    for (std::size_t i = 0; i < vehicles.size(); ++i) {
        if (vehicles[i].key == key) return static_cast<int>(i);
    }
    return -1;
}

const Library* Library::shared() {
    static const Library* library = load();
    return library;
}

}  // namespace hero
