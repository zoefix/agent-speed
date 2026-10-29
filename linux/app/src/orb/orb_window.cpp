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
#include "source_colors.hpp"
#include "orb/body.hpp"
#include "orb/x11.hpp"
#include "orbs/orbs_store.hpp"
#include "strings.hpp"

namespace orb {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kSizes[] = {112, 148, 196};
constexpr double kSnapReach = 24, kSnapMargin = 12;
constexpr double kStyleFade = 0.25;
constexpr const char* kTitleKeys[Gauge::kTitles] = {
    "title.walk",       "title.bicycle", "title.ebike",   "title.motorcycle", "title.car",
    "title.sportsCar",  "title.bulletTrain", "title.raceCar", "title.maglev",  "title.jetliner",
    "title.fighterJet", "title.hypersonic",  "title.rocket"};

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

OrbWindow::OrbWindow(const Strings& strings, const QString& id) : QOpenGLWindow(NoPartialUpdate), strings_(strings), id_(id) {
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
    style_ = Style::make(styleKind_);
    blur_ = x11::blurSupported();
    applyFlags();
    resize(qCeil(side()), qCeil(side()));

    frameTimer_.setTimerType(Qt::PreciseTimer);
    connect(&frameTimer_, &QTimer::timeout, this, [this] { update(); });
    wakeTimer_.setSingleShot(true);
    connect(&wakeTimer_, &QTimer::timeout, this, [this] { requestFrame(); });
    unitTimer_.setSingleShot(true);
    connect(&unitTimer_, &QTimer::timeout, this, [this] {
        emit editRequested(id_, QStringLiteral("unit"), unit_ == QLatin1String("cost") ? QStringLiteral("tokens") : QStringLiteral("cost"));
    });
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

void OrbWindow::placeAt(QPointF globalCenter) { setPosition((globalCenter - center()).toPoint()); }

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

void OrbWindow::setShown(bool visible) {
    shown_ = visible;
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
}

void OrbWindow::apply(const OrbConfig& config, const QString& name, int redline) {
    name_ = name;
    setStyle(config.style);
    setDiameter(config.size);
    if (config.alwaysOnTop != alwaysOnTop_ || config.clickThrough != clickThrough_) {
        alwaysOnTop_ = config.alwaysOnTop;
        clickThrough_ = config.clickThrough;
        applyFlags();
    }
    opacity_ = config.opacity;
    fadeWhenIdle_ = config.fadeWhenIdle;
    if (!fadeWhenIdle_) idleFaded_ = false;
    const QStringList sources = config.sources.value_or(QStringList());
    const QString theme = config.titleTheme.value_or(QString());
    const bool changed = sources != sources_ || config.unit != unit_ || theme != theme_ || config.traffic != traffic_ ||
                         std::abs(gauge_.redline() - redline) > 0.5;
    sources_ = sources;
    unit_ = config.unit;
    theme_ = theme;
    traffic_ = config.traffic;
    gauge_.setRedline(redline);
    applyOpacity();
    updateLabel();
    if (changed) requestFrame();
}

void OrbWindow::setHighlighted(bool on) {
    if (on == highlighted_) return;
    highlighted_ = on;
    requestFrame();
}

// The orb's opacity, × 55 % while every source is unavailable, × 45 % after 60 s idle when it fades (DESIGN 2, 2b).
void OrbWindow::applyOpacity() {
    double opacity = opacity_ * (unavailable_.isEmpty() ? 1.0 : 0.55);
    if (idleFaded_ && !hovered_) opacity *= 0.45;
    if (std::abs(QWindow::opacity() - opacity) > 0.005) setOpacity(opacity);
}

void OrbWindow::setDiameter(double diameter) {
    diameter = std::clamp(diameter, styleKind_ == StyleKind::Minimal ? 64.0 : 96.0, 240.0);
    if (std::abs(diameter - diameter_) < 0.01) return;
    const QPointF discCenter = QPointF(position()) + center();
    diameter_ = diameter;
    resize(qCeil(side()), qCeil(side()));
    setPosition((discCenter - center()).toPoint());
    bodyDirty_ = true;
    applyShape();
    requestFrame();
}

void OrbWindow::setEffectsLevel(int level) {
    level = std::clamp(level, 0, 3);
    if (level == effectsLevel_) return;
    effectsLevel_ = level;
    requestFrame();
}

QString OrbWindow::titleText(int index) const {
    if (titles_) {
        const auto text = titles_->title(index < 0 || index >= Gauge::kTitles ? 0 : index + 1, theme_);
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

// The outgoing and incoming instruments crossfade for 0.25 s (DESIGN 2a, Style change); a size below 96 pt only
// `minimal` allows grows back.
void OrbWindow::setStyle(const QString& key) {
    const StyleKind kind = styleFromKey(key);
    if (kind == styleKind_) return;
    const double t = now();
    outgoing_ = std::move(style_);
    style_ = Style::make(kind);
    styleKind_ = kind;
    styleChangedAt_ = t;
    bodyDirty_ = true;
    bodyCrossfade_ = true;
    resumeStyle_ = true;
    if (kind != StyleKind::Minimal && diameter_ < 96) setDiameter(96);
    requestFrame();
}

double OrbWindow::styleMix(double time) const {
    if (!outgoing_) return 1;
    return std::clamp((time - styleChangedAt_) / kStyleFade, 0.0, 1.0);
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
    // Every source unavailable (DESIGN 2b): the instrument rests, the value reads – and the title explains.
    const QString unavailable = snap.value(QStringLiteral("unavailable")).toString();
    const bool dimChanged = unavailable != unavailable_;
    unavailable_ = unavailable;
    unavailableSources_.clear();
    for (const auto& item : snap.value(QStringLiteral("sourceStates")).toList()) {
        const auto row = item.toMap();
        if (!row.value(QStringLiteral("status")).toString().isEmpty()) unavailableSources_.push_back(row.value(QStringLiteral("source")).toString());
    }
    gauge_.update(unavailable_.isEmpty() ? snap.value(QStringLiteral("liveOutput")).toDouble() : 0.0, t);
    // Fade when idle: after 60 s without a live value, back at once on activity.
    if (gauge_.tier() != Tier::Idle || lastActive_ < -1e8) lastActive_ = t;
    const bool faded = fadeWhenIdle_ && gauge_.tier() == Tier::Idle && t - lastActive_ >= 60;
    if (faded != idleFaded_ || dimChanged) {
        idleFaded_ = faded;
        applyOpacity();
    }
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
        style_->pulse(t, strength, heat);
        if (wavePulses_.size() >= 4) wavePulses_.erase(wavePulses_.begin());
        wavePulses_.push_back({t, strength, heat});
        flare_.value = std::max(flare_.value, strength);
        events = true;
    }
    if (snap.value(QStringLiteral("newPeak")).toBool() && e.celebrate) {
        peakStart_ = t;
        style_->celebrate(t);
        events = true;
    }
    const auto agents = snap.value(QStringLiteral("agents")).toList();
    atLeast_ = false;
    if (!unavailable_.isEmpty()) {
        valueText_ = QStringLiteral("–");
        info_.clear();
    } else if (unit_ == QLatin1String("cost")) {
        // DESIGN 9 Orb: the live cost rate of the orb's sources; – when nothing is priced, 0 for subscriptions only.
        const bool unknown = snap.value(QStringLiteral("costUnknown")).toBool();
        const bool subscriptions = snap.value(QStringLiteral("costSubscriptionOnly")).toBool();
        valueText_ = unknown ? QStringLiteral("–") : subscriptions ? QStringLiteral("0") : snap.value(QStringLiteral("costNumber")).toString();
        atLeast_ = !unknown && !subscriptions && snap.value(QStringLiteral("costPartial")).toBool() && valueText_ != QLatin1String("0");
        if (subscriptions) {
            info_ = strings_.t(QStringLiteral("cost.subscriptionOnly"));
        } else if (unknown) {
            // Up to two model names of the orb's agents, then +n.
            QStringList models;
            for (const auto& a : agents) {
                const QString model = a.toMap().value(QStringLiteral("model")).toString();
                if (!model.isEmpty() && !models.contains(model)) models.push_back(model);
            }
            QString names = models.mid(0, 2).join(strings_.t(QStringLiteral("list.separator")));
            if (models.size() > 2) names += QStringLiteral(" +%1").arg(models.size() - 2);
            info_ = fill(strings_.t(QStringLiteral("cost.unknownModels")), {names});
        } else if (atLeast_) {
            info_ = strings_.t(QStringLiteral("cost.partial"));
        } else {
            info_ = fill(strings_.t(QStringLiteral("cost.today.summary")),
                         {snap.value(QStringLiteral("costTodayText")).toString(), QString::number(agents.size())});
        }
        costSymbol_ = snap.value(QStringLiteral("costSymbol")).toString();
    } else {
        valueText_ = snap.value(QStringLiteral("liveText")).toString();
        info_ = fill(strings_.t(QStringLiteral("today.summary")),
                     {snap.value(QStringLiteral("todayOutputText")).toString(), QString::number(agents.size())});
    }
    if (valueText_ != shownValue_) {
        previousValue_ = shownValue_;
        valueChangedAt_ = t;
        valueRising_ = gauge_.value() >= shownNumber_;
        shownValue_ = valueText_;
        shownNumber_ = gauge_.value();
    }
    if (t - labelAt_ >= 1) updateLabel();
    agents_.clear();
    QString key = valueText_;
    for (const auto& a : agents) {
        const auto row = a.toMap();
        if (row.value(QStringLiteral("phase")).toString() != QLatin1String("generating")) continue;
        const QString source = row.value(QStringLiteral("source")).toString();
        agents_.push_back(sourceColor(source));
        key += source;
    }
    key += QString::number(static_cast<int>(gauge_.tier())) + QString::number(gauge_.title()) + unavailable_ + unavailableSources_.join(QLatin1Char(','));
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
    const double duration = style_->signOfLifeDuration();
    if (gauge_.tier() != Tier::Idle || !effects().idleSweep || duration <= 0 || !unavailable_.isEmpty()) return std::nullopt;
    const double cycleStart = std::floor(time / 6) * 6;
    if (cycleStart < gauge_.tierChangedAt() + 2) return std::nullopt;
    const double u = (time - cycleStart) / duration;
    if (u < 0 || u > 1) return std::nullopt;
    return u;
}

double OrbWindow::nextSignOfLife(double time) const {
    if (gauge_.tier() != Tier::Idle || !effects().idleSweep || style_->signOfLifeDuration() <= 0 || !unavailable_.isEmpty()) return -1;
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
    f.rawHeat = heat::of(gauge_.value(), gauge_.redline());
    f.rawOverdrive = std::clamp((gauge_.value() - gauge_.redline()) / gauge_.redline(), 0.0, 1.0);
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
    const int transition = styleKind_ == StyleKind::Minimal ? 30 : 60;
    if (time - peakStart_ < 2.0 || time - gauge_.tierChangedAt() < 0.5 || time - gauge_.titleChangedAt() < 0.45 || time - valueChangedAt_ < 0.25 ||
        time - hoverChangedAt_ < 0.45 || styleMix(time) < 1) {
        return transition;
    }
    const bool active = f.tier != Tier::Idle;
    const bool envelopes = redline_.settled(f.tier == Tier::Redline ? 1 : 0) && hover_.settled(hovered_ ? 1 : 0) &&
                           activity_.settled(active ? 1 : 0) && valueGlow_.settled(f.tier >= Tier::Boost ? 1 : 0) &&
                           skew_.settled(f.tier >= Tier::Overdrive && f.effects.skew ? 1 : 0);
    const bool waves = !wavePulses_.empty() && time - wavePulses_.back().start < shared::kPulseDuration && style_->sharedWave();
    const bool fading = std::abs(highlight_ - (highlighted_ ? 1.0 : 0.0)) > 0.01;
    const bool motion = !gauge_.isSettled() || !envelopes || flare_.value > 0.02 || style_->isBusy(f) || intelBusy(time) || waves || fading;
    if (effectsLevel_ == 0) return motion ? 30 : 0;
    int fps = style_->ambientFps(f);
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
    while (!wavePulses_.empty() && t - wavePulses_.front().start >= shared::kPulseDuration) wavePulses_.erase(wavePulses_.begin());
    if (resumed) wavePulses_.clear();

    const double dpr = devicePixelRatio();
    const double mix = styleMix(t);
    if (glReady_ && (bodyDirty_ || bodyDark_ != f.dark)) {
        renderer_.setBody(renderBody(diameter_, side(), dpr, f.tokens, blur_, styleKind_), QRectF(0, 0, side(), side()),
                          bodyCrossfade_ && mix < 1);
        bodyDirty_ = false;
        bodyCrossfade_ = false;
        bodyDark_ = f.dark;
    }

    ReadoutContent content;
    content.value = valueText_;
    content.atLeast = atLeast_;
    content.previousValue = previousValue_;
    content.valueChangedAt = valueChangedAt_;
    content.rising = valueRising_;
    content.rolls = e.level > 0 && diameter_ >= 80;
    content.unit = unit_ == QLatin1String("cost") ? fill(strings_.t(QStringLiteral("cost.unitPerSecond")), {costSymbol_}) : strings_.t(QStringLiteral("unit.tokS"));
    content.title = unavailable_.isEmpty() ? titleText(gauge_.title()) : strings_.t(QStringLiteral("source.") + unavailable_);
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
    f.unitText = content.unit;
    const MeasureText measure = [this, dpr](const TextDraw& text) { return renderer_.measure(text, dpr).width; };

    scene_.reset();
    scene_.hoverScale = 1 + 0.03 * hover_.value;
    scene_.bodyMix = mix;
    if (outgoing_ && mix < 1) {
        Frame out = f;
        out.alpha = 1 - mix;
        drawStyle(*outgoing_, out, content, measure);
    }
    if (mix >= 1) outgoing_.reset();
    Frame in = f;
    in.alpha = mix;
    in.resumed = resumed || resumeStyle_;
    resumeStyle_ = false;
    drawStyle(*style_, in, content, measure);
    scene_.alphaScale = 1;
    addBezel(scene_, f);
    highlight_ = highlighted_ ? std::min(1.0, highlight_ + dt / 0.15) : std::max(0.0, highlight_ - dt / 0.15);
    addHighlight(scene_, f);

    renderer_.render(scene_, QSizeF(side(), side()), dpr, center());
    schedule();
}

void OrbWindow::drawStyle(Style& style, Frame f, const ReadoutContent& content, const MeasureText& measure) {
    const auto& placement = style.linePlacement();
    if (f.s >= 64) {
        f.linesFit = linesFit(f, placement, content, measure);
        f.readoutShift = readoutShift(f, style.readoutLayout(), placement, content, measure);
    }
    style.step(f);
    scene_.alphaScale = f.alpha;
    if (f.alpha <= 0.002) return;
    if (f.s >= 64) {
        if (style.sharedAura()) shared::addAura(scene_, f);
        if (style.sharedWave() && f.effects.pulses) shared::addPulses(scene_, f, wavePulses_);
        if (style.sharedRays()) shared::addPeakRays(scene_, f);
    }
    style.build(scene_, f);
    style.buildReadout(scene_, f, content, measure);
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
    applyOpacity();
    emit hoverChanged(id_, hovered);
    requestFrame();
}

// The unit line (the value in `minimal`), grown to at least 44 × 20 pt: a single click toggles tok/s and money.
bool OrbWindow::onUnit(QPointF local) const {
    const auto& layout = style_->readoutLayout();
    const double s = diameter_;
    const QPointF c = center();
    QRectF area;
    if (layout.unitSize > 0) {
        const double size = std::max(7.0, layout.unitSize * s);
        const double baseline = c.y() + (layout.valueBaseline + layout.unitGap) * s;
        area = QRectF(c.x() - std::max(22.0, 0.14 * s), baseline - 0.75 * size - std::max(0.0, (20 - size) / 2), 2 * std::max(22.0, 0.14 * s),
                      std::max(20.0, size));
    } else {
        const double size = layout.valueSize * s;
        area = QRectF(c.x() - 0.25 * s, c.y() + layout.valueBaseline * s - 0.75 * size, 0.5 * s, size);
    }
    return area.contains(local) && insideDisc(local);
}

void OrbWindow::plateHover(bool hovered) { setHovered(hovered); }

void OrbWindow::beginDrag(QPointF global) {
    if (snap_) snap_->stop();
    dragging_ = true;
    moved_ = false;
    pressOnUnit_ = false;
    pressGlobal_ = global;
    pressPosition_ = position();
    pressAt_ = now();
}

void OrbWindow::dragTo(QPointF global) {
    if (!dragging_) return;
    const QPointF delta = global - pressGlobal_;
    if (!moved_ && std::hypot(delta.x(), delta.y()) < 4) return;
    if (!moved_) {
        moved_ = true;
        emit dragChanged(id_, true);
    }
    const QPointF raw = QPointF(pressPosition_ + delta.toPoint()) + center();
    setPosition((snapped(raw, false) - center()).toPoint());
}

void OrbWindow::endDrag() {
    if (!dragging_) return;
    dragging_ = false;
    if (moved_) {
        emit dragChanged(id_, false);
        settle();
    }
}

void OrbWindow::mousePressEvent(QMouseEvent* event) {
    if (!insideDisc(event->position())) {
        event->ignore();
        return;
    }
    if (event->button() == Qt::RightButton) {
        emit menuRequested(id_, event->globalPosition().toPoint());
        return;
    }
    if (event->button() != Qt::LeftButton) return;
    if (snap_) snap_->stop();
    dragging_ = true;
    moved_ = false;
    pressOnUnit_ = onUnit(event->position());
    pressGlobal_ = event->globalPosition();
    pressPosition_ = position();
    pressAt_ = now();
    setCursor(Qt::ClosedHandCursor);
}

void OrbWindow::mouseMoveEvent(QMouseEvent* event) {
    if (!dragging_) {
        const bool inside = insideDisc(event->position());
        setHovered(inside);
        if (inside) setCursor(onUnit(event->position()) ? Qt::PointingHandCursor : Qt::OpenHandCursor);
        return;
    }
    const QPointF delta = event->globalPosition() - pressGlobal_;
    if (!moved_ && std::hypot(delta.x(), delta.y()) < 4) return;
    if (!moved_) {
        moved_ = true;
        emit dragChanged(id_, true);
    }
    // Within 16 pt of a touching position beside another orb the disc is pulled in (DESIGN 2b Orb to orb).
    const QPointF raw = QPointF(pressPosition_ + delta.toPoint()) + center();
    setPosition((snapped(raw, false) - center()).toPoint());
}

void OrbWindow::mouseReleaseEvent(QMouseEvent* event) {
    if (event->button() != Qt::LeftButton || !dragging_) return;
    dragging_ = false;
    setCursor(hovered_ ? Qt::OpenHandCursor : Qt::ArrowCursor);
    if (moved_) {
        emit dragChanged(id_, false);
        settle();
    } else if (pressOnUnit_ && now() - pressAt_ < 0.5) {
        unitTimer_.start(QGuiApplication::styleHints()->mouseDoubleClickInterval());
    }
}

void OrbWindow::mouseDoubleClickEvent(QMouseEvent* event) {
    unitTimer_.stop();
    if (event->button() == Qt::LeftButton && insideDisc(event->position())) emit dashboardRequested();
}

// DESIGN 2b Creating, placing and snapping: screen edges first (released within 24 pt, a 12 pt margin), then touching
// another orb at a 12 pt gap (pulled in within 16 pt, pushed out of an overlap on release), then alignment of centers
// (within 8 pt, discs within 64 pt), which never pulls a disc off an edge it is docked to.
QPointF OrbWindow::snapped(QPointF c, bool release) const {
    const double r = diameter_ / 2;
    QScreen* target = QGuiApplication::screenAt(c.toPoint());
    if (!target) target = screen();
    const QRectF avail = target->availableGeometry();
    bool dockX = false, dockY = false;
    if (release) {
        if (c.x() - r - avail.left() < kSnapReach) c.setX(avail.left() + kSnapMargin + r), dockX = true;
        if (avail.right() + 1 - (c.x() + r) < kSnapReach) c.setX(avail.right() + 1 - kSnapMargin - r), dockX = true;
        if (c.y() - r - avail.top() < kSnapReach) c.setY(avail.top() + kSnapMargin + r), dockY = true;
        if (avail.bottom() + 1 - (c.y() + r) < kSnapReach) c.setY(avail.bottom() + 1 - kSnapMargin - r), dockY = true;
    }
    const auto others = neighbors_ ? neighbors_() : std::vector<Disc>{};
    for (const auto& other : others) {
        QPointF v = c - other.center;
        double distance = std::hypot(v.x(), v.y());
        const double want = r + other.radius + kSnapMargin;
        if (std::abs(distance - want) >= 16 && !(release && distance < want)) continue;
        if (distance < 0.01) v = QPointF(1, 0), distance = 1;
        QPointF touch = other.center + v / distance * want;
        // Docked to an edge: keep that coordinate and touch along the other axis.
        if (dockX || dockY) {
            const double dx = dockX ? c.x() - other.center.x() : 0, dy = dockY ? c.y() - other.center.y() : 0;
            if (dockX && std::abs(dx) < want) touch = QPointF(c.x(), other.center.y() + std::copysign(std::sqrt(want * want - dx * dx), v.y() == 0 ? 1.0 : v.y()));
            else if (dockY && std::abs(dy) < want) touch = QPointF(other.center.x() + std::copysign(std::sqrt(want * want - dy * dy), v.x() == 0 ? 1.0 : v.x()), c.y());
            else continue;
        }
        c = touch;
        break;
    }
    for (const auto& other : others) {
        const double gap = std::hypot(c.x() - other.center.x(), c.y() - other.center.y()) - r - other.radius;
        if (gap > 64) continue;
        if (!dockX && std::abs(c.x() - other.center.x()) < 8) c.setX(other.center.x());
        if (!dockY && std::abs(c.y() - other.center.y()) < 8) c.setY(other.center.y());
    }
    if (release) {
        // Never off-screen.
        c.setX(std::clamp(c.x(), avail.left() + r, std::max(avail.left() + r, avail.right() + 1 - r)));
        c.setY(std::clamp(c.y(), avail.top() + r, std::max(avail.top() + r, avail.bottom() + 1 - r)));
    }
    return c;
}

// Release springs into place (≈ 0.35 s), then the manager records the position.
void OrbWindow::settle() {
    const QPointF goal = snapped(discCenter(), true);
    const QPoint target = (goal - center()).toPoint();
    if (target == position()) {
        emit moved(id_);
        return;
    }
    auto* animation = new QVariantAnimation(this);
    animation->setStartValue(position());
    animation->setEndValue(target);
    animation->setDuration(360);
    animation->setEasingCurve(QEasingCurve::OutBack);
    connect(animation, &QVariantAnimation::valueChanged, this, [this](const QVariant& v) { setPosition(v.toPoint()); });
    connect(animation, &QVariantAnimation::finished, this, [this] { emit moved(id_); });
    snap_ = animation;
    animation->start(QAbstractAnimation::DeleteWhenStopped);
}

// DESIGN 2b Source bezel: a subset orb's rim in equal arcs of its source colors from 12 o'clock with 4° gaps, dashed
// for an unavailable source.
void OrbWindow::addBezel(Scene& scene, const Frame& f) const {
    if (sources_.isEmpty()) return;
    const double width = std::max(1.0, 0.0105 * f.s);
    const double radius = f.s / 2 - width / 2;
    const int count = static_cast<int>(sources_.size());
    const double gap = count > 1 ? 4.0 : 0.0;
    const double span = 360.0 / count;
    const Rgba unlit = f.tokens.unlitSegment.opacity(2.5);
    for (int i = 0; i < count; ++i) {
        const double start = -90 + span * i + gap / 2;
        const double sweep = span - gap;
        if (!unavailableSources_.contains(sources_[i])) {
            const Rgba tint = Rgba::from(sourceColor(sources_[i]));
            const Rgba color = f.dark ? tint.withAlpha(0.85) : tint.darkened(0.1).withAlpha(0.9);
            scene.add(Instance::bandArc(f.center, radius, width, start, sweep, color, color), Blend::Over);
            continue;
        }
        const double degreesPerPoint = 180 / (kPi * radius);
        const double on = width * 1.2 * degreesPerPoint, period = width * 2.6 * degreesPerPoint;
        for (double a = start; a < start + sweep; a += period) {
            scene.add(Instance::bandArc(f.center, radius, width, a, std::min(on, start + sweep - a), unlit, unlit), Blend::Over);
        }
    }
}

// The Settings highlight: a 2 pt accent ring 4 pt outside the disc at 60 %, fading in over 0.15 s.
void OrbWindow::addHighlight(Scene& scene, const Frame& f) const {
    if (highlight_ <= 0.01) return;
    const Rgba accent = f.dark ? Rgba::hex(0x5B8CFF) : Rgba::hex(0x2F6BFF);
    scene.add(Instance::ring(f.center, f.s / 2 + 5, 2, accent.withAlpha(0.6 * highlight_)), Blend::Over);
}

// DESIGN 2b Accessibility: the orb's label, at most once a second, as its window title.
void OrbWindow::updateLabel() {
    QString label;
    const QString title = titleText(gauge_.title());
    if (!unavailable_.isEmpty()) {
        label = fill(strings_.t(QStringLiteral("a11y.orb.unavailable")), {name_});
    } else if (name_.isEmpty()) {
        label = fill(strings_.t(QStringLiteral("a11y.orb")), {valueText_, title});
    } else {
        label = fill(strings_.t(QStringLiteral("a11y.orb.named")), {name_, valueText_, title});
    }
    if (label == labelText_) return;
    labelText_ = label;
    labelAt_ = now();
    setTitle(label);
}

}  // namespace orb
