#include "agentspeed/quota/routes/agy_report.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <charconv>
#include <cstdlib>
#include <filesystem>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/quota/routes/child_process.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"

extern char** environ;

namespace agentspeed::quota {

namespace {

constexpr std::string_view kWhitespace = " \t\n\r\v\f";
constexpr std::string_view kStub = "#!/bin/sh\nexit 1\n";

bool executableFile(const std::string& path) {
    struct stat info {};
    return !path.empty() && path.front() == '/' && ::stat(path.c_str(), &info) == 0 && S_ISREG(info.st_mode) &&
           ::access(path.c_str(), X_OK) == 0;
}

bool writeStub(const std::string& path) {
    const int fd = ::open(path.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0700);
    if (fd < 0) return false;
    const bool written = ::write(fd, kStub.data(), kStub.size()) == static_cast<ssize_t>(kStub.size()) && ::fchmod(fd, 0700) == 0;
    return ::close(fd) == 0 && written;
}

bool named(std::string_view entry, std::string_view name) {
    return entry.size() > name.size() && entry.substr(0, name.size()) == name && entry[name.size()] == '=';
}

std::vector<std::string> processEnvironment() {
    std::vector<std::string> out;
    for (char** entry = environ; entry && *entry; ++entry) out.emplace_back(*entry);
    return out;
}

// The run's private folder, removed with whatever agy left in it.
struct Folder {
    std::string path;
    ~Folder() {
        std::error_code ignored;
        if (!path.empty()) std::filesystem::remove_all(path, ignored);
    }
};

}  // namespace

namespace agy {

std::optional<Version> version(std::string_view text) {
    const auto first = text.find_first_not_of(kWhitespace);
    if (first == std::string_view::npos) return std::nullopt;
    text = text.substr(first, text.find_last_not_of(kWhitespace) - first + 1);
    Version out{};
    const char* cursor = text.data();
    const char* const end = text.data() + text.size();
    for (std::size_t part = 0; part < out.size(); ++part) {
        if (part > 0) {
            if (cursor == end || *cursor != '.') return std::nullopt;
            ++cursor;
        }
        if (cursor == end || *cursor < '0' || *cursor > '9') return std::nullopt;
        const auto [next, error] = std::from_chars(cursor, end, out[part]);
        if (error == std::errc::result_out_of_range) {
            out[part] = UINT64_MAX;
        } else if (error != std::errc()) {
            return std::nullopt;
        }
        cursor = next;
    }
    if (cursor != end) return std::nullopt;
    return out;
}

bool supported(std::string_view text) {
    const auto found = version(text);
    return found && *found >= kMinimum;
}

const std::vector<std::string>& reportArguments() {
    static const std::vector<std::string> kArguments = {"-p", "/usage", "--output-format", "json", "--print-timeout", "90s"};
    return kArguments;
}

std::optional<std::string> binary(const VendorEnvironment& env) {
    if (!env.home.empty()) {
        if (auto installed = env.inHome(".local/bin/agy"); executableFile(installed)) return installed;
    }
    const auto path = env.get("PATH");
    if (!path) return std::nullopt;
    std::string_view rest(*path);
    while (true) {
        const auto colon = rest.find(':');
        const auto entry = rest.substr(0, colon);
        if (!entry.empty() && entry.front() == '/') {
            if (auto candidate = fsx::join(entry, "agy"); executableFile(candidate)) return candidate;
        }
        if (colon == std::string_view::npos) return std::nullopt;
        rest.remove_prefix(colon + 1);
    }
}

}  // namespace agy

AgyReporter::AgyReporter(VendorEnvironment env, AgyReportLimits limits, std::function<std::vector<std::string>()> environment)
    : env_(std::move(env)), limits_(limits), environment_(std::move(environment)) {}

AgyReporter::~AgyReporter() {
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
    }
    stop_ = true;
    wake_.notify_all();
    if (worker_.joinable()) worker_.join();
}

bool AgyReporter::available() { return agy::binary(env_).has_value(); }

bool AgyReporter::start(std::function<void(AgyReportResult)> done) {
    std::lock_guard lock(mutex_);
    if (stopping_ || busy_) return false;
    if (!worker_.joinable()) {
        try {
            worker_ = std::thread([this] { work(); });
        } catch (...) {
            return false;
        }
    }
    busy_ = true;
    pending_ = std::move(done);
    wake_.notify_one();
    return true;
}

void AgyReporter::work() {
    std::unique_lock lock(mutex_);
    while (true) {
        wake_.wait(lock, [this] { return stopping_ || pending_ != nullptr; });
        if (stopping_) return;
        auto done = std::move(pending_);
        pending_ = nullptr;
        lock.unlock();
        AgyReportResult result;
        try {
            result = run();
        } catch (...) {
            result = {};
        }
        lock.lock();
        busy_ = false;
        if (stopping_) return;
        // Outside the lock: `done` may start the next report.
        lock.unlock();
        done(std::move(result));
        lock.lock();
    }
}

std::vector<std::string> AgyReporter::childEnvironment(const std::string& folder) const {
    auto entries = environment_ ? environment_() : processEnvironment();
    std::erase_if(entries, [](const std::string& entry) {
        return entry.find('=') == std::string::npos || named(entry, "PATH") || named(entry, "BROWSER") ||
               named(entry, "AGY_CLI_DISABLE_AUTO_UPDATE");
    });
    // A signed-out agy finds only the stubs to open a browser with.
    entries.push_back("PATH=" + folder + ":" + env_.get("PATH").value_or("/usr/bin:/bin"));
    entries.push_back("BROWSER=" + fsx::join(folder, "open"));
    // agy ignores `1`.
    entries.push_back("AGY_CLI_DISABLE_AUTO_UPDATE=true");
    return entries;
}

std::optional<agy::Version> AgyReporter::versionOf(const std::string& binary, const std::string& folder,
                                                   const std::vector<std::string>& environment) {
    std::optional<Identity> identity;
    if (char* resolved = ::realpath(binary.c_str(), nullptr)) {
        struct stat info {};
        if (::stat(resolved, &info) == 0) {
            identity = Identity{resolved, static_cast<std::int64_t>(info.st_size),
                                static_cast<std::int64_t>(info.st_mtim.tv_sec) * 1'000'000'000 + info.st_mtim.tv_nsec,
                                static_cast<std::uint64_t>(info.st_dev), static_cast<std::uint64_t>(info.st_ino)};
        }
        std::free(resolved);
    }
    if (identity) {
        if (const auto known = versions_.find(*identity); known != versions_.end()) return known->second;
    }
    const auto ran = child::run({binary, {"--version"}, environment, folder, limits_.versionTimeoutMs, limits_.versionCap},
                                [this] { return stop_.load(); });
    if (ran.end != child::End::Exited || ran.code != 0) return std::nullopt;
    const auto found = agy::version(ran.output);
    // A version that was read is kept, an older one too; a failed or unreadable answer is asked again next time.
    if (found && identity) versions_[*identity] = *found;
    return found;
}

AgyReportResult AgyReporter::run() {
    const auto found = agy::binary(env_);
    if (!found) return {};
    std::string pattern = fsx::join(env_.temporaryFolder(), "agent-speed-agy-XXXXXX");
    if (!::mkdtemp(pattern.data())) return {};
    const Folder folder{pattern};
    if (!writeStub(fsx::join(folder.path, "open")) || !writeStub(fsx::join(folder.path, "xdg-open"))) return {};
    const auto environment = childEnvironment(folder.path);
    const auto version = versionOf(*found, folder.path, environment);
    if (!version || *version < agy::kMinimum) return {};
    const auto ran = child::run({*found, agy::reportArguments(), environment, folder.path, limits_.reportTimeoutMs, limits_.reportCap},
                                [this] { return stop_.load(); });
    // Whatever the exit code, only the report decides; a cut-off, oversized or stopped run is a failure.
    if (ran.end != child::End::Exited && ran.end != child::End::Signaled) return {};
    auto windows = vendors::antigravityCliWindows(ran.output);
    if (!windows) return {};
    return {true, std::move(*windows), ran.endedMs};
}

}  // namespace agentspeed::quota
