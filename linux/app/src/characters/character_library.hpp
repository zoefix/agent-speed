#pragma once

#include <QDateTime>
#include <QObject>
#include <QSet>
#include <QStringList>
#include <QVariantList>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>

#include "agentspeed/character/pack.hpp"
#include "agentspeed/character/store.hpp"
#include "characters/character_transport.hpp"

class Strings;

// The character packs (CHARACTERS 2): what is installed, the catalog of the `characters` release, downloads the user
// starts, and the automatic download of every pack the user has not set aside (run when an orb switches to the
// `character` style, at launch while one uses it and when network features come back on, one pack at a time). The
// catalog is fetched when Settings shows the list (at most every 10 minutes), when a run needs it, and at most once a
// day otherwise, only while network features are on; its failures stay quiet, download failures are shown.
class CharacterLibrary : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList rows READ rows NOTIFY changed)
    Q_PROPERTY(QVariantList choices READ choices NOTIFY changed)
    Q_PROPERTY(QString catalogState READ catalogState NOTIFY changed)
    Q_PROPERTY(int autoPosition READ autoPosition NOTIFY changed)
    Q_PROPERTY(int autoTotal READ autoTotal NOTIFY changed)
    Q_PROPERTY(QString autoStatus READ autoStatus NOTIFY changed)
    Q_PROPERTY(bool networkEnabled READ networkEnabledNow NOTIFY changed)
    Q_PROPERTY(bool hasInstalled READ hasInstalled NOTIFY installedChanged)

public:
    struct Download {
        double progress = 0;
        bool installing = false;
    };

    CharacterLibrary(agentspeed::character::CharacterStore store, std::unique_ptr<CharacterTransport> transport,
                     const Strings* strings = nullptr, QObject* parent = nullptr);
    ~CharacterLibrary() override;

    // `$XDG_DATA_HOME/agent-speed/characters` (CHARACTERS 2) with the AGENT_SPEED_CHARACTERS development folder.
    static agentspeed::character::CharacterStore standardStore(const QString& appVersion);

    std::function<bool()> networkEnabled = [] { return true; };
    QUrl catalogUrl;

    // Installed packs.
    const std::vector<agentspeed::character::InstalledCharacter>& installed() const { return installed_; }
    const agentspeed::character::InstalledCharacter* installed(const QString& id) const;
    bool hasInstalled() const { return !installed_.empty(); }
    void reloadInstalled();
    // Installed packs in catalog order (the order Auto falls back on), then the rest by id.
    std::vector<agentspeed::character::CharacterSelection::Pack> packs() const;
    // The playable pack of an installed character, loaded once per pack version; null when it does not load.
    std::shared_ptr<const agentspeed::character::Pack> load(const QString& id);
    QString displayName(const QString& id) const;
    QString accent(const QString& id) const;
    // A model vendor's accent (the models card's dot): an installed character's for the vendor, else the catalog's;
    // empty when no character stands for it.
    QString vendorAccent(const QString& vendor) const;
    bool hasUpdate(const QString& id) const;

    // Catalog.
    const std::optional<agentspeed::character::CharacterCatalog>& catalog() const { return catalog_; }
    QString catalogState() const { return catalogState_; }
    Q_INVOKABLE void refreshForList();
    void refreshInBackground();
    Q_INVOKABLE void retry() { refreshCatalog(0); }
    void refreshCatalog(qint64 maxAgeMs);

    // Automatic download.
    void downloadAll(const QStringList& preferringVendors);
    Q_INVOKABLE void stopAutomatic();
    int autoPosition() const { return autoTotal_ - static_cast<int>(autoQueue_.size()); }
    int autoTotal() const { return autoTotal_; }
    QString autoStatus() const;
    const QStringList& autoQueue() const { return autoQueue_; }
    const QString& autoCurrent() const { return autoCurrent_; }
    const QSet<QString>& setAside() const { return setAside_; }

    // Downloads.
    Q_INVOKABLE void download(const QString& id);
    Q_INVOKABLE void cancel(const QString& id);
    Q_INVOKABLE void remove(const QString& id);
    Q_INVOKABLE void openFolder();
    const std::map<QString, Download>& downloads() const { return downloads_; }
    const std::map<QString, QString>& errors() const { return errors_; }

    // The portrait a style card shows: the locked character's preview, else the first installed one's; empty for none.
    Q_INVOKABLE QString previewFor(const QString& locked) const;
    QVariantList rows() const;
    QVariantList choices() const;
    bool networkEnabledNow() const { return networkEnabled(); }
    // Call when network features change (re-reads the switch for QML and resumes nothing by itself).
    void networkChanged() { emit changed(); }

signals:
    void changed();
    void installedChanged();

private:
    void finishCatalog(const CharacterTransport::Response& response);
    void start(const agentspeed::character::CharacterCatalog::Entry& entry);
    void startNextAutomatic();
    void finishDownload(const QString& id, std::optional<agentspeed::character::InstallError> error);
    void markSetAside(const QStringList& ids, bool aside);
    QString text(const QString& key) const;

    agentspeed::character::CharacterStore store_;
    std::unique_ptr<CharacterTransport> transport_;
    const Strings* strings_;
    std::vector<agentspeed::character::InstalledCharacter> installed_;
    std::optional<agentspeed::character::CharacterCatalog> catalog_;
    QString catalogState_ = QStringLiteral("idle");
    QString etag_;
    qint64 fetchedAtMs_ = 0;
    std::map<QString, Download> downloads_;
    std::map<QString, QString> errors_;
    std::map<QString, CharacterTransport::Cancel> cancels_;
    QStringList autoQueue_;
    QString autoCurrent_;
    int autoTotal_ = 0;
    QSet<QString> setAside_;
    QSet<QString> skipped_;
    std::optional<QStringList> pendingRun_;
    std::map<QString, std::pair<std::int64_t, std::shared_ptr<const agentspeed::character::Pack>>> loaded_;
    std::shared_ptr<bool> alive_ = std::make_shared<bool>(true);
};
