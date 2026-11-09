#include "agentspeed/character/zip.hpp"

#include <fcntl.h>
#include <openssl/evp.h>
#include <sys/stat.h>
#include <unistd.h>
#include <zlib.h>

#include <array>
#include <cstring>
#include <fstream>
#include <memory>

namespace agentspeed::character {

namespace fs = std::filesystem;

namespace {

constexpr std::uint32_t kEndSignature = 0x06054b50;
constexpr std::uint32_t kCentralSignature = 0x02014b50;
constexpr std::uint32_t kLocalSignature = 0x04034b50;

std::uint16_t u16(const unsigned char* p) { return static_cast<std::uint16_t>(p[0] | (p[1] << 8)); }
std::uint32_t u32(const unsigned char* p) {
    return static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8) |
           (static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}

[[noreturn]] void malformed(const std::string& what = "zip") { throw PackFailure(PackError::Format, what); }

bool readAt(std::ifstream& in, std::uint64_t offset, unsigned char* out, std::size_t size) {
    in.clear();
    in.seekg(static_cast<std::streamoff>(offset));
    in.read(reinterpret_cast<char*>(out), static_cast<std::streamsize>(size));
    return static_cast<std::size_t>(in.gcount()) == size;
}

struct Fd {
    int fd = -1;
    ~Fd() {
        if (fd >= 0) ::close(fd);
    }
};

void writeAll(int fd, const unsigned char* data, std::size_t size) {
    while (size > 0) {
        const auto n = ::write(fd, data, size);
        if (n <= 0) throw PackFailure(PackError::MissingFile, "write");
        data += n;
        size -= static_cast<std::size_t>(n);
    }
}

}  // namespace

ZipArchive::ZipArchive(const fs::path& path) : path_(path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) malformed();
    in.seekg(0, std::ios::end);
    const auto fileSize = static_cast<std::uint64_t>(in.tellg());
    if (fileSize < 22) malformed();
    const std::uint64_t tail = std::min<std::uint64_t>(fileSize, 22 + 65535);
    std::vector<unsigned char> buffer(tail);
    if (!readAt(in, fileSize - tail, buffer.data(), buffer.size())) malformed();
    std::size_t end = std::string::npos;
    for (std::size_t i = buffer.size() - 22 + 1; i-- > 0;) {
        if (u32(&buffer[i]) == kEndSignature) {
            end = i;
            break;
        }
    }
    if (end == std::string::npos) malformed();
    const unsigned char* e = &buffer[end];
    const auto disk = u16(e + 4), cdDisk = u16(e + 6), onDisk = u16(e + 8), total = u16(e + 10);
    const auto cdSize = u32(e + 12), cdOffset = u32(e + 16);
    if (disk != 0 || cdDisk != 0 || onDisk != total || total == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) {
        malformed();
    }
    if (total > pack_layout::kMaxEntries) throw PackFailure(PackError::TooLarge);
    if (static_cast<std::uint64_t>(cdOffset) + cdSize > fileSize) malformed();
    std::vector<unsigned char> cd(cdSize);
    if (cdSize > 0 && !readAt(in, cdOffset, cd.data(), cd.size())) malformed();
    std::size_t pos = 0;
    for (unsigned i = 0; i < total; ++i) {
        if (pos + 46 > cd.size() || u32(&cd[pos]) != kCentralSignature) malformed();
        const unsigned char* c = &cd[pos];
        const auto madeBy = u16(c + 4);
        Item item;
        item.flags = u16(c + 8);
        item.method = u16(c + 10);
        item.crc = u32(c + 16);
        item.compressedSize = u32(c + 20);
        const std::uint64_t uncompressed = u32(c + 24);
        const auto nameLength = u16(c + 28), extraLength = u16(c + 30), commentLength = u16(c + 32);
        const auto external = u32(c + 38);
        item.offset = u32(c + 42);
        if (pos + 46 + nameLength + extraLength + commentLength > cd.size()) malformed();
        std::string name(reinterpret_cast<const char*>(c + 46), nameLength);
        if (item.compressedSize == 0xFFFFFFFF || uncompressed == 0xFFFFFFFF || item.offset == 0xFFFFFFFF) malformed();
        const bool unixHost = (madeBy >> 8) == 3;
        const bool link = unixHost && ((external >> 16) & 0170000) == 0120000;
        entries_.push_back({std::move(name), uncompressed, link});
        items_.push_back(item);
        pos += 46 + nameLength + extraLength + commentLength;
    }
}

void ZipArchive::extractAll(const fs::path& folder, std::uint64_t maxTotal) const {
    pack_layout::validate(entries_);
    std::ifstream in(path_, std::ios::binary);
    if (!in) malformed();
    std::uint64_t total = 0;
    std::vector<unsigned char> input(1 << 16), output(1 << 16);
    for (std::size_t index = 0; index < entries_.size(); ++index) {
        const auto& entry = entries_[index];
        const auto& item = items_[index];
        if (item.flags & 1) throw PackFailure(PackError::Format, entry.path);
        const fs::path target = folder / fs::path(entry.path).relative_path();
        if (entry.isDirectory()) {
            std::error_code ec;
            fs::create_directories(target, ec);
            if (ec) throw PackFailure(PackError::MissingFile, entry.path);
            continue;
        }
        {
            std::error_code ec;
            fs::create_directories(target.parent_path(), ec);
            if (ec) throw PackFailure(PackError::MissingFile, entry.path);
        }
        // Every folder on the way must be a real folder of the extraction, never a link.
        for (fs::path p = target.parent_path(); p != folder && p.has_relative_path(); p = p.parent_path()) {
            if (fs::is_symlink(fs::symlink_status(p))) throw PackFailure(PackError::SymbolicLink, entry.path);
            if (p.parent_path() == p) break;
        }
        std::array<unsigned char, 30> local{};
        if (!readAt(in, item.offset, local.data(), local.size()) || u32(local.data()) != kLocalSignature) malformed(entry.path);
        const std::uint64_t dataStart = item.offset + 30 + u16(&local[26]) + u16(&local[28]);
        if (item.method != 0 && item.method != 8) throw PackFailure(PackError::Format, entry.path);
        if (entry.uncompressedSize > maxTotal - total) throw PackFailure(PackError::TooLarge);

        Fd out;
        out.fd = ::open(target.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
        if (out.fd < 0) throw PackFailure(PackError::UnsafePath, entry.path);
        in.clear();
        in.seekg(static_cast<std::streamoff>(dataStart));
        std::uint64_t remaining = item.compressedSize;
        std::uint64_t written = 0;
        uLong crc = crc32(0, nullptr, 0);
        auto sink = [&](const unsigned char* data, std::size_t size) {
            written += size;
            if (written > entry.uncompressedSize) throw PackFailure(PackError::TooLarge, entry.path);
            crc = crc32(crc, data, static_cast<uInt>(size));
            writeAll(out.fd, data, size);
        };
        if (item.method == 0) {
            if (item.compressedSize != entry.uncompressedSize) malformed(entry.path);
            while (remaining > 0) {
                const auto chunk = static_cast<std::size_t>(std::min<std::uint64_t>(remaining, input.size()));
                in.read(reinterpret_cast<char*>(input.data()), static_cast<std::streamsize>(chunk));
                if (static_cast<std::size_t>(in.gcount()) != chunk) malformed(entry.path);
                sink(input.data(), chunk);
                remaining -= chunk;
            }
        } else {
            z_stream z{};
            if (inflateInit2(&z, -MAX_WBITS) != Z_OK) malformed(entry.path);
            std::unique_ptr<z_stream, void (*)(z_stream*)> guard(&z, [](z_stream* s) { inflateEnd(s); });
            int status = Z_OK;
            while (status != Z_STREAM_END) {
                if (z.avail_in == 0) {
                    if (remaining == 0) malformed(entry.path);
                    const auto chunk = static_cast<std::size_t>(std::min<std::uint64_t>(remaining, input.size()));
                    in.read(reinterpret_cast<char*>(input.data()), static_cast<std::streamsize>(chunk));
                    if (static_cast<std::size_t>(in.gcount()) != chunk) malformed(entry.path);
                    remaining -= chunk;
                    z.next_in = input.data();
                    z.avail_in = static_cast<uInt>(chunk);
                }
                z.next_out = output.data();
                z.avail_out = static_cast<uInt>(output.size());
                status = inflate(&z, Z_NO_FLUSH);
                if (status != Z_OK && status != Z_STREAM_END) malformed(entry.path);
                sink(output.data(), output.size() - z.avail_out);
            }
        }
        if (written != entry.uncompressedSize || crc != item.crc) malformed(entry.path);
        total += written;
    }
}

std::string sha256File(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return {};
    std::unique_ptr<EVP_MD_CTX, void (*)(EVP_MD_CTX*)> ctx(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    if (!ctx || EVP_DigestInit_ex(ctx.get(), EVP_sha256(), nullptr) != 1) return {};
    std::vector<char> buffer(1 << 16);
    while (in) {
        in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const auto n = in.gcount();
        if (n > 0 && EVP_DigestUpdate(ctx.get(), buffer.data(), static_cast<std::size_t>(n)) != 1) return {};
    }
    if (in.bad()) return {};
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    if (EVP_DigestFinal_ex(ctx.get(), digest, &length) != 1) return {};
    static const char* hex = "0123456789abcdef";
    std::string out;
    for (unsigned i = 0; i < length; ++i) {
        out += hex[digest[i] >> 4];
        out += hex[digest[i] & 15];
    }
    return out;
}

}  // namespace agentspeed::character
