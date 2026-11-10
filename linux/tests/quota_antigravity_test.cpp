#include <gtest/gtest.h>

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
