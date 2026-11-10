#include "orb/character/character_readings.hpp"

#include <QStringList>
#include <QVariantList>
#include <algorithm>
#include <cmath>

#include "format.hpp"
#include "strings.hpp"

namespace orb {

namespace {

double ramp(double value, double target, double dt, double seconds) {
    const double step = dt / seconds;
    return value < target ? std::min(target, value + step) : std::max(target, value - step);
}

// A unit format such as `%@ 毫秒` without its number: `毫秒`.
QString unitOf(const QString& pattern) { return QString(pattern).remove(QStringLiteral("%@")).trimmed(); }

}  // namespace

std::optional<CharacterReadings::Latency> CharacterReadings::latencyOf(const QVariant& ms, const Strings& strings) {
    bool ok = false;
    const double value = ms.isValid() && !ms.isNull() ? ms.toDouble(&ok) : 0;
    if (!ok || !std::isfinite(value) || value < 0) return std::nullopt;
    const auto [number, unitKey] = format::firstTokenLatency(value);
    Latency out;
    out.glyph = strings.t(QStringLiteral("character.latency.glyph"));
    out.number = number;
    out.unit = unitOf(strings.t(unitKey));
    out.spoken = strings.fmt(QStringLiteral("a11y.orb.latency"), {strings.fmt(unitKey, {number})});
    return out;
}

std::vector<CharacterReadings::Model> CharacterReadings::modelsOf(const QVariantList& list) {
    std::vector<Model> out;
    out.reserve(static_cast<std::size_t>(list.size()));
    for (const auto& item : list) {
        const auto row = item.toMap();
        Model m;
        m.model = row.value(QStringLiteral("model")).toString();
        if (m.model.isEmpty()) continue;
        m.speed = row.value(QStringLiteral("speed"), -1).toLongLong();
        if (m.speed < 0) m.speed = -1;
        m.vendor = row.value(QStringLiteral("vendor")).toString();
        out.push_back(std::move(m));
    }
    return out;
}

void CharacterReadings::apply(const QVariantMap& set, double time, const Strings& strings) {
    const auto latency = latencyOf(set.value(QStringLiteral("firstLatencyMs")), strings);
    latencyLive_ = latency.has_value();
    if (latency) latency_ = latency;
    auto models = modelsOf(set.value(QStringLiteral("models")).toList());
    if (!models.empty()) {
        models_ = std::move(models);
        modelsLastActive_ = time;
    }
}

double CharacterReadings::modelsTarget(double time) const { return !models_.empty() && time - modelsLastActive_ < kQuiet ? 1 : 0; }

void CharacterReadings::step(double time, double dt) {
    latencyOpacity_ = ramp(latencyOpacity_, latencyLive_ ? 1 : 0, dt, kRowFade);
    if (!latencyLive_ && latencyOpacity_ <= 0) latency_.reset();
    const double target = modelsTarget(time);
    modelsOpacity_ = ramp(modelsOpacity_, target, dt, target > modelsOpacity_ ? 0.25 : 0.6);
    // The rows stay while the card itself fades out after its quiet rule's fade.
    if (target == 0 && modelsOpacity_ <= 0 && time - modelsLastActive_ >= kQuiet + kKeep) models_.clear();
}

bool CharacterReadings::busy(double time) const {
    return std::abs(latencyOpacity_ - (latencyLive_ ? 1 : 0)) > 0.001 || std::abs(modelsOpacity_ - modelsTarget(time)) > 0.001;
}

double CharacterReadings::nextWake(double time) const {
    const double quiet = modelsLastActive_ + kQuiet;
    return modelsOpacity_ > 0 && quiet > time ? quiet : -1;
}

QString CharacterReadings::spokenModels(const Strings& strings) const {
    if (models_.empty()) return {};
    QStringList items;
    for (const auto& m : models_) {
        items << (m.speed >= 0 ? strings.fmt(QStringLiteral("a11y.orb.modelSpeed"), {m.model, QString::number(m.speed)}) : m.model);
    }
    return strings.fmt(QStringLiteral("a11y.orb.models"), {items.join(strings.t(QStringLiteral("list.separator")))});
}

QString CharacterReadings::key() const {
    QString out = latency_ ? latency_->number + latency_->unit : QString();
    for (const auto& m : models_) out += QLatin1Char('|') + m.model + QLatin1Char(':') + QString::number(m.speed);
    return out;
}

}  // namespace orb
