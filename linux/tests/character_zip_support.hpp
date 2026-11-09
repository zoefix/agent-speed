#pragma once

#include <zlib.h>

#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "character_support.hpp"

// Zip archives and manifests for the character download tests.
namespace test_zip {

inline std::string manifestJSON(const std::string& id = "claude", int packVersion = 1,
                                const std::map<std::string, std::string>& poses = {{"idle", "poses/idle"}},
                                std::optional<std::string> minApp = std::nullopt, const std::string& vendor = "anthropic") {
    std::string p;
    for (const auto& [band, path] : poses) p += (p.empty() ? "" : ",") + ("\"" + band + "\":\"" + path + "\"");
    std::string s = R"({"format":"agent-speed-character-pack","version":1,"id":")" + id + R"(","packVersion":)" +
                    std::to_string(packVersion) +
                    R"(,"name":{"zh-Hant":"Claude 娘","zh-Hans":"Claude 娘","en":"Claude-chan","ja":"Claudeちゃん"},)"
                    R"("vendors":[")" + vendor + R"("],"accent":"#D97757","artist":{"name":"ZipZipPipe"},"poses":{)" + p +
                    R"(},"preview":"preview.png")";
    if (minApp) s += R"(,"minApp":")" + *minApp + "\"";
    return s + "}";
}

// A zip writer for tests: stored or deflated entries, optional Unix symlink mode.
struct ZipFile {
    struct Item {
        std::string name, data;
        bool deflate = false, link = false;
    };
    std::vector<Item> items;

    static void put16(std::string& o, unsigned v) {
        o += static_cast<char>(v & 0xFF);
        o += static_cast<char>((v >> 8) & 0xFF);
    }
    static void put32(std::string& o, std::uint32_t v) {
        put16(o, v & 0xFFFF);
        put16(o, v >> 16);
    }

    std::string bytes() const {
        std::string out, cd;
        for (const auto& it : items) {
            std::string payload = it.data;
            unsigned method = 0;
            if (it.deflate) {
                z_stream z{};
                deflateInit2(&z, 6, Z_DEFLATED, -MAX_WBITS, 8, Z_DEFAULT_STRATEGY);
                std::string buf(compressBound(static_cast<uLong>(it.data.size())) + 64, '\0');
                z.next_in = reinterpret_cast<Bytef*>(const_cast<char*>(it.data.data()));
                z.avail_in = static_cast<uInt>(it.data.size());
                z.next_out = reinterpret_cast<Bytef*>(buf.data());
                z.avail_out = static_cast<uInt>(buf.size());
                deflate(&z, Z_FINISH);
                buf.resize(z.total_out);
                deflateEnd(&z);
                payload = buf;
                method = 8;
            }
            const auto crc = static_cast<std::uint32_t>(
                crc32(0, reinterpret_cast<const Bytef*>(it.data.data()), static_cast<uInt>(it.data.size())));
            const auto offset = static_cast<std::uint32_t>(out.size());
            put32(out, 0x04034b50);
            put16(out, 20);
            put16(out, 0);
            put16(out, method);
            put16(out, 0);
            put16(out, 0);
            put32(out, crc);
            put32(out, static_cast<std::uint32_t>(payload.size()));
            put32(out, static_cast<std::uint32_t>(it.data.size()));
            put16(out, static_cast<unsigned>(it.name.size()));
            put16(out, 0);
            out += it.name + payload;
            put32(cd, 0x02014b50);
            put16(cd, (3 << 8) | 20);
            put16(cd, 20);
            put16(cd, 0);
            put16(cd, method);
            put16(cd, 0);
            put16(cd, 0);
            put32(cd, crc);
            put32(cd, static_cast<std::uint32_t>(payload.size()));
            put32(cd, static_cast<std::uint32_t>(it.data.size()));
            put16(cd, static_cast<unsigned>(it.name.size()));
            put16(cd, 0);
            put16(cd, 0);
            put16(cd, 0);
            put16(cd, 0);
            put32(cd, (it.link ? 0120777u : 0100644u) << 16);
            put32(cd, offset);
            cd += it.name;
        }
        const auto cdOffset = static_cast<std::uint32_t>(out.size());
        out += cd;
        put32(out, 0x06054b50);
        put16(out, 0);
        put16(out, 0);
        put16(out, static_cast<unsigned>(items.size()));
        put16(out, static_cast<unsigned>(items.size()));
        put32(out, static_cast<std::uint32_t>(cd.size()));
        put32(out, cdOffset);
        put16(out, 0);
        return out;
    }

    std::filesystem::path write(const std::filesystem::path& path) const {
        tiny_pack::write(path, bytes());
        return path;
    }
};

// A whole pack as a zip, as the release publishes it.
inline ZipFile packZip(const std::string& id = "claude", int packVersion = 1, const std::string& vendor = "anthropic") {
    ZipFile z;
    z.items.push_back({"manifest.json", manifestJSON(id, packVersion, {{"idle", "poses/idle"}}, std::nullopt, vendor), true});
    z.items.push_back({"preview.png", "\x89PNG", false});
    z.items.push_back({"poses/", "", false});
    z.items.push_back({"poses/idle/rig.json", R"({"format":"agent-speed-character","version":1})", true});
    z.items.push_back({"poses/idle/atlas.png", std::string(5000, 'x'), true});
    return z;
}

}  // namespace test_zip
