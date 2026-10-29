#include "agentspeed/file_reader.hpp"

#include <zstd.h>

#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include "agentspeed/line_splitter.hpp"
#include "agentspeed/zstd_frames.hpp"

namespace agentspeed {

namespace {

bool hasSuffix(const std::filesystem::path& path, std::string_view suffix) {
    const auto name = path.filename().string();
    return name.size() >= suffix.size() && name.compare(name.size() - suffix.size(), suffix.size(), suffix) == 0;
}

}  // namespace

ReadResult readWholeFile(const std::filesystem::path& path, const std::function<void(std::string_view)>& onLine) {
    ReadResult result;
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        result.ok = false;
        return result;
    }
    LineSplitter splitter;
    std::vector<char> input(1 << 20);
    if (hasSuffix(path, ".zstd")) {
        std::string bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        result.bytesRead = static_cast<std::int64_t>(bytes.size());
        ZstdFrameScanner scanner;
        scanner.scan(bytes, onLine, [&] { ++result.decodeErrors; });
        return result;
    }
    if (!hasSuffix(path, ".zst")) {
        while (in) {
            in.read(input.data(), static_cast<std::streamsize>(input.size()));
            const auto got = in.gcount();
            if (got <= 0) break;
            result.bytesRead += got;
            splitter.feed(std::string_view(input.data(), static_cast<std::size_t>(got)), onLine);
        }
        return result;
    }
    ZSTD_DStream* stream = ZSTD_createDStream();
    ZSTD_initDStream(stream);
    std::vector<char> output(ZSTD_DStreamOutSize());
    bool failed = false;
    while (!failed && in) {
        in.read(input.data(), static_cast<std::streamsize>(input.size()));
        const auto got = in.gcount();
        if (got <= 0) break;
        result.bytesRead += got;
        ZSTD_inBuffer inBuffer{input.data(), static_cast<std::size_t>(got), 0};
        while (inBuffer.pos < inBuffer.size) {
            ZSTD_outBuffer outBuffer{output.data(), output.size(), 0};
            const std::size_t code = ZSTD_decompressStream(stream, &outBuffer, &inBuffer);
            if (ZSTD_isError(code)) {
                failed = true;
                break;
            }
            splitter.feed(std::string_view(output.data(), outBuffer.pos), onLine);
        }
    }
    if (!failed) {
        // Flush output still held by the decoder.
        while (true) {
            ZSTD_inBuffer empty{nullptr, 0, 0};
            ZSTD_outBuffer outBuffer{output.data(), output.size(), 0};
            const std::size_t code = ZSTD_decompressStream(stream, &outBuffer, &empty);
            if (ZSTD_isError(code)) {
                failed = true;
                break;
            }
            splitter.feed(std::string_view(output.data(), outBuffer.pos), onLine);
            if (outBuffer.pos == 0) break;
        }
    }
    if (failed) result.decodeErrors = 1;
    ZSTD_freeDStream(stream);
    return result;
}

}  // namespace agentspeed
