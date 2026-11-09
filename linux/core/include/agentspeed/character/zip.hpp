#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "agentspeed/character/catalog.hpp"

namespace agentspeed::character {

// A read-only zip archive (stored and deflated entries, no ZIP64, no encryption), enough for character packs. Opening
// reads the central directory only; `extractAll` writes every entry below a folder, refusing anything unsafe.
class ZipArchive {
public:
    // Throws PackFailure(Format) when the file is not a zip this reader understands.
    explicit ZipArchive(const std::filesystem::path& path);

    const std::vector<ZipEntry>& entries() const { return entries_; }

    // Writes every entry below `folder` (an empty folder that exists). Paths must be safe (pack_layout::isSafe), links
    // are refused, files are created new (never through an existing name or link), sizes and CRCs must match the
    // directory and the total stays under `maxTotal`. Throws PackFailure.
    void extractAll(const std::filesystem::path& folder, std::uint64_t maxTotal = pack_layout::kMaxExpandedSize) const;

private:
    struct Item {
        std::uint16_t method = 0;
        std::uint16_t flags = 0;
        std::uint32_t crc = 0;
        std::uint64_t compressedSize = 0;
        std::uint64_t offset = 0;
    };

    std::filesystem::path path_;
    std::vector<ZipEntry> entries_;
    std::vector<Item> items_;
};

// The lowercase hex SHA-256 of a file; empty when it cannot be read.
std::string sha256File(const std::filesystem::path& path);

}  // namespace agentspeed::character
