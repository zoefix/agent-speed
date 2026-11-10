#include "orb/character/character_models.hpp"

#include <algorithm>

namespace orb {

namespace ac = agentspeed::character;
using cards::advance;
using cards::capHeight;
using cards::CardMetrics;
using cards::kUnitScale;
using cards::Pen;

namespace {

constexpr int kNameWeight = 500, kNumberWeight = 600, kUnitWeight = 500;

double dotSize(const CardMetrics& m) { return 0.62 * m.t; }
double nameIndent(const CardMetrics& m) { return dotSize(m) + 0.42 * m.t; }
double nameGap(const CardMetrics& m) { return 0.8 * m.t; }
// Lines are 0.3 t apart.
double pitch(const CardMetrics& m) { return CardMetrics::rowHeight(m.t) + 0.3 * m.t; }

QString numberOf(const ac::ModelRow& row) { return row.speed ? QString::number(*row.speed) : QStringLiteral("–"); }

}  // namespace

void ModelsCard::sync(const std::vector<ac::ModelRow>& rows, int limit, double time, bool still) { motion_.sync(rows, limit, time, still); }

const QString& ModelsCard::displayName(const std::string& model) const {
    if (names_.size() > 64) names_.clear();
    auto it = names_.find(model);
    if (it == names_.end()) it = names_.emplace(model, QString::fromStdString(ac::modelDisplayName(model))).first;
    return it->second;
}

// The "+N" line: `character.models.more`, or just `+N` beside the dots-and-speeds rows.
QString ModelsCard::moreText(const QString& pattern, int count, bool names) const {
    const QString n = QString::number(count);
    return names ? QString(pattern).replace(QStringLiteral("%@"), n) : QLatin1Char('+') + n;
}

ModelsCard::Columns ModelsCard::columns(const CardMetrics& m, const Texts& texts) const {
    Columns c;
    bool unit = false;
    for (const auto& line : motion_.lines()) {
        if (line.leaving() || !line.row) continue;
        c.number = std::max(c.number, advance(numberOf(*line.row), m.t, kNumberWeight));
        c.names = std::max(c.names, advance(displayName(line.row->model), m.t, kNameWeight));
        if (line.to == 0 && line.row->speed) unit = true;
    }
    c.unit = unit ? m.cellGap() + advance(texts.unit, kUnitScale * m.t, kUnitWeight) : 0;
    return c;
}

ac::ModelsShape ModelsCard::shape(const CardMetrics& m, const Texts& texts) const {
    ac::ModelsShape s;
    s.t = m.t;
    const int count = motion_.count();
    if (count == 0) return s;
    const auto c = columns(m, texts);
    const double lead = 2 * m.padX + nameIndent(m);
    const int more = motion_.more();
    s.fixed = lead + nameGap(m) + c.number + c.unit;
    s.names = c.names;
    s.floor = more > 0 ? lead + advance(moreText(texts.more, more, true), m.t, kNameWeight) : 0;
    s.dots = std::max(lead + c.number + c.unit, more > 0 ? lead + advance(moreText(texts.more, more, false), m.t, kNameWeight) : 0.0);
    s.h = 2 * m.padY + count * CardMetrics::rowHeight(m.t) + (count - 1) * 0.3 * m.t;
    return s;
}

void ModelsCard::draw(Scene& scene, const Pen& pen, ac::CardSize size, const ac::ModelsForm& form, const CardMetrics& m,
                      const Texts& texts, const Ink& ink, double time) {
    const auto c = columns(m, texts);
    const double left = -size.w / 2 + m.padX, top = -size.h / 2 + m.padY;
    const double numberRight = size.w / 2 - m.padX - c.unit;
    const double nameLeft = left + nameIndent(m);
    const double nameRoom = std::min(form.nameWidth, numberRight - c.number - nameGap(m) - nameLeft);
    const double dot = dotSize(m);
    const double cap = capHeight(m.t, kNameWeight);
    const auto measure = [&m](std::string_view s) {
        return advance(QString::fromUtf8(s.data(), static_cast<qsizetype>(s.size())), m.t, kNameWeight);
    };
    for (const auto& line : motion_.lines()) {
        const double a = line.alpha(time);
        if (a <= 0.01) continue;
        const Pen p = pen.faded(a);
        const double baseline = top + line.position(time) * pitch(m) + CardMetrics::baseline(m.t);
        if (!line.row) {
            scene.add(p.leading(moreText(texts.more, line.more, form.names), m.t, kNameWeight, nameLeft, baseline, ink.secondary));
            continue;
        }
        const auto& row = *line.row;
        const Rgba dotColor = row.accent ? Rgba::hex(*row.accent) : ink.secondary;
        scene.add(Instance::circle(p.point(left + dot / 2, baseline - cap / 2), dot / 2 * p.scale, dotColor.opacity(p.alpha)), Blend::Over);
        if (form.names && nameRoom > 0) {
            const QString& name = displayName(row.model);
            const QString shown = advance(name, m.t, kNameWeight) <= nameRoom
                                      ? name
                                      : QString::fromStdString(ac::cutAtEnd(name.toStdString(), nameRoom, measure));
            scene.add(p.leading(shown, m.t, kNameWeight, nameLeft, baseline, ink.number));
        }
        const QString number = numberOf(row);
        scene.add(p.leading(number, m.t, kNumberWeight, numberRight - advance(number, m.t, kNumberWeight), baseline,
                            row.speed ? ink.number : ink.secondary));
    }
    if (c.unit > 0) {
        scene.add(pen.leading(texts.unit, kUnitScale * m.t, kUnitWeight, numberRight + m.cellGap(), top + CardMetrics::baseline(m.t),
                              ink.secondary));
    }
}

}  // namespace orb
