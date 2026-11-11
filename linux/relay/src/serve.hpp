#pragma once

#include <chrono>
#include <cstdint>
#include <functional>
#include <string>

#include "quota_session.hpp"

// `agent-speed-relay serve` (REMOTE 3.2, 5): the protocol on two file descriptors until the input closes.
namespace agentspeed::relay {

struct ServeOptions {
    std::string folder;
    std::string version;
    std::string arch;
    // The host's wall clock (Unix ms); every time the relay sends is the host's (REMOTE 6).
    std::function<std::int64_t()> clock;
    std::chrono::milliseconds startTimeout{30'000};
    std::chrono::milliseconds tick{500};
    // After `start`, this long without any line from the app ends the serve (REMOTE 4.4): the app pings every 30 s, so
    // its ssh is gone (a cut network can leave the host's sshd unaware for many minutes).
    std::chrono::milliseconds inputSilence{150'000};
    // Called once by the reading thread when the input reached its end (the executable arms a hard exit there).
    std::function<void()> onInputClosed;
    // REMOTE 11: where the quota readers look and how they ask; `folder` and `version` are filled in from above.
    QuotaOptions quota;
};

// 0 when the input closed, 1 after a fatal `error` line, a failed write or input silence.
int serve(int inFd, int outFd, const ServeOptions& options);

std::int64_t systemClockMs();
// `x86_64` or `aarch64` for the executable's own architecture.
std::string buildArch();

}  // namespace agentspeed::relay
