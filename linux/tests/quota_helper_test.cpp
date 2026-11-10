#include <fcntl.h>
#include <gtest/gtest.h>
#include <spawn.h>
#include <sys/wait.h>

#include <algorithm>
#include <chrono>

#include "agentspeed/quota/cli_quota.hpp"
#include "agentspeed/quota/statusline_helper.hpp"
#include "quota_test_support.hpp"

// QUOTAS 5.5: the `helper` fixture steps (each from the previous step's stored file), the expired line, the quota file
// reader, the previous-command runner, and the `agent-speed-statusline` executable itself.
using namespace agentspeed;
using namespace agentspeed::quota;
using quota_test::section;
namespace fs = std::filesystem;

extern char** environ;

namespace {

void expectStored(const cli::Stored& got, const json::Value& want) {
    EXPECT_EQ(got.receivedMs, fixtures::optInt(want["receivedMs"]));
    const auto windows = want["windows"];
    const std::pair<const char*, const std::optional<cli::Value>*> fields[] = {{"five_hour", &got.fiveHour}, {"seven_day", &got.sevenDay}};
    for (const auto& [field, value] : fields) {
        SCOPED_TRACE(field);
        const auto w = windows[field];
        if (!w.valid()) {
            EXPECT_FALSE(value->has_value());
            continue;
        }
        ASSERT_TRUE(value->has_value());
        EXPECT_DOUBLE_EQ((*value)->usedPercentage, *w["used_percentage"].number());
        EXPECT_DOUBLE_EQ((*value)->resetsAt, *w["resets_at"].number());
    }
}

cli::Stored storedOf(const json::Value& fixture) { return cli::parseFile(fixtures::compactJson(fixture)); }

}  // namespace

TEST(QuotaFixtures, HelperMergeAndLine) {
    cli::Stored stored;
    for (const auto& step : section("helper").elements()) {
        const auto now = *step["nowMs"].truncated();
        SCOPED_TRACE(now);
        cli::mergeInput(stored, fixtures::compactJson(step["input"]), now);
        expectStored(stored, step["expected"]);
        EXPECT_EQ(cli::line(stored, now), *step["line"].string());
        // The file round-trips.
        EXPECT_EQ(cli::parseFile(cli::fileText(stored)), stored);
    }
}

TEST(QuotaFixtures, HelperStoresTheFixtureStepsOnDisk) {
    quota_test::TempDir dir;
    const auto folder = (dir / "quota").string();
    const auto file = dir / "quota" / "claude-cli.json";
    ino_t last = 0;
    std::optional<std::int64_t> received;
    for (const auto& step : section("helper").elements()) {
        const auto now = *step["nowMs"].truncated();
        SCOPED_TRACE(now);
        const bool wrote = helper::store(fixtures::compactJson(step["input"]), folder, now);
        const auto expected = fixtures::optInt(step["expected"]["receivedMs"]);
        // Written only when a value changed (receivedMs moves exactly then).
        EXPECT_EQ(wrote, expected != received);
        received = expected;
        const auto text = quota_test::readFile(file);
        ASSERT_TRUE(text.has_value());
        expectStored(cli::parseFile(*text), step["expected"]);
        EXPECT_EQ(quota_test::modeOf(file), 0600u);
        const auto inode = quota_test::inodeOf(file);
        EXPECT_EQ(inode != last, wrote);
        last = inode;
    }
    EXPECT_EQ(quota_test::modeOf(dir / "quota"), 0700u);
}

TEST(QuotaFixtures, HelperExpiredWindowsLeaveTheLine) {
    const auto fixture = section("helperExpired");
    EXPECT_EQ(cli::line(storedOf(fixture["stored"]), *fixture["nowMs"].truncated()), *fixture["line"].string());
    EXPECT_EQ(cli::line(cli::Stored{}, 0), "");
}

TEST(QuotaHelper, QuotaFileReader) {
    quota_test::TempDir dir;
    const auto file = dir / "quota" / "claude-cli.json";
    cli::FileReader reader(file.string());
    EXPECT_FALSE(reader.refresh(true));
    EXPECT_FALSE(reader.snapshot().has_value());
    const auto steps = section("helper").elements();
    ASSERT_TRUE(helper::store(fixtures::compactJson(steps[0]["input"]), (dir / "quota").string(), 1794338773000));
    EXPECT_TRUE(reader.refresh(true));
    ASSERT_TRUE(reader.snapshot().has_value());
    const auto& snapshot = *reader.snapshot();
    EXPECT_EQ(snapshot.source, SourceKind::ClaudeCode);
    EXPECT_EQ(snapshot.observedMs, 1794338773000);
    ASSERT_EQ(snapshot.windows.size(), 2u);
    EXPECT_EQ(snapshot.windows[0], makeWindow(Kind::FiveHour, 6, 1794354000000));
    EXPECT_EQ(snapshot.windows[1], makeWindow(Kind::SevenDay, 86, 1794448800000));
    EXPECT_FALSE(reader.refresh(true));
    // Disabled: nothing is read and nothing is shown.
    EXPECT_TRUE(reader.refresh(false));
    EXPECT_FALSE(reader.snapshot().has_value());
    EXPECT_TRUE(reader.refresh(true));
    fs::remove(file);
    EXPECT_TRUE(reader.refresh(true));
    EXPECT_FALSE(reader.snapshot().has_value());
    quota_test::writeFile(file, "{\"version\": 2, \"receivedMs\": 1}");
    EXPECT_FALSE(reader.refresh(true));
    EXPECT_FALSE(reader.snapshot().has_value());
}

TEST(QuotaHelper, PreviousCommandOfTheSidecar) {
    EXPECT_EQ(cli::previousCommand(R"({"version": 1, "statusLine": {"type": "command", "command": "~/bin/line.sh", "padding": 0}})"),
              "~/bin/line.sh");
    EXPECT_EQ(cli::previousCommand(R"({"version": 1})"), std::nullopt);
    EXPECT_EQ(cli::previousCommand(R"({"version": 1, "statusLine": {"type": "command", "command": ""}})"), std::nullopt);
    EXPECT_EQ(cli::previousCommand(R"({"version": 1, "statusLine": {"type": "static", "command": "x"}})"), std::nullopt);
    EXPECT_EQ(cli::previousCommand(R"({"version": 1, "statusLine": "x"})"), std::nullopt);
    EXPECT_EQ(cli::previousCommand("not json"), std::nullopt);
}

TEST(QuotaHelper, RateLimitsNeedNumbersAndAnObject) {
    cli::Stored stored;
    EXPECT_FALSE(cli::mergeInput(stored, R"({"rate_limits": {"five_hour": {"used_percentage": "6", "resets_at": 1}}})", 1));
    EXPECT_FALSE(cli::mergeInput(stored, R"({"rate_limits": {"five_hour": {"used_percentage": true, "resets_at": 1}}})", 1));
    EXPECT_FALSE(cli::mergeInput(stored, R"({"rate_limits": [1]})", 1));
    EXPECT_FALSE(cli::mergeInput(stored, R"([{"rate_limits": {}}])", 1));
    EXPECT_FALSE(cli::mergeInput(stored, "{\"rate_limits\": ", 1));
    EXPECT_TRUE(cli::mergeInput(stored, R"({"rate_limits": {"seven_day": {"used_percentage": 23.5, "resets_at": 1794448800.5}}})", 7));
    EXPECT_EQ(stored.receivedMs, 7);
    EXPECT_EQ(stored.sevenDay->resetsAtMs(), 1794448800500);
    EXPECT_NE(cli::fileText(stored).find("\"used_percentage\": 23.5, \"resets_at\": 1794448800.5"), std::string::npos);
}

TEST(QuotaHelper, PreviousCommandRunnerTimesOutAndPassesTheExitCode) {
    const auto start = std::chrono::steady_clock::now();
    EXPECT_EQ(helper::runPrevious("sleep 5; exit 4", "", 300), 0);
    EXPECT_LT(std::chrono::steady_clock::now() - start, std::chrono::seconds(3));
    EXPECT_EQ(helper::runPrevious("exit 7", "", 5000), 7);
    EXPECT_EQ(helper::runPrevious("kill -9 $$", "", 5000), 0);
    quota_test::TempDir dir;
    const std::string input(300'000, 'x');
    EXPECT_EQ(helper::runPrevious("cat > '" + (dir / "in").string() + "'", input, 5000), 0);
    EXPECT_EQ(quota_test::readFile(dir / "in"), input);
    // A command that never reads its input still ends.
    EXPECT_EQ(helper::runPrevious("exit 2", input, 5000), 2);
    EXPECT_EQ(helper::quotaFolderBeside("/home/u/.local/share/agent-speed/bin/agent-speed-statusline"),
              "/home/u/.local/share/agent-speed/quota");
}

#ifdef AGENTSPEED_STATUSLINE_BINARY

namespace {

struct Ran {
    int code = -1;
    std::string out;
};

// Runs `program` with `input` on standard input, `extraEnv` added, in `cwd`; collects standard output.
Ran runProgram(const std::string& program, const std::vector<std::string>& args, const std::string& input, const std::string& cwd,
               const std::vector<std::string>& extraEnv = {}) {
    int in[2], out[2];
    EXPECT_EQ(::pipe2(in, O_CLOEXEC), 0);
    EXPECT_EQ(::pipe2(out, O_CLOEXEC), 0);
    posix_spawn_file_actions_t actions;
    ::posix_spawn_file_actions_init(&actions);
    ::posix_spawn_file_actions_adddup2(&actions, in[0], 0);
    ::posix_spawn_file_actions_adddup2(&actions, out[1], 1);
    ::posix_spawn_file_actions_addchdir_np(&actions, cwd.c_str());
    std::vector<char*> argv{const_cast<char*>(program.c_str())};
    for (const auto& a : args) argv.push_back(const_cast<char*>(a.c_str()));
    argv.push_back(nullptr);
    std::vector<char*> envp;
    for (char** e = environ; *e; ++e) envp.push_back(*e);
    for (const auto& entry : extraEnv) envp.push_back(const_cast<char*>(entry.c_str()));
    envp.push_back(nullptr);
    pid_t pid = 0;
    const int spawned = ::posix_spawn(&pid, program.c_str(), &actions, nullptr, argv.data(), envp.data());
    ::posix_spawn_file_actions_destroy(&actions);
    ::close(in[0]);
    ::close(out[1]);
    Ran ran;
    if (spawned != 0) {
        ::close(in[1]);
        ::close(out[0]);
        return ran;
    }
    std::size_t written = 0;
    while (written < input.size()) {
        const auto n = ::write(in[1], input.data() + written, input.size() - written);
        if (n <= 0) break;
        written += static_cast<std::size_t>(n);
    }
    ::close(in[1]);
    char buffer[4096];
    while (true) {
        const auto n = ::read(out[0], buffer, sizeof buffer);
        if (n <= 0) break;
        ran.out.append(buffer, static_cast<std::size_t>(n));
    }
    ::close(out[0]);
    int status = 0;
    ::waitpid(pid, &status, 0);
    ran.code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    return ran;
}

// `<data>/bin/agent-speed-statusline`, a copy of the built helper, as the bridge installs it.
struct Installed {
    quota_test::TempDir data;
    fs::path helper = data / "bin" / "agent-speed-statusline";
    fs::path quota = data / "quota";
    Installed() {
        fs::create_directories(helper.parent_path());
        fs::copy_file(AGENTSPEED_STATUSLINE_BINARY, helper);
    }
};

const std::string kInput = R"({"session_id":"s","model":{"id":"claude-opus-5-5"},)"
                           R"("rate_limits":{"five_hour":{"used_percentage":6,"resets_at":4102444800},)"
                           R"("seven_day":{"used_percentage":86.4,"resets_at":4102531200}}})";

}  // namespace

TEST(QuotaHelperBinary, StoresTheQuotaAndPrintsTheLine) {
    Installed app;
    const auto ran = runProgram(app.helper.string(), {}, kInput, app.data.str());
    EXPECT_EQ(ran.code, 0);
    EXPECT_EQ(ran.out, "5h 6% \xC2\xB7 7d 86%\n");
    const auto file = app.quota / "claude-cli.json";
    ASSERT_TRUE(fs::exists(file));
    EXPECT_EQ(quota_test::modeOf(file), 0600u);
    EXPECT_EQ(quota_test::modeOf(app.quota), 0700u);
    const auto stored = cli::parseFile(*quota_test::readFile(file));
    ASSERT_TRUE(stored.receivedMs.has_value());
    EXPECT_DOUBLE_EQ(stored.sevenDay->usedPercentage, 86.4);
    // The same values again: the file is not rewritten.
    const auto inode = quota_test::inodeOf(file);
    EXPECT_EQ(runProgram(app.helper.string(), {}, kInput, app.data.str()).out, ran.out);
    EXPECT_EQ(quota_test::inodeOf(file), inode);
}

TEST(QuotaHelperBinary, PrintsNothingWithoutAQuotaAndExitsZeroOnBadInput) {
    Installed app;
    for (const std::string& input : {std::string(R"({"model":{"id":"x"}})"), std::string("not json"), std::string(),
                                    std::string((1u << 20) + 10, ' ') + "{}"}) {
        const auto ran = runProgram(app.helper.string(), {}, input, app.data.str());
        EXPECT_EQ(ran.code, 0);
        EXPECT_EQ(ran.out, "");
    }
    EXPECT_FALSE(fs::exists(app.quota / "claude-cli.json"));
}

TEST(QuotaHelperBinary, RunsThePreviousCommandWithTheSameInputEnvironmentAndFolder) {
    Installed app;
    quota_test::TempDir work;
    const auto script = work / "line.sh";
    quota_test::writeFile(script, "#!/bin/sh\nprintf 'prev:'\ncat > \"$AS_OUT/stdin.bin\"\npwd > \"$AS_OUT/cwd.txt\"\n"
                                  "printf '%s' \"$AS_TEST_VAR\" > \"$AS_OUT/env.txt\"\nprintf 'done'\nexit 3\n");
    fs::permissions(script, fs::perms::owner_all);
    quota_test::writeFile(app.quota / "claude-statusline-previous.json",
                          "{\"version\": 1, \"statusLine\": {\"type\": \"command\", \"command\": \"'" + script.string() + "'\"}}");
    const auto ran = runProgram(app.helper.string(), {}, kInput, work.str(), {"AS_OUT=" + work.str(), "AS_TEST_VAR=hello world"});
    EXPECT_EQ(ran.code, 3);
    EXPECT_EQ(ran.out, "prev:done");
    EXPECT_EQ(quota_test::readFile(work / "stdin.bin"), kInput);
    EXPECT_EQ(quota_test::readFile(work / "cwd.txt"), work.str() + "\n");
    EXPECT_EQ(quota_test::readFile(work / "env.txt"), "hello world");
    // The quota is still stored first.
    EXPECT_TRUE(fs::exists(app.quota / "claude-cli.json"));
}

TEST(QuotaHelperBinary, FolderOptionPutsTheFilesUnderIt) {
    quota_test::TempDir host;
    const auto ran = runProgram(AGENTSPEED_STATUSLINE_BINARY, {"--folder", host.str()}, kInput, host.str());
    EXPECT_EQ(ran.code, 0);
    EXPECT_EQ(ran.out, "5h 6% \xC2\xB7 7d 86%\n");
    EXPECT_TRUE(fs::exists(host / "quota" / "claude-cli.json"));
}

TEST(QuotaHelperBinary, StartsFast) {
    Installed app;
    runProgram(app.helper.string(), {}, kInput, app.data.str());
    std::vector<double> runs;
    for (int i = 0; i < 9; ++i) {
        const auto start = std::chrono::steady_clock::now();
        runProgram(app.helper.string(), {}, kInput, app.data.str());
        runs.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count());
    }
    std::sort(runs.begin(), runs.end());
    std::printf("[ helper ] median run %.2f ms\n", runs[runs.size() / 2]);
    EXPECT_LT(runs[runs.size() / 2], 50.0);
}

#endif
