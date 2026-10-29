#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Small POSIX file-system helpers that never follow links unless asked to.
namespace agentspeed::fsx {

struct Stat {
    std::int64_t length = 0;
    std::int64_t writeNs = 0;
    std::int64_t device = 0;
    std::int64_t inode = 0;
    bool directory = false;
    bool regular = false;
    bool link = false;
};

struct Entry {
    std::string name;
    std::string path;
    Stat stat;
};

// lstat (follow = false) or stat.
std::optional<Stat> statOf(const std::string& path, bool follow = false);
// The entries of a folder except `.` and `..`, each with its lstat; empty when it cannot be listed.
std::vector<Entry> list(const std::string& folder);

bool isDirectory(const std::string& path);       // follows links
bool isPlainDirectory(const std::string& path);  // a folder that is not a link
bool isRegularFile(const std::string& path);     // follows links
bool isPlainFile(const std::string& path);       // a regular file that is not a link
bool exists(const std::string& path);

std::string join(std::string_view a, std::string_view b);
std::string parentOf(std::string_view path);
std::string trimTrailing(std::string_view path);
// `.` and `..` resolved lexically, duplicate separators and the trailing one removed.
std::string normalize(std::string_view path);
// The components of `path` below `root`; nullopt when it is not inside it (or is the root itself).
std::optional<std::vector<std::string>> partsBelow(const std::string& root, const std::string& path);
// No link on the path itself or on any folder between `root` and it.
bool noLinkBelow(const std::string& root, const std::string& path);

bool endsWithNoCase(std::string_view text, std::string_view suffix);
bool equalsNoCase(std::string_view a, std::string_view b);

}  // namespace agentspeed::fsx
