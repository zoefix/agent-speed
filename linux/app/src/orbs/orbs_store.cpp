#include "orbs/orbs_store.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QSettings>
#include <QUuid>
#include <algorithm>
#include <cmath>

namespace {

constexpr int kMaxArrangements = 16;
const QStringList kStyles{QStringLiteral("led"), QStringLiteral("needle"), QStringLiteral("liquid"), QStringLiteral("reactor"),
                          QStringLiteral("minimal")};
const QStringList kTraffic{QStringLiteral("sources"), QStringLiteral("machine"), QStringLiteral("intelligence"), QStringLiteral("off")};

std::optional<int> optionalInt(const QJsonValue& value) {
    if (!value.isDouble()) return std::nullopt;
    return value.toInt();
}

std::optional<QString> optionalString(const QJsonValue& value) {
    if (!value.isString() || value.toString().trimmed().isEmpty()) return std::nullopt;
    return value.toString();
}

QJsonValue json(const std::optional<QString>& value) { return value ? QJsonValue(*value) : QJsonValue(QJsonValue::Null); }
QJsonValue json(const std::optional<int>& value) { return value ? QJsonValue(*value) : QJsonValue(QJsonValue::Null); }

}  // namespace

OrbConfig OrbConfig::fromJson(const QJsonObject& o) {
    OrbConfig c;
    c.extra = o;
    c.id = o.value(QStringLiteral("id")).toString();
    if (const auto name = optionalString(o.value(QStringLiteral("name")))) c.name = name->trimmed().left(32);
    const auto sources = o.value(QStringLiteral("sources"));
    if (sources.isArray()) {
        QStringList list;
        for (const auto& v : sources.toArray()) {
            if (v.isString() && !list.contains(v.toString())) list.push_back(v.toString());
        }
        if (!list.isEmpty()) c.sources = list;
    }
    c.style = o.value(QStringLiteral("style")).toString();
    if (!kStyles.contains(c.style)) c.style = QStringLiteral("led");
    c.size = o.value(QStringLiteral("size")).toDouble(148);
    c.opacity = o.value(QStringLiteral("opacity")).toDouble(1);
    c.alwaysOnTop = o.value(QStringLiteral("alwaysOnTop")).toBool(true);
    c.clickThrough = o.value(QStringLiteral("clickThrough")).toBool(false);
    c.fadeWhenIdle = o.value(QStringLiteral("fadeWhenIdle")).toBool(false);
    c.visible = o.value(QStringLiteral("visible")).toBool(true);
    c.redlineOutput = optionalInt(o.value(QStringLiteral("redlineOutput")));
    c.redlineTotal = optionalInt(o.value(QStringLiteral("redlineTotal")));
    c.titleTheme = optionalString(o.value(QStringLiteral("titleTheme")));
    c.unit = o.value(QStringLiteral("unit")).toString() == QLatin1String("cost") ? QStringLiteral("cost") : QStringLiteral("tokens");
    // Absent (older builds) → sources; unknown → off.
    const auto traffic = o.value(QStringLiteral("traffic"));
    c.traffic = traffic.isUndefined() ? QStringLiteral("sources") : kTraffic.contains(traffic.toString()) ? traffic.toString() : QStringLiteral("off");
    const auto showName = o.value(QStringLiteral("showName"));
    if (showName.isBool()) c.showName = showName.toBool();
    return c;
}

QJsonObject OrbConfig::toJson() const {
    QJsonObject o = extra;
    o.insert(QStringLiteral("id"), id);
    o.insert(QStringLiteral("name"), json(name));
    if (sources) {
        o.insert(QStringLiteral("sources"), QJsonArray::fromStringList(*sources));
    } else {
        o.insert(QStringLiteral("sources"), QStringLiteral("all"));
    }
    o.insert(QStringLiteral("style"), style);
    o.insert(QStringLiteral("size"), size);
    o.insert(QStringLiteral("opacity"), opacity);
    o.insert(QStringLiteral("alwaysOnTop"), alwaysOnTop);
    o.insert(QStringLiteral("clickThrough"), clickThrough);
    o.insert(QStringLiteral("fadeWhenIdle"), fadeWhenIdle);
    o.insert(QStringLiteral("visible"), visible);
    o.insert(QStringLiteral("redlineOutput"), json(redlineOutput));
    o.insert(QStringLiteral("redlineTotal"), json(redlineTotal));
    o.insert(QStringLiteral("titleTheme"), json(titleTheme));
    o.insert(QStringLiteral("unit"), unit);
    o.insert(QStringLiteral("traffic"), traffic);
    o.insert(QStringLiteral("showName"), showName ? QJsonValue(*showName) : QJsonValue(QJsonValue::Null));
    return o;
}

OrbPosition OrbPosition::fromJson(const QJsonObject& o) {
    OrbPosition p;
    p.display = o.value(QStringLiteral("display")).toString();
    p.xAnchor = o.value(QStringLiteral("xAnchor")).toString(QStringLiteral("fraction"));
    p.yAnchor = o.value(QStringLiteral("yAnchor")).toString(QStringLiteral("fraction"));
    p.x = o.value(QStringLiteral("x")).toDouble(0.5);
    p.y = o.value(QStringLiteral("y")).toDouble(0.32);
    return p;
}

QJsonObject OrbPosition::toJson() const {
    return {{QStringLiteral("display"), display}, {QStringLiteral("xAnchor"), xAnchor}, {QStringLiteral("x"), x},
            {QStringLiteral("yAnchor"), yAnchor}, {QStringLiteral("y"), y}};
}

OrbsStore::OrbsStore(QObject* parent) : QObject(parent) { load(); }

QString OrbsStore::newId() { return QUuid::createUuid().toString(QUuid::WithoutBraces).toUpper(); }

OrbConfig OrbsStore::clamped(OrbConfig c) {
    if (c.id.isEmpty()) c.id = newId();
    c.size = std::clamp(std::isfinite(c.size) ? c.size : 148.0, c.minimumSize(), 240.0);
    c.opacity = std::clamp(std::isfinite(c.opacity) ? c.opacity : 1.0, 0.4, 1.0);
    if (c.name) {
        const QString trimmed = c.name->trimmed().left(32);
        c.name = trimmed.isEmpty() ? std::nullopt : std::optional<QString>(trimmed);
    }
    if (c.sources && c.sources->isEmpty()) c.sources.reset();
    return c;
}

void OrbsStore::load() {
    QSettings settings;
    const auto text = settings.value(QStringLiteral("orbs")).toString();
    const auto doc = QJsonDocument::fromJson(text.toUtf8());
    if (!doc.isObject()) {
        migrate();
        return;
    }
    const auto root = doc.object();
    for (const auto& v : root.value(QStringLiteral("orbs")).toArray()) {
        if (static_cast<int>(orbs_.size()) >= kMaxOrbs) break;
        auto c = clamped(OrbConfig::fromJson(v.toObject()));
        if (indexOf(c.id) >= 0) c.id = newId();
        orbs_.push_back(c);
    }
    layouts_ = root.value(QStringLiteral("layouts")).toObject();
    for (const auto& key : root.value(QStringLiteral("layoutOrder")).toArray()) arrangementOrder_.push_back(key.toString());
    if (orbs_.empty()) migrate();
}

// The first launch of the multi-orb version: one all-sources orb from the single-orb keys, which stay untouched.
void OrbsStore::migrate() {
    QSettings settings;
    settings.beginGroup(QStringLiteral("orb"));
    OrbConfig c;
    c.id = newId();
    c.style = settings.value(QStringLiteral("style"), QStringLiteral("led")).toString();
    if (!kStyles.contains(c.style)) c.style = QStringLiteral("led");
    c.size = settings.value(QStringLiteral("size"), 148.0).toDouble();
    c.alwaysOnTop = settings.value(QStringLiteral("alwaysOnTop"), true).toBool();
    c.clickThrough = settings.value(QStringLiteral("clickThrough"), false).toBool();
    c.traffic = settings.value(QStringLiteral("traffic"), QStringLiteral("sources")).toString();
    if (!kTraffic.contains(c.traffic)) c.traffic = QStringLiteral("off");
    if (settings.contains(QStringLiteral("center"))) legacyCenter_ = settings.value(QStringLiteral("center")).toPointF();
    settings.endGroup();
    orbs_ = {clamped(c)};
    save();
}

QJsonObject OrbsStore::document() const {
    QJsonArray list;
    for (const auto& c : orbs_) list.push_back(c.toJson());
    return {{QStringLiteral("version"), 1}, {QStringLiteral("orbs"), list}, {QStringLiteral("layouts"), layouts_},
            {QStringLiteral("layoutOrder"), QJsonArray::fromStringList(arrangementOrder_)}};
}

void OrbsStore::save() {
    QSettings().setValue(QStringLiteral("orbs"), QString::fromUtf8(QJsonDocument(document()).toJson(QJsonDocument::Compact)));
    emit changed();
}

const OrbConfig* OrbsStore::find(const QString& id) const {
    const int i = indexOf(id);
    return i < 0 ? nullptr : &orbs_[static_cast<std::size_t>(i)];
}

int OrbsStore::indexOf(const QString& id) const {
    for (std::size_t i = 0; i < orbs_.size(); ++i) {
        if (orbs_[i].id == id) return static_cast<int>(i);
    }
    return -1;
}

void OrbsStore::update(const OrbConfig& config) {
    const int i = indexOf(config.id);
    if (i < 0) return;
    orbs_[static_cast<std::size_t>(i)] = clamped(config);
    save();
}

bool OrbsStore::insert(OrbConfig config, const QString& after) {
    if (static_cast<int>(orbs_.size()) >= kMaxOrbs) return false;
    config = clamped(config);
    if (indexOf(config.id) >= 0) config.id = newId();
    const int at = after.isEmpty() ? -1 : indexOf(after);
    orbs_.insert(at < 0 ? orbs_.end() : orbs_.begin() + at + 1, config);
    save();
    return true;
}

void OrbsStore::remove(const QString& id) {
    const int i = indexOf(id);
    if (i < 0 || orbs_.size() <= 1) return;
    orbs_.erase(orbs_.begin() + i);
    save();
}

void OrbsStore::replaceAll(std::vector<OrbConfig> orbs) {
    if (orbs.empty()) return;
    orbs_.clear();
    for (auto& c : orbs) {
        if (static_cast<int>(orbs_.size()) >= kMaxOrbs) break;
        orbs_.push_back(clamped(c));
    }
    save();
}

void OrbsStore::move(int from, int to) {
    const int n = static_cast<int>(orbs_.size());
    if (from < 0 || from >= n || to < 0 || to >= n || from == to) return;
    auto item = orbs_[static_cast<std::size_t>(from)];
    orbs_.erase(orbs_.begin() + from);
    orbs_.insert(orbs_.begin() + to, item);
    save();
}

std::optional<OrbPosition> OrbsStore::position(const QString& arrangement, const QString& id) const {
    const auto layout = layouts_.value(arrangement).toObject();
    if (!layout.contains(id)) return std::nullopt;
    return OrbPosition::fromJson(layout.value(id).toObject());
}

void OrbsStore::setPosition(const QString& arrangement, const QString& id, const OrbPosition& position) {
    auto layout = layouts_.value(arrangement).toObject();
    layout.insert(id, position.toJson());
    layouts_.insert(arrangement, layout);
    arrangementOrder_.removeAll(arrangement);
    arrangementOrder_.push_back(arrangement);
    while (arrangementOrder_.size() > kMaxArrangements) layouts_.remove(arrangementOrder_.takeFirst());
    QSettings().setValue(QStringLiteral("orbs"), QString::fromUtf8(QJsonDocument(document()).toJson(QJsonDocument::Compact)));
}

std::optional<OrbPosition> OrbsStore::lastPosition(const QString& id, const QString& except) const {
    for (auto it = arrangementOrder_.crbegin(); it != arrangementOrder_.crend(); ++it) {
        if (*it == except) continue;
        if (auto p = position(*it, id)) return p;
    }
    return std::nullopt;
}

void OrbsStore::keepUndo() { undo_ = document(); }

bool OrbsStore::undo() {
    if (!undo_) return false;
    const auto root = *undo_;
    undo_.reset();
    orbs_.clear();
    for (const auto& v : root.value(QStringLiteral("orbs")).toArray()) orbs_.push_back(clamped(OrbConfig::fromJson(v.toObject())));
    layouts_ = root.value(QStringLiteral("layouts")).toObject();
    arrangementOrder_.clear();
    for (const auto& key : root.value(QStringLiteral("layoutOrder")).toArray()) arrangementOrder_.push_back(key.toString());
    save();
    return true;
}

void OrbsStore::dropUndo() { undo_.reset(); }
