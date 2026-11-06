#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <utility>
#include <vector>

// spec/fixtures laid out as a home folder, where every source's default root (METRICS 2) finds its fixture folder
// without any environment variable, with the modification times of parser-expected.json `mtimes`.
namespace relay_test {

class FakeHome {
public:
    explicit FakeHome(const std::filesystem::path& home);

    const std::string& path() const { return home_; }
    // A fixture path (relative to spec/fixtures) → its absolute path in the home; nullopt outside the layout.
    std::optional<std::string> toHome(const std::string& fixture) const;
    // An absolute path in the home (or an id that starts with one) → the fixture path; the text itself otherwise.
    std::string toFixture(const std::string& path) const;

private:
    std::string home_;
    std::vector<std::pair<std::string, std::string>> layout_;  // (fixture, home-relative)
};

std::filesystem::path makeTempDir(const std::string& prefix);
void setMtime(const std::filesystem::path& path, std::int64_t ms);

}  // namespace relay_test
