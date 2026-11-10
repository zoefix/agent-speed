#include <dirent.h>
#include <fcntl.h>
#include <pwd.h>
#include <sys/stat.h>
#include <sys/utsname.h>
#include <unistd.h>

#include <cerrno>
#include <cstdint>
#include <cstdlib>

#include "agentspeed/quota/routes/environment.hpp"

namespace agentspeed::quota {

namespace {

constexpr std::size_t kChunk = 64 * 1024;

class Descriptor {
public:
    explicit Descriptor(int fd) : fd_(fd) {}
    ~Descriptor() {
        if (fd_ >= 0) ::close(fd_);
    }
    Descriptor(const Descriptor&) = delete;
    Descriptor& operator=(const Descriptor&) = delete;
    int get() const { return fd_; }
    bool valid() const { return fd_ >= 0; }

private:
    int fd_;
};

int openRegular(const std::string& path, struct stat& info) {
    const int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC | O_NOCTTY | O_NONBLOCK);
    if (fd < 0) return -1;
    if (::fstat(fd, &info) != 0 || !S_ISREG(info.st_mode)) {
        ::close(fd);
        return -1;
    }
    return fd;
}

// Reads to the end, or fails once more than `cap` bytes came.
bool readAll(int fd, std::size_t cap, std::string& out) {
    char buffer[kChunk];
    for (;;) {
        const ssize_t n = ::read(fd, buffer, sizeof buffer);
        if (n < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        if (n == 0) return true;
        if (out.size() + static_cast<std::size_t>(n) > cap) return false;
        out.append(buffer, static_cast<std::size_t>(n));
    }
}

bool writeAll(int fd, const char* data, std::size_t size) {
    while (size > 0) {
        const ssize_t n = ::write(fd, data, size);
        if (n < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        data += n;
        size -= static_cast<std::size_t>(n);
    }
    return true;
}

class SystemFiles final : public Files {
public:
    std::optional<fsx::Stat> stat(const std::string& path) override { return fsx::statOf(path, true); }

    std::optional<std::string> read(const std::string& path, std::size_t cap) override {
        struct stat info {};
        Descriptor fd(openRegular(path, info));
        if (!fd.valid() || static_cast<std::uint64_t>(info.st_size) > cap) return std::nullopt;
        std::string out;
        if (!readAll(fd.get(), cap, out)) {
            clear(out);
            return std::nullopt;
        }
        return out;
    }

    std::optional<std::string> readRange(const std::string& path, std::int64_t offset, std::size_t length) override {
        struct stat info {};
        Descriptor fd(openRegular(path, info));
        if (!fd.valid() || offset < 0) return std::nullopt;
        std::string out(length, '\0');
        std::size_t got = 0;
        while (got < length) {
            const ssize_t n = ::pread(fd.get(), out.data() + got, length - got, static_cast<off_t>(offset) + static_cast<off_t>(got));
            if (n < 0) {
                if (errno == EINTR) continue;
                return std::nullopt;
            }
            if (n == 0) break;
            got += static_cast<std::size_t>(n);
        }
        out.resize(got);
        return out;
    }

    std::vector<std::string> list(const std::string& folder) override {
        std::vector<std::string> out;
        for (auto& entry : fsx::list(folder)) out.push_back(std::move(entry.name));
        return out;
    }

    std::optional<std::string> readLink(const std::string& path) override {
        char buffer[4096];
        const ssize_t n = ::readlink(path.c_str(), buffer, sizeof buffer);
        if (n < 0 || static_cast<std::size_t>(n) >= sizeof buffer) return std::nullopt;
        return std::string(buffer, static_cast<std::size_t>(n));
    }

    bool copy(const std::string& from, const std::string& to, std::size_t cap) override {
        struct stat info {};
        Descriptor in(openRegular(from, info));
        if (!in.valid() || static_cast<std::uint64_t>(info.st_size) > cap) return false;
        Descriptor out(::open(to.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600));
        if (!out.valid()) return false;
        char buffer[kChunk];
        std::size_t total = 0;
        for (;;) {
            const ssize_t n = ::read(in.get(), buffer, sizeof buffer);
            if (n < 0) {
                if (errno == EINTR) continue;
                return false;
            }
            if (n == 0) return true;
            total += static_cast<std::size_t>(n);
            if (total > cap || !writeAll(out.get(), buffer, static_cast<std::size_t>(n))) return false;
        }
    }

private:
    static void clear(std::string& text) {
        for (char& c : text) c = '\0';
        text.clear();
    }
};

std::string passwdHome() {
    const auto* entry = ::getpwuid(::getuid());
    return entry && entry->pw_dir ? std::string(entry->pw_dir) : std::string();
}

}  // namespace

std::shared_ptr<Files> systemFiles() {
    static const auto shared = std::make_shared<SystemFiles>();
    return shared;
}

VendorEnvironment VendorEnvironment::system() {
    VendorEnvironment env;
    const char* home = std::getenv("HOME");
    env.home = home && *home ? std::string(home) : passwdHome();
    env.variable = [](std::string_view name) -> std::optional<std::string> {
        const char* value = std::getenv(std::string(name).c_str());
        if (!value) return std::nullopt;
        return std::string(value);
    };
    env.files = systemFiles();
    return env;
}

VendorEnvironment VendorEnvironment::fixed(std::string homeFolder, std::map<std::string, std::string> values,
                                           std::shared_ptr<Files> fileSystem) {
    VendorEnvironment env;
    env.home = std::move(homeFolder);
    env.variable = [vars = std::move(values)](std::string_view name) -> std::optional<std::string> {
        const auto found = vars.find(std::string(name));
        if (found == vars.end()) return std::nullopt;
        return found->second;
    };
    env.files = fileSystem ? std::move(fileSystem) : systemFiles();
    return env;
}

std::optional<std::string> VendorEnvironment::get(std::string_view name) const {
    if (!variable) return std::nullopt;
    auto value = variable(name);
    if (!value || value->empty()) return std::nullopt;
    return value;
}

std::optional<std::string> VendorEnvironment::folder(std::string_view name) const {
    auto value = get(name);
    if (!value) return std::nullopt;
    if (*value == "~") return home;
    if (value->rfind("~/", 0) == 0) return fsx::join(home, value->substr(2));
    return value;
}

std::string VendorEnvironment::inHome(std::string_view relative) const { return fsx::join(home, relative); }

std::string VendorEnvironment::configHome() const {
    auto config = folder("XDG_CONFIG_HOME");
    if (config && !config->empty() && config->front() == '/') return *config;
    return inHome(".config");
}

std::string VendorEnvironment::temporaryFolder() const {
    if (!tempFolder.empty()) return tempFolder;
    auto tmp = get("TMPDIR");
    if (tmp && tmp->front() == '/') return *tmp;
    return "/tmp";
}

std::string VendorEnvironment::architecture() const {
    if (!machine.empty()) return machine;
    struct utsname name {};
    if (::uname(&name) == 0) return name.machine;
    return "x86_64";
}

std::shared_ptr<const TimeZone> VendorEnvironment::zone() const {
    if (timeZone) return timeZone;
    if (auto local = TimeZone::load("")) return local;
    return TimeZone::utc();
}

Files& VendorEnvironment::fs() const { return files ? *files : *systemFiles(); }

}  // namespace agentspeed::quota
