#pragma once

#include <QElapsedTimer>
#include <QOpenGLWindow>
#include <QPointer>
#include <QTimer>
#include <QVariantMap>
#include <functional>
#include <memory>
#include <vector>

#include "orb/gauge.hpp"
#include "orb/readout.hpp"
#include "orb/renderer.hpp"
#include "orb/shared_layers.hpp"
#include "orb/style.hpp"

class QAccessibleInterface;
class QVariantAnimation;
class Strings;

class TitleThemes;
struct OrbConfig;

namespace orb {

// Another orb's disc on the desktop (global points), for snapping.
struct Disc {
    QPointF center;
    double radius = 0;
};

// One floating orb (DESIGN 2, 2b): a frameless, transparent, always-on-top window 1.5 S square with one of the five
// instruments of DESIGN 2a, drawn with OpenGL only while something moves (DESIGN 2a, Frame policy). Its settings come
// from the orb manager; edits from its own controls go back as requests.
class OrbWindow : public QOpenGLWindow {
    Q_OBJECT
public:
    OrbWindow(const Strings& strings, const QString& id);
    ~OrbWindow() override;
    // DESIGN 1 Title themes: the orb names its levels with its theme (the General one by default).
    void setTitles(const ::TitleThemes* titles);

    QString id() const { return id_; }
    // This orb's settings with what the manager resolves for it: its name and the redline of the current metric.
    void apply(const OrbConfig& config, const QString& name, int redline);
    void setEffectsLevel(int level);
    // The master switch and the orb's own visibility together.
    void setShown(bool shown);
    // Settings highlight (DESIGN 2b Identity at a glance): an accent ring outside the disc.
    void setHighlighted(bool on);
    // The other orbs' discs, for orb-to-orb snapping and alignment.
    void setNeighbors(std::function<std::vector<Disc>()> neighbors) { neighbors_ = std::move(neighbors); }
    void placeAt(QPointF globalCenter);
    // The name plate belongs to the orb (DESIGN 2b Name plate): hovering it hovers the orb, dragging it moves the orb.
    void plateHover(bool hovered);
    void beginDrag(QPointF global);
    void dragTo(QPointF global);
    void endDrag();

    double diameter() const { return diameter_; }
    QPointF discCenter() const { return QPointF(position()) + center(); }
    bool clickThrough() const { return clickThrough_; }
    bool hovered() const { return hovered_; }
    bool dragging() const { return dragging_; }
    double hoverScale() const { return 1 + 0.03 * hover_.value; }
    QString style() const { return styleKey(styleKind_); }
    // Development aid: draw the dark (smoked glass) look whatever the system appearance.
    void forceDark(bool on) { forcedDark_ = on; }
    // Development aid: renders the current frame offscreen.
    QImage snapshot();
    // Accessibility (OrbAccessible): label, hint, and the actions by string key.
    QString accessibleLabel() const { return labelText_; }
    QString accessibleHint() const { return hint_; }
    void setAccessibleHint(const QString& hint) { hint_ = hint; }
    QString actionName(const QString& key) const;
    void performAction(const QString& key);
    QAccessibleInterface* accessibleRoot() const override;

public slots:
    // This orb's values of an engine tick (OrbViews: counted over its sources).
    void applySnapshot(const QVariantMap& snapshot);

signals:
    void dashboardRequested();
    void menuRequested(const QString& id, QPoint global);
    void editRequested(const QString& id, const QString& key, const QVariant& value);
    // A drag ended and the disc settled.
    void moved(const QString& id);
    void hoverChanged(const QString& id, bool hovered);
    void dragChanged(const QString& id, bool dragging);
    void cornerRequested(const QString& id);
    void settingsRequested(const QString& id);

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
    bool openGLAvailable() const;
    bool dark() const;
    led::Effects effects() const;
    Frame makeFrame(double time, double dt);
    double styleMix(double time) const;
    // Steps and draws one style at its crossfade share, with the shared layers it uses and its readout.
    void drawStyle(Style& style, Frame frame, const ReadoutContent& content, const MeasureText& measure);
    std::optional<double> signOfLife(double time) const;
    double nextSignOfLife(double time) const;
    int desiredFps(double time, const Frame& frame) const;
    void schedule();
    void requestFrame();
    void setHovered(bool hovered);
    void applyFlags();
    void applyShape();
    void applyOpacity();
    void setStyle(const QString& style);
    void setDiameter(double diameter);
    QPointF snapped(QPointF center, bool release) const;
    void settle();
    QString titleText(int index) const;
    void addBezel(Scene& scene, const Frame& frame) const;
    void addHighlight(Scene& scene, const Frame& frame) const;
    bool onUnit(QPointF local) const;
    void updateLabel();
    const ::TitleThemes* titles_ = nullptr;
    std::function<std::vector<Disc>()> neighbors_;
    void stepIntel(double time, double dt);
    IntelReadout intelContent(double time) const;
    qint64 countedValue(double time, qint64 value) const;
    bool intelBusy(double time) const;
    double nextIntelWake(double time) const;

    const Strings& strings_;
    QString id_;
    Renderer renderer_;
    Scene scene_;
    StyleKind styleKind_ = StyleKind::Led;
    std::unique_ptr<Style> style_;
    std::unique_ptr<Style> outgoing_;  // the style fading out after a change
    double styleChangedAt_ = -1e9;
    std::vector<shared::Pulse> wavePulses_;
    Gauge gauge_;
    QElapsedTimer clock_;
    QTimer frameTimer_;
    QTimer wakeTimer_;
    bool glReady_ = false;
    bool bodyDirty_ = true;
    bool bodyCrossfade_ = false;  // the next body keeps the previous one for the style crossfade
    bool resumeStyle_ = false;    // the new style's first frame shows the current state
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
    bool atLeast_ = false;  // a partly unpriced money value
    // The odometer: the value on screen, the one it replaced, when, and whether it rose.
    QString shownValue_, previousValue_;
    double shownNumber_ = 0;
    double valueChangedAt_ = -1e9;
    bool valueRising_ = true;
    QString costSymbol_;
    QString info_;
    QString unavailable_;  // "", "disabled" or "notFound"
    QStringList unavailableSources_;
    double lastActive_ = -1e9;  // the last tick with a live value (fade when idle)
    QString labelText_;
    QString hint_;
    double labelAt_ = -1e9;
    std::vector<QColor> agents_;
    QString contentKey_;

    // The intelligence readout (DESIGN 10, 12): the orb's set from the snapshot, and its fades.
    int intelForm_ = 0;  // 0 none, 1 live, 2 resting
    qint64 intelShown_ = -1;
    qint64 intelLast_ = -1;  // kept through the quiet fade
    bool intelProvisional_ = false;
    QString intelWords_;  // the score words (DESIGN 12 Accessibility)
    bool intelGenerating_ = false;
    QString thinkingText_;
    double intelLastActive_ = -1e9;
    double intelOpacity_ = 0;
    double thinkingZeroSince_ = -1;
    double thinkingOpacity_ = 1;
    double glide_ = 0;
    qint64 countFrom_ = -1, countTo_ = -1;
    double countStart_ = -1e9, countDuration_ = 0;
    double numberFadeStart_ = -1e9;
    // The network readout (DESIGN 10): the shown rates, the last time they reached 1 KB/s, and the fade.
    QString netDown_, netUp_;
    bool netAvailable_ = false;
    double netLastActive_ = -1e9;
    double netOpacity_ = 0;

    // Settings.
    double diameter_ = 148;
    int effectsLevel_ = 2;
    bool alwaysOnTop_ = true;
    QString traffic_ = QStringLiteral("sources");
    bool clickThrough_ = false;
    bool blur_ = false;
    bool forcedDark_ = false;
    bool shown_ = false;
    double opacity_ = 1;
    bool fadeWhenIdle_ = false;
    bool idleFaded_ = false;
    QStringList sources_;  // empty: all sources
    QString unit_ = QStringLiteral("tokens");
    QString theme_;
    QString name_;
    bool highlighted_ = false;
    double highlight_ = 0;
    QTimer unitTimer_;  // a single click on the unit waits out the double-click interval

    // Dragging: the disc follows the pointer, pulled toward a touching position near another orb.
    bool dragging_ = false;
    bool moved_ = false;
    bool pressOnUnit_ = false;
    double pressAt_ = 0;
    QPointF pressGlobal_;
    QPoint pressPosition_;
    QPointer<QVariantAnimation> snap_;
};

}  // namespace orb
