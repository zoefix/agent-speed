#include "task_card.hpp"

#include <algorithm>
#include <cmath>
#include <numeric>

#include "strings.hpp"

namespace task_card {

namespace {

using agentspeed::tasks::Dims;

constexpr int kMax[5] = {40, 25, 15, 10, 10};
constexpr const char* kDimKeys[5] = {"verify", "converge", "recover", "efficiency", "complete"};
constexpr std::int64_t kMinTasks = 40;

QString checkName(const Strings& s, const std::optional<std::string>& suite, const std::optional<std::string>& cls) {
    if (suite) return QString::fromStdString(*suite);
    const auto c = QString::fromStdString(cls.value_or("test"));
    return s.t(QStringLiteral("task.check.") + (c == QStringLiteral("build") || c == QStringLiteral("check") ? c : QStringLiteral("test")));
}

QString status(const agentspeed::AgentTask& t, const Strings& s) {
    const auto state = t.value.state.value_or("none");
    if (!t.task || state == "none") return s.t(QStringLiteral("task.status.none"));
    if (state == "unverified") return s.t(QStringLiteral("task.status.unverified"));
    if (state == "failing" && t.task->openFailure) {
        const auto& f = *t.task->openFailure;
        const auto check = checkName(s, f.suite, f.cls);
        if (f.edited) return fmt(s, QStringLiteral("task.status.failing.edited"), {check});
        if (!f.counted) return fmt(s, QStringLiteral("task.status.failing.noCount"), {check});
        return fmt(s, QStringLiteral("task.status.failing"), {check, static_cast<qlonglong>(f.errors), static_cast<qlonglong>(f.peak)});
    }
    if ((state == "passing" || state == "pending") && t.task->lastPass) {
        const auto& p = *t.task->lastPass;
        const auto check = checkName(s, p.suite, p.cls);
        if (state == "pending") return fmt(s, QStringLiteral("task.status.pending"), {check});
        return fmt(s, p.buildOnly ? QStringLiteral("task.status.passing.buildOnly") : QStringLiteral("task.status.passing"), {check});
    }
    return s.t(QStringLiteral("task.status.none"));
}

QString flag(const agentspeed::tasks::Model& m, const Strings& s) {
    if (m.calibrated) return fmt(s, QStringLiteral("task.predict.cell.calibrated"), {static_cast<qlonglong>(m.tasks)});
    if (m.tasks < kMinTasks) return fmt(s, QStringLiteral("task.predict.cell.prior"), {static_cast<qlonglong>(m.tasks), static_cast<qlonglong>(kMinTasks)});
    return s.t(QStringLiteral("task.predict.cell.priorOnly"));
}

}  // namespace

QString fmt(const Strings& strings, const QString& key, const QVariantList& args) {
    const auto pattern = strings.t(key);
    QString out;
    int next = 0;
    for (int i = 0; i < pattern.size(); ++i) {
        if (pattern[i] == QLatin1Char('%') && i + 1 < pattern.size()) {
            const auto c = pattern[i + 1];
            if (c == QLatin1Char('%')) {
                out += QLatin1Char('%');
                ++i;
                continue;
            }
            if (c == QLatin1Char('@') || c == QLatin1Char('d')) {
                out += next < args.size() ? args[next].toString() : QString();
                ++next;
                ++i;
                continue;
            }
        }
        out += pattern[i];
    }
    return out;
}

std::array<int, 5> shownDims(const Dims& dims, std::int64_t shown) {
    const double values[5] = {dims.V, dims.C, dims.R, dims.E, dims.K};
    std::array<int, 5> out{};
    int sum = 0;
    for (int k = 0; k < 5; ++k) {
        out[k] = static_cast<int>(std::floor(values[k] + 1e-9));
        sum += out[k];
    }
    std::array<int, 5> order{0, 1, 2, 3, 4};
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) { return values[a] - out[a] > values[b] - out[b]; });
    for (int i = 0; i < 5 && sum < shown; ++i) {
        if (values[order[i]] - out[order[i]] <= 1e-9) break;
        ++out[order[i]];
        ++sum;
    }
    return out;
}

QString percent(double p) {
    const double v = p * 100;
    if (v < 0.5) return QStringLiteral("<1%");
    if (v >= 99.5) return QStringLiteral(">99%");
    return QString::number(agentspeed::tasks::roundHalfAway(v)) + QLatin1Char('%');
}

void cell(const agentspeed::AgentRow& row, QVariantMap& out) {
    const auto* t = row.task ? &*row.task : nullptr;
    out.insert(QStringLiteral("scored"), t != nullptr);
    out.insert(QStringLiteral("iq"), t && t->value.shown ? static_cast<int>(*t->value.shown) : -1);
    out.insert(QStringLiteral("iqForm"), t ? QString::fromStdString(t->value.form) : QStringLiteral("none"));
    const auto state = t ? t->value.state.value_or("") : std::string();
    out.insert(QStringLiteral("iqProof"), t && t->value.shown && (state == "unverified" || state == "pending"));
}

QVariantMap card(const agentspeed::AgentRow& row, const Strings& s, const QString& sourceName) {
    QVariantMap card;
    const auto project = row.project ? QString::fromStdString(*row.project) : sourceName;
    card.insert(QStringLiteral("project"), project);
    card.insert(QStringLiteral("source"), QString::fromUtf8(agentspeed::sourceId(row.source)));
    QStringList meta;
    if (row.model) meta << QString::fromStdString(*row.model);
    meta << sourceName;
    card.insert(QStringLiteral("meta"), meta.join(QStringLiteral(" · ")));
    QStringList facts;
    if (row.effort) facts << fmt(s, QStringLiteral("effort.tooltip"), {QString::fromStdString(*row.effort)});
    if (row.speed) facts << fmt(s, QStringLiteral("speed.tooltip"), {QString::fromStdString(*row.speed)});
    card.insert(QStringLiteral("facts"), facts);
    card.insert(QStringLiteral("footnote"), s.t(QStringLiteral("task.card.footnote")));
    if (!row.task) {
        card.insert(QStringLiteral("supported"), false);
        card.insert(QStringLiteral("unsupported"), s.t(QStringLiteral("task.unsupported")));
        return card;
    }
    const auto& t = *row.task;
    const auto state = QString::fromStdString(t.value.state.value_or("none"));
    card.insert(QStringLiteral("supported"), true);
    card.insert(QStringLiteral("state"), state);
    card.insert(QStringLiteral("chip"), s.t(QStringLiteral("task.state.") + state));
    card.insert(QStringLiteral("form"), QString::fromStdString(t.value.form));
    card.insert(QStringLiteral("eyebrow"), t.value.form == "resting" ? s.t(QStringLiteral("phase.idle")) : QString());
    const auto shown = t.value.shown;
    card.insert(QStringLiteral("number"), shown ? QString::number(*shown) : QStringLiteral("–"));
    card.insert(QStringLiteral("proof"), shown && (state == QStringLiteral("unverified") || state == QStringLiteral("pending")));
    card.insert(QStringLiteral("status"), status(t, s));

    const bool unproven = !t.task || state == QStringLiteral("none") || state == QStringLiteral("unverified");
    const bool pending = state == QStringLiteral("pending");
    const auto dims = t.task ? t.task->dims : Dims{};
    const auto ints = shownDims(dims, shown.value_or(0));
    const double held = t.task && t.task->vHeld ? *t.task->vHeld : 0;
    QVariantList comp, rows;
    for (int k = 0; k < 5; ++k) {
        QVariantMap seg;
        seg.insert(QStringLiteral("value"), ints[k]);
        seg.insert(QStringLiteral("ghost"), pending && k == 0 ? held : 0.0);
        comp.push_back(seg);
        QVariantMap dim;
        dim.insert(QStringLiteral("label"), s.t(QStringLiteral("task.dim.") + kDimKeys[k]));
        dim.insert(QStringLiteral("desc"), s.t(QStringLiteral("task.dim.") + kDimKeys[k] + QStringLiteral(".desc")));
        dim.insert(QStringLiteral("max"), kMax[k]);
        dim.insert(QStringLiteral("value"), ints[k]);
        QString mode = QStringLiteral("value");
        if (unproven && k != 3) mode = QStringLiteral("unverified");
        if (k == 3 && !shown) mode = QStringLiteral("none");
        if (pending && k == 0) mode = QStringLiteral("pending");
        dim.insert(QStringLiteral("mode"), mode);
        dim.insert(QStringLiteral("note"), mode == QStringLiteral("unverified") ? s.t(QStringLiteral("task.state.unverified"))
                                           : mode == QStringLiteral("pending")  ? s.t(QStringLiteral("task.state.pending"))
                                                                                : QString());
        dim.insert(QStringLiteral("ghost"), pending && k == 0 ? held / kMax[0] : 0.0);
        rows.push_back(dim);
    }
    card.insert(QStringLiteral("comp"), comp);
    card.insert(QStringLiteral("dims"), rows);
    card.insert(QStringLiteral("dimsTitle"), s.t(QStringLiteral("task.dims.title")));

    QVariantList cells;
    if (t.value.form == "live" && t.predictions) {
        const auto& p = *t.predictions;
        const auto add = [&](double value, const QString& label, const agentspeed::tasks::Model& model) {
            QVariantMap c;
            c.insert(QStringLiteral("pct"), percent(value));
            c.insert(QStringLiteral("label"), label);
            c.insert(QStringLiteral("flag"), flag(model, s));
            cells.push_back(c);
        };
        add(p.success, s.t(QStringLiteral("task.predict.success")), t.models.success);
        add(p.stall, s.t(QStringLiteral("task.predict.stall")), t.models.stall);
        if (p.fix && state == QStringLiteral("failing")) add(*p.fix, s.t(QStringLiteral("task.predict.fixFails")), t.models.fix);
        card.insert(QStringLiteral("predictTitle"), s.t(QStringLiteral("task.predict.title")));
        card.insert(QStringLiteral("predictNote"), fmt(s, QStringLiteral("task.predict.note"), {QVariant(sourceName), static_cast<qlonglong>(kMinTasks)}));
    }
    card.insert(QStringLiteral("predict"), cells);

    QVariantList behaviour;
    const auto addLine = [&](const QString& text, bool warn) {
        QVariantMap line;
        line.insert(QStringLiteral("text"), text);
        line.insert(QStringLiteral("warn"), warn);
        behaviour.push_back(line);
    };
    if (t.workSignals.looping) addLine(fmt(s, QStringLiteral("work.looping"), {static_cast<qlonglong>(*t.workSignals.looping)}), true);
    if (t.workSignals.repeating) addLine(fmt(s, QStringLiteral("work.repeating"), {static_cast<qlonglong>(*t.workSignals.repeating)}), false);
    if (t.task && t.task->counts.oscillations > 0) {
        addLine(fmt(s, QStringLiteral("task.signal.oscillation"), {static_cast<qlonglong>(t.task->counts.oscillations)}), false);
    }
    if (t.workSignals.novelty) {
        addLine(fmt(s, QStringLiteral("work.summary"),
                   {percent(*t.workSignals.novelty), static_cast<qlonglong>(t.workSignals.failed), static_cast<qlonglong>(t.workSignals.recovered)}),
               false);
    }
    card.insert(QStringLiteral("signals"), behaviour);
    card.insert(QStringLiteral("signalsTitle"), s.t(QStringLiteral("task.signals.title")));
    return card;
}

}  // namespace task_card
