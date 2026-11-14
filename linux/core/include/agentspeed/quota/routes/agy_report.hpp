#pragma once

#include <array>
#include <atomic>
#include <compare>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 9.4 CLI usage report: `agy -p /usage --output-format json --print-timeout 90s`, run by the app (never by a
// remote host's relay) when no Antigravity server gives windows. agy asks Google itself with its own sign-in; Agent
// Speed never reads agy's token file or keyring, only what the report prints.
namespace agentspeed::quota {

namespace agy {

using Version = std::array<std::uint64_t, 3>;
// Older print modes sent an unknown slash command to the model as a prompt.
inline constexpr Version kMinimum = {1, 1, 11};

// `agy --version`'s trimmed output when it is `<major>.<minor>.<patch>` in digits.
std::optional<Version> version(std::string_view text);
bool supported(std::string_view text);
// After argv[0].
const std::vector<std::string>& reportArguments();
// `~/.local/bin/agy`, else the first `agy` on `PATH` (absolute entries only): a regular executable file.
std::optional<std::string> binary(const VendorEnvironment& env);

}  // namespace agy

struct AgyReportResult {
    bool ok = false;  // a report with windows
    std::vector<Window> windows;
    std::int64_t observedMs = 0;  // when the process ended
};

class AgyReport {
public:
    virtual ~AgyReport() = default;
    // A binary to run exists (nothing is started).
    virtual bool available() = 0;
    // Runs one report off the caller's thread; `done` is called once, on another thread. False while one runs.
    virtual bool start(std::function<void(AgyReportResult)> done) = 0;
};

struct AgyReportLimits {
    std::int64_t versionTimeoutMs = 3'000;
    std::size_t versionCap = 4 << 10;
    std::int64_t reportTimeoutMs = 100'000;
    std::size_t reportCap = 1 << 20;
};

// The report on a worker thread of its own, started at the first report. The version is asked once per binary (its
// resolved path, size, modification time, device and inode). Every run gets a new private folder (0700) in the
// temporary folder as its working folder, holding the `open` and `xdg-open` stubs that `PATH` and `BROWSER` lead to; the
// folder is removed afterwards. The child's environment is the app's (`environment`, else this process's) with `PATH`
// from `env`, the stubs and `AGY_CLI_DISABLE_AUTO_UPDATE=true`.
class AgyReporter final : public AgyReport {
public:
    explicit AgyReporter(VendorEnvironment env, AgyReportLimits limits = {},
                         std::function<std::vector<std::string>()> environment = nullptr);
    // Kills a running report and waits for its thread.
    ~AgyReporter() override;
    AgyReporter(const AgyReporter&) = delete;
    AgyReporter& operator=(const AgyReporter&) = delete;

    bool available() override;
    bool start(std::function<void(AgyReportResult)> done) override;
    // One report on the calling thread.
    AgyReportResult run();

private:
    struct Identity {
        std::string path;
        std::int64_t size = 0, modifiedNs = 0;
        std::uint64_t device = 0, inode = 0;
        auto operator<=>(const Identity&) const = default;
    };
    std::optional<agy::Version> versionOf(const std::string& binary, const std::string& folder, const std::vector<std::string>& environment);
    std::vector<std::string> childEnvironment(const std::string& folder) const;
    void work();

    VendorEnvironment env_;
    AgyReportLimits limits_;
    std::function<std::vector<std::string>()> environment_;
    std::map<Identity, agy::Version> versions_;  // the running report's thread only
    std::mutex mutex_;
    std::condition_variable wake_;
    std::function<void(AgyReportResult)> pending_;
    bool busy_ = false;
    bool stopping_ = false;
    std::atomic<bool> stop_{false};
    std::thread worker_;
};

}  // namespace agentspeed::quota
