#pragma once

#include <QString>
#include <QVariant>
#include <QVariantMap>
#include <optional>
#include <vector>

class Strings;

namespace orb {

// A `character` orb's 首字 row and models card (DESIGN 2a.6, Readouts; METRICS 6.9 Readouts of a set): what its set
// reads on each engine tick, with their quiet rules. The row shows while the value exists, fading in and out over
// 0.25 s; the card is loud while a model runs and keeps its last rows for the 10 s after. Times are seconds.
class CharacterReadings {
public:
    static constexpr double kQuiet = 10;
    static constexpr double kKeep = 1.2;
    static constexpr double kRowFade = 0.25;

    struct Latency {
        QString glyph, number, unit;  // `首字`, `850`, `毫秒`
        QString spoken;               // a11y.orb.latency: the shown value with its unit
        bool operator==(const Latency&) const = default;
    };
    struct Model {
        QString model;      // the full id
        qint64 speed = -1;  // whole tok/s; -1 while it has no timed response yet (–)
        QString vendor;     // for the accent dot; empty when unknown
        bool operator==(const Model&) const = default;
    };

    // One engine tick: the orb's score set (`firstLatencyMs`, `models`).
    void apply(const QVariantMap& set, double time, const Strings& strings);
    void step(double time, double dt);

    // The latency the row shows (kept while it fades out) and the row's opacity.
    const std::optional<Latency>& latency() const { return latency_; }
    double latencyOpacity() const { return latencyOpacity_; }
    // The card's rows, fastest first (kept for 10 s after the last running model), and its quiet fade.
    const std::vector<Model>& models() const { return models_; }
    double modelsOpacity() const { return modelsOpacity_; }

    bool busy(double time) const;
    // When the quiet rule next changes something, -1 for never.
    double nextWake(double time) const;
    // a11y.orb.models with every running model, drawn or not; empty without one.
    QString spokenModels(const Strings& strings) const;
    // Changes whenever what the cards show does.
    QString key() const;

    static std::optional<Latency> latencyOf(const QVariant& ms, const Strings& strings);
    static std::vector<Model> modelsOf(const QVariantList& list);

private:
    double modelsTarget(double time) const;

    std::optional<Latency> latency_;
    bool latencyLive_ = false;
    double latencyOpacity_ = 0;
    std::vector<Model> models_;
    double modelsLastActive_ = -1e9;
    double modelsOpacity_ = 0;
};

}  // namespace orb
