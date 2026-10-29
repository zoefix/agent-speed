#include "hero/hero_badge.hpp"

#include <QFontMetricsF>
#include <QGuiApplication>
#include <QLinearGradient>
#include <QPainter>
#include <QRadialGradient>
#include <algorithm>
#include <cmath>

#include "heat.hpp"

namespace hero {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kOuter = 33, kInner = 30.5, kPips = 37.5;

QColor alpha(QColor c, double a) {
    c.setAlphaF(static_cast<float>(clamp(c.alphaF() * a, 0, 1)));
    return c;
}

bool han(QChar c) { return c.unicode() >= 0x4E00 && c.unicode() <= 0x9FFF; }
bool cjk(const QString& text) {
    return std::any_of(text.begin(), text.end(), [](QChar c) { return c.unicode() >= 0x2E80; });
}
bool latin(const QString& text) {
    return std::all_of(text.begin(), text.end(), [](QChar c) { return c.unicode() <= 0x024F; });
}

QFont badgeFont(double size, const QString& text) {
    QFont font = QGuiApplication::font();
    font.setPixelSize(std::max(1, static_cast<int>(std::lround(size * 8))));  // laid out ×8, then scaled to units
    font.setWeight(QFont::Black);
    if (cjk(text)) font.setLetterSpacing(QFont::PercentageSpacing, 104);
    else if (latin(text) && size >= 14) font.setLetterSpacing(QFont::PercentageSpacing, 105);
    return font;
}

double widthOf(const QString& text, double size) {
    const QFontMetricsF fm(badgeFont(size, text));
    return fm.horizontalAdvance(text) / 8;
}

}  // namespace

// Title layout (DESIGN 4c Hero title badge): a 2 × 2 seal for four-Han zh titles, two balanced lines when a break
// fits, else one line at the largest size that fits 72 units; the ink box sits on (50, 50).
Badge::Layout Badge::layoutOf(const QString& source) const {
    Layout layout;
    const QString text = latin(source) ? source.toUpper() : source;
    layout.text = text;
    if (text.isEmpty()) return layout;
    const int graphemes = static_cast<int>(text.size());
    const double maximum = graphemes == 1 ? 34 : cjk(text) ? 28 : graphemes == 2 ? 26 : 22;
    double s1 = 9;
    for (double size = maximum; size >= 9; size -= 0.5) {
        if (widthOf(text, size) <= 72) {
            s1 = size;
            break;
        }
    }
    std::vector<QString> lines;
    double size = s1, advance = 1.1;
    const bool zh = in_.language.startsWith(QLatin1String("zh"));
    if (zh && text.size() == 4 && std::all_of(text.begin(), text.end(), han)) {
        lines = {text.left(2), text.mid(2)};
        size = 21;
        advance = 1.04;
    } else {
        std::vector<std::pair<QString, QString>> breaks;
        const int space = static_cast<int>(text.indexOf(QLatin1Char(' ')));
        if (space > 0) breaks.emplace_back(text.left(space), text.mid(space + 1));
        if (std::all_of(text.begin(), text.end(), han) && text.size() >= 5) {
            const int at = text.size() == 5 ? 2 : static_cast<int>(text.size()) / 2;
            breaks.emplace_back(text.left(at), text.mid(at));
        }
        const double top = latin(text) ? 20 : 22;
        for (double candidate = top; candidate >= std::max(13.0, 1.15 * s1) && lines.empty(); candidate -= 0.5) {
            for (const auto& [a, b] : breaks) {
                if (widthOf(a, candidate) <= 72 && widthOf(b, candidate) <= 72) {
                    lines = {a, b};
                    size = candidate;
                    break;
                }
            }
        }
        if (lines.empty()) lines = {text};
    }
    const QFont font = badgeFont(size, text);
    const QFontMetricsF fm(font);
    QPainterPath path;
    double y = 0;
    for (const auto& line : lines) {
        const double w = fm.horizontalAdvance(line);
        path.addText(QPointF(-w / 2, y), font, line);
        y += advance * size * 8;
    }
    // Centered optically on the ink box, scaled from ×8 layout units to canvas units.
    const QRectF box = path.boundingRect();
    QTransform t;
    t.translate(50, 50);
    t.scale(1.0 / 8, 1.0 / 8);
    t.translate(-box.center().x(), -box.center().y());
    layout.path = t.map(path);
    layout.ink = layout.path.boundingRect();
    return layout;
}

void Badge::update(const BadgeInput& input) {
    const double previous = lastTime_.value_or(input.time);
    const double dt = clamp(input.time - previous, 0, 0.1);
    const bool first = !lastTime_;
    lastTime_ = input.time;
    const bool textChanged = input.text != in_.text || input.language != in_.language;
    in_ = input;
    if (first) {
        level_ = input.level;
        current_ = layoutOf(input.text);
        wake_.snap(input.level >= 0 ? 1 : 0);
        ground_.snap(0);
        return;
    }
    if (input.level != level_) {
        previousLevel_ = level_;
        previous_ = current_;
        level_ = input.level;
        changeAt_ = clamp(input.titleChangedAt, previous - 0.1, input.time);
        current_ = layoutOf(input.text);
        burstDone_ = false;
    } else if (textChanged) {
        previous_ = current_;
        previousLevel_ = level_;
        changeAt_ = input.time;
        current_ = layoutOf(input.text);
        burstDone_ = true;
    }
    const bool parked = level_ < 0;
    const bool animated = input.effects >= Level::Soft;
    const double h = clamp(input.heat, 0, 1), o = clamp(input.overdrive, 0, 1);
    if (input.effects == Level::Off) wake_.snap(parked ? 0 : 1);
    else wake_.step(parked ? 0 : 1, dt);
    // Streak ground speed G* = lerp(60, 900, h) + 300 o; motes ω toward 2π (0.06 + 0.9 h + 0.5 o).
    ground_.step(parked ? 0 : 60 + 840 * h + 300 * o, dt);
    omega_.step(parked ? 0 : 2 * kPi * (0.06 + 0.9 * h + 0.5 * o), dt);
    phase_ = std::fmod(phase_ + omega_.value * dt, 2 * kPi * 1000);
    sheenPhase_ += dt / std::max(1.4, 5 - 3.5 * h);
    if (!animated) {
        lines_.clear();
        particles_.clear();
        return;
    }
    static const std::array<double, 4> bands{16, 33, 67, 84};
    const int cap = parked ? 0 : SpeedLines::cap(input.effects, h, ground_.value, 60);
    lines_.step(dt, cap, h, ground_.value, 0, 0, 0, 0, std::nullopt, random_, &bands);
    particles_.step(dt, 0);
    // Embers (Standard and Max, from the overdrive tier up): born on the lower ring arc, rising.
    if (input.effects >= Level::Standard && input.tier >= 3 && !parked) {
        emberCarry_ += (6 + 26 * o) * (input.effects == Level::Max ? 1.6 : 1) * dt;
        while (emberCarry_ >= 1) {
            emberCarry_ -= 1;
            Particle* slot = particles_.free();
            if (!slot) break;
            const double a = (30 + 120 * random_.next()) * kPi / 180;
            Particle p;
            p.alive = true;
            p.kind = Particle::Kind::Ember;
            p.x = 50 + kOuter * std::cos(a);
            p.y = 50 + kOuter * std::sin(a);
            p.vy = -(18 + 16 * random_.next());
            p.vx = 6 * std::sin(random_.next() * 6.28);
            p.life = 0.6 + 0.5 * random_.next();
            *slot = p;
        }
    } else {
        emberCarry_ = 0;
    }
    // Level-up burst: 10 + 3 (climbed − 1) sparks (≤ 18, Soft 6) from the title's edge at τ ≈ 0.06.
    if (!burstDone_ && input.time - changeAt_ >= 0.06) {
        burstDone_ = true;
        if (previousLevel_ >= 0 && level_ > previousLevel_) {
            int count = std::min(18, 10 + 3 * (level_ - previousLevel_ - 1));
            if (input.effects == Level::Soft) count = 6;
            for (int i = 0; i < count; ++i) {
                Particle* slot = particles_.free();
                if (!slot) break;
                const double a = random_.next() * 2 * kPi;
                const double speed = 40 + 50 * random_.next();
                Particle p;
                p.alive = true;
                p.kind = Particle::Kind::Ember;
                p.x = 50 + std::cos(a) * current_.ink.width() / 2;
                p.y = 50 + std::sin(a) * current_.ink.height() / 2;
                p.vx = std::cos(a) * speed;
                p.vy = std::sin(a) * speed;
                p.drag = 2.5;
                p.life = 0.35 + 0.25 * random_.next();
                *slot = p;
            }
        }
    }
}

int Badge::demand() const {
    if (in_.effects == Level::Off) return 0;
    const bool parked = level_ < 0;
    const bool moving = !parked || std::abs(wake_.value - (parked ? 0 : 1)) > 0.002 || !lines_.empty() || !particles_.empty() ||
                        in_.time - changeAt_ < 0.8;
    if (moving) return 60;
    double phase = std::fmod(in_.time - in_.sweepEpoch, Stage::kSweepPeriod);
    if (phase < 0) phase += Stage::kSweepPeriod;
    return phase > Stage::kSweepPeriod - 0.4 || phase < Stage::kSweepDuration + 0.05 ? 30 : 10;
}

void Badge::drawTitle(QPainter& crisp, QPainter& glow, const Layout& layout, double scale, double dy, double a, bool dark, const Paint& paint) const {
    if (layout.path.isEmpty() || a <= 0.002) return;
    QTransform t;
    t.translate(50, 50 + dy);
    t.scale(scale, scale);
    t.translate(-50, -50);
    const QPainterPath path = t.map(layout.path);
    const QRectF ink = path.boundingRect();
    const double h = in_.heat, o = in_.overdrive;
    const double wake = clamp(wake_.value, 0, 1);
    const QColor whiteHot(0xFF, 0xF7, 0xD6);
    QBrush fill(alpha(paint.stroke, a));
    if (dark && wake > 0.01) {
        QLinearGradient g(ink.topLeft(), ink.bottomLeft());
        g.setColorAt(0, alpha(mixOklab(mixOklab(paint.stroke, paint.hot, 0.45), whiteHot, 0.35 * o), a));
        g.setColorAt(1, alpha(mixOklab(paint.stroke, whiteHot, 0.2 * o), a));
        fill = QBrush(g);
    }
    crisp.fillPath(path, fill);
    // Glow: the glyphs at 0.30 + 0.45 h + 0.15 o (dark), the undarkened heat at 0.16 + 0.22 h (light).
    double strength = dark ? 0.30 + 0.45 * h + 0.15 * o : 0.16 + 0.22 * h;
    if (in_.effects == Level::Off) strength = dark ? 0.30 : 0.20;
    const double since = in_.time - changeAt_;
    if (since >= 0.08 && since < 1.5 && previousLevel_ >= 0 && level_ > previousLevel_) strength *= 1 + 0.8 * std::exp(-(since - 0.08) / 0.25);
    glow.fillPath(path, alpha(dark ? paint.stroke : heat::color(h), strength * a * wake));
    // Sheen (Standard and Max, h ≥ 0.45): a diagonal band crossing the glyphs in 0.7 s.
    if (in_.effects >= Level::Standard && h >= 0.45) {
        const double cycle = sheenPhase_ - std::floor(sheenPhase_);
        const double period = std::max(1.4, 5 - 3.5 * h);
        const double u = cycle * period / 0.7;
        if (u < 1) {
            const double x = ink.left() - 18 + (ink.width() + 36) * u;
            QLinearGradient band(QPointF(x - 9, ink.top()), QPointF(x + 9, ink.bottom()));
            const QColor c = dark ? alpha(whiteHot, 0.4 * a) : QColor(255, 255, 255, static_cast<int>(127 * a));
            band.setColorAt(0, alpha(c, 0));
            band.setColorAt(0.5, c);
            band.setColorAt(1, alpha(c, 0));
            crisp.save();
            crisp.setClipPath(path);
            crisp.fillRect(ink.adjusted(-20, -2, 20, 2), band);
            crisp.restore();
        }
    }
}

QImage Badge::render(bool dark, double side, double dpr) {
    const int pixels = std::max(8, static_cast<int>(std::ceil(side * dpr)));
    QImage crisp(pixels, pixels, QImage::Format_ARGB32_Premultiplied);
    crisp.fill(Qt::transparent);
    QImage glowImage(pixels, pixels, QImage::Format_ARGB32_Premultiplied);
    glowImage.fill(Qt::transparent);
    const double unit = pixels / 100.0;
    const double t = in_.time, h = clamp(in_.heat, 0, 1), o = clamp(in_.overdrive, 0, 1);
    const double wake = clamp(wake_.value, 0, 1);
    const Paint paint = paintFor(h, o, wake, dark);
    const bool animated = in_.effects >= Level::Soft;
    const bool parked = level_ < 0;
    const double since = t - changeAt_;
    const double breath = animated ? 0.86 + 0.14 * std::sin(2 * kPi * t / 4.8) : 1;

    QPainter cp(&crisp), gp(&glowImage);
    for (QPainter* p : {&cp, &gp}) {
        p->setRenderHint(QPainter::Antialiasing);
        p->scale(unit, unit);
    }
    // Camera shake at the redline tier (Standard and Max) moves the content, never the mask.
    if (in_.effects >= Level::Standard && in_.tier >= 4 && !parked) {
        const double amp = (0.2 + 0.5 * o) * (in_.effects == Level::Max ? 1.4 : 1);
        const double p2 = 2 * kPi;
        const double sx = amp * (0.5 * std::sin(p2 * 23 * t) + 0.3 * std::sin(p2 * 31 * t + 1) + 0.2 * std::sin(p2 * 41 * t + 2));
        const double sy = amp * (0.5 * std::sin(p2 * 19 * t + 0.5) + 0.3 * std::sin(p2 * 29 * t + 2) + 0.2 * std::sin(p2 * 37 * t + 1));
        cp.translate(sx, sy);
        gp.translate(sx, sy);
    }
    // 1. Halo.
    if (wake > 0.01) {
        const double haloAlpha = (dark ? 0.10 + 0.22 * h : 0.05 + 0.08 * h) * wake * (animated ? 1 + 0.1 * std::sin(2 * kPi * t / 4) : 1);
        QRadialGradient g(QPointF(50, 50), 34);
        g.setColorAt(0, alpha(paint.stroke, haloAlpha));
        g.setColorAt(1, alpha(paint.stroke, 0));
        cp.setCompositionMode(dark ? QPainter::CompositionMode_Plus : QPainter::CompositionMode_SourceOver);
        cp.setPen(Qt::NoPen);
        cp.setBrush(g);
        cp.drawEllipse(QPointF(50, 50), 34, 34);
        cp.setCompositionMode(QPainter::CompositionMode_SourceOver);
    }
    // 2. Speed streaks.
    const QColor streakColor = dark && in_.tier >= 4 ? paint.hot : paint.stroke;
    for (const auto& line : lines_.pool) {
        if (!line.alive) continue;
        const double length = line.length + ground_.value / 60 * 1.5;
        const QPointF head(line.x, line.y), tail(line.x - line.dx * length, line.y - line.dy * length);
        QLinearGradient g(head, tail);
        const double op = line.opacity * clamp(line.age / 0.12, 0, 1) * wake;
        g.setColorAt(0, alpha(streakColor, op));
        g.setColorAt(1, alpha(streakColor, 0));
        cp.setPen(QPen(QBrush(g), 0.5, Qt::SolidLine, Qt::RoundCap));
        cp.drawLine(head, tail);
    }
    // 3. Medallion rings, interrupted by the title's ink box grown by 2.5 units.
    {
        const double presence = parked ? 0.6 : 0.6 + 0.4 * wake;
        const QRectF gap = current_.ink.adjusted(-2.5, -2.5, 2.5, 2.5);
        cp.save();
        QPainterPath clip;
        clip.addRect(QRectF(-10, -10, 120, 120));
        QPainterPath hole;
        hole.addRect(gap);
        cp.setClipPath(clip.subtracted(hole));
        // Start-up: the outer ring draws on clockwise from 12 o'clock (0.45 s).
        const double drawOn = previousLevel_ < 0 && !parked && since >= 0 && since < 0.45 ? easeOutCubic(since / 0.45) : 1;
        cp.setPen(QPen(alpha(paint.stroke, (dark ? 0.34 : 0.42) * presence), 0.8));
        cp.setBrush(Qt::NoBrush);
        if (drawOn >= 1) cp.drawEllipse(QPointF(50, 50), kOuter, kOuter);
        else cp.drawArc(QRectF(50 - kOuter, 50 - kOuter, 2 * kOuter, 2 * kOuter), 90 * 16, static_cast<int>(-360 * 16 * drawOn));
        cp.setPen(QPen(alpha(paint.stroke, (dark ? 0.16 : 0.22) * presence), 0.5));
        cp.drawEllipse(QPointF(50, 50), kInner, kInner);
        cp.restore();
    }
    // 4. Rank pips: 13 dots across the top; lit up to the current level in the heat of their threshold.
    for (int k = 0; k < 13; ++k) {
        const double angle = (216 + 9 * k) * kPi / 180;
        const QPointF c(50 + kPips * std::cos(angle), 50 + kPips * std::sin(angle));
        bool lit = !parked && k <= level_;
        double pop = 1;
        if (lit && previousLevel_ < level_ && k > previousLevel_) {
            // Newly lit pips pop in order, 45 ms apart from τ = 0.10 (start-up: 35 ms from 0.12).
            const double start = previousLevel_ < 0 ? 0.12 + 0.035 * k : 0.10 + 0.045 * (k - previousLevel_ - 1);
            pop = since < start ? 0 : spring(since - start, 0, 1, 0, 0.3, 0.5);
        }
        if (!lit && !parked && previousLevel_ > level_ && k > level_ && k <= previousLevel_) {
            const double fade = 1 - clamp(since / 0.35, 0, 1);
            if (fade > 0) {
                lit = true;
                pop = fade;
            }
        }
        const bool currentPip = !parked && k == level_;
        const double r = (currentPip ? 1.45 : 1.05) * (lit ? std::max(0.0, pop) : 1);
        cp.setPen(Qt::NoPen);
        if (lit && pop > 0.01) {
            QColor color = heat::color(in_.pipHeat[static_cast<std::size_t>(k)]);
            if (!dark) color = color.darker(130);
            cp.setBrush(alpha(color, std::min(1.0, pop)));
            cp.drawEllipse(c, r, r);
            if (currentPip && dark) {
                gp.setPen(Qt::NoPen);
                gp.setBrush(alpha(color, 0.9));
                gp.drawEllipse(c, r + 1.2, r + 1.2);
            }
        } else {
            cp.setBrush(dark ? QColor(255, 255, 255, 31) : QColor(0, 0, 0, 31));
            cp.drawEllipse(c, 1.05, 1.05);
        }
    }
    // 5. Orbit motes: comets running clockwise on the outer ring.
    if (animated && !parked && wake > 0.02) {
        static const int order[] = {0, 6, 3, 9, 1, 7, 4, 10, 2, 8, 5, 11};
        const int maxCount = in_.effects == Level::Soft ? 4 : in_.effects == Level::Max ? 12 : 8;
        const double count = std::min<double>(maxCount, (2 + 6 * h) * (in_.effects == Level::Max ? 1.5 : 1));
        const double trail = (8 + 40 * h) * kPi / 180;
        for (int i = 0; i < 12 && i < std::ceil(count); ++i) {
            const double fadeIn = std::min(1.0, count - i);
            const double head = phase_ + 2 * kPi * order[i] / 12;
            const int steps = 8;
            for (int s = 0; s < steps; ++s) {
                const double a0 = head - trail * s / steps, a1 = head - trail * (s + 1) / steps;
                const double u = static_cast<double>(s) / steps;
                cp.setPen(QPen(alpha(paint.stroke, (1 - u) * wake * fadeIn), 1.2 - 0.85 * u, Qt::SolidLine, Qt::RoundCap));
                cp.drawLine(QPointF(50 + kOuter * std::cos(a0), 50 + kOuter * std::sin(a0)), QPointF(50 + kOuter * std::cos(a1), 50 + kOuter * std::sin(a1)));
            }
            cp.setPen(Qt::NoPen);
            cp.setBrush(alpha(paint.hot, wake * fadeIn));
            cp.drawEllipse(QPointF(50 + kOuter * std::cos(head), 50 + kOuter * std::sin(head)), 1.0, 1.0);
        }
    }
    // 6. Embers and level-up sparks: streaks from the flame core to C.
    const QColor core = dark ? QColor(0xFF, 0xF7, 0xD6) : QColor(0xFF, 0xB0, 0x20);
    for (const auto& p : particles_.pool) {
        if (!p.alive) continue;
        const double u = p.progress();
        if (u < 0) continue;
        const double drift = p.drag > 0 ? 0 : 6 * std::sin(p.seed + 7 * p.age);
        const QPointF head(p.x + drift, p.y), tail(p.x + drift - p.vx * 0.022, p.y - p.vy * 0.022);
        cp.setPen(QPen(alpha(mixOklab(core, paint.stroke, u), 1 - u), 0.6, Qt::SolidLine, Qt::RoundCap));
        cp.drawLine(head, tail);
        gp.setPen(QPen(alpha(core, 0.6 * (1 - u)), 0.6, Qt::SolidLine, Qt::RoundCap));
        gp.drawLine(head, tail);
    }
    // 7. Flash and shockwave (level up).
    if (previousLevel_ >= 0 && level_ > previousLevel_ && since >= 0.06 && since < 0.6 && animated) {
        const double u = clamp((since - 0.06) / 0.35, 0, 1);
        const double strength = std::min(1.6, 1 + 0.2 * (level_ - previousLevel_ - 1)) * (in_.effects == Level::Soft ? 0.6 : 1);
        const double fa = (dark ? 0.42 : 0.26) * strength * (1 - u) * (1 - u);
        if (fa > 0.002) {
            const double radius = 8 + 32 * easeOutCubic(u);
            QRadialGradient g(QPointF(50, 50), radius);
            g.setColorAt(0, alpha(dark ? QColor(0xFF, 0xF7, 0xD6) : paint.hot, fa));
            g.setColorAt(0.45, alpha(paint.hot, fa * 0.45));
            g.setColorAt(1, alpha(paint.hot, 0));
            cp.setCompositionMode(dark ? QPainter::CompositionMode_Plus : QPainter::CompositionMode_SourceOver);
            cp.setPen(Qt::NoPen);
            cp.setBrush(g);
            cp.drawEllipse(QPointF(50, 50), radius, radius);
            cp.setCompositionMode(QPainter::CompositionMode_SourceOver);
        }
        const double ru = clamp((since - 0.06) / 0.5, 0, 1);
        if (ru < 1) {
            const double r = kOuter + 11 * easeOutCubic(ru);
            cp.setPen(QPen(alpha(paint.hot, 0.7 * (1 - ru)), std::max(0.05, 1.6 * (1 - ru))));
            cp.setBrush(Qt::NoBrush);
            cp.drawEllipse(QPointF(50, 50), r, r);
        }
    }
    // 8–9. Title (with its transition) and sheen; idle: the idle label in slate.
    const double idleAlpha = (dark ? 0.55 : 0.7) * breath;
    const double presence = idleAlpha + (1 - idleAlpha) * wake;
    if (previousLevel_ != level_ && since >= 0 && since < 0.75 && animated) {
        double outScale = 1, outDy = 0, outAlpha = 0, inScale = 1, inDy = 0, inAlpha = 1;
        if (previousLevel_ >= 0 && level_ > previousLevel_) {
            outScale = 1 + 0.12 * easeOutCubic(since / 0.22);
            outAlpha = 1 - smooth(since / 0.16);
            inAlpha = smooth((since - 0.08) / 0.12);
            inScale = since < 0.08 ? 0.72 : spring(since - 0.08, 0.72, 1, 0, 0.42, 0.6);
        } else if (previousLevel_ >= 0 && level_ >= 0) {
            outDy = 5 * easeInOutSine(since / 0.3);
            outAlpha = 1 - smooth(since / 0.3);
            inDy = since < 0.12 ? -3 : -3 * (1 - easeOutCubic((since - 0.12) / 0.35));
            inAlpha = smooth((since - 0.12) / 0.35);
        } else if (previousLevel_ < 0) {
            outAlpha = 1 - smooth(since / 0.25);
            inAlpha = smooth(since / 0.25);
            inScale = spring(since, 0.85, 1, 0, 0.42, 0.7);
        } else {
            outAlpha = 1 - smooth(since / 0.4);
            inAlpha = smooth(since / 0.4);
        }
        drawTitle(cp, gp, previous_, outScale, outDy, outAlpha * presence, dark, paint);
        drawTitle(cp, gp, current_, inScale, inDy, inAlpha * presence, dark, paint);
    } else {
        drawTitle(cp, gp, current_, 1, 0, presence, dark, paint);
    }
    cp.end();
    gp.end();
    const double sigma = (1.0 + 1.6 * h + 0.6 * o) * (in_.effects == Level::Max ? 1.2 : 1);
    Painter::composite(crisp, glowImage, sigma * unit, dark);
    // Idle: the light band crosses the glyphs only.
    if (parked && animated && 1 - wake >= 0.05) {
        double phase = std::fmod(t - in_.sweepEpoch, Stage::kSweepPeriod);
        if (phase < 0) phase += Stage::kSweepPeriod;
        const double u = phase / Stage::kSweepDuration;
        if (u < 1) {
            const double x = (-10 + 120 * (0.5 - 0.5 * std::cos(kPi * u))) * unit;
            QPainter p(&crisp);
            p.setRenderHint(QPainter::Antialiasing);
            QPainterPath glyphs = QTransform::fromScale(unit, unit).map(current_.path);
            p.setClipPath(glyphs);
            QLinearGradient band(QPointF(x - 9 * unit, 0), QPointF(x + 9 * unit, 0));
            const QColor c = alpha(paint.sweep, 1 - wake);
            band.setColorAt(0, alpha(c, 0));
            band.setColorAt(0.5, c);
            band.setColorAt(1, alpha(c, 0));
            p.fillRect(QRectF(x - 9 * unit, 0, 18 * unit, pixels), band);
        }
    }
    Painter::applyMask(crisp, 42, 49);
    crisp.setDevicePixelRatio(dpr);
    return crisp;
}

}  // namespace hero
