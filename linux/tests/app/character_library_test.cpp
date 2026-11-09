#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QElapsedTimer>
#include <QFile>
#include <QSettings>
#include <QTemporaryDir>
#include <QTimer>

#include "../character_zip_support.hpp"
#include "agentspeed/character/zip.hpp"
#include "characters/character_library.hpp"

// CHARACTERS 2: the catalog, downloads the user starts, and the automatic download of every pack (one at a time, live
// vendors first, set-aside packs left out, Stop, network failures end the run). Ported from macOS CharacterLibraryTests.
namespace {

namespace ac = agentspeed::character;
namespace fs = std::filesystem;

bool pump(const std::function<bool()>& done, int ms = 5000) {
    QElapsedTimer timer;
    timer.start();
    while (!done()) {
        if (timer.elapsed() > ms) return false;
        QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
    }
    return true;
}

// Answers the catalog with fixed bytes and copies a local archive per pack (after `delayMs`), logging the order.
class FakeTransport : public CharacterTransport {
public:
    QByteArray catalog;
    int catalogStatus = 200;
    std::map<QString, QString> archives;  // pack id → local zip
    QStringList order;
    int running = 0, mostAtOnce = 0, catalogRequests = 0;
    bool failNetwork = false;
    int delayMs = 20;

    void getCatalog(const QUrl&, const QString&, std::function<void(const Response&)> done) override {
        ++catalogRequests;
        QTimer::singleShot(5, [this, done] { done(Response{catalogStatus, catalog, QStringLiteral("\"e1\""), catalogStatus >= 500}); });
    }

    Cancel download(const QUrl& url, const QString& path, qint64, std::function<void(double)> progress,
                    std::function<void(bool, bool)> done) override {
        const QString id = url.fileName().section(QLatin1Char('-'), 0, 0);
        order.push_back(id);
        ++running;
        mostAtOnce = std::max(mostAtOnce, running);
        auto cancelled = std::make_shared<bool>(false);
        auto finished = std::make_shared<bool>(false);
        QTimer::singleShot(delayMs, [this, id, path, progress, done, cancelled, finished] {
            if (*finished) return;
            *finished = true;
            --running;
            if (*cancelled) return done(false, true);
            if (failNetwork) return done(false, false);
            QFile::remove(path);
            QFile::copy(archives.at(id), path);
            progress(1);
            done(true, false);
        });
        return [this, cancelled, finished, done] {
            *cancelled = true;
            if (*finished) return;
            *finished = true;
            --running;
            QTimer::singleShot(0, [done] { done(false, true); });
        };
    }
};

struct Fixture {
    std::unique_ptr<QTemporaryDir> dir = std::make_unique<QTemporaryDir>();
    FakeTransport* transport = nullptr;
    std::unique_ptr<CharacterLibrary> library;

    explicit Fixture(const std::vector<std::pair<std::string, std::string>>& packs = {{"claude", "anthropic"}, {"gpt", "openai"}, {"kimi", "moonshot"}},
                     bool network = true) {
        QSettings().remove(QLatin1String(ac::auto_download::kSetAsideKey));
        auto t = std::make_unique<FakeTransport>();
        std::string entries;
        for (const auto& [id, vendor] : packs) {
            const auto zip = test_zip::packZip(id, 1, vendor).write(fs::path(dir->filePath(QString::fromStdString(id + ".zip")).toStdString()));
            t->archives[QString::fromStdString(id)] = QString::fromStdString(zip.string());
            entries += (entries.empty() ? "" : ",") + std::string(R"({"id":")") + id +
                       R"(","packVersion":1,"name":{"zh-Hant":"a","zh-Hans":"a","en":")" + id + R"(","ja":"a"},"vendors":[")" +
                       vendor + R"("],"accent":"#D97757","size":)" + std::to_string(fs::file_size(zip)) + R"(,"sha256":")" +
                       ac::sha256File(zip) + R"(","url":"https://github.com/zoefix/agent-speed/releases/download/characters/)" + id +
                       R"(-1.zip"})";
        }
        t->catalog = QByteArray::fromStdString(R"({"format":"agent-speed-character-catalog","version":1,"characters":[)" + entries + "]}");
        transport = t.get();
        library = std::make_unique<CharacterLibrary>(
            ac::CharacterStore(fs::path(dir->filePath(QStringLiteral("characters")).toStdString())), std::move(t));
        library->networkEnabled = [network] { return network; };
    }

    bool settle(int ms = 8000) {
        return pump([this] { return library->autoTotal() == 0 && library->catalogState() != QLatin1String("loading") && library->downloads().empty(); }, ms);
    }
    std::set<std::string> installed() const {
        std::set<std::string> out;
        for (const auto& p : library->installed()) out.insert(p.id());
        return out;
    }
};

}  // namespace

TEST(CharacterLibrary, TheCatalogLoadsAndDownloadsInstall) {
    Fixture f({{"claude", "anthropic"}});
    f.library->refreshForList();
    ASSERT_TRUE(pump([&] { return f.library->catalogState() != QLatin1String("loading"); }));
    ASSERT_TRUE(f.library->catalog());
    ASSERT_EQ(f.library->catalog()->characters.size(), 1u);
    EXPECT_EQ(f.library->rows().size(), 1);
    f.library->download(QStringLiteral("claude"));
    ASSERT_TRUE(pump([&] { return !f.library->installed().empty(); }));
    EXPECT_EQ(f.installed(), (std::set<std::string>{"claude"}));
    ASSERT_EQ(f.library->packs().size(), 1u);
    EXPECT_EQ(f.library->packs()[0].vendors, std::vector<std::string>{"anthropic"});
    EXPECT_TRUE(f.library->errors().empty() && f.library->downloads().empty());
    EXPECT_FALSE(f.library->hasUpdate(QStringLiteral("claude")));
    EXPECT_TRUE(f.library->rows()[0].toMap().value(QStringLiteral("installed")).toBool());
    f.library->remove(QStringLiteral("claude"));
    EXPECT_TRUE(f.library->installed().empty());
}

TEST(CharacterLibrary, NothingIsFetchedWhileNetworkFeaturesAreOff) {
    Fixture f({{"claude", "anthropic"}}, false);
    f.library->refreshForList();
    EXPECT_EQ(f.library->catalogState(), QStringLiteral("idle"));
    f.library->downloadAll({});
    f.settle(300);
    EXPECT_EQ(f.transport->catalogRequests, 0);
    EXPECT_TRUE(f.transport->order.isEmpty());
}

TEST(CharacterLibrary, AChecksumMismatchShowsItsErrorAndInstallsNothing) {
    Fixture f({{"claude", "anthropic"}});
    f.library->refreshForList();
    ASSERT_TRUE(pump([&] { return f.library->catalog().has_value(); }));
    QFile file(f.transport->archives[QStringLiteral("claude")]);
    ASSERT_TRUE(file.open(QIODevice::ReadWrite));
    file.seek(file.size() - 30);
    file.write("X");
    file.close();
    f.library->download(QStringLiteral("claude"));
    ASSERT_TRUE(pump([&] { return f.library->downloads().empty() && !f.library->errors().empty(); }));
    EXPECT_EQ(f.library->errors().at(QStringLiteral("claude")), QStringLiteral("characters.error.checksum"));
    EXPECT_TRUE(f.library->installed().empty());
}

TEST(CharacterLibrary, SwitchingDownloadsEveryPackOneAtATimeLiveVendorFirst) {
    Fixture f;
    f.library->downloadAll({QStringLiteral("moonshot")});
    ASSERT_TRUE(f.settle());
    EXPECT_EQ(f.transport->order, (QStringList{QStringLiteral("kimi"), QStringLiteral("claude"), QStringLiteral("gpt")}));
    EXPECT_EQ(f.transport->mostAtOnce, 1);
    EXPECT_EQ(f.installed(), (std::set<std::string>{"claude", "gpt", "kimi"}));
    EXPECT_TRUE(f.library->autoTotal() == 0 && f.library->autoQueue().isEmpty() && f.library->autoCurrent().isEmpty());
}

TEST(CharacterLibrary, RemovedPacksStayAsideUntilDownloadedByHand) {
    Fixture f;
    f.library->downloadAll({});
    ASSERT_TRUE(f.settle());
    f.library->remove(QStringLiteral("gpt"));
    EXPECT_EQ(QSettings().value(QLatin1String(ac::auto_download::kSetAsideKey)).toStringList(), QStringList{QStringLiteral("gpt")});
    f.library->downloadAll({});
    ASSERT_TRUE(f.settle());
    EXPECT_EQ(f.library->installed(QStringLiteral("gpt")), nullptr);
    EXPECT_EQ(f.transport->order.size(), 3);
    // A new library over the same settings remembers it.
    CharacterLibrary again(ac::CharacterStore(fs::path(f.dir->filePath(QStringLiteral("characters")).toStdString())),
                           std::make_unique<FakeTransport>());
    EXPECT_EQ(again.setAside(), QSet<QString>{QStringLiteral("gpt")});
    f.library->download(QStringLiteral("gpt"));
    ASSERT_TRUE(f.settle());
    EXPECT_NE(f.library->installed(QStringLiteral("gpt")), nullptr);
    EXPECT_TRUE(f.library->setAside().isEmpty());
}

TEST(CharacterLibrary, StopSetsAsideWhatIsLeft) {
    Fixture f;
    f.transport->delayMs = 200;
    f.library->downloadAll({});
    ASSERT_TRUE(pump([&] { return !f.library->autoCurrent().isEmpty(); }));
    EXPECT_FALSE(f.library->autoStatus().isEmpty());
    f.library->stopAutomatic();
    ASSERT_TRUE(f.settle());
    QSet<QString> expected;
    for (const char* id : {"claude", "gpt", "kimi"}) {
        if (!f.library->installed(QString::fromLatin1(id))) expected.insert(QString::fromLatin1(id));
    }
    EXPECT_EQ(f.library->setAside(), expected);
    EXPECT_LE(f.library->installed().size(), 1u);
    EXPECT_EQ(f.transport->order.size(), 1);
}

TEST(CharacterLibrary, ANetworkFailureEndsTheRun) {
    Fixture f;
    f.transport->failNetwork = true;
    f.library->downloadAll({});
    ASSERT_TRUE(f.settle());
    EXPECT_EQ(f.transport->order.size(), 1);
    EXPECT_EQ(f.library->errors().begin()->second, QStringLiteral("characters.error.network"));
    EXPECT_TRUE(f.library->setAside().isEmpty());
    // The next trigger starts again.
    f.transport->failNetwork = false;
    f.library->downloadAll({});
    ASSERT_TRUE(f.settle());
    EXPECT_EQ(f.installed().size(), 3u);
}

TEST(CharacterLibrary, APackFailingItsChecksIsSkippedForTheRun) {
    Fixture f;
    f.library->refreshForList();
    ASSERT_TRUE(pump([&] { return f.library->catalog().has_value(); }));
    QFile file(f.transport->archives[QStringLiteral("claude")]);
    ASSERT_TRUE(file.open(QIODevice::ReadWrite));
    file.seek(10);
    file.write("X");
    file.close();
    f.library->downloadAll({});
    ASSERT_TRUE(f.settle());
    EXPECT_EQ(f.transport->order, (QStringList{QStringLiteral("claude"), QStringLiteral("gpt"), QStringLiteral("kimi")}));
    EXPECT_EQ(f.installed(), (std::set<std::string>{"gpt", "kimi"}));
    EXPECT_EQ(f.library->errors().at(QStringLiteral("claude")), QStringLiteral("characters.error.checksum"));
}
