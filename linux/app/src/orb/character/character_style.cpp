#include "orb/character/character_style.hpp"

#include <QFontMetricsF>
#include <algorithm>
#include <cmath>

#include "orb/character/character_assets.hpp"
#include "orb/renderer.hpp"

namespace orb {

namespace ac = agentspeed::character;
using ac::HudCard;

namespace {

constexpr double kHeight = 0.86;  // her height × D
constexpr double kFeet = 0.45;    // her feet below the disc center × D
constexpr double kRingRadius = 0.23, kRingSquash = 0.17;
constexpr int kRingLights = 96;
constexpr double kUnitScale = 0.82;

std::uint64_t nextSeed() {
    static std::uint64_t seeds = 7;
    seeds = seeds * 6364136223846793005ULL + 1442695040888963407ULL;
    return seeds >> 33;
}

// Sizes of the speed readout for a disc of D points (CharacterReadoutMetrics).
struct SpeedMetrics {
    double value, unit, title, peak;
    explicit SpeedMetrics(double d)
        : value(std::max(11.0, 0.068 * d)), unit(std::max(7.5, 0.022 * d)), title(std::max(7.5, 0.021 * d)),
          peak(std::max(8.0, 0.022 * d)) {}
    double q() const { return value / 92; }
};

// Sizes of the cards (CharacterCardMetrics).
struct CardMetrics {
    double t, p, padX, padY, rowGap;
    explicit CardMetrics(double d) : t(std::max(8.0, 0.027 * d)), p(1.25 * t), padX(0.8 * t), padY(0.55 * t), rowGap(0.36 * t) {}
    static double rowHeight(double size) { return 0.92 * size; }
    static double baseline(double size) { return 0.82 * size; }
    double glyphGap() const { return 0.24 * t; }
    double cellGap() const { return 0.3 * t; }
};

TextDraw textOf(const QString& text, double size, int weight) {
    TextDraw d;
    d.text = text;
    d.pixelSize = size;
    d.weight = weight;
    return d;
}

double advance(const QString& text, double size, int weight) {
    return text.isEmpty() ? 0 : textAdvance(textOf(text, size, weight));
}

double capHeight(double size, int weight) { return textCapHeight(textOf(QStringLiteral("0"), size, weight)); }

// The ink of the floating speed (CharacterInk).
struct Ink {
    bool dark;
    Rgba outline() const { return dark ? Rgba{0.02f, 0.025f, 0.045f, 0.9f} : Rgba{1, 1, 1, 0.95f}; }
    Rgba shadow() const { return dark ? Rgba{0, 0, 0.02f, 1} : Rgba{1, 1, 1, 1}; }
    double shadowAlpha() const { return 0.45; }
    static double width(double size) { return size * 0.06; }
    Rgba electric() const { return dark ? Rgba{0.55f, 0.82f, 1, 1} : Rgba{0.25f, 0.5f, 1, 1}; }
};

// A body of the readout laid out around `center`, scaled by `scale` and turned by `spin` (CharacterBodyPainter).
struct Pen {
    QPointF center;
    double scale, spin, alpha;
    QPointF point(double ox, double oy) const {
        const double c = std::cos(spin), s = std::sin(spin), x = ox * scale, y = oy * scale;
        return {center.x() + x * c - y * s, center.y() + x * s + y * c};
    }
    // A text centered at `ox` with its baseline at `oy` (offsets before the scale and spin).
    TextDraw text(const QString& string, double size, int weight, double ox, double oy, Rgba color) const {
        TextDraw d = textOf(string, size, weight);
        d.baselineCenter = QPointF(center.x() + ox, center.y() + oy);
        d.color = color.opacity(alpha);
        d.pivot = center;
        d.scale = scale;
        d.rotation = spin;
        return d;
    }
    // A text starting at `ox` (its left edge).
    TextDraw leading(const QString& string, double size, int weight, double ox, double oy, Rgba color) const {
        return text(string, size, weight, ox + advance(string, size, weight) / 2, oy, color);
    }
};

std::pair<double, double> chipHalf(const QString& string, double size) {
    return {advance(string, size, 600) / 2 + 0.7 * size, capHeight(size, 600) / 2 + 0.55 * size};
}

// A glass chip centered at the local offset `oy`: rim, fill, an optional leading dot, then the text.
double chip(Scene& scene, const Pen& pen, bool dark, const QString& string, double size, double oy, Rgba textColor, Rgba rim,
            std::optional<Rgba> dot = std::nullopt) {
    if (string.isEmpty()) return 0;
    const double cap = capHeight(size, 600);
    const double dotSpace = dot ? size * 0.85 : 0;
    const double hw = (advance(string, size, 600) + dotSpace) / 2 + 0.7 * size, hh = cap / 2 + 0.55 * size;
    const Rgba fill = dark ? Rgba::hex(0x10121A, 0.6f) : Rgba::white(0.78f);
    const QPointF c = pen.point(0, oy);
    const double s = pen.scale;
    scene.add(Instance::roundedRect(c, (hw + 0.6) * s, (hh + 0.6) * s, pen.spin, (hh + 0.6) * s, rim.opacity(pen.alpha)), Blend::Over);
    scene.add(Instance::roundedRect(c, hw * s, hh * s, pen.spin, hh * s, fill.opacity(pen.alpha)), Blend::Over);
    double textX = 0;
    if (dot) {
        const double dx = -hw + 0.7 * size + size * 0.28;
        scene.add(Instance::circle(pen.point(dx, oy), size * 0.28 * s, dot->opacity(pen.alpha)), Blend::Over);
        textX = dotSpace / 2;
    }
    scene.add(pen.text(string, size, 600, textX, oy + cap / 2, textColor));
    return hh * 2;
}

Rgba accentOf(const std::shared_ptr<const ac::Pack>& pack) {
    if (!pack) return Rgba{0.85f, 0.47f, 0.34f, 1};
    const QColor c(QString::fromStdString(pack->manifest.accent));
    return c.isValid() ? Rgba::from(c) : Rgba{0.85f, 0.47f, 0.34f, 1};
}

}  // namespace

CharacterStyle::CharacterStyle() {
    vertices_.reserve(5 * 4096);
    indices_.reserve(3 * 8192);
    particleDots_.reserve(3 * CharacterParticles::kLimit);
}

const ReadoutLayout& CharacterStyle::readoutLayout() const {
    static const ReadoutLayout layout = [] {
        ReadoutLayout l;
        l.valueSize = 0.13;
        l.valueBaseline = -0.02;
        l.valueMaxWidth = 0.36;
        l.unitSize = 0.036;
        l.unitGap = 0.055;
        l.title = ReadoutLayout::Title::BelowUnit;
        l.titlePlace = 0.048;
        l.titleSize = 0.032;
        l.titleMaxWidth = 0.34;
        l.infoMaxWidth = 0.4;
        l.dots.reset();
        l.pill = -0.16;
        return l;
    }();
    return layout;
}

void CharacterStyle::pulse(double, double strength, double) { hud_.pulse(strength); }

void CharacterStyle::celebrate(double) { hud_.celebratePeak(); }

CharacterStyle::Geometry CharacterStyle::geometry(const Frame& f, const ac::Performer& performer) const {
    const double d = f.s;
    const double height = kHeight * d;
    const double ppp = height / std::max(1.0, performer.idleHeight());
    return {f.center.x(), f.center.y() + kFeet * d, height, ppp, std::max(height / 972, 0.42), performer.bodyOffset().first * ppp, d};
}

std::optional<std::array<double, 4>> CharacterStyle::petalRect(const ac::Rig& rig) {
    for (const auto& part : rig.parts) {
        if (part.tag != "headwear" || part.uv.size() < 2) continue;
        double minX = 1e300, minY = 1e300, maxX = -1e300;
        for (std::size_t i = 0; i + 1 < part.uv.size(); i += 2) {
            minX = std::min(minX, part.uv[i]);
            maxX = std::max(maxX, part.uv[i]);
            minY = std::min(minY, part.uv[i + 1]);
        }
        const double side = maxX - minX;
        const double w = std::max(1, rig.atlasWidth), h = std::max(1, rig.atlasHeight);
        if (side <= 1) return std::nullopt;
        return std::array<double, 4>{minX / w, minY / h, maxX / w, (minY + side) / h};
    }
    return std::nullopt;
}

void CharacterStyle::step(const Frame& f) {
    if (f.dt > 0) comet_ = std::fmod(comet_ + f.dt / (6.0 - 5.3 * f.heat), 1.0);
    if (!content_.pack || f.s < 64) {
        stage_.reset();
        loaded_.clear();
        return;
    }
    const auto& pack = content_.pack;
    const double dt = f.resumed ? 1.0 / 240 : std::clamp(f.dt, 1.0 / 240, 0.1);
    if (!stage_) {
        stage_.emplace(ac::Performer(pack, nextSeed(), 1));
        loaded_ = {{pack->id(), pack}};
    } else if (stage_->current().pack().id() != pack->id() &&
               !(stage_->incoming() && stage_->incoming()->pack().id() == pack->id())) {
        loaded_[pack->id()] = pack;
        stage_->switchTo(ac::Performer(pack, nextSeed(), 1));
    }
    stage_->calm = false;
    stage_->amplitude = f.effects.level == 0 ? 0 : f.effects.level == 1 ? 0.5 : 1;
    stage_->step(dt, content_.level);
    const auto& performer = stage_->current();
    if (loaded_.size() > 1 && !stage_->incoming() && !stage_->outgoing()) {
        loaded_ = {{performer.pack().id(), performer.packPointer()}};
    }

    const auto g = geometry(f, performer);
    const bool particles = f.effects.level >= 2;
    const double rate = f.effects.level >= 3 ? 1.6 : 1.0;
    const auto& rig = performer.activeRig();
    particles_.use(performer.pack().id(), performer.pack().manifest.motion ? performer.pack().manifest.motion->particles : std::nullopt,
                   performer.pack().manifest.accent, petalRect(rig).has_value());
    for (std::size_t i = 0; particles && i < performer.events().size(); ++i) {
        particles_.burst(g.feetX + g.bodyDX, g.feetY - 0.55 * g.height, g.unit);
    }
    const double side = 2 * f.center.x();
    const std::array<double, 4> area = particles_.isPetals()
                                           ? std::array<double, 4>{0.05 * side, 0.1 * side, 0.95 * side, 0.85 * side}
                                           : std::array<double, 4>{g.feetX - 0.625 * g.height, g.feetY - 1.083 * g.height,
                                                                   g.feetX + 0.625 * g.height, g.feetY - 0.042 * g.height};
    particles_.update(dt, particles ? performer.intensity() : 0, area, g.unit, particles ? rate : 0);

    hudTime_ += dt;
    const double heat = content_.unavailable ? 0 : f.heat;
    hud_.emberRate = particles ? rate : 0;
    hud_.update(dt, hudTime_, content_.unavailable ? 0 : content_.value, heat, performer.intensity(), content_.title.toStdString());
    lightning_.update(dt, hud_.odometer().digits(), heat, lightningStrength(f));
    const SpeedMetrics m(f.s);
    const double digitAdvance = advance(QStringLiteral("0"), m.value, 700) * 0.95;
    std::vector<ac::Rgb> agents;
    if (f.s >= 192 && !f.idle()) {
        for (const auto& c : content_.agents) agents.push_back({c.redF() * 255, c.greenF() * 255, c.blueF() * 255});
    }
    if (!content_.modelName.isEmpty()) lastModel_ = content_.modelName;
    layoutCards(dt, f, performer, g);
    const auto& speed = wings_.places()[0];
    const double speedTop = speed.y - wings_.sizes()[0].h / 2;
    hud_.layout(dt, performer.hudLayout(g.feetX, g.feetY, g.ppp, g.unit), agents, digitAdvance, speed.x, speedTop - 1.1 * m.peak);
}

CharacterLightning::Strength CharacterStyle::lightningStrength(const Frame& f) const {
    using S = CharacterLightning::Strength;
    const bool small = f.s < 192;
    switch (f.effects.level) {
        case 0: return S::Off;
        case 1: return S::Soft;
        case 3: return small ? S::Standard : S::Max;
        default: return small ? S::Soft : S::Standard;
    }
}

bool CharacterStyle::isBusy(const Frame&) const {
    if (lightning_.isActive()) return true;
    if (!stage_) return content_.pack != nullptr || CharacterAssets::shared().loading();
    return CharacterAssets::shared().loading() || stage_->outgoing() || stage_->incoming() || stage_->current().fadingPose() ||
           hud_.peakAge() || particles_.isLively() || wings_.isFading();
}

int CharacterStyle::ambientFps(const Frame& f) const {
    if (!stage_) return f.idle() || content_.unavailable ? 0 : 30;
    const bool acting = isBusy(f) || stage_->current().isActing();
    if (f.effects.level == 0) return isBusy(f) ? 30 : 0;
    if (acting) return 60;
    if (content_.unavailable) return 15;
    const auto band = stage_->current().band();
    if (!band || *band == ac::Band::Idle) return 15;
    if (*band == ac::Band::Cruise) return 30;
    return 60;
}

// ---- layout of the cards

QString CharacterStyle::modelText() const {
    if (!content_.modelName.isEmpty()) return content_.modelName;
    if (!lastModel_.isEmpty()) return lastModel_;
    return {};
}

std::array<double, 2> CharacterStyle::cardSize(HudCard card, const Frame& f) const {
    const CardMetrics m(f.s);
    auto plate = [&](double w, double h) { return std::array<double, 2>{w + 2 * m.padX, h + 2 * m.padY}; };
    switch (card) {
        case HudCard::Speed: return {0, 0};
        case HudCard::Model: {
            const QString name = modelText();
            if (name.isEmpty()) return {0, 0};
            return {advance(name, m.t, 600) + 0.62 * m.t + 0.42 * m.t + 2 * 0.85 * m.t, CardMetrics::rowHeight(m.t) + m.t};
        }
        case HudCard::Performance: {
            const auto& in = content_.intel;
            const bool score = content_.readouts.intelligence && !in.number.isEmpty();
            const bool thinking = content_.readouts.thinking && !in.thinking.isEmpty();
            if (!score && !thinking) return {0, 0};
            double w = 0, h = 0;
            if (score) {
                w = advance(in.glyph, kUnitScale * m.p, 600) + m.glyphGap() * 1.25 + advance(in.number, m.p, 600);
                h += CardMetrics::rowHeight(m.p);
            }
            if (thinking) {
                const double share = score ? in.thinkingOpacity : 1;
                const double tw = advance(in.thinkingGlyph, kUnitScale * m.t, 600) + m.glyphGap() + advance(in.thinking, m.t, 600) +
                                  m.cellGap() + advance(in.unit, kUnitScale * m.t, 500);
                w = std::max(w, share * tw);
                h += share * ((score ? m.rowGap : 0) + CardMetrics::rowHeight(m.t));
            }
            return plate(w, h);
        }
        case HudCard::Network: {
            const auto& n = content_.net;
            if (n.downNumber.isEmpty() && n.upNumber.isEmpty()) return {0, 0};
            const double cell = advance(QStringLiteral("888.8"), m.t, 600);
            const double unit = std::max(advance(n.downUnit, kUnitScale * m.t, 500), advance(n.upUnit, kUnitScale * m.t, 500));
            const double w = advance(QStringLiteral("↓"), kUnitScale * m.t, 600) + m.glyphGap() + cell + m.cellGap() +
                             std::max(unit, advance(QStringLiteral("MB/s"), kUnitScale * m.t, 500));
            return plate(w, 2 * CardMetrics::rowHeight(m.t) + m.rowGap);
        }
        case HudCard::Cost: {
            if (content_.costNumber.isEmpty()) return {0, 0};
            double w = advance(content_.costGlyph, kUnitScale * m.p, 600) + m.glyphGap() * 1.25 + advance(content_.costNumber, m.p, 600);
            if (content_.costAtLeast) w += advance(QStringLiteral("≥"), kUnitScale * m.p, 500) + 0.08 * m.p;
            if (!content_.costUnknown) w += m.cellGap() + advance(content_.costUnit, kUnitScale * m.t, 500);
            return plate(w, CardMetrics::rowHeight(m.p));
        }
        case HudCard::Agents: {
            double w = 0;
            int rows = 0;
            for (const auto* line : {&content_.todayLine, &content_.agentsLine}) {
                if (line->isEmpty()) continue;
                w = std::max(w, advance(*line, m.t, 500));
                ++rows;
            }
            if (rows == 0) return {0, 0};
            return plate(w, rows * CardMetrics::rowHeight(m.t) + (rows - 1) * m.rowGap);
        }
    }
    return {0, 0};
}

std::pair<double, double> CharacterStyle::speedBox(const Frame& f) {
    const SpeedMetrics m(f.s);
    const double pad = Ink::width(m.value) + 1;
    const double half = capHeight(m.value, 700) * 0.62 + pad;
    double width = hud_.speedWidth() + 2 * pad;
    if (!content_.staticValue.isEmpty()) width = advance(content_.staticValue, m.value, 700) + 2 * pad;
    width = std::max(width, advance(content_.unit, m.unit, 500));
    double bottom = 50 * m.q() + capHeight(m.unit, 500);
    if (!content_.title.isEmpty()) {
        const auto [cw, ch] = chipHalf(content_.title, m.title);
        width = std::max(width, 2 * cw);
        bottom = 86 * m.q() + m.title + ch;
    }
    speedDrop_ = (bottom - half) / 2;
    return {width, bottom + half};
}

void CharacterStyle::layoutCards(double dt, const Frame& f, const ac::Performer& performer, const Geometry& g) {
    const double d = f.s;
    const double side = 2 * f.center.x();
    const double step = std::max(2.0, std::round(d / 150));
    if (!silhouette_ || silhouette_->step() != step || silhouette_->bands() != static_cast<std::size_t>(std::ceil(side / step))) {
        silhouette_.emplace(0, side, step);
    }
    const auto anchors = performer.trace(*silhouette_, g.feetX, g.feetY, g.ppp, scratch_);
    const auto speed = speedBox(f);
    for (HudCard card : ac::kHudCards) {
        const auto size = card == HudCard::Speed ? std::array<double, 2>{speed.first, speed.second} : cardSize(card, f);
        cardSizes_[static_cast<std::size_t>(ac::index(card))] = {size[0], size[1]};
    }
    auto has = [&](HudCard c) { return cardSizes_[static_cast<std::size_t>(ac::index(c))].w > 0; };
    const auto& r = content_.readouts;
    cardShown_[0] = true;
    cardShown_[1] = r.model && has(HudCard::Model);
    cardShown_[2] = content_.intel.opacity > 0.5 && has(HudCard::Performance);
    cardShown_[3] = r.network && content_.net.opacity > 0.5 && has(HudCard::Network);
    cardShown_[4] = r.cost && content_.costOpacity > 0.5 && has(HudCard::Cost);
    cardShown_[5] = r.agents && d >= 192 && has(HudCard::Agents);
    if (d < 192) {
        int room = 2;
        for (HudCard card : ac::kHudColumn) {
            auto& shown = cardShown_[static_cast<std::size_t>(ac::index(card))];
            if (!shown) continue;
            if (room == 0) shown = false;
            else --room;
        }
    }
    wings_.still = f.effects.level == 0;
    wings_.update(dt, hudTime_, ac::HudFrame({0, 0, side, side}, *silhouette_, d, anchors), cardSizes_, cardShown_);
}

// ---- drawing

QImage CharacterStyle::atlas(const ac::Performer& performer, int pose, double ppp, double now) {
    const auto& placement = performer.placements()[static_cast<std::size_t>(pose)];
    const double needed = CharacterAssets::needed(ppp * placement.scale, dpr_);
    return CharacterAssets::shared().atlas(performer.pack().poses[static_cast<std::size_t>(pose)].atlas, needed, now);
}

void CharacterStyle::drawPose(Scene& scene, const Frame& f, const ac::Performer& performer, int pose,
                              const std::vector<ac::Affine2>& world, const std::vector<double>& opacities, double alpha, double now) {
    if (alpha <= 0.002 || world.empty()) return;
    const auto g = geometry(f, performer);
    const QImage image = atlas(performer, pose, g.ppp, now);
    if (image.isNull()) return;
    const auto& rig = performer.pack().poses[static_cast<std::size_t>(pose)].rig;
    const auto& placement = performer.placements()[static_cast<std::size_t>(pose)];
    const double k = g.ppp * placement.scale;
    const double ox = g.feetX - placement.feetX * k, oy = g.feetY - placement.feetY * k;
    const double aw = std::max(1, rig.atlasWidth), ah = std::max(1, rig.atlasHeight);
    vertices_.clear();
    indices_.clear();
    for (std::size_t i = 0; i < rig.parts.size(); ++i) {
        const double opacity = i < opacities.size() ? opacities[i] : 1;
        if (opacity < 0.002) continue;
        const auto& part = rig.parts[i];
        const auto base = static_cast<std::uint32_t>(vertices_.size() / 5);
        const auto n = static_cast<std::size_t>(part.vertexCount());
        const auto m = static_cast<std::size_t>(part.influences);
        for (std::size_t v = 0; v < n; ++v) {
            const double x = part.vertices[2 * v], y = part.vertices[2 * v + 1];
            double sx = 0, sy = 0;
            for (std::size_t j = 0; j < m; ++j) {
                const double w = part.weights[v * m + j];
                if (w == 0) continue;
                const auto& t = world[part.bones[v * m + j]];
                sx += w * (t.a * x + t.b * y + t.tx);
                sy += w * (t.c * x + t.d * y + t.ty);
            }
            vertices_.insert(vertices_.end(), {static_cast<float>(ox + sx * k), static_cast<float>(oy + sy * k),
                                               static_cast<float>(part.uv[2 * v] / aw), static_cast<float>(part.uv[2 * v + 1] / ah),
                                               static_cast<float>(opacity * alpha * f.alpha)});
        }
        for (auto t : part.triangles) indices_.push_back(base + t);
    }
    scene.addMesh(image, vertices_.data(), static_cast<int>(vertices_.size() / 5), indices_.data(), static_cast<int>(indices_.size()));
}

void CharacterStyle::drawCharacter(Scene& scene, const Frame& f, const ac::Performer& performer, double alpha, double now) {
    const auto g = geometry(f, performer);
    // Keep the pose the band asks for resident too, so a swap finds its atlas ready.
    if (const auto band = performer.band()) {
        const int wanted = performer.poseIndex(*band);
        if (wanted != performer.activePose()) (void)atlas(performer, wanted, g.ppp, now);
    }
    if (const auto fading = performer.fadingPose()) {
        drawPose(scene, f, performer, *fading, performer.fadingWorld(), performer.fadingOpacities(), alpha * (1 - performer.fadeProgress()), now);
    }
    const double fadeIn = performer.fadingPose() ? performer.fadeProgress() : 1;
    drawPose(scene, f, performer, performer.activePose(), performer.world(), performer.opacities(), alpha * fadeIn, now);
}

void CharacterStyle::addDots(Scene& scene, const Frame& f, const std::vector<ac::HudDot>& dots) {
    for (const auto& dot : dots) {
        Rgba color{dot.r, dot.g, dot.b, dot.alpha * static_cast<float>(f.alpha)};
        if (dot.r == 1 && dot.g == 1 && dot.b == 1 && dot.softness < 0.99 && !f.dark) color = Rgba{0, 0, 0, dot.alpha * 0.55f * static_cast<float>(f.alpha)};
        scene.add(Instance::softDot(QPointF(dot.x, dot.y), dot.rx, dot.ry, 0, color, dot.softness),
                  dot.additive && f.dark ? Blend::Additive : Blend::Over);
    }
}

void CharacterStyle::drawParticles(Scene& scene, const Frame& f, bool front, int, double now) {
    if (particles_.items().empty() || !stage_) return;
    const auto* kind = particles_.kind();
    if (!kind) {
        // Petals: the headwear's flower from the atlas, squashing as they tumble.
        const auto& performer = stage_->current();
        const auto rect = petalRect(performer.activeRig());
        const QImage image = rect ? atlas(performer, performer.activePose(), geometry(f, performer).ppp, now) : QImage();
        if (!rect || image.isNull()) return;
        vertices_.clear();
        indices_.clear();
        for (const auto& p : particles_.items()) {
            if (p.front != front) continue;
            const double k = p.age / p.life;
            const double a = std::min(1.0, k / 0.15) * std::min(1.0, (1 - k) / 0.3) * f.alpha;
            const double angle = p.rotation * 3.14159265358979 / 180;
            const double squash = 0.55 + 0.45 * std::fabs(std::cos(angle * 1.7));
            const double hx = p.size / 2, hy = p.size * squash / 2, c = std::cos(angle), s = std::sin(angle);
            const auto base = static_cast<std::uint32_t>(vertices_.size() / 5);
            const double corners[4][2] = {{-1, -1}, {1, -1}, {1, 1}, {-1, 1}};
            for (const auto& cn : corners) {
                const double lx = cn[0] * hx, ly = cn[1] * hy;
                vertices_.insert(vertices_.end(), {static_cast<float>(p.x + lx * c - ly * s), static_cast<float>(p.y + lx * s + ly * c),
                                                   static_cast<float>(cn[0] < 0 ? (*rect)[0] : (*rect)[2]),
                                                   static_cast<float>(cn[1] < 0 ? (*rect)[1] : (*rect)[3]), static_cast<float>(a)});
            }
            indices_.insert(indices_.end(), {base, base + 1, base + 2, base, base + 2, base + 3});
        }
        scene.addMesh(image, vertices_.data(), static_cast<int>(vertices_.size() / 5), indices_.data(), static_cast<int>(indices_.size()));
        return;
    }
    if (kind->look != CharacterParticles::Look::Glyph) {
        particleDots_.clear();
        particles_.dots(front, f.alpha, particleDots_);
        const Blend blend = kind->additive() && f.dark ? Blend::Additive : Blend::Over;
        for (const auto& d : particleDots_) {
            scene.add(Instance::softDot(QPointF(d.x, d.y), d.rx, d.ry, d.rotation, Rgba{d.color[0], d.color[1], d.color[2], static_cast<float>(d.alpha)},
                                        d.softness),
                      blend);
        }
        return;
    }
    const double size = std::ceil(particles_.glyphCeiling(geometry(f, stage_->current()).unit));
    for (const auto& p : particles_.items()) {
        if (p.front != front) continue;
        const double a = particles_.alpha(p) * f.alpha;
        if (a <= 0.01) continue;
        TextDraw d = textOf(QString::fromUtf8(kind->glyphs[static_cast<std::size_t>(p.glyph)]), size, 700);
        const double cap = capHeight(size, 700);
        d.baselineCenter = QPointF(p.x, p.y + cap / 2);
        d.color = Rgba{p.color[0], p.color[1], p.color[2], static_cast<float>(a)};
        d.pivot = QPointF(p.x, p.y);
        d.scale = particles_.size(p) / size;
        d.rotation = p.rotation * 3.14159265358979 / 180;
        // Behind her: drawn before the next batch, her mesh.
        d.layer = front ? -1 : static_cast<int>(scene.batches().size());
        scene.add(d);
    }
}

void CharacterStyle::build(Scene& scene, const Frame& f) {
    drew_ = false;
    if (!stage_ || !loaded_.count(stage_->current().pack().id())) {
        buildRing(scene, f);
        return;
    }
    const double now = f.time;
    addDots(scene, f, hud_.back());
    drawParticles(scene, f, false, 0, now);
    if (const auto& outgoing = stage_->outgoing()) drawCharacter(scene, f, *outgoing, 1 - stage_->outgoingFade(), now);
    drawCharacter(scene, f, stage_->current(), stage_->outgoing() ? stage_->outgoingFade() : 1, now);
    addDots(scene, f, hud_.front());
    drawParticles(scene, f, true, 0, now);
    drawCards(scene, f);
    drawPeak(scene, f);
    CharacterAssets::shared().sweep(now);
    drew_ = true;
}

void CharacterStyle::buildReadout(Scene& scene, const Frame& f, const ReadoutContent& content, const MeasureText& measure) {
    if (drew_) return;
    Style::buildReadout(scene, f, content, measure);
    if (content_.noneInstalled && f.s >= 128 && !content_.emptyLabel.isEmpty()) {
        const auto& l = readoutLayout();
        TextDraw d = textOf(content_.emptyLabel, std::max(7.0, 0.03 * f.s), 500);
        d.baselineCenter = QPointF(f.center.x(), f.center.y() + (l.valueBaseline - l.valueSize - 0.03) * f.s);
        d.color = f.tokens.secondaryText.opacity(f.alpha);
        scene.add(d);
    }
}

// The gauge without a character: lights on a flat ellipse at the feet, filled in the heat colors with a bright comet.
void CharacterStyle::buildRing(Scene& scene, const Frame& f) {
    const double d = f.s;
    const QPointF center(f.center.x(), f.center.y() + kFeet * d);
    const double rx = kRingRadius * d, ry = rx * kRingSquash;
    const double heat = content_.unavailable ? 0 : f.heat;
    const double lit = heat * kRingLights;
    const double size = std::max(0.6, 0.006 * d);
    scene.add(Instance::ellipse(center, rx * 1.15, ry * 1.6, 0, f.labelColor(heat).opacity(0.08 + 0.25 * heat), 0, 0.4 * rx, 1), f.glowBlend());
    for (int i = 0; i < kRingLights; ++i) {
        const double t = static_cast<double>(i) / kRingLights;
        const double angle = 3.14159265358979 / 2 + 2 * 3.14159265358979 * t;
        const double depth = 0.55 + 0.45 * (std::sin(angle) + 1) / 2;
        const QPointF point(center.x() + rx * std::cos(angle), center.y() + ry * std::sin(angle));
        const double offset = std::fmod(t - comet_ + 1.5, 1.0) - 0.5;
        const double glint = std::exp(-std::pow(offset * 18, 2));
        if (i < lit) {
            const Rgba color = (i == static_cast<int>(lit) ? kWhiteHot : f.labelColor(t)).opacity((0.75 + 0.25 * glint) * depth);
            scene.add(Instance::circle(point, size * depth, color, 1, size * 2, 0.5), f.glowBlend());
        } else {
            scene.add(Instance::circle(point, size * 0.75 * depth, f.tokens.unlitSegment.opacity((1.4 + 2 * glint) * depth)), Blend::Over);
        }
    }
}

void CharacterStyle::drawCards(Scene& scene, const Frame& f) {
    const CardMetrics m(f.s);
    const bool dark = f.dark;
    const Rgba glass = dark ? Rgba::hex(0x12141D) : Rgba::white();
    const Rgba fill = glass.opacity(dark ? 0.64 : 0.8);
    const Rgba rim = dark ? Rgba::white(0.15f) : Rgba::black(0.085f);
    const Rgba shadow = Rgba::black(dark ? 0.3f : 0.11f);
    const Rgba sheen = Rgba::white(dark ? 0.1f : 0.75f);
    const Rgba number = (dark ? Rgba::white() : Rgba::hex(0x0B0D12)).opacity(0.9);
    const Rgba secondary = (dark ? Rgba::white() : Rgba::black()).opacity(0.55);
    const Rgba accent = accentOf(stage_->current().packPointer());

    auto plate = [&](const Pen& pen, double w, double h, std::optional<double> corner, std::optional<double> spine) {
        const double s = pen.scale, a = pen.alpha;
        const double hw = w / 2 * s, hh = h / 2 * s;
        const double r = std::min(corner.value_or(0.72 * m.t), h / 2) * s;
        scene.add(Instance::roundedRect(pen.point(0, 0.2 * m.t), hw, hh, pen.spin, r, shadow.opacity(a), 0, 0.9 * m.t * s, 1), Blend::Over);
        scene.add(Instance::roundedRect(pen.center, hw + 0.6, hh + 0.6, pen.spin, r + 0.6, rim.opacity(a)), Blend::Over);
        scene.add(Instance::roundedRect(pen.center, hw, hh, pen.spin, r, fill.opacity(a)), Blend::Over);
        const double inset = r / s + 0.15 * m.t;
        if (w / 2 > inset) {
            const double y = -h / 2 + 0.75;
            scene.add(Instance::capsule(pen.point(-w / 2 + inset, y), pen.point(w / 2 - inset, y), 0.9 * s, sheen.opacity(a), sheen.opacity(a)),
                      Blend::Over);
        }
        if (!spine) return;
        const double x = *spine * (w / 2 - 0.3 * m.t), half = 0.3 * h;
        scene.add(Instance::capsule(pen.point(x, -half), pen.point(x, half), std::max(1.2, 0.15 * m.t) * s, accent.opacity(0.9 * a),
                                    accent.opacity(0.9 * a)),
                  Blend::Over);
    };

    for (HudCard card : {HudCard::Performance, HudCard::Network, HudCard::Cost, HudCard::Agents, HudCard::Model}) {
        const auto i = static_cast<std::size_t>(ac::index(card));
        const auto& place = wings_.places()[i];
        const auto size = wings_.sizes()[i];
        const double alpha = place.alpha * f.alpha;
        if (alpha <= 0.01 || size.w <= 0 || size.h <= 0) continue;
        const Pen pen{QPointF(place.x, place.y), place.scale, place.spin, alpha};
        const double spine = wings_.isAtHerRight(card) ? -1 : 1;
        const double left = -size.w / 2 + m.padX, top = -size.h / 2 + m.padY;
        switch (card) {
            case HudCard::Model: {
                const QString name = modelText();
                if (name.isEmpty()) break;
                plate(pen, size.w, size.h, size.h / 2, std::nullopt);
                const double dot = 0.62 * m.t;
                const double x = -size.w / 2 + 0.85 * m.t;
                scene.add(Instance::circle(pen.point(x + dot / 2, 0), dot / 2 * pen.scale, accent.opacity(alpha)), Blend::Over);
                const double cap = capHeight(m.t, 600);
                scene.add(pen.leading(name, m.t, 600, x + dot + 0.42 * m.t, cap / 2, number));
                break;
            }
            case HudCard::Performance: {
                plate(pen, size.w, size.h, std::nullopt, spine);
                const auto& in = content_.intel;
                double y = top;
                if (content_.readouts.intelligence && !in.number.isEmpty()) {
                    const double baseline = y + CardMetrics::baseline(m.p);
                    double x = left;
                    scene.add(pen.leading(in.glyph, kUnitScale * m.p, 600, x, baseline, secondary));
                    x += advance(in.glyph, kUnitScale * m.p, 600) + m.glyphGap() * 1.25;
                    const Rgba ink = in.form == 2 ? number.mix(secondary, 0.6) : number;
                    scene.add(pen.leading(in.number, m.p, 600, x, baseline, ink.opacity(in.numberAlpha)));
                    y += CardMetrics::rowHeight(m.p) + m.rowGap;
                }
                if (content_.readouts.thinking && !in.thinking.isEmpty()) {
                    const double share = content_.readouts.intelligence && !in.number.isEmpty() ? in.thinkingOpacity : 1;
                    if (share > 0.01) {
                        const double baseline = y + CardMetrics::baseline(m.t);
                        double x = left;
                        const Pen faded{pen.center, pen.scale, pen.spin, pen.alpha * share};
                        scene.add(faded.leading(in.thinkingGlyph, kUnitScale * m.t, 600, x, baseline, secondary));
                        x += advance(in.thinkingGlyph, kUnitScale * m.t, 600) + m.glyphGap();
                        scene.add(faded.leading(in.thinking, m.t, 600, x, baseline, number));
                        x += advance(in.thinking, m.t, 600) + m.cellGap();
                        scene.add(faded.leading(in.unit, kUnitScale * m.t, 500, x, baseline, secondary));
                    }
                }
                break;
            }
            case HudCard::Network: {
                plate(pen, size.w, size.h, std::nullopt, spine);
                const auto& n = content_.net;
                const double cell = advance(QStringLiteral("888.8"), m.t, 600);
                const double arrow = advance(QStringLiteral("↓"), kUnitScale * m.t, 600);
                double y = top;
                for (int row = 0; row < 2; ++row) {
                    const double baseline = y + CardMetrics::baseline(m.t);
                    const QString& value = row == 0 ? n.downNumber : n.upNumber;
                    const QString& unit = row == 0 ? n.downUnit : n.upUnit;
                    scene.add(pen.leading(row == 0 ? QStringLiteral("↓") : QStringLiteral("↑"), kUnitScale * m.t, 600, left, baseline, secondary));
                    const double numberRight = left + arrow + m.glyphGap() + cell;
                    scene.add(pen.leading(value, m.t, 600, numberRight - advance(value, m.t, 600), baseline, number));
                    scene.add(pen.leading(unit, kUnitScale * m.t, 500, numberRight + m.cellGap(), baseline, secondary));
                    y += CardMetrics::rowHeight(m.t) + m.rowGap;
                }
                break;
            }
            case HudCard::Cost: {
                plate(pen, size.w, size.h, std::nullopt, spine);
                const double baseline = top + CardMetrics::baseline(m.p);
                double x = left;
                scene.add(pen.leading(content_.costGlyph, kUnitScale * m.p, 600, x, baseline, secondary));
                x += advance(content_.costGlyph, kUnitScale * m.p, 600) + m.glyphGap() * 1.25;
                if (content_.costAtLeast) {
                    scene.add(pen.leading(QStringLiteral("≥"), kUnitScale * m.p, 500, x, baseline, secondary));
                    x += advance(QStringLiteral("≥"), kUnitScale * m.p, 500) + 0.08 * m.p;
                }
                scene.add(pen.leading(content_.costNumber, m.p, 600, x, baseline, number));
                x += advance(content_.costNumber, m.p, 600);
                if (!content_.costUnknown) scene.add(pen.leading(content_.costUnit, kUnitScale * m.t, 500, x + m.cellGap(), baseline, secondary));
                break;
            }
            case HudCard::Agents: {
                plate(pen, size.w, size.h, std::nullopt, spine);
                double y = top;
                if (!content_.todayLine.isEmpty()) {
                    scene.add(pen.leading(content_.todayLine, m.t, 500, left, y + CardMetrics::baseline(m.t), number));
                    y += CardMetrics::rowHeight(m.t) + m.rowGap;
                }
                if (!content_.agentsLine.isEmpty()) scene.add(pen.leading(content_.agentsLine, m.t, 500, left, y + CardMetrics::baseline(m.t), secondary));
                break;
            }
            default:
                break;
        }
    }
    const auto& place = wings_.places()[0];
    const double drop = speedDrop_ * place.scale;
    const QPointF center(place.x + drop * std::sin(place.spin), place.y - drop * std::cos(place.spin));
    drawSpeed(scene, f, center, place.scale, place.spin, place.alpha * f.alpha);
}

void CharacterStyle::drawSpeed(Scene& scene, const Frame& f, QPointF center, double scale, double spin, double alpha) {
    const SpeedMetrics m(f.s);
    const double q = m.q();
    const Pen pen{center, scale, spin, alpha};
    const Ink ink{f.dark};
    const double width = hud_.speedWidth();
    const double heat = content_.unavailable ? 0 : f.heat;
    const Rgba glow = f.labelColor(heat);
    scene.add(Instance::softDot(center, (width * 0.75 + 30 * q) * scale, 50 * q * scale, spin,
                                glow.withAlpha((0.12 + 0.5 * std::max(0.0, heat - 0.3)) * alpha), 1),
              f.glowBlend());
    const double cap = capHeight(m.value, 700);
    const Rgba valueColor = f.idle() || content_.unavailable ? f.tokens.idleValueText : f.tokens.valueText;
    auto outlined = [&](TextDraw d, double a) {
        d.outlineColor = ink.outline().opacity(a * alpha);
        d.outlineWidth = Ink::width(m.value);
        return d;
    };
    if (!content_.staticValue.isEmpty()) {
        TextDraw d = pen.text(content_.staticValue, m.value, 700, 0, cap / 2, valueColor);
        d.glowColor = ink.shadow();
        d.glowStrength = ink.shadowAlpha() * alpha;
        d.glowRadius = 0.1 * m.value;
        scene.add(outlined(d, 1));
    } else {
        const auto& digits = hud_.odometer().digits();
        const double adv = digits.empty() ? 0 : width / static_cast<double>(digits.size());
        // Lightning under the digits.
        if (lightning_.isActive()) {
            const auto strength = lightningStrength(f);
            const Rgba electric = ink.electric().mix(glow, 0.2);
            const Rgba boltGlow = f.dark ? electric : Rgba{0.3f, 0.55f, 1, 1};
            const Rgba core = f.dark ? Rgba{0.95f, 0.98f, 1, 1} : Rgba{0.12f, 0.3f, 0.95f, 1};
            for (const auto& strike : lightning_.strikes()) {
                const double envelope = lightning_.envelope(strike) * strike.power * alpha;
                if (envelope <= 0.01) continue;
                for (int index = 0; lightning_.bolt(strike, index, strength, static_cast<int>(digits.size()), adv, cap); ++index) {
                    const auto& path = lightning_.path();
                    for (std::size_t k = 0; k + 1 < path.size(); ++k) {
                        const QPointF a = pen.point(path[k].first, path[k].second), b = pen.point(path[k + 1].first, path[k + 1].second);
                        scene.add(Instance::capsule(a, b, 0.22 * m.value * scale, boltGlow.withAlpha(0.3 * envelope), boltGlow.withAlpha(0.3 * envelope)), f.glowBlend());
                        scene.add(Instance::capsule(a, b, 0.09 * m.value * scale, boltGlow.withAlpha(0.6 * envelope), boltGlow.withAlpha(0.6 * envelope)), f.glowBlend());
                        scene.add(Instance::capsule(a, b, 0.04 * m.value * scale, core.withAlpha(envelope), core.withAlpha(envelope)), f.glowBlend());
                    }
                }
            }
        }
        const double pad = Ink::width(m.value) + 1;
        const double half = cap * 0.62 + pad;
        const double line = 2 * half;
        const QRectF clip(center.x() - width / 2 - pad, center.y() - half, width + 2 * pad, line);
        for (std::size_t i = 0; i < digits.size(); ++i) {
            const auto& digit = digits[i];
            const double ox = -width / 2 + adv * (static_cast<double>(i) + 0.5);
            const double ease = 1 - std::pow(1 - digit.t, 3);
            const double flash = lightning_.flash(static_cast<int>(digits.size() - 1 - i));
            const double k = std::clamp(flash, 0.0, 1.0);
            const Rgba haloColor = ink.shadow().mix(ink.electric().mix(glow, 0.2), k);
            const double haloAlpha = std::max(ink.shadowAlpha() * (1 - k), 0.9 * k), haloRadius = 0.1 + 0.08 * k;
            auto roll = [&](char ch, double dy, double a) {
                if (a <= 0.01 || !ch) return;
                TextDraw d = pen.text(QString(QChar::fromLatin1(ch)), m.value, 700, ox, cap / 2 + dy * line, valueColor.opacity(a));
                d.glowColor = haloColor;
                d.glowStrength = haloAlpha * a * alpha;
                d.glowRadius = haloRadius * m.value;
                d.clip = 1;
                d.clipRect = clip;
                scene.add(outlined(d, a));
            };
            roll(digit.previous, -ease, 1 - ease);
            roll(digit.current, 1 - ease, ease);
        }
    }
    const double unitCap = capHeight(m.unit, 500);
    TextDraw unit = pen.text(content_.unit, m.unit, 500, 0, 50 * q + unitCap, f.tokens.secondaryText);
    unit.outlineColor = ink.outline().opacity(0.85 * alpha * ink.outline().a);
    unit.outlineWidth = Ink::width(m.unit);
    scene.add(unit);
    const Rgba titleColor = f.labelColor(heat);
    const double e = 1 - std::pow(1 - hud_.titleProgress(), 3);
    auto titleChip = [&](const std::optional<std::string>& title, bool incoming) {
        if (!title || title->empty()) return;
        const double a = incoming ? std::max(0.0, (e - 0.3) / 0.7) : std::max(0.0, 1 - 2.5 * e);
        if (a <= 0.01) return;
        const double slide = incoming ? (1 - e) * 14 * q : -e * 14 * q;
        const Pen faded{center, scale, spin, alpha * a};
        chip(scene, faded, f.dark, QString::fromStdString(*title), m.title, 86 * q + m.title + slide, titleColor, titleColor.opacity(0.35));
    };
    titleChip(hud_.previousTitle(), false);
    titleChip(hud_.title(), true);
}

void CharacterStyle::drawPeak(Scene& scene, const Frame& f) {
    const auto& peak = hud_.peak();
    if (!f.effects.celebrate || !peak) return;
    const double side = 2 * f.center.x();
    const SpeedMetrics m(f.s);
    const double x = std::clamp(peak->x, 0.2 * side, 0.8 * side);
    const Rgba gold{1, 200.0f / 255, 80.0f / 255, 1};
    const Pen pen{QPointF(x, peak->y), peak->scale, 0, peak->alpha * f.alpha};
    chip(scene, pen, f.dark, content_.peakLabel, m.peak, 0, f.dark ? gold : gold.darkened(0.35), gold.opacity(0.5));
}

}  // namespace orb
