#include "agentspeed/quota/files.hpp"

#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include <atomic>
#include <cerrno>
#include <climits>
#include <cstdlib>
#include <ctime>

namespace agentspeed::quota::files {

namespace {

std::int64_t mtimeNsOf(const struct stat& st) {
    return static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + static_cast<std::int64_t>(st.st_mtim.tv_nsec);
}

Stamp stampFrom(const struct stat& st) { return {static_cast<std::int64_t>(st.st_size), mtimeNsOf(st)}; }

bool writeAll(int fd, std::string_view bytes) {
    std::size_t done = 0;
    while (done < bytes.size()) {
        const auto n = ::write(fd, bytes.data() + done, bytes.size() - done);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return false;
        done += static_cast<std::size_t>(n);
    }
    return true;
}

// `.<name>.agent-speed-<pid>-<n>.tmp`: unique per process and call; O_EXCL refuses anything already there.
std::string temporaryName(const std::string& path, unsigned attempt) {
    static std::atomic<unsigned> counter{0};
    const auto n = counter.fetch_add(1) + attempt;
    timespec now{};
    ::clock_gettime(CLOCK_MONOTONIC, &now);
    const auto parent = parentOf(path);
    return (parent.empty() ? std::string() : parent + "/") + "." + nameOf(path) + ".agent-speed-" + std::to_string(::getpid()) +
           "-" + std::to_string(n) + "-" + std::to_string(now.tv_nsec) + ".tmp";
}

}  // namespace

std::optional<Stamp> stampOf(const std::string& path, bool follow) {
    struct stat st {};
    const int rc = follow ? ::stat(path.c_str(), &st) : ::lstat(path.c_str(), &st);
    if (rc != 0 || !S_ISREG(st.st_mode)) return std::nullopt;
    return stampFrom(st);
}

bool isFolder(const std::string& path) {
    struct stat st {};
    return ::stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
}

ReadResult readFile(const std::string& path, std::size_t limit, bool follow) {
    ReadResult out;
    const int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC | O_NONBLOCK | (follow ? 0 : O_NOFOLLOW));
    if (fd < 0) {
        out.status = errno == ENOENT || errno == ENOTDIR ? ReadStatus::Missing : ReadStatus::Failed;
        return out;
    }
    struct stat st {};
    if (::fstat(fd, &st) != 0 || !S_ISREG(st.st_mode)) {
        ::close(fd);
        return out;
    }
    out.stamp = stampFrom(st);
    if (st.st_size < 0 || static_cast<std::uint64_t>(st.st_size) > limit) {
        ::close(fd);
        out.status = ReadStatus::TooLarge;
        return out;
    }
    out.bytes.reserve(static_cast<std::size_t>(st.st_size));
    char chunk[64 * 1024];
    while (true) {
        const auto n = ::read(fd, chunk, sizeof chunk);
        if (n < 0 && errno == EINTR) continue;
        if (n < 0) {
            ::close(fd);
            ::explicit_bzero(chunk, sizeof chunk);
            out.bytes.clear();
            return out;
        }
        if (n == 0) break;
        out.bytes.append(chunk, static_cast<std::size_t>(n));
        if (out.bytes.size() > limit) {
            out.bytes.clear();
            out.status = ReadStatus::TooLarge;
            break;
        }
    }
    ::close(fd);
    // A signed-in marker may hold tokens: no copy of it stays on the stack.
    ::explicit_bzero(chunk, sizeof chunk);
    if (out.status != ReadStatus::TooLarge) out.status = ReadStatus::Ok;
    return out;
}

bool replaceFile(const std::string& path, std::string_view bytes, mode_t mode) {
    for (unsigned attempt = 0; attempt < 8; ++attempt) {
        const auto temporary = temporaryName(path, attempt);
        const int fd = ::open(temporary.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, mode);
        if (fd < 0) {
            if (errno == EEXIST) continue;
            return false;
        }
        const bool written = writeAll(fd, bytes) && ::fchmod(fd, mode) == 0 && ::fsync(fd) == 0;
        const bool closed = ::close(fd) == 0;
        if (!written || !closed || ::rename(temporary.c_str(), path.c_str()) != 0) {
            ::unlink(temporary.c_str());
            return false;
        }
        return true;
    }
    return false;
}

bool makeFolders(const std::string& path, mode_t mode) {
    if (path.empty()) return false;
    if (isFolder(path)) return true;
    const auto parent = parentOf(path);
    if (!parent.empty() && parent != path && !makeFolders(parent, mode)) return false;
    if (::mkdir(path.c_str(), mode) == 0) return true;
    return errno == EEXIST && isFolder(path);
}

bool removeFile(const std::string& path) {
    struct stat st {};
    if (::lstat(path.c_str(), &st) != 0) return errno == ENOENT;
    if (S_ISDIR(st.st_mode)) return false;
    return ::unlink(path.c_str()) == 0 || errno == ENOENT;
}

std::optional<std::string> resolvedPath(const std::string& path) {
    char* resolved = ::realpath(path.c_str(), nullptr);
    if (!resolved) return std::nullopt;
    std::string out(resolved);
    std::free(resolved);
    return out;
}

std::optional<mode_t> modeOf(const std::string& path) {
    struct stat st {};
    if (::stat(path.c_str(), &st) != 0) return std::nullopt;
    return static_cast<mode_t>(st.st_mode & 07777);
}

std::string parentOf(std::string_view path) {
    auto end = path.size();
    while (end > 1 && path[end - 1] == '/') --end;
    const auto slash = path.substr(0, end).rfind('/');
    if (slash == std::string_view::npos) return {};
    if (slash == 0) return "/";
    return std::string(path.substr(0, slash));
}

std::string nameOf(std::string_view path) {
    auto end = path.size();
    while (end > 1 && path[end - 1] == '/') --end;
    const auto trimmed = path.substr(0, end);
    const auto slash = trimmed.rfind('/');
    return std::string(slash == std::string_view::npos ? trimmed : trimmed.substr(slash + 1));
}

bool validUtf8(std::string_view bytes) {
    std::size_t i = 0;
    while (i < bytes.size()) {
        const auto c = static_cast<unsigned char>(bytes[i]);
        if (c < 0x80) {
            ++i;
            continue;
        }
        std::size_t length = 0;
        std::uint32_t cp = 0;
        if (c >= 0xC2 && c <= 0xDF) {
            length = 2;
            cp = c & 0x1Fu;
        } else if (c >= 0xE0 && c <= 0xEF) {
            length = 3;
            cp = c & 0x0Fu;
        } else if (c >= 0xF0 && c <= 0xF4) {
            length = 4;
            cp = c & 0x07u;
        } else {
            return false;
        }
        if (i + length > bytes.size()) return false;
        for (std::size_t k = 1; k < length; ++k) {
            const auto next = static_cast<unsigned char>(bytes[i + k]);
            if ((next & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (next & 0x3Fu);
        }
        if ((length == 3 && (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))) || (length == 4 && (cp < 0x10000 || cp > 0x10FFFF))) {
            return false;
        }
        i += length;
    }
    return true;
}

}  // namespace agentspeed::quota::files
