#pragma once

#include <sys/types.h>

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

// The few file operations of the quota routes (QUOTAS 4, 5, 6), in plain POSIX: reads are read-only, and the bridge's
// and helper's writes go to an exclusively created temporary file that is then renamed over the target.
namespace agentspeed::quota::files {

// A regular file's size and modification time, to tell that it changed.
struct Stamp {
    std::int64_t size = 0;
    std::int64_t mtimeNs = 0;
    bool operator==(const Stamp&) const = default;
};
// nullopt unless the path is a regular file (`follow`: through a symbolic link).
std::optional<Stamp> stampOf(const std::string& path, bool follow);
bool isFolder(const std::string& path);

enum class ReadStatus : std::uint8_t { Ok, Missing, TooLarge, Failed };
struct ReadResult {
    ReadStatus status = ReadStatus::Failed;
    std::string bytes;
    Stamp stamp;
};
// The whole file, read-only; larger than `limit` bytes is TooLarge. `follow`: a symbolic link is opened where it points.
ReadResult readFile(const std::string& path, std::size_t limit, bool follow);

// Writes `bytes` to a temporary file beside `path` (O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC), gives it `mode`, syncs
// it and renames it over `path`. The temporary file is removed on any failure.
bool replaceFile(const std::string& path, std::string_view bytes, mode_t mode);
// `mkdir -p` with `mode` for the folders it creates.
bool makeFolders(const std::string& path, mode_t mode);
// Removes a file (never a folder); true when it is gone.
bool removeFile(const std::string& path);
// The target of a path through every symbolic link; nullopt when it does not resolve.
std::optional<std::string> resolvedPath(const std::string& path);
// The permission bits of an existing file (through links).
std::optional<mode_t> modeOf(const std::string& path);

std::string parentOf(std::string_view path);
std::string nameOf(std::string_view path);

// RFC 3629 UTF-8: no overlong forms, surrogates or code points above U+10FFFF.
bool validUtf8(std::string_view bytes);

}  // namespace agentspeed::quota::files
