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

// CodeBuddy Code in the app (DESIGN 1, 4b, 5): its colour, CSV id, settings group, location list and strings.
// The Qt application, its settings and the home folder come from source_location_settings_test.cpp's environment.
using namespace agentspeed;

namespace {

class CodeBuddySettings : public ::testing::Test {
protected:
    void SetUp() override { QSettings().clear(); }
    void TearDown() override {
        QSettings().clear();
        for (const char* folder : {"/.codebuddy", "/codebuddy-test"}) QDir(home() + QLatin1String(folder)).removeRecursively();
    }
    static QString home() { return qEnvironmentVariable("HOME"); }
    static QString scratch(const QString& relative) {
        const auto path = home() + QLatin1Char('/') + relative;
        QDir().mkpath(path);
        return path;
    }
    static QString readText(const QString& path) {
        QFile file(path);
        EXPECT_TRUE(file.open(QIODevice::ReadOnly)) << path.toStdString();
        return QString::fromUtf8(file.readAll());
    }
};

}  // namespace

TEST_F(CodeBuddySettings, TheSourceHasItsOwnListAndCaption) {
    {
        QSettings settings;
        settings.setValue(QStringLiteral("sources/disabled"), QStringList{QStringLiteral("hermes")});
    }
    Preferences prefs;
    EXPECT_TRUE(prefs.sourceEnabled(QStringLiteral("codebuddyCode"))) << "on for settings saved before it";
    EXPECT_EQ(prefs.sourceLocations()[RootFamily::CodeBuddyCode], LocationList{DataLocation::automaticEntry()});
    EXPECT_TRUE(prefs.monitorConfig().enabled.contains(SourceKind::CodeBuddyCode));
    prefs.setSourceEnabled(QStringLiteral("codebuddyCode"), false);
    const auto stored = Preferences().monitorConfig().enabled;
    EXPECT_FALSE(stored.contains(SourceKind::CodeBuddyCode)) << "persisted";
    EXPECT_TRUE(stored.contains(SourceKind::WorkBuddy)) << "its own switch";

    SourceLocationsView view(prefs);
    const auto groups = view.groups();
    ASSERT_GT(groups.size(), 19);
    const auto group = groups[19].toMap();
    EXPECT_EQ(group.value(QStringLiteral("list")).toString(), QStringLiteral("codebuddyCode"));
    EXPECT_EQ(group.value(QStringLiteral("sources")).toStringList(), QStringList{QStringLiteral("codebuddyCode")});
    EXPECT_EQ(group.value(QStringLiteral("hint")).toString(), QStringLiteral("settings.sources.codebuddyCodeHint"));
    EXPECT_FALSE(group.value(QStringLiteral("acceptsFile")).toBool());
}

// The automatic entry names the config folder's `projects`; it is there once that folder exists.
TEST_F(CodeBuddySettings, TheAutomaticEntryNamesTheProjectsFolder) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    scratch(QStringLiteral(".codebuddy"));
    auto row = view.entries(QStringLiteral("codebuddyCode")).front().toMap();
    EXPECT_TRUE(row.value(QStringLiteral("automatic")).toBool());
    EXPECT_EQ(row.value(QStringLiteral("path")).toString(), QStringLiteral("~/.codebuddy/projects"));
    EXPECT_FALSE(row.value(QStringLiteral("exists")).toBool()) << "a config folder without projects is not found";
    scratch(QStringLiteral(".codebuddy/projects"));
    row = view.entries(QStringLiteral("codebuddyCode")).front().toMap();
    EXPECT_TRUE(row.value(QStringLiteral("exists")).toBool());
    EXPECT_TRUE(row.value(QStringLiteral("canReveal")).toBool());
}

// Add Folder… takes a config folder or its projects folder; the two are one location.
TEST_F(CodeBuddySettings, AddFolderAcceptsTheConfigFolderOrItsProjectsFolder) {
    Preferences prefs;
    SourceLocationsView view(prefs);
    QString reason;
    QObject::connect(&view, &SourceLocationsView::refused, [&](const QString&, const QString& why, const QString&) { reason = why; });

    scratch(QStringLiteral(".codebuddy/projects"));
    view.add(QStringLiteral("codebuddyCode"), home() + QStringLiteral("/.codebuddy"));
    EXPECT_EQ(reason, QStringLiteral("alreadyDefault")) << "the config folder is read as its projects folder";
    const auto other = scratch(QStringLiteral("codebuddy-test/config"));
    view.add(QStringLiteral("codebuddyCode"), other);
    EXPECT_EQ(view.entries(QStringLiteral("codebuddyCode")).size(), 2);
    reason.clear();
    view.add(QStringLiteral("codebuddyCode"), other + QStringLiteral("/projects"));
    EXPECT_EQ(reason, QStringLiteral("alreadyListed"));
    EXPECT_EQ(prefs.monitorConfig().locations.codeBuddyCodeRoots.back(), (other + QStringLiteral("/projects")).toStdString());
}

TEST_F(CodeBuddySettings, ColorCsvIdAndStrings) {
    EXPECT_EQ(sourceColor(QStringLiteral("codebuddyCode")).name().toUpper(), QStringLiteral("#18AB06"));
    EXPECT_TRUE(readText(QStringLiteral(AGENTSPEED_APP_DIR "/qml/Theme.qml")).contains(QStringLiteral("codebuddyCode: \"#18AB06\"")));
    for (const auto* qml : {"/qml/HistoryWindow.qml", "/qml/ModeSection.qml"}) {
        EXPECT_TRUE(readText(QStringLiteral(AGENTSPEED_APP_DIR) + QLatin1String(qml)).contains(QStringLiteral("codebuddyCode: \"codebuddy-code\"")))
            << qml << ": the CSV id (DESIGN 4b)";
    }
    for (const auto* qml : {"/qml/HistoryWindow.qml", "/qml/Dashboard.qml", "/qml/BoardsGroup.qml"}) {
        EXPECT_TRUE(readText(QStringLiteral(AGENTSPEED_APP_DIR) + QLatin1String(qml)).contains(QStringLiteral("\"codebuddyCode\"")))
            << qml << ": the source order";
    }

    const auto table = QJsonDocument::fromJson(readText(QStringLiteral(AGENTSPEED_SPEC_DIR "/strings.json")).toUtf8()).object();
    for (const char* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
        SCOPED_TRACE(language);
        EXPECT_EQ(table.value(QStringLiteral("source.codebuddyCode")).toObject().value(QLatin1String(language)).toString(),
                  QStringLiteral("CodeBuddy Code"));
        const auto hint = table.value(QStringLiteral("settings.sources.codebuddyCodeHint")).toObject().value(QLatin1String(language)).toString();
        EXPECT_TRUE(hint.contains(QStringLiteral("~/.codebuddy/projects")));
        EXPECT_TRUE(hint.contains(QStringLiteral(".codebuddy")));
    }
}
