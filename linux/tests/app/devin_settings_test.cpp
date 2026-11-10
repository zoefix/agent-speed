#include <gtest/gtest.h>

#include <algorithm>

#include <QDir>
#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>

#include "agentspeed/location_entries.hpp"
#include "preferences.hpp"
#include "source_colors.hpp"
#include "source_locations_view.hpp"

// Devin in the app (DESIGN 1, 5): its colour, its settings group and location list, and its strings. The Qt
// application, its settings and the home folder come from source_location_settings_test.cpp's environment.
using namespace agentspeed;

namespace {

class DevinSettings : public ::testing::Test {
protected:
    void SetUp() override { QSettings().clear(); }
    void TearDown() override {
        QSettings().clear();
        QDir(home() + QStringLiteral("/devin-test")).removeRecursively();
    }
    static QString home() { return qEnvironmentVariable("HOME"); }
    static QString scratch(const QString& relative) {
        const auto path = home() + QStringLiteral("/devin-test/") + relative;
        QDir().mkpath(path);
        return path;
    }
};

QVariantMap only(const QVariantList& rows) {
    EXPECT_EQ(rows.size(), 1);
    return rows.isEmpty() ? QVariantMap() : rows.front().toMap();
}

}  // namespace

TEST_F(DevinSettings, SettingsSavedBeforeDevinTurnItOnWithItsAutomaticEntry) {
    {
        QSettings settings;
        settings.setValue(QStringLiteral("sources/disabled"), QStringList{QStringLiteral("hermes")});
    }
    Preferences prefs;
    EXPECT_TRUE(prefs.sourceEnabled(QStringLiteral("devin")));
    EXPECT_FALSE(prefs.sourceEnabled(QStringLiteral("hermes")));
    EXPECT_EQ(prefs.sourceLocations()[RootFamily::Devin], LocationList{DataLocation::automaticEntry()});
    EXPECT_TRUE(prefs.monitorConfig().enabled.contains(SourceKind::Devin));
    prefs.setSourceEnabled(QStringLiteral("devin"), false);
    EXPECT_FALSE(Preferences().monitorConfig().enabled.contains(SourceKind::Devin)) << "persisted";

    SourceLocationsView view(prefs);
    const auto groups = view.groups();
    const auto found = std::find_if(groups.begin(), groups.end(), [](const QVariant& g) {
        return g.toMap().value(QStringLiteral("list")).toString() == QStringLiteral("devin");
    });
    ASSERT_NE(found, groups.end());
    EXPECT_EQ(found - groups.begin(), 16) << "after Factory Droid";
    const auto group = found->toMap();
    EXPECT_EQ(group.value(QStringLiteral("list")).toString(), QStringLiteral("devin"));
    EXPECT_EQ(group.value(QStringLiteral("sources")).toStringList(), QStringList{QStringLiteral("devin")});
    EXPECT_EQ(group.value(QStringLiteral("hint")).toString(), QStringLiteral("settings.sources.devinHint"));
    EXPECT_FALSE(group.value(QStringLiteral("acceptsFile")).toBool());
}

TEST_F(DevinSettings, TheAutomaticEntryNamesTheCliFolderOnly) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    const auto row = only(view.entries(QStringLiteral("devin")));
    EXPECT_TRUE(row.value(QStringLiteral("automatic")).toBool());
    EXPECT_EQ(row.value(QStringLiteral("path")).toString(), QStringLiteral("~/.local/share/devin/cli"));
    EXPECT_EQ(row.value(QStringLiteral("lines")).toList().size(), 1) << "one folder, no Desktop folder";
    EXPECT_FALSE(row.value(QStringLiteral("exists")).toBool()) << "Devin is not installed in the scratch home";
    EXPECT_FALSE(row.value(QStringLiteral("isFile")).toBool());
    const auto environment = processEnvironment();
    EXPECT_EQ(automaticShown(RootFamily::Devin, environment, userHome(environment)),
              std::vector<std::string>{(home() + QStringLiteral("/.local/share/devin/cli")).toStdString()});
}

TEST_F(DevinSettings, AFolderCountsWhenItHoldsTheDatabase) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    const auto cli = scratch(QStringLiteral("devin/cli"));
    view.add(QStringLiteral("devin"), cli);
    auto rows = view.entries(QStringLiteral("devin"));
    ASSERT_EQ(rows.size(), 2);
    EXPECT_FALSE(rows[1].toMap().value(QStringLiteral("exists")).toBool()) << "an empty folder holds nothing Devin wrote";
    EXPECT_FALSE(rows[1].toMap().value(QStringLiteral("canReveal")).toBool());

    QFile database(cli + QStringLiteral("/sessions.db"));
    ASSERT_TRUE(database.open(QIODevice::WriteOnly));
    database.close();
    rows = view.entries(QStringLiteral("devin"));
    EXPECT_TRUE(rows[1].toMap().value(QStringLiteral("exists")).toBool());
    EXPECT_TRUE(rows[1].toMap().value(QStringLiteral("canReveal")).toBool());
    EXPECT_FALSE(rows[1].toMap().value(QStringLiteral("isFile")).toBool());
    EXPECT_TRUE(isReadableLocation(RootFamily::Devin, scratch(QStringLiteral("devin")).toStdString())) << "the devin folder above cli";

    const auto desktop = scratch(QStringLiteral("Support/Devin/User/acp-events"));
    QFile events(desktop + QStringLiteral("/d.ndjson"));
    ASSERT_TRUE(events.open(QIODevice::WriteOnly));
    events.write("{}\n");
    events.close();
    EXPECT_FALSE(isReadableLocation(RootFamily::Devin, scratch(QStringLiteral("Support/Devin")).toStdString())) << "Devin Desktop is not read";

    QString reason;
    QObject::connect(&view, &SourceLocationsView::refused, [&](const QString&, const QString& why, const QString&) { reason = why; });
    view.add(QStringLiteral("devin"), scratch(QStringLiteral("devin")));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed")) << "a Devin data folder and its cli folder name one database";
}

TEST_F(DevinSettings, ColorAndStrings) {
    EXPECT_EQ(sourceColor(QStringLiteral("devin")).name().toUpper(), QStringLiteral("#557F14"));
    QFile file(QStringLiteral(AGENTSPEED_SPEC_DIR "/strings.json"));
    ASSERT_TRUE(file.open(QIODevice::ReadOnly));
    const auto table = QJsonDocument::fromJson(file.readAll()).object();
    for (const char* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
        SCOPED_TRACE(language);
        EXPECT_EQ(table.value(QStringLiteral("source.devin")).toObject().value(QLatin1String(language)).toString(), QStringLiteral("Devin"));
        const auto hint = table.value(QStringLiteral("settings.sources.devinHint")).toObject().value(QLatin1String(language)).toString();
        EXPECT_TRUE(hint.contains(QStringLiteral("Devin")));
        EXPECT_TRUE(hint.contains(QStringLiteral("%APPDATA%\\devin\\cli")));
        EXPECT_FALSE(hint.contains(QStringLiteral("Desktop")));
        EXPECT_FALSE(hint.contains(QStringLiteral("acp-events")));
    }
    EXPECT_TRUE(table.value(QStringLiteral("settings.sources.devinHint")).toObject().value(QStringLiteral("zh-Hant")).toString().contains(
        QStringLiteral("工作階段")));
}
