#include "relay_process.hpp"

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cerrno>
#include <thread>

namespace relay_test {

namespace {

std::vector<char*> pointers(const std::vector<std::string>& strings) {
    std::vector<char*> out;
    for (const auto& s : strings) out.push_back(const_cast<char*>(s.c_str()));
    out.push_back(nullptr);
    return out;
}

}  // namespace

RelayProcess::RelayProcess(const std::string& binary, const std::vector<std::string>& args, const std::vector<std::string>& environment) {
    int input[2], output[2];
    if (::pipe(input) != 0 || ::pipe(output) != 0) return;
    std::vector<std::string> argv{binary};
    argv.insert(argv.end(), args.begin(), args.end());
    auto argp = pointers(argv);
    auto envp = pointers(environment);
    pid_ = ::fork();
    if (pid_ == 0) {
        ::dup2(input[0], STDIN_FILENO);
        ::dup2(output[1], STDOUT_FILENO);
        ::close(input[0]);
        ::close(input[1]);
        ::close(output[0]);
        ::close(output[1]);
        ::execve(binary.c_str(), argp.data(), envp.data());
        ::_exit(127);
    }
    ::close(input[0]);
    ::close(output[1]);
    in_ = input[1];
    out_ = output[0];
    ::signal(SIGPIPE, SIG_IGN);
}

RelayProcess::~RelayProcess() {
    closeInput();
    if (!wait(std::chrono::seconds(10)) && pid_ > 0) {
        ::kill(pid_, SIGKILL);
        int status = 0;
        ::waitpid(pid_, &status, 0);
    }
    if (out_ >= 0) ::close(out_);
}

bool RelayProcess::send(const std::string& line) {
    std::string bytes = line + "\n";
    std::size_t done = 0;
    while (done < bytes.size()) {
        const auto n = ::write(in_, bytes.data() + done, bytes.size() - done);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return false;
        done += static_cast<std::size_t>(n);
    }
    return true;
}

std::optional<std::string> RelayProcess::readLine(std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (true) {
        if (const auto newline = buffer_.find('\n'); newline != std::string::npos) {
            auto line = buffer_.substr(0, newline);
            buffer_.erase(0, newline + 1);
            return line;
        }
        const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
        if (left <= 0 || out_ < 0) return std::nullopt;
        pollfd fd{out_, POLLIN, 0};
        if (::poll(&fd, 1, static_cast<int>(left)) <= 0) continue;
        char chunk[65536];
        const auto n = ::read(out_, chunk, sizeof chunk);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return std::nullopt;
        buffer_.append(chunk, static_cast<std::size_t>(n));
    }
}

void RelayProcess::closeInput() {
    if (in_ >= 0) ::close(in_);
    in_ = -1;
}

std::optional<int> RelayProcess::wait(std::chrono::milliseconds timeout) {
    if (status_) return status_;
    if (pid_ <= 0) return -1;
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (true) {
        int status = 0;
        const auto done = ::waitpid(pid_, &status, WNOHANG);
        if (done == pid_) {
            status_ = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
            return status_;
        }
        if (std::chrono::steady_clock::now() >= deadline) return std::nullopt;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
}

std::pair<int, std::string> runToEnd(const std::string& binary, const std::vector<std::string>& args) {
    RelayProcess process(binary, args, {"LC_ALL=C"});
    process.closeInput();
    std::string out;
    while (const auto line = process.readLine(std::chrono::seconds(10))) out += *line + "\n";
    return {process.wait(std::chrono::seconds(10)).value_or(-1), out};
}

}  // namespace relay_test
