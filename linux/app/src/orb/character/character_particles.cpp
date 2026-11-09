#include "orb/character/character_particles.hpp"

#include <algorithm>
#include <cmath>
#include <map>

namespace orb {

namespace {

using Look = CharacterParticles::Look;
using Origin = CharacterParticles::Origin;
using K = CharacterParticles::Kind;

const std::map<std::string, K>& kinds() {
    static const std::map<std::string, K> table = {
        {"bubbles", {Look::Ring, 3, 16, 0, 1, false, Origin::Body, -15, 15, -90, -40, 20, 14, 4, 6, 14, 0.4, 1.6, 2.8, 0, 0, {nullptr, nullptr}, false}},
        {"sparkles", {Look::Star, 4, 26, 0, 1.5, false, Origin::Body, -12, 12, -12, 12, 0, 0, 0, 8, 16, 0, 0.8, 1.6, -40, 40, {nullptr, nullptr}, false}},
        {"stars", {Look::Star, 3, 18, 0, 1.4, false, Origin::Body, -10, 10, -30, -10, 10, 6, 2, 10, 18, 0, 1.4, 2.4, -90, 90, {nullptr, nullptr}, false}},
        {"notes", {Look::Glyph, 3, 14, 0, 1, false, Origin::Body, -10, 10, -60, -30, 30, 20, 3, 18, 28, 0, 1.8, 2.8, 0, 0, {"♪", "♫"}, false}},
        {"embers", {Look::Dot, 0, 30, 0.2, 1.3, false, Origin::Feet, -20, 20, -160, -80, 25, 18, 6, 2, 4, -0.5, 0.7, 1.5, 0, 0, {nullptr, nullptr}, true}},
        {"foxfire", {Look::Wisp, 2, 10, 0, 1, false, Origin::Body, -8, 8, -45, -20, 8, 16, 2.5, 7, 12, -0.3, 1.4, 2.4, 0, 0, {nullptr, nullptr}, false}},
        {"confetti", {Look::Chip, 0, 40, 0.4, 1.5, false, Origin::Top, -40, 40, 60, 140, 60, 25, 5, 5, 9, 0, 1.6, 2.6, -400, 400, {nullptr, nullptr}, false}},
        {"zzz", {Look::Glyph, 1.2, 0, 0, 1, true, Origin::Head, 10, 22, -30, -18, 0, 6, 1.5, 14, 18, 0.7, 2.2, 3, -10, 10, {"Z", "z"}, false}},
        {"pages", {Look::Chip, 0, 20, 0.2, 1.4, false, Origin::Body, 120, 220, -40, 30, 0, 18, 5, 10, 16, 0, 1.3, 2.4, -260, 260, {nullptr, nullptr}, false}},
        {"blossoms", {Look::Chip, 2, 24, 0, 1.5, false, Origin::Body, 100, 200, -30, 30, 0, 18, 5, 8, 13, 0, 1.4, 2.6, -200, 200, {nullptr, nullptr}, false}},
        {"feathers", {Look::Feather, 3, 18, 0, 1.4, false, Origin::Top, -20, 20, 25, 50, 40, 30, 1.8, 14, 24, 0, 2, 3.2, -60, 60, {nullptr, nullptr}, false}},
        {"snow", {Look::Dot, 6, 20, 0, 1, false, Origin::Top, -10, 10, 30, 60, 30, 12, 2, 3, 6, 0, 2.5, 4, 0, 0, {nullptr, nullptr}, false}},
    };
    return table;
}

std::array<float, 3> rgb(const std::string& hex) {
    const std::string digits = !hex.empty() && hex[0] == '#' ? hex.substr(1) : hex;
    if (digits.size() != 6) return {0.85f, 0.47f, 0.34f};
    unsigned v = 0;
    for (char c : digits) {
        const int d = c >= '0' && c <= '9' ? c - '0' : (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : -1;
        if (d < 0) return {0.85f, 0.47f, 0.34f};
        v = v * 16 + static_cast<unsigned>(d);
    }
    return {static_cast<float>((v >> 16) & 0xFF) / 255, static_cast<float>((v >> 8) & 0xFF) / 255, static_cast<float>(v & 0xFF) / 255};
}

std::vector<std::array<float, 3>> palette(const std::string& name, std::array<float, 3> a) {
    const std::array<float, 3> light = {0.45f * a[0] + 0.55f, 0.45f * a[1] + 0.55f, 0.45f * a[2] + 0.55f};
    if (name == "bubbles") return {{0.75f, 0.9f, 1}};
    if (name == "sparkles") return {light, {1, 0.97f, 0.85f}};
    if (name == "stars") return {light, {1, 0.9f, 0.55f}};
    if (name == "notes" || name == "zzz") return {light};
    if (name == "embers") return {{1, 0.55f, 0.2f}, {1, 0.8f, 0.35f}};
    if (name == "foxfire") return {{0.45f, 0.72f, 1}};
    if (name == "confetti") return {a, {1, 0.48f, 0.72f}, {1, 0.83f, 0.35f}, {0.48f, 0.88f, 0.76f}};
    if (name == "pages") return {{0.98f, 0.97f, 0.93f}};
    if (name == "blossoms") return {{1, 0.86f, 0.92f}, {0.86f, 0.9f, 1}, {1, 1, 1}};
    if (name == "feathers") return {a, light};
    if (name == "snow") return {{1, 1, 1}};
    return {a};
}

}  // namespace

CharacterParticles::CharacterParticles() : rng_(11) { items_.reserve(kLimit); }

void CharacterParticles::clear() {
    items_.clear();
    carry_ = 0;
}

void CharacterParticles::use(const std::string& packID, const std::optional<std::string>& particles, const std::string& accent,
                             bool hasSprite) {
    if (packID == packID_) return;
    clear();
    std::string wanted = particles.value_or("petals");
    if (!kinds().count(wanted)) wanted = "petals";
    if (wanted == "petals" && !hasSprite) wanted = "sparkles";
    if (wanted == "petals") kind_.reset();
    else kind_ = kinds().at(wanted);
    colors_ = palette(wanted, rgb(accent));
    packID_ = packID;
}

bool CharacterParticles::isLively() const {
    if (isPetals()) return !items_.empty();
    return std::any_of(items_.begin(), items_.end(), [](const Particle& p) { return p.burst; });
}

void CharacterParticles::update(double dt, double intensity, std::array<double, 4> area, double unit, double rate) {
    if (!kind_) {
        updatePetals(dt, intensity, area, unit, rate);
        return;
    }
    const K& k = *kind_;
    const double perSecond = k.inverse ? k.base * std::pow(1 - std::clamp(intensity, 0.0, 1.0), 2)
                                       : k.base + k.gain * std::pow(std::max(0.0, intensity - k.floor), k.power);
    carry_ += perSecond * rate * dt;
    while (carry_ >= 1) {
        carry_ -= 1;
        const Particle p = spawn(k, intensity, area, unit);
        if (items_.size() < kLimit) items_.push_back(p);
    }
    const double amplitude = k.wobbleAmplitude * unit, frequency = k.wobbleFrequency;
    std::size_t kept = 0;
    for (auto& p : items_) {
        p.age += dt;
        if (p.age >= p.life) continue;
        p.x += (p.vx + amplitude * std::sin(p.age * frequency + p.phase)) * dt;
        p.y += p.vy * dt;
        p.rotation += p.spin * dt;
        items_[kept++] = p;
    }
    items_.resize(kept);
}

CharacterParticles::Particle CharacterParticles::spawn(const Kind& k, double intensity, std::array<double, 4> area, double unit,
                                                       const double* at) {
    const auto [x0, y0, x1, y1] = area;
    const double w = x1 - x0, h = y1 - y0;
    double x, y;
    if (at) {
        x = at[0];
        y = at[1];
    } else {
        switch (k.origin) {
            case Origin::Top:
                x = rng_.uniform(x0, x1);
                y = y0 - 0.05 * h + rng_.uniform(0, 0.25 * h);
                break;
            case Origin::Head:
                x = rng_.uniform(x0 + 0.35 * w, x0 + 0.65 * w);
                y = rng_.uniform(y0, y0 + 0.2 * h);
                break;
            case Origin::Feet:
                x = rng_.uniform(x0 + 0.25 * w, x0 + 0.75 * w);
                y = y1 - rng_.uniform(0, 0.1 * h);
                break;
            case Origin::Body:
            default:
                x = rng_.uniform(x0 + 0.1 * w, x0 + 0.9 * w);
                y = rng_.uniform(y0 + 0.1 * h, y0 + 0.8 * h);
                break;
        }
    }
    Particle p{};
    p.x = x;
    p.y = y;
    p.vx = (rng_.uniform(k.vx0, k.vx1) + k.wind * (0.6 + intensity)) * unit;
    p.vy = rng_.uniform(k.vy0, k.vy1) * unit;
    p.size = rng_.uniform(k.size0, k.size1) * unit;
    p.life = rng_.uniform(k.life0, k.life1);
    p.spin = rng_.uniform(k.spin0, k.spin1);
    p.rotation = rng_.uniform(0, 360);
    p.phase = rng_.uniform(0, 6.28);
    p.front = rng_.random() < 0.55;
    p.color = colors_[static_cast<std::size_t>(rng_.index(static_cast<int>(colors_.size())))];
    p.glyph = k.glyphs[0] ? rng_.index(k.glyphs[1] ? 2 : 1) : 0;
    return p;
}

void CharacterParticles::burst(double x, double y, double unit, int count) {
    if (!kind_) {
        burstPetals(x, y, unit, count);
        return;
    }
    const double at[2] = {x, y};
    for (int i = 0; i < count && items_.size() < kLimit; ++i) {
        Particle p = spawn(*kind_, 0.6, {x, y, x, y}, unit, at);
        const double a = rng_.uniform(0, 2 * 3.14159265358979);
        const double v = rng_.uniform(0.3, 0.8) * 260 * unit;
        p.vx = std::cos(a) * v;
        p.vy = std::sin(a) * v * 0.6 - 40 * unit;
        p.life *= 0.7;
        p.burst = true;
        items_.push_back(p);
    }
}

double CharacterParticles::alpha(const Particle& p) const {
    const double k = p.age / p.life;
    double a = std::min(1.0, k / 0.15) * std::min(1.0, (1 - k) / 0.3);
    if (kind_ && kind_->look == Look::Star) {
        const double s = std::sin(p.age * 9 + p.phase);
        a *= 0.55 + 0.45 * s * s;
    }
    return a;
}

double CharacterParticles::size(const Particle& p) const { return p.size * (1 + (kind_ ? kind_->grow : 0) * p.age / p.life); }

void CharacterParticles::dots(bool front, double opacity, std::vector<Dot>& out) const {
    if (!kind_ || kind_->look == Look::Glyph) return;
    for (const auto& p : items_) {
        if (p.front != front) continue;
        const double a = alpha(p) * opacity, s = size(p);
        const auto c = p.color;
        auto dot = [&](double x, double y, double rx, double ry, std::array<float, 3> color, double alphaValue, double soft) {
            out.push_back({x, y, rx, ry, 0, color, alphaValue, soft});
        };
        switch (kind_->look) {
            case Look::Ring:
                dot(p.x, p.y, s, s, c, 0.22 * a, 0.25);
                dot(p.x - 0.35 * s, p.y - 0.35 * s, 0.25 * s, 0.25 * s, {1, 1, 1}, 0.8 * a, 0.5);
                break;
            case Look::Star:
                dot(p.x, p.y, s, 0.16 * s, c, a, 0.6);
                dot(p.x, p.y, 0.16 * s, s, c, a, 0.6);
                dot(p.x, p.y, 0.6 * s, 0.6 * s, c, 0.35 * a, 1);
                break;
            case Look::Wisp:
                dot(p.x, p.y, s, 1.7 * s, c, 0.45 * a, 1);
                dot(p.x, p.y + 0.2 * s, 0.4 * s, 0.6 * s, {0.9f, 0.97f, 1}, 0.8 * a, 0.6);
                break;
            case Look::Chip: {
                const double flip = std::fabs(std::cos(p.rotation * 3.14159265358979 / 180));
                dot(p.x, p.y, 0.5 * s, 0.5 * s * (0.15 + 0.85 * flip), c, a, 0.15);
                break;
            }
            case Look::Feather:
                dot(p.x, p.y, 0.3 * s, s, c, 0.85 * a, 0.5);
                break;
            case Look::Dot:
                dot(p.x, p.y, s, s, c, a, 0.7);
                break;
            default:
                break;
        }
    }
}

double CharacterParticles::glyphCeiling(double unit) const {
    if (!kind_) return 0;
    return kind_->size1 * std::max(1.0, 1 + kind_->grow) * unit;
}

void CharacterParticles::updatePetals(double dt, double intensity, std::array<double, 4> area, double unit, double rate) {
    carry_ += 34 * std::pow(std::max(0.0, intensity - 0.15), 1.6) * rate * dt;
    while (carry_ >= 1) {
        carry_ -= 1;
        if (items_.size() >= kLimit) continue;
        const auto [x0, y0, x1, y1] = area;
        const double w = x1 - x0, h = y1 - y0;
        const bool fromInside = rng_.random() < 0.6;
        Particle p{};
        p.x = fromInside ? rng_.uniform(x0, x1) : x0 - 0.15 * w;
        p.y = rng_.uniform(y0 + 0.05 * h, y0 + 0.75 * h);
        p.vx = rng_.uniform(120, 260) * (0.6 + intensity) * unit;
        p.vy = rng_.uniform(-40, 30) * unit;
        p.rotation = rng_.uniform(0, 360);
        p.spin = rng_.uniform(-260, 260);
        p.size = rng_.uniform(16, 30) * unit;
        p.life = rng_.uniform(1.3, 2.4);
        p.front = rng_.random() < 0.55;
        p.phase = rng_.uniform(0, 6.28);
        p.color = colors_[0];
        items_.push_back(p);
    }
    std::size_t kept = 0;
    for (auto& p : items_) {
        p.age += dt;
        if (p.age >= p.life) continue;
        p.x += p.vx * dt;
        p.y += (p.vy + 18 * unit * std::sin(p.age * 5 + p.phase)) * dt;
        p.rotation += p.spin * dt;
        items_[kept++] = p;
    }
    items_.resize(kept);
}

void CharacterParticles::burstPetals(double x, double y, double unit, int count) {
    for (int i = 0; i < count && items_.size() < kLimit; ++i) {
        const double a = rng_.uniform(0, 2 * 3.14159265358979);
        const double v = rng_.uniform(0.4, 1.0) * 260 * unit;
        Particle p{};
        p.x = x;
        p.y = y;
        p.rotation = rng_.uniform(0, 360);
        p.spin = rng_.uniform(-300, 300);
        p.size = rng_.uniform(14, 26) * unit;
        p.life = rng_.uniform(0.9, 1.6);
        p.front = rng_.random() < 0.6;
        p.phase = rng_.uniform(0, 6.28);
        p.vx = std::cos(a) * v;
        p.vy = std::sin(a) * v * 0.6 - 60 * unit;
        p.color = colors_[0];
        p.burst = true;
        items_.push_back(p);
    }
}

}  // namespace orb
