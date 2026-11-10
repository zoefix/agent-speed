#include <gtest/gtest.h>

#include <QDir>
#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>

#include "agentspeed/location_entries.hpp"
#include "preferences.hpp"
#include "source_colors.hpp"
#include "source_locations_view.hpp"

// Qoder IDE and Qoder CLI in the app (DESIGN 1, 5): their colours, settings groups and location lists, and their strings.
// The Qt application, its settings and the home folder come from source_location_settings_test.cpp's environment.
using namespace agentspeed;

namespace {

class QoderSettings : public ::testing::Test {
protected:
    void SetUp() override { QSettings().clear(); }
    void TearDown() override {
        QSettings().clear();
        for (const char* folder : {"/.config/Qoder", "/.config/QoderCN", "/.qoder", "/qoder-test"}) {
            QDir(home() + QLatin1String(folder)).removeRecursively();
        }
    }
    static QString home() { return qEnvironmentVariable("HOME"); }
    static QString scratch(const QString& relative) {
        const auto path = home() + QLatin1Char('/') + relative;
        QDir().mkpath(path);
        return path;
    }
    static void touch(const QString& path) {
        QFile file(path);
        ASSERT_TRUE(file.open(QIODevice::WriteOnly));
        file.close();
    }
};

}  // namespace

TEST_F(QoderSettings, BothSourcesSitLastWithTheirOwnListsAndCaptions) {
    {
        QSettings settings;
        settings.setValue(QStringLiteral("sources/disabled"), QStringList{QStringLiteral("hermes")});
    }
    Preferences prefs;
    for (const auto* id : {"qoder", "qoderCli"}) EXPECT_TRUE(prefs.sourceEnabled(QLatin1String(id))) << "on for settings saved before them";
    EXPECT_EQ(prefs.sourceLocations()[RootFamily::Qoder], LocationList{DataLocation::automaticEntry()});
    EXPECT_EQ(prefs.sourceLocations()[RootFamily::QoderCli], LocationList{DataLocation::automaticEntry()});
    EXPECT_TRUE(prefs.monitorConfig().enabled.contains(SourceKind::Qoder));
    EXPECT_TRUE(prefs.monitorConfig().enabled.contains(SourceKind::QoderCli));
    prefs.setSourceEnabled(QStringLiteral("qoderCli"), false);
    const auto stored = Preferences().monitorConfig().enabled;
    EXPECT_FALSE(stored.contains(SourceKind::QoderCli)) << "persisted";
    EXPECT_TRUE(stored.contains(SourceKind::Qoder)) << "each edition of the product line has its own switch";

    SourceLocationsView view(prefs);
    const auto groups = view.groups();
    ASSERT_GE(groups.size(), 2);
    const auto ide = groups[groups.size() - 2].toMap(), cli = groups.back().toMap();
    EXPECT_EQ(ide.value(QStringLiteral("list")).toString(), QStringLiteral("qoder"));
    EXPECT_EQ(ide.value(QStringLiteral("sources")).toStringList(), QStringList{QStringLiteral("qoder")});
    EXPECT_EQ(ide.value(QStringLiteral("hint")).toString(), QStringLiteral("settings.sources.qoderHint"));
    EXPECT_FALSE(ide.value(QStringLiteral("acceptsFile")).toBool()) << "a folder, never local.db itself";
    EXPECT_EQ(cli.value(QStringLiteral("list")).toString(), QStringLiteral("qoderCli"));
    EXPECT_EQ(cli.value(QStringLiteral("sources")).toStringList(), QStringList{QStringLiteral("qoderCli")});
    EXPECT_EQ(cli.value(QStringLiteral("hint")).toString(), QStringLiteral("settings.sources.qoderCliHint"));
}

// The automatic entry names each edition's folder that holds local.db; it is there once that database exists.
TEST_F(QoderSettings, TheAutomaticEntryNamesEachEditionsDatabaseFolder) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    auto rows = view.entries(QStringLiteral("qoder"));
    ASSERT_EQ(rows.size(), 1);
    auto row = rows.front().toMap();
    EXPECT_TRUE(row.value(QStringLiteral("automatic")).toBool());
    EXPECT_EQ(row.value(QStringLiteral("path")).toString(), QStringLiteral("~/.config/Qoder/SharedClientCache/cache/db"));
    auto lines = row.value(QStringLiteral("lines")).toList();
    ASSERT_EQ(lines.size(), 2);
    EXPECT_EQ(lines[1].toMap().value(QStringLiteral("text")).toString(), QStringLiteral("~/.config/QoderCN/SharedClientCache/cache/db"));
    EXPECT_FALSE(row.value(QStringLiteral("exists")).toBool()) << "Qoder is not installed in the scratch home";

    touch(scratch(QStringLiteral(".config/QoderCN/SharedClientCache/cache/db")) + QStringLiteral("/local.db"));
    row = view.entries(QStringLiteral("qoder")).front().toMap();
    lines = row.value(QStringLiteral("lines")).toList();
    EXPECT_FALSE(lines[0].toMap().value(QStringLiteral("readable")).toBool());
    EXPECT_TRUE(lines[1].toMap().value(QStringLiteral("readable")).toBool()) << "the China edition alone";
    EXPECT_TRUE(row.value(QStringLiteral("exists")).toBool());
    EXPECT_TRUE(row.value(QStringLiteral("canReveal")).toBool());

    const auto cliRow = view.entries(QStringLiteral("qoderCli")).front().toMap();
    EXPECT_EQ(cliRow.value(QStringLiteral("path")).toString(), QStringLiteral("~/.qoder/projects"));
    const auto cliLines = cliRow.value(QStringLiteral("lines")).toList();
    ASSERT_EQ(cliLines.size(), 2);
    EXPECT_EQ(cliLines[1].toMap().value(QStringLiteral("text")).toString(), QStringLiteral("~/.qoder-cn/projects"));
}

// Add Folder… takes the IDE's data folder, its SharedClientCache folder or the folder that holds local.db, and a Qoder
// CLI config folder or its projects folder; two that name the same database or folder are one location.
TEST_F(QoderSettings, AddFolderAcceptsWhatEachSourceNames) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    QString reason;
    QObject::connect(&view, &SourceLocationsView::refused, [&](const QString&, const QString& why, const QString&) { reason = why; });

    const auto data = scratch(QStringLiteral("qoder-test/portable/user-data"));
    view.add(QStringLiteral("qoder"), data);
    auto rows = view.entries(QStringLiteral("qoder"));
    ASSERT_EQ(rows.size(), 2);
    EXPECT_FALSE(rows[1].toMap().value(QStringLiteral("exists")).toBool()) << "an empty folder holds no database";
    touch(scratch(QStringLiteral("qoder-test/portable/user-data/SharedClientCache/cache/db")) + QStringLiteral("/local.db"));
    rows = view.entries(QStringLiteral("qoder"));
    EXPECT_TRUE(rows[1].toMap().value(QStringLiteral("exists")).toBool());
    EXPECT_FALSE(rows[1].toMap().value(QStringLiteral("isFile")).toBool());
    view.add(QStringLiteral("qoder"), data + QStringLiteral("/SharedClientCache"));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed")) << "the data folder and its SharedClientCache folder name one local.db";
    reason.clear();
    view.add(QStringLiteral("qoder"), data + QStringLiteral("/SharedClientCache/cache/db"));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed"));
    touch(scratch(QStringLiteral(".config/Qoder/SharedClientCache/cache/db")) + QStringLiteral("/local.db"));
    reason.clear();
    view.add(QStringLiteral("qoder"), home() + QStringLiteral("/.config/Qoder"));
    EXPECT_EQ(reason, QStringLiteral("alreadyDefault"));

    scratch(QStringLiteral(".qoder/projects"));
    reason.clear();
    view.add(QStringLiteral("qoderCli"), home() + QStringLiteral("/.qoder"));
    EXPECT_EQ(reason, QStringLiteral("alreadyDefault")) << "the config folder is read as its projects folder";
    const auto other = scratch(QStringLiteral("qoder-test/cli-config"));
    view.add(QStringLiteral("qoderCli"), other);
    EXPECT_EQ(view.entries(QStringLiteral("qoderCli")).size(), 2);
    reason.clear();
    view.add(QStringLiteral("qoderCli"), other + QStringLiteral("/projects"));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed"));
    EXPECT_EQ(prefs.monitorConfig().locations.qoderCliRoots.back(), (other + QStringLiteral("/projects")).toStdString());
}

TEST_F(QoderSettings, ColorsAndStrings) {
    EXPECT_EQ(sourceColor(QStringLiteral("qoder")).name().toUpper(), QStringLiteral("#B3329D"));
    EXPECT_EQ(sourceColor(QStringLiteral("qoderCli")).name().toUpper(), QStringLiteral("#7E61B6"));
    QFile theme(QStringLiteral(AGENTSPEED_APP_DIR "/qml/Theme.qml"));
    ASSERT_TRUE(theme.open(QIODevice::ReadOnly));
    const auto qml = QString::fromUtf8(theme.readAll());
    EXPECT_TRUE(qml.contains(QStringLiteral("qoder: \"#B3329D\"")));
    EXPECT_TRUE(qml.contains(QStringLiteral("qoderCli: \"#7E61B6\"")));
    QFile history(QStringLiteral(AGENTSPEED_APP_DIR "/qml/HistoryWindow.qml"));
    ASSERT_TRUE(history.open(QIODevice::ReadOnly));
    EXPECT_TRUE(QString::fromUtf8(history.readAll()).contains(QStringLiteral("qoderCli: \"qoder-cli\""))) << "the CSV id (DESIGN 4b)";

    QFile file(QStringLiteral(AGENTSPEED_SPEC_DIR "/strings.json"));
    ASSERT_TRUE(file.open(QIODevice::ReadOnly));
    const auto table = QJsonDocument::fromJson(file.readAll()).object();
    for (const char* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
        SCOPED_TRACE(language);
        EXPECT_EQ(table.value(QStringLiteral("source.qoder")).toObject().value(QLatin1String(language)).toString(), QStringLiteral("Qoder IDE"));
        EXPECT_EQ(table.value(QStringLiteral("source.qoderCli")).toObject().value(QLatin1String(language)).toString(), QStringLiteral("Qoder CLI"));
        const auto ide = table.value(QStringLiteral("settings.sources.qoderHint")).toObject().value(QLatin1String(language)).toString();
        EXPECT_TRUE(ide.contains(QStringLiteral("~/.config/Qoder")));
        EXPECT_TRUE(ide.contains(QStringLiteral("SharedClientCache")));
        const auto cli = table.value(QStringLiteral("settings.sources.qoderCliHint")).toObject().value(QLatin1String(language)).toString();
        EXPECT_TRUE(cli.contains(QStringLiteral("~/.qoder/projects")));
        EXPECT_TRUE(cli.contains(QStringLiteral("QODER_EXPOSE_TOKEN_USAGE=1")));
    }
}
