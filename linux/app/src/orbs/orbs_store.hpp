#pragma once

#include <QJsonObject>
#include <QObject>
#include <QPointF>
#include <QStringList>
#include <optional>
#include <vector>

// One floating orb's settings (DESIGN 2b Data model). Unknown fields of a newer version are kept in `extra`.
struct OrbConfig {
    QString id;
    std::optional<QString> name;          // custom name; nullopt: automatic
    std::optional<QStringList> sources;   // nullopt: every enabled source
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
    QJsonObject extra;

    static OrbConfig fromJson(const QJsonObject& object);
    QJsonObject toJson() const;
    double minimumSize() const { return style == QLatin1String("minimal") ? 64 : 96; }
    bool tracksAll() const { return !sources.has_value(); }
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
