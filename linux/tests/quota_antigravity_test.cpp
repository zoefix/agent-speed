#include <gtest/gtest.h>

#include <filesystem>

#include "agentspeed/quota/routes/antigravity.hpp"
#include "quota_vendor_support.hpp"

// QUOTAS 9.4 Servers and Ports on Linux, against a fake /proc tree.
namespace {

using agentspeed::quota::AntigravityEndpoint;
using agentspeed::quota::AntigravityServer;
using agentspeed::quota::ProcAntigravityLocator;
using agentspeed::quota::VendorEnvironment;
namespace antigravity = agentspeed::quota::antigravity;
using quota2_test::TempHome;

std::string nul(std::initializer_list<std::string_view> parts) {
    std::string out;
    for (const auto part : parts) {
        out += part;
        out += '\0';
    }
    return out;
}

constexpr std::string_view kTcpHeader =
    "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";

std::string tcpLine(std::string_view local, std::string_view state, std::uint64_t inode) {
    return "   0: " + std::string(local) + " 00000000:0000 " + std::string(state) +
           " 00000000:00000000 00:00000000 00000000  1000        0 " + std::to_string(inode) + " 1 0000000000000000 100 0 0 10 0\n";
}

TEST(QuotaAntigravity, ListeningSocketsOnLoopbackOrEveryAddress) {
    const std::string table = std::string(kTcpHeader) + tcpLine("0100007F:A1B2", "0A", 1111) + tcpLine("0A000001:1F90", "0A", 2222) +
                              tcpLine("00000000:0050", "0A", 3333) + tcpLine("0100007F:0051", "01", 4444);
    const auto sockets = antigravity::listeningSockets(table);
    EXPECT_EQ(sockets, (std::map<std::uint64_t, int>{{1111, 0xA1B2}, {3333, 0x50}}));
    const std::string v6 = std::string(kTcpHeader) + tcpLine("00000000000000000000000000000000:1F90", "0A", 5555) +
                           tcpLine("00000000000000000000000001000000:1F91", "0A", 6666);
    EXPECT_EQ(antigravity::listeningSockets(v6), (std::map<std::uint64_t, int>{{5555, 0x1F90}}));
}

TEST(QuotaAntigravity, RulesForServersAgyAndDaemons) {
    const std::vector<std::string> args = {"--app_data_dir", "/home/u/.antigravity", "--csrf_token=tok", "--extension_server_port",
                                           "5555"};
    EXPECT_TRUE(antigravity::isLanguageServer("/opt/Antigravity/bin/language_server_linux_x64", args));
    EXPECT_FALSE(antigravity::isLanguageServer("/opt/Windsurf/bin/language_server_linux_x64", {"--app_data_dir", "/home/u/.windsurf"}));
    EXPECT_FALSE(antigravity::isLanguageServer("/usr/bin/node", args));
    const auto server = antigravity::languageServer(7, args, {41000, 41000});
    EXPECT_EQ(server.endpoints, (std::vector<AntigravityEndpoint>{{"http", 5555, "tok"}, {"http", 41000, "tok"}, {"https", 41000, "tok"}}));
    EXPECT_FALSE(antigravity::agy(8, "t", "10.0.0.2:5000", {}).has_value());
    EXPECT_FALSE(antigravity::agy(8, "bad\r\nHost: x", "127.0.0.1:5000", {})->endpoints.at(0).token.has_value());
    EXPECT_EQ(antigravity::agy(8, "t", "localhost:5000", {})->endpoints,
              (std::vector<AntigravityEndpoint>{{"http", 5000, "t"}, {"https", 5000, "t"}}));
    EXPECT_FALSE(antigravity::daemon(R"({"pid": 0, "httpPort": 1})").has_value());
    EXPECT_FALSE(antigravity::daemon(R"({"pid": 9, "csrfToken": "x"})").has_value());
    EXPECT_EQ(antigravity::environmentValues(nul({"A=1", "ANTIGRAVITY_CSRF_TOKEN=x=y", "B"}), {"ANTIGRAVITY_CSRF_TOKEN"}),
              (std::map<std::string, std::string>{{"ANTIGRAVITY_CSRF_TOKEN", "x=y"}}));
}

TEST(QuotaAntigravity, ServersFromAFakeProcTreeReadingOnlyAgysOwnEnvironment) {
    TempHome home;
    // A language server with an extension server port and two sockets (one listening on a LAN address only).
    home.link("proc/100/exe", "/opt/antigravity/resources/bin/language_server_linux_x64");
    home.write("proc/100/cmdline", nul({"/opt/antigravity/resources/bin/language_server_linux_x64", "--app_data_dir", ".antigravity",
                                        "--csrf_token", "tok-a", "--extension_server_port", "5555",
                                        "--extension_server_csrf_token", "ext-tok"}));
    home.link("proc/100/fd/3", "socket:[1111]");
    home.link("proc/100/fd/4", "socket:[2222]");
    home.link("proc/100/fd/5", "/dev/null");
    home.write("proc/100/environ", nul({"ANTIGRAVITY_CSRF_TOKEN=never-read"}));
    // An agy process: its own environment names the token and the address.
    home.link("proc/200/exe", "/usr/local/bin/agy");
    home.write("proc/200/environ", nul({"PATH=/bin", "ANTIGRAVITY_CSRF_TOKEN=agy-tok", "ANTIGRAVITY_LS_ADDRESS=127.0.0.1:6000", "OTHER=x"}));
    home.link("proc/200/fd/7", "socket:[4444]");
    // Another process with the same variables: never read.
    home.link("proc/300/exe", "/usr/bin/bash");
    home.write("proc/300/environ", nul({"ANTIGRAVITY_CSRF_TOKEN=never-read"}));
    home.write("proc/300/cmdline", nul({"bash"}));
    // Another user's process: no readable exe.
    home.write("proc/400/cmdline", nul({"language_server_linux_x64", "--app_data_dir", "antigravity"}));
    // A daemon's process, and a daemon file whose pid is gone.
    home.link("proc/500/exe", "/opt/antigravity/antigravity");
    home.write(".gemini/antigravity/daemon/ls_live.json", R"({"pid": 500, "csrfToken": "d-tok", "httpsPort": 7001, "httpPort": 7000})");
    home.write(".gemini/antigravity-ide/daemon/ls_dead.json", R"({"pid": 999, "csrfToken": "gone", "httpPort": 7100})");
    home.write(".gemini/antigravity/daemon/other.json", R"({"pid": 500, "httpPort": 7200})");
    home.write("proc/net/tcp", std::string(kTcpHeader) + tcpLine("0100007F:A1B2", "0A", 1111) + tcpLine("0A000001:1F90", "0A", 2222));
    home.write("proc/net/tcp6", std::string(kTcpHeader) + tcpLine("0000000000000000FFFF00000100007F:1F90", "0A", 4444));

    auto files = std::make_shared<quota2_test::CountingFiles>();
    auto env = VendorEnvironment::fixed(home.path.string(), {}, files);
    env.procRoot = home.file("proc");
    ProcAntigravityLocator locator(env);
    const auto servers = locator.servers();
    const std::vector<AntigravityServer> expected = {
        {100, {{"http", 5555, "ext-tok"}, {"http", 0xA1B2, "tok-a"}, {"https", 0xA1B2, "tok-a"}}},
        {200, {{"http", 6000, "agy-tok"}, {"https", 6000, "agy-tok"}, {"http", 0x1F90, "agy-tok"}, {"https", 0x1F90, "agy-tok"}}},
        {500, {{"http", 7000, "d-tok"}, {"https", 7001, "d-tok"}}},
    };
    EXPECT_EQ(servers, expected);
    for (const auto& path : files->touched) {
        EXPECT_EQ(path.find("/proc/300/environ"), std::string::npos) << path;
        EXPECT_EQ(path.find("/proc/100/environ"), std::string::npos) << path;
        EXPECT_EQ(path.find("/proc/300/cmdline"), std::string::npos) << path;
    }
}

}  // namespace

namespace {

// `/proc/<pid>/stat` with the parent and the start time (field 22).
std::string statLine(std::int64_t pid, std::string_view comm, std::int64_t ppid, std::uint64_t start) {
    std::string out = std::to_string(pid) + " (" + std::string(comm) + ") S " + std::to_string(ppid);
    for (int field = 5; field <= 21; ++field) out += " 0";
    return out + " " + std::to_string(start) + " 4096 12\n";
}

TEST(QuotaAntigravity, ProcStatGivesTheParentAndTheStartTime) {
    EXPECT_EQ(antigravity::procStat(statLine(7, "a) (b", 3, 99)), (antigravity::ProcStat{3, 99}));
    EXPECT_FALSE(antigravity::procStat("7 (agy) S 3 0 0").has_value());
    EXPECT_FALSE(antigravity::procStat("").has_value());
}

// QUOTAS 9.4: the desktop app's plain `language_server` with its arguments; METRICS 10.5: the app's own agy and its
// server, a pid the app reused excepted, are never servers, never "agy runs", and their environment is never read.
TEST(QuotaAntigravity, PlainServersCountAndTheAppsOwnProcessesNever) {
    TempHome home;
    home.link("proc/1/exe", "/usr/lib/systemd/systemd");
    home.write("proc/1/stat", statLine(1, "systemd", 0, 1));
    home.link("proc/50/exe", "/usr/bin/agent-speed");
    home.write("proc/50/stat", statLine(50, "agent-speed", 1, 100));
    // The report's agy, its server and a daemon file naming it.
    home.link("proc/51/exe", "/home/u/.local/share/agy/agy");
    home.write("proc/51/stat", statLine(51, "agy", 50, 200));
    home.write("proc/51/environ", nul({"ANTIGRAVITY_CSRF_TOKEN=own-tok", "ANTIGRAVITY_LS_ADDRESS=127.0.0.1:6100"}));
    home.link("proc/52/exe", "/home/u/.local/share/agy/language_server_linux_x64");
    home.write("proc/52/stat", statLine(52, "language_server", 51, 300));
    home.write("proc/52/cmdline", nul({"language_server_linux_x64", "--app_data_dir", "antigravity-cli", "--csrf_token", "own"}));
    home.write(".gemini/antigravity/daemon/ls_own.json", R"({"pid": 51, "csrfToken": "own-d", "httpPort": 6200})");
    // The desktop app's plain server, and a generic one.
    home.link("proc/60/exe", "/opt/Antigravity/resources/bin/language_server");
    home.write("proc/60/stat", statLine(60, "language_server", 1, 150));
    home.write("proc/60/cmdline", nul({"/opt/Antigravity/resources/bin/language_server", "--standalone", "--override_ide_name",
                                       "antigravity", "--csrf_token", "t60", "--app_data_dir", "antigravity"}));
    home.link("proc/60/fd/3", "socket:[7777]");
    home.link("proc/61/exe", "/usr/bin/language_server");
    home.write("proc/61/stat", statLine(61, "language_server", 1, 150));
    home.write("proc/61/cmdline", nul({"language_server", "--stdio"}));
    // The user's agy, which serves nothing it can be asked without a token.
    home.link("proc/70/exe", "/home/u/.local/bin/agy");
    home.write("proc/70/stat", statLine(70, "agy", 1, 150));
    home.write("proc/70/environ", nul({"PATH=/bin"}));
    home.write("proc/net/tcp", std::string(kTcpHeader) + tcpLine("0100007F:9C40", "0A", 7777));

    auto files = std::make_shared<quota2_test::CountingFiles>();
    auto env = VendorEnvironment::fixed(home.path.string(), {}, files);
    env.procRoot = home.file("proc");
    env.selfPid = 50;
    ProcAntigravityLocator locator(env);
    EXPECT_EQ(locator.servers(), (std::vector<AntigravityServer>{{60, {{"http", 0x9C40, "t60"}, {"https", 0x9C40, "t60"}}}}));
    EXPECT_TRUE(locator.agyRuns());
    for (const auto& path : files->touched) {
        EXPECT_EQ(path.find("/proc/51/environ"), std::string::npos) << path;
        EXPECT_EQ(path.find("/proc/52/cmdline"), std::string::npos) << path;
    }

    // Only the app's own agy left: no agy runs.
    std::filesystem::remove_all(home.file("proc/70"));
    locator.servers();
    EXPECT_FALSE(locator.agyRuns());

    // A process under a pid the app reused (it started before the app) is not the app's.
    home.link("proc/80/exe", "/home/u/.local/bin/agy");
    home.write("proc/80/stat", statLine(80, "agy", 50, 50));
    home.write("proc/80/environ", nul({"PATH=/bin"}));
    locator.servers();
    EXPECT_TRUE(locator.agyRuns());

    // Without the app's pid, its agy is like any other.
    env.selfPid = 0;
    ProcAntigravityLocator unaware(env);
    const auto all = unaware.servers();
    ASSERT_EQ(all.size(), 3u);
    EXPECT_EQ(all[0], (AntigravityServer{51, {{"http", 6100, "own-tok"}, {"https", 6100, "own-tok"}}}));
    EXPECT_EQ(all[1].pid, 52);
    EXPECT_EQ(all[2].pid, 60);
}

}  // namespace
