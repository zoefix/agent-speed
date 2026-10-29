#include "orb/readout.hpp"

#include <algorithm>
#include <cmath>

#include "format.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kTitleSlide = 0.35;
constexpr double kTitleFade = 0.25;
constexpr double kPeakPill = 1.8;
constexpr double kPitch = 1.32, kGap = 0.16, kUnitScale = 0.82, kIntelScale = 1.25, kIntelGap = 0.20, kCap = 0.70;
constexpr double kTextFloor = 7;

bool latin(const QString& text) {
    for (QChar c : text) {
        if (c.unicode() > 0x024F) return false;
    }
    return true;
}

double smooth(double x) {
    x = std::clamp(x, 0.0, 1.0);
    return x * x * (3 - 2 * x);
}

struct Lines {
    bool intel = false;
    bool network = false;
};

double widthOf(const MeasureText& measure, const QString& text, double size, int weight) {
    TextDraw d;
    d.text = text;
    d.pixelSize = size;
    d.weight = weight;
    return measure ? measure(d) : size * 0.6 * static_cast<double>(text.size());
}

// The widest lines of each readout, against the placement's fit and hub (DESIGN 10: drawn only where they fit).
Lines fitting(const Frame& f, const LinePlacement& placement, const ReadoutContent& c, const MeasureText& measure) {
    Lines lines;
    const double s = f.s, t = placement.textSize(s), p = kIntelScale * t;
    if (c.intel.opacity > 0.01) {
        const double intel = widthOf(measure, c.intel.glyph, kUnitScale * p, 600) + kIntelGap * p + widthOf(measure, QString::fromLatin1(format::kPerfMeasureText), p, 600);
        const double think = widthOf(measure, c.intel.thinkingGlyph, kUnitScale * t, 600) + kGap * t + widthOf(measure, QStringLiteral("8.88"), t, 600) +
                             (placement.thinkingUnit ? kGap * t + widthOf(measure, c.intel.unit, kUnitScale * t, 500) : 0);
        lines.intel = std::max(intel, think) <= placement.fit * s;
        if (lines.intel && placement.hubReach) {
            const double intelBase = placement.nearestBaseline * s + kCap * (kIntelScale - 1) * t;
            const double reach = *placement.hubReach * s;
            lines.intel = std::hypot(intel / 2, intelBase) <= reach && std::hypot(think / 2, intelBase + kPitch * t) <= reach;
        }
        return lines;
    }
    if (c.net.opacity > 0.01) {
        const double width = widthOf(measure, QStringLiteral("↓"), t, 600) + 2 * kGap * t + widthOf(measure, QStringLiteral("8.88"), t, 600) +
                             widthOf(measure, QStringLiteral("MB/s"), kUnitScale * t, 500);
        lines.network = width <= placement.fit * s;
    }
    return lines;
}

double pillFade(const Frame& f) {
    if (!f.effects.celebrate || f.peakAge < 0 || f.peakAge >= kPeakPill) return 0;
    return std::min({1.0, f.peakAge / 0.15, (kPeakPill - f.peakAge) / 0.15});
}

// A size that fits `maxWidth`, never below `floor` × the size.
double fitted(const MeasureText& measure, TextDraw text, double maxWidth, double floor) {
    if (!measure || maxWidth <= 0) return text.pixelSize;
    const double width = measure(text);
    if (width <= maxWidth || width <= 0) return text.pixelSize;
    return text.pixelSize * std::max(floor, maxWidth / width);
}

}  // namespace

const ReadoutLayout ReadoutLayout::led{};

const LinePlacement LinePlacement::led{};
const LinePlacement LinePlacement::reactor{-0.175, false, 0.046, 0.32, false, -0.155, true, std::nullopt};
const LinePlacement LinePlacement::needle{0.108, true, 0.046, 0.285, false, std::nullopt, false, 0.223};
const LinePlacement LinePlacement::minimal{-0.228, false, 0.054, 0.50, true, std::nullopt, true, std::nullopt};

double ReadoutLayout::titleOffset() const {
    switch (title) {
    case Title::BelowUnit: return valueBaseline + (unitSize > 0 ? unitGap : 0) + titlePlace;
    case Title::BelowValue: return valueBaseline + titlePlace;
    case Title::Fixed: return titlePlace;
    }
    return titlePlace;
}

bool linesFit(const Frame& f, const LinePlacement& placement, const ReadoutContent& c, const MeasureText& measure) {
    const Lines lines = fitting(f, placement, c, measure);
    return lines.intel || lines.network;
}

double readoutShift(const Frame& f, const ReadoutLayout& layout, const LinePlacement& placement, const ReadoutContent& c,
                    const MeasureText& measure) {
    if (!placement.recenters || placement.below || f.s < 64) return 0;
    const Lines lines = fitting(f, placement, c, measure);
    if (!lines.intel && !lines.network) return 0;
    const double s = f.s, t = placement.textSize(s);
    const double top = lines.intel ? kIntelScale * t : t;
    const double nearest = lines.intel ? placement.thinkingBaseline.value_or(placement.nearestBaseline) : placement.nearestBaseline;
    const double single = lines.intel ? c.intel.glide : 0;
    const double blockTop = nearest * s - (1 - single) * kPitch * t - kCap * top;
    const double valueTop = (layout.valueBaseline - kCap * layout.valueSize) * s;
    double wanted = std::max(0.0, (valueTop - blockTop) / 2);
    if (layout.dots) {
        const double titleBottom = layout.titleOffset() * s + 0.22 * std::max(kTextFloor, layout.titleSize * s);
        const double dotsTop = *layout.dots * s - 2 * f.unit;
        wanted = std::min(wanted, std::max(0.0, dotsTop - 0.05 * s - titleBottom));
    }
    return wanted * smooth(lines.intel ? c.intel.opacity : c.net.opacity);
}

void buildReadout(Scene& scene, const Frame& f, const ReadoutContent& c, const MeasureText& measure, const ReadoutLayout& layout,
                  const LinePlacement& placement, const ReadoutPass& pass) {
    const double s = f.s;
    const double cx = f.center.x(), dotsY = f.center.y();
    const bool idle = f.idle();
    const bool valueOnly = s < 64;
    const auto& palette = pass.palette;
    const double cy = dotsY + f.readoutShift;
    const auto shadowed = [&](TextDraw& t) {
        if (palette.shadowRadius <= 0) return;
        t.glowColor = palette.shadow;
        t.glowStrength = palette.shadow.a;
        t.glowRadius = palette.shadowRadius;
    };

    // Value: tabular bold; from boost a heat glow, from overdrive a lean and a wider soft glow. It shrinks to its width.
    TextDraw value;
    value.text = c.value;
    value.pixelSize = layout.valueSize * s;
    value.weight = 700;
    value.pixelSize = fitted(measure, value, layout.valueMaxWidth * s, 0.4);
    const QPointF jitter = layout.valueMotion ? c.jitter : QPointF();
    // A partly unpriced money value: ≥ at 60 % of the value's size in the secondary color, part of its width.
    double prefixWidth = 0;
    TextDraw prefix;
    if (c.atLeast) {
        prefix.text = QStringLiteral("≥");
        prefix.pixelSize = 0.6 * value.pixelSize;
        prefix.weight = 600;
        prefixWidth = measure ? measure(prefix) + 0.04 * value.pixelSize : 0.4 * value.pixelSize;
        const double valueWidth = measure ? measure(value) : 0.6 * value.pixelSize * static_cast<double>(value.text.size());
        prefix.baselineCenter = QPointF(cx + jitter.x() - (valueWidth + prefixWidth) / 2 + prefixWidth / 2, cy + layout.valueBaseline * s + jitter.y());
        prefix.color = palette.unit.value_or(f.tokens.secondaryText);
        prefix.clip = pass.clip;
        scene.add(prefix);
    }
    value.baselineCenter = QPointF(cx + jitter.x() + prefixWidth / 2, cy + layout.valueBaseline * s + jitter.y());
    value.color = palette.value.value_or(idle ? f.tokens.idleValueText : f.tokens.valueText);
    value.clip = pass.clip;
    const Rgba heat = f.tokens.heat(std::max(f.heat, 0.2));
    const bool glows = layout.valueGlow && palette.valueGlow;
    if (glows) {
        value.glowRadius = 0.035 * s * led::glowScale(f.overdrive);
        value.glowColor = heat;
        value.glowStrength = c.valueGlow * (f.dark ? 0.75 : 0.35);
    }
    shadowed(value);
    const double skew = layout.valueMotion ? c.skew : 0;
    value.shear = std::tan(led::leanDegrees(f.heat) * kPi / 180) * skew;
    if (glows && skew > 0.01 && measure && pass.clip != 3) {
        const double width = measure(value);
        const double sigma = 2.4 * 0.0175 * s * led::glowScale(f.overdrive);
        const double alpha = (f.dark ? 0.45 : 0.2) * 0.42 * skew;
        scene.add(Instance::roundedRect(QPointF(cx, value.baselineCenter.y() - 0.36 * value.pixelSize),
                                        std::max(1.0, width / 2 - 0.08 * value.pixelSize), 0.3 * value.pixelSize, 0,
                                        0.28 * value.pixelSize, heat.withAlpha(alpha), 1, Instance::glowSigma(sigma), 1),
                  Blend::Over);
    }
    // The odometer: only the characters that changed roll, each clipped to its own cell, 0.92 × the value size in
    // 0.24 s (up when rising); different lengths crossfade in 0.12 s.
    const double sinceValue = f.time - c.valueChangedAt;
    const bool rolling = c.rolls && layout.rolls && measure && pass.clip == 0 && sinceValue >= 0 && sinceValue < 0.24 &&
                         !c.previousValue.isEmpty() && c.previousValue != value.text;
    if (!rolling) {
        scene.add(value);
    } else if (c.previousValue.size() != value.text.size()) {
        const double u = std::min(1.0, sinceValue / 0.12);
        TextDraw before = value;
        before.text = c.previousValue;
        before.color = value.color.withAlpha(value.color.a * (1 - u));
        before.glowStrength *= 1 - u;
        TextDraw after = value;
        after.color = value.color.withAlpha(value.color.a * u);
        after.glowStrength *= u;
        scene.add(before);
        scene.add(after);
    } else {
        const double u = led::easeOut(sinceValue / 0.24);
        const double travel = (c.rising ? 1 : -1) * 0.92 * value.pixelSize;
        const QString text = value.text;
        const double width = measure(value);
        const double left = value.baselineCenter.x() - width / 2, baseline = value.baselineCenter.y();
        const auto widthUpTo = [&](int n) {
            if (n <= 0) return 0.0;
            TextDraw part = value;
            part.text = text.left(n);
            return measure(part);
        };
        double x0 = 0;
        for (int i = 0; i < text.size(); ++i) {
            const double x1 = widthUpTo(i + 1);
            TextDraw ch = value;
            ch.text = text.mid(i, 1);
            ch.baselineCenter = QPointF(left + (x0 + x1) / 2, baseline);
            if (text.at(i) == c.previousValue.at(i)) {
                scene.add(ch);
            } else {
                ch.clip = 1;
                ch.clipRect = QRectF(left + x0 - 1, baseline - 1.05 * value.pixelSize, x1 - x0 + 2, 1.35 * value.pixelSize);
                TextDraw old = ch;
                old.text = c.previousValue.mid(i, 1);
                old.baselineCenter.setY(baseline - travel * u);
                ch.baselineCenter.setY(baseline + travel * (1 - u));
                scene.add(old);
                scene.add(ch);
            }
            x0 = x1;
        }
    }
    if (valueOnly) return;

    if (layout.unitSize > 0) {
        TextDraw unit;
        unit.text = c.unit;
        unit.pixelSize = std::max(kTextFloor, layout.unitSize * s);
        unit.weight = 500;
        unit.tracking = 0.5 * f.unit;
        unit.baselineCenter = QPointF(cx, cy + (layout.valueBaseline + layout.unitGap) * s);
        unit.color = palette.unit.value_or(f.tokens.secondaryText);
        unit.clip = pass.clip;
        shadowed(unit);
        scene.add(unit);
    }

    // Title: slides up on change (`minimal` crossfades); crossfades to the hover line. Titles shrink to 80 % to fit.
    const double titleY = cy + layout.titleOffset() * s;
    const double since = f.time - c.titleChangedAt;
    const double duration = layout.titleCrossfades ? kTitleFade : kTitleSlide;
    const double slide = since >= 0 && since < duration ? (layout.titleCrossfades ? since / duration : led::easeOut(since / duration)) : 1;
    const double rise = layout.titleCrossfades ? 0 : 0.05 * s;
    const double titleSize = std::max(kTextFloor, layout.titleSize * s);
    auto title = [&](const QString& text, double y, double alpha, Rgba color) {
        if (text.isEmpty() || alpha <= 0.01) return;
        TextDraw t;
        t.text = latin(text) ? text.toUpper() : text;
        t.pixelSize = titleSize;
        t.weight = 600;
        t.tracking = latin(text) ? 1.6 * f.unit : 0.6 * f.unit;
        t.pixelSize = std::max(kTextFloor, fitted(measure, t, layout.titleMaxWidth * s, 0.8));
        t.baselineCenter = QPointF(cx, y);
        t.color = color.withAlpha(color.a * alpha);
        t.clip = pass.clip;
        shadowed(t);
        scene.add(t);
    };
    const Rgba titleColor =
        palette.title.value_or(idle ? f.tokens.secondaryText : f.tokens.heat(std::max(c.titleHeat, 0.1)));
    const double show = 1 - c.hover;
    title(c.title, titleY + rise * (1 - slide), show * slide, titleColor);
    if (slide < 1) title(c.previousTitle, titleY - rise * slide, show * (1 - slide), titleColor);
    if (c.hover > 0.01 && !c.info.isEmpty()) {
        TextDraw info;
        info.text = c.info;
        info.pixelSize = std::max(kTextFloor, titleSize * layout.infoScale);
        info.weight = 500;
        info.pixelSize = std::max(kTextFloor, fitted(measure, info, layout.infoMaxWidth * s, 0.7));
        info.baselineCenter = QPointF(cx, titleY);
        const Rgba secondary = palette.unit.value_or(f.tokens.secondaryText);
        info.color = secondary.withAlpha(secondary.a * c.hover);
        info.clip = pass.clip;
        shadowed(info);
        scene.add(info);
    }

    // Agent dots: one per generating agent, max 6 (then 5 and +n), gently pulsing; `liquid` gives them a halo.
    if (pass.dots && layout.dots && !idle && !c.agents.empty()) {
        const int count = static_cast<int>(c.agents.size());
        const int overflow = count > 6 ? count - 5 : 0;
        const int dots = overflow > 0 ? 5 : count;
        const double d = 4 * f.unit, spacing = 3.2 * f.unit, more = 0.075 * s;
        const double total = dots * d + std::max(0, dots - 1) * spacing + (overflow > 0 ? spacing + more : 0);
        const double y = dotsY + *layout.dots * s;
        double x = cx - total / 2;
        for (int i = 0; i < dots; ++i) {
            const Rgba tint = Rgba::from(c.agents[static_cast<std::size_t>(i)]);
            const Rgba color = f.dark ? tint : tint.darkened(0.12);
            const double pulse =
                f.effects.breathing ? 0.62 + 0.38 * (0.5 + 0.5 * std::sin(2 * kPi * (f.time * 0.9 + i * 0.19))) : 1;
            if (layout.dotHalo) {
                const Rgba halo = f.dark ? Rgba{6 / 255.0f, 8 / 255.0f, 12 / 255.0f, 0.55f} : Rgba::white(0.75f);
                scene.add(Instance::circle(QPointF(x + d / 2, y), d / 2 + 1.2 * f.unit, halo), Blend::Over);
            }
            scene.add(Instance::circle(QPointF(x + d / 2, y), d / 2, color.withAlpha(pulse), 1, f.dark ? d * 0.7 : 0, 0.45),
                      Blend::Over);
            x += d + spacing;
        }
        if (overflow > 0) {
            TextDraw plus;
            plus.text = QStringLiteral("+%1").arg(overflow);
            plus.pixelSize = std::max(kTextFloor, 0.06 * s);
            plus.weight = 600;
            plus.baselineCenter = QPointF(x + more / 2, y + 0.02 * s);
            plus.color = f.tokens.secondaryText;
            scene.add(plus);
        }
    }

    // New peak: a gold pill springs in above the value for 1.8 s.
    const double t = f.peakAge;
    if (pass.pill && f.effects.celebrate && t >= 0 && t < kPeakPill && !c.peakLabel.isEmpty()) {
        const double in = std::min(1.0, t / 0.35);
        const double spring = 1 + 0.12 * std::sin(in * kPi) * (1 - in);
        const double out = t > kPeakPill - 0.3 ? (kPeakPill - t) / 0.3 : 1;
        const double alpha = std::clamp(std::min(in * 2, out), 0.0, 1.0);
        TextDraw label;
        label.text = c.peakLabel;
        label.pixelSize = std::max(kTextFloor, 0.058 * s * spring);
        label.weight = 700;
        label.tracking = 0.8 * f.unit;
        const double y = cy + layout.pill * s;
        const double width = measure ? measure(label) : label.pixelSize * static_cast<double>(label.text.size()) * 0.6;
        const Rgba gold = f.dark ? kGold : kGold.darkened(0.25);
        scene.add(Instance::roundedRect(QPointF(cx, y), width / 2 + 0.035 * s, 0.045 * s * spring, 0, 0.045 * s * spring,
                                        gold.withAlpha((f.dark ? 0.16 : 0.18) * alpha), 1, 0.02 * s, 0.35 * alpha),
                  Blend::Over);
        label.baselineCenter = QPointF(cx, y + 0.36 * label.pixelSize);
        label.color = gold.withAlpha(alpha);
        scene.add(label);
    }

    // The two-line readout: network (arrow · number · unit in fixed cells) or intelligence (智力 over 思考).
    const Lines lines = fitting(f, placement, c, measure);
    if (!lines.intel && !lines.network) return;
    const double ts = placement.textSize(s), ps = kIntelScale * ts, pitch = kPitch * ts;
    const double yieldPill = pass.linesYieldToPill ? pillFade(f) : 0;
    const Rgba ink = f.dark ? Rgba::white() : Rgba::hex(0x0B0D12);
    const Rgba lineSecondary = palette.lineSecondary.value_or((f.dark ? Rgba::white() : Rgba::black()).withAlpha(0.50));
    const Rgba lineNumber = palette.lineNumber.value_or(ink.withAlpha(f.dark ? 0.78 : 0.76));
    const double nearest = cy + placement.nearestBaseline * s;

    struct Piece {
        TextDraw draw;
        double gap = 0;
        double width = 0;
    };
    const auto text = [&](const QString& label, double size, int weight, Rgba color, double gap) {
        Piece piece;
        piece.draw.text = label;
        piece.draw.pixelSize = size;
        piece.draw.weight = weight;
        piece.draw.color = color;
        piece.draw.clip = pass.clip;
        shadowed(piece.draw);
        piece.gap = gap;
        return piece;
    };
    const auto put = [&](Piece& piece, double centerX, double baseline, double opacity) {
        piece.draw.baselineCenter = QPointF(centerX, baseline);
        piece.draw.color = piece.draw.color.withAlpha(piece.draw.color.a * opacity);
        if (piece.draw.color.a > 0.005) scene.add(piece.draw);
    };

    if (lines.network) {
        const double alpha = c.net.opacity * (1 - yieldPill);
        if (alpha <= 0.01) return;
        const double cellNumber = widthOf(measure, QStringLiteral("8.88"), ts, 600);
        const double cellUnit = widthOf(measure, QStringLiteral("MB/s"), kUnitScale * ts, 500);
        const double arrowWidth = widthOf(measure, QStringLiteral("↓"), ts, 600);
        const double lineWidth = arrowWidth + kGap * ts + cellNumber + kGap * ts + cellUnit;
        const double left = std::round(cx - lineWidth / 2);
        const auto drawLine = [&](const QString& arrow, const QString& number, const QString& unitText, double baseline) {
            Piece a = text(arrow, ts, 600, lineSecondary, 0);
            put(a, left + arrowWidth / 2, baseline, alpha);
            Piece n = text(number, ts, 600, lineNumber, 0);
            const double numberWidth = widthOf(measure, number, ts, 600);
            put(n, std::round(left + arrowWidth + kGap * ts + cellNumber - numberWidth / 2), baseline, alpha);
            Piece u = text(unitText, kUnitScale * ts, 500, lineSecondary, 0);
            const double unitWidth = widthOf(measure, unitText, kUnitScale * ts, 500);
            put(u, std::round(left + arrowWidth + 2 * kGap * ts + cellNumber + unitWidth / 2), baseline, alpha);
        };
        if (placement.below) {
            drawLine(QStringLiteral("↓"), c.net.downNumber, c.net.downUnit, nearest);
            drawLine(QStringLiteral("↑"), c.net.upNumber, c.net.upUnit, nearest + pitch);
        } else {
            drawLine(QStringLiteral("↓"), c.net.downNumber, c.net.downUnit, nearest - pitch);
            drawLine(QStringLiteral("↑"), c.net.upNumber, c.net.upUnit, nearest);
        }
        return;
    }

    const auto& in = c.intel;
    const double alpha = in.opacity * (1 - yieldPill);
    if (alpha <= 0.01) return;
    const auto line = [&](std::vector<Piece> pieces, double baseline, double opacity) -> std::vector<Piece> {
        double total = 0;
        for (auto& piece : pieces) {
            piece.width = widthOf(measure, piece.draw.text, piece.draw.pixelSize, piece.draw.weight);
            total += piece.width + piece.gap;
        }
        double x = std::round(cx - total / 2);
        for (auto& piece : pieces) {
            put(piece, x + piece.width / 2, baseline, opacity);
            piece.draw.baselineCenter.setX(x);
            x = std::round(x + piece.width + piece.gap);
        }
        return pieces;
    };
    double intelBaseline = 0, thinkingBaseline = 0;
    if (placement.below) {
        intelBaseline = nearest + kCap * (kIntelScale - 1) * ts;
        thinkingBaseline = intelBaseline + pitch;
    } else {
        thinkingBaseline = cy + placement.thinkingBaseline.value_or(placement.nearestBaseline) * s;
        intelBaseline = thinkingBaseline - (1 - in.glide) * pitch;
    }
    const Rgba numberColor = in.form == 1 ? lineNumber : lineSecondary;
    auto number = text(in.number, ps, 600, numberColor, 0);
    number.draw.color = number.draw.color.withAlpha(number.draw.color.a * in.numberAlpha);
    const auto drawn = line({text(in.glyph, kUnitScale * ps, 600, lineSecondary, kIntelGap * ps), number}, intelBaseline, alpha);
    if (in.provisional && drawn.size() == 2) {
        // The provisional mark: a dotted underline under the digits, 0.22 p below the baseline, no glow.
        const double w = drawn[1].width, left = drawn[1].draw.baselineCenter.x();
        const double d = std::max(1.0, 0.1 * ps);
        const int n = std::max(2, static_cast<int>(std::floor((w - d) / (2.4 * d))) + 1);
        const Rgba dot = numberColor.withAlpha(numberColor.a * 0.7 * alpha);
        for (int i = 0; i < n; ++i) {
            const double x = left + d / 2 + (n > 1 ? i * (w - d) / (n - 1) : 0);
            scene.add(Instance::circle(QPointF(x, intelBaseline + 0.22 * ps), d / 2, dot, 1, 0, 0), Blend::Over);
        }
    }
    if (in.thinkingOpacity > 0.01) {
        std::vector<Piece> pieces{text(in.thinkingGlyph, kUnitScale * ts, 600, lineSecondary, kGap * ts),
                                  text(in.thinking, ts, 600, lineNumber, placement.thinkingUnit ? kGap * ts : 0)};
        if (placement.thinkingUnit) pieces.push_back(text(in.unit, kUnitScale * ts, 500, lineSecondary, 0));
        line(std::move(pieces), thinkingBaseline, alpha * in.thinkingOpacity);
    }
}

}  // namespace orb
