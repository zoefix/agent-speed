#pragma once

#include <QJsonObject>
#include <QObject>
#include <QPointF>
#include <QStringList>
#include <QVariantList>
#include <optional>
#include <vector>

#include "agentspeed/character/selection.hpp"

// One floating orb's settings (DESIGN 2b Data model). Unknown fields of a newer version are kept in `extra`.
struct OrbConfig {
    QString id;
    std::optional<QString> name;          // custom name; nullopt: automatic
    std::optional<QStringList> sources;   // nullopt: every enabled source
    // DESIGN 13: host ids (`local` first, unknown ids kept); nullopt: every host.
    std::optional<QStringList> hosts;
    QString style = QStringLiteral("led");
    double size = 148;
    double opacity = 1;
    bool alwaysOnTop = true;
    bool clickThrough = false;
    bool fadeWhenIdle = false;
    bool visible = true;
    std::optional<int> redlineOutput, redlineTotal;
    std::optional<QString> titleTheme;
    QString unit = QStringLiteral("tokens");
    QString traffic = QStringLiteral("sources");
    std::optional<bool> showName;
    // DESIGN 2b `character`: a pack id, or nullopt to follow the fastest model vendor (CHARACTERS 7).
    std::optional<QString> character;
    // DESIGN 2b `characterReadouts`: the cards beside her; written only when not the defaults.
    agentspeed::character::CharacterReadouts characterReadouts;
    QJsonObject extra;

    static OrbConfig fromJson(const QJsonObject& object);
    QJsonObject toJson() const;
    // One size range for every style (DESIGN 2b `size`), so changing the style never resizes an orb.
    static constexpr double kMinSize = 64, kMaxSize = 240;
    bool tracksAll() const { return !sources.has_value(); }
    bool tracksAllHosts() const { return !hosts.has_value(); }
    // `"all"`, one id, or an array of ids; anything else or an empty list is every host.
    static std::optional<QStringList> hostsFromJson(const QJsonValue& value);
    static QJsonValue hostsToJson(const std::optional<QStringList>& hosts);
    // A valid pack id (trimmed), else nullopt (Auto).
    static std::optional<QString> cleanCharacter(const QString& id);
    // A missing key or a value that is not a boolean takes its default.
    static agentspeed::character::CharacterReadouts readoutsFromJson(const QJsonValue& value);
    static QJsonObject readoutsToJson(const agentspeed::character::CharacterReadouts& readouts);
    // The readout checks of the Settings orb editor, in the menus' order: {key, title} per switch.
    static QVariantList readoutItems();
    // The disc the orb occupies: its size × the style's footprint (DESIGN 2a.6).
    double footprintDiameter() const { return size * agentspeed::character::footprint::scale(style.toStdString()); }
    // Add and Split (DESIGN 2b): a new orb with `from`'s look and toggles, its character and cards included.
    static OrbConfig childOf(const OrbConfig& from, const QString& id);
};

// Where an orb sits on one display: edge-docked distance or a fraction of the usable area, per axis.
struct OrbPosition {
    QString display;
    QString xAnchor = QStringLiteral("fraction"), yAnchor = QStringLiteral("fraction");
    double x = 0.5, y = 0.32;

    static OrbPosition fromJson(const QJsonObject& object);
    QJsonObject toJson() const;
};

// DESIGN 2b: the orbs (1–8) and their positions per display arrangement, persisted as one JSON value (settings key
// `orbs`) on every change, migrated once from the single-orb keys. List edits that can be undone (split, merge, remove)
// keep the previous document for 8 s.
class OrbsStore : public QObject {
    Q_OBJECT
public:
    static constexpr int kMaxOrbs = 8;

    explicit OrbsStore(QObject* parent = nullptr);

    const std::vector<OrbConfig>& orbs() const { return orbs_; }
    const OrbConfig* find(const QString& id) const;
    int indexOf(const QString& id) const;

    // Replaces one orb's settings (clamped) and saves.
    void update(const OrbConfig& config);
    // Inserts after `after` (or at the end); returns false at the limit.
    bool insert(OrbConfig config, const QString& after = {});
    void remove(const QString& id);
    void replaceAll(std::vector<OrbConfig> orbs);
    void move(int from, int to);

    std::optional<OrbPosition> position(const QString& arrangement, const QString& id) const;
    void setPosition(const QString& arrangement, const QString& id, const OrbPosition& position);
    // The record of an orb in the most recently used arrangement other than `arrangement`, if any.
    std::optional<OrbPosition> lastPosition(const QString& id, const QString& except) const;

    // Undo (8 s): the document before a split, merge or remove.
    void keepUndo();
    bool undo();
    void dropUndo();

    static QString newId();
    // The single orb's saved disc center (global points) from before the migration, for its first placement.
    std::optional<QPointF> legacyCenter() const { return legacyCenter_; }

signals:
    void changed();

private:
    void load();
    void migrate();
    void save();
    QJsonObject document() const;
    static OrbConfig clamped(OrbConfig config);

    std::vector<OrbConfig> orbs_;
    QJsonObject layouts_;
    QStringList arrangementOrder_;  // least recently used first
    std::optional<QJsonObject> undo_;
    std::optional<QPointF> legacyCenter_;
};
