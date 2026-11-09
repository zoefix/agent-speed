#include "agentspeed/character/rig.hpp"

#include <algorithm>
#include <cmath>
#include <numeric>

#include "agentspeed/character/error.hpp"
#include "agentspeed/character/json_read.hpp"
#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

using Kind = CharacterError::Kind;

std::optional<Param> Param::decode(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    auto id = v["id"].string();
    auto lo = jr::number(v["min"]), hi = jr::number(v["max"]), def = jr::number(v["default"]);
    if (!id || !lo || !hi || !def) return std::nullopt;
    return Param{*id, *lo, *hi, *def};
}

std::vector<Param> Param::decodeList(const json::Value& v) {
    if (!v.isArray()) jr::fail("params");
    std::vector<Param> out;
    for (const auto& e : v.elements()) {
        auto p = decode(e);
        if (!p) jr::fail("params");
        out.push_back(std::move(*p));
    }
    return out;
}

namespace {

std::vector<std::vector<double>> keyList(const json::Value& o, std::string_view key) {
    auto v = o[key];
    if (!v.isArray()) jr::fail(key);
    std::vector<std::vector<double>> out;
    for (const auto& e : v.elements()) {
        auto k = jr::numbers(e);
        if (!k) jr::fail(key);
        out.push_back(std::move(*k));
    }
    return out;
}

template <class T, class F>
std::vector<T> objects(const json::Value& o, std::string_view key, bool required, F&& read) {
    auto v = o[key];
    if (!v || v.isNull()) {
        if (required) jr::fail(key);
        return {};
    }
    if (!v.isArray()) jr::fail(key);
    std::vector<T> out;
    out.reserve(v.size());
    for (const auto& e : v.elements()) {
        if (!e.isObject()) jr::fail(key);
        out.push_back(read(e));
    }
    return out;
}

RigDocument::Landmarks readLandmarks(const json::Value& v) {
    RigDocument::Landmarks lm;
    if (!v.isObject()) return lm;
    lm.face = jr::optNumbers(v, "face");
    lm.faceWidth = jr::optNumber(v, "faceWidth");
    lm.feet = jr::optNumbers(v, "feet");
    lm.waist = jr::optNumbers(v, "waist");
    lm.chin = jr::optNumber(v, "chin");
    lm.hem = jr::optNumber(v, "hem");
    lm.torsoLength = jr::optNumber(v, "torsoLength");
    lm.objectSide = jr::optString(v, "objectSide");
    auto hands = v["hands"];
    if (hands.isObject()) {
        for (const auto& [side, h] : hands.members()) {
            if (!h.isObject()) jr::fail("hands");
            RigDocument::Hand hand;
            hand.wrist = jr::needNumbers(h, "wrist");
            hand.size = jr::needNumber(h, "size");
            hand.open = jr::optNumber(h, "open");
            lm.hands[side] = std::move(hand);
        }
    }
    return lm;
}

}  // namespace

RigDocument::Chain RigDocument::Chain::decode(const json::Value& e) {
    Chain c;
    c.name = jr::needString(e, "name");
    c.anchor = jr::needNumbers(e, "anchor");
    c.segments = jr::needNumbers(e, "segments");
    c.inputs = objects<PhysicsInput>(e, "inputs", true, [](const json::Value& i) {
        return PhysicsInput{jr::needString(i, "param"), jr::optNumber(i, "x"), jr::optNumber(i, "y")};
    });
    c.outputs = objects<PhysicsOutput>(e, "outputs", true, [](const json::Value& o) {
        return PhysicsOutput{jr::needString(o, "param"), jr::needInteger(o, "segment"), jr::needNumber(o, "degrees")};
    });
    c.stiffness = jr::needNumber(e, "stiffness");
    c.damping = jr::needNumber(e, "damping");
    c.wind = jr::needNumber(e, "wind");
    return c;
}

RigDocument RigDocument::decode(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text)) throw CharacterError(Kind::Unreadable, "rig.json");
    return decode(doc.root());
}

RigDocument RigDocument::decode(const json::Value& root) {
    if (!root.isObject()) throw CharacterError(Kind::Unreadable, "rig.json");
    RigDocument d;
    d.format = jr::needString(root, "format");
    d.version = jr::needInteger(root, "version");
    d.name = root["name"].string().value_or("");
    d.canvas = jr::needNumbers(root, "canvas");
    auto atlas = root["atlas"];
    if (!atlas.isObject()) jr::fail("atlas");
    d.atlasFile = jr::needString(atlas, "file");
    d.atlasSize = jr::needIntegers(atlas, "size");
    d.landmarks = readLandmarks(root["landmarks"]);
    d.bones = objects<Bone>(root, "bones", true, [](const json::Value& b) {
        Bone bone;
        bone.name = jr::needString(b, "name");
        bone.parent = jr::optString(b, "parent");
        bone.pivot = jr::needNumbers(b, "pivot");
        return bone;
    });
    d.params = Param::decodeList(root["params"]);
    d.drivers = objects<Driver>(root, "drivers", false, [](const json::Value& v) {
        return Driver{jr::needString(v, "param"), jr::needString(v, "bone"), jr::needString(v, "channel"),
                      keyList(v, "keys")};
    });
    d.physics = objects<Chain>(root, "physics", false, [](const json::Value& v) { return Chain::decode(v); });
    d.partDrivers = objects<PartDriver>(root, "partDrivers", false, [](const json::Value& v) {
        return PartDriver{jr::needString(v, "param"), jr::needString(v, "part"), keyList(v, "keys")};
    });
    d.parts = objects<Part>(root, "parts", true, [](const json::Value& v) {
        Part p;
        p.name = jr::needString(v, "name");
        p.tag = jr::needString(v, "tag");
        p.order = jr::needNumber(v, "order");
        p.vertices = jr::needNumbers(v, "vertices");
        p.uv = jr::needNumbers(v, "uv");
        p.triangles = jr::needIntegers(v, "triangles");
        p.influences = jr::needInteger(v, "influences");
        p.bones = jr::needIntegers(v, "bones");
        p.weights = jr::needNumbers(v, "weights");
        return p;
    });
    return d;
}

namespace {

std::optional<Rig::Channel> channelOf(const std::string& name) {
    if (name == "rotate") return Rig::Channel::Rotate;
    if (name == "x") return Rig::Channel::X;
    if (name == "y") return Rig::Channel::Y;
    if (name == "scaleX") return Rig::Channel::ScaleX;
    if (name == "scaleY") return Rig::Channel::ScaleY;
    return std::nullopt;
}

void splitKeys(const std::vector<std::vector<double>>& keys, std::vector<double>& xs, std::vector<double>& ys) {
    xs.clear();
    ys.clear();
    for (const auto& k : keys) {
        xs.push_back(k.size() > 0 ? k[0] : 0);
        ys.push_back(k.size() > 1 ? k[1] : 0);
    }
}

}  // namespace

Rig::Rig(const RigDocument& doc) {
    if (doc.format != RigDocument::kFormat || doc.version != RigDocument::kVersion) {
        throw CharacterError(Kind::Unsupported, doc.format + " " + std::to_string(doc.version));
    }
    name = doc.name;
    canvasWidth = doc.canvas.empty() ? 0 : doc.canvas[0];
    canvasHeight = doc.canvas.size() > 1 ? doc.canvas[1] : 0;
    atlasFile = doc.atlasFile;
    atlasWidth = doc.atlasSize.empty() ? 0 : static_cast<int>(doc.atlasSize[0]);
    atlasHeight = doc.atlasSize.size() > 1 ? static_cast<int>(doc.atlasSize[1]) : 0;
    for (const auto& p : doc.params) {
        if (paramIndex.count(p.id)) continue;
        paramIndex[p.id] = static_cast<int>(params.size());
        params.push_back(p);
    }

    std::unordered_map<std::string, int> boneIndex;
    for (std::size_t i = 0; i < doc.bones.size(); ++i) {
        const auto& b = doc.bones[i];
        if (b.pivot.size() != 2 || boneIndex.count(b.name)) throw CharacterError(Kind::BadBone, b.name);
        int parent = -1;
        if (b.parent) {
            auto it = boneIndex.find(*b.parent);
            if (it == boneIndex.end()) throw CharacterError(Kind::ParentsNotFirst, b.name);
            parent = it->second;
        }
        boneIndex[b.name] = static_cast<int>(i);
        parents.push_back(parent);
        pivots.emplace_back(b.pivot[0], b.pivot[1]);
        boneNames.push_back(b.name);
    }

    drivers.assign(doc.bones.size(), {});
    for (const auto& d : doc.drivers) {
        auto bone = boneIndex.find(d.bone);
        auto param = paramIndex.find(d.param);
        auto channel = channelOf(d.channel);
        const bool pairs = std::all_of(d.keys.begin(), d.keys.end(), [](const auto& k) { return k.size() == 2; });
        if (bone == boneIndex.end() || param == paramIndex.end() || !channel || d.keys.empty() || !pairs) {
            throw CharacterError(Kind::BadDriver, d.param + " → " + d.bone + "." + d.channel);
        }
        Driver driver;
        driver.param = param->second;
        driver.channel = *channel;
        splitKeys(d.keys, driver.xs, driver.ys);
        drivers[static_cast<std::size_t>(bone->second)].push_back(std::move(driver));
    }

    std::unordered_map<std::string, std::vector<Opacity>> opacity;
    for (const auto& d : doc.partDrivers) {
        auto param = paramIndex.find(d.param);
        if (param == paramIndex.end() || d.keys.empty()) continue;
        Opacity o;
        o.param = param->second;
        splitKeys(d.keys, o.xs, o.ys);
        opacity[d.part].push_back(std::move(o));
    }
    std::vector<std::size_t> order(doc.parts.size());
    std::iota(order.begin(), order.end(), 0);
    std::stable_sort(order.begin(), order.end(),
                     [&](std::size_t a, std::size_t b) { return doc.parts[a].order < doc.parts[b].order; });
    const auto boneCountAll = static_cast<std::int64_t>(doc.bones.size());
    for (std::size_t index : order) {
        const auto& p = doc.parts[index];
        const auto n = static_cast<std::int64_t>(p.vertices.size() / 2);
        const bool ok = p.vertices.size() % 2 == 0 && p.uv.size() == p.vertices.size() && p.triangles.size() % 3 == 0 &&
                        p.influences > 0 && static_cast<std::int64_t>(p.bones.size()) == n * p.influences &&
                        static_cast<std::int64_t>(p.weights.size()) == n * p.influences &&
                        std::all_of(p.triangles.begin(), p.triangles.end(),
                                    [n](std::int64_t t) { return t >= 0 && t < n; }) &&
                        std::all_of(p.bones.begin(), p.bones.end(),
                                    [boneCountAll](std::int64_t b) { return b >= 0 && b < boneCountAll; });
        if (!ok) throw CharacterError(Kind::BadPart, p.name);
        Part part;
        part.name = p.name;
        part.tag = p.tag;
        part.order = p.order;
        part.vertices = p.vertices;
        part.uv = p.uv;
        part.triangles.reserve(p.triangles.size());
        for (auto t : p.triangles) part.triangles.push_back(static_cast<std::uint32_t>(t));
        part.influences = static_cast<int>(p.influences);
        part.bones.reserve(p.bones.size());
        for (auto b : p.bones) part.bones.push_back(static_cast<std::uint16_t>(b));
        part.weights = p.weights;
        if (auto it = opacity.find(p.name); it != opacity.end()) part.opacity = it->second;
        parts.push_back(std::move(part));
    }
    for (const auto& chain : doc.physics) {
        const auto segments = static_cast<std::int64_t>(chain.segments.size());
        const bool ok = chain.anchor.size() == 2 && !chain.segments.empty() &&
                        std::all_of(chain.outputs.begin(), chain.outputs.end(), [segments](const auto& o) {
                            return o.segment >= 0 && o.segment < segments && o.degrees != 0;
                        });
        if (!ok) throw CharacterError(Kind::BadChain, chain.name);
    }
    physics = doc.physics;
    landmarks = doc.landmarks;
}

std::vector<double> Rig::defaults() const {
    std::vector<double> out;
    out.reserve(params.size());
    for (const auto& p : params) out.push_back(p.def);
    return out;
}

double Rig::clamped(const std::vector<double>& values, int i) const {
    const auto& p = params[static_cast<std::size_t>(i)];
    return static_cast<std::size_t>(i) < values.size() ? std::min(std::max(values[static_cast<std::size_t>(i)], p.min), p.max)
                                                         : p.def;
}

void Rig::worldTransforms(const std::vector<double>& values, std::vector<Affine2>& world) const {
    const auto count = static_cast<std::size_t>(boneCount());
    if (world.size() != count) world.assign(count, Affine2{});
    for (std::size_t i = 0; i < count; ++i) {
        double rotate = 0, x = 0, y = 0, scaleX = 0, scaleY = 0;
        for (const auto& d : drivers[i]) {
            const double v = math::interpolate(d.xs, d.ys, clamped(values, d.param));
            switch (d.channel) {
                case Channel::Rotate: rotate += v; break;
                case Channel::X: x += v; break;
                case Channel::Y: y += v; break;
                case Channel::ScaleX: scaleX += v; break;
                case Channel::ScaleY: scaleY += v; break;
            }
        }
        const auto [px, py] = pivots[i];
        const double r = rotate * (math::kPi / 180);
        const double c = std::cos(r), s = std::sin(r);
        const double sx = 1 + scaleX, sy = 1 + scaleY;
        const double a = c * sx, b = -s * sy, cc = s * sx, d = c * sy;
        const Affine2 local{a, b, cc, d, px - (a * px + b * py) + x, py - (cc * px + d * py) + y};
        world[i] = parents[i] < 0 ? local : world[static_cast<std::size_t>(parents[i])].then(local);
    }
}

void Rig::opacities(const std::vector<double>& values, std::vector<double>& out) const {
    if (out.size() != parts.size()) out.assign(parts.size(), 1.0);
    for (std::size_t i = 0; i < parts.size(); ++i) {
        double o = 1.0;
        for (const auto& d : parts[i].opacity) o *= math::interpolate(d.xs, d.ys, clamped(values, d.param));
        out[i] = o;
    }
}

void Rig::skin(int index, const std::vector<Affine2>& world, std::vector<double>& out) const {
    const auto& part = parts[static_cast<std::size_t>(index)];
    const auto n = static_cast<std::size_t>(part.vertexCount());
    const auto k = static_cast<std::size_t>(part.influences);
    if (out.size() != n * 2) out.assign(n * 2, 0.0);
    for (std::size_t v = 0; v < n; ++v) {
        const double x = part.vertices[2 * v], y = part.vertices[2 * v + 1];
        double sx = 0, sy = 0;
        for (std::size_t j = 0; j < k; ++j) {
            const double w = part.weights[v * k + j];
            if (w == 0) continue;
            const auto& m = world[part.bones[v * k + j]];
            sx += w * (m.a * x + m.b * y + m.tx);
            sy += w * (m.c * x + m.d * y + m.ty);
        }
        out[2 * v] = sx;
        out[2 * v + 1] = sy;
    }
}

}  // namespace agentspeed::character
