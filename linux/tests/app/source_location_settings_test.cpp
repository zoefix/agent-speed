#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QDir>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>
#include <QTemporaryDir>

#include "preferences.hpp"
#include "source_locations_view.hpp"

// DESIGN 5 Storage and the Settings side of METRICS 2.1: the stored lists, the one-time migration of the single folders
// of earlier versions, editing through Settings and the rows Settings shows.
using namespace agentspeed;

namespace {

// A Qt application with its settings and home folder in a scratch directory.
class QtEnvironment : public ::testing::Environment {
public:
    void SetUp() override {
        static int argc = 1;
        static char name[] = "agentspeed_app_tests";
        static char* argv[] = {name, nullptr};
        scratch_ = std::make_unique<QTemporaryDir>();
        qputenv("HOME", scratch_->filePath(QStringLiteral("home")).toUtf8());
        qputenv("OPENCLAW_STATE_DIR", "/nonexistent/claw");
        for (const char* variable : {"CODEX_HOME", "CLAUDE_CONFIG_DIR", "XDG_DATA_HOME", "HERMES_HOME", "CHISEL_SESSION_DB", "XDG_CONFIG_HOME",
                                     "QODER_CONFIG_DIR", "QODERCN_CONFIG_DIR"}) {
            qunsetenv(variable);
        }
        QDir().mkpath(scratch_->filePath(QStringLiteral("home/.codex/sessions")));
        app_ = std::make_unique<QCoreApplication>(argc, argv);
        QCoreApplication::setOrganizationName(QStringLiteral("zoefix"));
        QCoreApplication::setApplicationName(QStringLiteral("Agent Speed Tests"));
        QSettings::setPath(QSettings::NativeFormat, QSettings::UserScope, scratch_->filePath(QStringLiteral("config")));
    }
    void TearDown() override {
        app_.reset();
        scratch_.reset();
    }
    static QString home() { return qEnvironmentVariable("HOME"); }

private:
    std::unique_ptr<QTemporaryDir> scratch_;
    std::unique_ptr<QCoreApplication> app_;
};

[[maybe_unused]] const auto* const kEnvironment = ::testing::AddGlobalTestEnvironment(new QtEnvironment);

class SourceLocationSettings : public ::testing::Test {
protected:
    void SetUp() override { QSettings().clear(); }
    void TearDown() override { QSettings().clear(); }
};

DataLocation at(const std::string& path) { return DataLocation::at(path); }
const DataLocation kAutomatic = DataLocation::automaticEntry();

void storeLegacy(const QVariantMap& folders) {
    QSettings settings;
    settings.setValue(QStringLiteral("sources/folders"), folders);
}

QVariantMap legacy() { return QSettings().value(QStringLiteral("sources/folders")).toMap(); }

}  // namespace

TEST_F(SourceLocationSettings, TheSingleFoldersOfEarlierVersionsMigrateWithoutBeingWritten) {
    storeLegacy({{QStringLiteral("codex"), QStringLiteral("/w/codex/sessions")},
                 {QStringLiteral("claudeCode"), QStringLiteral("/w/claude/projects")},
                 {QStringLiteral("openclaw"), QStringLiteral("/w/agent/openclaw-agent.sqlite")}});
    Preferences prefs;
    const auto lists = prefs.sourceLocations();
    EXPECT_EQ(lists[RootFamily::Codex], LocationList{at("/w/codex/sessions")}) << "exactly the same folders after upgrading";
    EXPECT_EQ(lists[RootFamily::Claude], LocationList{at("/w/claude/projects")});
    EXPECT_EQ(lists[RootFamily::OpenClaw], LocationList{at("/w/agent/openclaw-agent.sqlite")});
    EXPECT_EQ(lists[RootFamily::DeepSeek], LocationList{kAutomatic});
    EXPECT_FALSE(QSettings().contains(QStringLiteral("sourceLocations"))) << "migrating writes nothing";
    EXPECT_EQ(prefs.monitorConfig().locations.codexRoots, std::vector<std::string>{"/w/codex/sessions"});
}

TEST_F(SourceLocationSettings, TheOldKeysAreNeverRewrittenAndTheNewValueWins) {
    const QVariantMap old{{QStringLiteral("codex"), QStringLiteral("/w/codex/sessions")}};
    storeLegacy(old);
    Preferences prefs;
    SourceLocationsView view(prefs);
    int changes = 0;
    QObject::connect(&prefs, &Preferences::sourcesChanged, [&] { ++changes; });
    view.add(QStringLiteral("codex"), QStringLiteral("/w/second/sessions"));
    EXPECT_EQ(changes, 1);
    EXPECT_TRUE(QSettings().contains(QStringLiteral("sourceLocations")));
    EXPECT_EQ(legacy(), old) << "the old key keeps the old value, so a downgrade restores it";
    storeLegacy({{QStringLiteral("codex"), QStringLiteral("/w/changed")}});
    EXPECT_EQ(prefs.sourceLocations()[RootFamily::Codex], (LocationList{at("/w/codex/sessions"), at("/w/second/sessions")}))
        << "once present, the new value wins";
    view.reset(QStringLiteral("codex"));
    EXPECT_TRUE(prefs.sourceLocations().isAutomaticOnly()) << "a reset list is stored too, not read from the old key again";
    EXPECT_EQ(QSettings().value(QStringLiteral("sourceLocations")).toString(), QStringLiteral(R"({"lists":{},"version":1})"));
}

TEST_F(SourceLocationSettings, EditingThroughTheSettings) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    QString reason;
    QObject::connect(&view, &SourceLocationsView::refused, [&](const QString&, const QString& why, const QString&) { reason = why; });
    const auto codex = [&] { return prefs.sourceLocations()[RootFamily::Codex]; };
    view.add(QStringLiteral("codex"), QStringLiteral("/w/a"));
    EXPECT_EQ(codex(), (LocationList{kAutomatic, at("/w/a")}));
    view.add(QStringLiteral("codex"), QStringLiteral("/w/a/"));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed"));
    reason.clear();
    view.add(QStringLiteral("codex"), QtEnvironment::home() + QStringLiteral("/.codex/sessions"));
    EXPECT_EQ(reason, QStringLiteral("alreadyDefault"));
    EXPECT_TRUE(view.canRemove(QStringLiteral("codex")));
    view.remove(QStringLiteral("codex"), 0);
    EXPECT_EQ(codex(), LocationList{at("/w/a")}) << "the automatic entry can be removed";
    EXPECT_FALSE(view.canRemove(QStringLiteral("codex")));
    view.remove(QStringLiteral("codex"), 0);
    EXPECT_EQ(codex(), LocationList{at("/w/a")}) << "the only entry stays";
    view.add(QStringLiteral("codex"), QtEnvironment::home() + QStringLiteral("/.codex/sessions"));
    EXPECT_EQ(codex(), (LocationList{kAutomatic, at("/w/a")})) << "the default location brings the automatic entry back";
    for (int i = 0; i < 6; ++i) view.add(QStringLiteral("codex"), QStringLiteral("/w/more/%1").arg(i));
    EXPECT_EQ(codex().size(), 8u);
    EXPECT_FALSE(view.canAdd(QStringLiteral("codex")));
    reason.clear();
    view.add(QStringLiteral("codex"), QStringLiteral("/w/ninth"));
    EXPECT_EQ(reason, QStringLiteral("limit"));
    EXPECT_TRUE(view.canReset(QStringLiteral("codex")));
    view.reset(QStringLiteral("codex"));
    EXPECT_EQ(codex(), LocationList{kAutomatic});
    EXPECT_FALSE(view.canReset(QStringLiteral("codex")));
    EXPECT_EQ(Preferences().sourceLocations()[RootFamily::Codex], LocationList{kAutomatic}) << "persisted";
}

TEST_F(SourceLocationSettings, EntriesAsSettingsShowsThem) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    view.add(QStringLiteral("codex"), QStringLiteral("/nonexistent/sessions"));
    const auto rows = view.entries(QStringLiteral("codex"));
    ASSERT_EQ(rows.size(), 2);
    const auto automatic = rows[0].toMap(), missing = rows[1].toMap();
    EXPECT_TRUE(automatic.value(QStringLiteral("automatic")).toBool());
    EXPECT_EQ(automatic.value(QStringLiteral("path")).toString(), QStringLiteral("~/.codex/sessions"));
    EXPECT_TRUE(automatic.value(QStringLiteral("exists")).toBool());
    EXPECT_TRUE(automatic.value(QStringLiteral("canReveal")).toBool());
    EXPECT_FALSE(missing.value(QStringLiteral("exists")).toBool());
    EXPECT_FALSE(missing.value(QStringLiteral("canReveal")).toBool());
    EXPECT_EQ(missing.value(QStringLiteral("key")).toString(), QStringLiteral("path:/nonexistent/sessions"));
    const auto groups = view.groups();
    ASSERT_EQ(groups.size(), 19);
    EXPECT_EQ(groups[0].toMap().value(QStringLiteral("list")).toString(), QStringLiteral("codex"));
    EXPECT_EQ(groups[1].toMap().value(QStringLiteral("sources")).toStringList(), QStringList{QStringLiteral("claudeCode")});
    EXPECT_EQ(groups[13].toMap().value(QStringLiteral("list")).toString(), QStringLiteral("openclaw"));
    EXPECT_TRUE(groups[13].toMap().value(QStringLiteral("acceptsFile")).toBool());
    EXPECT_EQ(view.displayPath(QtEnvironment::home() + QStringLiteral("/work")), QStringLiteral("~/work"));
}

TEST_F(SourceLocationSettings, TheNewStringsExistInEveryLanguage) {
    const auto load = [](const QString& path) {
        QFile file(path);
        EXPECT_TRUE(file.open(QIODevice::ReadOnly)) << path.toStdString();
        return QJsonDocument::fromJson(file.readAll()).object();
    };
    const auto shared = load(QStringLiteral(AGENTSPEED_SPEC_DIR "/strings.json"));
    const auto linuxOnly = load(QStringLiteral(AGENTSPEED_APP_DIR "/strings.linux.json"));
    const QStringList sharedKeys = {"settings.sources.locationsHint", "settings.sources.add", "settings.sources.default",
                                    "settings.sources.remove", "settings.sources.removeLocation", "settings.sources.keepOne",
                                    "settings.sources.limit", "settings.sources.alreadyListed", "settings.sources.alreadyDefault",
                                    "settings.sources.reset", "source.folderMissing", "source.sessions"};
    const QStringList linuxKeys = {"linux.ok", "linux.settings.sources.folder", "linux.settings.sources.reveal"};
    for (const auto& [table, keys] : {std::pair{shared, sharedKeys}, std::pair{linuxOnly, linuxKeys}}) {
        for (const auto& key : keys) {
            for (const char* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
                EXPECT_FALSE(table.value(key).toObject().value(QLatin1String(language)).toString().isEmpty())
                    << key.toStdString() << " " << language;
            }
        }
    }
}
