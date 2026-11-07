#include <openssl/evp.h>

#include <algorithm>

#include "agentspeed/lan_websocket.hpp"
#include "agentspeed/remote_protocol.hpp"

namespace agentspeed::lan::ws {

namespace {

constexpr std::string_view kGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

bool base64Char(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/'; }

std::uint64_t bigEndian(std::string_view bytes) {
    std::uint64_t value = 0;
    for (const char b : bytes) value = (value << 8) | static_cast<unsigned char>(b);
    return value;
}

}  // namespace

std::string accept(std::string_view key) {
    const std::string input = std::string(key) + std::string(kGuid);
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    EVP_Digest(input.data(), input.size(), digest, &length, EVP_sha1(), nullptr);
    return remote::encode::base64(std::string_view(reinterpret_cast<const char*>(digest), length));
}

bool validKey(std::string_view key) {
    return key.size() == 24 && key.substr(22) == "==" && std::all_of(key.begin(), key.begin() + 22, base64Char);
}

std::string frame(int opcode, std::string_view payload) {
    std::string out(1, static_cast<char>(0x80 | opcode));
    const auto size = payload.size();
    if (size < 126) {
        out += static_cast<char>(size);
    } else if (size <= 0xFFFF) {
        out += static_cast<char>(126);
        out += static_cast<char>(size >> 8);
        out += static_cast<char>(size & 0xFF);
    } else {
        out += static_cast<char>(127);
        for (int shift = 56; shift >= 0; shift -= 8) out += static_cast<char>((static_cast<std::uint64_t>(size) >> shift) & 0xFF);
    }
    return out + std::string(payload);
}

std::string close(std::optional<int> code) {
    if (!code) return frame(kClose, {});
    const char bytes[2] = {static_cast<char>((*code >> 8) & 0xFF), static_cast<char>(*code & 0xFF)};
    return frame(kClose, std::string_view(bytes, 2));
}

ClientFrame clientFrame(std::string_view data) {
    ClientFrame out;
    if (data.size() < 2) return out;
    const auto first = static_cast<unsigned char>(data[0]), second = static_cast<unsigned char>(data[1]);
    const bool fin = first & 0x80, masked = second & 0x80;
    const int rsv = first & 0x70, opcode = first & 0x0F;
    const auto fail = [&](int code) {
        out.action = ClientFrame::Action::Close;
        out.code = code;
        return out;
    };
    const bool known = opcode == 0x0 || opcode == kText || opcode == 0x2 || opcode == kClose || opcode == kPing || opcode == kPong;
    if (rsv || !known || !masked) return fail(1002);
    std::uint64_t size = second & 0x7F;
    std::size_t pos = 2;
    if (size == 126 || size == 127) {
        const std::size_t extra = size == 126 ? 2 : 8;
        if (data.size() < 2 + extra) return out;
        size = bigEndian(data.substr(2, extra));
        pos += extra;
    }
    if (opcode >= 0x8 && (!fin || size > 125)) return fail(1002);
    if (opcode < 0x8 && size > kClientLimit) return fail(1009);
    if (data.size() < pos + 4 + size) return out;
    const auto mask = data.substr(pos, 4);
    std::string payload(data.substr(pos + 4, static_cast<std::size_t>(size)));
    for (std::size_t i = 0; i < payload.size(); ++i) payload[i] = static_cast<char>(payload[i] ^ mask[i % 4]);
    out.consumed = pos + 4 + static_cast<std::size_t>(size);
    if (opcode == kPing) {
        out.action = ClientFrame::Action::Pong;
        out.payload = std::move(payload);
    } else if (opcode == kClose) {
        if (payload.size() == 1) return fail(1002);
        out.action = ClientFrame::Action::EchoClose;
        if (payload.size() >= 2) out.code = static_cast<int>(bigEndian(std::string_view(payload).substr(0, 2)));
    } else {
        out.action = ClientFrame::Action::Ignore;
    }
    return out;
}

}  // namespace agentspeed::lan::ws
