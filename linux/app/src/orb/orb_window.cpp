#include "orb/orb_window.hpp"

#include "title_themes.hpp"

#include <QOpenGLContext>

#include <QActionGroup>
#include <QCursor>
#include <QGuiApplication>
#include <QHash>
#include <QMenu>
#include <QMouseEvent>
#include <QOpenGLFramebufferObject>
#include <QPalette>
#include <QRegularExpression>
#include <QScreen>
#include <QSettings>
#include <tuple>
#include <QStyleHints>
#include <QSurfaceFormat>
#include <QVariantAnimation>
#include <algorithm>
#include <cmath>

#include "heat.hpp"
#include "orb/body.hpp"
#include "orb/x11.hpp"
#include "strings.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kSizes[] = {112, 148, 196};
constexpr double kSnapReach = 24, kSnapMargin = 12;
constexpr const char* kTitleKeys[Gauge::kTitles] = {
    "title.walk",       "title.bicycle", "title.ebike",   "title.motorcycle", "title.car",
    "title.sportsCar",  "title.bulletTrain", "title.raceCar", "title.maglev",  "title.jetliner",
    "title.fighterJet", "title.hypersonic",  "title.rocket"};

QColor sourceColor(const QString& id) {
    static const QHash<QString, QRgb> colors = {
        {QStringLiteral("codex"), 0x34D399},     {QStringLiteral("claudeCode"), 0xFF8A65},
        {QStringLiteral("claudeDesktop"), 0xB39DFF}, {QStringLiteral("deepseek"), 0x4D6BFE},
        {QStringLiteral("opencode"), 0xF4B13E},  {QStringLiteral("gemini"), 0x34B8F0},
        {QStringLiteral("omp"), 0x84CC16},       {QStringLiteral("antigravity"), 0xEC4899},
        {QStringLiteral("workbuddy"), 0x11A68B}, {QStringLiteral("workbuddyAi"), 0x007AB4},
        {QStringLiteral("zcode"), 0x466BFF},     {QStringLiteral("kimi"), 0xE16F8A},
        {QStringLiteral("grokBuild"), 0x8290A6}, {QStringLiteral("pi"), 0xA855F7},
        {QStringLiteral("openclaw"), 0xDE2E1F},  {QStringLiteral("hermes"), 0xB8860B},
        {QStringLiteral("droid"), 0x9E5F46}};
    return QColor(colors.value(id, 0x7C8798));
}

QString fill(QString text, const QStringList& args) {
    for (const auto& arg : args) {
        const auto at = text.indexOf(QRegularExpression(QStringLiteral("%[d@]")));
        if (at < 0) break;
        text.replace(at, 2, arg);
    }
    return text;
}

}  // namespace

void OrbWindow::Envelope::step(double target, double dt, double timeConstant) {
    if (timeConstant <= 0) {
        value = target;
        return;
    }
    value += (target - value) * (1 - std::exp(-dt / timeConstant));
    if (std::abs(target - value) < 0.0005) value = target;
}

OrbWindow::OrbWindow(const Strings& strings) : QOpenGLWindow(NoPartialUpdate), strings_(strings) {
    QSurfaceFormat format;
    format.setRenderableType(QSurfaceFormat::OpenGL);
    format.setVersion(3, 3);
    format.setProfile(QSurfaceFormat::CoreProfile);
    format.setAlphaBufferSize(8);
    format.setDepthBufferSize(0);
    format.setStencilBufferSize(0);
    setFormat(format);
    setTitle(strings.t(QStringLiteral("app.name")));
    clock_.start();

    QSettings settings;
    settings.beginGroup(QStringLiteral("orb"));
    diameter_ = std::clamp(settings.value(QStringLiteral("size"), 148.0).toDouble(), 96.0, 240.0);
    effectsLevel_ = std::clamp(settings.value(QStringLiteral("effects"), 2).toInt(), 0, 3);
    alwaysOnTop_ = settings.value(QStringLiteral("alwaysOnTop"), true).toBool();
    clickThrough_ = settings.value(QStringLiteral("clickThrough"), false).toBool();
    // Absent (older builds) → sources; unknown → off (DESIGN 2b).
    traffic_ = settings.value(QStringLiteral("traffic"), QStringLiteral("sources")).toString();
    if (traffic_ != QLatin1String("sources") && traffic_ != QLatin1String("machine") && traffic_ != QLatin1String("intelligence")) {
        traffic_ = QStringLiteral("off");
    }
    settings.endGroup();
    blur_ = x11::blurSupported();
    applyFlags();
    resize(qCeil(side()), qCeil(side()));

    frameTimer_.setTimerType(Qt::PreciseTimer);
    connect(&frameTimer_, &QTimer::timeout, this, [this] { update(); });
    wakeTimer_.setSingleShot(true);
    connect(&wakeTimer_, &QTimer::timeout, this, [this] { requestFrame(); });
    connect(QGuiApplication::styleHints(), &QStyleHints::colorSchemeChanged, this, [this] {
        bodyDirty_ = true;
        requestFrame();
    });
}

OrbWindow::~OrbWindow() {
    if (glReady_) {
        makeCurrent();
        renderer_.release();
        doneCurrent();
    }
}

void OrbWindow::restore() {
    QSettings settings;
    const QPointF saved = settings.value(QStringLiteral("orb/center")).toPointF();
    const bool valid = settings.contains(QStringLiteral("orb/center")) && QGuiApplication::screenAt(saved.toPoint());
    QPointF discCenter = saved;
    if (!valid) {
        const QRect avail = QGuiApplication::primaryScreen()->availableGeometry();
        discCenter = QPointF(avail.right() + 1 - kSnapMargin - diameter_ / 2, avail.top() + avail.height() * 0.32);
    }
    setPosition((discCenter - center()).toPoint());
    if (settings.value(QStringLiteral("orb/visible"), true).toBool()) setOrbVisible(true);
}

bool OrbWindow::dark() const {
    if (forcedDark_) return true;
    const auto scheme = QGuiApplication::styleHints()->colorScheme();
    if (scheme != Qt::ColorScheme::Unknown) return scheme == Qt::ColorScheme::Dark;
    return QGuiApplication::palette().color(QPalette::Window).lightnessF() < 0.5;
}

led::Effects OrbWindow::effects() const { return led::Effects::make(effectsLevel_, diameter_ < 120); }

bool OrbWindow::insideDisc(QPointF local) const {
    const QPointF d = local - center();
    return std::hypot(d.x(), d.y()) <= diameter_ / 2 * (1 + 0.03 * hover_.value);
}

void OrbWindow::applyFlags() {
    Qt::WindowFlags flags = Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint | Qt::WindowDoesNotAcceptFocus;
    if (alwaysOnTop_) flags |= Qt::WindowStaysOnTopHint;
    if (clickThrough_) flags |= Qt::WindowTransparentForInput;
    if (flags == this->flags()) return;
    const bool wasVisible = isVisible();
    setFlags(flags);
    if (wasVisible) {
        show();
        QTimer::singleShot(60, this, [this] { applyShape(); });
    }
}

void OrbWindow::applyShape() {
    if (!clickThrough_) x11::setInputDisc(this, center(), diameter_ / 2);
    if (blur_) x11::setBlurDisc(this, center(), diameter_ / 2 - 0.5);
    x11::skipTaskbar(this);
}

// QOpenGLWindow cannot paint without a context (an offscreen platform, a missing driver) and crashes inside Qt when it
// tries: the orb is only shown where a context can be made.
bool OrbWindow::openGLAvailable() const {
    static const bool available = [this] {
        QOpenGLContext context;
        context.setFormat(format());
        return context.create();
    }();
    return available;
}

void OrbWindow::setOrbVisible(bool visible) {
    if (visible == isVisible()) return;
    if (visible && !openGLAvailable()) {
        qWarning("the floating orb needs OpenGL, which is not available here");
        return;
    }
    if (visible) {
        resumePending_ = true;
        show();
        QTimer::singleShot(60, this, [this] { applyShape(); });
    } else {
        hide();
        frameTimer_.stop();
        wakeTimer_.stop();
    }
    QSettings().setValue(QStringLiteral("orb/visible"), visible);
    emit orbVisibilityChanged(visible);
}

void OrbWindow::setClickThrough(bool on) {
    if (on == clickThrough_) return;
    clickThrough_ = on;
    QSettings().setValue(QStringLiteral("orb/clickThrough"), on);
    applyFlags();
    emit clickThroughChanged(on);
}

void OrbWindow::setDiameter(double diameter) {
    diameter = std::clamp(diameter, 96.0, 240.0);
    if (std::abs(diameter - diameter_) < 0.01) return;
    const QPointF discCenter = QPointF(position()) + center();
    diameter_ = diameter;
    resize(qCeil(side()), qCeil(side()));
    setPosition((discCenter - center()).toPoint());
    bodyDirty_ = true;
    QSettings().setValue(QStringLiteral("orb/size"), diameter);
    applyShape();
    requestFrame();
    emit diameterChanged();
}

void OrbWindow::setEffectsLevel(int level) {
    level = std::clamp(level, 0, 3);
    if (level == effectsLevel_) return;
    effectsLevel_ = level;
    QSettings().setValue(QStringLiteral("orb/effects"), level);
    requestFrame();
    emit effectsLevelChanged();
}

void OrbWindow::setAlwaysOnTop(bool on) {
    if (on == alwaysOnTop_) return;
    alwaysOnTop_ = on;
    QSettings().setValue(QStringLiteral("orb/alwaysOnTop"), on);
    applyFlags();
    emit alwaysOnTopChanged();
}

QString OrbWindow::titleText(int index) const {
    if (titles_) {
        const auto text = titles_->title(index < 0 || index >= Gauge::kTitles ? 0 : index + 1);
        // Latin is upper-case on the orb when the app speaks English (custom text as typed otherwise).
        return strings_.language() == QLatin1String("en") ? text.toUpper() : text;
    }
    if (index < 0 || index >= Gauge::kTitles) return strings_.t(QStringLiteral("tier.idle"));
    return strings_.t(QString::fromLatin1(kTitleKeys[index]));
}

void OrbWindow::setTitles(const TitleThemes* titles) {
    titles_ = titles;
    if (titles_) connect(titles_, &TitleThemes::changed, this, [this] { requestFrame(); });
}

void OrbWindow::setTraffic(const QString& traffic) {
    if (traffic == traffic_) return;
    traffic_ = traffic;
    QSettings().setValue(QStringLiteral("orb/traffic"), traffic);
    emit trafficChanged();
    requestFrame();
}

namespace {

double smoothstep(double x) {
    x = std::clamp(x, 0.0, 1.0);
    return x * x * (3 - 2 * x);
}

double ramp(double value, double target, double dt, double seconds) {
    const double step = dt / seconds;
    return value < target ? std::min(target, value + step) : std::max(target, value - step);
}

}  // namespace

// DESIGN 10 Intelligence readout: visible while 智力 has a value or an agent generates, fading out 10 s after neither;
// 思考 hides 3 s after it reads 0 and the 智力 line glides to the single-line position.
void OrbWindow::stepIntel(double time, double dt) {
    const bool enabled = traffic_ == QLatin1String("intelligence");
    if (enabled && (intelForm_ != 0 || intelGenerating_)) intelLastActive_ = time;
    const double target = enabled && time - intelLastActive_ < 10 ? 1 : 0;
    intelOpacity_ = ramp(intelOpacity_, target, dt, target > intelOpacity_ ? 0.25 : 0.6);
    const bool zero = thinkingText_.isEmpty() || thinkingText_ == QLatin1String("0");
    if (!zero) thinkingZeroSince_ = -1;
    else if (thinkingZeroSince_ < 0) thinkingZeroSince_ = time;
    const double shown = zero && time - thinkingZeroSince_ >= 3 ? 0 : 1;
    thinkingOpacity_ = ramp(thinkingOpacity_, shown, dt, 0.25);
    glide_ = ramp(glide_, 1 - shown, dt, 0.3);
    const double netTarget = netAvailable_ && time - netLastActive_ < 10 ? 1 : 0;
    netOpacity_ = ramp(netOpacity_, netTarget, dt, netTarget > netOpacity_ ? 0.25 : 0.6);
}

IntelReadout OrbWindow::intelContent(double time) const {
    IntelReadout in;
    in.opacity = intelOpacity_;
    in.glyph = strings_.t(QStringLiteral("intel.glyph"));
    in.thinkingGlyph = strings_.t(QStringLiteral("intel.thinking.glyph"));
    in.thinking = thinkingText_.isEmpty() ? QStringLiteral("0") : thinkingText_;
    in.unit = strings_.t(QStringLiteral("unit.tokS"));
    in.thinkingOpacity = thinkingOpacity_;
    in.glide = smoothstep(glide_);
    int value = intelShown_;
    in.form = intelForm_;
    if (value < 0 && intelLast_ >= 0 && intelOpacity_ > 0 && traffic_ == QLatin1String("intelligence") && time - intelLastActive_ >= 10) {
        value = intelLast_;  // the quiet fade keeps the last score
        in.form = 2;
    }
    const double into = time - countStart_;
    if (value >= 0 && countTo_ == value && into >= 0 && into < countDuration_) {
        const double u = into / countDuration_;
        value = countFrom_ + static_cast<int>(std::lround((countTo_ - countFrom_) * (1 - std::pow(1 - u, 3))));
    }
    in.number = value >= 0 ? QString::number(value) : QStringLiteral("–");
    in.proof = intelProof_ && value >= 0;
    const double fade = time - numberFadeStart_;
    in.numberAlpha = fade >= 0 && fade < 0.2 ? 0.25 + 0.75 * fade / 0.2 : 1;
    return in;
}

bool OrbWindow::intelBusy(double time) const {
    const bool enabled = traffic_ == QLatin1String("intelligence");
    const double target = enabled && time - intelLastActive_ < 10 ? 1 : 0;
    const bool zero = thinkingText_.isEmpty() || thinkingText_ == QLatin1String("0");
    const double shown = zero && thinkingZeroSince_ >= 0 && time - thinkingZeroSince_ >= 3 ? 0 : 1;
    const double netTarget = netAvailable_ && time - netLastActive_ < 10 ? 1 : 0;
    return std::abs(netOpacity_ - netTarget) > 0.001 || std::abs(intelOpacity_ - target) > 0.001 || std::abs(thinkingOpacity_ - shown) > 0.001 ||
           std::abs(glide_ - (1 - shown)) > 0.001 ||
           (time - countStart_ >= 0 && time - countStart_ < countDuration_) || (time - numberFadeStart_ >= 0 && time - numberFadeStart_ < 0.2);
}

double OrbWindow::nextIntelWake(double time) const {
    double wake = -1;
    const auto consider = [&](double at) {
        if (at > time && (wake < 0 || at < wake)) wake = at;
    };
    if (netOpacity_ > 0) consider(netLastActive_ + 10);
    if (traffic_ != QLatin1String("intelligence")) return wake;
    if (intelOpacity_ > 0) consider(intelLastActive_ + 10);
    if (thinkingZeroSince_ >= 0 && thinkingOpacity_ > 0) consider(thinkingZeroSince_ + 3);
    return wake;
}

void OrbWindow::applySnapshot(const QVariantMap& snap) {
    const double t = now();
    const int tierBefore = static_cast<int>(gauge_.tier());
    const int titleBefore = gauge_.title();
    gauge_.update(snap.value(QStringLiteral("liveOutput")).toDouble(), t);
    const auto e = effects();
    bool events = false;
    for (const auto& item : snap.value(QStringLiteral("completions")).toList()) {
        const auto c = item.toMap();
        if (!e.pulses) break;
        const double output = c.value(QStringLiteral("output")).toDouble();
        const double speed = c.value(QStringLiteral("speed"), -1).toDouble();
        const double strength = std::clamp(std::log10(std::max(1.0, output)) / 3.4, 0.3, 1.0);
        const double heat = std::max(heat::of(speed >= 0 ? speed : gauge_.value(), gauge_.redline()), 0.08);
        if (t - lastPulse_ < 0.15) continue;
        lastPulse_ = t;
        style_.pulse(t, strength, heat);
        flare_.value = std::max(flare_.value, strength);
        events = true;
    }
    if (snap.value(QStringLiteral("newPeak")).toBool() && e.celebrate) {
        peakStart_ = t;
        style_.celebrate();
        events = true;
    }
    valueText_ = snap.value(QStringLiteral("liveText")).toString();
    const auto agents = snap.value(QStringLiteral("agents")).toList();
    info_ = fill(strings_.t(QStringLiteral("today.summary")),
                 {snap.value(QStringLiteral("todayOutputText")).toString(), QString::number(agents.size())});
    agents_.clear();
    QString key = valueText_;
    for (const auto& a : agents) {
        const auto row = a.toMap();
        if (row.value(QStringLiteral("phase")).toString() != QLatin1String("generating")) continue;
        const QString source = row.value(QStringLiteral("source")).toString();
        agents_.push_back(sourceColor(source));
        key += source;
    }
    key += QString::number(static_cast<int>(gauge_.tier())) + QString::number(gauge_.title());
    // 智力 and 思考 of the orb's agents: the first score set (every enabled source while there is one orb).
    const auto sets = snap.value(QStringLiteral("scoreSets")).toList();
    const auto set = sets.isEmpty() ? QVariantMap() : sets.front().toMap();
    const auto form = set.value(QStringLiteral("form")).toString();
    intelForm_ = form == QLatin1String("live") ? 1 : form == QLatin1String("resting") ? 2 : 0;
    const int shown = set.value(QStringLiteral("shown"), -1).toInt();
    if (shown != intelShown_) {
        if (shown >= 0 && intelShown_ >= 0 && shown - intelShown_ >= 3) {
            countFrom_ = intelShown_;
            countTo_ = shown;
            countStart_ = t;
            countDuration_ = std::min(0.8, 0.3 + 0.012 * (shown - intelShown_));
        } else if (shown >= 0 && intelShown_ >= 0 && shown < intelShown_) {
            numberFadeStart_ = t;
        }
        intelShown_ = shown;
    }
    if (shown >= 0) intelLast_ = shown;
    intelProof_ = set.value(QStringLiteral("proof")).toBool();
    intelGenerating_ = set.value(QStringLiteral("generating")).toBool();
    thinkingText_ = set.value(QStringLiteral("thinkingText")).toString();
    if (traffic_ == QLatin1String("intelligence")) {
        key += QString::number(intelForm_) + QString::number(shown) + (intelProof_ ? QStringLiteral("p") : QString()) + thinkingText_ +
               (intelGenerating_ ? QStringLiteral("g") : QString());
    }
    // The network readout: the orb's sources (every enabled source while there is one orb) or this computer.
    const auto traffic = snap.value(QStringLiteral("traffic")).toMap();
    const bool machine = traffic_ == QLatin1String("machine");
    const bool network = traffic.value(QStringLiteral("enabled")).toBool() && (machine || traffic_ == QLatin1String("sources"));
    netAvailable_ = network && (machine ? traffic.value(QStringLiteral("machine")).toBool() : traffic.value(QStringLiteral("agentsKnown")).toBool());
    if (netAvailable_) {
        const double rx = traffic.value(machine ? QStringLiteral("machineRx") : QStringLiteral("agentsRx")).toDouble();
        const double tx = traffic.value(machine ? QStringLiteral("machineTx") : QStringLiteral("agentsTx")).toDouble();
        netDown_ = traffic.value(machine ? QStringLiteral("machineRxText") : QStringLiteral("agentsRxText")).toString();
        netUp_ = traffic.value(machine ? QStringLiteral("machineTxText") : QStringLiteral("agentsTxText")).toString();
        if (rx + tx >= 1000) netLastActive_ = t;
        key += netDown_ + netUp_;
    }
    const bool changed = key != contentKey_ || static_cast<int>(gauge_.tier()) != tierBefore ||
                         gauge_.title() != titleBefore || !gauge_.isSettled();
    contentKey_ = key;
    if (changed || events) {
        requestFrame();
    } else {
        schedule();
    }
}

std::optional<double> OrbWindow::signOfLife(double time) const {
    if (gauge_.tier() != Tier::Idle || !effects().idleSweep) return std::nullopt;
    const double cycleStart = std::floor(time / 6) * 6;
    if (cycleStart < gauge_.tierChangedAt() + 2) return std::nullopt;
    const double u = (time - cycleStart) / LedStyle::kSignOfLifeDuration;
    if (u < 0 || u > 1) return std::nullopt;
    return u;
}

double OrbWindow::nextSignOfLife(double time) const {
    if (gauge_.tier() != Tier::Idle || !effects().idleSweep) return -1;
    double start = (std::floor(time / 6) + 1) * 6;
    while (start < gauge_.tierChangedAt() + 2) start += 6;
    return start;
}

Frame OrbWindow::makeFrame(double time, double dt) {
    Frame f;
    f.time = time;
    f.dt = dt;
    f.s = diameter_;
    f.center = center();
    f.unit = diameter_ / 148;
    f.dark = dark();
    f.tokens = Tokens::forDark(f.dark);
    f.effects = effects();
    f.heat = gauge_.heat();
    f.overdrive = gauge_.overdrive();
    f.tier = gauge_.tier();
    f.redline = gauge_.redline();
    const double sinceePeak = time - peakStart_;
    const double sweep = sinceePeak >= 0 && sinceePeak < 0.5 && f.effects.celebrate ? led::easeOut(sinceePeak / 0.5) : 1;
    f.displayHeat = f.heat * sweep;
    f.redlineAmount = redline_.value;
    f.activity = activity_.value;
    f.breathPhase = breathPhase_;
    f.breath = f.effects.breathing ? 1 + 0.15 * std::sin(2 * kPi * breathPhase_) : 1;
    const double sinceTier = time - gauge_.tierChangedAt();
    f.flash = f.effects.flash && gauge_.tierRose() && sinceTier >= 0 && sinceTier < 0.4 ? 1 - sinceTier / 0.4 : 0;
    f.auraFlare = flare_.value;
    f.signOfLife = signOfLife(time);
    f.peakAge = sinceePeak >= 0 && sinceePeak < 2.0 ? sinceePeak : -1;
    f.small = diameter_ < 120;
    return f;
}

int OrbWindow::desiredFps(double time, const Frame& f) const {
    if (time - peakStart_ < 2.0 || time - gauge_.tierChangedAt() < 0.5 || time - gauge_.titleChangedAt() < 0.45 ||
        time - hoverChangedAt_ < 0.45) {
        return 60;
    }
    const bool active = f.tier != Tier::Idle;
    const bool envelopes = redline_.settled(f.tier == Tier::Redline ? 1 : 0) && hover_.settled(hovered_ ? 1 : 0) &&
                           activity_.settled(active ? 1 : 0) && valueGlow_.settled(f.tier >= Tier::Boost ? 1 : 0) &&
                           skew_.settled(f.tier >= Tier::Overdrive && f.effects.skew ? 1 : 0);
    const bool motion = !gauge_.isSettled() || !envelopes || flare_.value > 0.02 || style_.isBusy() || intelBusy(time);
    if (effectsLevel_ == 0) return motion ? 30 : 0;
    int fps = style_.ambientFps(f);
    if (motion || f.signOfLife) fps = std::max(fps, 30);
    return fps;
}

void OrbWindow::schedule() {
    if (!isVisible() || !isExposed()) {
        frameTimer_.stop();
        wakeTimer_.stop();
        return;
    }
    const double t = now();
    const int fps = desiredFps(t, makeFrame(t, 0));
    if (fps > 0) {
        // The integer divisor of the refresh rate closest to the class (DESIGN 2a), never above 72. Where OpenGL runs on
        // the CPU every frame costs a core's worth of work, so each class runs at half its rate.
        const int target = renderer_.software() ? std::max(15, fps / 2) : fps;
        double refresh = screen() ? screen()->refreshRate() : 60;
        if (!(refresh > 1)) refresh = 60;
        const double rate = refresh / std::max(1.0, std::round(refresh / target));
        const int interval = std::max(1, static_cast<int>(std::lround(1000.0 / std::min(rate, 72.0))));
        if (!frameTimer_.isActive() || frameTimer_.interval() != interval) frameTimer_.start(interval);
        wakeTimer_.stop();
        return;
    }
    frameTimer_.stop();
    double wake = nextSignOfLife(t);
    if (const double intel = nextIntelWake(t); intel > t && (wake <= t || intel < wake)) wake = intel;
    if (wake > t) wakeTimer_.start(static_cast<int>(std::ceil((wake - t) * 1000)));
}

void OrbWindow::requestFrame() {
    if (isVisible() && isExposed()) update();
}

void OrbWindow::initializeGL() {
    // Without a GL context (an offscreen platform, a broken driver) the orb stays blank instead of crashing.
    glReady_ = context() && context()->isValid() && renderer_.initialize();
    bodyDirty_ = true;
}

void OrbWindow::paintGL() {
    if (glReady_) renderFrame();
}

QImage OrbWindow::snapshot() {
    if (!glReady_) return {};
    makeCurrent();
    const double dpr = devicePixelRatio();
    QOpenGLFramebufferObject fbo(QSize(qCeil(side() * dpr), qCeil(side() * dpr)));
    fbo.bind();
    renderFrame();
    fbo.release();
    QImage image = fbo.toImage();
    doneCurrent();
    return image;
}

void OrbWindow::renderFrame() {
    const double t = now();
    const bool resumed = resumePending_;
    resumePending_ = false;
    const double dt = resumed || lastFrame_ < 0 ? 1.0 / 60 : std::clamp(t - lastFrame_, 0.0, 1.0 / 15);
    lastFrame_ = t;
    const auto e = effects();
    const Tier tier = gauge_.tier();
    const bool active = tier != Tier::Idle;
    if (resumed) {
        gauge_.settle();
        redline_.value = tier == Tier::Redline ? 1 : 0;
        activity_.value = active ? 1 : 0;
        valueGlow_.value = tier >= Tier::Boost ? 1 : 0;
        skew_.value = tier >= Tier::Overdrive && e.skew ? 1 : 0;
        hover_.value = hovered_ ? 1 : 0;
        flare_.value = 0;
    }
    gauge_.step(t);
    redline_.step(tier == Tier::Redline ? 1 : 0, dt, 0.2);
    hover_.step(hovered_ ? 1 : 0, dt, 0.09);
    flare_.step(0, dt, 0.28);
    activity_.step(active ? 1 : 0, dt, 0.18);
    valueGlow_.step(tier >= Tier::Boost ? 1 : 0, dt, 0.22);
    skew_.step(tier >= Tier::Overdrive && e.skew ? 1 : 0, dt, 0.3);
    if (e.breathing) breathPhase_ = std::fmod(breathPhase_ + (0.25 + (1 / 0.7 - 0.25) * gauge_.heat()) * dt, 1.0);
    stepIntel(t, dt);

    Frame f = makeFrame(t, dt);
    f.resumed = resumed;
    style_.step(f);

    const double dpr = devicePixelRatio();
    if (glReady_ && (bodyDirty_ || bodyDark_ != f.dark)) {
        renderer_.setBody(renderBody(diameter_, side(), dpr, f.tokens, blur_), QRectF(0, 0, side(), side()));
        bodyDirty_ = false;
        bodyDark_ = f.dark;
    }
    scene_.reset();
    scene_.hoverScale = 1 + 0.03 * hover_.value;
    style_.build(scene_, f);

    ReadoutContent content;
    content.value = valueText_;
    content.unit = strings_.t(QStringLiteral("unit.tokS"));
    content.title = titleText(gauge_.title());
    content.previousTitle = titleText(gauge_.previousTitle());
    content.titleChangedAt = gauge_.titleChangedAt();
    content.titleHeat = f.heat;
    content.info = info_;
    content.hover = hover_.value;
    content.valueGlow = valueGlow_.value;
    content.skew = skew_.value;
    if (e.jitter && f.redlineAmount > 0.01) {
        if (t - jitterAt_ >= 1.0 / 30) {
            jitterAt_ = t;
            const double amount = led::jitter(f.overdrive) * f.unit * 0.5 * f.redlineAmount;
            jitter_ = QPointF((std::fmod(t * 7919.0, 2.0) - 1) * amount, (std::fmod(t * 104729.0, 2.0) - 1) * amount);
        }
        content.jitter = jitter_;
    }
    content.agents = agents_;
    QString peak = strings_.t(QStringLiteral("peak.new"));
    if (!peak.contains(QLatin1String("PEAK"), Qt::CaseInsensitive)) peak += QStringLiteral(" NEW PEAK");
    content.peakLabel = peak;
    content.intel = intelContent(t);
    const auto split = [](const QString& text) {
        const auto at = text.indexOf(QChar(0x00A0));
        return at < 0 ? std::make_pair(text, QString()) : std::make_pair(text.left(at), text.mid(at + 1));
    };
    content.net.opacity = netOpacity_;
    std::tie(content.net.downNumber, content.net.downUnit) = split(netDown_);
    std::tie(content.net.upNumber, content.net.upUnit) = split(netUp_);
    buildReadout(scene_, f, content, [this, dpr](const TextDraw& text) { return renderer_.measure(text, dpr).width; });

    renderer_.render(scene_, QSizeF(side(), side()), dpr, center());
    schedule();
}

bool OrbWindow::event(QEvent* event) {
    if (event->type() == QEvent::Leave) setHovered(false);
    return QOpenGLWindow::event(event);
}

void OrbWindow::exposeEvent(QExposeEvent* event) {
    QOpenGLWindow::exposeEvent(event);
    if (isExposed()) {
        resumePending_ = true;
        requestFrame();
    } else {
        frameTimer_.stop();
        wakeTimer_.stop();
    }
}

void OrbWindow::setHovered(bool hovered) {
    if (hovered == hovered_) return;
    hovered_ = hovered;
    hoverChangedAt_ = now();
    setCursor(hovered ? Qt::OpenHandCursor : Qt::ArrowCursor);
    requestFrame();
}

void OrbWindow::mousePressEvent(QMouseEvent* event) {
    if (!insideDisc(event->position())) {
        event->ignore();
        return;
    }
    if (event->button() == Qt::RightButton) {
        showMenu(event->globalPosition().toPoint());
        return;
    }
    if (event->button() != Qt::LeftButton) return;
    if (snap_) snap_->stop();
    dragging_ = true;
    moved_ = false;
    pressGlobal_ = event->globalPosition();
    pressPosition_ = position();
    setCursor(Qt::ClosedHandCursor);
}

void OrbWindow::mouseMoveEvent(QMouseEvent* event) {
    if (!dragging_) {
        setHovered(insideDisc(event->position()));
        return;
    }
    const QPointF delta = event->globalPosition() - pressGlobal_;
    if (!moved_ && std::hypot(delta.x(), delta.y()) < 3) return;
    moved_ = true;
    setPosition(pressPosition_ + delta.toPoint());
}

void OrbWindow::mouseReleaseEvent(QMouseEvent* event) {
    if (event->button() != Qt::LeftButton || !dragging_) return;
    dragging_ = false;
    setCursor(hovered_ ? Qt::OpenHandCursor : Qt::ArrowCursor);
    if (moved_) snapToEdges();
}

void OrbWindow::mouseDoubleClickEvent(QMouseEvent* event) {
    if (event->button() == Qt::LeftButton && insideDisc(event->position())) emit dashboardRequested();
}

void OrbWindow::snapToEdges() {
    QPointF c = QPointF(position()) + center();
    QScreen* target = QGuiApplication::screenAt(c.toPoint());
    if (!target) target = screen();
    const QRectF avail = target->availableGeometry();
    const double r = diameter_ / 2;
    if (c.x() - r - avail.left() < kSnapReach) c.setX(avail.left() + kSnapMargin + r);
    if (avail.right() + 1 - (c.x() + r) < kSnapReach) c.setX(avail.right() + 1 - kSnapMargin - r);
    if (c.y() - r - avail.top() < kSnapReach) c.setY(avail.top() + kSnapMargin + r);
    if (avail.bottom() + 1 - (c.y() + r) < kSnapReach) c.setY(avail.bottom() + 1 - kSnapMargin - r);
    const QPoint goal = (c - center()).toPoint();
    if (goal == position()) {
        savePosition();
        return;
    }
    auto* animation = new QVariantAnimation(this);
    animation->setStartValue(position());
    animation->setEndValue(goal);
    animation->setDuration(360);
    animation->setEasingCurve(QEasingCurve::OutBack);
    connect(animation, &QVariantAnimation::valueChanged, this, [this](const QVariant& v) { setPosition(v.toPoint()); });
    connect(animation, &QVariantAnimation::finished, this, [this] { savePosition(); });
    snap_ = animation;
    animation->start(QAbstractAnimation::DeleteWhenStopped);
}

void OrbWindow::savePosition() { QSettings().setValue(QStringLiteral("orb/center"), QPointF(position()) + center()); }

void OrbWindow::showMenu(QPoint global) {
    auto* menu = new QMenu();
    menu->setAttribute(Qt::WA_DeleteOnClose);
    menu->addAction(strings_.t(QStringLiteral("menu.dashboard")), this, &OrbWindow::dashboardRequested);
    menu->addSeparator();

    auto* size = menu->addMenu(strings_.t(QStringLiteral("menu.size")));
    auto* sizes = new QActionGroup(size);
    const char* sizeKeys[] = {"menu.size.small", "menu.size.medium", "menu.size.large"};
    for (int i = 0; i < 3; ++i) {
        auto* action = size->addAction(strings_.t(QString::fromLatin1(sizeKeys[i])));
        action->setCheckable(true);
        action->setChecked(std::abs(diameter_ - kSizes[i]) < 0.5);
        sizes->addAction(action);
        const double d = kSizes[i];
        connect(action, &QAction::triggered, this, [this, d] { setDiameter(d); });
    }
    auto* fx = menu->addMenu(strings_.t(QStringLiteral("menu.effects")));
    auto* levels = new QActionGroup(fx);
    const char* levelKeys[] = {"effects.off", "effects.soft", "effects.standard", "effects.max"};
    for (int i = 0; i < 4; ++i) {
        auto* action = fx->addAction(strings_.t(QString::fromLatin1(levelKeys[i])));
        action->setCheckable(true);
        action->setChecked(effectsLevel_ == i);
        levels->addAction(action);
        connect(action, &QAction::triggered, this, [this, i] { setEffectsLevel(i); });
    }
    auto* traffic = menu->addMenu(strings_.t(QStringLiteral("orbs.traffic")));
    auto* modes = new QActionGroup(traffic);
    for (const char* mode : {"sources", "machine", "intelligence", "off"}) {
        const auto value = QString::fromLatin1(mode);
        auto* action = traffic->addAction(strings_.t(QStringLiteral("orbs.traffic.") + value));
        action->setCheckable(true);
        action->setChecked(traffic_ == value);
        modes->addAction(action);
        connect(action, &QAction::triggered, this, [this, value] { setTraffic(value); });
    }
    auto* top = menu->addAction(strings_.t(QStringLiteral("menu.alwaysOnTop")));
    top->setCheckable(true);
    top->setChecked(alwaysOnTop_);
    connect(top, &QAction::toggled, this, &OrbWindow::setAlwaysOnTop);
    auto* through = menu->addAction(strings_.t(QStringLiteral("menu.clickThrough")));
    through->setCheckable(true);
    through->setChecked(clickThrough_);
    connect(through, &QAction::toggled, this, &OrbWindow::setClickThrough);
    menu->addSeparator();
    menu->addAction(strings_.t(QStringLiteral("menu.orb.hide")), this, [this] { setOrbVisible(false); });
    menu->addAction(strings_.t(QStringLiteral("menu.settings")), this, &OrbWindow::settingsRequested);
    menu->addAction(strings_.t(QStringLiteral("menu.quit")), this, &OrbWindow::quitRequested);
    menu->popup(global);
}

}  // namespace orb
