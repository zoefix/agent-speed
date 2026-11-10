#include "orbs/orb_manager.hpp"

#include "characters/character_library.hpp"
#include "orbs/character_demo.hpp"

#include <QActionGroup>
#include <QDateTime>
#include <QIcon>
#include <QPainter>
#include <QPixmap>
#include <QGuiApplication>
#include <QMenu>
#include <QMessageBox>
#include <QPushButton>
#include <QRegularExpression>
#include <QScreen>
#include <QSettings>
#include <algorithm>
#include <cmath>

#include "agentspeed/model.hpp"
#include "appearance.hpp"
#include "host_dot.hpp"
#include "orb/orb_window.hpp"
#include "orb/style.hpp"
#include "orbs/name_plate.hpp"
#include "orbs/size_slider_action.hpp"
#include "preferences.hpp"
#include "quota/quota_view.hpp"
#include "remote_hosts_view.hpp"
#include "source_colors.hpp"
#include "strings.hpp"
#include "title_themes.hpp"

namespace {

constexpr double kGap = 12, kSnapMargin = 12;
constexpr double kPresets[] = {112, 148, 196};

QString fill(QString text, const QStringList& args) {
    for (const auto& arg : args) {
        const auto at = text.indexOf(QRegularExpression(QStringLiteral("%[d@]")));
        if (at < 0) break;
        text.replace(at, 2, arg);
    }
    return text;
}

// METRICS 1 source order.
QStringList sourceOrder() {
    QStringList keys;
    for (int k = 0; k <= static_cast<int>(agentspeed::SourceKind::QoderCli); ++k) {
        keys.push_back(QString::fromUtf8(agentspeed::sourceId(static_cast<agentspeed::SourceKind>(k))));
    }
    return keys;
}

QStringList ordered(const QStringList& keys) {
    QStringList out;
    for (const auto& key : sourceOrder()) {
        if (keys.contains(key)) out.push_back(key);
    }
    for (const auto& key : keys) {
        if (!out.contains(key)) out.push_back(key);  // unknown keys of a newer version stay
    }
    return out;
}

}  // namespace

OrbManager::OrbManager(const Strings& strings, const Preferences& prefs, OrbsStore& store, QObject* parent)
    : QObject(parent), strings_(strings), prefs_(prefs), store_(store) {
    QSettings settings;
    // The master switch takes the single orb's visibility the first time.
    shown_ = settings.value(QStringLiteral("orbsShown"), settings.value(QStringLiteral("orb/visible"), true)).toBool();
    names_ = settings.value(QStringLiteral("orbNames"), QStringLiteral("auto")).toString();
    if (names_ != QLatin1String("always") && names_ != QLatin1String("never")) names_ = QStringLiteral("auto");
    effectsLevel_ = std::clamp(settings.value(QStringLiteral("orb/effects"), 2).toInt(), 0, 3);
    selected_ = store_.orbs().front().id;
    bannerTimer_.setSingleShot(true);
    connect(&bannerTimer_, &QTimer::timeout, this, [this] {
        store_.dropUndo();
        banner_.clear();
        emit bannerChanged();
    });
    plateTimer_.setSingleShot(true);
    connect(&plateTimer_, &QTimer::timeout, this, &OrbManager::layoutPlates);
    connect(&store_, &OrbsStore::changed, this, &OrbManager::sync);
    // Display changes: every orb takes its record from the new arrangement.
    connect(qApp, &QGuiApplication::screenAdded, this, [this] { QTimer::singleShot(500, this, &OrbManager::placeAll); });
    connect(qApp, &QGuiApplication::screenRemoved, this, [this] { QTimer::singleShot(500, this, &OrbManager::placeAll); });
    connect(&Appearance::instance(), &Appearance::changed, this, &OrbManager::layoutPlates);
    connect(&prefs_, &Preferences::redlineChanged, this, &OrbManager::sync);
    connect(&prefs_, &Preferences::metricChanged, this, &OrbManager::sync);
    sync();
}

OrbManager::~OrbManager() = default;

void OrbManager::setTitles(const TitleThemes* titles) {
    titles_ = titles;
    for (auto& [id, orb] : orbs_) orb.window->setTitles(titles);
    if (titles_) connect(titles_, &TitleThemes::changed, this, &OrbManager::listChanged);
}

void OrbManager::setHostsView(const RemoteHostsView* hosts) {
    hosts_ = hosts;
    if (hosts_) connect(hosts_, &RemoteHostsView::namesChanged, this, &OrbManager::sync);
}

void OrbManager::setQuotas(const QuotaView* quotas) {
    quotas_ = quotas;
    if (quotas_) connect(quotas_, &QuotaView::changed, this, &OrbManager::pushQuotas);
    pushQuotas();
}

// Every `character` orb's 額度 items, made again once the quotas or the orb's sources or hosts changed.
void OrbManager::pushQuotas() {
    if (!quotas_) return;
    for (const auto& c : store_.orbs()) {
        const auto it = orbs_.find(c.id);
        if (it == orbs_.end() || c.style != QLatin1String("character")) continue;
        const QStringList sources = c.sources.value_or(QStringList());
        const QStringList hosts = c.hosts.value_or(QStringList());
        const QString key = QString::number(quotas_->revision()) + QLatin1Char('|') + sources.join(QLatin1Char(',')) + QLatin1Char('|') +
                            (c.tracksAllHosts() ? QStringLiteral("*") : hosts.join(QLatin1Char(',')));
        if (key == it->second.quotaKey) continue;
        it->second.quotaKey = key;
        it->second.window->setQuota(quotas_->cardItems(sources, hosts, c.tracksAllHosts()));
    }
}

void OrbManager::start() {
    started_ = true;
    placeAll();
    sync();
}

orb::OrbWindow* OrbManager::first() const {
    const auto it = orbs_.find(store_.orbs().front().id);
    return it == orbs_.end() ? nullptr : it->second.window.get();
}

QString OrbManager::firstId() const { return store_.orbs().empty() ? QString() : store_.orbs().front().id; }

QImage OrbManager::platePicture(const QString& id) const {
    const auto it = orbs_.find(id);
    return it == orbs_.end() || !it->second.plate ? QImage() : it->second.plate->picture();
}

void OrbManager::forceDark(bool on) {
    forcedDark_ = on;
    for (auto& [id, orb] : orbs_) orb.window->forceDark(on);
}

// ---- names and sources

QString OrbManager::sourceName(const QString& key) const { return strings_.t(QStringLiteral("source.") + key); }

// DESIGN 2b `name`: custom, else all sources, one source's name, `A + B`, or `A +n`, in source order.
QString OrbManager::nameOf(const OrbConfig& c) const {
    if (c.name) return *c.name;
    // DESIGN 13: ` · <host>` or ` · <first> +N` after the sources' name.
    const QString hosts = hosts_ ? hosts_->nameSuffix(c.hosts) : QString();
    if (!c.sources) return strings_.t(QStringLiteral("orbs.sources.all")) + hosts;
    const auto keys = ordered(*c.sources);
    if (keys.size() == 1) return sourceName(keys[0]) + hosts;
    if (keys.size() == 2) return fill(strings_.t(QStringLiteral("orbs.name.pair")), {sourceName(keys[0]), sourceName(keys[1])}) + hosts;
    return fill(strings_.t(QStringLiteral("orbs.name.more")), {sourceName(keys[0]), QString::number(keys.size() - 1)}) + hosts;
}

QString OrbManager::autoName(const QString& id) const {
    const auto* c = store_.find(id);
    if (!c) return {};
    OrbConfig copy = *c;
    copy.name.reset();
    return nameOf(copy);
}

// Detected, enabled sources in source order (no folder and no data today counts as not detected).
QStringList OrbManager::detected() const {
    QStringList keys;
    for (const auto& item : sourceRows_) {
        const auto row = item.toMap();
        if (row.value(QStringLiteral("enabled")).toBool() &&
            (row.value(QStringLiteral("available")).toBool() || row.value(QStringLiteral("today")).toLongLong() > 0)) {
            keys.push_back(row.value(QStringLiteral("source")).toString());
        }
    }
    return ordered(keys);
}

QVariantList OrbManager::sources() const {
    QVariantList out;
    for (const auto& item : sourceRows_) {
        const auto row = item.toMap();
        const QString key = row.value(QStringLiteral("source")).toString();
        const bool enabled = row.value(QStringLiteral("enabled")).toBool();
        const bool found = row.value(QStringLiteral("available")).toBool() || row.value(QStringLiteral("today")).toLongLong() > 0;
        out.push_back(QVariantMap{{QStringLiteral("key"), key},
                                  {QStringLiteral("name"), sourceName(key)},
                                  {QStringLiteral("color"), sourceColor(key)},
                                  {QStringLiteral("status"), !enabled ? strings_.t(QStringLiteral("source.disabled"))
                                                             : !found ? strings_.t(QStringLiteral("source.notFound"))
                                                                      : QString()}});
    }
    return out;
}

int OrbManager::redlineOf(const OrbConfig& c) const {
    const bool total = prefs_.metric() == QLatin1String("total");
    const auto own = total ? c.redlineTotal : c.redlineOutput;
    return own ? std::clamp(*own, 50, 2000) : prefs_.redline();
}

int OrbManager::visibleCount() const {
    return static_cast<int>(std::count_if(store_.orbs().begin(), store_.orbs().end(), [](const OrbConfig& c) { return c.visible; }));
}

bool OrbManager::platesShown(const QString& id) const {
    const auto* c = store_.find(id);
    if (!c || !c->visible || !shown_ || c->size < 80) return false;
    if (c->showName) return *c->showName;
    if (names_ == QLatin1String("always")) return true;
    if (names_ == QLatin1String("never")) return false;
    return visibleCount() >= 2 || !c->tracksAll() || !c->tracksAllHosts();
}

std::vector<OrbSpec> orbSpecs(const OrbsStore& store) {
    std::vector<OrbSpec> out;
    for (const auto& c : store.orbs()) {
        OrbSpec spec{c.id, std::nullopt, std::nullopt};
        if (c.sources) {
            std::vector<agentspeed::SourceKind> kinds;
            for (const auto& key : *c.sources) {
                if (const auto kind = agentspeed::sourceFromId(key.toStdString())) kinds.push_back(*kind);
            }
            spec.sources = kinds;
        }
        if (c.hosts) {
            std::vector<std::string> ids;
            for (const auto& id : *c.hosts) ids.push_back(id.toStdString());
            spec.hosts = ids;
        }
        out.push_back(spec);
    }
    return out;
}

QVariantList OrbManager::list() const {
    QVariantList out;
    for (const auto& c : store_.orbs()) {
        QVariantMap m;
        m.insert(QStringLiteral("id"), c.id);
        m.insert(QStringLiteral("name"), nameOf(c));
        m.insert(QStringLiteral("customName"), c.name.value_or(QString()));
        m.insert(QStringLiteral("autoName"), autoName(c.id));
        m.insert(QStringLiteral("detail"), fill(strings_.t(QStringLiteral("orbs.row.detail")),
                                                {strings_.t(QStringLiteral("style.") + c.style), QString::number(qRound(c.size))}));
        m.insert(QStringLiteral("style"), c.style);
        m.insert(QStringLiteral("size"), c.size);
        m.insert(QStringLiteral("minimumSize"), OrbConfig::kMinSize);
        m.insert(QStringLiteral("opacity"), c.opacity);
        m.insert(QStringLiteral("visible"), c.visible);
        m.insert(QStringLiteral("all"), c.tracksAll());
        m.insert(QStringLiteral("sources"), c.sources.value_or(QStringList()));
        m.insert(QStringLiteral("hosts"), c.hosts ? QVariant(*c.hosts) : QVariant(QStringLiteral("all")));
        QVariantList colors;
        for (const auto& key : c.sources ? ordered(*c.sources) : detected()) colors.push_back(sourceColor(key));
        m.insert(QStringLiteral("colors"), colors);
        m.insert(QStringLiteral("alwaysOnTop"), c.alwaysOnTop);
        m.insert(QStringLiteral("clickThrough"), c.clickThrough);
        m.insert(QStringLiteral("fadeWhenIdle"), c.fadeWhenIdle);
        m.insert(QStringLiteral("traffic"), c.traffic);
        m.insert(QStringLiteral("unit"), c.unit);
        m.insert(QStringLiteral("showName"), c.showName ? QVariant(*c.showName) : QVariant());
        m.insert(QStringLiteral("plate"), platesShown(c.id));
        m.insert(QStringLiteral("titleTheme"), c.titleTheme.value_or(QString()));
        m.insert(QStringLiteral("character"), c.character.value_or(QString()));
        m.insert(QStringLiteral("characterReadouts"), OrbConfig::readoutsToJson(c.characterReadouts).toVariantMap());
        const bool total = prefs_.metric() == QLatin1String("total");
        const auto own = total ? c.redlineTotal : c.redlineOutput;
        m.insert(QStringLiteral("customRedline"), own.has_value());
        m.insert(QStringLiteral("redline"), redlineOf(c));
        const auto it = orbs_.find(c.id);
        m.insert(QStringLiteral("costSymbol"), it == orbs_.end() ? QString() : it->second.values.value(QStringLiteral("costSymbol")).toString());
        out.push_back(m);
    }
    return out;
}

QVariantMap OrbManager::values() const {
    QVariantMap out;
    for (const auto& [id, orb] : orbs_) {
        const auto& v = orb.values;
        out.insert(id, QVariantMap{{QStringLiteral("value"), v.value(QStringLiteral("liveOutput")).toDouble()},
                                   {QStringLiteral("valueText"), v.contains(QStringLiteral("liveText")) ? v.value(QStringLiteral("liveText")).toString()
                                                                                                       : QStringLiteral("0")},
                                   {QStringLiteral("unavailable"), !v.value(QStringLiteral("unavailable")).toString().isEmpty()}});
    }
    return out;
}

// ---- windows

void OrbManager::sync() {
    // Windows follow the store: created, removed and set to their settings.
    std::map<QString, Orb> kept;
    QStringList created;
    for (const auto& c : store_.orbs()) {
        auto it = orbs_.find(c.id);
        Orb orb;
        if (it != orbs_.end()) {
            orb = std::move(it->second);
            orbs_.erase(it);
        } else {
            orb.window = std::make_unique<orb::OrbWindow>(strings_, c.id);
            orb.window->setTitles(titles_);
            orb.window->setCharacters(characters_);
            orb.window->forceDark(forcedDark_);
            orb.window->setEffectsLevel(effectsLevel_);
            orb.plate = std::make_unique<NamePlate>();
            auto* window = orb.window.get();
            auto* plate = orb.plate.get();
            const QString id = c.id;
            // The plate belongs to its orb: hover, drag, menu and double-click.
            connect(plate, &NamePlate::hovered, window, &orb::OrbWindow::plateHover);
            connect(plate, &NamePlate::pressed, this, [this, window, id](QPointF global, Qt::MouseButton button) {
                if (button == Qt::RightButton) showMenu(id, global.toPoint());
                else if (button == Qt::LeftButton) window->beginDrag(global);
            });
            connect(plate, &NamePlate::dragged, window, &orb::OrbWindow::dragTo);
            connect(plate, &NamePlate::released, window, &orb::OrbWindow::endDrag);
            connect(plate, &NamePlate::doubleClicked, this, &OrbManager::dashboardRequested);
            window->setNeighbors([this, id] { return discs(id); });
            connect(window, &orb::OrbWindow::dashboardRequested, this, &OrbManager::dashboardRequested);
            connect(window, &orb::OrbWindow::menuRequested, this, &OrbManager::showMenu);
            connect(window, &orb::OrbWindow::editRequested, this, &OrbManager::set);
            connect(window, &orb::OrbWindow::moved, this, [this](const QString& orbId) {
                savePosition(orbId);
                layoutPlates();
            });
            connect(window, &orb::OrbWindow::hoverChanged, this, [this](const QString& orbId, bool on) {
                // The chip shows after 0.35 s of hover and goes with it.
                if (on) {
                    QTimer::singleShot(350, this, [this, orbId] {
                        const auto o = orbs_.find(orbId);
                        if (o != orbs_.end() && o->second.window->hovered()) layoutPlates();
                    });
                } else {
                    layoutPlates();
                }
            });
            connect(window, &orb::OrbWindow::dragChanged, this, [this](const QString&, bool) { layoutPlates(); });
            connect(window, &orb::OrbWindow::cornerRequested, this, &OrbManager::moveToNextCorner);
            connect(window, &orb::OrbWindow::settingsRequested, this, [this](const QString& orbId) {
                select(orbId);
                emit settingsRequested();
            });
            connect(window, &QWindow::xChanged, this, [this] { plateTimer_.start(0); });
            connect(window, &QWindow::yChanged, this, [this] { plateTimer_.start(0); });
            created.push_back(c.id);
        }
        syncWindow(c, orb);
        kept.emplace(c.id, std::move(orb));
    }
    orbs_ = std::move(kept);
    if (started_) {
        for (const auto& c : store_.orbs()) {
            if (created.contains(c.id)) place(c.id);
            orbs_[c.id].window->setShown(shown_ && c.visible);
        }
    }
    if (store_.indexOf(selected_) < 0) {
        selected_ = store_.orbs().front().id;
        emit selectedChanged();
    }
    const auto next = orbSpecs(store_);
    if (next != appliedSpecs_) {
        appliedSpecs_ = next;
        emit specsChanged();
    }
    layoutPlates();
    pushQuotas();
    emit listChanged();
}

void OrbManager::syncWindow(const OrbConfig& c, Orb& orb) {
    orb.window->apply(c, store_.orbs().size() > 1 || !c.tracksAll() || !c.tracksAllHosts() ? nameOf(c) : QString(), redlineOf(c));
    // a11y.orb.sources: the tracked sources joined with list.separator.
    QStringList names;
    for (const auto& key : c.sources ? ordered(*c.sources) : detected()) names.push_back(sourceName(key));
    orb.window->setAccessibleHint(fill(strings_.t(QStringLiteral("a11y.orb.sources")), {names.join(strings_.t(QStringLiteral("list.separator")))}));
    orb.plate->setClickThrough(c.clickThrough);
}

void OrbManager::applySnapshot(const QVariantMap& snap) {
    const auto rows = snap.value(QStringLiteral("sources")).toList();
    if (rows != sourceRows_) {
        sourceRows_ = rows;
        emit sourcesChanged();
        layoutPlates();
    }
    bool valuesChanged = false;
    for (const auto& item : snap.value(QStringLiteral("orbs")).toList()) {
        const auto map = item.toMap();
        auto it = orbs_.find(map.value(QStringLiteral("id")).toString());
        if (it == orbs_.end()) continue;
        valuesChanged = valuesChanged || it->second.values.value(QStringLiteral("liveText")) != map.value(QStringLiteral("liveText"));
        it->second.values = map;
        if (character_demo::enabled() && it->second.window->style() == QLatin1String("character")) {
            auto demo = map;
            character_demo::apply(demo, static_cast<double>(QDateTime::currentMSecsSinceEpoch()) / 1000, strings_.language());
            it->second.window->applySnapshot(demo);
            continue;
        }
        it->second.window->applySnapshot(map);
    }
    pushQuotas();
    if (valuesChanged) emit this->valuesChanged();
}

void OrbManager::setShown(bool shown) {
    // Turning the master switch on while every orb is hidden shows them all.
    if (shown && visibleCount() == 0) {
        auto all = store_.orbs();
        for (auto& c : all) c.visible = true;
        store_.replaceAll(all);
    }
    if (shown == shown_) return;
    shown_ = shown;
    QSettings().setValue(QStringLiteral("orbsShown"), shown);
    for (const auto& c : store_.orbs()) orbs_[c.id].window->setShown(shown_ && c.visible);
    layoutPlates();
    emit shownChanged();
    emit listChanged();
}

void OrbManager::setNames(const QString& names) {
    if (names == names_) return;
    names_ = names;
    QSettings().setValue(QStringLiteral("orbNames"), names);
    layoutPlates();
    emit namesChanged();
    emit listChanged();
}

void OrbManager::setEffectsLevel(int level) {
    level = std::clamp(level, 0, 3);
    if (level == effectsLevel_) return;
    effectsLevel_ = level;
    QSettings().setValue(QStringLiteral("orb/effects"), level);
    for (auto& [id, orb] : orbs_) orb.window->setEffectsLevel(level);
    emit effectsLevelChanged();
}

void OrbManager::select(const QString& id) {
    if (id == selected_ || store_.indexOf(id) < 0) return;
    selected_ = id;
    emit selectedChanged();
}

void OrbManager::highlight(const QString& id, bool on) {
    for (auto& [orbId, orb] : orbs_) orb.window->setHighlighted(on && orbId == id);
}

// ---- edits

void OrbManager::set(const QString& id, const QString& key, const QVariant& value) {
    const auto* found = store_.find(id);
    if (!found) return;
    OrbConfig c = *found;
    const bool sourcesBefore = c.tracksAll();
    const QString styleBefore = c.style;
    if (key == QLatin1String("style")) {
        c.style = value.toString();
    } else if (key == QLatin1String("size")) {
        c.size = std::round(value.toDouble() / 4) * 4;
    } else if (key == QLatin1String("opacity")) {
        c.opacity = value.toDouble();
    } else if (key == QLatin1String("alwaysOnTop")) {
        c.alwaysOnTop = value.toBool();
    } else if (key == QLatin1String("clickThrough")) {
        c.clickThrough = value.toBool();
    } else if (key == QLatin1String("fadeWhenIdle")) {
        c.fadeWhenIdle = value.toBool();
    } else if (key == QLatin1String("visible")) {
        c.visible = value.toBool();
    } else if (key == QLatin1String("traffic")) {
        c.traffic = value.toString();
    } else if (key == QLatin1String("unit")) {
        c.unit = value.toString() == QLatin1String("cost") ? QStringLiteral("cost") : QStringLiteral("tokens");
    } else if (key == QLatin1String("name")) {
        const QString name = value.toString().trimmed();
        c.name = name.isEmpty() ? std::nullopt : std::optional<QString>(name);
    } else if (key == QLatin1String("showName")) {
        c.showName = value.isValid() && !value.isNull() ? std::optional<bool>(value.toBool()) : std::nullopt;
    } else if (key == QLatin1String("titleTheme")) {
        const QString theme = value.toString();
        c.titleTheme = theme.isEmpty() ? std::nullopt : std::optional<QString>(theme);
    } else if (key == QLatin1String("redline")) {
        // An invalid value restores the General redline of the current metric.
        const bool total = prefs_.metric() == QLatin1String("total");
        const std::optional<int> own = value.isValid() && !value.isNull() ? std::optional<int>(value.toInt() / 50 * 50) : std::nullopt;
        (total ? c.redlineTotal : c.redlineOutput) = own;
    } else if (key == QLatin1String("allSources")) {
        if (value.toBool()) c.sources.reset();
        else c.sources = detected().isEmpty() ? QStringList{sourceOrder().front()} : QStringList{detected().front()};
    } else {
        return;
    }
    store_.update(c);
    if (sourcesBefore != c.tracksAll()) showChip(id, 2);
    // Switching to `character` downloads every character, this orb's live vendors first (CHARACTERS 2).
    if (characters_ && key == QLatin1String("style") && c.style == QLatin1String("character") && styleBefore != c.style) {
        const auto it = orbs_.find(id);
        characters_->downloadAll(it == orbs_.end() ? QStringList() : it->second.values.value(QStringLiteral("vendorOrder")).toStringList());
    }
}

void OrbManager::setCharacter(const QString& id, const QString& character) {
    const auto* found = store_.find(id);
    if (!found) return;
    OrbConfig c = *found;
    c.character = OrbConfig::cleanCharacter(character);
    if (c.character == found->character) return;
    store_.update(c);
}

void OrbManager::setCharacterReadout(const QString& id, const QString& key, bool on) {
    const auto* found = store_.find(id);
    if (!found) return;
    OrbConfig c = *found;
    for (const auto readout : agentspeed::character::kCharacterReadouts) {
        if (key == QLatin1String(agentspeed::character::key(readout))) c.characterReadouts.set(readout, on);
    }
    if (c.characterReadouts == found->characterReadouts) return;
    store_.update(c);
}

QVariantList OrbManager::characterReadoutItems() const { return OrbConfig::readoutItems(); }

void OrbManager::setCharacters(CharacterLibrary* characters) {
    characters_ = characters;
    for (auto& [id, orb] : orbs_) orb.window->setCharacters(characters_);
    if (characters_) connect(characters_, &CharacterLibrary::installedChanged, this, [this] { sync(); });
}

bool OrbManager::anyCharacterOrb() const {
    return std::any_of(store_.orbs().begin(), store_.orbs().end(), [](const OrbConfig& c) { return c.style == QLatin1String("character"); });
}

void OrbManager::resumeCharacterDownloads() {
    if (characters_ && anyCharacterOrb()) characters_->downloadAll({});
}

// Checking a source on an all-sources orb makes the set that single source; the last one cannot be unchecked.
void OrbManager::setSource(const QString& id, const QString& source, bool on) {
    const auto* found = store_.find(id);
    if (!found) return;
    OrbConfig c = *found;
    QStringList keys = c.sources.value_or(QStringList());
    if (on) {
        if (!c.sources) keys.clear();
        if (!keys.contains(source)) keys.push_back(source);
    } else {
        if (!c.sources || keys.size() <= 1) return;
        keys.removeAll(source);
    }
    c.sources = ordered(keys);
    store_.update(c);
    showChip(id, 2);
}

void OrbManager::setHosts(const QString& id, const QVariant& hosts) {
    const auto* found = store_.find(id);
    if (!found) return;
    OrbConfig c = *found;
    const auto next = OrbConfig::hostsFromJson(QJsonValue::fromVariant(hosts));
    if (next == c.hosts) return;
    c.hosts = next;
    store_.update(c);
    showChip(id, 2);
}

// Add, Duplicate and Split copy the look and the toggles; a new orb's sources are the first detected source no orb
// tracks explicitly (else all sources).
OrbConfig OrbManager::childOf(const OrbConfig& from) const { return OrbConfig::childOf(from, OrbsStore::newId()); }

void OrbManager::add(const QString& from) {
    const auto* source = store_.find(from.isEmpty() ? selected_ : from);
    if (!source || count() >= OrbsStore::kMaxOrbs) return;
    OrbConfig c = childOf(*source);
    QStringList tracked;
    for (const auto& o : store_.orbs()) {
        if (o.sources) tracked += *o.sources;
    }
    for (const auto& key : detected()) {
        if (!tracked.contains(key)) {
            c.sources = QStringList{key};
            break;
        }
    }
    const QString origin = source->id;
    const QPointF from_ = orbs_.count(origin) ? orbs_[origin].window->discCenter() : defaultCenter(c.footprintDiameter());
    const double fromRadius = source->footprintDiameter() / 2;
    store_.setPosition(arrangement(), c.id, recordOf(freeSpot(from_, fromRadius, c.footprintDiameter() / 2, QString()), c.footprintDiameter()));
    if (store_.insert(c, origin)) {
        select(c.id);
        showChip(c.id, 2);
    }
}

void OrbManager::duplicate(const QString& id) {
    const auto* source = store_.find(id);
    if (!source || count() >= OrbsStore::kMaxOrbs) return;
    OrbConfig c = *source;
    c.id = OrbsStore::newId();
    c.extra = {};
    if (c.name) c.name = *c.name + QStringLiteral(" 2");
    const QPointF from = orbs_.count(id) ? orbs_[id].window->discCenter() : defaultCenter(c.footprintDiameter());
    store_.setPosition(arrangement(), c.id, recordOf(freeSpot(from, source->footprintDiameter() / 2, c.footprintDiameter() / 2, QString()), c.footprintDiameter()));
    if (store_.insert(c, id)) select(c.id);
}

void OrbManager::remove(const QString& id) {
    const auto* c = store_.find(id);
    if (!c || count() <= 1) return;
    const QString name = nameOf(*c);
    store_.keepUndo();
    store_.remove(id);
    showBanner(fill(strings_.t(QStringLiteral("orbs.didRemove")), {name}));
}

// One orb per key from `original` into `all` at `at`: the first keeps its id and position, the others form a row toward
// the screen interior (a column when docked to a side edge) at 12 pt gaps. Returns the orbs made.
std::vector<OrbConfig> OrbManager::splitInto(std::vector<OrbConfig>& all, int at, const OrbConfig& original, const QStringList& keys) {
    OrbConfig first = original;
    first.sources = QStringList{keys[0]};
    first.name.reset();
    all[static_cast<std::size_t>(at)] = first;
    const QPointF start = orbs_.count(original.id) ? orbs_[original.id].window->discCenter() : defaultCenter(original.footprintDiameter());
    QScreen* screen = QGuiApplication::screenAt(start.toPoint());
    if (!screen) screen = QGuiApplication::primaryScreen();
    const QRectF avail = screen->availableGeometry();
    const double r = original.footprintDiameter() / 2;
    const bool column = start.x() - r - avail.left() <= kSnapMargin + 2 || avail.right() + 1 - start.x() - r <= kSnapMargin + 2;
    const QPointF step = column ? QPointF(0, start.y() < avail.center().y() ? 1 : -1) * (2 * r + kGap)
                                : QPointF(start.x() < avail.center().x() ? 1 : -1, 0) * (2 * r + kGap);
    QPointF previous = start;
    std::vector<OrbConfig> children;
    for (int i = 1; i < keys.size(); ++i) {
        OrbConfig c = childOf(original);
        c.sources = QStringList{keys[i]};
        // DESIGN 13: a split keeps the orb's hosts.
        c.hosts = original.hosts;
        QPointF next = previous + step;
        if (!avail.adjusted(r, r, -r, -r).contains(next)) next = freeSpot(previous, r, r, original.id);
        store_.setPosition(arrangement(), c.id, recordOf(next, c.footprintDiameter()));
        previous = next;
        children.push_back(c);
    }
    all.insert(all.begin() + at + 1, children.begin(), children.end());
    return children;
}

// Split this orb: one orb per available source of its set (every detected source for an all-sources orb).
void OrbManager::split(const QString& id) {
    const auto* found = store_.find(id);
    if (!found) return;
    const OrbConfig original = *found;
    QStringList keys = detected();
    if (original.sources) {
        QStringList available;
        for (const auto& key : ordered(*original.sources)) {
            if (keys.contains(key)) available.push_back(key);
        }
        keys = available;
    }
    if (keys.size() < 2) return;
    store_.keepUndo();
    auto all = store_.orbs();
    const int room = OrbsStore::kMaxOrbs - static_cast<int>(all.size()) + 1;
    const bool limited = keys.size() > room;
    keys = keys.mid(0, room);
    const auto children = splitInto(all, store_.indexOf(id), original, keys);
    store_.replaceAll(all);
    for (const auto& c : children) showChip(c.id, 2);
    showChip(id, 2);
    showBanner(fill(strings_.t(limited ? QStringLiteral("orbs.didSplitLimited") : QStringLiteral("orbs.didSplit")), {QString::number(keys.size())}));
}

// Split by source (Settings): replaces every orb by one per detected source, styled like the selected orb, laid out
// from the first orb's position.
void OrbManager::splitAll() {
    QStringList keys = detected();
    if (keys.size() < 2) return;
    const auto* selected = store_.find(selected_);
    OrbConfig base = selected ? *selected : store_.orbs().front();
    base.id = store_.orbs().front().id;
    store_.keepUndo();
    const bool limited = keys.size() > OrbsStore::kMaxOrbs;
    keys = keys.mid(0, OrbsStore::kMaxOrbs);
    std::vector<OrbConfig> all{base};
    splitInto(all, 0, base, keys);
    store_.replaceAll(all);
    for (const auto& c : all) showChip(c.id, 2);
    showBanner(fill(strings_.t(limited ? QStringLiteral("orbs.didSplitLimited") : QStringLiteral("orbs.didSplit")), {QString::number(keys.size())}));
}

// Merge: one all-sources orb keeping the target's id, style, size, toggles and position.
void OrbManager::merge(const QString& target) {
    const auto* found = store_.find(target.isEmpty() ? selected_ : target);
    if (!found || count() < 2) return;
    store_.keepUndo();
    OrbConfig c = *found;
    c.sources.reset();
    c.name.reset();
    store_.replaceAll({c});
    showChip(c.id, 2);
    showBanner(strings_.t(QStringLiteral("orbs.didMerge")));
}

void OrbManager::undo() {
    bannerTimer_.stop();
    banner_.clear();
    emit bannerChanged();
    if (store_.undo()) placeAll();
}

void OrbManager::move(int from, int to) { store_.move(from, to); }

void OrbManager::showBanner(const QString& text) {
    banner_ = text;
    bannerTimer_.start(8000);
    emit bannerChanged();
}

void OrbManager::showChip(const QString& id, double seconds) {
    auto it = orbs_.find(id);
    if (it == orbs_.end()) return;
    it->second.chipUntil = static_cast<double>(QDateTime::currentMSecsSinceEpoch()) / 1000 + seconds;
    layoutPlates();
    QTimer::singleShot(static_cast<int>(seconds * 1000) + 50, this, &OrbManager::layoutPlates);
}

// ---- positions

QString OrbManager::arrangement() const {
    QStringList keys;
    for (auto* screen : QGuiApplication::screens()) keys.push_back(screen->name());
    keys.sort();
    return keys.join(QLatin1Char('+'));
}

QPointF OrbManager::defaultCenter(double size) const {
    const QRectF avail = QGuiApplication::primaryScreen()->availableGeometry();
    return {avail.right() + 1 - kSnapMargin - size / 2, avail.top() + avail.height() * 0.32};
}

// A record per axis: the distance from an edge the disc is docked to, else the fraction of the usable area.
OrbPosition OrbManager::recordOf(QPointF c, double size) const {
    QScreen* screen = QGuiApplication::screenAt(c.toPoint());
    if (!screen) screen = QGuiApplication::primaryScreen();
    const QRectF avail = screen->availableGeometry();
    const double r = size / 2;
    OrbPosition p;
    p.display = screen->name();
    const double left = c.x() - r - avail.left(), right = avail.right() + 1 - c.x() - r;
    const double top = c.y() - r - avail.top(), bottom = avail.bottom() + 1 - c.y() - r;
    if (left <= kSnapMargin + 2) p.xAnchor = QStringLiteral("low"), p.x = left;
    else if (right <= kSnapMargin + 2) p.xAnchor = QStringLiteral("high"), p.x = right;
    else p.xAnchor = QStringLiteral("fraction"), p.x = (c.x() - avail.left()) / avail.width();
    if (top <= kSnapMargin + 2) p.yAnchor = QStringLiteral("low"), p.y = top;
    else if (bottom <= kSnapMargin + 2) p.yAnchor = QStringLiteral("high"), p.y = bottom;
    else p.yAnchor = QStringLiteral("fraction"), p.y = (c.y() - avail.top()) / avail.height();
    return p;
}

QPointF OrbManager::centerOf(const OrbPosition& p, double size) const {
    QScreen* screen = QGuiApplication::primaryScreen();
    for (auto* s : QGuiApplication::screens()) {
        if (s->name() == p.display) screen = s;
    }
    const QRectF avail = screen->availableGeometry();
    const double r = size / 2;
    const auto axis = [r](const QString& anchor, double v, double lo, double hi) {
        if (anchor == QLatin1String("low")) return lo + v + r;
        if (anchor == QLatin1String("high")) return hi - v - r;
        return lo + v * (hi - lo);
    };
    QPointF c(axis(p.xAnchor, p.x, avail.left(), avail.right() + 1), axis(p.yAnchor, p.y, avail.top(), avail.bottom() + 1));
    // Never off-screen.
    c.setX(std::clamp(c.x(), avail.left() + r, std::max(avail.left() + r, avail.right() + 1 - r)));
    c.setY(std::clamp(c.y(), avail.top() + r, std::max(avail.top() + r, avail.bottom() + 1 - r)));
    return c;
}

void OrbManager::savePosition(const QString& id) {
    const auto it = orbs_.find(id);
    const auto* c = store_.find(id);
    if (it == orbs_.end() || !c) return;
    store_.setPosition(arrangement(), id, recordOf(it->second.window->discCenter(), c->footprintDiameter()));
}

// An orb's place in this arrangement; else its record from the last arrangement when its display is still connected
// (else the main display keeping its anchors); else the single orb's old place or the default corner.
void OrbManager::place(const QString& id) {
    const auto* c = store_.find(id);
    auto it = orbs_.find(id);
    if (!c || it == orbs_.end()) return;
    QPointF center;
    if (auto p = store_.position(arrangement(), id)) {
        center = centerOf(*p, c->footprintDiameter());
    } else if (auto last = store_.lastPosition(id, arrangement())) {
        center = centerOf(*last, c->footprintDiameter());
    } else if (store_.indexOf(id) == 0 && store_.legacyCenter() && QGuiApplication::screenAt(store_.legacyCenter()->toPoint())) {
        center = *store_.legacyCenter();
    } else {
        center = store_.indexOf(id) == 0 ? defaultCenter(c->footprintDiameter()) : freeSpot(defaultCenter(c->footprintDiameter()), c->footprintDiameter() / 2, c->footprintDiameter() / 2, id);
    }
    it->second.window->placeAt(center);
    store_.setPosition(arrangement(), id, recordOf(center, c->footprintDiameter()));
}

// Every orb from the layout of the current arrangement; overlaps are resolved in list order.
void OrbManager::placeAll() {
    if (!started_) return;
    for (const auto& c : store_.orbs()) place(c.id);
    for (const auto& c : store_.orbs()) {
        auto& window = orbs_[c.id].window;
        const QPointF center = window->discCenter();
        if (!fits(center, c.footprintDiameter() / 2, c.id)) {
            const QPointF free = freeSpot(center, c.footprintDiameter() / 2, c.footprintDiameter() / 2, c.id);
            window->placeAt(free);
            store_.setPosition(arrangement(), c.id, recordOf(free, c.footprintDiameter()));
        }
        window->setShown(shown_ && c.visible);
    }
    layoutPlates();
}

std::vector<orb::Disc> OrbManager::discs(const QString& except) const {
    std::vector<orb::Disc> out;
    for (const auto& c : store_.orbs()) {
        if (c.id == except) continue;
        const auto it = orbs_.find(c.id);
        if (it == orbs_.end() || !it->second.window->isVisible()) continue;
        out.push_back({it->second.window->discCenter(), c.footprintDiameter() / 2});
    }
    return out;
}

bool OrbManager::fits(QPointF center, double radius, const QString& except) const {
    QScreen* screen = QGuiApplication::screenAt(center.toPoint());
    if (!screen) return false;
    const QRectF avail = screen->availableGeometry();
    if (!avail.adjusted(radius, radius, -radius, -radius).contains(center)) return false;
    for (const auto& d : discs(except)) {
        if (std::hypot(center.x() - d.center.x(), center.y() - d.center.y()) < radius + d.radius + kGap - 0.5) return false;
    }
    return true;
}

// DESIGN 2b Placement: the four touching slots of the orb it came from (left, below, right, above), then the nearest
// free spot of a 24 pt grid around it, else a +24/+24 cascade clamped inside the usable area.
QPointF OrbManager::freeSpot(QPointF from, double fromRadius, double radius, const QString& except) const {
    const double reach = fromRadius + radius + kGap;
    for (const QPointF offset : {QPointF(-reach, 0), QPointF(0, reach), QPointF(reach, 0), QPointF(0, -reach)}) {
        if (fits(from + offset, radius, except)) return from + offset;
    }
    for (int ring = 1; ring <= 40; ++ring) {
        std::optional<QPointF> best;
        double bestDistance = 1e9;
        for (int dx = -ring; dx <= ring; ++dx) {
            for (int dy = -ring; dy <= ring; ++dy) {
                if (std::max(std::abs(dx), std::abs(dy)) != ring) continue;
                const QPointF p = from + QPointF(dx * 24, dy * 24);
                const double distance = std::hypot(dx, dy);
                if (distance < bestDistance && fits(p, radius, except)) best = p, bestDistance = distance;
            }
        }
        if (best) return *best;
    }
    QScreen* screen = QGuiApplication::screenAt(from.toPoint());
    if (!screen) screen = QGuiApplication::primaryScreen();
    const QRectF avail = screen->availableGeometry();
    return {std::clamp(from.x() + 24, avail.left() + radius, avail.right() + 1 - radius),
            std::clamp(from.y() + 24, avail.top() + radius, avail.bottom() + 1 - radius)};
}

void OrbManager::resetPosition(const QString& id) {
    const auto* c = store_.find(id);
    auto it = orbs_.find(id);
    if (!c || it == orbs_.end()) return;
    QPointF center = defaultCenter(c->footprintDiameter());
    if (!fits(center, c->footprintDiameter() / 2, id)) center = freeSpot(center, c->footprintDiameter() / 2, c->footprintDiameter() / 2, id);
    it->second.window->placeAt(center);
    savePosition(id);
    layoutPlates();
}

void OrbManager::moveToNextCorner(const QString& id) {
    const auto* c = store_.find(id);
    auto it = orbs_.find(id);
    if (!c || it == orbs_.end()) return;
    const QPointF center = it->second.window->discCenter();
    QScreen* screen = QGuiApplication::screenAt(center.toPoint());
    if (!screen) screen = QGuiApplication::primaryScreen();
    const QRectF avail = screen->availableGeometry();
    const double r = c->footprintDiameter() / 2, m = kSnapMargin + r;
    const QPointF corners[] = {{avail.right() + 1 - m, avail.top() + m}, {avail.right() + 1 - m, avail.bottom() + 1 - m},
                               {avail.left() + m, avail.bottom() + 1 - m}, {avail.left() + m, avail.top() + m}};
    int nearest = 0;
    for (int i = 1; i < 4; ++i) {
        if (std::hypot(center.x() - corners[i].x(), center.y() - corners[i].y()) < std::hypot(center.x() - corners[nearest].x(), center.y() - corners[nearest].y())) nearest = i;
    }
    for (int step = 1; step <= 4; ++step) {
        QPointF target = corners[(nearest + step) % 4];
        if (!fits(target, r, id)) target = freeSpot(target, r, r, id);
        if (fits(target, r, id) || step == 4) {
            it->second.window->placeAt(target);
            savePosition(id);
            layoutPlates();
            return;
        }
    }
}

// ---- name plates and chips

// DESIGN 2b Name plate placement: below the disc, else above, else beside it (toward the screen center first), the
// first slot that stays 3 pt inside the usable area and 4 pt from every other disc and plate; the chip widens it.
void OrbManager::layoutPlates() {
    const double now = static_cast<double>(QDateTime::currentMSecsSinceEpoch()) / 1000;
    const bool isDark = forcedDark_ || Appearance::instance().dark();
    std::vector<QRectF> taken;
    for (const auto& d : discs(QString())) taken.push_back(QRectF(d.center.x() - d.radius, d.center.y() - d.radius, 2 * d.radius, 2 * d.radius));
    for (const auto& c : store_.orbs()) {
        auto it = orbs_.find(c.id);
        if (it == orbs_.end()) continue;
        auto& orb = it->second;
        auto* window = orb.window.get();
        const bool visible = window->isVisible();
        const bool plate = platesShown(c.id);
        const bool chipWanted = visible && (window->hovered() || window->dragging() || now < orb.chipUntil);
        const bool chip = chipWanted && (count() > 1 || !c.tracksAll() || !c.tracksAllHosts());
        if (!visible || (!plate && !chip)) {
            orb.plate->disappear();
            continue;
        }
        NamePlate::Look look;
        look.dark = isDark;
        look.diameter = c.footprintDiameter();
        const QStringList keys = c.sources ? ordered(*c.sources) : QStringList();
        QStringList unavailable;
        for (const auto& item : orb.values.value(QStringLiteral("sourceStates")).toList()) {
            const auto row = item.toMap();
            if (!row.value(QStringLiteral("status")).toString().isEmpty()) unavailable.push_back(row.value(QStringLiteral("source")).toString());
        }
        if (c.sources) {
            for (const auto& key : keys) look.dots.push_back({sourceColor(key), !unavailable.contains(key)});
        } else {
            for (const auto& key : detected()) look.spectrum.push_back(sourceColor(key));
            if (look.spectrum.empty()) look.spectrum.push_back(sourceColor(QString()));
        }
        const QString name = nameOf(c);
        if (chip && c.sources) {
            // The chip: every source with its status.
            QStringList parts;
            QStringList statuses;
            for (const auto& key : keys) {
                parts.push_back(sourceName(key));
                for (const auto& item : orb.values.value(QStringLiteral("sourceStates")).toList()) {
                    const auto row = item.toMap();
                    const QString status = row.value(QStringLiteral("status")).toString();
                    if (row.value(QStringLiteral("source")).toString() == key && !status.isEmpty()) {
                        statuses.push_back(strings_.t(status == QLatin1String("hostsOffline") ? QStringLiteral("remote.orb.offline")
                                                                                              : QStringLiteral("source.") + status));
                    }
                }
            }
            look.forms = {c.name ? *c.name : parts.join(QStringLiteral(" · "))};
            if (!statuses.isEmpty()) look.status = statuses.front();
        } else if (!c.name && keys.size() >= 2) {
            // Shortening: A + B → A +1 → A… +1; A +n → A… +n.
            const QString more = fill(strings_.t(QStringLiteral("orbs.name.more")), {sourceName(keys[0]), QString::number(keys.size() - 1)});
            look.forms = keys.size() == 2 ? QStringList{name, more} : QStringList{more};
            look.tail = more.mid(sourceName(keys[0]).size());
        } else {
            look.forms = {name};
        }
        // Width budget: 1.5 S; with another disc in the horizontal band, to the point dividing the centers by size, less 3 pt.
        const QPointF center = window->discCenter();
        const double r = c.footprintDiameter() / 2 * window->hoverScale();
        double budget = 1.5 * c.footprintDiameter();
        for (const auto& d : discs(c.id)) {
            if (std::abs(d.center.y() - center.y()) >= (c.footprintDiameter() / 2 + d.radius)) continue;
            const double distance = std::abs(d.center.x() - center.x());
            budget = std::min(budget, 2 * (distance * c.footprintDiameter() / (c.footprintDiameter() + 2 * d.radius) - 3));
        }
        look.budget = std::max(budget, 40.0);
        orb.plate->setLook(look);
        const QSizeF size = orb.plate->capsule();
        const double g = NamePlate::gap(c.footprintDiameter());
        QScreen* screen = QGuiApplication::screenAt(center.toPoint());
        if (!screen) screen = QGuiApplication::primaryScreen();
        const QRectF avail = screen->availableGeometry().adjusted(3, 3, -3, -3);
        const bool towardRight = center.x() < avail.center().x();
        std::vector<QPointF> places{QPointF(center.x() - size.width() / 2, center.y() + r + g),
                                   QPointF(center.x() - size.width() / 2, center.y() - r - g - size.height())};
        const QPointF right(center.x() + r + g, center.y() - size.height() / 2), left(center.x() - r - g - size.width(), center.y() - size.height() / 2);
        places.push_back(towardRight ? right : left);
        places.push_back(towardRight ? left : right);
        std::optional<QPointF> chosen;
        double anchor = 0.5;
        for (std::size_t i = 0; i < places.size(); ++i) {
            const auto& place = places[i];
            const QRectF rect(place, size);
            if (!avail.contains(rect)) continue;
            bool clear = true;
            for (const auto& other : taken) {
                const bool own = std::abs(other.center().x() - center.x()) < 0.5 && std::abs(other.center().y() - center.y()) < 0.5;
                if (!own && rect.adjusted(-4, -4, 4, 4).intersects(other)) clear = false;
            }
            if (clear) {
                chosen = place;
                // Side plates grow away from the disc; plates above or below grow about their center.
                anchor = i < 2 ? 0.5 : (place == right ? 0.0 : 1.0);
                break;
            }
        }
        if (!chosen) {
            orb.plate->disappear();
            continue;
        }
        taken.push_back(QRectF(*chosen, size));
        orb.plate->placeCapsule(*chosen, anchor);
        orb.plate->appear();
    }
}

// ---- context menu

// DESIGN 2b Context menu: header, style, sources, unit, traffic, name plate, size, effects, window toggles, add,
// duplicate, split, merge, hide, remove, settings, dashboard, quit.
void OrbManager::showMenu(const QString& id, QPoint global) {
    if (auto* menu = buildMenu(id)) menu->popup(global);
}

QList<std::pair<QString, QImage>> OrbManager::menuPictures(const QString& id) {
    QList<std::pair<QString, QImage>> out;
    auto* menu = buildMenu(id);
    if (!menu) return out;
    out.push_back({QStringLiteral("menu"), menu->grab().toImage()});
    for (auto* sub : menu->findChildren<QMenu*>()) {
        if (sub->title() == strings_.t(QStringLiteral("remote.hosts"))) out.push_back({QStringLiteral("hosts"), sub->grab().toImage()});
        if (sub->title() == strings_.t(QStringLiteral("characters.title"))) {
            sub->adjustSize();
            out.push_back({QStringLiteral("characters"), sub->grab().toImage()});
        }
        if (sub->title() == strings_.t(QStringLiteral("character.readouts"))) {
            sub->adjustSize();
            out.push_back({QStringLiteral("readouts"), sub->grab().toImage()});
        }
        if (sub->title() == strings_.t(QStringLiteral("menu.size"))) {
            sub->adjustSize();
            out.push_back({QStringLiteral("size"), sub->grab().toImage()});
        }
        if (sub->title() == strings_.t(QStringLiteral("orbs.style"))) {
            sub->adjustSize();
            out.push_back({QStringLiteral("style"), sub->grab().toImage()});
        }
    }
    menu->deleteLater();
    return out;
}

QMenu* OrbManager::buildMenu(const QString& id) {
    const auto* found = store_.find(id);
    if (!found) return nullptr;
    const OrbConfig c = *found;
    auto* menu = new QMenu();
    menu->setAttribute(Qt::WA_DeleteOnClose);
    menu->addAction(nameOf(c))->setEnabled(false);
    menu->addSeparator();
    const auto radio = [&](QMenu* parent, const QString& label, bool checked, auto&& slot) {
        auto* action = parent->addAction(label);
        action->setCheckable(true);
        action->setChecked(checked);
        connect(action, &QAction::triggered, this, slot);
        return action;
    };
    auto* styles = menu->addMenu(strings_.t(QStringLiteral("orbs.style")));
    for (const auto kind : orb::kStyles) {
        const QString key = orb::styleKey(kind);
        radio(styles, strings_.t(QStringLiteral("style.") + key), c.style == key, [this, id, key] { set(id, QStringLiteral("style"), key); });
    }
    const bool character = c.style == QLatin1String("character");
    if (character) addCharacterMenu(menu, id);
    auto* sources = menu->addMenu(strings_.t(QStringLiteral("orbs.sources")));
    radio(sources, strings_.t(QStringLiteral("orbs.sources.all")), c.tracksAll(), [this, id] { set(id, QStringLiteral("allSources"), true); });
    sources->addSeparator();
    for (const auto& item : this->sources()) {
        const auto row = item.toMap();
        const QString key = row.value(QStringLiteral("key")).toString();
        const QString status = row.value(QStringLiteral("status")).toString();
        const bool tracked = c.sources && c.sources->contains(key);
        auto* action = sources->addAction(status.isEmpty() ? row.value(QStringLiteral("name")).toString()
                                                           : row.value(QStringLiteral("name")).toString() + QStringLiteral(" — ") + status);
        QPixmap dot(10, 10);
        dot.fill(Qt::transparent);
        {
            QPainter p(&dot);
            p.setRenderHint(QPainter::Antialiasing);
            p.setPen(Qt::NoPen);
            p.setBrush(sourceColor(key));
            p.drawEllipse(QRectF(1, 1, 8, 8));
        }
        action->setIcon(QIcon(dot));
        action->setCheckable(true);
        action->setChecked(tracked);
        if (tracked && c.sources->size() <= 1) {
            action->setEnabled(false);
            action->setToolTip(strings_.t(QStringLiteral("orbs.sources.lastOne")));
        }
        connect(action, &QAction::triggered, this, [this, id, key](bool on) { setSource(id, key, on); });
    }
    addHostsMenu(menu, c);
    if (character) addReadoutsMenu(menu, id);
    auto* unit = character ? new QMenu(menu) : menu->addMenu(strings_.t(QStringLiteral("orbs.unit")));
    radio(unit, strings_.t(QStringLiteral("orbs.unit.tokens")), c.unit != QLatin1String("cost"), [this, id] { set(id, QStringLiteral("unit"), QStringLiteral("tokens")); });
    const QString symbol = orbs_.count(id) ? orbs_[id].values.value(QStringLiteral("costSymbol")).toString() : QString();
    radio(unit, fill(strings_.t(QStringLiteral("orbs.unit.cost")), {symbol.isEmpty() ? QStringLiteral("US$") : symbol}), c.unit == QLatin1String("cost"),
          [this, id] { set(id, QStringLiteral("unit"), QStringLiteral("cost")); });
    auto* traffic = character ? new QMenu(menu) : menu->addMenu(strings_.t(QStringLiteral("orbs.traffic")));
    for (const char* mode : {"sources", "machine", "intelligence", "off"}) {
        const QString value = QString::fromLatin1(mode);
        radio(traffic, strings_.t(QStringLiteral("orbs.traffic.") + value), c.traffic == value, [this, id, value] { set(id, QStringLiteral("traffic"), value); });
    }
    const bool plate = platesShown(id);
    auto* showName = menu->addAction(strings_.t(QStringLiteral("orbs.showName")));
    showName->setCheckable(true);
    showName->setChecked(plate);
    connect(showName, &QAction::triggered, this, [this, id, plate] { set(id, QStringLiteral("showName"), !plate); });

    // The presets, a separator and the slider over the whole range; the presets' check follows the slider.
    auto* size = menu->addMenu(strings_.t(QStringLiteral("menu.size")));
    const char* sizeKeys[] = {"menu.size.small", "menu.size.medium", "menu.size.large"};
    QList<QAction*> presets;
    for (int i = 0; i < 3; ++i) {
        const double d = kPresets[i];
        presets.push_back(radio(size, strings_.t(QString::fromLatin1(sizeKeys[i])), std::abs(c.size - d) < 0.5,
                                [this, id, d] { set(id, QStringLiteral("size"), d); }));
    }
    size->addSeparator();
    auto* slider = new SizeSliderAction(c.size, strings_.t(QStringLiteral("settings.orb.size")), size);
    connect(slider, &SizeSliderAction::sizeChanged, this, [this, id, presets](double d) {
        set(id, QStringLiteral("size"), d);
        for (int i = 0; i < 3; ++i) presets[i]->setChecked(std::abs(d - kPresets[i]) < 0.5);
    });
    size->addAction(slider);
    auto* fx = menu->addMenu(strings_.t(QStringLiteral("menu.effects")));
    const char* levelKeys[] = {"effects.off", "effects.soft", "effects.standard", "effects.max"};
    for (int i = 0; i < 4; ++i) radio(fx, strings_.t(QString::fromLatin1(levelKeys[i])), effectsLevel_ == i, [this, i] { setEffectsLevel(i); });
    menu->addSeparator();
    auto* top = menu->addAction(strings_.t(QStringLiteral("menu.alwaysOnTop")));
    top->setCheckable(true);
    top->setChecked(c.alwaysOnTop);
    connect(top, &QAction::toggled, this, [this, id](bool on) { set(id, QStringLiteral("alwaysOnTop"), on); });
    auto* through = menu->addAction(strings_.t(QStringLiteral("menu.clickThrough")));
    through->setCheckable(true);
    through->setChecked(c.clickThrough);
    connect(through, &QAction::toggled, this, [this, id](bool on) { set(id, QStringLiteral("clickThrough"), on); });

    menu->addSeparator();
    const bool full = count() >= OrbsStore::kMaxOrbs;
    auto* addAction = menu->addAction(strings_.t(QStringLiteral("orbs.add")), this, [this, id] { add(id); });
    auto* duplicateAction = menu->addAction(strings_.t(QStringLiteral("menu.orb.duplicate")), this, [this, id] { duplicate(id); });
    for (auto* action : {addAction, duplicateAction}) {
        action->setEnabled(!full);
        if (full) action->setToolTip(fill(strings_.t(QStringLiteral("orbs.limit")), {QString::number(OrbsStore::kMaxOrbs)}));
    }
    int available = 0;
    for (const auto& key : detected()) {
        if (!c.sources || c.sources->contains(key)) ++available;
    }
    menu->addAction(strings_.t(QStringLiteral("menu.orb.split")), this, [this, id] { split(id); })->setEnabled(available >= 2 && !full);
    menu->addAction(strings_.t(QStringLiteral("menu.orb.mergeAll")), this, [this, id] { merge(id); })->setEnabled(count() >= 2);
    menu->addSeparator();
    if (visibleCount() <= 1) {
        menu->addAction(strings_.t(QStringLiteral("menu.hideOrb")), this, [this] { setShown(false); });
    } else {
        menu->addAction(strings_.t(QStringLiteral("menu.orb.hide")), this, [this, id] { set(id, QStringLiteral("visible"), false); });
    }
    if (count() >= 2) {
        menu->addAction(strings_.t(QStringLiteral("menu.orb.remove")), this, [this, id] {
            const auto* target = store_.find(id);
            if (!target) return;
            QMessageBox box(QMessageBox::Warning, strings_.t(QStringLiteral("app.name")),
                            fill(strings_.t(QStringLiteral("orbs.remove.confirm")), {nameOf(*target)}));
            box.setInformativeText(strings_.t(QStringLiteral("orbs.remove.detail")));
            auto* removeButton = box.addButton(strings_.t(QStringLiteral("orbs.remove.button")), QMessageBox::DestructiveRole);
            box.addButton(QMessageBox::Cancel);
            box.exec();
            if (box.clickedButton() == static_cast<QAbstractButton*>(removeButton)) remove(id);
        });
    }
    menu->addSeparator();
    menu->addAction(strings_.t(QStringLiteral("menu.orb.settings")), this, [this, id] {
        select(id);
        emit settingsRequested();
    });
    menu->addAction(strings_.t(QStringLiteral("menu.dashboard")), this, &OrbManager::dashboardRequested);
    menu->addAction(strings_.t(QStringLiteral("menu.quit")), this, &OrbManager::quitRequested);
    return menu;
}

void OrbManager::addCharacterMenu(QMenu* menu, const QString& id) {
    const auto* c = store_.find(id);
    if (!c) return;
    auto* sub = menu->addMenu(strings_.t(QStringLiteral("characters.title")));
    auto* group = new QActionGroup(sub);
    auto* automatic = sub->addAction(strings_.t(QStringLiteral("characters.auto")));
    automatic->setCheckable(true);
    automatic->setChecked(!c->character);
    group->addAction(automatic);
    connect(automatic, &QAction::triggered, this, [this, id] { setCharacter(id, QString()); });
    if (characters_ && characters_->hasInstalled()) {
        sub->addSeparator();
        for (const auto& pack : characters_->packs()) {
            const QString packID = QString::fromStdString(pack.id);
            auto* action = sub->addAction(characters_->displayName(packID));
            QPixmap dot(10, 10);
            dot.fill(Qt::transparent);
            {
                QPainter p(&dot);
                p.setRenderHint(QPainter::Antialiasing);
                p.setPen(Qt::NoPen);
                p.setBrush(QColor(characters_->accent(packID)));
                p.drawEllipse(QRectF(1, 1, 8, 8));
            }
            action->setIcon(QIcon(dot));
            action->setCheckable(true);
            action->setChecked(c->character == packID);
            group->addAction(action);
            connect(action, &QAction::triggered, this, [this, id, packID] { setCharacter(id, packID); });
        }
    }
    sub->addSeparator();
    // Downloads live in the orb's Settings, under the style cards.
    sub->addAction(strings_.t(QStringLiteral("characters.more")), this, [this, id] {
        select(id);
        emit settingsRequested();
    });
}

void OrbManager::addReadoutsMenu(QMenu* menu, const QString& id) {
    const auto* c = store_.find(id);
    if (!c) return;
    auto* sub = menu->addMenu(strings_.t(QStringLiteral("character.readouts")));
    const bool measured = prefs_.trafficEnabled();
    for (const auto readout : agentspeed::character::kCharacterReadouts) {
        auto* action = sub->addAction(strings_.t(QLatin1String(agentspeed::character::titleKey(readout))));
        action->setCheckable(true);
        action->setChecked(c->characterReadouts.get(readout));
        if (readout == agentspeed::character::CharacterReadout::Network && !measured) action->setEnabled(false);
        const QString key = QLatin1String(agentspeed::character::key(readout));
        connect(action, &QAction::triggered, this, [this, id, key](bool on) { setCharacterReadout(id, key, on); });
    }
    sub->addSeparator();
    sub->addAction(strings_.t(QStringLiteral("character.readouts.networkScope")))->setEnabled(false);
    auto* group = new QActionGroup(sub);
    const bool scopeOn = measured && c->characterReadouts.network;
    for (const char* mode : {"sources", "machine"}) {
        const QString value = QString::fromLatin1(mode);
        auto* action = sub->addAction(strings_.t(QStringLiteral("orbs.traffic.") + value));
        action->setCheckable(true);
        action->setChecked(value == QLatin1String("machine") ? c->traffic == value : c->traffic != QLatin1String("machine"));
        action->setEnabled(scopeOn);
        group->addAction(action);
        connect(action, &QAction::triggered, this, [this, id, value] { set(id, QStringLiteral("traffic"), value); });
    }
}

// DESIGN 13: the Hosts submenu, only while remote hosts exist: All Hosts, then every host with its status dot and a
// check when tracked; the last tracked host cannot be unchecked.
void OrbManager::addHostsMenu(QMenu* menu, const OrbConfig& c) {
    if (!hosts_ || !hosts_->shown()) return;
    const QString id = c.id;
    const QVariant current = c.hosts ? QVariant(*c.hosts) : QVariant(QStringLiteral("all"));
    auto* submenu = menu->addMenu(strings_.t(QStringLiteral("remote.hosts")));
    auto* all = submenu->addAction(strings_.t(QStringLiteral("remote.hosts.all")));
    all->setCheckable(true);
    all->setChecked(c.tracksAllHosts());
    connect(all, &QAction::triggered, this, [this, id] { setHosts(id, QStringLiteral("all")); });
    submenu->addSeparator();
    for (const auto& item : hosts_->entries(current)) {
        const auto entry = item.toMap();
        const QString host = entry.value(QStringLiteral("id")).toString();
        const QString word = entry.value(QStringLiteral("word")).toString();
        const QString name = entry.value(QStringLiteral("name")).toString();
        auto* action = submenu->addAction(word.isEmpty() ? name : name + QStringLiteral(" — ") + word);
        action->setIcon(QIcon(hostDot(entry.value(QStringLiteral("dot")).toString())));
        action->setCheckable(true);
        action->setChecked(entry.value(QStringLiteral("checked")).toBool());
        if (!entry.value(QStringLiteral("enabled")).toBool()) {
            action->setEnabled(false);
            action->setToolTip(strings_.t(QStringLiteral("remote.hosts.lastOne")));
        }
        connect(action, &QAction::triggered, this, [this, id, host] {
            const auto* found = store_.find(id);
            if (!found || !hosts_) return;
            const auto next = hosts_->toggled(found->hosts ? QVariant(*found->hosts) : QVariant(QStringLiteral("all")), host);
            if (next.isValid()) setHosts(id, next);
        });
    }
    submenu->menuAction()->setToolTip(strings_.t(QStringLiteral("remote.hosts.allHint")));
}
