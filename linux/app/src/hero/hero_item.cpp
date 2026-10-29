#include "hero/hero_item.hpp"

#include <QPainter>
#include <QQuickWindow>
#include <QSettings>
#include <algorithm>
#include <cmath>

#include "heat.hpp"
#include "hero/hero_badge.hpp"
#include "hero/hero_painter.hpp"
#include "hero/hero_stage.hpp"
#include "strings.hpp"
#include "title_themes.hpp"

namespace {

const TitleThemes* gTitles = nullptr;
const Strings* gStrings = nullptr;

const char* const kVehicles[] = {"walk", "bicycle", "ebike", "motorcycle", "car", "sportsCar", "bulletTrain",
                                 "raceCar", "maglev", "jetliner", "fighterJet", "hypersonic", "rocket"};

hero::Level levelOf(int effects) {
    switch (effects) {
    case 0: return hero::Level::Off;
    case 1: return hero::Level::Soft;
    case 3: return hero::Level::Max;
    default: return hero::Level::Standard;
    }
}

}  // namespace

HeroItem::HeroItem(QQuickItem* parent) : orb::PreviewItem(parent) {
    showsReadout_ = false;
    smallEffects_ = false;
    effectsLevel_ = 1;
    setFps(60);
    setLiveValue(0);
    if (const auto* library = hero::Library::shared()) {
        stage_ = std::make_unique<hero::Stage>(*library);
        painter_ = std::make_unique<hero::Painter>(*library);
    }
    badge_ = std::make_unique<hero::Badge>();
}

HeroItem::~HeroItem() = default;

void HeroItem::setContext(const TitleThemes* titles, const Strings* strings) {
    gTitles = titles;
    gStrings = strings;
}

// Pulses and new peaks of every source, like the orb (DESIGN 4: same component, ring effects capped at Soft).
void HeroItem::setSnapshot(const QVariantMap& snapshot) {
    snapshot_ = snapshot;
    setLiveValue(snapshot.value(QStringLiteral("liveOutput")).toDouble());
    const double now = lastTick_;
    for (const auto& item : snapshot.value(QStringLiteral("completions")).toList()) {
        if (now - lastPulse_ < 0.15) continue;
        lastPulse_ = now;
        const auto c = item.toMap();
        const double output = c.value(QStringLiteral("output")).toDouble();
        const double speed = c.value(QStringLiteral("speed"), -1).toDouble();
        pulse(std::clamp(std::log10(std::max(1.0, output)) / 3.4, 0.3, 1.0), std::max(heat::of(speed >= 0 ? speed : liveValue(), redline()), 0.08));
    }
    if (snapshot.value(QStringLiteral("newPeak")).toBool()) celebrate();
    emit snapshotChanged();
}

void HeroItem::setEffects(int level) {
    level = std::clamp(level, 0, 3);
    if (level == effects_) return;
    effects_ = level;
    effectsLevel_ = std::min(level, 1);
    emit effectsChanged();
}

void HeroItem::tick() {
    orb::PreviewItem::tick();
    if (!window() || !stage_) return;
    const double t = lastTick_;
    const double R = redline();
    const int title = gauge_.title();
    // DESIGN 4c Idle: the last vehicle stays parked, also across relaunches.
    if (title != lastTitle_) {
        if (title >= 0 && lastTitle_ != -2) QSettings().setValue(QStringLiteral("heroLastTitle"), QString::fromLatin1(kVehicles[title]));
        lastTitle_ = title;
    }
    hero::Input input;
    input.time = t;
    if (title >= 0) input.title = stage_->library.indexOf(QString::fromLatin1(kVehicles[title]));
    if (input.title && *input.title < 0) input.title.reset();
    input.titleChangedAt = gauge_.titleChangedAt();
    input.threshold = orb::Gauge::titleThreshold(std::max(title, 0), R);
    if (title >= 0 && title < orb::Gauge::kTitles - 1) input.nextThreshold = orb::Gauge::titleThreshold(title + 1, R);
    input.redline = R;
    input.heat = gauge_.heat();
    input.overdrive = gauge_.overdrive();
    input.tier = static_cast<int>(gauge_.tier());
    input.level = levelOf(effects_);
    // The light band crosses the vehicle while the gauge's idle sweep passes the top of the ring.
    input.sweepEpoch = 0.63;
    const bool vehicle = !gTitles || gTitles->vehicleArt();
    const double side = stage_->library.orbFraction * diameter();
    const double dpr = window()->effectiveDevicePixelRatio();
    int demand = 0;
    if (vehicle) {
        if (!primed_) {
            // The dashboard opens onto a vehicle already under way: 1.2 s of motion before the first frame.
            std::optional<int> parked;
            const int last = stage_->library.indexOf(QSettings().value(QStringLiteral("heroLastTitle")).toString());
            if (last >= 0) parked = last;
            hero::Input warm = input;
            warm.time = t - 1.2;
            stage_->prime(warm, parked);
            for (double time = warm.time; time < t - 1e-6;) {
                time = std::min(t, time + 1.0 / 60);
                warm.time = time;
                stage_->update(warm);
            }
            primed_ = true;
        }
        stage_->update(input);
        const auto paint = hero::paintFor(stage_->heat, stage_->overdrive, stage_->wake.value, dark());
        layer_ = painter_->render(*stage_, paint, side, dpr);
        demand = stage_->demand();
    } else {
        hero::BadgeInput in;
        in.time = t;
        in.level = title;
        in.titleChangedAt = gauge_.titleChangedAt();
        in.text = gTitles ? gTitles->title(title < 0 ? 0 : title + 1) : QString();
        in.language = gStrings ? gStrings->language() : QStringLiteral("en");
        in.heat = gauge_.heat();
        in.overdrive = gauge_.overdrive();
        in.tier = static_cast<int>(gauge_.tier());
        in.effects = levelOf(effects_);
        in.sweepEpoch = 0.63;
        for (int k = 0; k < orb::Gauge::kTitles; ++k) {
            in.pipHeat[static_cast<std::size_t>(k)] = std::pow(orb::Gauge::titleThreshold(k, R) / R, 0.6);
        }
        badge_->update(in);
        layer_ = badge_->render(dark(), side, dpr);
        demand = badge_->demand();
        primed_ = false;
    }
    // Moving: the display rate; parked: 10 fps breathing, 30 fps during the sweep; Off: on change only.
    setFps(demand > 0 ? demand : 10);
}

void HeroItem::paint(QPainter* painter) {
    orb::PreviewItem::paint(painter);
    if (layer_.isNull() || !stage_) return;
    const double side = stage_->library.orbFraction * diameter();
    painter->setRenderHint(QPainter::SmoothPixmapTransform);
    painter->drawImage(QRectF((width() - side) / 2, (height() - side) / 2, side, side), layer_);
}
