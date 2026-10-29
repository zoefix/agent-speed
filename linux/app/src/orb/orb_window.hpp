#pragma once

#include <QElapsedTimer>
#include <QOpenGLWindow>
#include <QPointer>
#include <QTimer>
#include <QVariantMap>
#include <vector>

#include "orb/gauge.hpp"
#include "orb/led_style.hpp"
#include "orb/readout.hpp"
#include "orb/renderer.hpp"

class QVariantAnimation;
class Strings;

namespace orb {

// The floating orb (DESIGN 2): a frameless, transparent, always-on-top window 1.5 S square with the `led` instrument,
// drawn with OpenGL only while something moves (DESIGN 2a, Frame policy).
class OrbWindow : public QOpenGLWindow {
    Q_OBJECT
    Q_PROPERTY(bool orbVisible READ isVisible WRITE setOrbVisible NOTIFY orbVisibilityChanged)
    Q_PROPERTY(bool clickThrough READ clickThrough WRITE setClickThrough NOTIFY clickThroughChanged)
    Q_PROPERTY(double diameter READ diameter WRITE setDiameter NOTIFY diameterChanged)
    Q_PROPERTY(int effectsLevel READ effectsLevel WRITE setEffectsLevel NOTIFY effectsLevelChanged)
    Q_PROPERTY(bool alwaysOnTop READ alwaysOnTop WRITE setAlwaysOnTop NOTIFY alwaysOnTopChanged)
    // DESIGN 2b `traffic`: what the two-line readout shows — sources, machine, intelligence or off.
    Q_PROPERTY(QString traffic READ traffic WRITE setTraffic NOTIFY trafficChanged)
public:
    explicit OrbWindow(const Strings& strings);
    ~OrbWindow() override;

    bool clickThrough() const { return clickThrough_; }
    double diameter() const { return diameter_; }
    int effectsLevel() const { return effectsLevel_; }
    bool alwaysOnTop() const { return alwaysOnTop_; }
    QString traffic() const { return traffic_; }
    // Development aid: draw the dark (smoked glass) look whatever the system appearance.
    void forceDark(bool on) { forcedDark_ = on; }
    void restore();
    // Development aid: renders the current frame offscreen.
    QImage snapshot();

public slots:
    void applySnapshot(const QVariantMap& snapshot);
    void setOrbVisible(bool visible);
    void setClickThrough(bool on);
    void setDiameter(double diameter);
    void setEffectsLevel(int level);
    void setAlwaysOnTop(bool on);
    void setTraffic(const QString& traffic);

signals:
    void trafficChanged();
    void dashboardRequested();
    void orbVisibilityChanged(bool visible);
    void clickThroughChanged(bool on);
    void diameterChanged();
    void effectsLevelChanged();
    void alwaysOnTopChanged();
    void quitRequested();
    void settingsRequested();

protected:
    void initializeGL() override;
    void paintGL() override;
    void renderFrame();
    bool event(QEvent* event) override;
    void exposeEvent(QExposeEvent* event) override;
    void mousePressEvent(QMouseEvent* event) override;
    void mouseMoveEvent(QMouseEvent* event) override;
    void mouseReleaseEvent(QMouseEvent* event) override;
    void mouseDoubleClickEvent(QMouseEvent* event) override;

private:
    struct Envelope {
        double value = 0;
        void step(double target, double dt, double timeConstant);
        bool settled(double target) const { return std::abs(value - target) < 0.001; }
    };

    double now() const { return static_cast<double>(clock_.nsecsElapsed()) / 1e9; }
    double side() const { return 1.5 * diameter_; }
    QPointF center() const { return {side() / 2, side() / 2}; }
    bool insideDisc(QPointF local) const;
    bool dark() const;
    led::Effects effects() const;
    Frame makeFrame(double time, double dt);
    std::optional<double> signOfLife(double time) const;
    double nextSignOfLife(double time) const;
    int desiredFps(double time, const Frame& frame) const;
    void schedule();
    void requestFrame();
    void setHovered(bool hovered);
    void applyFlags();
    void applyShape();
    void snapToEdges();
    void savePosition();
    void showMenu(QPoint global);
    QString titleText(int index) const;
    void stepIntel(double time, double dt);
    IntelReadout intelContent(double time) const;
    bool intelBusy(double time) const;
    double nextIntelWake(double time) const;

    const Strings& strings_;
    Renderer renderer_;
    Scene scene_;
    LedStyle style_;
    Gauge gauge_;
    QElapsedTimer clock_;
    QTimer frameTimer_;
    QTimer wakeTimer_;
    bool glReady_ = false;
    bool bodyDirty_ = true;
    bool bodyDark_ = true;
    bool resumePending_ = true;
    double lastFrame_ = -1;
    double peakStart_ = -1e9;
    double hoverChangedAt_ = -1e9;
    double breathPhase_ = 0;
    bool hovered_ = false;
    Envelope redline_, hover_, activity_, valueGlow_, skew_, flare_;
    double lastPulse_ = -1e9;
    QPointF jitter_;
    double jitterAt_ = 0;

    // Content from the latest snapshot.
    QString valueText_ = QStringLiteral("0");
    QString info_;
    std::vector<QColor> agents_;
    QString contentKey_;

    // The intelligence readout (DESIGN 10, 12): the orb's set from the snapshot, and its fades.
    int intelForm_ = 0;  // 0 none, 1 live, 2 resting
    int intelShown_ = -1;
    int intelLast_ = -1;  // kept through the quiet fade
    bool intelProof_ = false;
    bool intelGenerating_ = false;
    QString thinkingText_;
    double intelLastActive_ = -1e9;
    double intelOpacity_ = 0;
    double thinkingZeroSince_ = -1;
    double thinkingOpacity_ = 1;
    double glide_ = 0;
    int countFrom_ = -1, countTo_ = -1;
    double countStart_ = -1e9, countDuration_ = 0;
    double numberFadeStart_ = -1e9;

    // Settings.
    double diameter_ = 148;
    int effectsLevel_ = 2;
    bool alwaysOnTop_ = true;
    QString traffic_ = QStringLiteral("sources");
    bool clickThrough_ = false;
    bool blur_ = false;
    bool forcedDark_ = false;

    // Dragging.
    bool dragging_ = false;
    bool moved_ = false;
    QPointF pressGlobal_;
    QPoint pressPosition_;
    QPointer<QVariantAnimation> snap_;
};

}  // namespace orb
