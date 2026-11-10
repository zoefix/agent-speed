#pragma once

#include <fcntl.h>
#include <gtest/gtest.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/quota/window.hpp"
#include "fixture_support.hpp"

// spec/fixtures/quota-cases.json and the temporary folders of the quota tests.
namespace quota_test {

namespace fs = std::filesystem;
using agentspeed::json::Value;

inline const fixtures::JsonFile& cases() {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-cases.json");
    return file;
}
inline Value section(const char* name) { return cases().root()[name]; }
inline std::int64_t fixtureNow() { return *cases().root()["now"].truncated(); }

inline fs::path entryFile(const std::string& name) { return fixtures::fixtureDir() / "quota" / name; }

// A fixture window `{kind, name, minutes, usedPercent, resetsAtMs}`.
inline agentspeed::quota::Window windowOf(const Value& v) {
    agentspeed::quota::Window w;
    w.kind = *agentspeed::quota::kindFromId(*v["kind"].string());
    w.name = fixtures::optString(v["name"]);
    w.minutes = fixtures::optInt(v["minutes"]);
    w.usedPercent = *v["usedPercent"].number();
    w.resetsAtMs = fixtures::optInt(v["resetsAtMs"]);
    return w;
}

inline std::vector<agentspeed::quota::Window> windowsOf(const Value& array) {
    std::vector<agentspeed::quota::Window> out;
    for (const auto& item : array.elements()) out.push_back(windowOf(item));
    return out;
}

inline void expectWindows(const std::vector<agentspeed::quota::Window>& got, const Value& want) {
    const auto expected = windowsOf(want);
    ASSERT_EQ(got.size(), expected.size());
    for (std::size_t i = 0; i < got.size(); ++i) {
        SCOPED_TRACE("window " + std::to_string(i));
        EXPECT_EQ(got[i].kind, expected[i].kind);
        EXPECT_EQ(got[i].name, expected[i].name);
        EXPECT_EQ(got[i].minutes, expected[i].minutes);
        EXPECT_DOUBLE_EQ(got[i].usedPercent, expected[i].usedPercent);
        EXPECT_EQ(got[i].resetsAtMs, expected[i].resetsAtMs);
    }
}

// A fresh folder under the system's temporary folder, removed with everything in it.
class TempDir {
public:
    TempDir() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-quota-XXXXXX").string();
        path_ = ::mkdtemp(pattern.data());
    }
    ~TempDir() {
        std::error_code ec;
        fs::remove_all(path_, ec);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;
    const fs::path& path() const { return path_; }
    std::string str() const { return path_.string(); }
    fs::path operator/(const std::string& name) const { return path_ / name; }

private:
    fs::path path_;
};

inline void writeFile(const fs::path& path, const std::string& bytes) {
    fs::create_directories(path.parent_path());
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    out << bytes;
}

inline std::optional<std::string> readFile(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::nullopt;
    return fixtures::readText(path);
}

inline void setMtimeMs(const fs::path& path, std::int64_t ms) {
    struct timespec times[2];
    times[0].tv_sec = static_cast<time_t>(ms / 1000);
    times[0].tv_nsec = static_cast<long>((ms % 1000) * 1'000'000);
    times[1] = times[0];
    ASSERT_EQ(::utimensat(AT_FDCWD, path.c_str(), times, 0), 0) << path;
}

inline unsigned modeOf(const fs::path& path) {
    struct stat st {};
    if (::stat(path.c_str(), &st) != 0) return 0;
    return st.st_mode & 07777;
}

inline ino_t inodeOf(const fs::path& path) {
    struct stat st {};
    if (::stat(path.c_str(), &st) != 0) return 0;
    return st.st_ino;
}

}  // namespace quota_test
