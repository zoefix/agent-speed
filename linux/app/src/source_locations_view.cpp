#include "source_locations_view.hpp"

#include <QApplication>
#include <QDesktopServices>
#include <QDir>
#include <QFileDialog>
#include <QFileInfo>
#include <QUrl>
#include <QVariantMap>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/location_entries.hpp"
#include "preferences.hpp"

namespace {

using agentspeed::RootFamily;

QString hintOf(RootFamily list) {
    switch (list) {
        case RootFamily::Claude: return QStringLiteral("settings.sources.sharedRoot");
        case RootFamily::DeepSeek: return QStringLiteral("settings.sources.dshHint");
        case RootFamily::OpenCode: return QStringLiteral("settings.sources.opencodeHint");
        case RootFamily::Gemini: return QStringLiteral("settings.sources.geminiHint");
        case RootFamily::Omp: return QStringLiteral("settings.sources.ompHint");
        case RootFamily::Antigravity: return QStringLiteral("settings.sources.antigravityHint");
        case RootFamily::WorkBuddy: return QStringLiteral("settings.sources.workbuddyHint");
        case RootFamily::WorkBuddyAi: return QStringLiteral("settings.sources.workbuddyAiHint");
        case RootFamily::ZCode: return QStringLiteral("settings.sources.zcodeHint");
        case RootFamily::Kimi: return QStringLiteral("settings.sources.kimiHint");
        case RootFamily::GrokBuild: return QStringLiteral("settings.sources.grokBuildHint");
        case RootFamily::Pi: return QStringLiteral("settings.sources.piHint");
        case RootFamily::OpenClaw: return QStringLiteral("settings.sources.openclawHint");
        case RootFamily::Hermes: return QStringLiteral("settings.sources.hermesHint");
        case RootFamily::Droid: return QStringLiteral("settings.sources.droidHint");
        case RootFamily::Devin: return QStringLiteral("settings.sources.devinHint");
        case RootFamily::Qoder: return QStringLiteral("settings.sources.qoderHint");
        case RootFamily::QoderCli: return QStringLiteral("settings.sources.qoderCliHint");
        default: return {};
    }
}

QString reasonOf(agentspeed::AddOutcome outcome) {
    switch (outcome) {
        case agentspeed::AddOutcome::AlreadyListed: return QStringLiteral("alreadyListed");
        case agentspeed::AddOutcome::AlreadyAutomatic: return QStringLiteral("alreadyDefault");
        case agentspeed::AddOutcome::Full: return QStringLiteral("limit");
        default: return {};
    }
}

QString qs(const std::string& text) { return QString::fromStdString(text); }

}  // namespace

SourceLocationsView::SourceLocationsView(Preferences& prefs, QObject* parent)
    : QObject(parent), prefs_(prefs), environment_(agentspeed::processEnvironment()), home_(agentspeed::userHome(environment_)) {
    connect(&prefs_, &Preferences::sourcesChanged, this, [this] {
        ++revision_;
        emit changed();
    });
}

std::optional<RootFamily> SourceLocationsView::listOf(const QString& list) const { return agentspeed::listNamed(list.toStdString()); }

QVariantList SourceLocationsView::groups() const {
    QVariantList out;
    for (const auto family : agentspeed::locationListOrder()) {
        QStringList sources;
        for (int k = 0; k <= static_cast<int>(agentspeed::SourceKind::QoderCli); ++k) {
            const auto kind = static_cast<agentspeed::SourceKind>(k);
            // Claude Code and Claude Desktop share one switch on Linux.
            if (kind == agentspeed::SourceKind::ClaudeDesktop) continue;
            if (agentspeed::familyOf(kind) == family) sources.append(QString::fromUtf8(agentspeed::sourceId(kind)));
        }
        out.append(QVariantMap{{QStringLiteral("list"), QString::fromUtf8(agentspeed::listName(family))},
                               {QStringLiteral("sources"), sources},
                               {QStringLiteral("hint"), hintOf(family)},
                               {QStringLiteral("acceptsFile"), agentspeed::acceptsDatabaseFile(family)}});
    }
    return out;
}

QVariantList SourceLocationsView::entries(const QString& list) const {
    const auto family = listOf(list);
    if (!family) return {};
    QVariantList out;
    for (const auto& entry : agentspeed::locationEntries(*family, prefs_.sourceLocations(), environment_, home_)) {
        QVariantList lines;
        for (const auto& [path, readable] : entry.shown) {
            lines.append(QVariantMap{{QStringLiteral("text"), qs(agentspeed::abbreviatedPath(path, home_))},
                                     {QStringLiteral("full"), qs(path)},
                                     {QStringLiteral("readable"), readable}});
        }
        const auto first = entry.shown.empty() ? std::string() : entry.shown.front().first;
        out.append(QVariantMap{{QStringLiteral("key"), entry.location.automatic ? QStringLiteral("automatic") : QStringLiteral("path:") + qs(entry.location.path)},
                               {QStringLiteral("automatic"), entry.location.automatic},
                               {QStringLiteral("lines"), lines},
                               {QStringLiteral("path"), qs(agentspeed::abbreviatedPath(first, home_))},
                               {QStringLiteral("exists"), entry.exists},
                               {QStringLiteral("canReveal"), entry.reveal.has_value()},
                               {QStringLiteral("isFile"), entry.isFile}});
    }
    return out;
}

bool SourceLocationsView::canAdd(const QString& list) const {
    const auto family = listOf(list);
    return family && prefs_.sourceLocations().canAdd(*family);
}

bool SourceLocationsView::canRemove(const QString& list) const {
    const auto family = listOf(list);
    return family && prefs_.sourceLocations().canRemove(*family);
}

bool SourceLocationsView::canReset(const QString& list) const {
    const auto family = listOf(list);
    return family && prefs_.sourceLocations().canReset(*family);
}

QString SourceLocationsView::pickerStart(RootFamily list) const {
    const auto entries = agentspeed::locationEntries(list, prefs_.sourceLocations(), environment_, home_);
    std::string start;
    for (auto it = entries.rbegin(); it != entries.rend() && start.empty(); ++it) {
        if (it->reveal) start = *it->reveal;
    }
    if (start.empty()) {
        const auto defaults = agentspeed::defaultLocations(list, environment_, home_);
        start = defaults.empty() ? home_ : defaults.front();
    }
    // The nearest folder that exists, so the picker never opens somewhere unrelated.
    while (!start.empty() && start != "/" && !agentspeed::fsx::isDirectory(start)) start = agentspeed::fsx::parentOf(start);
    return start.empty() ? qs(home_) : qs(start);
}

void SourceLocationsView::choose(const QString& list, const QString& title) {
    const auto family = listOf(list);
    if (!family) return;
    const auto start = pickerStart(*family);
    QString picked;
    if (agentspeed::acceptsDatabaseFile(*family)) {
        QFileDialog dialog(nullptr, title, start);
        dialog.setFileMode(QFileDialog::AnyFile);
        dialog.setOption(QFileDialog::DontResolveSymlinks);
        if (dialog.exec() == QDialog::Accepted && !dialog.selectedFiles().isEmpty()) picked = dialog.selectedFiles().constFirst();
    } else {
        picked = QFileDialog::getExistingDirectory(nullptr, title, start, QFileDialog::ShowDirsOnly | QFileDialog::DontResolveSymlinks);
    }
    if (!picked.isEmpty()) add(list, picked);
}

void SourceLocationsView::add(const QString& list, const QString& path) {
    const auto family = listOf(list);
    if (!family) return;
    auto lists = prefs_.sourceLocations();
    const auto outcome = lists.add(path.toStdString(), *family, agentspeed::automaticKeys(*family, lists, environment_, home_));
    if (agentspeed::changedList(outcome)) {
        prefs_.setSourceLocations(lists);
    } else if (outcome == agentspeed::AddOutcome::Invalid) {
        QApplication::beep();
    } else {
        emit refused(list, reasonOf(outcome), displayPath(QDir::cleanPath(path)));
    }
}

void SourceLocationsView::remove(const QString& list, int index) {
    const auto family = listOf(list);
    if (!family) return;
    auto lists = prefs_.sourceLocations();
    const auto& entries = lists[*family];
    if (index < 0 || index >= static_cast<int>(entries.size())) return;
    lists.remove(*family, agentspeed::DataLocation(entries[static_cast<std::size_t>(index)]));
    prefs_.setSourceLocations(lists);
}

void SourceLocationsView::reset(const QString& list) {
    const auto family = listOf(list);
    if (!family) return;
    auto lists = prefs_.sourceLocations();
    lists.reset(*family);
    prefs_.setSourceLocations(lists);
}

void SourceLocationsView::reveal(const QString& list, int index) const {
    const auto family = listOf(list);
    if (!family) return;
    const auto lists = prefs_.sourceLocations();
    const auto& entries = lists[*family];
    if (index < 0 || index >= static_cast<int>(entries.size())) return;
    const auto entry = agentspeed::locationEntry(*family, entries[static_cast<std::size_t>(index)], environment_, home_);
    if (!entry.reveal) return;
    // A database file opens its folder.
    auto folder = *entry.reveal;
    if (!agentspeed::fsx::isDirectory(folder)) folder = agentspeed::fsx::parentOf(folder);
    QDesktopServices::openUrl(QUrl::fromLocalFile(qs(folder)));
}

QString SourceLocationsView::displayPath(const QString& path) const { return qs(agentspeed::abbreviatedPath(path.toStdString(), home_)); }
