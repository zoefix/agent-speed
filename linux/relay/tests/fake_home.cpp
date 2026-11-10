#include "fake_home.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cstdlib>

#include "agentspeed/fs_util.hpp"
#include "fixture_support.hpp"

namespace fs = std::filesystem;

namespace relay_test {

namespace {

// Each fixture folder plays one folder of the home (parser-expected.json `_comment`); "" lays out its entries.
const std::vector<std::pair<std::string, std::string>> kFolders = {
    {"codex", ".codex"},
    {"claude", ".claude"},
    {"dsh", ".dsh"},
    {"opencode", ".local/share/opencode"},
    {"gemini/tmp", ".gemini/tmp"},
    {"gemini/settings.json", ".gemini/settings.json"},
    {"gemini/projects.json", ".gemini/projects.json"},
    {"antigravity", ".gemini"},
    {"omp", ".omp"},
    {"workbuddy", ""},
    {"pi/agent", ".pi/agent"},
    {"pi/custom-sessions", "pi/custom-sessions"},
    {"openclaw", ""},
    {"hermes/home", ".hermes"},
    {"droid", ""},
    {"qoder-cli", ".qoder"},
};

bool under(const std::string& path, const std::string& prefix) {
    return path == prefix || (path.size() > prefix.size() && path.compare(0, prefix.size(), prefix) == 0 &&
                              (path[prefix.size()] == '/' || path[prefix.size()] == '#'));
}

}  // namespace

fs::path makeTempDir(const std::string& prefix) {
    std::string pattern = "/tmp/" + prefix + "-XXXXXX";
    return fs::path(::mkdtemp(pattern.data()));
}

void setMtime(const fs::path& path, std::int64_t ms) {
    struct timespec times[2];
    times[0].tv_sec = times[1].tv_sec = static_cast<time_t>(ms / 1000);
    times[0].tv_nsec = times[1].tv_nsec = static_cast<long>((ms % 1000) * 1'000'000);
    ::utimensat(AT_FDCWD, path.c_str(), times, 0);
}

FakeHome::FakeHome(const fs::path& home) : home_(agentspeed::fsx::normalize(home.string())) {
    const auto fixtures = fixtures::fixtureDir();
    for (const auto& [fixture, target] : kFolders) {
        if (!target.empty()) {
            layout_.emplace_back(fixture, target);
            continue;
        }
        for (const auto& entry : fs::directory_iterator(fixtures / fixture)) {
            const auto name = entry.path().filename().string();
            layout_.emplace_back(fixture + "/" + name, name);
        }
    }
    for (const auto& [fixture, target] : layout_) {
        const auto destination = fs::path(home_) / target;
        fs::create_directories(destination.parent_path());
        fs::copy(fixtures / fixture, destination, fs::copy_options::recursive | fs::copy_options::copy_symlinks);
    }
    fixtures::JsonFile expected(fixtures / "parser-expected.json");
    for (const auto& [relative, ms] : expected.root()["mtimes"].members()) {
        if (const auto path = toHome(relative)) setMtime(*path, *fixtures::optInt(ms));
    }
}

std::optional<std::string> FakeHome::toHome(const std::string& fixture) const {
    const std::pair<std::string, std::string>* best = nullptr;
    for (const auto& entry : layout_) {
        if (under(fixture, entry.first) && (!best || entry.first.size() > best->first.size())) best = &entry;
    }
    if (!best) return std::nullopt;
    return home_ + "/" + best->second + fixture.substr(best->first.size());
}

std::string FakeHome::toFixture(const std::string& path) const {
    if (path.compare(0, home_.size() + 1, home_ + "/") != 0) return path;
    const auto relative = path.substr(home_.size() + 1);
    const std::pair<std::string, std::string>* best = nullptr;
    for (const auto& entry : layout_) {
        if (under(relative, entry.second) && (!best || entry.second.size() > best->second.size())) best = &entry;
    }
    if (!best) return path;
    return best->first + relative.substr(best->second.size());
}

}  // namespace relay_test
