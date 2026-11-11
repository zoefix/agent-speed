#include <fcntl.h>
#include <gtest/gtest.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <memory>

#include "agentspeed/quota/cli_quota.hpp"
#include "agentspeed/quota/ordered_json.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/rfc3339.hpp"
#include "fake_home.hpp"
#include "fixture_support.hpp"
#include "quota_test_support.hpp"
#include "relay_process.hpp"

// REMOTE 10 with the built executable: the status line helper by its name (REMOTE 11.3, QUOTAS 5.5), the host's Claude
// Code bridge turned on, checked, replaced and off over `serve` (QUOTAS 5.2–5.4), and the QUOTAS 10 Codex cases from
// rollouts in a fake home to the `quota` line the app decodes.
using namespace agentspeed;
using namespace relay_test;
namespace fs = std::filesystem;

namespace {

const std::string kHost = "8C0D6B0A-1111-4222-8333-444455556666";
constexpr auto kWait = std::chrono::seconds(20);

std::int64_t nowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

std::string readFile(const fs::path& path) { return fixtures::readText(path); }

void writeFile(const fs::path& path, const std::string& text) {
    fs::create_directories(path.parent_path());
    std::ofstream(path, std::ios::binary) << text;
}

struct Finished {
    int code = -1;
    std::string out;
};

// Runs `executable` (argv[0] is its path) in `cwd` with `input` on its standard input, to its end.
Finished run(const std::string& executable, const std::vector<std::string>& args, const std::string& input, const fs::path& cwd,
        const std::vector<std::string>& environment) {
    int in[2], out[2];
    if (::pipe(in) != 0 || ::pipe(out) != 0) return {};
    std::vector<std::string> argv{executable};
    argv.insert(argv.end(), args.begin(), args.end());
    std::vector<char*> argp, envp;
    for (auto& a : argv) argp.push_back(a.data());
    argp.push_back(nullptr);
    auto env = environment;
    for (auto& e : env) envp.push_back(e.data());
    envp.push_back(nullptr);
    const pid_t pid = ::fork();
    if (pid == 0) {
        if (::chdir(cwd.c_str()) != 0) ::_exit(126);
        ::dup2(in[0], STDIN_FILENO);
        ::dup2(out[1], STDOUT_FILENO);
        for (const int fd : {in[0], in[1], out[0], out[1]}) ::close(fd);
        ::execve(executable.c_str(), argp.data(), envp.data());
        ::_exit(127);
    }
    ::close(in[0]);
    ::close(out[1]);
    [[maybe_unused]] const auto written = ::write(in[1], input.data(), input.size());
    ::close(in[1]);
    Finished result;
    char buffer[4096];
    for (auto n = ::read(out[0], buffer, sizeof buffer); n > 0; n = ::read(out[0], buffer, sizeof buffer)) {
        result.out.append(buffer, static_cast<std::size_t>(n));
    }
    ::close(out[0]);
    int status = 0;
    ::waitpid(pid, &status, 0);
    result.code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    return result;
}

// A relay line with what the app makes of it; every line must pass its validation (5.6).
struct Line {
    std::string text;
    json::Document doc;
    std::optional<remote::RelayMessage> message;
    explicit Line(std::string t) : text(std::move(t)) {
        EXPECT_TRUE(doc.parse(text)) << text;
        static const remote::LineDecoder decoder(kHost);
        auto outcome = decoder.decode(text);
        if (const auto* error = std::get_if<remote::LineError>(&outcome)) ADD_FAILURE() << "the app refuses " << error->field << ": " << text;
        if (auto* m = std::get_if<remote::RelayMessage>(&outcome)) message = std::move(*m);
    }
    std::string type() const { return doc.root()["t"].string().value_or(""); }
};

// The first line of type `t` that `accept` takes; earlier lines are passed over.
std::unique_ptr<Line> waitFor(RelayProcess& relay, const std::string& t, const std::function<bool(const Line&)>& accept = nullptr,
                              std::chrono::milliseconds timeout = kWait) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (true) {
        const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
        if (left.count() <= 0) return nullptr;
        const auto text = relay.readLine(left);
        if (!text) return nullptr;
        auto line = std::make_unique<Line>(*text);
        if (line->type() == t && (!accept || accept(*line))) return line;
    }
}

std::string bridgeState(const Line& line) { return line.doc.root()["state"].string().value_or(""); }

const remote::HostQuota* quotaOf(const Line& line) {
    return line.message ? std::get_if<remote::HostQuota>(&*line.message) : nullptr;
}

// A host: a home, the relay's folder, and `serve` started with a quota config.
class Host {
public:
    Host() : temp_(makeTempDir("agentspeed-host")) {
        home = temp_ / "home";
        folder = temp_ / "relay";
        fs::create_directories(home);
        fs::create_directories(folder);
    }
    ~Host() {
        relay.reset();
        fs::remove_all(temp_);
    }

    void start(const std::string& quota, const std::string& sources = R"(["codex","claudeCode"])") {
        relay = std::make_unique<RelayProcess>(AGENTSPEED_RELAY_BINARY, std::vector<std::string>{"serve", "--folder", folder.string()},
                                               std::vector<std::string>{"HOME=" + home.string(), "LC_ALL=C", "PATH=/usr/bin:/bin"});
        std::string line = R"({"t":"start","protocol":1,"todayStartMs":)" + std::to_string(nowMs() - 3'600'000) + R"(,"sources":)" + sources +
                           R"(,"zone":{"id":"UTC","tzif":""},"prices":{})";
        if (!quota.empty()) line += ",\"quota\":" + quota;
        ASSERT_TRUE(relay->send(line + "}"));
    }
    void stop() {
        relay->closeInput();
        EXPECT_EQ(relay->wait(std::chrono::seconds(10)), 0);
        relay.reset();
    }
    void configure(const std::string& config) { ASSERT_TRUE(relay->send(R"({"t":"quota","config":)" + config + "}")); }

    std::string settings() const { return readFile(home / ".claude/settings.json"); }
    void writeSettings(const std::string& text) const { writeFile(home / ".claude/settings.json", text); }
    fs::path quotaFolder() const { return folder / "quota"; }
    std::string command() const {
        return "'" + folder.string() + "/agent-speed-statusline' --folder '" + folder.string() + "'";
    }

    fs::path home, folder;
    std::unique_ptr<RelayProcess> relay;

private:
    fs::path temp_;
};

const std::string kBridgeOn = R"({"sources":["claudeCode"],"network":false,"grokLive":false,"claudeBridge":true})";
const std::string kBridgeOff = R"({"sources":[],"network":false,"grokLive":false,"claudeBridge":false})";

const std::string kOriginal = "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"echo previous\",\n"
                              "    \"padding\": 2\n  },\n  \"env\": {}\n}\n";

std::string rateLimits(int fiveHour, std::int64_t fiveHourReset, int sevenDay, std::int64_t sevenDayReset) {
    return R"({"session_id":"s","rate_limits":{"five_hour":{"used_percentage":)" + std::to_string(fiveHour) + R"(,"resets_at":)" +
           std::to_string(fiveHourReset) + R"(},"seven_day":{"used_percentage":)" + std::to_string(sevenDay) + R"(,"resets_at":)" +
           std::to_string(sevenDayReset) + "}}}";
}

}  // namespace

// ---- The helper by its name (REMOTE 3.2, 11.3) ----

class RelayHelperMode : public ::testing::Test {
protected:
    void SetUp() override {
        temp_ = makeTempDir("agentspeed-helper");
        fs::create_directories(temp_ / "bin");
        fs::create_symlink(AGENTSPEED_RELAY_BINARY, temp_ / "bin/agent-speed-statusline");
        folder_ = temp_ / "data";
        fs::create_directories(temp_ / "work");
    }
    void TearDown() override { fs::remove_all(temp_); }

    Finished helper(const std::string& input, const std::vector<std::string>& environment = {"LC_ALL=C"}) const {
        return run((temp_ / "bin/agent-speed-statusline").string(), {"--folder", folder_.string()}, input, temp_ / "work", environment);
    }
    quota::cli::Stored stored() const { return quota::cli::parseFile(readFile(folder_ / "quota/claude-cli.json")); }

    fs::path temp_, folder_;
};

TEST_F(RelayHelperMode, MergesTheRateLimitsAndPrintsTheLine) {
    // Far-future resets, so the line does not depend on today's date.
    auto result = helper(rateLimits(6, 4102000000, 86, 4102400000));
    EXPECT_EQ(result.code, 0);
    EXPECT_EQ(result.out, "5h 6% \xC2\xB7 7d 86%\n");
    struct stat info {};
    ASSERT_EQ(::stat((folder_ / "quota/claude-cli.json").c_str(), &info), 0);
    EXPECT_EQ(info.st_mode & 0777, 0600u);
    ASSERT_TRUE(stored().receivedMs);

    // The same window within 60 s: the higher value wins; a missing window is kept.
    result = helper(R"({"rate_limits":{"five_hour":{"used_percentage":4,"resets_at":4102000030}}})");
    EXPECT_EQ(result.out, "5h 6% \xC2\xB7 7d 86%\n");
    result = helper(R"({"rate_limits":{"five_hour":{"used_percentage":9,"resets_at":4102000030}}})");
    EXPECT_EQ(result.out, "5h 9% \xC2\xB7 7d 86%\n");
    // A later window replaces it; an earlier one is ignored.
    result = helper(R"({"rate_limits":{"five_hour":{"used_percentage":1,"resets_at":4102018000}}})");
    EXPECT_EQ(result.out, "5h 1% \xC2\xB7 7d 86%\n");
    result = helper(R"({"rate_limits":{"five_hour":{"used_percentage":50,"resets_at":4101990000}}})");
    EXPECT_EQ(result.out, "5h 1% \xC2\xB7 7d 86%\n");
    // Input that is not JSON changes nothing and prints only the line.
    result = helper("not json at all");
    EXPECT_EQ(result.code, 0);
    EXPECT_EQ(result.out, "5h 1% \xC2\xB7 7d 86%\n");
}

TEST_F(RelayHelperMode, FollowsTheFixtureMerges) {
    const auto cases = quota_test::section("helper");
    for (std::uint32_t k = 0; k < cases.size(); ++k) {
        SCOPED_TRACE("helper case " + std::to_string(k));
        const auto c = cases.at(k);
        EXPECT_EQ(helper(fixtures::compactJson(c["input"])).code, 0);
        const auto want = quota::cli::parseFile(fixtures::compactJson(c["expected"]));
        const auto got = stored();
        EXPECT_EQ(got.fiveHour, want.fiveHour);
        EXPECT_EQ(got.sevenDay, want.sevenDay);
    }
}

TEST_F(RelayHelperMode, RunsThePreviousCommandWithTheSameInputEnvironmentAndFolder) {
    writeFile(folder_ / "quota/claude-statusline-previous.json",
              R"({"version":1,"statusLine":{"type":"command","command":"printf '%s|%s|' \"$(pwd -P)\" \"$AS_TEST\"; cat; exit 3"}})");
    const auto input = rateLimits(6, 4102000000, 86, 4102400000);
    const auto result = helper(input, {"LC_ALL=C", "AS_TEST=hello", "PATH=/usr/bin:/bin"});
    EXPECT_EQ(result.code, 3);
    EXPECT_EQ(result.out, fs::canonical(temp_ / "work").string() + "|hello|" + input);
    // The quota is stored all the same.
    EXPECT_TRUE(stored().fiveHour);
}

TEST_F(RelayHelperMode, OtherNamesAreTheRelay) {
    fs::create_symlink(AGENTSPEED_RELAY_BINARY, temp_ / "bin/agent-speed-statusline-old");
    const auto result = run((temp_ / "bin/agent-speed-statusline-old").string(), {"--folder", folder_.string()}, "{}", temp_, {"LC_ALL=C"});
    EXPECT_EQ(result.code, 2);
    EXPECT_FALSE(fs::exists(folder_ / "quota"));
}

// ---- The bridge over `serve` (QUOTAS 5.2–5.4 on a host) ----

TEST(RelayBridge, TurnsOnFeedsTheQuotaAndTurnsOff) {
    Host host;
    host.writeSettings(kOriginal);
    host.start(kBridgeOn);
    auto line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "waiting");
    EXPECT_EQ(line->doc.root()["settingsPath"].string(), "~/.claude/settings.json");

    // Our command where the line was, its padding kept, every other member as it was.
    EXPECT_EQ(host.settings(), "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"" + host.command() +
                                   "\",\n    \"padding\": 2\n  },\n  \"env\": {}\n}\n");
    EXPECT_EQ(readFile(host.folder / "agent-speed-statusline"), readFile(AGENTSPEED_RELAY_BINARY));
    struct stat info {};
    ASSERT_EQ(::stat((host.folder / "agent-speed-statusline").c_str(), &info), 0);
    EXPECT_EQ(info.st_mode & 0777, 0700u);
    EXPECT_EQ(readFile(host.quotaFolder() / "claude-settings.backup.json"), kOriginal);
    const auto sidecar = quota::ordered_json::parse(readFile(host.quotaFolder() / "claude-statusline-previous.json"));
    ASSERT_TRUE(sidecar && sidecar->find("statusLine"));
    EXPECT_EQ(sidecar->find("statusLine")->find("command")->text, "echo previous");

    // Claude Code runs the status line: the previous line still shows, and the quota reaches the app.
    const auto shown = run("/bin/sh", {"-c", host.command()}, rateLimits(6, 4102000000, 86, 4102400000), host.home,
                           {"LC_ALL=C", "PATH=/usr/bin:/bin", "HOME=" + host.home.string()});
    EXPECT_EQ(shown.code, 0);
    EXPECT_EQ(shown.out, "previous\n");
    host.configure(kBridgeOn);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "ok");
    line = waitFor(*host.relay, "quota", [](const Line& l) {
        const auto* q = quotaOf(l);
        return q && q->snapshot.source == SourceKind::ClaudeCode && !q->snapshot.windows.empty();
    });
    ASSERT_TRUE(line);
    const auto& windows = quotaOf(*line)->snapshot.windows;
    ASSERT_EQ(windows.size(), 2u);
    EXPECT_EQ(windows[0].kind, quota::Kind::FiveHour);
    EXPECT_DOUBLE_EQ(windows[0].usedPercent, 6);
    EXPECT_EQ(windows[1].resetsAtMs, 4102400000000);

    // Off: the previous line back exactly, our files gone.
    host.configure(kBridgeOff);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "off");
    EXPECT_EQ(host.settings(), kOriginal);
    for (const auto* name : {"claude-statusline-previous.json", "claude-settings.backup.json", "claude-cli.json"}) {
        EXPECT_FALSE(fs::exists(host.quotaFolder() / name)) << name;
    }
    EXPECT_TRUE(waitFor(*host.relay, "quotaRemoved"));
    host.stop();
}

TEST(RelayBridge, LeavesALineTheUserSetAlone) {
    Host host;
    host.writeSettings("{\n  \"model\": \"opus\"\n}\n");
    host.start(kBridgeOn);
    auto line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "waiting");
    const std::string mine = "{\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"my-line\"\n  }\n}\n";
    host.writeSettings(mine);
    host.configure(kBridgeOn);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "replaced");
    // *Reconnect* sends off, then on: the user's line stays the previous one.
    host.configure(kBridgeOff);
    host.configure(kBridgeOn);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "off");
    EXPECT_EQ(host.settings(), mine);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "waiting");
    EXPECT_NE(host.settings().find(host.command()), std::string::npos);
    host.stop();

    // The user replaces it again while the app is away; the next session leaves it so.
    host.writeSettings(mine);
    host.start(kBridgeOn);
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "replaced");
    EXPECT_EQ(host.settings(), mine);
    host.stop();

    // Switched off while away: the next session cleans up and leaves the user's line.
    host.start(R"({"sources":[],"network":false,"grokLive":false,"claudeBridge":false})");
    line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "off");
    EXPECT_EQ(host.settings(), mine);
    EXPECT_FALSE(fs::exists(host.quotaFolder() / "claude-statusline-previous.json"));
    host.stop();
}

TEST(RelayBridge, RestoresALeftOverBridgeWhenTheAppSaysOff) {
    Host host;
    host.writeSettings(kOriginal);
    host.start(kBridgeOn);
    ASSERT_TRUE(waitFor(*host.relay, "quotaBridge"));
    host.stop();
    host.start(kBridgeOff);
    const auto line = waitFor(*host.relay, "quotaBridge");
    ASSERT_TRUE(line);
    EXPECT_EQ(bridgeState(*line), "off");
    EXPECT_EQ(host.settings(), kOriginal);
    host.stop();
}

TEST(RelayBridge, ChangesNothingInSettingsItCannotRead) {
    for (const std::string& text : {std::string("\xEF\xBB\xBF{}\n"), std::string("[1]\n"), std::string("{\"a\": \"\xFF\"}\n")}) {
        Host host;
        host.writeSettings(text);
        host.start(kBridgeOn);
        const auto line = waitFor(*host.relay, "quotaBridge");
        ASSERT_TRUE(line);
        EXPECT_EQ(bridgeState(*line), "unreadable");
        EXPECT_EQ(host.settings(), text);
        EXPECT_FALSE(fs::exists(host.quotaFolder() / "claude-statusline-previous.json"));
        host.stop();
    }
}

TEST(RelayBridge, AnAppWithoutQuotasGetsNoQuotaLines) {
    Host host;
    host.writeSettings(kOriginal);
    host.start("");
    ASSERT_TRUE(waitFor(*host.relay, "status"));
    EXPECT_FALSE(waitFor(*host.relay, "quotaBridge", nullptr, std::chrono::milliseconds(1500)));
    EXPECT_EQ(host.settings(), kOriginal);
    host.stop();
}

// ---- Codex (QUOTAS 3 and 10) through `serve` ----

namespace {

std::string sessionMeta(const std::string& timestamp, const std::string& id) {
    return R"({"timestamp":")" + timestamp + R"(","type":"session_meta","payload":{"id":")" + id + R"(","cwd":"/w"}})";
}

std::string isoOf(std::int64_t ms) {
    const std::time_t seconds = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    gmtime_r(&seconds, &tm);
    char out[32];
    std::strftime(out, sizeof out, "%Y-%m-%dT%H:%M:%SZ", &tm);
    return out;
}

}  // namespace

TEST(RelayCodexQuota, EveryFixtureCaseFromRolloutsToTheQuotaLine) {
    for (const auto& c : quota_test::section("codex").elements()) {
        SCOPED_TRACE(*c["name"].string());
        Host host;
        const auto sessions = host.home / ".codex/sessions/2026/11/10";
        std::string main = sessionMeta("2020-01-01T00:00:00Z", "main") + "\n";
        int forks = 0;
        for (const auto& line : c["lines"].elements()) {
            const auto text = fixtures::compactJson(line["line"]) + "\n";
            if (line["copied"].isTrue()) {
                // A fork copies its parent's records: its session_meta is newer than the copied line (METRICS 3.5).
                const auto copiedAt = *parseTimestamp(*line["line"]["timestamp"].string());
                const auto fork = std::to_string(++forks);
                writeFile(sessions / ("rollout-2026-11-10T00-00-0" + fork + "-fork.jsonl"),
                          sessionMeta(isoOf(copiedAt + 3'600'000), "fork" + fork) + "\n" + text);
            } else {
                main += text;
            }
        }
        writeFile(sessions / "rollout-2026-11-10T00-00-00-main.jsonl", main);
        writeFile(host.home / ".codex/auth.json", R"({"auth_mode":"chatgpt"})");
        host.start(R"({"sources":["codex"],"network":false,"grokLive":false,"claudeBridge":false})", R"(["codex"])");
        const auto line = waitFor(*host.relay, "quota");
        ASSERT_TRUE(line);
        const auto* q = quotaOf(*line);
        ASSERT_TRUE(q);
        EXPECT_EQ(q->snapshot.source, SourceKind::Codex);
        EXPECT_TRUE(q->signedIn);
        EXPECT_EQ(q->status, remote::HostQuota::Status::Ok);
        const auto expected = c["expected"];
        if (expected.isNull()) {
            EXPECT_TRUE(q->snapshot.windows.empty());
        } else {
            EXPECT_EQ(q->snapshot.observedMs, *expected["observedMs"].truncated());
            quota_test::expectWindows(q->snapshot.windows, expected["windows"]);
        }
        host.stop();
    }
}
