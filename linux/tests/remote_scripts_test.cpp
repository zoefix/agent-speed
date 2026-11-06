#include <gtest/gtest.h>
#include <poll.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>
#include <thread>

#include "agentspeed/remote_relay_bundle.hpp"
#include "agentspeed/remote_ssh.hpp"
#include "agentspeed/remote_status.hpp"

// REMOTE 4.2, 4.3: the remote commands executed for real, as the login shell on a host runs them (`sh -c <command>`).
namespace {

namespace fs = std::filesystem;
using namespace agentspeed::remote;

struct Ran {
    int status = -1;
    std::string out, err;
};

Ran runShell(const std::string& command, const std::string& input, const std::vector<std::string>& environment) {
    int in[2], out[2], err[2];
    if (::pipe(in) != 0 || ::pipe(out) != 0 || ::pipe(err) != 0) return {};
    const pid_t pid = ::fork();
    if (pid == 0) {
        ::dup2(in[0], 0);
        ::dup2(out[1], 1);
        ::dup2(err[1], 2);
        for (int fd : {in[0], in[1], out[0], out[1], err[0], err[1]}) ::close(fd);
        std::vector<char*> envp;
        for (const auto& e : environment) envp.push_back(const_cast<char*>(e.c_str()));
        envp.push_back(nullptr);
        char* argv[] = {const_cast<char*>("/bin/sh"), const_cast<char*>("-c"), const_cast<char*>(command.c_str()), nullptr};
        ::execve("/bin/sh", argv, envp.data());
        ::_exit(127);
    }
    ::close(in[0]);
    ::close(out[1]);
    ::close(err[1]);
    std::thread writer([fd = in[1], &input] {
        std::size_t done = 0;
        while (done < input.size()) {
            const auto n = ::write(fd, input.data() + done, input.size() - done);
            if (n <= 0) break;
            done += static_cast<std::size_t>(n);
        }
        ::close(fd);
    });
    Ran ran;
    pollfd fds[2] = {{out[0], POLLIN, 0}, {err[0], POLLIN, 0}};
    int open = 2;
    while (open > 0) {
        ::poll(fds, 2, 10'000);
        for (auto& f : fds) {
            if (f.fd < 0 || !f.revents) continue;
            char buffer[4096];
            const auto n = ::read(f.fd, buffer, sizeof buffer);
            if (n <= 0) {
                ::close(f.fd);
                f.fd = -1;
                --open;
            } else {
                (f.fd == out[0] ? ran.out : ran.err).append(buffer, static_cast<std::size_t>(n));
            }
        }
    }
    writer.join();
    int status = 0;
    ::waitpid(pid, &status, 0);
    ran.status = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    return ran;
}

class RemoteScripts : public ::testing::Test {
protected:
    void SetUp() override {
        root_ = fs::temp_directory_path() / ("as-scripts-" + std::to_string(::getpid()) + "-" + ::testing::UnitTest::GetInstance()->current_test_info()->name());
        fs::remove_all(root_);
        fs::create_directories(root_ / "home");
        environment_ = {"HOME=" + (root_ / "home").string(), "PATH=/usr/local/bin:/usr/bin:/bin", "LC_ALL=C"};
    }
    void TearDown() override { fs::remove_all(root_); }

    static void write(const fs::path& path, const std::string& text, fs::perms perms = fs::perms::owner_read | fs::perms::owner_write) {
        fs::create_directories(path.parent_path());
        std::ofstream(path, std::ios::binary) << text;
        fs::permissions(path, perms);
    }
    static std::string sha(const fs::path& path) { return *fileSha256(path.string()); }
    static fs::perms permsOf(const fs::path& path) { return fs::status(path).permissions() & fs::perms::all; }

    fs::path root_;
    std::vector<std::string> environment_;
};

TEST_F(RemoteScripts, ProbeListsSystemToolAndRelayHashes) {
    const auto folder = root_ / "relay";
    write(folder / "agent-speed-relay-a", "one");
    write(folder / "agent-speed-relay-b", "two");
    write(folder / "other", "three");
    const auto ran = runShell(*command::probe(folder.string()), "", environment_);
    ASSERT_EQ(ran.status, 0) << ran.err;
    const auto result = ProbeResult::parse(ran.out);
    EXPECT_TRUE(result.complete);
    EXPECT_EQ(result.kernel, "Linux");
    EXPECT_TRUE(archOfMachine(result.machine)) << result.machine;
    EXPECT_EQ(result.hashTool, "sha256sum");
    ASSERT_EQ(result.hashes.size(), 2u);
    EXPECT_EQ(result.hashes.at("agent-speed-relay-a"), sha(folder / "agent-speed-relay-a"));
    EXPECT_EQ(result.hashes.at("agent-speed-relay-b"), sha(folder / "agent-speed-relay-b"));
    EXPECT_EQ(result.verdict(), ProbeResult::Verdict::Supported);
}

TEST_F(RemoteScripts, ProbeOfAMissingFolderListsNothingAndExpandsTheTilde) {
    write(root_ / "home/.cache/agent-speed/agent-speed-relay-x", "x");
    const auto ran = runShell(*command::probe("~/.cache/agent-speed"), "", environment_);
    ASSERT_EQ(ran.status, 0) << ran.err;
    EXPECT_EQ(ProbeResult::parse(ran.out).hashes.size(), 1u);
    const auto missing = runShell(*command::probe((root_ / "missing").string()), "", environment_);
    const auto result = ProbeResult::parse(missing.out);
    EXPECT_TRUE(result.complete);
    EXPECT_TRUE(result.hashes.empty());
}

TEST_F(RemoteScripts, ProbeWithoutAHashToolSaysNone) {
    const auto bin = root_ / "bin";
    fs::create_directories(bin);
    fs::create_symlink(fs::exists("/bin/uname") ? "/bin/uname" : "/usr/bin/uname", bin / "uname");
    fs::create_symlink("/bin/sh", bin / "sh");
    auto env = environment_;
    env[1] = "PATH=" + bin.string();
    const auto ran = runShell(*command::probe((root_ / "relay").string()), "", env);
    const auto result = ProbeResult::parse(ran.out);
    EXPECT_TRUE(result.complete) << ran.out << ran.err;
    EXPECT_EQ(result.hashTool, std::nullopt);
    EXPECT_EQ(result.verdict(), ProbeResult::Verdict::CannotVerify);
}

TEST_F(RemoteScripts, UploadVerifiesRenamesAndCleansOnlyRelayFiles) {
    const auto folder = root_ / "relay";
    write(folder / "agent-speed-relay-old", "old");
    write(folder / ".upload-123", "partial");
    write(folder / "keep.txt", "mine");
    write(folder / "history.sqlite", "db");
    const std::string payload(300'000, 'R');
    write(root_ / "payload", payload);
    const auto digest = sha(root_ / "payload");
    const std::string name = "agent-speed-relay-1.0-x86_64-" + digest.substr(0, 12);
    const auto ran = runShell(*command::upload(folder.string(), name, digest), payload, environment_);
    ASSERT_EQ(ran.status, 0) << ran.err;
    EXPECT_EQ(ProbeResult::uploadedHash(ran.out), digest);
    EXPECT_EQ(sha(folder / name), digest);
    EXPECT_EQ(permsOf(folder / name), fs::perms::owner_all);
    EXPECT_EQ(permsOf(folder), fs::perms::owner_all);
    EXPECT_FALSE(fs::exists(folder / "agent-speed-relay-old"));
    EXPECT_FALSE(fs::exists(folder / ".upload-123"));
    EXPECT_TRUE(fs::exists(folder / "keep.txt"));
    EXPECT_TRUE(fs::exists(folder / "history.sqlite"));
}

TEST_F(RemoteScripts, UploadCreatesTheFolderPrivate) {
    fs::create_directories(root_ / "home/.cache");
    fs::permissions(root_ / "home/.cache", fs::perms::owner_all | fs::perms::group_all | fs::perms::others_all);
    write(root_ / "payload", "relay");
    const auto digest = sha(root_ / "payload");
    const auto ran = runShell(*command::upload("~/.cache/agent-speed/nested", "agent-speed-relay-x", digest), "relay", environment_);
    ASSERT_EQ(ran.status, 0) << ran.err;
    EXPECT_EQ(permsOf(root_ / "home/.cache/agent-speed/nested"), fs::perms::owner_all);
    EXPECT_TRUE(fs::exists(root_ / "home/.cache/agent-speed/nested/agent-speed-relay-x"));
}

TEST_F(RemoteScripts, UploadMismatchDeletesTheCopyAndExits3) {
    const auto folder = root_ / "relay";
    write(folder / "agent-speed-relay-current", "current");
    const auto ran = runShell(*command::upload(folder.string(), "agent-speed-relay-new", std::string(64, '0')), "different bytes", environment_);
    EXPECT_EQ(ran.status, command::kMismatchStatus);
    EXPECT_TRUE(ProbeResult::uploadedHash(ran.out));
    EXPECT_FALSE(fs::exists(folder / "agent-speed-relay-new"));
    EXPECT_TRUE(fs::exists(folder / "agent-speed-relay-current"));
    for (const auto& entry : fs::directory_iterator(folder)) EXPECT_FALSE(entry.path().filename().string().starts_with(".upload-"));
}

TEST_F(RemoteScripts, RunExecsTheRelayWithItsFolder) {
    const auto folder = root_ / "home/.cache/agent-speed";
    write(folder / "agent-speed-relay-x", "#!/bin/sh\necho \"args:$*\"\nread line\necho \"got:$line\"\n", fs::perms::owner_all);
    const auto ran = runShell(*command::run("~/.cache/agent-speed", "agent-speed-relay-x"), "hello\n", environment_);
    ASSERT_EQ(ran.status, 0) << ran.err;
    EXPECT_EQ(ran.out, "args:serve --folder " + folder.string() + "\ngot:hello\n");
    EXPECT_EQ(permsOf(folder), fs::perms::owner_all);
}

TEST_F(RemoteScripts, RunOfANonExecutableOrMissingFileIsNotExecutable) {
    const auto folder = root_ / "relay";
    write(folder / "agent-speed-relay-x", "#!/bin/sh\necho ran\n");
    const auto ran = runShell(*command::run(folder.string(), "agent-speed-relay-x"), "", environment_);
    EXPECT_EQ(ran.status, 126);
    EXPECT_EQ(classify::ended(ran.status, ran.err, classify::Stage::Run).code, StatusCode::NotExecutable);
    const auto missing = runShell(*command::run(folder.string(), "agent-speed-relay-none"), "", environment_);
    EXPECT_EQ(missing.status, 126);
}

TEST_F(RemoteScripts, ValidatedArgumentsCannotInject) {
    const auto odd = root_ / "a~b" / "c-d.e_f";
    write(odd / "agent-speed-relay-z", "z");
    const auto ran = runShell(*command::probe(odd.string()), "", environment_);
    EXPECT_EQ(ProbeResult::parse(ran.out).hashes.size(), 1u);
    EXPECT_FALSE(command::probe((root_ / "x;touch pwned").string()));
    EXPECT_FALSE(command::probe((root_ / "$(touch pwned)").string()));
    EXPECT_FALSE(command::probe((root_ / "x y").string()));
    EXPECT_FALSE(command::run(root_.string(), "x`touch pwned`"));
    EXPECT_FALSE(fs::exists(root_ / "home/pwned"));
}

}  // namespace
