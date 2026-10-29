#pragma once

#include <QJsonObject>
#include <QObject>
#include <QStringList>
#include <QVariantList>
#include <optional>
#include <vector>

class Strings;
class Preferences;

// DESIGN 1 Title themes and Custom title sets: nine presets and up to eight custom sets name the 13 speed-title levels
// and the idle label. The level (0 idle, 1–13) never depends on the theme. Persisted as `titleThemes` JSON.
class TitleThemes : public QObject {
    Q_OBJECT
    Q_PROPERTY(int rev READ revision NOTIFY changed)
    Q_PROPERTY(QString selected READ selected NOTIFY changed)
    Q_PROPERTY(QVariantList presets READ presets NOTIFY changed)
    Q_PROPERTY(QVariantList customSets READ customSets NOTIFY changed)
    Q_PROPERTY(bool canCreate READ canCreate NOTIFY changed)
    Q_PROPERTY(bool selectedCustom READ selectedCustom NOTIFY changed)
    Q_PROPERTY(bool vehicleArt READ vehicleArt NOTIFY changed)
    Q_PROPERTY(QVariantMap editor READ editor NOTIFY changed)
    Q_PROPERTY(QString banner READ banner NOTIFY changed)
    Q_PROPERTY(QString notice READ notice NOTIFY changed)

public:
    static constexpr int kLevels = 13;
    static constexpr int kMaxCustom = 8;
    static const QStringList& presetIds();

    TitleThemes(const Strings& strings, Preferences& prefs, QObject* parent = nullptr);

    int revision() const { return revision_; }
    QString selected() const { return selected_; }
    QVariantList presets() const;
    QVariantList customSets() const;
    bool canCreate() const { return static_cast<int>(custom_.size()) < kMaxCustom; }
    bool selectedCustom() const { return customIndex(selected_) >= 0; }
    // The hero keeps the vehicle for Vehicles and the sets based on it (4c); every other theme shows the badge.
    bool vehicleArt() const;
    QVariantMap editor() const;
    QString banner() const { return banner_; }
    QString notice() const { return notice_; }

    // Level 0 is the idle label, 1–13 the titles, of the selected theme (or `theme` when given).
    Q_INVOKABLE QString title(int level, const QString& theme = QString()) const;
    // Thresholds of the 13 levels at the redline, in tok/s (level 1 is 0.5).
    Q_INVOKABLE double threshold(int level, double redline) const;
    Q_INVOKABLE void select(const QString& id);
    Q_INVOKABLE void createFrom(const QString& id);
    Q_INVOKABLE void remove(const QString& id);
    Q_INVOKABLE void undo();
    Q_INVOKABLE void setName(const QString& name);
    Q_INVOKABLE void setBase(const QString& base);
    // Level 0 is the idle row. An empty text follows the base preset.
    Q_INVOKABLE void setText(int level, const QString& text);
    Q_INVOKABLE void resetAll();

signals:
    void changed();

private:
    struct Custom {
        QString id, name, base, idle;
        QStringList titles;  // 13
    };
    QString presetTitle(const QString& preset, int level) const;
    QString presetName(const QString& preset) const;
    int customIndex(const QString& id) const;
    QString baseOf(const QString& id) const;
    QVariantMap card(const QString& id, bool custom) const;
    QString cleaned(const QString& text, bool* hadEmoji) const;
    QString fitted(const QString& text, bool* cut) const;
    void load();
    void save();
    void touch();
    void flash(const QString& key);

    const Strings& strings_;
    Preferences& prefs_;
    QString selected_ = QStringLiteral("vehicles");
    std::vector<Custom> custom_;
    int revision_ = 0;
    QString banner_, notice_;
    // Undo of the last delete or reset all (8 s).
    std::optional<std::pair<std::vector<Custom>, QString>> undo_;
};
