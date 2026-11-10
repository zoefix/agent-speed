#include "characters/character_library.hpp"

#include <QDesktopServices>
#include <QDir>
#include <QSettings>
#include <QStandardPaths>
#include <QTemporaryDir>
#include <QThreadPool>
#include <QUrl>
#include <algorithm>
#include <cmath>

#include "agentspeed/character/error.hpp"
#include "agentspeed/traffic.hpp"
#include "strings.hpp"

namespace ac = agentspeed::character;

namespace {

constexpr qint64 kListRefreshMs = 10 * 60 * 1000;
constexpr qint64 kBackgroundRefreshMs = 24 * 60 * 60 * 1000;

QString q(const std::string& s) { return QString::fromStdString(s); }

QString fillArgs(QString text, const QStringList& args) {
    for (const auto& arg : args) {
        int at = -1;
        for (int i = 0; i + 1 < text.size(); ++i) {
            if (text[i] == QLatin1Char('%') && (text[i + 1] == QLatin1Char('@') || text[i + 1] == QLatin1Char('d'))) {
                at = i;
                break;
            }
        }
        if (at < 0) break;
        text.replace(at, 2, arg);
    }
    return text;
}

// One pool thread for verifying and unpacking, so installs never block the UI and never run two at once.
QThreadPool& installPool() {
    static QThreadPool pool;
    pool.setMaxThreadCount(1);
    return pool;
}

}  // namespace

CharacterLibrary::CharacterLibrary(ac::CharacterStore store, std::unique_ptr<CharacterTransport> transport,
                                   const Strings* strings, QObject* parent)
    : QObject(parent), store_(std::move(store)), transport_(std::move(transport)), strings_(strings) {
    catalogUrl = QUrl(QString::fromLatin1(ac::CharacterCatalog::kUrl.data(), static_cast<int>(ac::CharacterCatalog::kUrl.size())));
    for (const auto& id : QSettings().value(QLatin1String(ac::auto_download::kSetAsideKey)).toStringList()) setAside_.insert(id);
    if (strings_) connect(strings_, &Strings::changed, this, &CharacterLibrary::changed);
    reloadInstalled();
}

CharacterLibrary::~CharacterLibrary() {
    *alive_ = false;
    for (auto& [id, cancel] : cancels_) cancel();
    installPool().waitForDone();
}

ac::CharacterStore CharacterLibrary::standardStore(const QString& appVersion) {
    const QString data = QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation);
    std::optional<std::filesystem::path> dev;
    const QByteArray env = qgetenv(ac::CharacterStore::kDevelopmentVariable);
    if (!env.isEmpty()) dev = std::filesystem::path(env.toStdString());
    return ac::CharacterStore(std::filesystem::path((data + QStringLiteral("/agent-speed/characters")).toStdString()), dev,
                              appVersion.toStdString());
}

QString CharacterLibrary::text(const QString& key) const { return strings_ ? strings_->t(key) : key; }

// ---- installed

void CharacterLibrary::reloadInstalled() {
    auto next = store_.installed();
    if (next == installed_) return;
    installed_ = std::move(next);
    for (auto it = loaded_.begin(); it != loaded_.end();) {
        const auto* pack = installed(it->first);
        if (!pack || pack->manifest.packVersion != it->second.first) it = loaded_.erase(it);
        else ++it;
    }
    emit installedChanged();
    emit changed();
}

const ac::InstalledCharacter* CharacterLibrary::installed(const QString& id) const {
    const auto key = id.toStdString();
    for (const auto& p : installed_) {
        if (p.id() == key) return &p;
    }
    return nullptr;
}

std::vector<ac::CharacterSelection::Pack> CharacterLibrary::packs() const {
    std::map<std::string, std::size_t> order;
    if (catalog_) {
        for (std::size_t i = 0; i < catalog_->characters.size(); ++i) order.emplace(catalog_->characters[i].id, i);
    }
    std::vector<const ac::InstalledCharacter*> sorted;
    for (const auto& p : installed_) sorted.push_back(&p);
    std::sort(sorted.begin(), sorted.end(), [&](const auto* a, const auto* b) {
        const auto ia = order.count(a->id()) ? order[a->id()] : SIZE_MAX, ib = order.count(b->id()) ? order[b->id()] : SIZE_MAX;
        return ia != ib ? ia < ib : a->id() < b->id();
    });
    std::vector<ac::CharacterSelection::Pack> out;
    for (const auto* p : sorted) out.push_back({p->id(), p->manifest.vendors});
    return out;
}

std::shared_ptr<const ac::Pack> CharacterLibrary::load(const QString& id) {
    const auto* pack = installed(id);
    if (!pack) return nullptr;
    auto it = loaded_.find(id);
    if (it != loaded_.end() && it->second.first == pack->manifest.packVersion) return it->second.second;
    std::shared_ptr<const ac::Pack> loadedPack;
    try {
        loadedPack = ac::Pack::load(pack->folder);
    } catch (const std::exception& e) {
        qWarning("characters: %s does not load: %s", qPrintable(id), e.what());
    }
    loaded_[id] = {pack->manifest.packVersion, loadedPack};
    return loadedPack;
}

QString CharacterLibrary::displayName(const QString& id) const {
    const QString language = strings_ ? strings_->language() : QStringLiteral("en");
    if (const auto* pack = installed(id)) return q(pack->manifest.name.localized(language.toStdString()));
    if (catalog_) {
        if (const auto* e = catalog_->find(id.toStdString())) return q(e->name.localized(language.toStdString()));
    }
    return id;
}

QString CharacterLibrary::accent(const QString& id) const {
    if (const auto* pack = installed(id)) return q(pack->manifest.accent);
    if (catalog_) {
        if (const auto* e = catalog_->find(id.toStdString())) return q(e->accent);
    }
    return {};
}

QString CharacterLibrary::vendorAccent(const QString& vendor) const {
    if (vendor.isEmpty()) return {};
    const auto hex = agentspeed::character::vendors::accent(vendor.toStdString(), installed_, catalog_ ? &*catalog_ : nullptr);
    return hex ? q(*hex) : QString();
}

bool CharacterLibrary::hasUpdate(const QString& id) const {
    const auto* pack = installed(id);
    if (!pack || pack->isDevelopment || !catalog_) return false;
    const auto* entry = catalog_->find(id.toStdString());
    return entry && entry->packVersion > pack->manifest.packVersion;
}

// ---- catalog

void CharacterLibrary::refreshForList() { refreshCatalog(kListRefreshMs); }

void CharacterLibrary::refreshInBackground() { refreshCatalog(kBackgroundRefreshMs); }

void CharacterLibrary::refreshCatalog(qint64 maxAgeMs) {
    if (!networkEnabled() || catalogState_ == QLatin1String("loading")) return;
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    if (catalog_ && fetchedAtMs_ > 0 && now - fetchedAtMs_ < maxAgeMs) return;
    catalogState_ = QStringLiteral("loading");
    emit changed();
    auto alive = std::weak_ptr<bool>(alive_);
    transport_->getCatalog(catalogUrl, catalog_ ? etag_ : QString(), [this, alive](const CharacterTransport::Response& r) {
        if (alive.lock() && *alive.lock()) finishCatalog(r);
    });
}

void CharacterLibrary::finishCatalog(const CharacterTransport::Response& response) {
    if (!response.failed && response.status == 304 && catalog_) {
        fetchedAtMs_ = QDateTime::currentMSecsSinceEpoch();
        catalogState_ = QStringLiteral("loaded");
    } else if (!response.failed && response.status == 200) {
        try {
            auto next = ac::CharacterCatalog::load(std::string_view(response.body.constData(), static_cast<std::size_t>(response.body.size())));
            if (!catalog_ || !(next == *catalog_)) catalog_ = std::move(next);
            etag_ = response.etag;
            fetchedAtMs_ = QDateTime::currentMSecsSinceEpoch();
            catalogState_ = QStringLiteral("loaded");
        } catch (const ac::PackFailure&) {
            catalogState_ = QStringLiteral("failed");
        }
    } else {
        catalogState_ = catalog_ ? QStringLiteral("loaded") : QStringLiteral("failed");
        qInfo("characters: catalog unavailable (HTTP %d)", response.status);
    }
    emit changed();
    if (pendingRun_) {
        const auto vendors = *pendingRun_;
        pendingRun_.reset();
        if (catalog_) downloadAll(vendors);
    }
}

// ---- automatic download

void CharacterLibrary::downloadAll(const QStringList& vendors) {
    if (!networkEnabled()) return;
    if (!catalog_) {
        pendingRun_ = vendors;
        refreshCatalog(kListRefreshMs);
        return;
    }
    std::set<std::string> installedIds, aside, skipped;
    for (const auto& p : installed_) installedIds.insert(p.id());
    for (const auto& id : setAside_) aside.insert(id.toStdString());
    for (const auto& id : skipped_) skipped.insert(id.toStdString());
    for (const auto& [id, d] : downloads_) skipped.insert(id.toStdString());
    for (const auto& id : autoQueue_) skipped.insert(id.toStdString());
    std::vector<std::string> wanted;
    for (const auto& v : vendors) wanted.push_back(v.toStdString());
    const auto fresh = ac::auto_download::queue(*catalog_, installedIds, aside, skipped, wanted);
    if (fresh.empty()) return;
    if (autoCurrent_.isEmpty() && autoQueue_.isEmpty()) {
        autoTotal_ = 0;
        skipped_.clear();
    }
    for (const auto& id : fresh) autoQueue_.push_back(q(id));
    autoTotal_ += static_cast<int>(fresh.size());
    qInfo("characters: automatic download of %d, live vendors first: %s", static_cast<int>(fresh.size()),
          qPrintable(vendors.isEmpty() ? QStringLiteral("none") : vendors.join(QLatin1Char(','))));
    emit changed();
    startNextAutomatic();
}

void CharacterLibrary::stopAutomatic() {
    QStringList ids = autoQueue_;
    if (!autoCurrent_.isEmpty()) ids.push_back(autoCurrent_);
    autoQueue_.clear();
    markSetAside(ids, true);
    if (!autoCurrent_.isEmpty()) {
        if (auto it = cancels_.find(autoCurrent_); it != cancels_.end()) it->second();
    } else {
        autoTotal_ = 0;
    }
    emit changed();
}

void CharacterLibrary::startNextAutomatic() {
    if (!autoCurrent_.isEmpty()) return;
    while (networkEnabled() && !autoQueue_.isEmpty()) {
        const QString id = autoQueue_.takeFirst();
        if (installed(id) || setAside_.contains(id) || downloads_.count(id) || !catalog_) continue;
        const auto* entry = catalog_->find(id.toStdString());
        if (!entry) continue;
        autoCurrent_ = id;
        start(*entry);
        emit changed();
        return;
    }
    autoQueue_.clear();
    autoTotal_ = 0;
    emit changed();
}

QString CharacterLibrary::autoStatus() const {
    if (autoTotal_ <= 0) return {};
    return fillArgs(text(QStringLiteral("characters.auto.status")),
                    {QString::number(std::max(1, autoPosition())), QString::number(autoTotal_)});
}

void CharacterLibrary::markSetAside(const QStringList& ids, bool aside) {
    auto next = setAside_;
    for (const auto& id : ids) {
        if (aside) next.insert(id);
        else next.remove(id);
    }
    if (next == setAside_) return;
    setAside_ = next;
    QStringList sorted(next.begin(), next.end());
    sorted.sort();
    QSettings().setValue(QLatin1String(ac::auto_download::kSetAsideKey), sorted);
}

// ---- downloads

void CharacterLibrary::download(const QString& id) {
    if (!catalog_) return;
    const auto* entry = catalog_->find(id.toStdString());
    if (!entry) return;
    markSetAside({id}, false);
    start(*entry);
}

void CharacterLibrary::start(const ac::CharacterCatalog::Entry& entry) {
    const QString id = q(entry.id);
    if (downloads_.count(id)) return;
    if (!networkEnabled()) {
        errors_[id] = QStringLiteral("characters.error.offline");
        emit changed();
        return;
    }
    errors_.erase(id);
    downloads_[id] = Download{};
    qInfo("characters: downloading %s (%lld bytes)", qPrintable(id), static_cast<long long>(entry.size));
    emit changed();
    auto dir = std::make_shared<QTemporaryDir>();
    if (!dir->isValid()) {
        finishDownload(id, ac::InstallError::InvalidPack);
        return;
    }
    const QString archive = dir->filePath(QStringLiteral("pack.zip"));
    auto alive = std::weak_ptr<bool>(alive_);
    const auto entryCopy = entry;
    cancels_[id] = transport_->download(
        QUrl(q(entry.url)), archive, entry.size,
        [this, alive, id](double fraction) {
            auto a = alive.lock();
            if (!a || !*a) return;
            auto it = downloads_.find(id);
            if (it == downloads_.end()) return;
            const double p = std::min(std::max(fraction, 0.0), 1.0);
            if (std::abs(p - it->second.progress) >= 0.005 || p >= 1) {
                it->second.progress = p;
                emit changed();
            }
        },
        [this, alive, id, dir, archive, entryCopy](bool ok, bool cancelled) {
            auto a = alive.lock();
            if (!a || !*a) return;
            cancels_.erase(id);
            if (cancelled) {
                finishDownload(id, ac::InstallError::Cancelled);
                return;
            }
            if (!ok) {
                finishDownload(id, ac::InstallError::Network);
                return;
            }
            if (auto it = downloads_.find(id); it != downloads_.end()) {
                it->second.progress = 1;
                it->second.installing = true;
            }
            emit changed();
            const auto store = store_;
            installPool().start([this, alive, id, dir, archive, entryCopy, store] {
                const auto error = ac::installArchive(store, entryCopy, std::filesystem::path(archive.toStdString()));
                QMetaObject::invokeMethod(
                    this,
                    [this, alive, id, dir, error] {
                        auto live = alive.lock();
                        if (live && *live) finishDownload(id, error);
                    },
                    Qt::QueuedConnection);
            });
        });
}

void CharacterLibrary::cancel(const QString& id) {
    markSetAside({id}, true);
    if (auto it = cancels_.find(id); it != cancels_.end()) it->second();
}

void CharacterLibrary::remove(const QString& id) {
    const auto* pack = installed(id);
    if (!pack || pack->isDevelopment) return;
    markSetAside({id}, true);
    try {
        store_.remove(id.toStdString());
        errors_.erase(id);
    } catch (const ac::PackFailure&) {
        errors_[id] = QStringLiteral("characters.error.remove");
    }
    loaded_.erase(id);
    reloadInstalled();
    emit changed();
}

void CharacterLibrary::openFolder() {
    const QString folder = QString::fromStdString(store_.root().string());
    QDir().mkpath(folder);
    QDesktopServices::openUrl(QUrl::fromLocalFile(folder));
}

void CharacterLibrary::finishDownload(const QString& id, std::optional<ac::InstallError> error) {
    downloads_.erase(id);
    cancels_.erase(id);
    std::optional<QString> key;
    if (error && *error != ac::InstallError::Cancelled) key = QString::fromLatin1(ac::errorKey(*error));
    if (key) errors_[id] = *key;
    if (error && *error != ac::InstallError::Cancelled) qInfo("characters: %s failed: %s", qPrintable(id), qPrintable(*key));
    else if (!error) qInfo("characters: installed %s", qPrintable(id));
    reloadInstalled();
    emit changed();
    if (id != autoCurrent_) return;
    autoCurrent_.clear();
    if (key && *key == QLatin1String("characters.error.network")) {
        // The connection is down: end the run; the next launch or style switch starts it again.
        autoQueue_.clear();
    } else if (key) {
        skipped_.insert(id);
    }
    startNextAutomatic();
}

// ---- QML

QVariantList CharacterLibrary::rows() const {
    const QString language = strings_ ? strings_->language() : QStringLiteral("en");
    QVariantList out;
    auto row = [&](const QString& id, const ac::CharacterCatalog::Entry* entry, const ac::InstalledCharacter* pack) {
        QVariantMap m;
        const auto& names = pack ? pack->manifest.name : entry->name;
        const auto& artist = pack ? pack->manifest.artist : entry->artist;
        m.insert(QStringLiteral("id"), id);
        m.insert(QStringLiteral("name"), q(names.localized(language.toStdString())));
        m.insert(QStringLiteral("accent"), q(pack ? pack->manifest.accent : entry->accent));
        m.insert(QStringLiteral("artist"), artist ? fillArgs(text(QStringLiteral("characters.artist")), {q(artist->name)}) : QString());
        m.insert(QStringLiteral("artistLink"), artist && artist->link() ? q(*artist->link()) : QString());
        m.insert(QStringLiteral("size"), entry ? q(agentspeed::traffic::formatTotal(static_cast<double>(entry->size))) : QString());
        m.insert(QStringLiteral("preview"), pack ? QUrl::fromLocalFile(QString::fromStdString(pack->preview().string())).toString() : QString());
        m.insert(QStringLiteral("installed"), pack != nullptr);
        m.insert(QStringLiteral("development"), pack && pack->isDevelopment);
        m.insert(QStringLiteral("canDownload"), entry != nullptr);
        m.insert(QStringLiteral("hasUpdate"), hasUpdate(id));
        const auto d = downloads_.find(id);
        m.insert(QStringLiteral("downloading"), d != downloads_.end());
        m.insert(QStringLiteral("progress"), d != downloads_.end() ? d->second.progress : 0.0);
        m.insert(QStringLiteral("installing"), d != downloads_.end() && d->second.installing);
        const auto e = errors_.find(id);
        m.insert(QStringLiteral("error"), e != errors_.end() ? text(e->second) : QString());
        out.push_back(m);
    };
    std::set<std::string> listed;
    if (catalog_) {
        for (const auto& entry : catalog_->characters) {
            listed.insert(entry.id);
            row(q(entry.id), &entry, installed(q(entry.id)));
        }
    }
    for (const auto& p : installed_) {
        if (!listed.count(p.id())) row(q(p.id()), nullptr, &p);
    }
    return out;
}

QString CharacterLibrary::previewFor(const QString& locked) const {
    const ac::InstalledCharacter* pack = locked.isEmpty() ? nullptr : installed(locked);
    if (!pack) {
        const auto ordered = packs();
        if (!ordered.empty()) pack = installed(q(ordered.front().id));
    }
    return pack ? QUrl::fromLocalFile(QString::fromStdString(pack->preview().string())).toString() : QString();
}

QVariantList CharacterLibrary::choices() const {
    QVariantList out;
    out.push_back(QVariantMap{{QStringLiteral("value"), QString()}, {QStringLiteral("label"), text(QStringLiteral("characters.auto"))}});
    for (const auto& p : packs()) {
        const QString id = q(p.id);
        out.push_back(QVariantMap{{QStringLiteral("value"), id}, {QStringLiteral("label"), displayName(id)}});
    }
    return out;
}
