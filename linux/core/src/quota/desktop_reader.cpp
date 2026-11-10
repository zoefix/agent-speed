#include "agentspeed/quota/desktop_reader.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>

namespace agentspeed::quota::desktop {

namespace {

std::int64_t msOf(const timespec& t) { return static_cast<std::int64_t>(t.tv_sec) * 1000 + t.tv_nsec / 1'000'000; }

bool readAt(int fd, char* buffer, std::size_t size, off_t offset) {
    std::size_t done = 0;
    while (done < size) {
        const auto n = ::pread(fd, buffer + done, size - done, offset + static_cast<off_t>(done));
        if (n <= 0) return false;
        done += static_cast<std::size_t>(n);
    }
    return true;
}

}  // namespace

bool Reader::tick(const Tick& tick) {
    const auto before = snapshot_;
    if (!tick.enabled) {
        forget();
        return before != snapshot_;
    }
    if (tick.desktopStarted) searchWanted_ = true;
    const bool checkDue = !checkedMs_ || tick.nowMs - *checkedMs_ >= kCheckIntervalMs;
    bool searched = false;
    if (searchDue(tick) && (!missingFolderMs_ || checkDue || tick.desktopStarted)) {
        searched = search(tick.nowMs);
    }
    if (searched || checkDue) {
        check();
        checkedMs_ = tick.nowMs;
    }
    return before != snapshot_;
}

std::vector<std::string> Reader::candidates() const {
    std::vector<std::string> out;
    for (const auto& [name, candidate] : candidates_) out.push_back(name);
    return out;
}

bool Reader::searchDue(const Tick& tick) const {
    if (searchWanted_) return true;
    if (!tick.desktopRunning || (searchedMs_ && tick.nowMs - *searchedMs_ < kSearchIntervalMs)) return false;
    std::optional<std::int64_t> newest;
    for (const auto& [name, candidate] : candidates_) {
        if (!candidate.stamp) continue;
        const std::int64_t ms = candidate.stamp->mtimeNs / 1'000'000;
        if (!newest || ms > *newest) newest = ms;
    }
    return !newest || tick.nowMs - *newest > kStaleCandidateMs;
}

std::optional<std::vector<Reader::Listed>> Reader::listEntries() const {
    const int folderFd = ::open(folder_.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (folderFd < 0) return std::nullopt;
    DIR* dir = ::fdopendir(folderFd);
    if (!dir) {
        ::close(folderFd);
        return std::nullopt;
    }
    std::vector<Listed> out;
    while (const dirent* item = ::readdir(dir)) {
        const std::string_view name(item->d_name);
        if (!isEntryFileName(name) || (item->d_type != DT_REG && item->d_type != DT_UNKNOWN)) continue;
        struct stat st {};
        if (::fstatat(folderFd, item->d_name, &st, AT_SYMLINK_NOFOLLOW) != 0 || !S_ISREG(st.st_mode)) continue;
        out.push_back({std::string(name), msOf(st.st_mtim)});
    }
    ::closedir(dir);
    return out;
}

bool Reader::isUsageHeader(const std::string& name) const {
    const int fd = ::open((folder_ + "/" + name).c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    if (fd < 0) return false;
    char header[kHeaderSize];
    bool usage = false;
    if (readAt(fd, header, sizeof header, 0)) {
        const auto length = keyLength(std::string_view(header, sizeof header));
        if (length && *length > 0 && *length <= kMaxKeyBytes) {
            std::string key(*length, '\0');
            usage = readAt(fd, key.data(), key.size(), static_cast<off_t>(kHeaderSize)) && isUsageKey(key);
        }
    }
    ::close(fd);
    return usage;
}

bool Reader::search(std::int64_t nowMs) {
    const auto listed = listEntries();
    if (!listed) {
        // QUOTAS 4.4: the search runs at start while the folder exists; a missing folder is looked for at each check.
        missingFolderMs_ = nowMs;
        return false;
    }
    missingFolderMs_.reset();
    searchWanted_ = false;
    searchedMs_ = nowMs;
    lastSearchReads_ = 0;
    ++searches_;
    std::vector<Listed> recent;
    for (const auto& file : *listed) {
        if (nowMs - file.mtimeMs <= kMaxEntryAgeMs) recent.push_back(file);
    }
    std::sort(recent.begin(), recent.end(), [](const Listed& a, const Listed& b) {
        return a.mtimeMs != b.mtimeMs ? a.mtimeMs > b.mtimeMs : a.name < b.name;
    });
    for (const auto& file : recent) {
        if (lastSearchReads_ >= kMaxHeaderReads) break;
        ++lastSearchReads_;
        if (!isUsageHeader(file.name)) continue;
        candidates_.try_emplace(file.name);
        break;
    }
    return true;
}

void Reader::check() {
    ++checks_;
    for (auto it = candidates_.begin(); it != candidates_.end();) {
        const auto path = folder_ + "/" + it->first;
        const auto stamp = files::stampOf(path, false);
        if (!stamp) {
            it = candidates_.erase(it);
            continue;
        }
        auto& candidate = it->second;
        if (candidate.stamp != stamp) {
            const auto read = stamp->size <= static_cast<std::int64_t>(kFileLimit) ? files::readFile(path, kFileLimit, false) : files::ReadResult{};
            candidate.stamp = read.status == files::ReadStatus::Ok ? read.stamp : *stamp;
            candidate.entry = read.status == files::ReadStatus::Ok ? parseUsageEntry(read.bytes, candidate.stamp->mtimeNs / 1'000'000)
                                                                   : std::nullopt;
        }
        ++it;
    }
    choose();
}

void Reader::choose() {
    std::vector<FoundEntry> found;
    for (const auto& [name, candidate] : candidates_) {
        if (candidate.entry && candidate.stamp) found.push_back({name, *candidate.entry, candidate.stamp->mtimeNs});
    }
    const auto* best = newestEntry(found);
    if (!best) {
        snapshot_.reset();
        snapshotFile_.reset();
        return;
    }
    snapshot_ = Snapshot{SourceKind::ClaudeDesktop, best->entry.observedMs, best->entry.windows};
    snapshotFile_ = best->file;
}

void Reader::forget() {
    candidates_.clear();
    snapshot_.reset();
    snapshotFile_.reset();
    searchWanted_ = true;
    searchedMs_.reset();
    checkedMs_.reset();
    missingFolderMs_.reset();
}

}  // namespace agentspeed::quota::desktop
