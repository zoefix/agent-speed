#include <brotli/decode.h>
#include <zlib.h>
#include <zstd.h>

#include <memory>

#include "agentspeed/quota/desktop_entry.hpp"

// QUOTAS 4.2: the content codings of a cached response body. Every decoder stops past `limit` bytes of output, so a
// small body cannot expand without bound, and a damaged, truncated or trailing-garbage body gives nothing.
namespace agentspeed::quota::desktop {

namespace {

constexpr std::size_t kChunk = 64 * 1024;

// Every frame of the input (skippable ones too), which must be consumed whole.
std::optional<std::string> zstdDecode(std::string_view input, std::size_t limit) {
    if (input.empty()) return std::nullopt;
    const std::unique_ptr<ZSTD_DCtx, decltype(&ZSTD_freeDCtx)> context(ZSTD_createDCtx(), ZSTD_freeDCtx);
    if (!context) return std::nullopt;
    std::string out;
    std::string chunk(kChunk, '\0');
    ZSTD_inBuffer in{input.data(), input.size(), 0};
    while (true) {
        ZSTD_outBuffer target{chunk.data(), chunk.size(), 0};
        const auto before = in.pos;
        const auto status = ZSTD_decompressStream(context.get(), &target, &in);
        if (ZSTD_isError(status)) return std::nullopt;
        out.append(chunk.data(), target.pos);
        if (out.size() > limit) return std::nullopt;
        if (in.pos == in.size && status == 0) return out;
        if (in.pos == before && target.pos == 0) return std::nullopt;
    }
}

// Gzip members (RFC 1952) with their CRC and length checked by zlib; several members follow each other.
std::optional<std::string> gzipDecode(std::string_view input, std::size_t limit) {
    if (input.empty()) return std::nullopt;
    z_stream stream{};
    if (inflateInit2(&stream, 16 + MAX_WBITS) != Z_OK) return std::nullopt;
    const std::unique_ptr<z_stream, decltype(&inflateEnd)> guard(&stream, inflateEnd);
    stream.next_in = reinterpret_cast<Bytef*>(const_cast<char*>(input.data()));
    stream.avail_in = static_cast<uInt>(input.size());
    std::string out;
    std::string chunk(kChunk, '\0');
    while (true) {
        stream.next_out = reinterpret_cast<Bytef*>(chunk.data());
        stream.avail_out = static_cast<uInt>(chunk.size());
        const int status = inflate(&stream, Z_NO_FLUSH);
        out.append(chunk.data(), chunk.size() - stream.avail_out);
        if (out.size() > limit) return std::nullopt;
        if (status == Z_STREAM_END) {
            if (stream.avail_in == 0) return out;
            if (inflateReset(&stream) != Z_OK) return std::nullopt;
            continue;
        }
        if (status != Z_OK || (stream.avail_in == 0 && stream.avail_out != 0)) return std::nullopt;
    }
}

// One Brotli stream (RFC 7932), consumed whole.
std::optional<std::string> brotliDecode(std::string_view input, std::size_t limit) {
    if (input.empty()) return std::nullopt;
    const std::unique_ptr<BrotliDecoderState, decltype(&BrotliDecoderDestroyInstance)> state(
        BrotliDecoderCreateInstance(nullptr, nullptr, nullptr), BrotliDecoderDestroyInstance);
    if (!state) return std::nullopt;
    std::size_t availableIn = input.size();
    const auto* nextIn = reinterpret_cast<const std::uint8_t*>(input.data());
    std::string out;
    std::string chunk(kChunk, '\0');
    while (true) {
        std::size_t availableOut = chunk.size();
        auto* nextOut = reinterpret_cast<std::uint8_t*>(chunk.data());
        const auto result = BrotliDecoderDecompressStream(state.get(), &availableIn, &nextIn, &availableOut, &nextOut, nullptr);
        out.append(chunk.data(), chunk.size() - availableOut);
        if (out.size() > limit) return std::nullopt;
        if (result == BROTLI_DECODER_RESULT_SUCCESS) {
            if (availableIn == 0) return out;
            return std::nullopt;
        }
        if (result != BROTLI_DECODER_RESULT_NEEDS_MORE_OUTPUT) return std::nullopt;
    }
}

}  // namespace

std::optional<std::string> decodeBody(std::string_view body, Coding coding, std::size_t limit) {
    switch (coding) {
        case Coding::Identity: return body.size() <= limit ? std::optional<std::string>(std::string(body)) : std::nullopt;
        case Coding::Zstd: return zstdDecode(body, limit);
        case Coding::Gzip: return gzipDecode(body, limit);
        case Coding::Brotli: return brotliDecode(body, limit);
    }
    return std::nullopt;
}

}  // namespace agentspeed::quota::desktop
