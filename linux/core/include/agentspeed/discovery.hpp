#pragma once

#include <cstdint>
#include <limits>
#include <string>
#include <vector>

#include "agentspeed/locations.hpp"

// METRICS 2: transcript discovery per family, never following links, at most 20 000 files per root.
namespace agentspeed::discovery {

constexpr int kMaxDepth = 6;
constexpr std::size_t kMaxFilesPerRoot = 20'000;
// Lifts the scan caps too (daily history).
constexpr std::size_t kUnlimited = std::numeric_limits<std::size_t>::max();

struct DiscoveredFile {
    std::string path;
    std::int64_t length = 0;
    // The time that orders startup reads; Grok Build and Factory Droid take the newer of the session's two files.
    std::int64_t writeNs = 0;
};

// Families without transcript files (OpenCode, ZCode, OpenClaw, Hermes) enumerate nothing.
std::vector<DiscoveredFile> enumerate(const std::string& root, RootFamily family, std::size_t limit = kMaxFilesPerRoot);
// A path reported by a watcher, checked with the family's rules.
bool isAcceptablePath(const std::string& root, const std::string& path, RootFamily family);
bool isTranscriptName(std::string_view name, RootFamily family);

// Factory Droid: the transcript of a sidecar path inside the layout, else empty.
std::string droidTranscriptOfSidecar(const std::string& root, const std::string& path);

}  // namespace agentspeed::discovery
