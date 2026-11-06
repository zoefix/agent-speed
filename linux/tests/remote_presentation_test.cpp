#include <gtest/gtest.h>

#include "agentspeed/host_selection.hpp"
#include "agentspeed/remote_presentation.hpp"
#include "agentspeed/remote_ssh.hpp"

namespace {

using namespace agentspeed::remote;
using ui::Dot;
using ui::Phrase;

const std::string kId = "8C0D6B0A-1111-4222-8333-00000000000A";
const std::string kOther = "8C0D6B0A-1111-4222-8333-00000000000B";

HostStatus live(Status status) {
    HostStatus s;
    s.status = std::move(status);
    return s;
}

TEST(RemotePresentation, DotsFollowTheStatusKinds) {
    EXPECT_EQ(ui::dotOf(StatusCode::Connected), Dot::Connected);
    for (const auto code : {StatusCode::Connecting, StatusCode::Installing, StatusCode::Seeding}) EXPECT_EQ(ui::dotOf(code), Dot::Progress);
    for (const auto code : {StatusCode::Unreachable, StatusCode::ProtocolError, StatusCode::Exited}) EXPECT_EQ(ui::dotOf(code), Dot::Retrying);
    for (const auto code : {StatusCode::AuthFailed, StatusCode::HostKeyUnknown, StatusCode::UnsupportedSystem, StatusCode::CannotVerify,
                            StatusCode::UploadFailed, StatusCode::NotExecutable, StatusCode::VersionMismatch, StatusCode::SshMissing,
                            StatusCode::InvalidDestination}) {
        EXPECT_EQ(ui::dotOf(code), Dot::Attention) << statusName(code);
    }
    EXPECT_EQ(ui::dotOf(StatusCode::Disabled), Dot::Idle);
    EXPECT_EQ(ui::dotOf(StatusCode::NetworkOff), Dot::Idle);
    EXPECT_EQ(ui::dotName(Dot::Attention), "attention");
    EXPECT_EQ(ui::statusFromName("hostKeyUnknown"), StatusCode::HostKeyUnknown);
    EXPECT_EQ(ui::statusFromName("disabled"), StatusCode::Disabled);
    EXPECT_FALSE(ui::statusFromName("nope"));
}

TEST(RemotePresentation, TheSettingsDecideOffNetworkAndInvalidFirst) {
    auto host = Host::make(kId, "devbox");
    const auto connected = std::optional<HostStatus>(live(Status::of(StatusCode::Connected)));
    EXPECT_EQ(ui::effective(host, false, connected, false).status.code, StatusCode::NetworkOff);
    host.enabled = false;
    EXPECT_EQ(ui::effective(host, true, connected, false).status.code, StatusCode::Disabled);
    host.enabled = true;
    EXPECT_EQ(ui::effective(host, true, connected, false).status.code, StatusCode::Connected);
    EXPECT_EQ(ui::effective(host, true, std::nullopt, false).status.code, StatusCode::Connecting) << "before its first status";
    EXPECT_EQ(ui::effective(host, true, live(Status::of(StatusCode::Disabled)), false).status.code, StatusCode::Connecting)
        << "a stale off status after it was switched on";
    auto bad = host;
    bad.destination = "a;b";
    EXPECT_EQ(ui::effective(bad, true, connected, false).status.code, StatusCode::InvalidDestination);
    EXPECT_TRUE(ui::effective(host, false, std::nullopt, true).historyTooLarge) << "whatever the connection does";
    auto tooLarge = live(Status::of(StatusCode::Unreachable));
    tooLarge.historyTooLarge = true;
    EXPECT_TRUE(ui::effective(host, true, tooLarge, false).historyTooLarge);
}

TEST(RemotePresentation, StatusLines) {
    EXPECT_EQ(ui::line(live(Status::of(StatusCode::Connected)), "devbox", 0, 0).sentence, (Phrase{"remote.status.connected", {}}));
    EXPECT_EQ(ui::line(live(Status::of(StatusCode::Connected)), "devbox", 1, 0).sentence, (Phrase{"remote.status.connected.one", {"1"}}));
    EXPECT_EQ(ui::line(live(Status::of(StatusCode::Connected)), "devbox", 3, 0).sentence, (Phrase{"remote.status.connected.agents", {"3"}}));
    EXPECT_EQ(ui::line(live(Status::seeding(0.426)), "devbox", 0, 0).sentence, (Phrase{"remote.status.seeding", {"42"}}));
    EXPECT_EQ(ui::line(live(Status::seeding(1.7)), "devbox", 0, 0).sentence, (Phrase{"remote.status.seeding", {"100"}}));
    EXPECT_EQ(ui::sentence(Status::of(StatusCode::HostKeyUnknown), "dev@box"), (Phrase{"remote.status.hostKeyUnknown", {"dev@box"}}));
    EXPECT_EQ(ui::sentence(Status::unsupportedSystem("Darwin arm64"), "x"), (Phrase{"remote.status.unsupportedSystem", {"Darwin arm64"}}));
    EXPECT_EQ(ui::sentence(Status::exited(12), "x").key, "remote.status.exited");
}

TEST(RemotePresentation, RetryCountdownRoundsUp) {
    auto s = live(Status::of(StatusCode::Unreachable));
    s.retryAtMs = 10'000;
    auto l = ui::line(s, "devbox", 0, 0);
    EXPECT_EQ(l.sentence.key, "remote.status.unreachable");
    EXPECT_EQ(l.retryInSeconds, 10);
    EXPECT_EQ(ui::line(s, "devbox", 0, 9'001).retryInSeconds, 1);
    l = ui::line(s, "devbox", 0, 10'000);
    EXPECT_FALSE(l.retryInSeconds);
    EXPECT_TRUE(l.retryingNow);
    auto user = live(Status::of(StatusCode::AuthFailed));
    user.retryAtMs = 300'000;
    EXPECT_FALSE(ui::line(user, "devbox", 0, 0).retryInSeconds) << "a host that needs the user shows no countdown";
    EXPECT_FALSE(ui::line(live(Status::of(StatusCode::Exited)), "devbox", 0, 0).retryInSeconds) << "no attempt planned";
}

TEST(RemotePresentation, ClockAndHistoryNotes) {
    auto s = live(Status::of(StatusCode::Connected));
    EXPECT_FALSE(ui::clockNote(s));
    s.clockOffsetMs = 4'999;
    EXPECT_FALSE(ui::clockNote(s));
    s.clockOffsetMs = -12'400;
    EXPECT_EQ(ui::clockNote(s), (Phrase{"remote.clock.skewed", {"12"}}));
    s.clockOffsetMs = 119'400;
    EXPECT_EQ(ui::clockNote(s), (Phrase{"remote.clock.skewed", {"119"}}));
    s.clockOffsetMs = 150'000;
    EXPECT_EQ(ui::clockNote(s), (Phrase{"remote.clock.skewedMinutes", {"3"}}));
    s.status = Status::of(StatusCode::Unreachable);
    EXPECT_FALSE(ui::clockNote(s)) << "only while connected";
    EXPECT_FALSE(ui::historyNote(s));
    s.historyTooLarge = true;
    EXPECT_EQ(ui::historyNote(s), (Phrase{"remote.status.historyTooLarge", {}}));
}

TEST(RemotePresentation, TooltipFacts) {
    auto s = live(Status::of(StatusCode::Connected));
    s.relayVersion = "1.6.1";
    s.relayArch = "x86_64";
    s.lastErrorAtMs = 5;
    auto t = ui::tooltip(s);
    EXPECT_EQ(t.relayVersion, "1.6.1");
    EXPECT_FALSE(t.lastErrorAtMs) << "a connected host shows its helper, not an old error";
    s.status = Status::exited(255);
    t = ui::tooltip(s);
    EXPECT_FALSE(t.relayVersion);
    EXPECT_EQ(t.lastErrorAtMs, 5);
    EXPECT_EQ(t.exitStatus, 255);
    EXPECT_TRUE(ui::tooltip(live(Status::of(StatusCode::Disabled))).empty());
}

TEST(RemotePresentation, WordsAndRetry) {
    EXPECT_EQ(ui::word(StatusCode::Seeding).key, "remote.word.connecting");
    EXPECT_EQ(ui::word(StatusCode::Unreachable).key, "remote.word.retrying");
    EXPECT_EQ(ui::word(StatusCode::AuthFailed).key, "remote.word.attention");
    EXPECT_EQ(ui::word(StatusCode::NetworkOff).key, "remote.word.offline");
    EXPECT_EQ(ui::word(StatusCode::Disabled).key, "remote.status.disabled");
    EXPECT_TRUE(ui::canRetry(StatusCode::Unreachable));
    EXPECT_TRUE(ui::canRetry(StatusCode::HostKeyUnknown));
    EXPECT_FALSE(ui::canRetry(StatusCode::InvalidDestination));
    EXPECT_FALSE(ui::canRetry(StatusCode::Connected));
    EXPECT_FALSE(ui::canRetry(StatusCode::Connecting));
    EXPECT_FALSE(ui::canRetry(StatusCode::Disabled));
}

TEST(RemotePresentation, FieldErrors) {
    EXPECT_EQ(ui::fieldError(FieldIssue::TooLong, false), (Phrase{"remote.error.tooLong", {"255"}}));
    EXPECT_EQ(ui::fieldError(FieldIssue::TooLong, true), (Phrase{"remote.error.tooLong", {"200"}}));
    EXPECT_EQ(ui::fieldError(FieldIssue::ForbiddenCharacter, false).key, "remote.error.forbiddenCharacter");
    EXPECT_EQ(ui::fieldError(FieldIssue::ForbiddenCharacter, true).key, "remote.error.folderCharacter");
    EXPECT_FALSE(ui::nameError("  " + std::string(32, 'x') + "  "));
    EXPECT_EQ(ui::nameError(std::string(33, 'x')), (Phrase{"remote.error.tooLong", {"32"}}));
    std::string wide;
    for (int i = 0; i < 32; ++i) wide += "開";
    EXPECT_FALSE(ui::nameError(wide)) << "characters, not bytes";
    EXPECT_TRUE(ui::nameError(wide + "發"));
}

TEST(RemotePresentation, TerminalCommandIsQuotedForTheShell) {
    EXPECT_EQ(ssh::terminalCommand("devbox"), "ssh devbox");
    EXPECT_EQ(ssh::terminalCommand("dev@10.0.0.2"), "ssh dev@10.0.0.2");
    EXPECT_EQ(ssh::terminalCommand("ssh://dev@box.example:2222"), "ssh ssh://dev@box.example:2222");
    EXPECT_EQ(ssh::terminalCommand("ssh://[fe80::1]:22"), "ssh 'ssh://[fe80::1]:22'") << "brackets are a glob";
    EXPECT_EQ(ssh::terminalCommand("~devbox"), "ssh '~devbox'") << "a leading tilde expands";
    EXPECT_EQ(ssh::terminalCommand("it's"), "ssh 'it'\\''s'");
    EXPECT_EQ(ssh::terminalCommand(""), "ssh ''");
}

TEST(HostSelection, NameSuffix) {
    using agentspeed::hosts::HostChoice;
    using agentspeed::hosts::HostSelection;
    using agentspeed::hosts::nameSuffix;
    const std::vector<HostChoice> choices{{"local", "This Computer"}, {kId, "devbox"}, {kOther, "gpu"}};
    EXPECT_FALSE(nameSuffix(HostSelection::all(), choices));
    EXPECT_FALSE(nameSuffix(HostSelection::normalized({"local"}), {{"local", "This Computer"}})) << "no remote host";
    EXPECT_EQ(nameSuffix(HostSelection::normalized({kId}), choices), (agentspeed::hosts::NameSuffix{"devbox", 0}));
    EXPECT_EQ(nameSuffix(HostSelection::normalized({kOther, "local", kId}), choices),
              (agentspeed::hosts::NameSuffix{"This Computer", 2}));
    EXPECT_FALSE(nameSuffix(HostSelection::normalized({"gone"}), choices)) << "unknown ids name nothing";
}

}  // namespace
