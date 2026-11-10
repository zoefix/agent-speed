#include "orb/character/character_quota_card.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/hud_models.hpp"

namespace orb {

namespace ac = agentspeed::character;
using cards::advance;
using cards::capHeight;
using cards::CardMetrics;
using cards::Pen;

namespace {

constexpr int kLabelWeight = 500, kNameWeight = 600, kNumberWeight = 600;
constexpr double kHostInk = 0.72;

const QString& percentCell() {
    static const QString cell = QStringLiteral("100%");
    return cell;
}

const QString& hostSeparator() {
    static const QString separator = QStringLiteral(" · ");
    return separator;
}

double smoothstep(double x) {
    const double k = std::clamp(x, 0.0, 1.0);
    return k * k * (3 - 2 * k);
}

ac::QuotaMetrics metricsOf(const CardMetrics& m) { return ac::QuotaMetrics{m.t}; }

QString percentText(int percent) { return QString::number(percent) + QLatin1Char('%'); }

// The track, then the fill from its left end; both fully rounded, the fill a smaller dot while it is shorter than tall.
void bar(Scene& scene, const Pen& pen, double x, double y, double length, double thickness, double share, Rgba track, Rgba fill) {
    const double r = thickness / 2, s = pen.scale;
    scene.add(Instance::capsule(pen.point(x + r, y), pen.point(x + length - r, y), thickness * s, track.opacity(pen.alpha),
                                track.opacity(pen.alpha)),
              Blend::Over);
    const double filled = length * std::clamp(share, 0.0, 1.0);
    if (filled <= 0.2) return;
    const Rgba ink = fill.opacity(pen.alpha);
    if (filled < thickness) {
        scene.add(Instance::circle(pen.point(x + filled / 2, y), filled / 2 * s, ink), Blend::Over);
        return;
    }
    scene.add(Instance::capsule(pen.point(x + r, y), pen.point(x + filled - r, y), thickness * s, ink, ink), Blend::Over);
}

}  // namespace

void QuotaCard::sync(const std::vector<ac::QuotaSource>& sources, bool live, bool gone, bool small, double time, bool still) {
    small_ = small;
    columnsAt_ = {-1, -1};
    if (texts_.size() > 160) texts_.clear();
    if (live) {
        motion_.sync(sources, small, time, still);
    } else if (gone) {
        motion_.clear();
    }
}

// The QString of a card text, kept until the next sync, so a frame converts each text once.
const QString& QuotaCard::text(const std::string& s) const {
    auto it = texts_.find(s);
    if (it == texts_.end()) it = texts_.emplace(s, QString::fromStdString(s)).first;
    return it->second;
}

ac::QuotaForm QuotaCard::form() const {
    const auto f = fit_.form();
    return small_ && f == ac::QuotaForm::Full ? ac::QuotaForm::NoResets : f;
}

ac::QuotaColumns QuotaCard::columns(const CardMetrics& m, double time) const {
    if (columnsAt_[0] == time && columnsAt_[1] == m.t) return columns_;
    const auto q = metricsOf(m);
    auto& c = columns_;
    c = {};
    columnsAt_ = {time, m.t};
    c.percent = advance(percentCell(), m.t, kNumberWeight);
    for (const auto& source : motion_.sources()) {
        c.header = std::max(c.header, q.dot() + q.dotGap() + advance(text(source.name), q.small(), kNameWeight));
        for (const auto& line : source.lines) {
            c.label = std::max(c.label, advance(text(line.data.label), q.small(), kLabelWeight));
            if (!line.reset.empty() && (line.resetPresence.wanted || line.resetPresence.value(time) > 0)) {
                c.reset = std::max(c.reset, advance(text(line.reset), q.small(), kLabelWeight));
            }
            if (!line.oldReset.empty() && line.resetMix(time) < 1) {
                c.reset = std::max(c.reset, advance(text(line.oldReset), q.small(), kLabelWeight));
            }
        }
    }
    return c;
}

ac::QuotaWidths QuotaCard::widths(const CardMetrics& m, double time) const {
    if (motion_.empty()) return {};
    return ac::quotaWidths(columns(m, time), metricsOf(m), m.padX, small_);
}

void QuotaCard::fit(double dt, const ac::QuotaWidths& widths, double room, double spare, bool snap, bool still) {
    fit_.update(dt, widths, room, spare, snap);
    ease_.update(dt, form(), snap || still);
}

bool QuotaCard::hostOwnLine(const ac::QuotaMotion::Source& source, ac::QuotaForm form, const ac::QuotaColumns& c,
                            const CardMetrics& m) const {
    if (source.host.empty()) return false;
    const auto q = metricsOf(m);
    const double header = q.dot() + q.dotGap() + advance(text(source.name), q.small(), kNameWeight) +
                          advance(hostSeparator() + text(source.host), q.small(), kLabelWeight);
    return ac::quotaHostOwnLine(form, header, ac::quotaContentWidth(form, c, q));
}

ac::CardSize QuotaCard::size(const CardMetrics& m, double time) const {
    if (motion_.empty()) return {};
    const auto q = metricsOf(m);
    const auto c = columns(m, time);
    const auto f = form();
    const double h = ac::quotaContentHeight(motion_, f, q, time, [&](const ac::QuotaMotion::Source& s) { return hostOwnLine(s, f, c, m); });
    if (h <= 0.5) return {};
    return {ac::quotaContentWidth(f, c, q) + 2 * m.padX, h + 2 * m.padY};
}

void QuotaCard::draw(Scene& scene, const Pen& pen, ac::CardSize box, const CardMetrics& m, const Ink& ink, double time) const {
    if (motion_.empty()) return;
    const auto q = metricsOf(m);
    const auto c = columns(m, time);
    const auto f = form();
    const auto l = ac::quotaLineLayout(f, c, q);
    const double contentWidth = ac::quotaContentWidth(f, c, q);
    // The bars and reset cells ease with the plate to a new form; a reset cell shows as far as the plate holds it.
    const double barLength = std::clamp(ease_.bar(), 3.0, 5.0) * m.t;
    const double percentRight = l.bar + barLength + q.barGap() + c.percent;
    const double resetLeft = percentRight + q.resetGap();
    const double resetShare =
        small_ || c.reset <= 0 ? 0 : ease_.resets() * std::clamp((box.w - 2 * m.padX - resetLeft) / c.reset, 0.0, 1.0);
    const bool dark = ink.dark;
    const Rgba track = (dark ? Rgba::white() : Rgba::hex(0x0B0D12)).opacity(dark ? 0.16 : 0.12);
    const Rgba amber = dark ? Rgba::hex(0xF5A524) : Rgba::hex(0xB86E00);
    const Rgba red = dark ? Rgba::hex(0xF87171) : Rgba::hex(0xD92D20);
    const double left = -box.w / 2 + m.padX;
    const double bottom = box.h / 2 - m.padY;
    const double small = q.small();
    const double dot = q.dot();
    const double nameX = left + dot + q.dotGap();
    const double nameMiddle = capHeight(small, kNameWeight) / 2;
    const double digitMiddle = capHeight(m.t, kNumberWeight) / 2;
    const auto measureHost = [small](std::string_view s) {
        return advance(QString::fromUtf8(s.data(), static_cast<qsizetype>(s.size())), small, kLabelWeight);
    };
    double y = -box.h / 2 + m.padY;
    for (const auto& source : motion_.sources()) {
        const double sourceTop = y;
        const double presence = source.presence.value(time);
        const double sourceAlpha = smoothstep(presence);
        const bool ownLine = hostOwnLine(source, f, c, m);
        const Rgba tint = dark ? Rgba::hex(source.color) : Rgba::hex(source.color).darkened(0.14);
        // A row the plate has not grown to yet fades instead of leaving it.
        const auto alphaAt = [&](double rowTop, double h) {
            const double overflow = (rowTop + h - bottom) / (0.5 * h);
            return sourceAlpha * std::clamp(1 - overflow, 0.0, 1.0);
        };
        if (ac::hasHeaders(f)) {
            const double h = q.headerRow();
            if (const double a = alphaAt(y, h); a > 0.01) {
                const Pen p = pen.faded(a);
                const double baseline = y + CardMetrics::baseline(small);
                scene.add(Instance::circle(p.point(left + dot / 2, baseline - nameMiddle), dot / 2 * p.scale, tint.opacity(p.alpha)),
                          Blend::Over);
                const QString& name = text(source.name);
                scene.add(p.leading(name, small, kNameWeight, nameX, baseline, ink.secondary));
                if (!source.host.empty() && !ownLine) {
                    scene.add(p.leading(hostSeparator() + text(source.host), small, kLabelWeight, nameX + advance(name, small, kNameWeight),
                                        baseline, ink.secondary.opacity(kHostInk)));
                }
            }
            y += h + q.lineGap();
        }
        if (ownLine) {
            const double h = q.headerRow();
            if (const double a = alphaAt(y, h); a > 0.01) {
                const double x = ac::hasHeaders(f) ? nameX : left;
                const double room = contentWidth - (x - left);
                const QString& host = text(source.host);
                const QString shown = advance(host, small, kLabelWeight) <= room
                                          ? host
                                          : QString::fromStdString(ac::cutAtEnd(source.host, room, measureHost));
                scene.add(pen.faded(a).leading(shown, small, kLabelWeight, x, y + CardMetrics::baseline(small),
                                               ink.secondary.opacity(kHostInk)));
            }
            y += h + q.lineGap();
        }
        bool first = true;
        for (const auto& line : source.lines) {
            const double h = q.row();
            const double linePresence = line.presence.value(time);
            const double a = alphaAt(y, h) * smoothstep(linePresence);
            const double rowTop = y;
            y += linePresence * (h + q.lineGap());
            const bool firstLine = first;
            first = false;
            if (a <= 0.01) continue;
            const Pen p = pen.faded(a);
            const double baseline = rowTop + CardMetrics::baseline(m.t);
            const double middle = baseline - digitMiddle;
            if (!ac::hasHeaders(f) && firstLine) {
                scene.add(Instance::circle(p.point(left + dot / 2, middle), dot / 2 * p.scale, tint.opacity(p.alpha)), Blend::Over);
            }
            scene.add(p.leading(text(line.data.label), small, kLabelWeight, left + l.label, baseline, ink.secondary));
            const auto level = ac::quotaLevel(line.data.percent);
            const Rgba fill = level == ac::QuotaLevel::Critical ? red : level == ac::QuotaLevel::Warning ? amber : tint;
            bar(scene, p, left + l.bar, middle, barLength, q.barHeight(), line.bar(time), track, fill);
            // The percent right-aligned in its cell; a changed value crossfades, and from 95 % it takes the bar's red.
            const auto percentInk = [&](int percent) {
                return ac::quotaLevel(percent) == ac::QuotaLevel::Critical ? red.opacity(0.95) : ink.number;
            };
            const double mix = line.percentMix(time);
            const double right = left + percentRight;
            if (line.oldPercent && mix < 1) {
                const QString old = percentText(*line.oldPercent);
                scene.add(pen.faded(a * (1 - mix)).leading(old, m.t, kNumberWeight, right - advance(old, m.t, kNumberWeight), baseline,
                                                           percentInk(*line.oldPercent)));
            }
            const QString percent = percentText(line.data.percent);
            scene.add(pen.faded(a * mix).leading(percent, m.t, kNumberWeight, right - advance(percent, m.t, kNumberWeight), baseline,
                                                 percentInk(line.data.percent)));
            if (resetShare <= 0.01) continue;
            // A passed reset fades its cell with the emptying bar; a new one comes in like a crossfade.
            const double cell = a * resetShare * line.resetPresence.value(time);
            const double resetMix = line.resetMix(time);
            if (!line.reset.empty() && cell * resetMix > 0.01) {
                scene.add(pen.faded(cell * resetMix)
                              .leading(text(line.reset), small, kLabelWeight, left + resetLeft, baseline, ink.secondary));
            }
            if (!line.oldReset.empty() && resetMix < 1) {
                scene.add(pen.faded(cell * (1 - resetMix))
                              .leading(text(line.oldReset), small, kLabelWeight, left + resetLeft, baseline, ink.secondary));
            }
        }
        y = sourceTop + presence * (ac::quotaBlockHeight(source, f, ownLine, q, time) + q.sourceGap());
    }
}

}  // namespace orb
