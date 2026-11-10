#pragma once

#include <QImage>
#include <QList>
#include <QObject>
#include <QPointer>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>
#include <map>
#include <memory>
#include <utility>

#include "orbs/orb_views.hpp"
#include "orbs/orbs_store.hpp"

class CharacterLibrary;
class Preferences;
class QuotaView;
class RemoteHostsView;
class Strings;
class TitleThemes;
class NamePlate;
class QMenu;
namespace orb {
class OrbWindow;
struct Disc;
}

// The worker's view of the orbs: their ids and sources.
std::vector<OrbSpec> orbSpecs(const OrbsStore& store);

// DESIGN 2b Multiple orbs: up to eight floating orbs, each tracking its own sources with its own position, size, style
// and window options. Owns the orb windows and their name plates, places new orbs, snaps them to each other, splits and
// merges them with an 8 s undo, builds their context menu, and is the `orbs` object of Settings and the tray.
class OrbManager : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList list READ list NOTIFY listChanged)
    // Each orb's live value by id ({value, valueText, unavailable}), apart from the list so its rows are not rebuilt.
    Q_PROPERTY(QVariantMap values READ values NOTIFY valuesChanged)
    Q_PROPERTY(QVariantList sources READ sources NOTIFY sourcesChanged)
    Q_PROPERTY(int count READ count NOTIFY listChanged)
    Q_PROPERTY(int maximum READ maximum CONSTANT)
    Q_PROPERTY(bool shown READ shown WRITE setShown NOTIFY shownChanged)
    Q_PROPERTY(QString names READ names WRITE setNames NOTIFY namesChanged)
    Q_PROPERTY(int effectsLevel READ effectsLevel WRITE setEffectsLevel NOTIFY effectsLevelChanged)
    Q_PROPERTY(QString selected READ selected WRITE select NOTIFY selectedChanged)
    Q_PROPERTY(QString banner READ banner NOTIFY bannerChanged)
    Q_PROPERTY(bool canSplitAll READ canSplitAll NOTIFY sourcesChanged)

public:
    OrbManager(const Strings& strings, const Preferences& prefs, OrbsStore& store, QObject* parent = nullptr);
    ~OrbManager() override;
    void setTitles(const TitleThemes* titles);
    // DESIGN 13: the hosts' names (automatic names, the Hosts submenu); renames re-sync the windows.
    void setHostsView(const RemoteHostsView* hosts);
    // CHARACTERS 2, 7: the installed packs the `character` orbs show and download.
    void setCharacters(CharacterLibrary* characters);
    // QUOTAS 6, DESIGN 2a.6: the quotas a `character` orb's 額度 card lists, for its sources and hosts.
    void setQuotas(const QuotaView* quotas);
    // Shows the orbs where they were (call once the windows may appear).
    void start();
    // While an orb uses the `character` style, download every pack not set aside (CHARACTERS 2, Automatic download).
    void resumeCharacterDownloads();
    bool anyCharacterOrb() const;

    QVariantList list() const;
    QVariantMap values() const;
    QVariantList sources() const;
    int count() const { return static_cast<int>(store_.orbs().size()); }
    int maximum() const { return OrbsStore::kMaxOrbs; }
    bool shown() const { return shown_; }
    void setShown(bool shown);
    QString names() const { return names_; }
    void setNames(const QString& names);
    int effectsLevel() const { return effectsLevel_; }
    void setEffectsLevel(int level);
    QString selected() const { return selected_; }
    void select(const QString& id);
    QString banner() const { return banner_; }
    bool canSplitAll() const { return detected().size() >= 2; }
    // Development aids.
    orb::OrbWindow* first() const;
    // Development aid: the first orb's id and its name plate's picture.
    QString firstId() const;
    QImage platePicture(const QString& id) const;
    // The source chip (folded into the plate while one shows) for `seconds`.
    void showChip(const QString& id, double seconds);
    void forceDark(bool on);
    // Development aid: the orb's context menu and its Hosts and Size submenus as pictures, by name (menu, hosts, size).
    QList<std::pair<QString, QImage>> menuPictures(const QString& id);

    Q_INVOKABLE void set(const QString& id, const QString& key, const QVariant& value);
    Q_INVOKABLE void setSource(const QString& id, const QString& source, bool on);
    // DESIGN 2b `character` (empty: Auto) and one switch of `characterReadouts` (`intelligence`, ... `model`).
    Q_INVOKABLE void setCharacter(const QString& id, const QString& character);
    Q_INVOKABLE void setCharacterReadout(const QString& id, const QString& key, bool on);
    // The readout checks in the menus' order (intel, thinking, latency, network, cost, agents, models, quota, model).
    Q_INVOKABLE QVariantList characterReadoutItems() const;
    // DESIGN 13: "all" or the host ids the orb tracks.
    Q_INVOKABLE void setHosts(const QString& id, const QVariant& hosts);
    Q_INVOKABLE void add(const QString& from);
    Q_INVOKABLE void duplicate(const QString& id);
    Q_INVOKABLE void remove(const QString& id);
    Q_INVOKABLE void split(const QString& id);
    Q_INVOKABLE void splitAll();
    Q_INVOKABLE void merge(const QString& target);
    Q_INVOKABLE void undo();
    Q_INVOKABLE void move(int from, int to);
    Q_INVOKABLE void highlight(const QString& id, bool on);
    Q_INVOKABLE void resetPosition(const QString& id);
    // a11y.orb.moveCorner: top-right → bottom-right → bottom-left → top-left of its display, skipping occupied ones.
    void moveToNextCorner(const QString& id);
    Q_INVOKABLE QString autoName(const QString& id) const;
    Q_INVOKABLE bool platesShown(const QString& id) const;

public slots:
    void applySnapshot(const QVariantMap& snapshot);

signals:
    void listChanged();
    void valuesChanged();
    void sourcesChanged();
    void shownChanged();
    void namesChanged();
    void effectsLevelChanged();
    void selectedChanged();
    void bannerChanged();
    // The orbs' sources changed: the worker needs new specs.
    void specsChanged();
    void dashboardRequested();
    void settingsRequested();
    void quitRequested();

private:
    struct Orb {
        std::unique_ptr<orb::OrbWindow> window;
        std::unique_ptr<NamePlate> plate;
        QVariantMap values;  // its OrbViews map of the last tick
        double chipUntil = 0;
        QString quotaKey;  // the quotas' revision and the sources and hosts its 額度 items were made for
    };

    void sync();
    void pushQuotas();
    void syncWindow(const OrbConfig& config, Orb& orb);
    void placeAll();
    void place(const QString& id);
    void savePosition(const QString& id);
    void layoutPlates();
    QPointF centerOf(const OrbPosition& position, double size) const;
    OrbPosition recordOf(QPointF center, double size) const;
    QString arrangement() const;
    QPointF defaultCenter(double size) const;
    QPointF freeSpot(QPointF from, double fromRadius, double radius, const QString& except) const;
    bool fits(QPointF center, double radius, const QString& except) const;
    std::vector<orb::Disc> discs(const QString& except) const;
    void showMenu(const QString& id, QPoint global);
    QMenu* buildMenu(const QString& id);
public:
    // DESIGN 2b Context menu items 2–3 for a `character` orb: `characters.title ▸` (Auto, the installed characters,
    // `characters.more`) and `character.readouts ▸` (a check per card, the network scope); the tray's quick menu too.
    void addCharacterMenu(QMenu* menu, const QString& id);
    void addReadoutsMenu(QMenu* menu, const QString& id);
private:
    void showBanner(const QString& text);
    QStringList detected() const;
    QString nameOf(const OrbConfig& config) const;
    QString sourceName(const QString& key) const;
    void addHostsMenu(QMenu* menu, const OrbConfig& config);
    int redlineOf(const OrbConfig& config) const;
    int visibleCount() const;
    OrbConfig childOf(const OrbConfig& from) const;
    std::vector<OrbConfig> splitInto(std::vector<OrbConfig>& all, int at, const OrbConfig& original, const QStringList& keys);

    const Strings& strings_;
    const Preferences& prefs_;
    OrbsStore& store_;
    const TitleThemes* titles_ = nullptr;
    const RemoteHostsView* hosts_ = nullptr;
    CharacterLibrary* characters_ = nullptr;
    const QuotaView* quotas_ = nullptr;
    std::map<QString, Orb> orbs_;
    QVariantList sourceRows_;
    bool shown_ = true;
    bool started_ = false;
    bool forcedDark_ = false;
    QString names_ = QStringLiteral("auto");
    int effectsLevel_ = 2;
    QString selected_;
    QString banner_;
    QTimer bannerTimer_;
    QTimer plateTimer_;
    std::vector<OrbSpec> appliedSpecs_;
};
