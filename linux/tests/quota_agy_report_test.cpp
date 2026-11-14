#include <gtest/gtest.h>
#include <fcntl.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <thread>

#include "agentspeed/quota/routes/agy_report.hpp"
#include "fixture_support.hpp"
#include "quota_service_support.hpp"

// QUOTAS 9.4 CLI usage report on Linux, with a shell script standing in for agy (a real agy is never run): the
// version gate and its cache, the exact command, the sandbox it runs in, the output cap, the process group killed on
// a timeout and after a normal end, and the route's fallback from servers that give no windows.
namespace {

namespace fs = std::filesystem;
using agentspeed::quota::AgyReporter;
using agentspeed::quota::AgyReportLimits;
using agentspeed::quota::AgyReportResult;
using agentspeed::quota::AntigravityServer;
using agentspeed::quota::VendorEnvironment;
using namespace quota2_test;

const fixtures::JsonFile& cases() {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-cases.json");
    return file;
}

// The fixture report agy 1.5.9 printed (2026-11-14), and its windows.
agentspeed::json::Value observedCase() { return cases().root()["vendors"]["antigravityCli"].at(0); }

std::int64_t wallMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

template <typename Done>
bool waitFor(Done done, int timeoutMs = 15'000) {
    const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (std::chrono::steady_clock::now() < until) {
        if (done()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return done();
}

// Killed, or a zombie waiting for its new parent to reap it.
bool gone(pid_t pid) {
    return waitFor(
        [pid] {
            std::ifstream stat("/proc/" + std::to_string(pid) + "/stat");
            std::string line;
            if (!std::getline(stat, line)) return true;
            const auto close = line.rfind(')');
            return close != std::string::npos && close + 2 < line.size() && line[close + 2] == 'Z';
        },
        3'000);
}

// A home with a stand-in agy that logs what it sees to `agy.log`: `version` for each `--version`, `report` and
// `key=value` lines for each report, then runs `body`. The stubs it may run are only ever those in its own folder.
struct FakeAgy {
    TempHome home;
    FakeAgy() {
        fs::create_directories(home.file("tmp"));
        home.write("report.json", *observedCase()["stdout"].string());
    }

    std::string log() const { return home.file("agy.log"); }

    void install(std::string_view version, std::string_view body = {}, const std::string& relative = ".local/bin/agy") const {
        const std::string report = body.empty() ? "cat '" + home.file("report.json") + "'" : std::string(body);
        std::string script = "#!/bin/sh\nlog='" + log() + "'\n";
        script += "if [ \"$1\" = \"--version\" ]; then\n  echo version >> \"$log\"\n  " + std::string(version) + "\n  exit 0\nfi\n";
        script += R"sh({
  echo report
  echo "args=$*"
  here=$(pwd)
  echo "cwd=$here"
  echo "mode=$(stat -c %a .)"
  echo "stdin=$(readlink /proc/$$/fd/0)"
  echo "path=$PATH"
  echo "browser=$BROWSER"
  echo "update=$AGY_CLI_DISABLE_AUTO_UPDATE"
  echo "xdg=$(command -v xdg-open)"
  echo "open=$(command -v open)"
  case "$BROWSER" in "$here"/*) "$BROWSER" https://accounts.invalid/; echo "browserExit=$?";; esac
  case "$(command -v xdg-open)" in "$here"/*) xdg-open https://accounts.invalid/; echo "xdgExit=$?";; esac
  echo "stubMode=$(stat -c %a "$here/open") $(stat -c %a "$here/xdg-open")"
  if [ -e /proc/$$/fd/77 ]; then echo fd77=open; else echo fd77=closed; fi
} >> "$log" 2>&1
)sh";
        script += report + "\n";
        home.write(relative, script);
        fs::permissions(home.file(relative), fs::perms::owner_all | fs::perms::group_read | fs::perms::group_exec, fs::perm_options::replace);
    }

    std::vector<std::string> lines() const {
        std::ifstream in(log());
        std::vector<std::string> out;
        for (std::string line; std::getline(in, line);) out.push_back(line);
        return out;
    }
    int count(std::string_view word) const {
        int n = 0;
        for (const auto& line : lines()) n += line == word ? 1 : 0;
        return n;
    }
    // The last report's `key=value` lines.
    std::map<std::string, std::string> seen() const {
        std::map<std::string, std::string> out;
        for (const auto& line : lines()) {
            if (line == "report") out.clear();
            const auto equals = line.find('=');
            if (equals != std::string::npos) out[line.substr(0, equals)] = line.substr(equals + 1);
        }
        return out;
    }

    VendorEnvironment env(const std::string& path = "/usr/bin:/bin") const {
        auto out = VendorEnvironment::fixed(home.path.string(), {{"PATH", path}});
        out.tempFolder = home.file("tmp");
        return out;
    }
    bool tmpEmpty() const { return fs::is_empty(home.file("tmp")); }
};

TEST(QuotaAgyReport, RunsTheReportInASandboxAndRemovesItsFolder) {
    FakeAgy agy;
    agy.install("echo 1.5.9");
    // A descriptor the app holds without close-on-exec: the child never gets it.
    const int null = ::open("/dev/null", O_RDONLY);
    ASSERT_GE(::dup2(null, 77), 0);
    ::close(null);
    AgyReporter reporter(agy.env());
    EXPECT_TRUE(reporter.available());
    const auto before = wallMs();
    const auto result = reporter.run();
    const auto after = wallMs();
    ::close(77);
    ASSERT_TRUE(result.ok);
    expectWindows(result.windows, observedCase()["expected"]);
    EXPECT_GE(result.observedMs, before);
    EXPECT_LE(result.observedMs, after);

    const auto seen = agy.seen();
    const auto cwd = seen.at("cwd");
    EXPECT_EQ(seen.at("args"), "-p /usage --output-format json --print-timeout 90s");
    EXPECT_EQ(cwd.rfind(agy.home.file("tmp") + "/agent-speed-agy-", 0), 0u) << cwd;
    EXPECT_EQ(seen.at("mode"), "700");
    EXPECT_EQ(seen.at("stdin"), "/dev/null");
    EXPECT_EQ(seen.at("path"), cwd + ":/usr/bin:/bin");
    EXPECT_EQ(seen.at("browser"), cwd + "/open");
    EXPECT_EQ(seen.at("update"), "true");
    EXPECT_EQ(seen.at("xdg"), cwd + "/xdg-open");
    EXPECT_EQ(seen.at("open"), cwd + "/open");
    EXPECT_EQ(seen.at("browserExit"), "1");
    EXPECT_EQ(seen.at("xdgExit"), "1");
    EXPECT_EQ(seen.at("stubMode"), "700 700");
    EXPECT_EQ(seen.at("fd77"), "closed");
    EXPECT_FALSE(fs::exists(cwd));
    EXPECT_TRUE(agy.tmpEmpty());
    EXPECT_EQ(agy.count("version"), 1);
}

TEST(QuotaAgyReport, AsksTheVersionOncePerBinaryAndNeedsAtLeast1_1_11) {
    FakeAgy agy;
    agy.install("echo 1.5.9");
    AgyReporter reporter(agy.env());
    EXPECT_TRUE(reporter.run().ok);
    EXPECT_TRUE(reporter.run().ok);
    EXPECT_EQ(agy.count("version"), 1);
    EXPECT_EQ(agy.count("report"), 2);

    // Another binary (size and modification time): asked again. Older than 1.1.11: no report, a failure, and the
    // answer is kept.
    agy.install("echo 1.1.10  # older");
    agy.home.touch(".local/bin/agy", 5);
    EXPECT_FALSE(reporter.run().ok);
    EXPECT_FALSE(reporter.run().ok);
    EXPECT_EQ(agy.count("version"), 2);
    EXPECT_EQ(agy.count("report"), 2);

    // No version in the answer: no report, asked again next time.
    agy.install("echo agy 1.5.9");
    agy.home.touch(".local/bin/agy", 10);
    EXPECT_FALSE(reporter.run().ok);
    EXPECT_FALSE(reporter.run().ok);
    EXPECT_EQ(agy.count("version"), 4);
    EXPECT_EQ(agy.count("report"), 2);

    // A version that does not answer in time.
    agy.install("sleep 30");
    agy.home.touch(".local/bin/agy", 15);
    AgyReporter quick(agy.env(), AgyReportLimits{300, 4096, 100'000, 1 << 20});
    const auto start = std::chrono::steady_clock::now();
    EXPECT_FALSE(quick.run().ok);
    EXPECT_LT(std::chrono::steady_clock::now() - start, std::chrono::seconds(3));
    EXPECT_EQ(agy.count("report"), 2);
    EXPECT_TRUE(agy.tmpEmpty());
}

TEST(QuotaAgyReport, FindsTheBinaryInTheHomeElseOnPath) {
    FakeAgy agy;
    // A relative entry and a file that cannot run come before the one on PATH.
    agy.install("echo 1.5.9", {}, "bin2/agy");
    agy.home.write("bin1/agy", "#!/bin/sh\nexit 1\n");
    const auto path = "relative:" + agy.home.file("bin1") + ":" + agy.home.file("bin2") + ":/usr/bin:/bin";
    EXPECT_EQ(agentspeed::quota::agy::binary(agy.env(path)), agy.home.file("bin2/agy"));
    AgyReporter onPath(agy.env(path));
    EXPECT_TRUE(onPath.available());
    EXPECT_TRUE(onPath.run().ok);

    agy.install("echo 1.5.9");
    EXPECT_EQ(agentspeed::quota::agy::binary(agy.env(path)), agy.home.file(".local/bin/agy"));

    FakeAgy none;
    AgyReporter missing(none.env(none.home.file("tmp")));
    EXPECT_FALSE(missing.available());
    EXPECT_FALSE(missing.run().ok);
    EXPECT_TRUE(none.tmpEmpty());
}

TEST(QuotaAgyReport, ReadsAtMostOneMebibyte) {
    FakeAgy agy;
    auto text = *observedCase()["stdout"].string();
    text.resize(1 << 20, ' ');
    agy.home.write("report.json", text);
    agy.install("echo 1.5.9");
    AgyReporter reporter(agy.env());
    EXPECT_TRUE(reporter.run().ok) << "exactly 1 MiB";

    text.push_back(' ');
    agy.home.write("report.json", text);
    EXPECT_FALSE(reporter.run().ok) << "one byte more";

    // Output without end: killed at the cap.
    agy.install("echo 1.5.9", "cat '" + agy.home.file("report.json") + "'; exec yes");
    const auto start = std::chrono::steady_clock::now();
    EXPECT_FALSE(reporter.run().ok);
    EXPECT_LT(std::chrono::steady_clock::now() - start, std::chrono::seconds(5));
    EXPECT_TRUE(agy.tmpEmpty());
}

TEST(QuotaAgyReport, ATimeoutKillsTheWholeProcessGroup) {
    FakeAgy agy;
    const auto pidFile = agy.home.file("grandchild");
    agy.install("echo 1.5.9", "sleep 300 > /dev/null 2>&1 &\necho $! > '" + pidFile + "'\nsleep 300");
    AgyReporter reporter(agy.env(), AgyReportLimits{3'000, 4096, 1'500, 1 << 20});
    const auto start = std::chrono::steady_clock::now();
    EXPECT_FALSE(reporter.run().ok);
    const auto took = std::chrono::steady_clock::now() - start;
    EXPECT_GE(took, std::chrono::milliseconds(1'400));
    EXPECT_LT(took, std::chrono::seconds(5));
    std::ifstream in(pidFile);
    pid_t grandchild = 0;
    ASSERT_TRUE(in >> grandchild);
    EXPECT_TRUE(gone(grandchild)) << "the background sleep outlived the report";
    EXPECT_TRUE(agy.tmpEmpty());
}

TEST(QuotaAgyReport, AfterANormalEndNoDescendantStays) {
    FakeAgy agy;
    const auto pidFile = agy.home.file("grandchild");
    // The background child keeps standard output open: the report still ends with its process.
    agy.install("echo 1.5.9", "sleep 300 &\necho $! > '" + pidFile + "'\ncat '" + agy.home.file("report.json") + "'");
    AgyReporter reporter(agy.env());
    const auto start = std::chrono::steady_clock::now();
    EXPECT_TRUE(reporter.run().ok);
    EXPECT_LT(std::chrono::steady_clock::now() - start, std::chrono::seconds(5));
    std::ifstream in(pidFile);
    pid_t grandchild = 0;
    ASSERT_TRUE(in >> grandchild);
    EXPECT_TRUE(gone(grandchild));
}

TEST(QuotaAgyReport, StartsOffTheCallersThreadAndStopsWhenDestroyed) {
    FakeAgy agy;
    agy.install("echo 1.5.9");
    auto reporter = std::make_unique<AgyReporter>(agy.env());
    std::atomic<int> calls = 0;
    std::atomic<bool> ok = false;
    const auto caller = std::this_thread::get_id();
    std::atomic<bool> elsewhere = false;
    ASSERT_TRUE(reporter->start([&](AgyReportResult result) {
        ok = result.ok;
        elsewhere = std::this_thread::get_id() != caller;
        ++calls;
    }));
    ASSERT_TRUE(waitFor([&] { return calls.load() == 1; }));
    EXPECT_TRUE(ok.load());
    EXPECT_TRUE(elsewhere.load());

    agy.install("echo 1.5.9", "sleep 300");
    ASSERT_TRUE(reporter->start([&](AgyReportResult) { ++calls; }));
    EXPECT_FALSE(reporter->start([&](AgyReportResult) { ++calls; })) << "one at a time";
    ASSERT_TRUE(waitFor([&] { return agy.count("report") == 2; }));
    const auto start = std::chrono::steady_clock::now();
    reporter.reset();
    EXPECT_LT(std::chrono::steady_clock::now() - start, std::chrono::seconds(3));
    EXPECT_EQ(calls.load(), 1);
    EXPECT_TRUE(agy.tmpEmpty());
}

// ---- The route (QUOTAS 9.4 CLI usage report): servers first, the report when they give no windows.

struct ReportFixture {
    FakeAgy agy;
    ServiceFixture f;
    VendorInputs in = ServiceFixture::only(VendorRoute::Antigravity);

    explicit ReportFixture(std::string_view version = "echo 1.5.9", std::string_view body = {}) {
        agy.install(version, body);
        f.agyReport = std::make_shared<AgyReporter>(agy.env());
        f.build({agentspeed::quota::kVendorRoutes.begin(), agentspeed::quota::kVendorRoutes.end()});
    }
    // Ticks until no request or report is in flight.
    const agentspeed::quota::VendorReadings& settle() {
        EXPECT_TRUE(waitFor([&] {
            f.service->tick(in);
            return !f.service->busy();
        }));
        return f.service->tick(in);
    }
};

TEST(QuotaAgyReport, ServersThatAnswerLeaveTheReportUnrun) {
    ReportFixture r;
    r.f.locator->agy = true;
    const auto& readings = r.settle();
    ASSERT_TRUE(readings.snapshot(SourceKind::Antigravity).has_value());
    EXPECT_EQ(readings.snapshot(SourceKind::Antigravity)->windows.at(0).name, "Gemini");
    EXPECT_EQ(r.f.sentTo("RetrieveUserQuotaSummary").size(), 1u);
    EXPECT_FALSE(fs::exists(r.agy.log())) << "neither --version nor the report ran";
}

TEST(QuotaAgyReport, ATokenlessRefusalFallsBackToTheReportAndDoesNotStopTheRoute) {
    ReportFixture r;
    // agy 1.2.2 and later: a server inside agy that only answers with a token it never shows.
    r.f.locator->list = {AntigravityServer{4242, {{"http", 5555, std::nullopt}, {"https", 5555, std::nullopt}}}};
    r.f.locator->agy = true;
    r.f.vendors.statuses["127.0.0.1"] = 401;
    const auto before = wallMs();
    auto readings = r.settle();
    EXPECT_EQ(r.f.sentTo("127.0.0.1").size(), 2u) << "http then https";
    ASSERT_TRUE(readings.snapshot(SourceKind::Antigravity).has_value());
    expectWindows(readings.snapshot(SourceKind::Antigravity)->windows, observedCase()["expected"]);
    const auto observed = readings.snapshot(SourceKind::Antigravity)->observedMs;
    EXPECT_GE(observed, before) << "when the process ended";
    EXPECT_LE(observed, wallMs());
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Ok);
    EXPECT_TRUE(readings.signedIn(SourceKind::Antigravity));
    EXPECT_TRUE(readings.runs(SourceKind::Antigravity));
    EXPECT_EQ(r.agy.count("report"), 1);

    // Five minutes later the route asks again, servers first.
    r.in.nowMs = kNow + 4 * kMinute;
    r.settle();
    EXPECT_EQ(r.f.sentTo("127.0.0.1").size(), 2u);
    r.in.nowMs = kNow + 5 * kMinute;
    readings = r.settle();
    EXPECT_EQ(r.f.sentTo("127.0.0.1").size(), 4u);
    EXPECT_EQ(r.agy.count("report"), 2);
    EXPECT_EQ(r.agy.count("version"), 1);
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Ok);
}

TEST(QuotaAgyReport, AFailedReportWaitsFifteenMinutes) {
    ReportFixture r("echo 1.5.9", "echo 'Please sign in to Antigravity'");
    // No server and no agy running: the open dashboard asks.
    r.f.locator->list.clear();
    r.in.running.clear();
    r.in.dashboardOpen = true;
    auto readings = r.settle();
    EXPECT_TRUE(r.f.transport.sent.empty());
    EXPECT_EQ(r.agy.count("report"), 1);
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Failed);
    EXPECT_FALSE(readings.signedIn(SourceKind::Antigravity));
    EXPECT_FALSE(readings.snapshot(SourceKind::Antigravity).has_value());

    r.in.nowMs = kNow + 5 * kMinute;
    r.settle();
    r.in.nowMs = kNow + 14 * kMinute;
    r.settle();
    EXPECT_EQ(r.agy.count("report"), 1);
    r.in.nowMs = kNow + 15 * kMinute;
    readings = r.settle();
    EXPECT_EQ(r.agy.count("report"), 2);
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Failed);
}

TEST(QuotaAgyReport, AnOlderAgyIsAFailureWithoutAReport) {
    ReportFixture r("echo 1.1.10");
    r.f.locator->list.clear();
    r.f.locator->agy = true;
    const auto& readings = r.settle();
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Failed);
    EXPECT_EQ(r.agy.count("version"), 1);
    EXPECT_EQ(r.agy.count("report"), 0);
}

TEST(QuotaAgyReport, NoReportWithoutARunningAgyOrTheDashboardOrABinary) {
    // A refusing language server with the dashboard closed and no agy: refused as before.
    ReportFixture r;
    r.f.vendors.statuses["127.0.0.1"] = 403;
    auto readings = r.settle();
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::Denied);
    EXPECT_FALSE(fs::exists(r.agy.log()));

    // No server, agy runs, but no binary to run: nothing is asked.
    ReportFixture bare;
    fs::remove(bare.agy.home.file(".local/bin/agy"));
    bare.f.locator->list.clear();
    bare.f.locator->agy = true;
    readings = bare.settle();
    EXPECT_EQ(readings.status(VendorRoute::Antigravity)->kind, VendorStatusKind::NotRunning);
    EXPECT_TRUE(readings.antigravityRuns) << "agy runs";
    EXPECT_TRUE(bare.f.transport.sent.empty());
}

}  // namespace
