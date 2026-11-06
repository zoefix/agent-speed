#include <gtest/gtest.h>

#include <algorithm>

#include "agentspeed/remote_ssh.hpp"
#include "agentspeed/remote_status.hpp"

namespace {

using namespace agentspeed::remote;

const std::string kDigest(64, 'a');

TEST(RemoteCommand, ArgumentVectorIsExactlyTheSpecs) {
    const auto args = ssh::arguments("devbox", "echo hi");
    ASSERT_TRUE(args);
    const std::vector<std::string> expected = {
        "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
        "-o", "Compression=yes", "-o", "ControlMaster=no", "-o", "ClearAllForwardings=yes", "-o", "ForwardAgent=no", "-o",
        "ForwardX11=no", "-o", "PermitLocalCommand=no", "-o", "RequestTTY=no", "-o", "LogLevel=ERROR", "--", "devbox", "echo hi"};
    EXPECT_EQ(*args, expected);
    const auto configured = ssh::arguments("devbox", "x", std::string("/tmp/ssh_config"));
    ASSERT_TRUE(configured);
    EXPECT_EQ((*configured)[configured->size() - 5], "-F");
    EXPECT_EQ((*configured)[configured->size() - 4], "/tmp/ssh_config");
    EXPECT_EQ((*configured)[configured->size() - 3], "--");
    EXPECT_EQ(ssh::kExecutable, "/usr/bin/ssh");
}

TEST(RemoteCommand, InvalidDestinationsGetNoArguments) {
    EXPECT_FALSE(ssh::arguments("-oProxyCommand=id", "x"));
    EXPECT_FALSE(ssh::arguments("a;b", "x"));
    EXPECT_FALSE(ssh::arguments("", "x"));
}

TEST(RemoteCommand, EnvironmentAddsTheCLocale) {
    const auto env = ssh::environment({"HOME=/home/me", "LC_ALL=de_DE.UTF-8", "LANG=de_DE.UTF-8"});
    EXPECT_EQ(std::count(env.begin(), env.end(), "LC_ALL=C"), 1);
    EXPECT_EQ(std::count_if(env.begin(), env.end(), [](const std::string& e) { return e.starts_with("LC_ALL="); }), 1);
    EXPECT_NE(std::find(env.begin(), env.end(), "HOME=/home/me"), env.end());
}

TEST(RemoteCommand, TheDevelopmentHookIsAnAbsolutePathInDebugBuildsOnly) {
#ifndef NDEBUG
    EXPECT_EQ(ssh::developmentConfigFile({"AGENT_SPEED_SSH_CONFIG=/tmp/c"}), "/tmp/c");
#else
    EXPECT_EQ(ssh::developmentConfigFile({"AGENT_SPEED_SSH_CONFIG=/tmp/c"}), std::nullopt);
#endif
    EXPECT_EQ(ssh::developmentConfigFile({"AGENT_SPEED_SSH_CONFIG=relative"}), std::nullopt);
    EXPECT_EQ(ssh::developmentConfigFile({"OTHER=/tmp/c"}), std::nullopt);
}

TEST(RemoteCommand, MachinesMapToArchitectures) {
    EXPECT_EQ(archOfMachine("x86_64"), Arch::X86_64);
    EXPECT_EQ(archOfMachine("AMD64"), Arch::X86_64);
    EXPECT_EQ(archOfMachine("aarch64"), Arch::Aarch64);
    EXPECT_EQ(archOfMachine("arm64"), Arch::Aarch64);
    EXPECT_EQ(archOfMachine("armv7l"), std::nullopt);
    EXPECT_EQ(archOfMachine("riscv64"), std::nullopt);
}

TEST(RemoteCommand, RelayFileNames) {
    EXPECT_EQ(relay_name::bundled(Arch::X86_64), "agent-speed-relay-linux-x86_64");
    EXPECT_EQ(relay_name::bundled(Arch::Aarch64), "agent-speed-relay-linux-aarch64");
    EXPECT_EQ(relay_name::installed("1.6.1", Arch::Aarch64, "ABCDEF0123456789" + std::string(48, '0')),
              "agent-speed-relay-1.6.1-aarch64-abcdef012345");
    EXPECT_EQ(relay_name::installed("1.6 beta/1", Arch::X86_64, kDigest), "agent-speed-relay-1.6-beta-1-x86_64-aaaaaaaaaaaa");
    EXPECT_EQ(relay_name::installed("", Arch::X86_64, kDigest), "agent-speed-relay-0-x86_64-aaaaaaaaaaaa");
    EXPECT_TRUE(relay_name::isSafe("agent-speed-relay-1.6.1-x86_64-abc"));
    EXPECT_FALSE(relay_name::isSafe("-x"));
    EXPECT_FALSE(relay_name::isSafe(".."));
    EXPECT_FALSE(relay_name::isSafe("a/b"));
    EXPECT_FALSE(relay_name::isSafe("a b"));
}

TEST(RemoteCommand, CommandsAreFixedScriptsWithValidatedArguments) {
    EXPECT_EQ(*command::probe("~/.cache/agent-speed/"), "sh -c '" + scripts::kProbe + "' agent-speed ~/.cache/agent-speed");
    EXPECT_EQ(*command::upload("/srv/as", "agent-speed-relay-1-x86_64-aaa", std::string(64, 'A')),
              "sh -c '" + scripts::kUpload + "' agent-speed /srv/as agent-speed-relay-1-x86_64-aaa " + kDigest);
    EXPECT_EQ(*command::run("/srv/as", "agent-speed-relay-1-x86_64-aaa"),
              "sh -c '" + scripts::kRun + "' agent-speed /srv/as agent-speed-relay-1-x86_64-aaa");
    EXPECT_EQ(scripts::kRun, "chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"");
}

TEST(RemoteCommand, CommandsRefuseUnvalidatedValues) {
    EXPECT_FALSE(command::probe("~/x;id"));
    EXPECT_FALSE(command::probe("relative"));
    EXPECT_FALSE(command::upload("/srv", "a;b", kDigest));
    EXPECT_FALSE(command::upload("/srv", "name", "abc"));
    EXPECT_FALSE(command::upload("/srv", "name", std::string(63, 'a') + "g"));
    EXPECT_FALSE(command::run("/srv", "$(id)"));
    EXPECT_FALSE(command::run("/a/../b", "name"));
}

TEST(RemoteCommand, ScriptsSurviveEveryLoginShellsSingleQuotes) {
    for (const auto* script : {&scripts::kProbe, &scripts::kUpload, &scripts::kRun, &scripts::kSelectHashTool}) {
        EXPECT_TRUE(scripts::quotable(*script)) << *script;
    }
    EXPECT_FALSE(scripts::quotable("echo 'x'"));
    EXPECT_FALSE(scripts::quotable("echo !x"));
}

TEST(RemoteProbe, OutputParses) {
    const auto result = ProbeResult::parse("Welcome to devbox\nsystem Linux x86_64\ntool sha256sum\nhash " + std::string(64, 'B') +
                                           " agent-speed-relay-1-x86_64-bbb\nhash short agent-speed-relay-2\nhash " + kDigest +
                                           " other-file\nend\n");
    EXPECT_EQ(result.system, "Linux x86_64");
    EXPECT_EQ(result.kernel, "Linux");
    EXPECT_EQ(result.machine, "x86_64");
    EXPECT_EQ(result.hashTool, "sha256sum");
    EXPECT_TRUE(result.complete);
    ASSERT_EQ(result.hashes.size(), 1u);
    EXPECT_TRUE(result.hasInstalled("agent-speed-relay-1-x86_64-bbb", std::string(64, 'b')));
    EXPECT_EQ(result.verdict(), ProbeResult::Verdict::Supported);
    EXPECT_EQ(result.arch(), Arch::X86_64);
}

TEST(RemoteProbe, Verdicts) {
    EXPECT_EQ(ProbeResult::parse("system Linux x86_64\ntool sha256sum\n").verdict(), ProbeResult::Verdict::Malformed);
    EXPECT_EQ(ProbeResult::parse("tool sha256sum\nend\n").verdict(), ProbeResult::Verdict::Malformed);
    EXPECT_EQ(ProbeResult::parse("system Darwin arm64\ntool shasum -a 256\nend\n").verdict(), ProbeResult::Verdict::UnsupportedSystem);
    EXPECT_EQ(ProbeResult::parse("system Linux armv7l\ntool sha256sum\nend\n").verdict(), ProbeResult::Verdict::UnsupportedSystem);
    EXPECT_EQ(ProbeResult::parse("system Linux aarch64\ntool none\nend\n").verdict(), ProbeResult::Verdict::CannotVerify);
    EXPECT_EQ(ProbeResult::parse("system Linux aarch64\ntool openssl dgst -sha256 -r\nend\n").verdict(), ProbeResult::Verdict::Supported);
    EXPECT_EQ(ProbeResult::parse("system \x1b[31mLinux\x07 x86_64\nend\n").system, "[31mLinux x86_64");
}

TEST(RemoteProbe, UploadedHashIsTheScriptsLine) {
    EXPECT_EQ(ProbeResult::uploadedHash("hash " + std::string(64, 'C') + "\n"), std::string(64, 'c'));
    EXPECT_EQ(ProbeResult::uploadedHash("hash nothex\n"), std::nullopt);
    EXPECT_EQ(ProbeResult::uploadedHash(""), std::nullopt);
}

TEST(RemoteStatus, SshFailures) {
    EXPECT_EQ(classify::ended(255, "Host key verification failed.", classify::Stage::Probe).code, StatusCode::HostKeyUnknown);
    EXPECT_EQ(classify::ended(255, "me@devbox: Permission denied (publickey).", classify::Stage::Probe).code, StatusCode::AuthFailed);
    for (const auto* text : {"ssh: Could not resolve hostname x", "Connection timed out", "connect to host x port 22: Connection refused",
                             "No route to host", "Network is unreachable", "Connection closed by 10.0.0.1 port 22",
                             "kex_exchange_identification: read: Connection reset by peer", "", "\r\n",
                             "ssh: connect to host 192.0.2.10 port 2224: Permission denied",
                             "ssh: connect to host h port 22: Operation not permitted"}) {
        EXPECT_EQ(classify::ended(255, text, classify::Stage::Run).code, StatusCode::Unreachable) << text;
    }
    const auto other = classify::ended(255, "something else", classify::Stage::Probe);
    EXPECT_EQ(other.code, StatusCode::Exited);
    EXPECT_EQ(other.exitStatus, 255);
}

TEST(RemoteStatus, RemoteFailures) {
    EXPECT_EQ(classify::ended(126, "", classify::Stage::Run).code, StatusCode::NotExecutable);
    EXPECT_EQ(classify::ended(1, "sh: 1: /x: Permission denied", classify::Stage::Run).code, StatusCode::NotExecutable);
    EXPECT_EQ(classify::ended(1, "cannot execute binary file", classify::Stage::Run).code, StatusCode::NotExecutable);
    EXPECT_EQ(classify::ended(5, "", classify::Stage::Upload).code, StatusCode::CannotVerify);
    EXPECT_EQ(classify::ended(3, "", classify::Stage::Upload).code, StatusCode::UploadFailed);
    EXPECT_EQ(classify::ended(0, "", classify::Stage::Run).code, StatusCode::Exited);
    EXPECT_EQ(classify::ended(126, "", classify::Stage::Probe).code, StatusCode::Exited);
}

TEST(RemoteStatus, OnlyTheLast64KiBCount) {
    const std::string early = "Permission denied" + std::string(70 * 1024, 'x');
    EXPECT_EQ(classify::ended(255, early, classify::Stage::Probe).code, StatusCode::Exited);
    EXPECT_EQ(classify::ended(255, std::string(70 * 1024, 'x') + "Permission denied", classify::Stage::Probe).code, StatusCode::AuthFailed);
}

TEST(RemoteStatus, Groups) {
    for (const auto code : {StatusCode::AuthFailed, StatusCode::HostKeyUnknown, StatusCode::UnsupportedSystem, StatusCode::NotExecutable,
                            StatusCode::CannotVerify, StatusCode::UploadFailed, StatusCode::VersionMismatch, StatusCode::SshMissing,
                            StatusCode::InvalidDestination}) {
        EXPECT_TRUE(Status::of(code).needsUser()) << Status::of(code).name();
        EXPECT_FALSE(Status::of(code).retrying());
    }
    for (const auto code : {StatusCode::Unreachable, StatusCode::ProtocolError, StatusCode::Exited}) EXPECT_TRUE(Status::of(code).retrying());
    for (const auto code : {StatusCode::Connecting, StatusCode::Installing, StatusCode::Seeding}) EXPECT_TRUE(Status::of(code).inProgress());
    EXPECT_FALSE(Status::of(StatusCode::Connected).inProgress());
    EXPECT_EQ(Status::of(StatusCode::HostKeyUnknown).name(), "hostKeyUnknown");
    HostStatus status;
    EXPECT_FALSE(status.clockSkewed());
    status.clockOffsetMs = -5000;
    EXPECT_TRUE(status.clockSkewed());
    status.clockOffsetMs = 4999;
    EXPECT_FALSE(status.clockSkewed());
}

}  // namespace
