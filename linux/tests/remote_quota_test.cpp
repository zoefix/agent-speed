#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "remote_test_support.hpp"

// REMOTE 11.1 on the app side: the `quota` config it sends, and the relay's `quota`, `quotaRemoved` and `quotaBridge`
// lines validated by 5.6, through scripted relay lines.
namespace {

namespace fs = std::filesystem;
using namespace agentspeed;
using namespace agentspeed::remote;
using namespace remote_test;

const LineDecoder kDecoder(kHost);
// A JSON `\u` escape, spelled so the source holds no bidi control.
const std::string kEscape = std::string(1, '\\') + "u";

const std::string kWindows =
    R"([{"kind":"fiveHour","name":null,"minutes":300,"usedPercent":23.5,"resetsAtMs":1794348000000},)"
    R"({"kind":"sevenDay","name":"Claude/GPT","minutes":10080,"usedPercent":41.2,"resetsAtMs":null}])";

std::string quotaLine(const std::string& windows = kWindows, const std::string& source = "kimi", const std::string& status = "ok") {
    return R"({"t":"quota","q":{"source":")" + source + R"(","observedMs":1794338773000,"windows":)" + windows +
           R"(,"running":true,"signedIn":true,"status":")" + status + R"("}})";
}

std::string oneWindow(const std::string& members) { return "[{" + members + "}]"; }

bool invalid(const std::string& line) { return std::holds_alternative<LineError>(kDecoder.decode(line)); }
bool ignored(const std::string& line) { return std::holds_alternative<LineDecoder::Ignored>(kDecoder.decode(line)); }

template <typename T>
T decoded(const std::string& line) {
    auto outcome = kDecoder.decode(line);
    if (const auto* error = std::get_if<LineError>(&outcome)) ADD_FAILURE() << "invalid: " << error->field << " in " << line;
    const auto* message = std::get_if<RelayMessage>(&outcome);
    const auto* value = message ? std::get_if<T>(message) : nullptr;
    if (!value) {
        ADD_FAILURE() << "not decoded: " << line;
        return T{};
    }
    return *value;
}

TEST(RemoteQuota, DecodesAQuotaLine) {
    const auto q = decoded<HostQuota>(quotaLine());
    EXPECT_EQ(q.hostId, kHost);
    EXPECT_EQ(q.snapshot.source, SourceKind::Kimi);
    EXPECT_EQ(q.snapshot.observedMs, 1794338773000);
    ASSERT_EQ(q.snapshot.windows.size(), 2u);
    EXPECT_EQ(q.snapshot.windows[0].kind, quota::Kind::FiveHour);
    EXPECT_FALSE(q.snapshot.windows[0].name);
    EXPECT_EQ(q.snapshot.windows[0].minutes, 300);
    EXPECT_DOUBLE_EQ(q.snapshot.windows[0].usedPercent, 23.5);
    EXPECT_EQ(q.snapshot.windows[0].resetsAtMs, 1794348000000);
    EXPECT_EQ(q.snapshot.windows[1].name, "Claude/GPT");
    EXPECT_FALSE(q.snapshot.windows[1].resetsAtMs);
    EXPECT_TRUE(q.running);
    EXPECT_TRUE(q.signedIn);
    EXPECT_EQ(q.status, HostQuota::Status::Ok);
    for (const auto* status : {"noCredential", "expired", "denied", "failed", "networkOff", "noCertificates", "notRunning"}) {
        EXPECT_EQ(hostQuotaStatusId(decoded<HostQuota>(quotaLine(kWindows, "kimi", status)).status), status);
    }
    EXPECT_TRUE(decoded<HostQuota>(quotaLine("[]")).snapshot.windows.empty());
}

TEST(RemoteQuota, SourcesAHostCannotReadAreIgnored) {
    for (const auto* source : {"claudeDesktop", "devin", "zcode", "qoder", "qoderCli", "workbuddy", "deepseek", "futureAgent"}) {
        EXPECT_TRUE(ignored(quotaLine(kWindows, source))) << source;
        EXPECT_TRUE(ignored(std::string(R"({"t":"quotaRemoved","source":")") + source + R"("})")) << source;
    }
    for (const auto* source : {"codex", "claudeCode", "grokBuild", "kimi", "gemini", "droid", "antigravity"}) {
        EXPECT_EQ(sourceId(decoded<HostQuota>(quotaLine(kWindows, source)).snapshot.source), source);
    }
    // An ignored source is still checked: an invalid line stays invalid.
    EXPECT_TRUE(invalid(quotaLine(oneWindow(R"("kind":"week","minutes":1,"usedPercent":1,"resetsAtMs":null)"), "devin")));
}

TEST(RemoteQuota, EveryWindowRuleOfSectionElevenOne) {
    const auto window = [](const std::string& members) { return quotaLine(oneWindow(members)); };
    EXPECT_FALSE(invalid(window(R"("kind":"credits","name":null,"minutes":null,"usedPercent":0,"resetsAtMs":null)")));
    EXPECT_FALSE(invalid(window(R"("kind":"month","minutes":527040,"usedPercent":1000)")));
    EXPECT_TRUE(invalid(window(R"("kind":"weekly","minutes":10080,"usedPercent":1,"resetsAtMs":null)")));
    EXPECT_TRUE(invalid(window(R"("kind":"other","name":null,"minutes":null,"usedPercent":1,"resetsAtMs":null)")));
    EXPECT_TRUE(invalid(window(R"("kind":"other","minutes":0,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"other","minutes":527041,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"other","minutes":1.5,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":-0.1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":1000.5)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":"5")")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":1e999)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":1,"resetsAtMs":4133980800000)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":1,"resetsAtMs":946684799999)")));
    EXPECT_TRUE(invalid(window(R"("kind":"fiveHour","minutes":300,"usedPercent":1,"resetsAtMs":"soon")")));
    // Names: display text of at most 64 characters, no control or bidi characters.
    const std::string name64(64, 'n');
    EXPECT_FALSE(invalid(window(R"("kind":"sevenDay","name":")" + name64 + R"(","minutes":10080,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"sevenDay","name":")" + name64 + R"(n","minutes":10080,"usedPercent":1)")));
    std::string wide;
    for (int k = 0; k < 64; ++k) wide += "\xE6\x9C\x88";  // 月
    EXPECT_FALSE(invalid(window(R"("kind":"month","name":")" + wide + R"(","minutes":43200,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"sevenDay","name":"a\u0007b","minutes":10080,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"sevenDay","name":"a)" + kEscape + R"(202Eb","minutes":10080,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"sevenDay","name":"a)" + kEscape + R"(2028b","minutes":10080,"usedPercent":1)")));
    EXPECT_TRUE(invalid(window(R"("kind":"sevenDay","name":7,"minutes":10080,"usedPercent":1)")));
    // An empty name is no name.
    EXPECT_FALSE(decoded<HostQuota>(window(R"("kind":"sevenDay","name":"","minutes":10080,"usedPercent":1)")).snapshot.windows[0].name);
    // At most 16 windows.
    std::string sixteen = "[", seventeen;
    for (int k = 0; k < 16; ++k) sixteen += std::string(k ? "," : "") + R"({"kind":"fiveHour","minutes":300,"usedPercent":1})";
    seventeen = sixteen + R"(,{"kind":"fiveHour","minutes":300,"usedPercent":1}])";
    sixteen += "]";
    EXPECT_FALSE(invalid(quotaLine(sixteen)));
    EXPECT_TRUE(invalid(quotaLine(seventeen)));
    EXPECT_TRUE(invalid(quotaLine("[7]")));
    EXPECT_TRUE(invalid(quotaLine("{}")));
}

TEST(RemoteQuota, EveryFieldOfTheQuotaObjectIsChecked) {
    EXPECT_TRUE(invalid(R"({"t":"quota"})"));
    EXPECT_TRUE(invalid(R"({"t":"quota","q":[]})"));
    EXPECT_TRUE(invalid(quotaLine(kWindows, "kimi", "great")));
    EXPECT_TRUE(invalid(quotaLine(kWindows, "kimi", "noBrowser")));
    auto line = quotaLine();
    EXPECT_TRUE(invalid(std::string(line).replace(line.find("\"running\":true"), 14, "\"running\":1")));
    EXPECT_TRUE(invalid(std::string(line).replace(line.find("\"signedIn\":true,"), 16, "")));
    EXPECT_TRUE(invalid(std::string(line).replace(line.find("1794338773000"), 13, "94338773000")));
    EXPECT_TRUE(invalid(std::string(line).replace(line.find("\"source\":\"kimi\""), 15, "\"source\":5")));
    EXPECT_TRUE(invalid(R"({"t":"quotaRemoved"})"));
    EXPECT_EQ(decoded<HostQuotaRemoved>(R"({"t":"quotaRemoved","source":"codex"})").source, SourceKind::Codex);
}

TEST(RemoteQuota, DecodesTheBridgeState) {
    for (const auto* state : {"off", "waiting", "ok", "replaced", "unreadable", "failed"}) {
        const auto b = decoded<HostQuotaBridge>(std::string(R"({"t":"quotaBridge","state":")") + state +
                                                R"(","settingsPath":"~/.claude/settings.json"})");
        EXPECT_EQ(hostBridgeStateId(b.state), state);
        EXPECT_EQ(b.settingsPath, "~/.claude/settings.json");
        EXPECT_EQ(b.hostId, kHost);
    }
    EXPECT_TRUE(invalid(R"({"t":"quotaBridge","state":"on","settingsPath":"~/.claude/settings.json"})"));
    EXPECT_TRUE(invalid(R"({"t":"quotaBridge","state":"ok"})"));
    EXPECT_TRUE(invalid(R"({"t":"quotaBridge","state":"ok","settingsPath":"~/x\u0000"})"));
    EXPECT_TRUE(invalid(R"({"t":"quotaBridge","state":"ok","settingsPath":"~/)" + kEscape + R"(202Ex"})"));
}

TEST(RemoteQuota, HostConfigFollowsTheSwitchesAndTheHostsBridge) {
    QuotaSwitches switches;
    switches.sources = {SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::ClaudeDesktop, SourceKind::GrokBuild, SourceKind::ZCode,
                        SourceKind::Qoder, SourceKind::Kimi};
    switches.network = true;
    switches.grokLive = true;
    switches.claudeCode = true;
    auto off = HostQuotaConfig::forHost(false, switches);
    EXPECT_EQ(off.sources, (std::set<SourceKind>{SourceKind::Codex, SourceKind::Kimi, SourceKind::GrokBuild}));
    EXPECT_TRUE(off.grokLive);
    EXPECT_FALSE(off.claudeBridge);
    auto on = HostQuotaConfig::forHost(true, switches);
    EXPECT_TRUE(on.sources.contains(SourceKind::ClaudeCode));
    EXPECT_TRUE(on.claudeBridge);
    // Claude Code disabled: no quota from the bridge, but the bridge follows the host's switch so it can be restored.
    switches.claudeCode = false;
    on = HostQuotaConfig::forHost(true, switches);
    EXPECT_FALSE(on.sources.contains(SourceKind::ClaudeCode));
    EXPECT_TRUE(on.claudeBridge);
    // Grok's live request only with Grok Build itself.
    switches.sources.erase(SourceKind::GrokBuild);
    EXPECT_FALSE(HostQuotaConfig::forHost(true, switches).grokLive);
}

TEST(RemoteQuota, EncodesStartQuotaAndTheConfigLine) {
    HostQuotaConfig config{{SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::Kimi}, true, false, true};
    const auto start = encode::start(1, {SourceKind::Codex}, Zone{"UTC", utcTzif()}, "{}", config);
    ASSERT_TRUE(start);
    EXPECT_NE(start->find(R"(,"quota":{"sources":["codex","claudeCode","kimi"],"network":true,"grokLive":false,"claudeBridge":true}})"),
              std::string::npos)
        << *start;
    EXPECT_EQ(encode::start(1, {SourceKind::Codex}, Zone{"UTC", utcTzif()}, "{}")->find("quota"), std::string::npos);
    EXPECT_EQ(*encode::quota(HostQuotaConfig{{}, false, false, false}),
              "{\"t\":\"quota\",\"config\":{\"sources\":[],\"network\":false,\"grokLive\":false,\"claudeBridge\":false}}\n");
    json::Document doc;
    EXPECT_TRUE(doc.parse(*encode::quota(config)));
}

TEST(RemoteQuota, HostDocumentKeepsTheBridgeSwitch) {
    const auto parsed = HostsDocument::parse(
        R"({"version":1,"hosts":[{"id":"8C0D6B0A-1111-4222-8333-444455556666","destination":"devbox","quotaClaudeCli":true},)"
        R"({"id":"9C0D6B0A-1111-4222-8333-444455556666","destination":"other"}]})");
    ASSERT_TRUE(parsed);
    EXPECT_TRUE(parsed->hosts()[0].quotaClaudeCli);
    EXPECT_FALSE(parsed->hosts()[1].quotaClaudeCli);
    const auto text = parsed->json();
    EXPECT_NE(text.find("\"quotaClaudeCli\":true"), std::string::npos);
    EXPECT_EQ(text.find("quotaClaudeCli"), text.rfind("quotaClaudeCli"));
    const auto again = HostsDocument::parse(text);
    EXPECT_EQ(*again, *parsed);
    // Not an extra member: it is read, not kept verbatim.
    EXPECT_TRUE(again->hosts()[0].extra.empty());
}

class RemoteQuotaConnection : public ::testing::Test {
protected:
    void SetUp() override {
        dir_ = fs::temp_directory_path() / ("as-quota-connection-" + std::to_string(::getpid()));
        fs::create_directories(dir_);
        std::ofstream(dir_ / "agent-speed-relay-linux-x86_64", std::ios::binary) << std::string(600'000, 'R');
        bundle_ = std::make_unique<RelayBundle>(dir_.string(), "1.6.1");
        relay_ = *bundle_->executable(Arch::X86_64);
    }
    void TearDown() override {
        if (connection_) connection_->stop();
        fs::remove_all(dir_);
    }

    FakeProcess& run(bool bridge, const QuotaSwitches& switches) {
        auto host = Host::make(kHost, "dev@devbox");
        host.quotaClaudeCli = bridge;
        ConnectionEnvironment environment;
        environment.transport = &transport_;
        environment.scheduler = &scheduler_;
        environment.relays = bundle_.get();
        environment.zone = [] { return Zone{"UTC", utcTzif()}; };
        SessionSettings settings{{SourceKind::Codex, SourceKind::Kimi}, "{}", false, switches};
        connection_ = Connection::make(std::move(host), settings, environment, [this](const HostEvent& e) { events_.push_back(e); });
        connection_->start();
        transport_.last().out("system Linux x86_64\ntool sha256sum\nhash " + relay_.sha256 + " " + relay_.installedName + "\nend\n");
        transport_.last().exit(0);
        EXPECT_TRUE(transport_.last().isRun());
        return transport_.last();
    }

    std::vector<RelayMessage> messages() const {
        std::vector<RelayMessage> out;
        for (const auto& e : events_) {
            const auto* m = std::get_if<HostEvent::Message>(&e.kind);
            if (m && (std::holds_alternative<HostQuota>(m->message) || std::holds_alternative<HostQuotaRemoved>(m->message) ||
                      std::holds_alternative<HostQuotaBridge>(m->message))) {
                out.push_back(m->message);
            }
        }
        return out;
    }

    fs::path dir_;
    std::unique_ptr<RelayBundle> bundle_;
    RelayExecutable relay_;
    FakeScheduler scheduler_;
    FakeTransport transport_;
    std::shared_ptr<Connection> connection_;
    std::vector<HostEvent> events_;
};

TEST_F(RemoteQuotaConnection, StartCarriesTheConfigAndChangesFollowWithoutAReconnect) {
    QuotaSwitches switches{{SourceKind::Codex, SourceKind::Kimi, SourceKind::ClaudeCode}, true, false, true};
    auto& relay = run(false, switches);
    const auto first = relay.input();
    EXPECT_NE(first.find(R"("quota":{"sources":["codex","kimi"],"network":true,"grokLive":false,"claudeBridge":false})"), std::string::npos)
        << first;
    relay.line(helloLine());
    // The host's bridge turned on: one `quota` line, no new process.
    const auto launched = transport_.launched.size();
    connection_->setQuota(switches, true);
    EXPECT_NE(relay.input().find(R"({"t":"quota","config":{"sources":["codex","claudeCode","kimi"],"network":true,"grokLive":false,"claudeBridge":true}})"),
              std::string::npos)
        << relay.input();
    // Nothing changed: nothing sent.
    const auto sent = relay.written.size();
    connection_->setQuota(switches, true);
    EXPECT_EQ(relay.written.size(), sent);
    EXPECT_EQ(transport_.launched.size(), launched);
}

TEST_F(RemoteQuotaConnection, QuotaLinesReachTheSinkAndInvalidOnesAreCounted) {
    auto& relay = run(true, QuotaSwitches{{SourceKind::Kimi}, true, false, true});
    relay.line(helloLine());
    relay.line(quotaLine());
    relay.line(R"({"t":"quotaRemoved","source":"kimi"})");
    relay.line(R"({"t":"quotaBridge","state":"waiting","settingsPath":"~/.claude/settings.json"})");
    relay.line(quotaLine(kWindows, "zcode"));
    for (int k = 0; k < 100; ++k) relay.line(quotaLine(kWindows, "kimi", "bogus"));
    const auto got = messages();
    ASSERT_EQ(got.size(), 3u);
    EXPECT_TRUE(std::holds_alternative<HostQuota>(got[0]));
    EXPECT_TRUE(std::holds_alternative<HostQuotaRemoved>(got[1]));
    EXPECT_EQ(std::get<HostQuotaBridge>(got[2]).state, HostQuotaBridge::State::Waiting);
    EXPECT_FALSE(relay.terminated);
    // The 101st invalid line ends the connection (REMOTE 5.6).
    relay.line(quotaLine(kWindows, "kimi", "bogus"));
    EXPECT_TRUE(relay.inputClosed || relay.terminated);
}

}  // namespace
