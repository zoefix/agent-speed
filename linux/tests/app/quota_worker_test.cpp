#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <functional>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/codex.hpp"
#include "quota/quota_worker.hpp"

// QUOTAS 9.1 in the app's worker: a source is read only while it is enabled in Data sources and its quota switch is
// on — Codex's collector, Claude Desktop's cache and the Claude CLI quota file included — and nothing of a disabled
// source is published. The test home is the app tests' scratch $HOME.
namespace {

namespace quota = agentspeed::quota;
using agentspeed::SourceKind;

bool pump(const std::function<bool()>& done, int ms = 5000) {
    QElapsedTimer timer;
    timer.start();
    while (!done()) {
        if (timer.elapsed() > ms) return false;
        QCoreApplication::processEvents(QEventLoop::AllEvents, 20);
    }
    return true;
}

QString home() { return qEnvironmentVariable("HOME"); }

void write(const QString& path, const QByteArray& bytes) {
    QDir().mkpath(QFileInfo(path).absolutePath());
    QFile file(path);
    ASSERT_TRUE(file.open(QIODevice::WriteOnly | QIODevice::Truncate));
    file.write(bytes);
}

const QuotaSourceState* find(const QuotaPublication& p, SourceKind source) {
    for (const auto& s : p.sources) {
        if (s.source == source) return &s;
    }
    return nullptr;
}

bool hasWindows(const QuotaPublication& p, SourceKind source) {
    const auto* s = find(p, source);
    return s && s->snapshot && !s->snapshot->windows.empty();
}

class QuotaWorkerGate : public ::testing::Test {
protected:
    void SetUp() override {
        QFile fixture(QStringLiteral(AGENTSPEED_SPEC_DIR "/fixtures/quota/zstd-ultra.bin"));
        ASSERT_TRUE(fixture.open(QIODevice::ReadOnly));
        write(home() + QStringLiteral("/.config/Claude/Cache/Cache_Data/383706cf5aea4bcc_0"), fixture.readAll());
        write(home() + QStringLiteral("/.local/share/agent-speed/quota/claude-cli.json"),
              R"({"version":1,"receivedMs":1794338685000,"windows":{"five_hour":{"used_percentage":6,"resets_at":1794354000},)"
              R"("seven_day":{"used_percentage":86,"resets_at":1794448800}}})");
        codex_ = std::make_shared<quota::CodexCollector>();
        worker_ = std::make_unique<QuotaWorker>(codex_, QStringLiteral("/nonexistent/agent-speed-statusline"));
        QObject::connect(worker_.get(), &QuotaWorker::published, [this](const QuotaPublication& p) {
            last_ = p;
            ++publications_;
        });
        worker_->start();
    }
    void TearDown() override {
        worker_.reset();
        QDir(home() + QStringLiteral("/.config/Claude")).removeRecursively();
        QDir(home() + QStringLiteral("/.local/share/agent-speed")).removeRecursively();
    }
    void observeCodex() {
        agentspeed::json::Document doc;
        ASSERT_TRUE(doc.parse(R"({"limit_id":"codex","primary":{"used_percent":23.0,"window_minutes":10080,"resets_at":1794897479}})"));
        codex_->observe(doc.root(), 1794337882318);
    }
    void apply(QuotaWorkerSettings settings) {
        const int before = publications_;
        worker_->setSettings(std::move(settings));
        pump([&] { return publications_ > before; }, 1500);
    }

    std::shared_ptr<quota::CodexCollector> codex_;
    std::unique_ptr<QuotaWorker> worker_;
    QuotaPublication last_;
    int publications_ = 0;
};

TEST_F(QuotaWorkerGate, ReadsEveryEnabledSourceWithItsSwitchOn) {
    QuotaWorkerSettings s;
    s.enabled = {SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::ClaudeDesktop};
    s.switchesOn = {"quotaCodex", "quotaClaudeDesktop", "quotaClaudeCli"};
    apply(s);
    observeCodex();
    ASSERT_TRUE(pump([&] { return hasWindows(last_, SourceKind::Codex) && hasWindows(last_, SourceKind::ClaudeDesktop) &&
                                  hasWindows(last_, SourceKind::ClaudeCode); }));
    EXPECT_EQ(find(last_, SourceKind::ClaudeCode)->snapshot->observedMs, 1794338685000);
    EXPECT_EQ(find(last_, SourceKind::Codex)->snapshot->windows.front().usedPercent, 23.0);
}

TEST_F(QuotaWorkerGate, ADisabledSourceReadsAndShowsNothing) {
    QuotaWorkerSettings s;
    s.enabled = {SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::ClaudeDesktop};
    s.switchesOn = {"quotaCodex", "quotaClaudeDesktop", "quotaClaudeCli"};
    apply(s);
    observeCodex();
    ASSERT_TRUE(pump([&] { return hasWindows(last_, SourceKind::ClaudeDesktop) && hasWindows(last_, SourceKind::Codex); }));

    // Claude Code off in Data sources (Claude Desktop shares its switch on Linux): neither Claude row remains.
    s.enabled = {SourceKind::Codex};
    apply(s);
    ASSERT_TRUE(pump([&] { return !find(last_, SourceKind::ClaudeDesktop) && !find(last_, SourceKind::ClaudeCode); }));
    EXPECT_TRUE(hasWindows(last_, SourceKind::Codex));

    // Codex's switch off: the collector forgets and drops what the parsers still hand it.
    s.switchesOn = {"quotaClaudeDesktop", "quotaClaudeCli"};
    apply(s);
    ASSERT_TRUE(pump([&] { return !find(last_, SourceKind::Codex); }));
    EXPECT_FALSE(codex_->snapshot());
    observeCodex();
    EXPECT_FALSE(codex_->snapshot());

    // Enabled again: the cache is searched again and the next Codex line counts.
    s.enabled = {SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::ClaudeDesktop};
    s.switchesOn = {"quotaCodex", "quotaClaudeDesktop"};
    apply(s);
    observeCodex();
    ASSERT_TRUE(pump([&] { return hasWindows(last_, SourceKind::ClaudeDesktop) && hasWindows(last_, SourceKind::Codex); }));
    // The bridge is off: no Claude Code row and no CLI status.
    EXPECT_FALSE(find(last_, SourceKind::ClaudeCode));
    EXPECT_EQ(last_.cliState, QStringLiteral("off"));
}

TEST_F(QuotaWorkerGate, PhaseTwoSourcesFollowTheSameRule) {
    QuotaWorkerSettings s;
    s.enabled = {SourceKind::Kimi, SourceKind::Gemini};
    s.switchesOn = {"quotaKimi", "quotaGemini", "quotaDroid"};
    s.networkEnabled = true;
    apply(s);
    ASSERT_TRUE(pump([&] { return find(last_, SourceKind::Kimi) && find(last_, SourceKind::Gemini); }));
    // Droid's switch is on but the source is disabled: no row.
    EXPECT_FALSE(find(last_, SourceKind::Droid));
    s.enabled = {SourceKind::Gemini};
    apply(s);
    ASSERT_TRUE(pump([&] { return !find(last_, SourceKind::Kimi); }));
    EXPECT_FALSE(last_.statuses.contains(QStringLiteral("quotaKimi")));
}

}  // namespace
