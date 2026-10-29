#include "agentspeed/fs_util.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>

namespace agentspeed::fsx {

namespace {

Stat fromStat(const struct stat& st) {
    Stat s;
    s.length = static_cast<std::int64_t>(st.st_size);
    s.writeNs = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
    s.device = static_cast<std::int64_t>(st.st_dev);
    s.inode = static_cast<std::int64_t>(st.st_ino);
    s.directory = S_ISDIR(st.st_mode);
    s.regular = S_ISREG(st.st_mode);
    s.link = S_ISLNK(st.st_mode);
    return s;
}

char lowerAscii(char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c - 'A' + 'a') : c; }

}  // namespace

std::optional<Stat> statOf(const std::string& path, bool follow) {
    struct stat st {};
    const int rc = follow ? ::stat(path.c_str(), &st) : ::lstat(path.c_str(), &st);
    if (rc != 0) return std::nullopt;
    return fromStat(st);
}

std::vector<Entry> list(const std::string& folder) {
    std::vector<Entry> entries;
    DIR* dir = ::opendir(folder.c_str());
    if (!dir) return entries;
    const int fd = ::dirfd(dir);
    while (const dirent* e = ::readdir(dir)) {
        const std::string_view name(e->d_name);
        if (name == "." || name == "..") continue;
        struct stat st {};
        if (::fstatat(fd, e->d_name, &st, AT_SYMLINK_NOFOLLOW) != 0) continue;
        entries.push_back({std::string(name), join(folder, name), fromStat(st)});
    }
    ::closedir(dir);
    return entries;
}

bool isDirectory(const std::string& path) {
    const auto s = statOf(path, true);
    return s && s->directory;
}

bool isPlainDirectory(const std::string& path) {
    const auto s = statOf(path);
    return s && s->directory;
}

bool isRegularFile(const std::string& path) {
    const auto s = statOf(path, true);
    return s && s->regular;
}

bool isPlainFile(const std::string& path) {
    const auto s = statOf(path);
    return s && s->regular;
}

bool exists(const std::string& path) { return !path.empty() && statOf(path, true).has_value(); }

std::string join(std::string_view a, std::string_view b) {
    if (a.empty()) return std::string(b);
    std::string out(a);
    if (out.back() != '/') out += '/';
    out += b;
    return out;
}

std::string parentOf(std::string_view path) {
    const auto trimmed = trimTrailing(path);
    const auto slash = trimmed.rfind('/');
    if (slash == std::string::npos) return {};
    return slash == 0 ? std::string("/") : trimmed.substr(0, slash);
}

std::string trimTrailing(std::string_view path) {
    std::string out(path);
    while (out.size() > 1 && out.back() == '/') out.pop_back();
    return out;
}

std::string normalize(std::string_view path) {
    const bool absolute = !path.empty() && path.front() == '/';
    std::vector<std::string> parts;
    std::size_t i = 0;
    while (i <= path.size()) {
        const auto next = path.find('/', i);
        const auto part = path.substr(i, next == std::string_view::npos ? std::string_view::npos : next - i);
        if (part == "..") {
            if (!parts.empty() && parts.back() != "..") {
                parts.pop_back();
            } else if (!absolute) {
                parts.emplace_back("..");
            }
        } else if (!part.empty() && part != ".") {
            parts.emplace_back(part);
        }
        if (next == std::string_view::npos) break;
        i = next + 1;
    }
    std::string out = absolute ? "/" : "";
    for (std::size_t k = 0; k < parts.size(); ++k) {
        if (k > 0) out += '/';
        out += parts[k];
    }
    return out.empty() ? std::string(".") : out;
}

std::optional<std::vector<std::string>> partsBelow(const std::string& root, const std::string& path) {
    const auto r = normalize(root), p = normalize(path);
    const auto prefix = r == "/" ? r : r + "/";
    if (p.size() <= prefix.size() || p.compare(0, prefix.size(), prefix) != 0) return std::nullopt;
    std::vector<std::string> parts;
    std::size_t i = prefix.size();
    while (i < p.size()) {
        const auto next = p.find('/', i);
        parts.push_back(p.substr(i, next == std::string::npos ? std::string::npos : next - i));
        if (next == std::string::npos) break;
        i = next + 1;
    }
    return parts;
}

bool noLinkBelow(const std::string& root, const std::string& path) {
    const auto stop = normalize(root);
    auto current = normalize(path);
    while (current.size() > stop.size()) {
        const auto s = statOf(current);
        if (!s || s->link) return false;
        current = parentOf(current);
        if (current.empty()) break;
    }
    return true;
}

bool endsWithNoCase(std::string_view text, std::string_view suffix) {
    if (text.size() < suffix.size()) return false;
    return equalsNoCase(text.substr(text.size() - suffix.size()), suffix);
}

bool equalsNoCase(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (lowerAscii(a[i]) != lowerAscii(b[i])) return false;
    }
    return true;
}

}  // namespace agentspeed::fsx
