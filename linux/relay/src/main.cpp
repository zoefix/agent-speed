#include <unistd.h>

#include <chrono>
#include <csignal>
#include <cstdio>
#include <string>
#include <string_view>
#include <thread>

#include "agentspeed/quota/paths.hpp"
#include "agentspeed/quota/statusline_helper.hpp"
#include "protocol.hpp"
#include "serve.hpp"

// agent-speed-relay (REMOTE 3.2), and under the name agent-speed-statusline the status line helper (REMOTE 11.3).
namespace {

constexpr auto kHangGrace = std::chrono::seconds(5);

int usage() {
    std::fputs("usage: agent-speed-relay --version | agent-speed-relay serve --folder <path> | "
               "agent-speed-statusline --folder <path>\n",
               stderr);
    return 2;
}

bool runsAsHelper(const char* argv0) {
    const std::string_view path = argv0 ? argv0 : "";
    const auto slash = path.rfind('/');
    return path.substr(slash == std::string_view::npos ? 0 : slash + 1) == agentspeed::quota::kHelperName;
}

}  // namespace

int main(int argc, char** argv) {
    using namespace agentspeed::relay;
    // Claude Code runs the copy with the user's status line input; it never speaks the protocol (QUOTAS 5.5).
    if (argc > 0 && runsAsHelper(argv[0])) return agentspeed::quota::helper::main(argc, argv);
    const std::string_view first = argc > 1 ? argv[1] : "";
    if (argc == 2 && first == "--version") {
        std::printf("agent-speed-relay %s protocol %d\n", AGENTSPEED_RELAY_VERSION, kProtocol);
        return 0;
    }
    if (argc != 4 || first != "serve" || std::string_view(argv[2]) != "--folder" || argv[3][0] == '\0') return usage();

    // A write to a closed pipe fails with EPIPE, which ends the relay (REMOTE 3.2).
    std::signal(SIGPIPE, SIG_IGN);
    ServeOptions options;
    options.folder = argv[3];
    options.version = AGENTSPEED_RELAY_VERSION;
    options.arch = buildArch();
    options.clock = systemClockMs;
    // A dropped connection never leaves the relay running: seeding or a history run cannot hold it past the grace.
    options.onInputClosed = [] {
        std::thread([] {
            std::this_thread::sleep_for(kHangGrace);
            _exit(0);
        }).detach();
    };
    const int code = serve(STDIN_FILENO, STDOUT_FILENO, options);
    _exit(code);
}
