#include "orb/character/character_session.hpp"

#include <QCoreApplication>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QOffscreenSurface>
#include <QOpenGLContext>
#include <QOpenGLFramebufferObject>
#include <QSurfaceFormat>
#include <QThread>
#include <algorithm>
#include <cmath>
#include <memory>

#include "agentspeed/character/error.hpp"
#include "heat.hpp"
#include "orb/body.hpp"
#include "orb/character/character_assets.hpp"
#include "orb/character/character_style.hpp"
#include "orb/gauge.hpp"
#include "orb/renderer.hpp"

namespace orb {

namespace ac = agentspeed::character;

namespace {

std::optional<double> number(const QJsonValue& v) { return v.isDouble() ? std::optional<double>(v.toDouble()) : std::nullopt; }

std::optional<ac::AgentActivity> phaseOf(const QJsonValue& v) {
    const auto s = v.toString();
    if (s == QLatin1String("generating")) return ac::AgentActivity::Generating;
    if (s == QLatin1String("tools")) return ac::AgentActivity::Tools;
    if (s == QLatin1String("idle")) return ac::AgentActivity::Idle;
    return std::nullopt;
}

// The rows of `key` with at least `min` items.
std::vector<QJsonArray> rows(const QJsonObject& root, const char* key, int min) {
    std::vector<QJsonArray> out;
    for (const auto& row : root.value(QLatin1String(key)).toArray()) {
        if (row.isArray() && row.toArray().size() >= min) out.push_back(row.toArray());
    }
    return out;
}

std::vector<double> numbers(const QJsonObject& root, const char* key) {
    std::vector<double> out;
    for (const auto& item : root.value(QLatin1String(key)).toArray()) {
        if (const auto v = number(item)) out.push_back(*v);
    }
    return out;
}

}  // namespace

std::optional<CharacterSessionScript> CharacterSessionScript::parse(const QByteArray& json) {
    const auto doc = QJsonDocument::fromJson(json);
    if (!doc.isObject()) return std::nullopt;
    const auto root = doc.object();
    CharacterSessionScript s;
    if (const auto h = root.value(QStringLiteral("hour")); h.isDouble()) s.hour = h.toInt();
    s.seconds = number(root.value(QStringLiteral("seconds")));
    for (const auto& r : rows(root, "phases", 2)) {
        const auto start = number(r[0]);
        const auto phase = phaseOf(r[1]);
        if (start && phase) s.phases_.push_back({*start, *phase, r.size() > 2 ? number(r[2]).value_or(0) : 0});
    }
    for (const auto& [key, list] : {std::pair{"requests", &s.requests_}, std::pair{"idleJumps", &s.idleJumps_}, std::pair{"speed", &s.speed_}}) {
        for (const auto& r : rows(root, key, 2)) {
            if (const auto a = number(r[0]), b = number(r[1]); a && b) list->emplace_back(*a, *b);
        }
    }
    for (const auto& r : rows(root, "events", 2)) {
        if (const auto at = number(r[0]); at && r[1].isString()) s.events_.emplace_back(*at, r[1].toString().toStdString());
    }
    for (const auto& r : rows(root, "cursor", 3)) {
        if (const auto t = number(r[0]), x = number(r[1]), y = number(r[2]); t && x && y) s.cursor_.push_back({*t, *x, *y});
    }
    s.clicks_ = numbers(root, "clicks");
    s.shots = numbers(root, "shots");
    std::sort(s.shots.begin(), s.shots.end());
    return s;
}

std::optional<CharacterSessionScript> CharacterSessionScript::load(const QString& path) {
    QFile file(path);
    if (path.isEmpty() || !file.open(QIODevice::ReadOnly)) return std::nullopt;
    return parse(file.readAll());
}

CharacterSessionScript::Phase CharacterSessionScript::phaseAt(double t) const {
    Phase out{ac::AgentActivity::Idle, 0, 0};
    for (const auto& p : phases_) {
        if (t >= p.start) out = {p.phase, p.idleAt, p.start};
    }
    return out;
}

std::optional<double> CharacterSessionScript::waitAt(double t) const {
    for (const auto& [start, first] : requests_) {
        if (start <= t && t < first) return t - start;
    }
    return std::nullopt;
}

double CharacterSessionScript::idleAt(double t) const {
    const auto p = phaseAt(t);
    if (p.phase != ac::AgentActivity::Idle) return 0;
    double jumps = 0;
    for (const auto& [at, by] : idleJumps_) {
        if (t >= at) jumps += by;
    }
    return p.idleAt + t - p.since + jumps;
}

std::optional<std::pair<double, double>> CharacterSessionScript::cursorAt(double t) const {
    for (std::size_t i = 0; i + 1 < cursor_.size(); ++i) {
        const auto& a = cursor_[i];
        const auto& b = cursor_[i + 1];
        if (a.t <= t && t <= b.t) {
            const double k = (t - a.t) / std::max(b.t - a.t, 1e-9);
            return std::pair{a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k};
        }
    }
    return std::nullopt;
}

std::optional<double> CharacterSessionScript::speedAt(double t) const {
    if (speed_.empty()) return std::nullopt;
    if (t <= speed_.front().first) return speed_.front().second;
    for (std::size_t i = 0; i + 1 < speed_.size(); ++i) {
        const auto& [ta, va] = speed_[i];
        const auto& [tb, vb] = speed_[i + 1];
        if (t <= tb) return va + (vb - va) * (t - ta) / std::max(tb - ta, 1e-9);
    }
    return speed_.back().second;
}

bool CharacterSessionScript::apply(double t, double dt, double now, CharacterLive& live) const {
    const auto p = phaseAt(t);
    live.scripted = true;
    live.phase = p.phase;
    live.waitingSince.reset();
    if (p.phase == ac::AgentActivity::Generating) {
        if (const auto wait = waitAt(t)) live.waitingSince = now - *wait;
    }
    live.idleSince = now - idleAt(t);
    live.hour = hour;
    live.scriptedCursor = cursorAt(t);
    bool peak = false;
    for (const auto& [at, name] : events_) {
        if (!(t - dt < at && at <= t)) continue;
        if (name == "newPeak") {
            live.notePeak();
            peak = true;
        } else if (name == "trouble") {
            live.noteTrouble();
        }
    }
    for (double c : clicks_) {
        if (t - dt < c && c <= t) {
            ++live.clicks;
            break;
        }
    }
    return peak;
}

int runCharacterSession(const QString& scriptPath, const std::filesystem::path& packPath, const QString& folder) {
    const auto script = CharacterSessionScript::load(scriptPath);
    if (!script) {
        qWarning("session: cannot read %s", qPrintable(scriptPath));
        return 1;
    }
    std::shared_ptr<const ac::Pack> pack;
    try {
        pack = ac::Pack::load(packPath);
    } catch (const ac::CharacterError& e) {
        qWarning("session: cannot load the pack %s (%s)", packPath.string().c_str(), e.what());
        return 1;
    }
    if (!QDir().mkpath(folder)) {
        qWarning("session: cannot write to %s", qPrintable(folder));
        return 1;
    }

    QSurfaceFormat format;
    format.setRenderableType(QSurfaceFormat::OpenGL);
    format.setVersion(3, 3);
    format.setProfile(QSurfaceFormat::CoreProfile);
    format.setAlphaBufferSize(8);
    QOffscreenSurface surface;
    surface.setFormat(format);
    surface.create();
    QOpenGLContext context;
    context.setFormat(format);
    Renderer renderer;
    if (!context.create() || !context.makeCurrent(&surface) || !renderer.initialize()) {
        qWarning("session: OpenGL is not available");
        return 1;
    }

    constexpr double kSize = 148, kFootprint = 2 * kSize, kSide = 1.5 * kFootprint, kDpr = 2, kRedline = 400, kStep = 1.0 / 60;
    const Tokens tokens = Tokens::forDark(true);
    renderer.setBody(renderBody(kSize, kSide, kDpr, tokens, false, StyleKind::Character), QRectF(0, 0, kSide, kSide));
    auto fbo = std::make_unique<QOpenGLFramebufferObject>(QSize(qCeil(kSide * kDpr), qCeil(kSide * kDpr)));
    const MeasureText measure = [&renderer](const TextDraw& text) { return renderer.measure(text, kDpr).width; };
    CharacterStyle style;
    style.setBackingScale(kDpr);
    auto live = std::make_shared<CharacterLive>();
    live->scripted = true;
    Gauge gauge;
    gauge.setRedline(kRedline);

    const double seconds = script->seconds.value_or(30);
    std::vector<double> shots = script->shots;
    if (shots.empty()) {
        for (double t = 0; t <= seconds + 1e-9; t += 0.5) shots.push_back(t);
    }
    const bool tracing = qEnvironmentVariableIntValue("AGENT_SPEED_ORB_TRACE") == 1;
    QString trace;
    const double origin = 400;
    double nextTick = 0, peakStart = -1e9;
    std::size_t nextShot = 0;
    int frames = 0;
    QElapsedTimer wall;
    wall.start();

    auto frameAt = [&](double time, double dt, bool resumed) {
        Frame f;
        f.time = time;
        f.dt = dt;
        f.s = kFootprint;
        f.center = QPointF(kSide / 2, kSide / 2);
        f.unit = kFootprint / 148;
        f.dark = true;
        f.tokens = tokens;
        f.effects = led::Effects::make(2, false);
        f.heat = gauge.heat();
        f.overdrive = gauge.overdrive();
        f.rawHeat = heat::of(gauge.value(), kRedline);
        f.rawOverdrive = std::clamp((gauge.value() - kRedline) / kRedline, 0.0, 1.0);
        f.tier = gauge.tier();
        f.redline = kRedline;
        const double sincePeak = time - peakStart;
        f.peakAge = sincePeak >= 0 && sincePeak < 2.0 ? sincePeak : -1;
        f.displayHeat = f.heat;
        f.redlineAmount = f.tier == Tier::Redline ? 1 : 0;
        f.activity = f.tier != Tier::Idle ? 1 : 0;
        f.resumed = resumed;
        f.unitText = QStringLiteral("tok/s");
        return f;
    };
    auto contentAt = [&]() {
        CharacterContent c;
        c.pack = pack;
        c.noneInstalled = false;
        c.unit = QStringLiteral("tok/s");
        c.peakLabel = QStringLiteral("NEW PEAK");
        c.value = gauge.value();
        c.level = std::clamp(gauge.title() + 1, 0, 13);
        if (gauge.value() > 0) c.agents = {QColor(217, 119, 87)};
        c.live = live;
        return c;
    };
    auto render = [&](const Frame& f) {
        ReadoutContent readout;
        readout.value = QString::number(qRound(gauge.value()));
        readout.unit = f.unitText;
        readout.peakLabel = QStringLiteral("NEW PEAK");
        Scene scene;
        style.build(scene, f);
        style.buildReadout(scene, f, readout, measure);
        fbo->bind();
        renderer.render(scene, QSizeF(kSide, kSide), kDpr, f.center);
        fbo->release();
        return fbo->toImage();
    };

    const int total = static_cast<int>(std::lround(seconds * 60));
    for (int n = 0; n <= total; ++n) {
        const double t = n / 60.0;
        const double time = origin + t;
        const bool peak = script->apply(t, kStep, time, *live);
        if (t >= nextTick || peak) {
            nextTick = t + 0.5;
            gauge.update(script->speedAt(t).value_or(0), time);
        }
        if (n == 0) gauge.settle();
        gauge.step(time);
        if (peak) {
            peakStart = time;
            style.celebrate(time);
        }
        QCoreApplication::processEvents();
        const Frame f = frameAt(time, kStep, n == 0);
        style.setContent(contentAt());
        style.step(f);
        ++frames;
        while (nextShot < shots.size() && t >= shots[nextShot] - 1e-9) {
            QImage image = render(f);
            // The atlases it asks for decode off this thread: draw again once they are in.
            for (int wait = 0; wait < 500 && CharacterAssets::shared().loading(); ++wait) {
                QCoreApplication::processEvents();
                QThread::msleep(20);
            }
            QCoreApplication::processEvents();
            image = render(f);
            const auto name = QString::asprintf("%06.2f", shots[nextShot]);
            image.save(folder + QStringLiteral("/frame-") + name + QStringLiteral(".png"));
            if (tracing) {
                const QString wait = live->waitingSince ? QString::number(time - *live->waitingSince, 'f', 1) : QStringLiteral("-");
                trace += QStringLiteral("t=%1 %2 wait=%3\n").arg(name, style.behaviorTrace(), wait);
            }
            ++nextShot;
        }
    }
    if (tracing) {
        QFile out(folder + QStringLiteral("/trace.txt"));
        if (out.open(QIODevice::WriteOnly | QIODevice::Truncate)) out.write(trace.toUtf8());
    }
    qInfo("character session: %s, %d frames over %.1f s, %zu shots, %.1f s wall", pack->id().c_str(), frames, seconds, nextShot,
          static_cast<double>(wall.elapsed()) / 1000);
    fbo.reset();
    renderer.release();
    context.doneCurrent();
    return 0;
}

}  // namespace orb
