#include "perf_card.hpp"

#include <cmath>

#include "format.hpp"
#include "strings.hpp"

namespace perf_card {

namespace {

using agentspeed::perf::Component;
using agentspeed::perf::Id;

struct Spec {
    Id id;
    const char* key;
};
// DESIGN 12 Components: the speed group (T, L, R), then the efficiency group (N, U, S, H).
constexpr Spec kSpeed[] = {{Id::T, "throughput"}, {Id::L, "latency"}, {Id::R, "reasoning"}};
constexpr Spec kEfficiency[] = {{Id::N, "pace"}, {Id::U, "toolTime"}, {Id::S, "toolSuccess"}, {Id::H, "cache"}};

std::int64_t whole(double value) { return static_cast<std::int64_t>(std::floor(value + 0.5)); }

}  // namespace

QString measureText(const Strings& s, Id id, double m) {
    switch (id) {
        case Id::T:
        case Id::R: return format::speed(m) + QLatin1Char(' ') + s.t(QStringLiteral("unit.tokS"));
        case Id::L:
        case Id::U: {
            const auto [number, unit] = format::perfDuration(m);
            return s.fmt(unit, {number});
        }
        case Id::N: return s.fmt(QStringLiteral("perf.unit.perMin"), {format::oneDecimalBelowTen(m)});
        case Id::S:
        case Id::H: return format::cappedPercent(m);
    }
    return {};
}

QString tierWord(const Strings& s, std::int64_t shown) {
    if (shown >= 2000) return s.t(QStringLiteral("perf.tier.veryFast"));
    if (shown >= 1300) return s.t(QStringLiteral("perf.tier.fast"));
    if (shown >= 700) return s.t(QStringLiteral("perf.tier.standard"));
    return s.t(QStringLiteral("perf.tier.slow"));
}

namespace {

QString ratioText(std::int64_t shown) { return QString::number(static_cast<double>(shown) / 1000.0, 'f', 2); }

QString signedPercent(std::int64_t p) {
    if (p > 0) return QStringLiteral("+%1%").arg(p);
    if (p < 0) return QStringLiteral("−%1%").arg(-p);
    return QStringLiteral("±0%");
}

}  // namespace

QString scoreWords(const Strings& s, std::int64_t shown, bool provisional, bool resting, std::int64_t top) {
    auto words = format::perfFull(shown);
    if (shown >= 100'000) words = QString::number(shown);
    if (provisional) words = s.fmt(QStringLiteral("a11y.perf.provisional"), {words});
    if (resting) words = s.fmt(QStringLiteral("a11y.perf.idle"), {words});
    if (top >= 2) words = s.fmt(QStringLiteral("a11y.perf.top"), {words, static_cast<qlonglong>(top)});
    return words;
}

void cell(const agentspeed::AgentRow& row, const Strings& s, QVariantMap& out) {
    const auto* e = row.perf ? &row.perf->score : nullptr;
    const bool scored = e && e->shown;
    out.insert(QStringLiteral("perf"), scored ? static_cast<qlonglong>(*e->shown) : -1);
    out.insert(QStringLiteral("perfText"), scored ? format::perfCompact(*e->shown) : QStringLiteral("–"));
    out.insert(QStringLiteral("perfForm"), QString::fromUtf8(agentspeed::perf::formId(e ? e->form : agentspeed::perf::Form::None)));
    out.insert(QStringLiteral("perfProvisional"), scored && e->provisional);
    out.insert(QStringLiteral("perfA11y"), scored ? s.fmt(QStringLiteral("a11y.agent.intel"),
                                                          {scoreWords(s, *e->shown, e->provisional, e->form == agentspeed::perf::Form::Resting)})
                                                 : s.t(QStringLiteral("intel.none")));
}

QVariantMap card(const agentspeed::AgentRow& row, const Strings& s, const QString& sourceName) {
    QVariantMap card;
    agentspeed::perf::Evaluation empty;
    const auto& e = row.perf ? row.perf->score : empty;
    const bool scored = e.shown.has_value();
    const auto shown = e.shown.value_or(0);
    const bool resting = e.form == agentspeed::perf::Form::Resting;
    QStringList a11y;

    card.insert(QStringLiteral("project"), row.project ? QString::fromStdString(*row.project) : sourceName);
    card.insert(QStringLiteral("source"), QString::fromUtf8(agentspeed::sourceId(row.source)));
    QStringList meta;
    if (e.model) {
        meta << QString::fromStdString(*e.model);
    } else if (row.model) {
        meta << QString::fromStdString(*row.model);
    }
    meta << sourceName;
    card.insert(QStringLiteral("meta"), meta.join(QStringLiteral(" · ")));
    card.insert(QStringLiteral("provisional"), scored && e.provisional);
    card.insert(QStringLiteral("provisionalChip"), s.t(QStringLiteral("perf.provisional")));
    card.insert(QStringLiteral("form"), QString::fromUtf8(agentspeed::perf::formId(e.form)));
    card.insert(QStringLiteral("eyebrow"), resting ? s.t(QStringLiteral("phase.idle")) : QString());
    card.insert(QStringLiteral("shown"), scored ? static_cast<qlonglong>(shown) : -1);
    card.insert(QStringLiteral("number"), scored ? format::perfFull(shown) : QStringLiteral("–"));
    card.insert(QStringLiteral("scale"), scored ? format::perfScale(static_cast<double>(shown)) : 0.0);

    // The usual chip (比平常).
    QVariantMap usual;
    QString usualWords;
    if (scored && e.vsUsualPercent) {
        const auto p = *e.vsUsualPercent;
        usual.insert(QStringLiteral("text"), s.fmt(QStringLiteral("perf.usual"), {signedPercent(p)}));
        usual.insert(QStringLiteral("kind"), p > 0 ? QStringLiteral("up") : QStringLiteral("neutral"));
        usualWords = p > 0   ? s.fmt(QStringLiteral("a11y.perf.usual.up"), {QStringLiteral("%1%").arg(p)})
                     : p < 0 ? s.fmt(QStringLiteral("a11y.perf.usual.down"), {QStringLiteral("%1%").arg(-p)})
                             : s.t(QStringLiteral("a11y.perf.usual.same"));
    } else if (scored) {
        usual.insert(QStringLiteral("text"), s.t(QStringLiteral("perf.usual.none")));
        usual.insert(QStringLiteral("kind"), QStringLiteral("none"));
        usualWords = s.t(QStringLiteral("perf.usual.none"));
    }
    card.insert(QStringLiteral("usual"), usual);

    // The summary: the slowest present component when two or more are present.
    QString summary;
    if (scored) {
        const Component* slowest = nullptr;
        int present = 0;
        for (const auto* list : {kSpeed, kEfficiency}) {
            const auto count = list == kSpeed ? std::size(kSpeed) : std::size(kEfficiency);
            for (std::size_t i = 0; i < count; ++i) {
                const auto& c = e.components[static_cast<std::size_t>(list[i].id)];
                if (!c.present) continue;
                ++present;
                if (!slowest || *c.subScore < *slowest->subScore) slowest = &c;
            }
        }
        const auto tier = tierWord(s, shown);
        if (present >= 2 && slowest && *slowest->subScore < 1000) {
            QString label;
            for (const auto& spec : kSpeed)
                if (spec.id == slowest->id) label = s.t(QStringLiteral("perf.comp.") + spec.key);
            for (const auto& spec : kEfficiency)
                if (spec.id == slowest->id) label = s.t(QStringLiteral("perf.comp.") + spec.key);
            summary = s.fmt(QStringLiteral("perf.summary.slowest"), {tier, ratioText(shown), label});
        } else if (present >= 2) {
            summary = s.fmt(QStringLiteral("perf.summary.allAbove"), {tier, ratioText(shown)});
        } else {
            summary = s.fmt(QStringLiteral("perf.summary"), {tier, ratioText(shown)});
        }
        a11y << s.fmt(QStringLiteral("a11y.perf.score"), {scoreWords(s, shown, e.provisional, resting), tier}) << usualWords << summary;
    } else {
        summary = s.fmt(QStringLiteral("perf.status.none"), {static_cast<qlonglong>(e.n), static_cast<qlonglong>(agentspeed::perf::kMinSamples)});
        a11y << s.t(QStringLiteral("intel.none")) << summary;
    }
    card.insert(QStringLiteral("summary"), summary);
    const auto hint = scored && e.provisional
                          ? s.fmt(QStringLiteral("perf.provisional.hint"), {static_cast<qlonglong>(e.n), static_cast<qlonglong>(agentspeed::perf::kProvisionalBelow)})
                          : QString();
    card.insert(QStringLiteral("provisionalHint"), hint);
    if (!hint.isEmpty()) a11y << hint;

    // Components, two groups.
    QVariantList groups;
    if (scored) {
        const auto addGroup = [&](const QString& title, const Spec* list, std::size_t count) {
            QVariantList rows;
            for (std::size_t i = 0; i < count; ++i) {
                const auto& c = e.components[static_cast<std::size_t>(list[i].id)];
                const auto key = QStringLiteral("perf.comp.") + list[i].key;
                QVariantMap r;
                r.insert(QStringLiteral("label"), s.t(key));
                r.insert(QStringLiteral("present"), c.present);
                if (c.present) {
                    const auto value = measureText(s, c.id, *c.measure);
                    const auto sub = format::perfFull(whole(*c.subScore));
                    r.insert(QStringLiteral("value"), value);
                    r.insert(QStringLiteral("sub"), sub);
                    r.insert(QStringLiteral("f"), format::perfScale(*c.subScore));
                    auto desc = s.t(key + QStringLiteral(".desc"));
                    if (c.id == Id::N) desc += QStringLiteral(" · ") + s.fmt(QStringLiteral("perf.comp.pace.count"), {static_cast<qlonglong>(e.tools)});
                    r.insert(QStringLiteral("desc"), desc);
                    a11y << s.fmt(QStringLiteral("a11y.perf.comp"), {s.t(key), value, sub}) + QStringLiteral(". ") + desc;
                } else {
                    const bool notRecorded = c.reason == agentspeed::perf::Reason::NotRecorded;
                    const auto reason = s.t(notRecorded ? QStringLiteral("perf.reason.notRecorded") : QStringLiteral("perf.reason.fewSamples"));
                    const auto why = notRecorded ? s.fmt(QStringLiteral("perf.reason.notRecorded.hint"), {sourceName})
                                                 : s.fmt(QStringLiteral("perf.reason.fewSamples.hint"), {static_cast<qlonglong>(agentspeed::perf::kMinSamples)});
                    r.insert(QStringLiteral("value"), reason);
                    r.insert(QStringLiteral("sub"), QStringLiteral("–"));
                    r.insert(QStringLiteral("f"), 0.0);
                    r.insert(QStringLiteral("desc"), why);
                    a11y << s.fmt(QStringLiteral("a11y.perf.comp.absent"), {s.t(key), reason}) + QStringLiteral(". ") + why;
                }
                rows.push_back(r);
            }
            groups.push_back(QVariantMap{{QStringLiteral("title"), title}, {QStringLiteral("rows"), rows}});
        };
        addGroup(s.t(QStringLiteral("perf.group.speed")), kSpeed, std::size(kSpeed));
        addGroup(s.t(QStringLiteral("perf.group.efficiency")), kEfficiency, std::size(kEfficiency));
    }
    card.insert(QStringLiteral("groups"), groups);

    // 行為: the work signals.
    QVariantList behaviour;
    if (row.perf) {
        const auto& w = row.perf->workSignals;
        const auto addLine = [&](const QString& text, bool warn) {
            behaviour.push_back(QVariantMap{{QStringLiteral("text"), text}, {QStringLiteral("warn"), warn}});
            a11y << text;
        };
        if (w.looping) addLine(s.fmt(QStringLiteral("work.looping"), {static_cast<qlonglong>(*w.looping)}), true);
        if (w.repeating) addLine(s.fmt(QStringLiteral("work.repeating"), {static_cast<qlonglong>(*w.repeating)}), false);
        if (w.novelty) {
            addLine(s.fmt(QStringLiteral("work.summary"), {QString::number(whole(*w.novelty * 100)) + QLatin1Char('%'), static_cast<qlonglong>(w.failed),
                                                          static_cast<qlonglong>(w.recovered)}),
                    false);
        }
    }
    card.insert(QStringLiteral("signals"), behaviour);
    card.insert(QStringLiteral("signalsTitle"), s.t(QStringLiteral("perf.signals.title")));

    QStringList facts;
    if (scored) facts << s.fmt(QStringLiteral("perf.card.sample"), {static_cast<qlonglong>(e.n)});
    // Effort and speed, each spelled as its token (unrecorded when missing), and the top rung's explanation.
    if (row.effort || row.speed) {
        const auto speedValue = [&](const std::string& v) {
            return v == "standard" || v == "fast" || v == "ultrafast" || v == "highspeed" ? s.t(QStringLiteral("speed.value.") + QString::fromStdString(v))
                                                                                         : QString::fromStdString(v);
        };
        facts << s.fmt(QStringLiteral("effort.tooltip"), {row.effort ? QString::fromStdString(*row.effort) : s.t(QStringLiteral("modes.unrecorded"))});
        facts << s.fmt(QStringLiteral("speed.tooltip"), {row.speed ? speedValue(*row.speed) : s.t(QStringLiteral("modes.unrecorded"))});
        if (row.effort == "ultra") facts << s.t(QStringLiteral("effort.ultra.detail"));
        if (row.effort == "ultracode") facts << s.t(QStringLiteral("effort.ultracode.detail"));
    }
    card.insert(QStringLiteral("facts"), facts);
    a11y << facts;
    const auto footnote = s.t(QStringLiteral("perf.card.footnote"));
    card.insert(QStringLiteral("footnote"), footnote);
    a11y << footnote;
    a11y.removeAll(QString());
    card.insert(QStringLiteral("a11y"), a11y.join(QLatin1Char('\n')));
    return card;
}

}  // namespace perf_card
