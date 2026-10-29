#include "orb/preview.hpp"

#include <QOffscreenSurface>
#include <QOpenGLContext>
#include <QOpenGLFramebufferObject>
#include <QPainter>
#include <QQuickWindow>
#include <QSurfaceFormat>
#include <cmath>
#include <map>

#include "heat.hpp"
#include "orb/body.hpp"
#include "orb/renderer.hpp"
#include "orb/shared_layers.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kLoop = 8, kRedline = 400;

QString& unitText() {
    static QString unit;
    return unit;
}

// One OpenGL context, renderer and clock for every preview.
class Engine {
public:
    // Never destroyed: its OpenGL context must not outlive the application object at exit.
    static Engine& shared() {
        static auto* engine = new Engine;
        return *engine;
    }

    double now() const { return static_cast<double>(clock_.nsecsElapsed()) / 1e9; }

    // Text measuring creates textures, so the context is current for the whole frame.
    bool begin() { return ready_ && context_.makeCurrent(&surface_); }
    void end() { context_.doneCurrent(); }

    QImage render(const Scene& scene, StyleKind kind, bool dark, double s, double dpr) {
        const double side = 1.5 * s;
        const QSize pixels(qCeil(side * dpr), qCeil(side * dpr));
        auto& fbo = fbos_[{pixels.width(), pixels.height()}];
        if (!fbo) fbo = std::make_unique<QOpenGLFramebufferObject>(pixels);
        const BodyKey key{static_cast<int>(kind), dark, qRound(s * dpr)};
        if (key != body_) {
            renderer_.setBody(renderBody(s, side, dpr, Tokens::forDark(dark), false, kind), QRectF(0, 0, side, side));
            body_ = key;
        }
        fbo->bind();
        renderer_.render(scene, QSizeF(side, side), dpr, QPointF(side / 2, side / 2));
        fbo->release();
        QImage image = fbo->toImage();
        image.setDevicePixelRatio(dpr);
        return image;
    }

    MeasureText measure(double dpr) {
        return [this, dpr](const TextDraw& text) { return renderer_.measure(text, dpr).width; };
    }

private:
    using BodyKey = std::tuple<int, bool, int>;

    Engine() {
        clock_.start();
        QSurfaceFormat format;
        format.setRenderableType(QSurfaceFormat::OpenGL);
        format.setVersion(3, 3);
        format.setProfile(QSurfaceFormat::CoreProfile);
        format.setAlphaBufferSize(8);
        context_.setFormat(format);
        surface_.setFormat(format);
        surface_.create();
        ready_ = context_.create() && surface_.isValid() && context_.makeCurrent(&surface_) && renderer_.initialize();
        if (ready_) context_.doneCurrent();
    }

    QElapsedTimer clock_;
    QOffscreenSurface surface_;
    QOpenGLContext context_;
    Renderer renderer_;
    std::map<std::pair<int, int>, std::unique_ptr<QOpenGLFramebufferObject>> fbos_;
    BodyKey body_{-1, false, 0};
    bool ready_ = false;
};

}  // namespace

PreviewItem::PreviewItem(QQuickItem* parent) : QQuickPaintedItem(parent), style_(Style::make(kind_)) {
    setAntialiasing(true);
    timer_.setInterval(1000 / 24);
    connect(&timer_, &QTimer::timeout, this, &PreviewItem::tick);
}

PreviewItem::~PreviewItem() = default;

void PreviewItem::setUnit(QString unit) { unitText() = std::move(unit); }

void PreviewItem::setStyle(const QString& style) {
    const StyleKind kind = styleFromKey(style);
    if (kind == kind_) return;
    kind_ = kind;
    style_ = Style::make(kind);
    fresh_ = true;
    emit styleChanged();
    tick();
}

void PreviewItem::setDiameter(double diameter) {
    if (std::abs(diameter - diameter_) < 0.01) return;
    diameter_ = diameter;
    emit diameterChanged();
    tick();
}

void PreviewItem::setOffset(double offset) {
    if (offset == offset_) return;
    offset_ = offset;
    emit offsetChanged();
}

void PreviewItem::setDark(bool dark) {
    if (dark == dark_) return;
    dark_ = dark;
    emit darkChanged();
    tick();
}

void PreviewItem::setRunning(bool running) {
    if (running == running_) return;
    running_ = running;
    emit runningChanged();
    updateTimer();
}

void PreviewItem::itemChange(ItemChange change, const ItemChangeData& value) {
    QQuickPaintedItem::itemChange(change, value);
    if (change == ItemVisibleHasChanged || change == ItemSceneChange) {
        updateTimer();
        if (change == ItemSceneChange && value.window) {
            connect(value.window, &QWindow::visibleChanged, this, &PreviewItem::updateTimer, Qt::UniqueConnection);
        }
    }
}

void PreviewItem::updateTimer() {
    const bool shown = isVisible() && window() && window()->isVisible();
    if (shown && running_) {
        if (!timer_.isActive()) {
            fresh_ = true;
            timer_.start();
            tick();
        }
    } else {
        timer_.stop();
        if (shown) tick();
    }
}

void PreviewItem::tick() {
    if (!window()) return;
    auto& engine = Engine::shared();
    const double t = engine.now();
    const bool resumed = fresh_ || last_ < 0;
    fresh_ = false;
    const double dt = resumed ? 1.0 / 24 : std::clamp(t - last_, 0.0, 1.0 / 10);
    last_ = t;

    // Cruise → redline → cruise every 8 s; a still frame at the redline when not running.
    const double u = running_ ? std::fmod(t + offset_, kLoop) / kLoop : 0.5;
    const double value = kRedline * (0.3 + 0.85 * (0.5 - 0.5 * std::cos(2 * kPi * u)));
    gauge_.setRedline(kRedline);
    gauge_.update(value, t);
    if (resumed) gauge_.settle();
    gauge_.step(t);
    const Tier tier = gauge_.tier();
    const auto ease = [dt](double& v, double target, double tau) { v += (target - v) * (1 - std::exp(-dt / tau)); };
    if (resumed) {
        redline_ = tier == Tier::Redline ? 1 : 0;
        activity_ = tier != Tier::Idle ? 1 : 0;
    }
    ease(redline_, tier == Tier::Redline ? 1 : 0, 0.2);
    ease(activity_, tier != Tier::Idle ? 1 : 0, 0.18);

    const double s = diameter_, side = 1.5 * s;
    Frame f;
    f.time = t;
    f.dt = dt;
    f.s = s;
    f.center = QPointF(side / 2, side / 2);
    f.unit = s / 148;
    f.dark = dark_;
    f.tokens = Tokens::forDark(dark_);
    f.effects = led::Effects::make(1, true);
    f.heat = gauge_.heat();
    f.displayHeat = f.heat;
    f.overdrive = gauge_.overdrive();
    f.rawHeat = heat::of(gauge_.value(), kRedline);
    f.rawOverdrive = std::clamp((gauge_.value() - kRedline) / kRedline, 0.0, 1.0);
    f.tier = tier;
    f.redline = kRedline;
    f.redlineAmount = redline_;
    f.activity = activity_;
    f.small = s < 120;
    f.resumed = resumed;
    f.reach = 0.66;
    f.unitText = unitText();

    ReadoutContent content;
    content.value = QString::number(qRound(gauge_.value()));
    // At 64 pt the 7 pt title floor would crowd the unit, so a preview shows the value and the unit.
    content.unit = unitText();

    if (!engine.begin()) return;
    const double dpr = window()->effectiveDevicePixelRatio();
    const MeasureText measure = engine.measure(dpr);
    Scene scene;
    style_->step(f);
    if (style_->sharedAura()) shared::addAura(scene, f);
    style_->build(scene, f);
    style_->buildReadout(scene, f, content, measure);
    frame_ = engine.render(scene, kind_, dark_, s, dpr);
    engine.end();
    update();
}

void PreviewItem::paint(QPainter* painter) {
    if (frame_.isNull()) return;
    painter->setRenderHint(QPainter::SmoothPixmapTransform);
    // The disc keeps its diameter in points; the outer aura is cropped by the item's bounds.
    const double side = 1.5 * diameter_;
    const QRectF target((width() - side) / 2, (height() - side) / 2, side, side);
    painter->drawImage(target, frame_);
}

}  // namespace orb
