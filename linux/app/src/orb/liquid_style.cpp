#include "orb/liquid_style.hpp"

#include <algorithm>

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kGlass = 0.455;  // the inner glass, which clips the liquid
constexpr double kIdleLevel = 0.08;
constexpr double kRippleLife = 2.2;
constexpr int kMaxDrops = 3, kMaxRipples = 4, kGoldMotes = 22;
constexpr std::size_t kMotes = 72, kPops = 16, kSplashes = 40, kSteam = 12;
const Rgba kGoldColor = Rgba::hex(0xFACC15);

std::array<float, 4> vec(Rgba c, double alpha) { return {c.r, c.g, c.b, static_cast<float>(std::clamp(alpha, 0.0, 1.0))}; }
std::array<float, 4> vec(double a, double b, double c, double d) {
    return {static_cast<float>(a), static_cast<float>(b), static_cast<float>(c), static_cast<float>(d)};
}

// Mean surface (y down): c_y + 0.455 S − 0.91 S f.
double meanSurface(double s, double level) { return kGlass * s - 2 * kGlass * s * level; }

double wave(double x, double s, double amplitude, double chop, const double* phases) {
    const double a = amplitude * s;
    double y = a * (0.55 * std::sin(x / (0.9 * s) * 2 * kPi + phases[0]) + 0.30 * std::sin(x / (0.47 * s) * 2 * kPi - phases[1]) +
                    0.15 * std::sin(x / (0.23 * s) * 2 * kPi + phases[2]));
    y += chop * s * std::sin(x / (0.11 * s) * 2 * kPi + phases[3]) * (0.7 + 0.3 * std::sin(x / (0.05 * s) + 1.7 * phases[3]));
    return y;
}

double ripple(double distance, double tau, double amplitude, double s) {
    const double u = distance - 0.42 * s * tau;
    const double envelope = std::exp(-tau / 0.75) * std::exp(-std::pow(u / (0.075 * s), 2));
    return -amplitude * s * envelope * std::cos(2 * kPi * u / (0.1 * s));
}

double range(led::Random& random, double lo, double hi) { return lo + (hi - lo) * random.next(); }

// Removes the elements `keep` rejects, swapping in the last one.
template <typename T, typename F>
void update(std::vector<T>& items, F keep) {
    for (std::size_t i = 0; i < items.size();) {
        if (keep(items[i])) {
            ++i;
        } else {
            items[i] = items.back();
            items.pop_back();
        }
    }
}

}  // namespace

void LiquidStyle::Oscillator::step(double target, double dt) {
    if (dt <= 0) return;
    const int count = std::max(1, static_cast<int>(std::ceil(dt * 480)));
    const double h = dt / count;
    for (int i = 0; i < count; ++i) {
        const double acceleration = -omega * omega * (value - target) - 2 * damping * omega * velocity;
        velocity += acceleration * h;
        value += velocity * h;
    }
}

void LiquidStyle::Critical::step(double target, double dt) {
    if (dt <= 0) return;
    const double c1 = value - target;
    const double c2 = velocity + omega * c1;
    const double decay = std::exp(-omega * dt);
    value = target + (c1 + c2 * dt) * decay;
    velocity = (c2 - omega * (c1 + c2 * dt)) * decay;
}

const ReadoutLayout& LiquidStyle::readoutLayout() const {
    static const ReadoutLayout layout = [] {
        ReadoutLayout l;
        l.dotHalo = true;
        return l;
    }();
    return layout;
}

double LiquidStyle::currentLevel() const { return std::clamp(level_.value + surge_.value, 0.04, 0.95); }

// Surface height (points from the center, y down) at x points from the center; mirrors liquidWaveY in the shader.
double LiquidStyle::surfaceOffset(double x, double s, double time) const {
    double y = meanSurface(s, currentLevel()) + wave(x, s, amplitude_.value, chop_.value, phases_);
    for (const auto& r : ripples_) y += ripple(std::abs(x - r.x), time - r.start, r.amplitude, s);
    return y;
}

void LiquidStyle::snap(const Targets& t) {
    level_.value = levelEased_.value = t.level;
    level_.velocity = levelEased_.velocity = 0;
    surge_.value = surge_.velocity = 0;
    amplitude_.value = t.amplitude;
    amplitude_.velocity = 0;
    chop_.value = t.chop;
    chop_.velocity = 0;
    motes_.clear();
    pops_.clear();
    splashes_.clear();
    steam_.clear();
    drops_.clear();
    ripples_.clear();
    pendingPeak_.reset();
    pendingDrops_ = 0;
    goldStart_ = -1e9;
}

void LiquidStyle::pulse(double, double strength, double heat) {
    if (!allowsDrops_) return;
    pendingDrops_ = std::min(kMaxDrops, pendingDrops_ + 1);
    pendingStrength_ = strength;
    pendingHeat_ = heat;
}

void LiquidStyle::step(const Frame& f) {
    const double dt = f.dt, s = f.s;
    const double h = f.heat * f.activity, o = f.overdrive;
    const bool active = !f.idle();
    const int level = f.effects.level;
    const bool still = level == 0 || s < 64;
    const bool boiling = level >= 2 && f.tier == Tier::Redline && active && !still;
    allowsDrops_ = level >= 1 && !still;

    // Level f* = 0.08 + 0.74 h + 0.06 o; amplitude (0.004 + 0.02 h + 0.018 o) S (Soft × 0.6, idle 0.002); chop at the
    // redline 0.006 S (0.5 + 0.5 o), halved below S 120.
    Targets t;
    t.level = active ? kIdleLevel + 0.74 * h + 0.06 * o : kIdleLevel;
    t.amplitude = still ? 0 : (active ? 0.004 + 0.02 * h + 0.018 * o : 0.002) * (level == 1 ? 0.6 : 1);
    t.chop = boiling ? 0.006 * (0.5 + 0.5 * o) * (f.small ? 0.5 : 1) : 0;
    targets_ = t;
    if (f.resumed) snap(t);
    if (dt <= 0) return;

    if (still) {
        levelEased_.step(t.level, dt);
        level_.value = levelEased_.value;
        level_.velocity = 0;
    } else {
        level_.step(t.level, dt);
        levelEased_.value = level_.value;
        levelEased_.velocity = 0;
    }
    if (pendingPeak_) {
        if (f.effects.celebrate) {
            goldStart_ = *pendingPeak_;
            if (!still) {
                surge_.velocity += 0.9;
                spawnGold(s);
            }
        }
        pendingPeak_.reset();
    }
    surge_.step(0, dt);
    amplitude_.step(t.amplitude, dt);
    chop_.step(t.chop, dt);
    if (!still) {
        const double rates[4] = {0.25 + 0.5 * h + 0.3 * o, 0.4 + 0.8 * h + 0.4 * o, 0.9 + 1.6 * h, 3.1};
        for (int i = 0; i < 4; ++i) {
            phases_[i] += 2 * kPi * rates[i] * dt;
            if (phases_[i] > 2 * kPi * 100) phases_[i] -= 2 * kPi * 100;
        }
    }
    const double time = f.time;
    update(ripples_, [time](const Ripple& r) { return time - r.start < kRippleLife; });

    if (still) {
        motes_.clear();
        pops_.clear();
        splashes_.clear();
        steam_.clear();
        drops_.clear();
        pendingDrops_ = 0;
        moteCarry_ = steamCarry_ = 0;
    } else {
        stepParticles(f, dt, s, h, o, level, boiling);
    }

    const bool signOfLife = f.signOfLife.has_value();
    if (signOfLife && !hadSignOfLife_ && !still) {
        // One cool bubble from the bottom-left of the shallow idle pool; it rises and pops within 1.2 s.
        const double x = -0.1 * s;
        const double bottom = std::sqrt(std::max(0.0, std::pow(kGlass * s, 2) - x * x));
        spawnMote(s, 0.15, 0, x, bottom - 0.01 * s);
    }
    hadSignOfLife_ = signOfLife;
}

void LiquidStyle::stepParticles(const Frame& f, double dt, double s, double h, double o, int level, bool boiling) {
    const bool active = !f.idle();
    // Soft 0.6 + 3 h per s (≤ 6); Standard 1.5 + 12 h (+ 6 + 10 o in the redline tier), pool 32 (16 below S 120); Max × 1.6, 48.
    double rate = 0;
    if (active) rate = level == 1 ? 0.6 + 3 * h : (1.5 + 12 * h + (f.tier == Tier::Redline ? 6 + 10 * o : 0)) * (level == 3 ? 1.6 : 1);
    const std::size_t cap = level <= 1 ? 6 : f.small ? 16 : level == 3 ? 48 : 32;
    moteCarry_ += rate * dt;
    while (moteCarry_ >= 1) {
        moteCarry_ -= 1;
        if (motes_.size() < cap) spawnMote(s, h, o, std::nullopt, std::nullopt);
    }

    const double accelerate = (0.12 + 0.2 * h) * s;
    const double time = f.time;
    std::vector<Pop> newPops;
    std::vector<std::pair<double, double>> splashAt;
    update(motes_, [&](Mote& m) {
        m.vy -= accelerate * dt;
        m.y += m.vy * dt;
        m.phase += 2 * kPi * 2.3 * dt;
        const double x = m.x + std::sin(m.phase) * 0.006 * s;
        const double surface = surfaceOffset(x, s, time);
        if (m.y - m.size < surface) return true;
        newPops.push_back({x, surface, 0, m.size});
        if (boiling && !m.gold && random_.next() < 0.55) splashAt.emplace_back(x, surface);
        return false;
    });
    for (const auto& p : newPops) {
        if (pops_.size() < kPops) pops_.push_back(p);
    }
    for (const auto& [x, y] : splashAt) spawnSplash(x, y, 1 + static_cast<int>(random_.next() * 2), 0.5, s);
    update(pops_, [dt](Pop& p) {
        p.age += dt;
        return p.age < 0.25;
    });

    if (boiling && !f.small) {
        steamCarry_ += (2 + 4 * o) * (level == 3 ? 1.6 : 1) * dt;
        while (steamCarry_ >= 1) {
            steamCarry_ -= 1;
            if (steam_.size() >= kSteam) continue;
            steam_.push_back({range(random_, -0.25, 0.25) * s, 0, -range(random_, 0.06, 0.12) * s, 0, range(random_, 0.8, 1.4),
                              range(random_, 0.03, 0.06) * s, random_.next() * 2 * kPi});
        }
    } else {
        steamCarry_ = 0;
    }
    update(steam_, [&](Steam& w) {
        w.age += dt;
        if (w.age >= w.life) return false;
        w.y += w.vy * dt;
        w.x += std::sin(w.phase + w.age * 3) * 0.02 * s * dt;
        return true;
    });

    // Pulse droplets fall in; at impact a ripple, splashes and bubbles.
    while (pendingDrops_ > 0) {
        --pendingDrops_;
        if (static_cast<int>(drops_.size()) >= kMaxDrops) continue;
        const double x = (random_.next() - 0.5) * 0.42 * s;
        const double surface = surfaceOffset(x, s, time);
        // 0.16 S above the surface, never above c − 0.414 S nor below c − 0.3 S.
        const double start = std::min(std::max(surface - 0.16 * s, -0.414 * s), -0.3 * s);
        drops_.push_back({x, start, 0, pendingHeat_, pendingStrength_});
    }
    std::vector<Drop> landed;
    update(drops_, [&](Drop& d) {
        d.vy += 2.6 * s * dt;
        d.y += d.vy * dt;
        if (d.y < surfaceOffset(d.x, s, time)) return true;
        landed.push_back(d);
        return false;
    });
    for (const auto& d : landed) {
        const double surface = surfaceOffset(d.x, s, time);
        if (static_cast<int>(ripples_.size()) >= kMaxRipples) ripples_.erase(ripples_.begin());
        ripples_.push_back({d.x, time, 0.022 * (0.6 + 0.6 * d.strength)});
        if (level >= 2) spawnSplash(d.x, surface, 3 + static_cast<int>(std::lround(3 * d.strength)), 1, s);
        for (int i = 0; i < (level >= 2 ? 6 : 2); ++i) {
            spawnMote(s, h, o, d.x + range(random_, -0.03, 0.03) * s, surface + range(random_, 0.02, 0.08) * s);
        }
    }
    update(splashes_, [&](Splash& d) {
        d.age += dt;
        if (d.age >= d.life) return false;
        d.vy += 2.2 * s * dt;
        d.x += d.vx * dt;
        d.y += d.vy * dt;
        return !(d.vy > 0 && d.y > surfaceOffset(d.x, s, time));
    });
}

void LiquidStyle::spawnMote(double s, double h, double o, std::optional<double> x, std::optional<double> y) {
    if (motes_.size() >= kMotes) return;
    const double radius = kGlass * s;
    const double px = x.value_or(range(random_, -0.6, 0.6) * radius);
    const double bottom = std::sqrt(std::max(0.0, radius * radius - px * px));
    const double py = y.value_or(bottom - range(random_, 0.02, 0.06) * s);
    motes_.push_back({px, py, -(0.08 + 0.35 * h + 0.25 * o) * s * range(random_, 0.7, 1.3),
                      std::max(0.9, (0.006 + 0.01 * h) * s * range(random_, 0.6, 1.4)), random_.next() * 2 * kPi, false});
}

void LiquidStyle::spawnGold(double s) {
    for (int i = 0; i < kGoldMotes && motes_.size() < kMotes; ++i) {
        motes_.push_back({range(random_, -0.3, 0.3) * s, kGlass * s * 0.85 - range(random_, 0, 0.1) * s, -range(random_, 0.5, 0.9) * s,
                          range(random_, 0.008, 0.016) * s, random_.next() * 2 * kPi, true});
    }
}

void LiquidStyle::spawnSplash(double x, double y, int count, double strength, double s) {
    for (int i = 0; i < count && splashes_.size() < kSplashes; ++i) {
        splashes_.push_back({x, y - 0.004 * s, range(random_, -0.25, 0.25) * s, -range(random_, 0.35, 0.7) * s * (0.6 + 0.6 * strength), 0,
                             range(random_, 0.3, 0.6), range(random_, 0.006, 0.011) * s});
    }
}

bool LiquidStyle::isBusy(const Frame& f) const {
    return !motes_.empty() || !pops_.empty() || !splashes_.empty() || !steam_.empty() || !drops_.empty() || !ripples_.empty() ||
           pendingDrops_ > 0 || pendingPeak_ || !level_.settled(targets_.level) || !levelEased_.settled(targets_.level) ||
           !surge_.settled(0) || !amplitude_.settled(targets_.amplitude, 0.00005) || !chop_.settled(targets_.chop, 0.00005) ||
           f.time - goldStart_ < 0.95;
}

int LiquidStyle::ambientFps(const Frame& f) const {
    if (f.idle()) return 0;
    if (f.effects.level == 1) return 30;
    return f.tier >= Tier::Overdrive ? 60 : 30;
}

void LiquidStyle::build(Scene& scene, const Frame& f) {
    const double s = f.s;
    const QPointF center = f.center;
    const double h = f.heat * f.activity, o = f.overdrive;
    const bool active = !f.idle();
    const bool dark = f.dark;
    const int level = f.effects.level;
    const bool valueOnly = s < 64;
    const bool still = level == 0 || valueOnly;
    const double glowScale = level == 3 ? 1.25 : 1;
    const double radius = kGlass * s;
    const bool boiling = f.tier == Tier::Redline && active;

    const Rgba liquid = dark ? heatColor(h) : heatColor(h).mix(Rgba::white(), 0.12);
    const Rgba slate = dark ? heatColor(0) : heatColor(0).mix(Rgba::white(), 0.12);
    const Rgba tint = active ? liquid : slate;
    const double bodyAlpha = active ? (dark ? 0.46 + 0.10 * h : 0.80) : (dark ? 0.40 : 0.55);
    const double meanY = center.y() + meanSurface(s, currentLevel());

    auto& u = scene.liquidField;
    u = {};
    u.frame = vec(center.x(), center.y(), s, radius);
    u.surface = vec(meanY, valueOnly ? 0 : amplitude_.value * s, valueOnly ? 0 : chop_.value * s, !f.small && !valueOnly ? 1 : 0);
    u.phases = vec(phases_[0], phases_[1], phases_[2], phases_[3]);
    for (std::size_t i = 0; i < ripples_.size() && i < 4 && !valueOnly; ++i) {
        const auto& r = ripples_[i];
        const double tau = f.time - r.start;
        u.ripples[i] = vec(center.x() + r.x, 0.42 * s * tau, r.amplitude * s * std::exp(-tau / 0.75), 1);
    }
    u.liquid = vec(tint, bodyAlpha);
    u.deep = vec(dark ? tint.darkened(0.62) : tint.darkened(0.14), bodyAlpha + 0.2);
    u.back = vec(dark ? tint.darkened(0.25) : tint.darkened(0.08), std::min(1.0, bodyAlpha) * 0.55);
    if (active && !valueOnly) u.spill = vec(liquid, (dark ? 0.16 : 0.10) * (0.4 + 0.6 * h));
    // The inner glow, kept below the readout.
    if (active) {
        const Rgba glow = dark ? liquid.mixLinear(kWhiteHot, 0.25 + 0.45 * o) : liquid;
        u.glow = vec(glow, (dark ? 0.26 + 0.2 * o : 0.10) * (0.3 + 0.7 * h) * glowScale);
        u.glowGeom = vec(center.x(), std::max(meanY + 0.08 * s, center.y() + 0.38 * s), 0.32 * s, dark ? 1 : 0);
    }
    u.shade = dark ? vec(0, 0, 0, 0.5) : vec(Rgba::hex(0x283042), 0.14);
    if (active && level >= 1 && !still && !f.small) {
        const Rgba current = dark ? liquid.mixLinear(kWhiteHot, 0.35 + 0.4 * o) : Rgba::white();
        u.current = vec(current, (dark ? 0.10 : 0.22) * (0.4 + 0.6 * h));
        u.currentGeom = vec(0, 0.006 * s, phases_[0], dark ? 1 : 0);
    }
    const double goldAge = f.time - goldStart_;
    if (!valueOnly && goldAge >= 0 && goldAge < 0.9) {
        const double bottom = center.y() + radius;
        const double bandY = bottom - (bottom - meanY) * led::easeOut(goldAge / 0.9);
        u.gold = vec(bandY, 0.45 * (1 - goldAge / 0.9), 0.06 * s, dark ? 1 : 0);
    }
    const double bodyTop = std::min(meanY, center.y() + 0.2 * s) - 0.04 * s;
    u.flags = vec(dark ? 1 : 0, dark && active ? 1 : 0, bodyTop, center.y() + radius);

    // The meniscus flashes with a tier change.
    Rgba meniscus;
    double meniscusAlpha;
    if (active) {
        meniscus = dark ? liquid.mixLinear(kWhiteHot, 0.45 + 0.5 * o) : heatColor(h).darkened(0.25);
        meniscusAlpha = 0.95;
    } else {
        meniscus = dark ? Rgba::white() : slate.darkened(0.25);
        meniscusAlpha = dark ? 0.28 : 0.5;
    }
    const double flash = f.flash + (still && boiling ? 0.3 : 0);
    if (flash > 0) {
        meniscus = meniscus.mix(Rgba::white(), 0.25 * std::min(1.0, flash));
        meniscusAlpha = std::min(1.0, meniscusAlpha * (1 + 0.25 * flash));
    }
    const double meniscusWidth = std::max(1.0, (0.008 + 0.004 * h) * s);
    u.meniscus = vec(meniscus, meniscusAlpha);
    if (dark && active) {
        const double sigma = (0.015 + 0.015 * h) * s * (level == 3 ? 1.3 : 1);
        const double strength = 0.9 * std::min(1.0, meniscusWidth / (sigma * 2.507)) * (still && boiling ? 1.4 : 1);
        u.meniscusGlow = vec(liquid, std::min(1.0, strength * 1.6));
        u.meniscusGeom = vec(meniscusWidth, sigma, 1, 0);
    } else {
        u.meniscusGeom = vec(meniscusWidth, 1, 0, 0);
    }
    scene.addLiquid(0);

    if (!still) {
        scene.clipRadius = radius;
        addMotes(scene, f, h, liquid);
        addSurfaceParticles(scene, f, liquid);
        scene.clipRadius = 0;
    }
    scene.addLiquid(1);
    addGlass(scene, f, h, active, liquid, glowScale);
}

void LiquidStyle::addMotes(Scene& scene, const Frame& f, double h, Rgba liquid) const {
    if (motes_.empty()) return;
    const double s = f.s;
    const bool dark = f.dark;
    const double t = std::clamp((h - 0.35) / (0.9 - 0.35), 0.0, 1.0);
    const double heatHot = t * t * (3 - 2 * t);
    const Rgba hotColor = dark ? liquid.mixLinear(kWhiteHot, 0.9) : Rgba::white();
    const auto at = [&](const Mote& m) { return QPointF(f.center.x() + m.x + std::sin(m.phase) * 0.006 * s, f.center.y() + m.y); };
    // Glass bubbles first (normal blending), then the luminous motes (additive in dark).
    for (const auto& m : motes_) {
        const double hot = m.gold ? 1 : heatHot;
        if (hot >= 1) continue;
        const QPointF c = at(m);
        const double clear = f.readoutZoneFade(c);
        scene.add(Instance::ring(c, m.size, std::max(0.6, m.size * 0.28), Rgba::white((dark ? 0.5f : 0.85f)).opacity((1 - hot) * clear)),
                  Blend::Over);
        scene.add(Instance::circle(QPointF(c.x() - m.size * 0.35, c.y() - m.size * 0.35), std::max(0.4, m.size * 0.28),
                                   Rgba::white(dark ? 0.6f : 0.95f).opacity((1 - hot) * clear)),
                  Blend::Over);
    }
    for (const auto& m : motes_) {
        const double hot = m.gold ? 1 : heatHot;
        if (hot <= 0) continue;
        const QPointF c = at(m);
        const double clear = f.readoutZoneFade(c);
        const Rgba color = m.gold ? kGoldColor : hotColor;
        const double peak = (dark ? 0.95 : 0.9) * hot * clear, rim = (dark ? 0.4 : 0.45) * hot * clear;
        scene.add(Instance::circle(c, m.size * 0.7, color.opacity(peak), 1, m.size * 0.9, 1).radialGradient(color.opacity(rim), QPointF(), m.size * 0.7),
                  f.glowBlend());
    }
}

void LiquidStyle::addSurfaceParticles(Scene& scene, const Frame& f, Rgba liquid) const {
    const double s = f.s;
    const bool dark = f.dark;
    const auto at = [&](double x, double y) { return QPointF(f.center.x() + x, f.center.y() + y); };
    const Rgba popColor = dark ? kWhiteHot : Rgba::white();
    for (const auto& p : pops_) {
        const double u = p.age / 0.25;
        scene.add(Instance::ring(at(p.x, p.y), p.size * (1 + 1.5 * u), 0.8, popColor.opacity(0.6 * (1 - u))), Blend::Over);
    }
    const Rgba splashColor = dark ? liquid.mixLinear(kWhiteHot, 0.5) : liquid.darkened(0.1);
    for (const auto& d : splashes_) {
        const double u = d.age / d.life;
        scene.add(Instance::circle(at(d.x, d.y), d.size, splashColor.opacity(0.9 * (1 - u * 0.6))), f.glowBlend());
    }
    const double mean = meanSurface(s, currentLevel());
    const Rgba steamColor = dark ? kWhiteHot : Rgba::white();
    for (const auto& w : steam_) {
        const double u = w.age / w.life;
        const double radius = w.size * (1 + u);
        const double a = std::sin(kPi * u) * (dark ? 0.12 : 0.18);
        scene.add(Instance::circle(at(w.x, mean + w.y), radius, steamColor.opacity(a)).radialGradient(steamColor.opacity(0), QPointF(), radius),
                  f.glowBlend());
    }
    for (const auto& d : drops_) {
        const double heat = std::max(0.15, d.heat);
        const Rgba color = dark ? heatColor(heat).mixLinear(kWhiteHot, 0.3) : f.labelColor(heat);
        const double radius = (0.012 + 0.006 * d.strength) * s;
        scene.add(Instance::blade(at(d.x, d.y), -90, radius * 2.2, 0, radius, radius * 0.12, radius, color.opacity(0.95)), f.glowBlend());
    }
}

void LiquidStyle::addGlass(Scene& scene, const Frame& f, double h, bool active, Rgba liquid, double glowScale) const {
    const double s = f.s;
    const bool dark = f.dark;
    const QPointF center = f.center;
    scene.add(Instance::ring(center, kGlass * s, std::max(1.0, 0.006 * s), dark ? Rgba::white(0.09f) : Rgba::black(0.06f)), Blend::Over);
    if (s < 64) return;
    if (active) {
        const Rgba caustic = liquid.opacity((dark ? 0.35 : 0.3) * (0.3 + 0.7 * h) * std::min(1.0, glowScale));
        scene.add(Instance::bandArc(center, 0.475 * s, 0.02 * s, 55, 70, caustic, caustic,
                                    Instance::Band{.core = 0.55, .glow = Instance::glowSigma(0.008 * s), .glowAlpha = 0.7}),
                  f.glowBlend());
    }
    const Rgba highlight = dark ? Rgba::white(0.13f) : Rgba::white(0.75f);
    scene.add(Instance::ellipse(QPointF(center.x() - 0.17 * s, center.y() - 0.22 * s), 0.2 * s, 0.12 * s, -34, highlight)
                  .radialGradient(highlight.opacity(0), QPointF(), 0.24 * s),
              Blend::Over);
    const Rgba glint = dark ? Rgba::white(0.22f) : Rgba::white(0.95f);
    scene.add(Instance::bandArc(center, 0.42 * s, std::max(1.0, 0.012 * s), 200, 36, glint, glint, Instance::Band{.roundCaps = true}),
              Blend::Over);
}

// Split by the surface: above it in the normal tokens, below it in colors that hold on the lit liquid (DESIGN 2a.3,
// layer 10), so the title never sits in its own color on its own color.
void LiquidStyle::buildReadout(Scene& scene, const Frame& f, const ReadoutContent& content, const MeasureText& measure) {
    ReadoutPass above;
    above.clip = 2;
    orb::buildReadout(scene, f, content, measure, readoutLayout(), linePlacement(), above);
    ReadoutPass below;
    below.clip = 3;
    below.pill = false;
    below.dots = false;
    if (f.dark) {
        below.palette.value = Rgba::white(0.97f);
        below.palette.unit = Rgba::white(0.80f);
        below.palette.title = Rgba::white(0.94f);
        below.palette.lineNumber = Rgba::white(0.94f);
        below.palette.lineSecondary = Rgba::white(0.72f);
        below.palette.shadow = Rgba::black(0.55f);
        below.palette.shadowRadius = 0.03 * f.s;
    } else {
        const Rgba ink = Rgba::hex(0x0B0D12);
        below.palette.value = ink.opacity(0.94);
        below.palette.unit = ink.opacity(0.72);
        below.palette.title = heatColor(std::max(f.heat * f.activity, 0.05)).darkened(0.55);
        below.palette.lineNumber = ink.opacity(0.88);
        below.palette.lineSecondary = ink.opacity(0.62);
        below.palette.valueGlow = false;
    }
    orb::buildReadout(scene, f, content, measure, readoutLayout(), linePlacement(), below);
}

}  // namespace orb
