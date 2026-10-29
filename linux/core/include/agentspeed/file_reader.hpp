#pragma once

#include <cstdint>
#include <filesystem>
#include <functional>
#include <string_view>

namespace agentspeed {

struct ReadResult {
    bool ok = true;  // false when the file could not be opened
    std::int64_t decodeErrors = 0;  // METRICS 5.3: a zstd decoding error stops the file and counts one malformed line
    std::int64_t bytesRead = 0;
};

// Reads a whole transcript and hands its complete lines to `onLine` (METRICS 5); `.zst` files are decompressed as
// one stream (5.3), `.zstd` files frame by frame (5.1). An incomplete last line is not delivered.
ReadResult readWholeFile(const std::filesystem::path& path, const std::function<void(std::string_view)>& onLine);

}  // namespace agentspeed
