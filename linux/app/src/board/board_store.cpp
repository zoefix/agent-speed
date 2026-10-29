#include "board/board_store.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>
#include <QUuid>
#include <optional>

BoardStore::BoardStore(QObject* parent) : QObject(parent) { load(); }

QVariantMap BoardStore::normalized(QVariantMap b) {
    const auto style = b.value(QStringLiteral("style")).toString();
    if (style != QLatin1String("glass") && style != QLatin1String("timing") && style != QLatin1String("neon")) b.insert(QStringLiteral("style"), QStringLiteral("glass"));
    const auto size = b.value(QStringLiteral("size")).toString();
    if (size != QLatin1String("S") && size != QLatin1String("M") && size != QLatin1String("L")) b.insert(QStringLiteral("size"), QStringLiteral("M"));
    if (!b.contains(QStringLiteral("sources"))) b.insert(QStringLiteral("sources"), QStringLiteral("all"));
    const auto opacity = b.value(QStringLiteral("opacity"), 1.0).toDouble();
    b.insert(QStringLiteral("opacity"), std::clamp(opacity, 0.3, 1.0));
    for (const auto* flag : {"alwaysOnTop", "allSpaces", "visible"})
        if (!b.value(QLatin1String(flag)).canConvert<bool>() || b.value(QLatin1String(flag)).typeId() != QMetaType::Bool) b.insert(QLatin1String(flag), true);
    if (b.value(QStringLiteral("clickThrough")).typeId() != QMetaType::Bool) b.insert(QStringLiteral("clickThrough"), false);
    auto readouts = b.value(QStringLiteral("readouts")).toMap();
    for (const auto* readout : {"intelligence", "thinking", "cost"})
        if (readouts.value(QLatin1String(readout)).typeId() != QMetaType::Bool) readouts.insert(QLatin1String(readout), true);
    readouts.remove(QStringLiteral("rowCost"));
    b.insert(QStringLiteral("readouts"), readouts);
    return b;
}

void BoardStore::load() {
    const auto doc = QJsonDocument::fromJson(QSettings().value(QStringLiteral("boards")).toByteArray());
    const auto root = doc.object();
    boards_.clear();
    for (const auto& value : root.value(QStringLiteral("boards")).toArray()) {
        auto board = value.toObject().toVariantMap();
        if (board.value(QStringLiteral("id")).toString().isEmpty() || boards_.size() >= kMaxBoards) continue;
        boards_.append(normalized(board));
    }
    positions_ = root.value(QStringLiteral("positions")).toObject().toVariantMap();
    lastStyle_ = root.value(QStringLiteral("lastStyle")).toString(QStringLiteral("neon"));
}

void BoardStore::save() {
    QJsonArray list;
    for (const auto& b : boards_) list.append(QJsonObject::fromVariantMap(b.toMap()));
    QSettings().setValue(QStringLiteral("boards"),
                         QJsonDocument(QJsonObject{{QStringLiteral("version"), 1},
                                                   {QStringLiteral("boards"), list},
                                                   {QStringLiteral("positions"), QJsonObject::fromVariantMap(positions_)},
                                                   {QStringLiteral("lastStyle"), lastStyle_}})
                             .toJson(QJsonDocument::Compact));
    emit changed();
}

bool BoardStore::anyVisible() const {
    return std::any_of(boards_.begin(), boards_.end(), [](const QVariant& b) { return b.toMap().value(QStringLiteral("visible")).toBool(); });
}

QVariantMap BoardStore::board(const QString& id) const {
    for (const auto& b : boards_)
        if (b.toMap().value(QStringLiteral("id")).toString() == id) return b.toMap();
    return {};
}

std::optional<QPoint> BoardStore::position(const QString& id) const {
    const auto p = positions_.value(id).toMap();
    if (p.isEmpty()) return std::nullopt;
    return QPoint(p.value(QStringLiteral("x")).toInt(), p.value(QStringLiteral("y")).toInt());
}

QString BoardStore::add() {
    if (!canAdd()) return {};
    const auto id = QUuid::createUuid().toString(QUuid::WithoutBraces);
    QVariantMap b{{QStringLiteral("id"), id}, {QStringLiteral("style"), lastStyle_}, {QStringLiteral("size"), QStringLiteral("M")}};
    boards_.append(normalized(b));
    save();
    return id;
}

void BoardStore::remove(const QString& id) {
    for (int i = 0; i < boards_.size(); ++i) {
        if (boards_[i].toMap().value(QStringLiteral("id")).toString() != id) continue;
        boards_.removeAt(i);
        positions_.remove(id);
        save();
        return;
    }
}

void BoardStore::set(const QString& id, const QString& key, const QVariant& value) {
    for (auto& entry : boards_) {
        auto b = entry.toMap();
        if (b.value(QStringLiteral("id")).toString() != id) continue;
        b.insert(key, value);
        entry = normalized(b);
        if (key == QLatin1String("style")) lastStyle_ = entry.toMap().value(QStringLiteral("style")).toString();
        save();
        return;
    }
}

void BoardStore::setReadout(const QString& id, const QString& readout, bool on) {
    auto readouts = board(id).value(QStringLiteral("readouts")).toMap();
    readouts.insert(readout, on);
    set(id, QStringLiteral("readouts"), readouts);
}

void BoardStore::setSources(const QString& id, const QVariant& sources) { set(id, QStringLiteral("sources"), sources); }

void BoardStore::setAllVisible(bool visible) {
    for (auto& entry : boards_) {
        auto b = entry.toMap();
        b.insert(QStringLiteral("visible"), visible);
        entry = b;
    }
    save();
}

void BoardStore::setPosition(const QString& id, QPoint topLeft) {
    positions_.insert(id, QVariantMap{{QStringLiteral("x"), topLeft.x()}, {QStringLiteral("y"), topLeft.y()}});
    save();
}
