#pragma once

#include <QVariantMap>
#include <memory>

#include "orb/preview.hpp"

class TitleThemes;
class Strings;

namespace hero {
class Stage;
class Painter;
class Badge;
}

// The dashboard hero (DESIGN 4, 4c): the real `led` orb at its size with the ring effects capped at Soft and no
// readout, its center carrying the hero vehicle (Vehicles title theme) or the title badge (every other theme). Runs
// only while the dashboard shows it.
class HeroItem : public orb::PreviewItem {
    Q_OBJECT
    // The all-sources values of the latest engine tick (liveOutput, completions, newPeak).
    Q_PROPERTY(QVariantMap snapshot READ snapshot WRITE setSnapshot NOTIFY snapshotChanged)
    Q_PROPERTY(int effects READ effects WRITE setEffects NOTIFY effectsChanged)

public:
    explicit HeroItem(QQuickItem* parent = nullptr);
    ~HeroItem() override;
    static void setContext(const TitleThemes* titles, const Strings* strings);

    QVariantMap snapshot() const { return snapshot_; }
    void setSnapshot(const QVariantMap& snapshot);
    int effects() const { return effects_; }
    void setEffects(int level);
    void paint(QPainter* painter) override;

signals:
    void snapshotChanged();
    void effectsChanged();

protected:
    void tick() override;

private:
    QVariantMap snapshot_;
    int effects_ = 2;
    std::unique_ptr<hero::Stage> stage_;
    std::unique_ptr<hero::Painter> painter_;
    std::unique_ptr<hero::Badge> badge_;
    bool primed_ = false;
    int lastTitle_ = -2;
    double lastPulse_ = -1e9;
    QImage layer_;
};
